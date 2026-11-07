using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;
using WebPush;
using Sub = UniSchedule.Api.Data.PushSubscription;

namespace UniSchedule.Api.Students;

/// <summary>One notification for a student's devices.</summary>
public record Note(string Title, string Body, string Url, string Tag, int TtlSeconds);

/// <summary>
/// Web Push: reminders before each class and an alert when one of your classes changes. Works in the browser and in the
/// app installed from it (on iPhone, once it's on the Home Screen). The server's signing keys are made on first use and
/// kept in the database, so nothing has to be set up by hand.
/// </summary>
public static class Notifications
{
    const string PublicKey = "push.vapid.public";
    const string PrivateKey = "push.vapid.private";
    /// <summary>Changes up to this time have been announced.</summary>
    public const string ChangesSince = "push.changes.since";
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);
    static readonly int[] RemindChoices = [5, 10, 15, 30, 60];

    public record SubscribeBody(string? Endpoint, Keys? Keys);
    public record Keys(string? P256dh, string? Auth);
    public record EndpointBody(string? Endpoint);
    public record SettingsBody(int? RemindMinutes, bool Changes);

    /// <summary>The signing keys, made and saved the first time they're needed.</summary>
    public static async Task<VapidDetails> KeysAsync(AppDb db, IConfiguration config, CancellationToken ct)
    {
        var saved = await db.AppSettings.Where(x => x.Key == PublicKey || x.Key == PrivateKey).ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        var subject = config["Push:Subject"] ?? "mailto:schedule@tryzarqa.com";
        if (saved.TryGetValue(PublicKey, out var pub) && saved.TryGetValue(PrivateKey, out var priv)) return new VapidDetails(subject, pub, priv);
        var keys = VapidHelper.GenerateVapidKeys();
        var now = DateTimeOffset.UtcNow;
        db.AppSettings.Add(new AppSetting { Key = PublicKey, Value = keys.PublicKey, UpdatedBy = "system", UpdatedAt = now });
        db.AppSettings.Add(new AppSetting { Key = PrivateKey, Value = keys.PrivateKey, UpdatedBy = "system", UpdatedAt = now });
        try
        {
            await db.SaveChangesAsync(ct);
            return new VapidDetails(subject, keys.PublicKey, keys.PrivateKey);
        }
        catch (DbUpdateException)
        {
            // Another request made them at the same moment: use those.
            db.ChangeTracker.Clear();
            return await KeysAsync(db, config, ct);
        }
    }

    public static void MapNotifications(this RouteGroupBuilder api)
    {
        // The public half of the signing key, which the browser needs to subscribe.
        api.MapGet("/push/key", async (AppDb db, IConfiguration config, CancellationToken ct) =>
            Results.Ok(new { publicKey = (await KeysAsync(db, config, ct)).PublicKey }));

        var me = api.MapGroup("/me").RequireAuthorization(Session.StudentPolicy);

        me.MapGet("/notifications", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var s = await db.Students.Where(x => x.Id == id).Select(x => new { x.RemindMinutes, x.MuteChanges }).FirstAsync(ct);
            return Results.Ok(new { remindMinutes = s.RemindMinutes, changes = !s.MuteChanges, devices = await db.PushSubscriptions.CountAsync(p => p.StudentId == id, ct) });
        });

        me.MapPut("/notifications", async (SettingsBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            if (body.RemindMinutes is int m && !RemindChoices.Contains(m)) return Errors.BadRequest("Pick 5, 10, 15, 30 or 60 minutes before.");
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            s.RemindMinutes = body.RemindMinutes;
            s.MuteChanges = !body.Changes;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // This browser or installed app accepts notifications. An endpoint is one device: it moves to whoever signs in there.
        me.MapPost("/push", async (SubscribeBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (body.Endpoint is not { Length: > 0 and < 1000 } endpoint || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || body.Keys?.P256dh is not { Length: > 0 and < 200 } p256dh || body.Keys.Auth is not { Length: > 0 and < 100 } auth)
                return Errors.BadRequest("This browser sent an incomplete subscription. Try again.");
            var id = http.User.Id();
            var sub = await db.PushSubscriptions.FirstOrDefaultAsync(x => x.Endpoint == endpoint, ct);
            if (sub is null) db.PushSubscriptions.Add(sub = new Sub { Endpoint = endpoint, CreatedAt = clock.GetUtcNow() });
            sub.StudentId = id;
            sub.P256dh = p256dh;
            sub.Auth = auth;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        me.MapPost("/push/remove", async (EndpointBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            await db.PushSubscriptions.Where(x => x.StudentId == id && x.Endpoint == body.Endpoint).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // A test notification to every device the student turned on.
        me.MapPost("/push/test", async (HttpContext http, AppDb db, PushSender push, CancellationToken ct) =>
        {
            var sent = await push.SendAsync(db, [http.User.Id()], new Note("Notifications are on", "Classy will remind you before class and tell you when a class changes.", "/", "test", 600), ct);
            return sent == 0 ? Errors.Conflict("No device is set up for notifications yet.") : Results.Ok(new { sent });
        });
    }

    /// <summary>
    /// The reminders due in a window of time: every class (and own slot) starting <c>RemindMinutes</c> after a moment in
    /// (from, to], for students who turned reminders on and have a device. Days off and the semester's weeks apply.
    /// </summary>
    public static async Task<List<(int StudentId, Note Note)>> DueReminders(AppDb db, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        if (semester is null || to <= from) return [];
        var students = await db.Students.Where(s => s.RemindMinutes != null && db.PushSubscriptions.Any(p => p.StudentId == s.Id))
            .Select(s => new { s.Id, Minutes = s.RemindMinutes!.Value }).ToListAsync(ct);
        if (students.Count == 0) return [];
        var ids = students.Select(s => s.Id).ToList();
        var picks = await db.Picks.Where(p => ids.Contains(p.StudentId) && p.Course.SemesterId == semester.Id && p.SectionId != null)
            .Include(p => p.Course).Include(p => p.Section).ThenInclude(x => x!.Meetings).AsSplitQuery().ToListAsync(ct);
        var slots = await db.CustomSlots.Where(x => ids.Contains(x.StudentId) && x.SemesterId == semester.Id).ToListAsync(ct);
        var daysOff = await DaysOff.ForAsync(db, semester.Id, ct);
        var first = SemesterWeeks.Sunday(semester.Week1Start ?? DateOnly.FromDateTime((semester.PublishedAt ?? from).ToOffset(Oman).DateTime));
        var last = first.AddDays((semester.Week1Start is not null && semester.LastWeek is int weeks ? weeks : 16) * 7 - 1);

        var due = new List<(int, Note)>();
        foreach (var s in students)
        {
            // Classes starting in this window, shifted by how early the student wants to know.
            var lo = from.AddMinutes(s.Minutes).ToOffset(Oman);
            var hi = to.AddMinutes(s.Minutes).ToOffset(Oman);
            bool Starts(DateOnly? date, int day, TimeOnly start, bool isClass, out DateOnly on)
            {
                for (on = DateOnly.FromDateTime(lo.DateTime); on <= DateOnly.FromDateTime(hi.DateTime); on = on.AddDays(1))
                {
                    var happens = date == on || (date is null && day == (int)on.DayOfWeek && on >= first && on <= last && !(isClass && daysOff.ContainsKey(on)));
                    var at = new DateTimeOffset(on.ToDateTime(start), Oman);
                    if (happens && at > lo && at <= hi) return true;
                }
                return false;
            }
            var ttl = s.Minutes * 60;
            foreach (var p in picks.Where(p => p.StudentId == s.Id))
            foreach (var m in p.Section!.Meetings)
                if (Starts(m.Date, m.Day, m.Start, true, out var on))
                {
                    var where = string.IsNullOrWhiteSpace(m.Room) ? "room to be announced" : m.Room;
                    due.Add((s.Id, new Note($"{p.Course.Code} in {s.Minutes} min", $"{p.Course.Name} · {Timeslots.Format(m.Start)} · {where}",
                        $"/?session={p.CourseId}.{on:yyyy-MM-dd}.{Timeslots.Format(m.Start)}", $"class-{p.CourseId}-{on:yyyy-MM-dd}-{m.Start:HHmm}", ttl)));
                }
            foreach (var slot in slots.Where(x => x.StudentId == s.Id))
                if (Starts(slot.Date, slot.Day, slot.Start, false, out var on))
                    due.Add((s.Id, new Note($"{slot.Title} in {s.Minutes} min", string.Join(" · ", new[] { Timeslots.Format(slot.Start), slot.Place }.Where(x => !string.IsNullOrWhiteSpace(x))),
                        "/", $"slot-{slot.Id}-{on:yyyy-MM-dd}", ttl)));
        }
        return due;
    }

    /// <summary>
    /// Alerts for classes that changed after <paramref name="since"/> (up to <paramref name="until"/>): one per student and
    /// changed class, for students who picked it, have a device and didn't mute changes.
    /// </summary>
    public static async Task<List<(int StudentId, Note Note)>> ChangeAlerts(AppDb db, DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        if (semester is null) return [];
        var changed = await db.Sections.Where(s => s.Course.SemesterId == semester.Id && s.ChangedAt != null)
            .Select(s => new { s.Id, s.CourseId, s.Course.Code, s.Course.Name, s.ChangedAt, s.ChangeSummary }).ToListAsync(ct);
        // SQLite can't compare these stored times in a query, so the time filter runs here.
        changed = changed.Where(s => s.ChangedAt > since && s.ChangedAt <= until).ToList();
        if (changed.Count == 0) return [];
        var sectionIds = changed.Select(s => s.Id).ToList();
        var who = await db.Picks.Where(p => p.SectionId != null && sectionIds.Contains(p.SectionId.Value) && !p.Student.MuteChanges
                && db.PushSubscriptions.Any(x => x.StudentId == p.StudentId))
            .Select(p => new { p.StudentId, SectionId = p.SectionId!.Value }).ToListAsync(ct);
        return who.Select(w =>
        {
            var s = changed.First(c => c.Id == w.SectionId);
            return (w.StudentId, new Note($"{s.Code} changed", string.IsNullOrWhiteSpace(s.ChangeSummary) ? $"{s.Name}: check the new time and room." : $"{s.Name}: {s.ChangeSummary}",
                $"/course/{s.CourseId}", $"change-{s.Id}", 3 * 86_400));
        }).ToList();
    }
}

/// <summary>Sends notifications to students' devices, forgetting devices that have unsubscribed.</summary>
public sealed class PushSender(IHttpClientFactory http, IConfiguration config, ILogger<PushSender> log)
{
    /// <summary>Sends to every device of the given students; returns how many devices accepted it.</summary>
    public async Task<int> SendAsync(AppDb db, IReadOnlyCollection<int> studentIds, Note note, CancellationToken ct)
    {
        var subs = await db.PushSubscriptions.Where(p => studentIds.Contains(p.StudentId)).ToListAsync(ct);
        if (subs.Count == 0) return 0;
        var keys = await Notifications.KeysAsync(db, config, ct);
        var client = new WebPushClient(http.CreateClient("push"));
        var payload = JsonSerializer.Serialize(new { title = note.Title, body = note.Body, url = note.Url, tag = note.Tag });
        var sent = 0;
        foreach (var sub in subs)
        {
            try
            {
                await client.SendNotificationAsync(new WebPush.PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth), payload,
                    new Dictionary<string, object> { ["vapidDetails"] = keys, ["TTL"] = note.TtlSeconds }, ct);
                sent++;
            }
            catch (WebPushException e) when (e.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
            {
                // The device unsubscribed or the app was removed: stop sending there.
                db.PushSubscriptions.Remove(sub);
            }
            catch (Exception e) when (e is WebPushException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("A notification didn't go out: {Error}", e.Message);
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }
}

/// <summary>Every 30 seconds: sends the class reminders that have come due, and alerts for classes that changed.</summary>
public sealed class NotificationWorker(IServiceScopeFactory scopes, PushSender push, TimeProvider clock, ILogger<NotificationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var last = clock.GetUtcNow();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stop))
        {
            var now = clock.GetUtcNow();
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                foreach (var (student, note) in await Notifications.DueReminders(db, last, now, stop))
                    await push.SendAsync(db, [student], note, stop);

                // Changes since the last announced moment; the first run starts from now rather than announcing history.
                var setting = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == Notifications.ChangesSince, stop);
                if (setting is null)
                    db.AppSettings.Add(setting = new AppSetting { Key = Notifications.ChangesSince, Value = now.ToString("O"), UpdatedBy = "system", UpdatedAt = now });
                else
                {
                    var since = DateTimeOffset.Parse(setting.Value, System.Globalization.CultureInfo.InvariantCulture);
                    foreach (var (student, note) in await Notifications.ChangeAlerts(db, since, now, stop))
                        await push.SendAsync(db, [student], note, stop);
                    setting.Value = now.ToString("O");
                    setting.UpdatedAt = now;
                }
                await db.SaveChangesAsync(stop);
                last = now;
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                log.LogError(e, "Notifications round failed");
            }
        }
    }
}
