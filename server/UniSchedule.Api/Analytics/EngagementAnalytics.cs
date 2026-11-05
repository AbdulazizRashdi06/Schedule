using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Analytics;

public class EngagementConsent
{
    public int StudentId { get; set; }
    public bool Enabled { get; set; }
    public long ChangedAt { get; set; }
    public int Version { get; set; }
}

public class EngagementSession
{
    public string Id { get; set; } = "";
    public int StudentId { get; set; }
    public long StartedAt { get; set; }
    public long LastAt { get; set; }
    public long Sequence { get; set; }
    public long ActiveMs { get; set; }
    public List<EngagementScreen> Screens { get; set; } = [];
}

public class EngagementScreen
{
    public string SessionId { get; set; } = "";
    public string Screen { get; set; } = "";
    public long ReportedMs { get; set; }
    public long ActiveMs { get; set; }
    public int Views { get; set; }
}

public static class EngagementAnalytics
{
    public const int ConsentVersion = 1;
    public static readonly string[] Screens = ["home", "schedule", "course", "setup", "edit", "colours", "groups", "group", "join"];
    public record ConsentBody(bool Enabled, int Version);
    public record ScreenBody(string Screen, long ActiveMs, int Views);
    public record PulseBody(string SessionId, long Sequence, List<ScreenBody>? Screens);

    public static void MapEngagementAnalytics(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me/analytics").RequireAuthorization(Session.StudentPolicy);
        me.MapGet("/consent", async (HttpContext http, AppDb db, IConfiguration config, CancellationToken ct) =>
        {
            var consent = await db.EngagementConsents.FindAsync([http.User.Id()], ct);
            return Results.Ok(new { enabled = consent?.Enabled == true && consent.Version == ConsentVersion,
                available = config.GetValue("Analytics:Enabled", true), version = ConsentVersion });
        });
        me.MapPut("/consent", async (ConsentBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (body.Enabled && body.Version != ConsentVersion) return Errors.BadRequest("Refresh the usage analytics notice before opting in.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var id = http.User.Id();
            var consent = await db.EngagementConsents.FindAsync([id], ct);
            if (consent is null) { consent = new EngagementConsent { StudentId = id }; db.EngagementConsents.Add(consent); }
            consent.Enabled = body.Enabled;
            consent.Version = ConsentVersion;
            consent.ChangedAt = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (!body.Enabled) await db.EngagementSessions.Where(x => x.StudentId == id).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { enabled = body.Enabled, version = ConsentVersion });
        });
        me.MapPost("/pulse", async (PulseBody body, HttpContext http, AppDb db, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            if (!config.GetValue("Analytics:Enabled", true)) return Results.NoContent();
            if (!Guid.TryParseExact(body.SessionId, "N", out _) || body.Sequence < 1 || body.Screens is null || body.Screens.Count is < 1 or > 9 ||
                body.Screens.Any(x => x is null || !Screens.Contains(x.Screen) || x.ActiveMs is < 0 or > 604800000 || x.Views is < 0 or > 10000) ||
                body.Screens.Select(x => x.Screen).Distinct().Count() != body.Screens.Count)
                return Errors.BadRequest("Invalid usage sample.");
            // Serializes consent/revocation and competing tabs before reading counters.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var id = http.User.Id();
            var consent = await db.EngagementConsents.FindAsync([id], ct);
            if (consent?.Enabled != true || consent.Version != ConsentVersion) return Errors.Forbidden("Optional usage analytics is off.");
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var session = await db.EngagementSessions.Include(x => x.Screens).FirstOrDefaultAsync(x => x.Id == body.SessionId, ct);
            if (session is not null && session.StudentId != id) return Errors.Forbidden("That usage session belongs to another account.");
            if (session is not null && body.Sequence <= session.Sequence) return Results.NoContent();
            if (session is not null && now - session.LastAt > 30 * 60 * 1000) return Errors.Conflict("Start a new usage session.");
            // Across all tabs, accepted time cannot exceed elapsed wall time. No offline catch-up.
            var last = await db.EngagementSessions.Where(x => x.StudentId == id).Select(x => (long?)x.LastAt).MaxAsync(ct);
            long budget = session is null ? 0 : Math.Clamp(now - (last ?? now), 0, 30000);
            if (session is null)
            {
                session = new EngagementSession { Id = body.SessionId, StudentId = id, StartedAt = now };
                db.EngagementSessions.Add(session);
            }
            foreach (var sample in body.Screens)
            {
                var screen = session.Screens.FirstOrDefault(x => x.Screen == sample.Screen);
                if (screen is null) { screen = new EngagementScreen { SessionId = session.Id, Screen = sample.Screen }; session.Screens.Add(screen); }
                if (sample.ActiveMs < screen.ReportedMs || sample.Views < screen.Views) return Errors.BadRequest("Usage counters must increase.");
                var accepted = Math.Min(budget, sample.ActiveMs - screen.ReportedMs);
                budget -= accepted;
                screen.ActiveMs += accepted;
                session.ActiveMs += accepted;
                screen.ReportedMs = sample.ActiveMs;
                screen.Views = Math.Min(sample.Views, screen.Views + 50);
            }
            session.Sequence = body.Sequence;
            session.LastAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireRateLimiting("engagement");
    }

    public static async Task<object> Report(AppDb db, long since, CancellationToken ct)
    {
        var sessions = await db.EngagementSessions.AsNoTracking().Include(x => x.Screens).Where(x => x.StartedAt >= since * 1000).ToListAsync(ct);
        var users = sessions.GroupBy(x => x.StudentId).ToList();
        var totalMs = sessions.Sum(x => x.ActiveMs);
        var views = sessions.Sum(x => x.Screens.Sum(s => s.Views));
        var engaged = sessions.Count(x => x.ActiveMs >= 10000 || x.Screens.Sum(s => s.Views) >= 2);
        return new
        {
            consentedUsers = await db.EngagementConsents.CountAsync(x => x.Enabled && x.Version == ConsentVersion, ct),
            participatingUsers = users.Count, sessions = sessions.Count, screenViews = views, totalActiveSeconds = totalMs / 1000d,
            averageActiveSecondsPerSession = sessions.Count == 0 ? (double?)null : totalMs / 1000d / sessions.Count,
            averageActiveSecondsPerUser = users.Count == 0 ? (double?)null : totalMs / 1000d / users.Count,
            averageSessionsPerUser = users.Count == 0 ? (double?)null : (double)sessions.Count / users.Count,
            averageScreensPerSession = sessions.Count == 0 ? (double?)null : (double)views / sessions.Count,
            repeatUsers = users.Count(g => g.Count() > 1), engagedSessions = engaged,
            engagementRate = sessions.Count == 0 ? (double?)null : (double)engaged / sessions.Count,
            shortSingleScreenRate = sessions.Count == 0 ? (double?)null : (double)(sessions.Count - engaged) / sessions.Count,
            screens = sessions.SelectMany(x => x.Screens).GroupBy(x => x.Screen).Select(g => new
            {
                screen = g.Key, views = g.Sum(x => x.Views), activeSeconds = g.Sum(x => x.ActiveMs) / 1000d,
                averageActiveSecondsPerView = g.Sum(x => x.Views) == 0 ? (double?)null : g.Sum(x => x.ActiveMs) / 1000d / g.Sum(x => x.Views),
            }).OrderByDescending(x => x.views),
            daily = sessions.GroupBy(x => UsageAnalytics.OmanDay(DateTimeOffset.FromUnixTimeMilliseconds(x.StartedAt))).OrderBy(g => g.Key)
                .Select(g => new { day = g.Key, sessions = g.Count(), users = g.Select(x => x.StudentId).Distinct().Count(), activeSeconds = g.Sum(x => x.ActiveMs) / 1000d,
                    averageActiveSecondsPerSession = g.Average(x => x.ActiveMs) / 1000d }),
        };
    }
}
