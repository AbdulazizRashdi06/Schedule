using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Teachers;

/// <summary>
/// A teacher's own devices, notification settings and calendar link (/api/teach). All of it is personal: an owner
/// looking in through "view as" gets 409. A staff account's devices are kept in their own table; a teaching
/// assistant's devices are their student devices, so their own reminders keep working.
/// </summary>
public static class TeachDevices
{
    public static void MapTeachDevices(this RouteGroupBuilder teach)
    {
        teach.MapGet("/notifications", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            var p = actor.Profile!;
            return Results.Ok(new { remindMinutes = p.RemindMinutes, changes = !p.MuteChanges, devices = await DevicesAsync(db, p, ct) });
        });

        // "changes" covers the alerts about university timetable changes only: someone changing your class is always told.
        teach.MapPut("/notifications", async (Notifications.SettingsBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            if (body.RemindMinutes is int m && !Notifications.RemindChoices.Contains(m)) return Errors.BadRequest(Notifications.PickMinutes);
            var id = actor.Profile!.Id;
            await db.Teachers.Where(t => t.Id == id)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.RemindMinutes, body.RemindMinutes).SetProperty(t => t.MuteChanges, !body.Changes), ct);
            return Results.NoContent();
        });

        // This browser or installed app accepts notifications. An endpoint is one device and belongs to one person:
        // saving it for a teacher takes it from the student who had it, in one transaction.
        teach.MapPost("/push", async (Notifications.SubscribeBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            if (Notifications.Subscription(body) is not { } device) return Errors.BadRequest(Notifications.IncompleteSubscription);
            var p = actor.Profile!;
            // A teaching assistant's device stays a student device.
            if (p.StudentId is int studentId) return await Notifications.SaveDeviceAsync(db, studentId, device, clock.GetUtcNow(), ct) ?? Results.NoContent();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var sub = await db.TeacherPushSubscriptions.FirstOrDefaultAsync(x => x.Endpoint == device.Endpoint, ct);
            if (sub is null && await db.TeacherPushSubscriptions.CountAsync(x => x.TeacherId == p.Id, ct) >= Notifications.MaxDevices)
                return Errors.Conflict($"Notifications are on for {Notifications.MaxDevices} devices already. Turn them off on one you don't use first.");
            if (sub is null) db.TeacherPushSubscriptions.Add(sub = new TeacherPushSubscription { Endpoint = device.Endpoint, CreatedAt = clock.GetUtcNow() });
            (sub.TeacherId, sub.P256dh, sub.Auth) = (p.Id, device.P256dh, device.Auth);
            await db.PushSubscriptions.Where(x => x.Endpoint == device.Endpoint).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // Only the caller's own row for that device.
        teach.MapPost("/push/remove", async (Notifications.EndpointBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            var p = actor.Profile!;
            if (p.StudentId is int studentId)
                await db.PushSubscriptions.Where(x => x.StudentId == studentId && x.Endpoint == body.Endpoint).ExecuteDeleteAsync(ct);
            else
                await db.TeacherPushSubscriptions.Where(x => x.TeacherId == p.Id && x.Endpoint == body.Endpoint).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // A test notification to every device the teacher turned on.
        teach.MapPost("/push/test", async (HttpContext http, AppDb db, PushSender push, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            var sent = await push.SendToTeacherAsync(db, actor.Profile!,
                new Note("Notifications are on", "Classy will remind you before class and tell you when one of your classes changes.", "/teach", "test", 600), ct);
            return sent == 0 ? Errors.Conflict("No device is set up for notifications yet.") : Results.Ok(new { sent });
        });

        // The teacher's feed link, made the first time they ask for it.
        teach.MapGet("/calendar", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            var id = actor.Profile!.Id;
            var token = await db.Teachers.Where(x => x.Id == id).Select(x => x.CalendarToken).FirstAsync(ct);
            if (token is null)
            {
                // Only set while still unset, then read back: two first requests at once get the same link, not one dead one.
                await db.Teachers.Where(x => x.Id == id && x.CalendarToken == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.CalendarToken, CalendarFeed.NewToken()), ct);
                token = await db.Teachers.Where(x => x.Id == id).Select(x => x.CalendarToken).FirstAsync(ct);
            }
            return Results.Ok(CalendarFeed.Links(http.Request, token!));
        });

        // A new link; calendars subscribed with the old one stop updating.
        teach.MapPost("/calendar/reset", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            var id = actor.Profile!.Id;
            var token = CalendarFeed.NewToken();
            await db.Teachers.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.CalendarToken, token), ct);
            return Results.Ok(CalendarFeed.Links(http.Request, token));
        });
    }

    /// <summary>How many devices a teaching account has notifications on: its own for a staff account, the student's for a teaching assistant.</summary>
    static Task<int> DevicesAsync(AppDb db, Teacher t, CancellationToken ct) => t.StudentId is int studentId
        ? db.PushSubscriptions.CountAsync(x => x.StudentId == studentId, ct)
        : db.TeacherPushSubscriptions.CountAsync(x => x.TeacherId == t.Id, ct);
}

/// <summary>Reminders before the classes a teacher teaches and before their own slots. Office hours send none.</summary>
public static class TeachReminders
{
    /// <summary>Teachers' reminders due up to this time have been sent (or given up on: never sent twice).</summary>
    public const string Since = "push.reminders.teach.since";
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The reminders due in a window of time: every class (one per real class, however many codes it has), own slot
    /// and batch rep's event starting <c>RemindMinutes</c> after a moment in (from, to], for teaching accounts that
    /// turned reminders on and have a device. Days off, the semester's weeks and teachers' changes apply as on the
    /// timetable: none for a cancelled class, one at the new time for a moved one.
    /// </summary>
    public static async Task<List<(Teacher Teacher, Note Note)>> DueAsync(AppDb db, DateTimeOffset from, DateTimeOffset to, CancellationToken ct, bool planner = false)
    {
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        if (semester is null || to <= from) return [];
        // "Has a device": its own for a staff account, the student's for a teaching assistant.
        var teachers = await db.Teachers.AsNoTracking().Where(t => t.RemindMinutes != null
                && (t.StudentId == null ? db.TeacherPushSubscriptions.Any(p => p.TeacherId == t.Id) : db.PushSubscriptions.Any(p => p.StudentId == t.StudentId)))
            .ToListAsync(ct);
        if (teachers.Count == 0) return [];
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var items = await TeacherSchedule.ItemsAsync(db, semester, teachers, slots: true, planner, ct, tracked: false);

        var due = new List<(Teacher, Note)>();
        var now = to.ToOffset(Oman);
        foreach (var t in teachers)
        {
            // Things starting in this window, shifted by how early the teacher wants to know.
            var minutes = t.RemindMinutes!.Value;
            var lo = from.AddMinutes(minutes).ToOffset(Oman);
            var hi = to.AddMinutes(minutes).ToOffset(Oman);
            var ttl = minutes * 60;
            foreach (var i in items[t.Id])
            {
                if (i.From == StudentSchedule.Source.Slot && i.EventKind == TeacherSchedule.OfficeKind) continue;
                for (var on = DateOnly.FromDateTime(lo.DateTime); on <= DateOnly.FromDateTime(hi.DateTime); on = on.AddDays(1))
                {
                    var at = new DateTimeOffset(on.ToDateTime(i.Start), Oman);
                    // A restarted worker catches up on missed reminders; "in 5 min" for a class that has already started is no use.
                    if (at <= lo || at > hi || at <= now || !span.Happens(i, on)) continue;
                    var time = Timeslots.Format(i.Start);
                    var day = on.ToString("yyyy-MM-dd", Inv);
                    var where = string.IsNullOrWhiteSpace(i.Room) ? "room to be announced" : i.Room;
                    due.Add((t, i.From switch
                    {
                        StudentSchedule.Source.Slot => new Note($"{i.Name} in {minutes} min", string.Join(" · ", new[] { time, i.Room }.Where(x => !string.IsNullOrWhiteSpace(x))),
                            "/teach", string.Create(Inv, $"tslot-{i.SlotId}-{day}"), ttl),
                        StudentSchedule.Source.Planner => new Note($"{i.Code} {(i.EventKind == "exam" ? "exam " : "")}in {minutes} min", $"{i.Name} · {time} · {where}",
                            string.Create(Inv, $"/teach?session=p{i.EventId}.{day}.{time}"), string.Create(Inv, $"tevent-{i.EventId}-{day}"), ttl),
                        _ => new Note($"{i.Code} in {minutes} min", $"{i.Name} · {time} · {where}",
                            string.Create(Inv, $"/teach?session=s{i.SectionId}.{day}.{time}"), string.Create(Inv, $"tclass-{i.SectionId}-{day}-{i.Start:HHmm}"), ttl),
                    }));
                }
            }
        }
        return due;
    }

    /// <summary>
    /// One round of teachers' reminders: those due in (<paramref name="last"/>, <paramref name="now"/>], the window of
    /// the students' reminders. They keep a mark of their own (<see cref="Since"/>), so a round whose student part
    /// failed and is repeated, a restart, or two processes at once never send a teacher's reminder twice: the window
    /// starts at the mark when there is one (never further back than the worker catches up), and it is claimed, by
    /// moving the mark with one conditional update, before anything is sent. A crash while sending loses the rest of
    /// that window rather than repeating it. Returns how many reminders were sent.
    /// </summary>
    public static async Task<int> RoundAsync(AppDb db, PushSender push, DateTimeOffset last, DateTimeOffset now, bool planner, CancellationToken ct)
    {
        now = now.ToUniversalTime();
        var mark = await db.AppSettings.AsNoTracking().Where(x => x.Key == Since).Select(x => x.Value).FirstOrDefaultAsync(ct);
        var from = mark is not null && DateTimeOffset.TryParse(mark, Inv, DateTimeStyles.None, out var saved) ? saved : last;
        if (from < now - NotificationWorker.CatchUp) from = now - NotificationWorker.CatchUp;
        // Another round already covered up to here (or the clock went back): nothing new is due.
        if (from >= now) return 0;

        // Read before the claim: if this fails, the window is still there for the next round.
        var due = await DueAsync(db, from, now, ct, planner);
        var value = now.ToString("O", Inv);
        if (mark is null)
        {
            db.AppSettings.Add(new AppSetting { Key = Since, Value = value, UpdatedBy = "system", UpdatedAt = now });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another process made the mark at the same moment: this window is theirs.
                db.ChangeTracker.Clear();
                return 0;
            }
        }
        else if (await db.AppSettings.Where(x => x.Key == Since && x.Value == mark)
                     .ExecuteUpdateAsync(u => u.SetProperty(x => x.Value, value).SetProperty(x => x.UpdatedAt, now), ct) != 1)
            return 0;

        foreach (var (teacher, note) in due)
            await push.SendToTeacherAsync(db, teacher, note, ct);
        return due.Count;
    }
}
