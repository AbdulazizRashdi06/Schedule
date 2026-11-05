using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Analytics;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

public class EngagementAnalyticsTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static async Task<HttpClient> Student(WebApplicationFactory<Program> app, string id)
    {
        var client = app.CreateClient();
        var sent = await client.PostAsJsonAsync("/api/auth/request-code", new { universityId = id });
        var code = (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("testCode").GetString();
        (await client.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).EnsureSuccessStatusCode();
        return client;
    }

    static object Pulse(string id, long sequence, long ms, string screen = "home", int views = 1) =>
        new { sessionId = id, sequence, screens = new[] { new { screen, activeMs = ms, views } } };

    [Fact]
    public async Task Consent_is_required_and_withdrawal_deletes_all_sessions()
    {
        using var factory = new ApiFactory();
        var client = await Student(factory, "25-9801");
        var id = Guid.NewGuid().ToString("N");
        var preference = await client.GetFromJsonAsync<JsonElement>("/api/me/analytics/consent");
        Assert.False(preference.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 1, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/me/analytics/consent", new { enabled = true, version = 0 })).StatusCode);
        (await client.PutAsJsonAsync("/api/me/analytics/consent", new { enabled = true, version = 1 })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 1, 0))).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.Single(await db.EngagementSessions.AsNoTracking().ToListAsync());
        (await client.PutAsJsonAsync("/api/me/analytics/consent", new { enabled = false })).EnsureSuccessStatusCode();
        Assert.Empty(await db.EngagementSessions.AsNoTracking().ToListAsync());
        Assert.Empty(await db.EngagementScreens.AsNoTracking().ToListAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 2, 1000))).StatusCode);
        Assert.Empty(await db.EngagementSessions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Timing_is_idempotent_bounded_and_deduplicated_across_tabs()
    {
        using var factory = new ApiFactory();
        var clock = new Clock();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock)));
        var client = await Student(app, "25-9802");
        (await client.PutAsJsonAsync("/api/me/analytics/consent", new { enabled = true, version = 1 })).EnsureSuccessStatusCode();
        var id = Guid.NewGuid().ToString("N");
        (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 1, 0))).EnsureSuccessStatusCode();
        clock.Now = clock.Now.AddSeconds(15);
        (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 2, 15000))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 2, 15000))).EnsureSuccessStatusCode();
        clock.Now = clock.Now.AddSeconds(15);
        (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 3, 60000))).EnsureSuccessStatusCode();
        var other = Guid.NewGuid().ToString("N");
        (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(other, 1, 0))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 4, 75000))).EnsureSuccessStatusCode();
        clock.Now = clock.Now.AddSeconds(15);
        (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(other, 2, 15000))).EnsureSuccessStatusCode();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var report = JsonSerializer.SerializeToElement(await EngagementAnalytics.Report(db, UsageAnalytics.OmanDay(clock.Now), default));
        Assert.Equal(45, report.GetProperty("totalActiveSeconds").GetDouble());
        Assert.Equal(22.5, report.GetProperty("averageActiveSecondsPerSession").GetDouble());
        Assert.Equal(45, report.GetProperty("averageActiveSecondsPerUser").GetDouble());
        Assert.Equal(1, report.GetProperty("repeatUsers").GetInt32());
        Assert.Equal(2, report.GetProperty("screenViews").GetInt32());
        Assert.DoesNotContain("25-9802", report.ToString());
        Assert.DoesNotContain(id, report.ToString());
        clock.Now = clock.Now.AddMinutes(31);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 5, 80000))).StatusCode);
    }

    [Fact]
    public async Task Samples_cannot_include_private_routes_or_change_another_users_session()
    {
        using var factory = new ApiFactory();
        var first = await Student(factory, "25-9803");
        var second = await Student(factory, "25-9804");
        foreach (var client in new[] { first, second })
            (await client.PutAsJsonAsync("/api/me/analytics/consent", new { enabled = true, version = 1 })).EnsureSuccessStatusCode();
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.BadRequest, (await first.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 1, 0, "/join/private-code"))).StatusCode);
        (await first.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 1, 0))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await second.PostAsJsonAsync("/api/me/analytics/pulse", Pulse(id, 2, 1000))).StatusCode);
    }
}
