using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace UniSchedule.Api.Auth;

public sealed class EmailOptions
{
    /// <summary>"log" (codes go to the server log) or "resend".</summary>
    public string Provider { get; set; } = "log";
    public string? ResendApiKey { get; set; }
    /// <summary>Sender, e.g. "Classy &lt;schedule@example.com&gt;". Its domain must be verified in Resend.</summary>
    public string? From { get; set; }
}

public interface IEmailSender
{
    Task SendSignInCodeAsync(string email, string code, CancellationToken ct);
}

/// <summary>No email provider configured: codes go to the server log (docker compose logs api).</summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> log) : IEmailSender
{
    public Task SendSignInCodeAsync(string email, string code, CancellationToken ct)
    {
        log.LogWarning("Sign-in code for {Email}: {Code}", email, code);
        return Task.CompletedTask;
    }
}

/// <summary>Sends through Resend's HTTP API.</summary>
public sealed class ResendEmailSender(HttpClient http, IOptions<EmailOptions> options, ILogger<ResendEmailSender> log) : IEmailSender
{
    public async Task SendSignInCodeAsync(string email, string code, CancellationToken ct)
    {
        var text = $"Your Classy code is {code}\n\nIt expires in 10 minutes. If you didn't ask for it, ignore this email.";
        var html = $"""
            <div style="font-family:system-ui,sans-serif;max-width:420px;margin:auto;padding:24px">
              <p style="font-size:15px;color:#333">Your Classy sign-in code:</p>
              <p style="font-size:34px;font-weight:700;letter-spacing:8px;margin:12px 0">{code}</p>
              <p style="font-size:13px;color:#777">It expires in 10 minutes. If you didn't ask for it, ignore this email.</p>
            </div>
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails")
        {
            Content = JsonContent.Create(new { from = options.Value.From, to = new[] { email }, subject = $"{code} is your Classy code", html, text }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ResendApiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            log.LogError("Resend refused the email to {Email}: {Status} {Body}", email, (int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
            throw new EmailNotSentException();
        }
    }
}

public sealed class EmailNotSentException() : Exception("The email provider refused the message.");
