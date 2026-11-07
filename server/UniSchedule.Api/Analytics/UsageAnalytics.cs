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

/// <summary>
/// Request counts gathered in memory between writes, so a request never waits on an analytics write and SQLite's one
/// writer isn't shared with every page load. A background job (and the analytics page, before it reads) writes them out.
/// </summary>
public sealed class UsageBuffer
{
    public readonly record struct Key(long Hour, string Route, string Method, string Audience, int Status);
    public sealed class Totals { public long Requests; public double TotalMs; public double MaxMs; }

    readonly Lock gate = new();
    Dictionary<Key, Totals> hours = [];
    bool studentSeen;

    public void Add(Key key, double ms, bool student)
    {
        lock (gate)
        {
            if (!hours.TryGetValue(key, out var t)) hours[key] = t = new Totals();
            t.Requests++;
            t.TotalMs += ms;
            t.MaxMs = Math.Max(t.MaxMs, ms);
            studentSeen |= student;
        }
    }

    /// <summary>Everything gathered so far, and whether a student was active; the buffer starts again empty.</summary>
    public (Dictionary<Key, Totals> Hours, bool StudentSeen) Take()
    {
        lock (gate)
        {
            var taken = (hours, studentSeen);
            hours = [];
            studentSeen = false;
            return taken;
        }
    }
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

    /// <summary>Counts one finished API request in memory. Never touches the database and never throws.</summary>
    public static void Record(HttpContext http, long started, bool failed)
    {
        try
        {
            if (!http.RequestServices.GetRequiredService<IConfiguration>().GetValue("Analytics:Enabled", true)) return;
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText?.TrimEnd('/');
            if (route is null || !route.StartsWith("/api/") || route.Contains("**") || route.StartsWith("/api/me/analytics") || route is "/api/health" or "/api/admin/analytics") return;
            var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            var status = failed ? 500 : http.Response.StatusCode;
            var audience = http.User.Identity?.IsAuthenticated == true ? (http.User.IsStudent() ? "student" : "staff") : "anonymous";
            var method = http.Request.Method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH" ? http.Request.Method : "OTHER";
            http.RequestServices.GetRequiredService<UsageBuffer>()
                .Add(new(now.ToUnixTimeSeconds() / 3600 * 3600, route, method, audience, status), duration, audience == "student" && status < 400);
        }
        catch (Exception ex)
        {
            http.RequestServices.GetService<ILogger<UsageBuffer>>()?.LogWarning(ex, "Could not count a request for usage analytics");
        }
    }

    /// <summary>
    /// Writes the gathered request counts, and, if students were active since the last write, samples how many are
    /// active now (5 minutes) and today. One short transaction a minute instead of writes on every request.
    /// </summary>
    public static async Task Flush(AppDb db, UsageBuffer buffer, DateTimeOffset now, CancellationToken ct)
    {
        var (hours, studentSeen) = buffer.Take();
        if (hours.Count == 0 && !studentSeen) return;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        foreach (var (k, t) in hours)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO UsageHours (Hour, Route, Method, Audience, Status, Requests, TotalMs, MaxMs)
                VALUES ({k.Hour}, {k.Route}, {k.Method}, {k.Audience}, {k.Status}, {t.Requests}, {t.TotalMs}, {t.MaxMs})
                ON CONFLICT (Hour, Route, Method, Audience, Status) DO UPDATE SET
                Requests = Requests + excluded.Requests, TotalMs = TotalMs + excluded.TotalMs, MaxMs = MAX(MaxMs, excluded.MaxMs)
                """, ct);
        if (studentSeen)
        {
            var day = OmanDay(now);
            var at = now.ToUnixTimeSeconds();
            var midnight = DateTimeOffset.FromUnixTimeSeconds(day);
            var activeSince = now.AddMinutes(-5);
            var active = await db.Students.CountAsync(s => s.LastSeenAt >= activeSince, ct);
            var today = await db.Students.CountAsync(s => s.LastSeenAt >= midnight, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO UsageDays (Day, ActiveUsers, PeakUsers, PeakAt, FirstSample, LastSample)
                VALUES ({day}, {today}, {active}, {at}, {at}, {at})
                ON CONFLICT (Day) DO UPDATE SET
                ActiveUsers = MAX(ActiveUsers, excluded.ActiveUsers),
                PeakAt = CASE WHEN excluded.PeakUsers > PeakUsers THEN excluded.PeakAt ELSE PeakAt END,
                PeakUsers = MAX(PeakUsers, excluded.PeakUsers), LastSample = MAX(LastSample, excluded.LastSample)
                """, ct);
        }
        await tx.CommitAsync(ct);
    }

    public static void MapUsageAnalytics(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/analytics", async (int? days, AppDb db, UsageBuffer buffer, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            if (days is < 1 or > 90) return Errors.BadRequest("Choose between 1 and 90 days.");
            var now = clock.GetUtcNow();
            // Include requests from the last minute that haven't been written yet.
            await Flush(db, buffer, now, ct);
            var since = OmanDay(now) - ((days ?? 30) - 1) * 86400;
            var daily = await db.UsageDays.AsNoTracking().Where(x => x.Day >= since).OrderBy(x => x.Day).ToListAsync(ct);
            var hourly = db.UsageHours.AsNoTracking().Where(x => x.Hour >= since);
            // Totals are added up in the database; the page only gets what it draws: 24 hours of the day and one row per route.
            var totals = await hourly.GroupBy(_ => 1).Select(g => new
            {
                Requests = g.Sum(x => x.Requests),
                TotalMs = g.Sum(x => x.TotalMs),
                MaxMs = g.Max(x => x.MaxMs),
                ServerErrors = g.Where(x => x.Status >= 500).Sum(x => x.Requests),
                ClientErrors = g.Where(x => x.Status >= 400 && x.Status < 500).Sum(x => x.Requests),
            }).FirstOrDefaultAsync(ct);
            var byHour = await hourly.GroupBy(x => x.Hour).Select(g => new { g.Key, Requests = g.Sum(x => x.Requests) }).ToListAsync(ct);
            var hours = Enumerable.Range(0, 24).Select(h => new
            {
                hour = h,
                requests = byHour.Where(x => (x.Key / 3600 + 4) % 24 == h).Sum(x => x.Requests),
            });
            var routes = await hourly.GroupBy(x => x.Route).Select(g => new { route = g.Key, requests = g.Sum(x => x.Requests) })
                .OrderByDescending(x => x.requests).ToListAsync(ct);
            var requests = totals?.Requests ?? 0;
            var students = await db.Students.CountAsync(ct);
            return Results.Ok(new
            {
                enabled = config.GetValue("Analytics:Enabled", true), retentionDays = 90, timezone = "Asia/Muscat",
                since = DateTimeOffset.FromUnixTimeSeconds(since),
                firstSample = daily.Count == 0 ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(daily.Min(x => x.FirstSample)),
                busiestDay = daily.OrderByDescending(x => x.ActiveUsers).ThenBy(x => x.Day).FirstOrDefault(),
                peak = daily.OrderByDescending(x => x.PeakUsers).ThenBy(x => x.PeakAt).FirstOrDefault(),
                requests, serverErrors = totals?.ServerErrors ?? 0, clientErrors = totals?.ClientErrors ?? 0,
                averageResponseMs = requests == 0 ? 0 : totals!.TotalMs / requests,
                maxResponseMs = totals?.MaxMs ?? 0,
                registrations = await db.Students.CountAsync(x => x.CreatedAt >= DateTimeOffset.FromUnixTimeSeconds(since), ct),
                studentsRegistered = students,
                setup = await db.Students.GroupBy(x => x.SetupStep ?? "not-started").Select(g => new { step = g.Key, students = g.Count() }).ToListAsync(ct),
                majors = await db.Students.GroupBy(x => x.MajorId).Select(g => new { majorId = g.Key, students = g.Count() }).ToListAsync(ct),
                years = await db.Students.GroupBy(x => x.Year).Select(g => new { year = g.Key, students = g.Count() }).ToListAsync(ct),
                daily, hours, routes,
            });
        }).RequireAuthorization(Session.OwnerPolicy);
    }
}

/// <summary>Writes the gathered usage counts every minute (and once more on shutdown), and drops history older than 90 days hourly.</summary>
public sealed class UsageRetention(IServiceScopeFactory scopes, UsageBuffer buffer, TimeProvider clock, ILogger<UsageRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        var lastPrune = DateTimeOffset.MinValue;
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var now = clock.GetUtcNow();
                await UsageAnalytics.Flush(db, buffer, now, stoppingToken);
                if (now - lastPrune >= TimeSpan.FromHours(1))
                {
                    await UsageAnalytics.Prune(db, now, stoppingToken);
                    lastPrune = now;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Usage analytics write or retention failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Don't lose the last minute's counts when the server restarts (each deploy).
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await UsageAnalytics.Flush(scope.ServiceProvider.GetRequiredService<AppDb>(), buffer, clock.GetUtcNow(), cancellationToken);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not write the last usage counts on shutdown"); }
    }
}
