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
    /// <summary>Set when a semester is published (its name), removed once students were told it's out.</summary>
    public const string PublishPending = "push.publish.pending";
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

    /// <summary>Devices one student can have notifications on.</summary>
    public const int MaxDevices = 10;

    /// <summary>
    /// Only real Web Push services (Chrome/Edge/Android, Firefox, Windows, Apple) get an endpoint: the server posts to it,
    /// so any other https address could be used to make the server call it.
    /// </summary>
    public static bool AllowedEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        var host = uri.Host.ToLowerInvariant();
        return host == "fcm.googleapis.com" || host.EndsWith(".push.services.mozilla.com") || host.EndsWith(".notify.windows.com") || host.EndsWith(".push.apple.com");
    }

    static byte[]? FromBase64Url(string s)
    {
        var b64 = s.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// A browser's keys as Web Push defines them: a P-256 public key (65 bytes, uncompressed, on the curve) and a 16-byte
    /// secret. Anything else can't be encrypted to, and is refused here rather than failing on every send.
    /// </summary>
    public static bool ValidKeys(string p256dh, string auth)
    {
        if (FromBase64Url(auth) is not { Length: 16 }) return false;
        if (FromBase64Url(p256dh) is not { Length: 65 } point || point[0] != 0x04) return false;
        try
        {
            using var ecdh = System.Security.Cryptography.ECDiffieHellman.Create(new System.Security.Cryptography.ECParameters
            {
                Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
                Q = new System.Security.Cryptography.ECPoint { X = point[1..33], Y = point[33..] },
            });
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
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
            if (body.Endpoint is not { Length: > 0 and < 1000 } endpoint || !AllowedEndpoint(endpoint)
                || body.Keys?.P256dh is not { Length: > 0 and < 200 } p256dh || body.Keys.Auth is not { Length: > 0 and < 100 } auth
                || !ValidKeys(p256dh, auth))
                return Errors.BadRequest("This browser sent an incomplete subscription. Try again.");
            var id = http.User.Id();
            var sub = await db.PushSubscriptions.FirstOrDefaultAsync(x => x.Endpoint == endpoint, ct);
            if (sub is null && await db.PushSubscriptions.CountAsync(x => x.StudentId == id, ct) >= MaxDevices)
                return Errors.Conflict($"Notifications are on for {MaxDevices} devices already. Turn them off on one you don't use first.");
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
    /// The reminders due in a window of time: every class, own slot and batch rep's event starting <c>RemindMinutes</c>
    /// after a moment in (from, to], for students who turned reminders on and have a device. Days off, the semester's
    /// weeks and (with <paramref name="planner"/>) classes the batch reps cancelled or moved apply, as on the timetable.
    /// </summary>
    public static async Task<List<(int StudentId, Note Note)>> DueReminders(AppDb db, DateTimeOffset from, DateTimeOffset to, CancellationToken ct, bool planner = false)
    {
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        if (semester is null || to <= from) return [];
        var students = await db.Students.Where(s => s.RemindMinutes != null && db.PushSubscriptions.Any(p => p.StudentId == s.Id))
            .Select(s => new { s.Id, Minutes = s.RemindMinutes!.Value }).ToListAsync(ct);
        if (students.Count == 0) return [];
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var items = (await StudentSchedule.ForAsync(db, semester.Id, students.Select(s => s.Id).ToList(), slots: true, planner, ct)).ToLookup(i => i.StudentId);

        var due = new List<(int, Note)>();
        foreach (var s in students)
        {
            // Things starting in this window, shifted by how early the student wants to know.
            var lo = from.AddMinutes(s.Minutes).ToOffset(Oman);
            var hi = to.AddMinutes(s.Minutes).ToOffset(Oman);
            var ttl = s.Minutes * 60;
            foreach (var i in items[s.Id])
            for (var on = DateOnly.FromDateTime(lo.DateTime); on <= DateOnly.FromDateTime(hi.DateTime); on = on.AddDays(1))
            {
                var at = new DateTimeOffset(on.ToDateTime(i.Start), Oman);
                if (at <= lo || at > hi || !span.Happens(i, on)) continue;
                var time = Timeslots.Format(i.Start);
                var where = string.IsNullOrWhiteSpace(i.Room) ? "room to be announced" : i.Room;
                due.Add((s.Id, i.From switch
                {
                    StudentSchedule.Source.Slot => new Note($"{i.Name} in {s.Minutes} min", string.Join(" · ", new[] { time, i.Room }.Where(x => !string.IsNullOrWhiteSpace(x))),
                        "/", $"slot-{i.SlotId}-{on:yyyy-MM-dd}", ttl),
                    StudentSchedule.Source.Planner => new Note($"{i.Code} {(i.EventKind == "exam" ? "exam " : "")}in {s.Minutes} min", $"{i.Name} · {time} · {where}",
                        $"/?session=p{i.EventId}.{on:yyyy-MM-dd}.{time}", $"event-{i.EventId}-{on:yyyy-MM-dd}", ttl),
                    _ => new Note($"{i.Code} in {s.Minutes} min", $"{i.Name} · {time} · {where}",
                        $"/?session={i.CourseId}.{on:yyyy-MM-dd}.{time}", $"class-{i.CourseId}-{on:yyyy-MM-dd}-{i.Start:HHmm}", ttl),
                }));
            }
        }
        return due;
    }

    /// <summary>
    /// "A new semester is out", once a semester was published (<see cref="PublishPending"/> holds its name): for every
    /// student with a device. Null when nothing is waiting to be announced.
    /// </summary>
    public static async Task<(List<int> StudentIds, Note Note)?> PublishAnnouncement(AppDb db, CancellationToken ct)
    {
        var pending = await db.AppSettings.Where(x => x.Key == PublishPending).Select(x => x.Value).FirstOrDefaultAsync(ct);
        if (pending is null) return null;
        var who = await db.PushSubscriptions.Select(p => p.StudentId).Distinct().ToListAsync(ct);
        return (who, new Note($"{pending} is out", "Set up your courses in about a minute.", "/setup", "published", 3 * 86_400));
    }

    /// <summary>
    /// Alerts for classes that changed after <paramref name="since"/> (up to <paramref name="until"/>): one per student and
    /// changed class, for students who picked it, have a device and didn't mute changes.
    /// </summary>
    public static async Task<List<(int StudentId, Note Note)>> ChangeAlerts(AppDb db, DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        if (semester is null) return [];
        // Times are stored as numbers (AppDb), so the database compares them: only this window's changes are read. The
        // stored number includes the offset, and every stored time is UTC, so compare in UTC too.
        (since, until) = (since.ToUniversalTime(), until.ToUniversalTime());
        var changed = await db.Sections.Where(s => s.Course.SemesterId == semester.Id && s.ChangedAt != null && s.ChangedAt > since && s.ChangedAt <= until)
            .Select(s => new { s.Id, s.CourseId, s.Course.Code, s.Course.Name, s.ChangeSummary }).ToListAsync(ct);
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
            // Keys nothing can be encrypted to (saved before keys were checked) would fail on every send: forget them.
            if (!Notifications.ValidKeys(sub.P256dh, sub.Auth) || !Notifications.AllowedEndpoint(sub.Endpoint))
            {
                log.LogWarning("Dropped a device with unusable push keys or address");
                db.PushSubscriptions.Remove(sub);
                continue;
            }
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
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // One device failing never stops the others.
                log.LogWarning("A notification didn't go out: {Error}", e.Message);
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }
}

/// <summary>
/// Every 30 seconds: sends the class reminders that have come due, alerts for classes that changed, and "a new semester is
/// out" after one was published. Each part fails on its own, so one problem never repeats or stops the others.
/// </summary>
public sealed class NotificationWorker(IServiceScopeFactory scopes, PushSender push, TimeProvider clock, ILogger<NotificationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var last = clock.GetUtcNow();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stop))
            last = await RoundAsync(last, clock.GetUtcNow(), stop);
    }

    /// <summary>One round: reminders due in (last, now], then change alerts, then a semester announcement. Returns where the next round's reminders start.</summary>
    public async Task<DateTimeOffset> RoundAsync(DateTimeOffset last, DateTimeOffset now, CancellationToken stop)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var planner = await Planner.PlannerEngine.Enabled(db, scope.ServiceProvider.GetRequiredService<IConfiguration>(), stop);
            foreach (var (student, note) in await Notifications.DueReminders(db, last, now, stop, planner))
                await push.SendAsync(db, [student], note, stop);
            // The reminders went out: whatever fails below must not send them again next round.
            last = now;
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            log.LogError(e, "Notifications round failed");
        }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
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
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            log.LogError(e, "Change alerts failed");
        }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            // A semester was published: tell everyone with a device, once.
            if (await Notifications.PublishAnnouncement(db, stop) is { } announce)
            {
                if (announce.StudentIds.Count > 0) await push.SendAsync(db, announce.StudentIds, announce.Note, stop);
                await db.AppSettings.Where(x => x.Key == Notifications.PublishPending).ExecuteDeleteAsync(stop);
            }
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            log.LogError(e, "Semester announcement failed");
        }
        return last;
    }
}
