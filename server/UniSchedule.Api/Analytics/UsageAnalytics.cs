using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Analytics;

// Aggregate rows only: no account IDs, IPs, headers, query strings, or request/response bodies.
public class UsageDay
{
    public long Day { get; set; }
    public int ActiveUsers { get; set; }
    public int PeakUsers { get; set; }
    public long PeakAt { get; set; }
    public long FirstSample { get; set; }
    public long LastSample { get; set; }
}

public class UsageHour
{
    public long Hour { get; set; }
    public string Route { get; set; } = "";
    public string Method { get; set; } = "";
    public string Audience { get; set; } = "";
    public int Status { get; set; }
    public long Requests { get; set; }
    public double TotalMs { get; set; }
    public double MaxMs { get; set; }
}

public static class UsageAnalytics
{
    public static long OmanDay(DateTimeOffset now) => new DateTimeOffset(now.ToOffset(TimeSpan.FromHours(4)).Date, TimeSpan.FromHours(4)).ToUnixTimeSeconds();

    public static async Task Prune(AppDb db, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = OmanDay(now) - 89 * 86400;
        await db.UsageDays.Where(x => x.Day < cutoff).ExecuteDeleteAsync(ct);
        await db.UsageHours.Where(x => x.Hour < cutoff).ExecuteDeleteAsync(ct);
    }

    public static async Task Record(HttpContext http, long started, bool failed)
    {
        if (!http.RequestServices.GetRequiredService<IConfiguration>().GetValue("Analytics:Enabled", true)) return;
        var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText?.TrimEnd('/');
        if (route is null || !route.StartsWith("/api/") || route.Contains("**") || route.StartsWith("/api/me/analytics") || route is "/api/health" or "/api/admin/analytics") return;
        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var day = OmanDay(now);
        var hour = now.ToUnixTimeSeconds() / 3600 * 3600;
        var at = now.ToUnixTimeSeconds();
        var status = failed ? 500 : http.Response.StatusCode;
        var audience = http.User.Identity?.IsAuthenticated == true ? (http.User.IsStudent() ? "student" : "staff") : "anonymous";
        var method = http.Request.Method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH" ? http.Request.Method : "OTHER";
        // A separate context keeps failed application transactions and tracked entities out of analytics.
        await using var scope = http.RequestServices.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO UsageHours (Hour, Route, Method, Audience, Status, Requests, TotalMs, MaxMs)
            VALUES ({hour}, {route}, {method}, {audience}, {status}, 1, {duration}, {duration})
            ON CONFLICT (Hour, Route, Method, Audience, Status) DO UPDATE SET
            Requests = Requests + 1, TotalMs = TotalMs + excluded.TotalMs, MaxMs = MAX(MaxMs, excluded.MaxMs)
            """);
        if (audience != "student" || status >= 400) return;
        var midnight = DateTimeOffset.FromUnixTimeSeconds(day);
        var activeSince = now.AddMinutes(-5);
        var active = await db.Students.CountAsync(s => s.LastSeenAt >= activeSince);
        var today = await db.Students.CountAsync(s => s.LastSeenAt >= midnight);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO UsageDays (Day, ActiveUsers, PeakUsers, PeakAt, FirstSample, LastSample)
            VALUES ({day}, {today}, {active}, {at}, {at}, {at})
            ON CONFLICT (Day) DO UPDATE SET
            ActiveUsers = MAX(ActiveUsers, excluded.ActiveUsers),
            PeakAt = CASE WHEN excluded.PeakUsers > PeakUsers THEN excluded.PeakAt ELSE PeakAt END,
            PeakUsers = MAX(PeakUsers, excluded.PeakUsers), LastSample = MAX(LastSample, excluded.LastSample)
            """);
    }

    public static void MapUsageAnalytics(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/analytics", async (int? days, AppDb db, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            if (days is < 1 or > 90) return Errors.BadRequest("Choose between 1 and 90 days.");
            var now = clock.GetUtcNow();
            var since = OmanDay(now) - ((days ?? 30) - 1) * 86400;
            var daily = await db.UsageDays.AsNoTracking().Where(x => x.Day >= since).OrderBy(x => x.Day).ToListAsync(ct);
            var hourly = await db.UsageHours.AsNoTracking().Where(x => x.Hour >= since).OrderBy(x => x.Hour).ToListAsync(ct);
            var requests = hourly.Sum(x => x.Requests);
            var students = await db.Students.CountAsync(ct);
            return Results.Ok(new
            {
                enabled = config.GetValue("Analytics:Enabled", true), retentionDays = 90, timezone = "Asia/Muscat",
                since = DateTimeOffset.FromUnixTimeSeconds(since),
                firstSample = daily.Count == 0 ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(daily.Min(x => x.FirstSample)),
                busiestDay = daily.OrderByDescending(x => x.ActiveUsers).ThenBy(x => x.Day).FirstOrDefault(),
                peak = daily.OrderByDescending(x => x.PeakUsers).ThenBy(x => x.PeakAt).FirstOrDefault(),
                requests, serverErrors = hourly.Where(x => x.Status >= 500).Sum(x => x.Requests),
                clientErrors = hourly.Where(x => x.Status is >= 400 and < 500).Sum(x => x.Requests),
                averageResponseMs = requests == 0 ? 0 : hourly.Sum(x => x.TotalMs) / requests,
                maxResponseMs = hourly.Count == 0 ? 0 : hourly.Max(x => x.MaxMs),
                registrations = await db.Students.CountAsync(x => x.CreatedAt >= DateTimeOffset.FromUnixTimeSeconds(since), ct),
                studentsRegistered = students,
                setup = await db.Students.GroupBy(x => x.SetupStep ?? "not-started").Select(g => new { step = g.Key, students = g.Count() }).ToListAsync(ct),
                majors = await db.Students.GroupBy(x => x.MajorId).Select(g => new { majorId = g.Key, students = g.Count() }).ToListAsync(ct),
                years = await db.Students.GroupBy(x => x.Year).Select(g => new { year = g.Key, students = g.Count() }).ToListAsync(ct),
                daily, hourly,
            });
        }).RequireAuthorization(Session.OwnerPolicy);
    }
}

public sealed class UsageRetention(IServiceScopeFactory scopes, TimeProvider clock, ILogger<UsageRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                await UsageAnalytics.Prune(db, clock.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Usage analytics retention failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
