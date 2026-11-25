using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Teachers;
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
    /// <summary>Reminders due up to this time have been sent (kept so a restart picks up where the last process stopped).</summary>
    public const string RemindersSince = "push.reminders.since";
    /// <summary>Set when a semester is published (its name), removed once students were told it's out.</summary>
    public const string PublishPending = "push.publish.pending";
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);
    internal static readonly int[] RemindChoices = [5, 10, 15, 30, 60];
    internal const string PickMinutes = "Pick 5, 10, 15, 30 or 60 minutes before.";
    internal const string IncompleteSubscription = "This browser sent an incomplete subscription. Try again.";

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

    /// <summary>What a browser sent to subscribe, when it is complete and usable: a real push service's address and keys that can be encrypted to.</summary>
    internal static (string Endpoint, string P256dh, string Auth)? Subscription(SubscribeBody body) =>
        body.Endpoint is { Length: > 0 and < 1000 } endpoint && AllowedEndpoint(endpoint)
            && body.Keys?.P256dh is { Length: > 0 and < 200 } p256dh && body.Keys.Auth is { Length: > 0 and < 100 } auth
            && ValidKeys(p256dh, auth)
            ? (endpoint, p256dh, auth) : null;

    /// <summary>
    /// Saves this device for a student. An endpoint is one device and belongs to one person: a teacher who had it stops
    /// getting notifications on it, in the same transaction. Null when saved, else the refusal.
    /// </summary>
    internal static async Task<IResult?> SaveDeviceAsync(AppDb db, int studentId, (string Endpoint, string P256dh, string Auth) device, DateTimeOffset now, CancellationToken ct)
    {
        var (endpoint, p256dh, auth) = device;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var sub = await db.PushSubscriptions.FirstOrDefaultAsync(x => x.Endpoint == endpoint, ct);
        if (sub is null && await db.PushSubscriptions.CountAsync(x => x.StudentId == studentId, ct) >= MaxDevices)
            return Errors.Conflict($"Notifications are on for {MaxDevices} devices already. Turn them off on one you don't use first.");
        if (sub is null) db.PushSubscriptions.Add(sub = new Sub { Endpoint = endpoint, CreatedAt = now });
        sub.StudentId = studentId;
        sub.P256dh = p256dh;
        sub.Auth = auth;
        await db.TeacherPushSubscriptions.Where(x => x.Endpoint == endpoint).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return null;
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
            if (body.RemindMinutes is int m && !RemindChoices.Contains(m)) return Errors.BadRequest(PickMinutes);
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            s.RemindMinutes = body.RemindMinutes;
            s.MuteChanges = !body.Changes;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // This browser or installed app accepts notifications. An endpoint is one device: it moves to whoever signs in there.
        me.MapPost("/push", async (SubscribeBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (Subscription(body) is not { } device) return Errors.BadRequest(IncompleteSubscription);
            return await SaveDeviceAsync(db, http.User.Id(), device, clock.GetUtcNow(), ct) ?? Results.NoContent();
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
        var now = to.ToOffset(Oman);
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
                // A restarted worker catches up on missed reminders; "in 5 min" for a class that has already started is no use.
                if (at <= lo || at > hi || at <= now || !span.Happens(i, on)) continue;
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
    /// student with a device. Null when nothing is waiting to be announced. <c>Name</c> and <c>At</c> are the pending
    /// row as read, so the worker can claim exactly that one.
    /// </summary>
    public static async Task<(List<int> StudentIds, Note Note, string Name, DateTimeOffset At)?> PublishAnnouncement(AppDb db, CancellationToken ct)
    {
        var pending = await db.AppSettings.Where(x => x.Key == PublishPending).Select(x => new { x.Value, x.UpdatedAt }).FirstOrDefaultAsync(ct);
        if (pending is null) return null;
        var who = await db.PushSubscriptions.Select(p => p.StudentId).Distinct().ToListAsync(ct);
        return (who, new Note($"{pending.Value} is out", "Set up your courses in about a minute.", "/setup", "published", 3 * 86_400), pending.Value, pending.UpdatedAt);
    }

    /// <summary>
    /// Alerts for classes that changed after <paramref name="since"/> (up to <paramref name="until"/>): one per student and
    /// changed class, for students who picked it, have a device and didn't mute changes.
    /// </summary>
    public static async Task<List<(int StudentId, Note Note)>> ChangeAlerts(AppDb db, DateTimeOffset since, DateTimeOffset until, CancellationToken ct) =>
        (await Changes(db, since, until, ct)).Alerts;

    /// <summary>
    /// <see cref="ChangeAlerts"/>, plus the newest change time this read saw (null when none). Announcing stops there, not
    /// at <paramref name="until"/>: an upload stamps its changes before it commits, so changes stamped inside this window
    /// can still turn up after it was read.
    /// </summary>
    public static async Task<(List<(int StudentId, Note Note)> Alerts, DateTimeOffset? Newest)> Changes(AppDb db, DateTimeOffset since, DateTimeOffset until, CancellationToken ct)
    {
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        if (semester is null) return ([], null);
        // Times are stored as numbers (AppDb), so the database compares them: only this window's changes are read. The
        // stored number includes the offset, and every stored time is UTC, so compare in UTC too.
        (since, until) = (since.ToUniversalTime(), until.ToUniversalTime());
        var changed = await db.Sections.Where(s => s.Course.SemesterId == semester.Id && s.ChangedAt != null && s.ChangedAt > since && s.ChangedAt <= until)
            .Select(s => new { s.Id, s.CourseId, s.Course.Code, s.Course.Name, s.ChangeSummary, s.ChangedAt }).ToListAsync(ct);
        if (changed.Count == 0) return ([], null);
        var newest = changed.Max(s => s.ChangedAt);
        var sectionIds = changed.Select(s => s.Id).ToList();
        var who = await db.Picks.Where(p => p.SectionId != null && sectionIds.Contains(p.SectionId.Value) && !p.Student.MuteChanges
                && db.PushSubscriptions.Any(x => x.StudentId == p.StudentId))
            .Select(p => new { p.StudentId, SectionId = p.SectionId!.Value }).ToListAsync(ct);
        return (who.Select(w =>
        {
            var s = changed.First(c => c.Id == w.SectionId);
            return (w.StudentId, new Note($"{s.Code} changed", string.IsNullOrWhiteSpace(s.ChangeSummary) ? $"{s.Name}: check the new time and room." : $"{s.Name}: {s.ChangeSummary}",
                $"/course/{s.CourseId}", $"change-{s.Id}", 3 * 86_400));
        }).ToList(), newest);
    }
}

/// <summary>Sends notifications to students' and teachers' devices, forgetting devices that have unsubscribed.</summary>
public sealed class PushSender(IHttpClientFactory http, IConfiguration config, ILogger<PushSender> log)
{
    /// <summary>One browser or installed app, and how to forget it once it has unsubscribed.</summary>
    readonly record struct Device(string Endpoint, string P256dh, string Auth, Action Forget);

    /// <summary>Sends to every device of the given students; returns how many devices accepted it.</summary>
    public async Task<int> SendAsync(AppDb db, IReadOnlyCollection<int> studentIds, Note note, CancellationToken ct)
    {
        var subs = await db.PushSubscriptions.Where(p => studentIds.Contains(p.StudentId)).ToListAsync(ct);
        return await DeliverAsync(db, subs.Select(sub => new Device(sub.Endpoint, sub.P256dh, sub.Auth, () => db.PushSubscriptions.Remove(sub))).ToList(), note, ct);
    }

    /// <summary>
    /// Sends to a teaching account's devices: its own for a staff account, the student's for a teaching assistant.
    /// The only way teacher code reaches a device: a teacher id is never a student id.
    /// </summary>
    public async Task<int> SendToTeacherAsync(AppDb db, Teacher t, Note note, CancellationToken ct)
    {
        if (t.StudentId is int studentId) return await SendAsync(db, [studentId], note, ct);
        var subs = await db.TeacherPushSubscriptions.Where(p => p.TeacherId == t.Id).ToListAsync(ct);
        return await DeliverAsync(db, subs.Select(sub => new Device(sub.Endpoint, sub.P256dh, sub.Auth, () => db.TeacherPushSubscriptions.Remove(sub))).ToList(), note, ct);
    }

    async Task<int> DeliverAsync(AppDb db, IReadOnlyList<Device> devices, Note note, CancellationToken ct)
    {
        if (devices.Count == 0) return 0;
        var keys = await Notifications.KeysAsync(db, config, ct);
        var client = new WebPushClient(http.CreateClient("push"));
        var payload = JsonSerializer.Serialize(new { title = note.Title, body = note.Body, url = note.Url, tag = note.Tag });
        var sent = 0;
        foreach (var device in devices)
        {
            // Keys nothing can be encrypted to (saved before keys were checked) would fail on every send: forget them.
            if (!Notifications.ValidKeys(device.P256dh, device.Auth) || !Notifications.AllowedEndpoint(device.Endpoint))
            {
                log.LogWarning("Dropped a device with unusable push keys or address");
                device.Forget();
                continue;
            }
            try
            {
                await client.SendNotificationAsync(new WebPush.PushSubscription(device.Endpoint, device.P256dh, device.Auth), payload,
                    new Dictionary<string, object> { ["vapidDetails"] = keys, ["TTL"] = note.TtlSeconds }, ct);
                sent++;
            }
            catch (WebPushException e) when (e.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
            {
                // The device unsubscribed or the app was removed: stop sending there.
                device.Forget();
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
/// out" after one was published; then teachers' reminders and the notices about teachers' class changes. Each part
/// fails on its own, so one problem never repeats or stops the others.
/// </summary>
public sealed class NotificationWorker(IServiceScopeFactory scopes, PushSender push, TimeProvider clock, ILogger<NotificationWorker> log) : BackgroundService
{
    /// <summary>
    /// How far back a restarted worker sends reminders it missed; older ones would only be out of date. Reminders for a
    /// class that has already started are never sent (<see cref="Notifications.DueReminders"/>).
    /// </summary>
    public static readonly TimeSpan CatchUp = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var last = await RemindersFromAsync(clock.GetUtcNow(), stop);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stop))
            last = await RoundAsync(last, clock.GetUtcNow(), stop);
    }

    /// <summary>
    /// Where the first round's reminders start: where the last process got to (saved every round), so reminders due while
    /// the server restarted still go out and none go out twice, but never more than <see cref="CatchUp"/> back.
    /// </summary>
    public async Task<DateTimeOffset> RemindersFromAsync(DateTimeOffset now, CancellationToken stop)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var saved = await db.AppSettings.Where(x => x.Key == Notifications.RemindersSince).Select(x => x.Value).FirstOrDefaultAsync(stop);
            if (saved is null || !DateTimeOffset.TryParse(saved, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var from)) return now;
            return from < now - CatchUp ? now - CatchUp : from > now ? now : from;
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            log.LogError(e, "Couldn't read where reminders stopped");
            return now;
        }
    }

    /// <summary>One round: reminders due in (last, now], then change alerts, then a semester announcement. Returns where the next round's reminders start.</summary>
    public async Task<DateTimeOffset> RoundAsync(DateTimeOffset last, DateTimeOffset now, CancellationToken stop)
    {
        // Teachers' reminders cover the same window; the students' part below moves `last` on once theirs are out.
        var window = last;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var planner = await Planner.PlannerEngine.Enabled(db, scope.ServiceProvider.GetRequiredService<IConfiguration>(), stop);
            foreach (var (student, note) in await Notifications.DueReminders(db, last, now, stop, planner))
                await push.SendAsync(db, [student], note, stop);
            // The reminders went out: whatever fails below must not send them again next round.
            last = now;
            var mark = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == Notifications.RemindersSince, stop);
            if (mark is null) db.AppSettings.Add(mark = new AppSetting { Key = Notifications.RemindersSince, UpdatedBy = "system" });
            mark.Value = now.ToString("O");
            mark.UpdatedAt = now;
            await db.SaveChangesAsync(stop);
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
                var (alerts, newest) = await Notifications.Changes(db, since, now, stop);
                foreach (var (student, note) in alerts)
                    await push.SendAsync(db, [student], note, stop);
                // Only up to the newest change read, not to now: one stamped earlier but committed after this read is
                // still ahead of the mark next round.
                if (newest is { } upTo && upTo > since)
                {
                    setting.Value = upTo.ToString("O");
                    setting.UpdatedAt = now;
                }
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
            // A semester was published: tell everyone with a device, once. It's claimed before sending, and only if it's
            // still the one read: a semester published meanwhile stays pending for the next round, and a restart part-way
            // through doesn't send it to everyone again.
            if (await Notifications.PublishAnnouncement(db, stop) is { } announce
                && await db.AppSettings.Where(x => x.Key == Notifications.PublishPending && x.Value == announce.Name && x.UpdatedAt == announce.At).ExecuteDeleteAsync(stop) == 1
                && announce.StudentIds.Count > 0)
                await push.SendAsync(db, announce.StudentIds, announce.Note, stop);
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            log.LogError(e, "Semester announcement failed");
        }

        // Teachers' reminders, apart from the students': whatever goes wrong here, a student's reminder is neither held
        // back nor sent again. They keep their own mark, so they go out once however the parts above went.
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var planner = await Planner.PlannerEngine.Enabled(db, scope.ServiceProvider.GetRequiredService<IConfiguration>(), stop);
            await TeachReminders.RoundAsync(db, push, window, now, planner, stop);
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            log.LogError(e, "Teacher reminders failed");
        }

        // What teachers did to a class (cancelled, moved, another room, an extra one): to its students, the other
        // teachers and staff. Each notice is claimed before it is sent.
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await TeachNotices.RoundAsync(db, push, now, stop);
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            log.LogError(e, "Teacher change notices failed");
        }
        return last;
    }
}
