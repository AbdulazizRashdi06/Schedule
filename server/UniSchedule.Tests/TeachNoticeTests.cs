using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UniSchedule.Api.Data;
using UniSchedule.Api.Students;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.ChangeKit;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// Telling people about teachers' class changes, once: the outbox round (claims, expiry, an undo or an edit before
/// and after sending), who is told, the daily alert to staff, teachers' reminders, whose device gets what, and the
/// teacher's calendar link.
/// </summary>
public class TeachNoticeTests
{
    static object Cancel(int section, DateOnly date, string start = "08:00", int[]? sectionIds = null) =>
        new { kind = "cancel", sectionId = section, date = Iso(date), start, sectionIds };

    static object Move(int section, DateOnly date, string start, DateOnly to, string newStart, string? room = null, int[]? sectionIds = null) =>
        new { kind = "move", sectionId = section, date = Iso(date), start, sectionIds, newDate = Iso(to), newStart, room };

    static object Extra(int[] sectionIds, DateOnly to, string newStart, string newEnd) =>
        new { kind = "extra", sectionIds, newDate = Iso(to), newStart, newEnd, room = "B202", type = "lecture" };

    sealed record Ids(int Be, int Ibsm, int Log, int Be2, int Cs1, int Cs2);

    static async Task<Ids> Sections(TeachWorld w) => new(await w.Section("BE1101", "1"), await w.Section("IBSM1101", "1"), await w.Section("LOG1101", "1"),
        await w.Section("BE1101", "2"), await w.Section("CS2201", "1"), await w.Section("CS2201", "2"));

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static string Address(string name) => $"https://fcm.googleapis.com/fcm/send/{name}";

    /// <summary>What a browser sends to turn notifications on: a push address and real keys.</summary>
    static object Device(string name)
    {
        using var ec = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var q = ec.ExportParameters(false).Q;
        return new { endpoint = Address(name), keys = new { p256dh = B64Url([0x04, .. q.X!, .. q.Y!]), auth = B64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)) } };
    }

    static async Task On(HttpClient c, string basePath, string name) =>
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api{basePath}/push", Device(name))).StatusCode);

    static async Task Settings(HttpClient c, string basePath, int? remindMinutes, bool changes = true) =>
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api{basePath}/notifications", new { remindMinutes, changes })).StatusCode);

    /// <summary>The devices pushed to so far, by name, in order.</summary>
    static List<string> Pushed(TeachWorld w) => w.Pushes.Sent.Select(u => u[(u.LastIndexOf('/') + 1)..]).ToList();

    /// <summary>One round of the outbox at the clock's time. Returns how many notices it pushed.</summary>
    static Task<int> Send(TeachWorld w) => w.Db(db => TeachNotices.RoundAsync(db, w.Services.GetRequiredService<PushSender>(), w.Time.GetUtcNow(), default));

    static NotificationWorker Worker(TeachWorld w) => new(w.Services.GetRequiredService<IServiceScopeFactory>(), w.Services.GetRequiredService<PushSender>(),
        w.Time, NullLogger<NotificationWorker>.Instance);

    static Task<List<(Teacher Teacher, Note Note)>> Due(TeachWorld w, DateTimeOffset from, DateTimeOffset to) =>
        w.Db(db => TeachReminders.DueAsync(db, from, to, default));

    static DateTimeOffset At(DateOnly d, int h, int m, int s = 0) => OmanTime(d, h, m).AddSeconds(s);

    /// <summary>Ends changes the way an import, or staff, will: through the one door every ending uses.</summary>
    static Task End(TeachWorld w, string group, string reason) => w.Db(async db =>
    {
        var rows = await db.ClassChanges.Where(c => c.GroupId == group && c.EndedAt == null).ToListAsync();
        await ChangeRules.EndAsync(db, rows, reason, "staff:test", w.Time.GetUtcNow());
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Students_of_the_section_get_one_push_and_a_later_state_reuses_the_tag()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var sun = w.Date(0, 0);
        // Student 0 (BE1101) has a device; student 1 (IBSM1101) has one but muted changes; student 2 (LOG1101) has none.
        await On(w.Students[0], "/me", "s0");
        await On(w.Students[1], "/me", "s1");
        await Settings(w.Students[1], "/me", null, changes: false);

        var cancel = await w.Teacher1.Made(Cancel(s.Be, sun));
        Assert.Equal(3, (await w.Notices(TeachNotice.Students)).Count);
        Assert.Equal(3, await Send(w));
        Assert.Equal(["s0"], Pushed(w));
        Assert.All(await w.Notices(TeachNotice.Students), n => Assert.True(n is { ClaimedAt: not null, SentAt: not null }));

        // Sent is sent: more rounds push nothing.
        Assert.Equal(0, await Send(w));
        w.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await Send(w));
        Assert.Equal(["s0"], Pushed(w));

        // The next state of the same session carries the same tag, so it replaces the first on the phone.
        await Json(await w.Teacher1.Replace(cancel, new { kind = "move", newDate = Iso(w.Date(0, 3)), newStart = "10:00", room = "B202" }));
        Assert.Equal(3, await Send(w));
        Assert.Equal(["s0", "s0"], Pushed(w));
        var toBe = (await w.Notices(TeachNotice.Students)).Where(n => n.SectionId == s.Be).ToList();
        Assert.Equal(["BE1101 cancelled", "BE1101 moved"], toBe.Select(n => n.Title));
        Assert.Equal($"tchange-{s.Be}-{sun:yyyyMMdd}-0800", Assert.Single(toBe.Select(n => n.Tag).Distinct()));
    }

    [Fact]
    public async Task Undone_or_edited_before_a_round_and_undone_after_one()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        await On(w.Students[0], "/me", "s0");

        // Made and undone before any round: nobody hears of it.
        var first = await w.Teacher1.Made(Cancel(s.Be, w.Date(0, 0)));
        await Json(await w.Teacher1.Undo(first));
        Assert.Empty(await w.Notices(TeachNotice.Students));
        Assert.Equal(0, await Send(w));
        Assert.Empty(Pushed(w));

        // Edited before a round: one notification, with the new details.
        var second = await w.Teacher1.Made(Cancel(s.Be, w.Date(1, 0)));
        var edited = Group(await Json(await w.Teacher1.Replace(second, new { kind = "move", newDate = Iso(w.Date(1, 3)), newStart = "10:00", room = "B202" })));
        Assert.Equal(3, await Send(w));
        Assert.Equal(["s0"], Pushed(w));
        Assert.Equal("BE1101 moved", Assert.Single(await w.Notices(TeachNotice.Students), n => n.SectionId == s.Be).Title);

        // Undone after it was sent: "back on", once.
        await Json(await w.Teacher1.Undo(edited));
        Assert.Equal(3, await Send(w));
        Assert.Equal(0, await Send(w));
        Assert.Equal(["s0", "s0"], Pushed(w));
        Assert.Equal(["BE1101 moved", "BE1101 is back on"], (await w.Notices(TeachNotice.Students)).Where(n => n.SectionId == s.Be).Select(n => n.Title));
    }

    [Fact]
    public async Task A_notice_is_claimed_before_it_is_sent_so_it_goes_out_once()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        await On(w.Students[2], "/me", "s2");
        var cancel = await w.Teacher2.Made(Cancel(s.Cs2, w.Date(0, 2)));
        var id = Assert.Single(await w.Notices(TeachNotice.Students)).Id;

        // Another round has claimed it and is sending: this one keeps its hands off.
        await w.Db(db => db.TeachNotices.Where(n => n.Id == id).ExecuteUpdateAsync(u => u.SetProperty(n => n.ClaimedAt, w.Time.GetUtcNow())));
        Assert.Equal(0, await Send(w));
        w.Time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(0, await Send(w));
        Assert.Empty(Pushed(w));

        // That sender died before sending: after two minutes the notice goes out, once.
        w.Time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await Send(w));
        Assert.Equal(0, await Send(w));
        Assert.Equal(["s2"], Pushed(w));

        // Two rounds at the same moment (an old and a new process): one of them sends it.
        await Json(await w.Teacher2.Undo(cancel));
        var both = await Task.WhenAll(Send(w), Send(w));
        Assert.Equal(1, both.Sum());
        Assert.Equal(["s2", "s2"], Pushed(w));
    }

    [Fact]
    public async Task After_a_sender_died_an_undo_sends_only_back_on_and_in_order()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        await On(w.Students[2], "/me", "s2");
        var cancel = await w.Teacher2.Made(Cancel(s.Cs2, w.Date(0, 2)));
        // Claimed, and the sender died. Students count as told, so the undo writes "back on".
        await w.Db(db => db.TeachNotices.Where(n => n.Audience == TeachNotice.Students).ExecuteUpdateAsync(u => u.SetProperty(n => n.ClaimedAt, w.Time.GetUtcNow())));
        await Json(await w.Teacher2.Undo(cancel));
        Assert.Equal(["change", "back-on"], (await w.Notices(TeachNotice.Students)).Select(n => n.Kind));

        // While the claim on "cancelled" holds, "back on" waits behind it: the two never arrive the wrong way round.
        Assert.Equal(0, await Send(w));
        Assert.Empty(Pushed(w));

        // Once the claim has run out, only the newest state is sent: "cancelled" would be false by now.
        w.Time.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(1, await Send(w));
        Assert.Equal(0, await Send(w));
        Assert.Equal(["s2"], Pushed(w));
        var rows = await w.Notices(TeachNotice.Students);
        Assert.All(rows, n => Assert.NotNull(n.SentAt));
        // The one that was pushed is the one claimed in that round.
        Assert.Equal(w.Time.GetUtcNow(), rows.Single(n => n.Kind == "back-on").ClaimedAt);
        Assert.NotEqual(w.Time.GetUtcNow(), rows.Single(n => n.Kind == "change").ClaimedAt);
    }

    [Fact]
    public async Task Expired_notices_are_not_pushed_but_a_move_to_an_earlier_time_still_is()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        await On(w.Students[0], "/me", "s0");
        await On(w.Students[2], "/me", "s2");
        var sun = w.Date(0, 0);

        // No round ran until a minute after the class started: the cancellation is no use to anyone now.
        await w.Teacher1.Made(Cancel(s.Be, sun));
        w.GoTo(sun, 8, 1);
        Assert.Equal(0, await Send(w));
        Assert.Empty(Pushed(w));
        Assert.All(await w.Notices(TeachNotice.Students), n => Assert.True(n is { SentAt: not null, ClaimedAt: null }));

        // Tuesday's class moved to Monday 10:00. After Monday 10:00 the old slot is still ahead: students must know it is off.
        var t2 = await w.Fresh(TeachWorld.Teacher2Email);
        await t2.Made(Move(s.Cs2, w.Date(1, 2), "08:00", w.Date(1, 1), "10:00"));
        w.GoTo(w.Date(1, 1), 10, 1);
        Assert.Equal(1, await Send(w));
        Assert.Equal(["s2"], Pushed(w));
    }

    [Fact]
    public async Task A_dropped_change_is_never_announced_and_never_followed_by_back_on()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        await On(w.Students[2], "/me", "s2");

        // Dropped (the university moved the class) before a round ran: nothing is pushed.
        var first = await w.Teacher2.Made(Cancel(s.Cs2, w.Date(0, 2)));
        await End(w, first, "dropped-moved");
        Assert.Empty(await w.Notices(TeachNotice.Students));
        Assert.Equal(0, await Send(w));
        Assert.Empty(Pushed(w));

        // Dropped after the announcement: "back on" would be false, so nothing more is sent from here.
        var second = await w.Teacher2.Made(Cancel(s.Cs2, w.Date(1, 2)));
        Assert.Equal(1, await Send(w));
        await End(w, second, "dropped-moved");
        Assert.Equal(0, await Send(w));
        Assert.Equal(["s2"], Pushed(w));
        Assert.Equal(["change"], (await w.Notices(TeachNotice.Students)).Select(n => n.Kind));

        // A notice whose section is gone has nobody to tell: it is closed without a push.
        await w.Db(async db =>
        {
            db.TeachNotices.Add(new TeachNotice { Audience = TeachNotice.Students, SectionId = null, Kind = "change", Title = "X", Body = "", Url = "/", Tag = "t", CreatedAt = w.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        });
        Assert.Equal(0, await Send(w));
        Assert.All(await w.Notices(TeachNotice.Students), n => Assert.NotNull(n.SentAt));
        Assert.Equal(["s2"], Pushed(w));
    }

    [Fact]
    public async Task The_other_teachers_are_told_once_each_and_the_maker_is_not()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        // Teacher 2 and a third teacher both added the class Teacher 1 is named on (all three codes for Teacher 2).
        foreach (var id in new[] { s.Be, s.Ibsm, s.Log }) await w.Teacher2.AddSection(id);
        var third = await w.SignInTeacher("third.person@gutech.edu.om");
        await third.AddSection(s.Be);
        var thirdId = await w.Db(db => db.Teachers.Where(t => t.Email == "third.person@gutech.edu.om").Select(t => t.Id).SingleAsync());
        await On(w.Teacher1, "/teach", "t1");
        await On(w.Teacher2, "/teach", "t2");

        // One cancel of the three-code class: the named teacher and the co-teacher get one notice each, the maker none.
        var cancel = await w.Teacher2.Made(Cancel(s.Be, w.Date(0, 0)));
        var told = await w.Notices(TeachNotice.ToTeacher);
        Assert.Equal([w.Teacher1Id, thirdId], told.Select(n => n.TeacherId!.Value).Order());
        Assert.All(told, n => Assert.Equal(("other-teacher", $"{TeachWorld.Teacher2Name} changed your class"), (n.Kind, n.Title)));
        Assert.All(told, n => Assert.StartsWith("BE1101 · IBSM1101 · LOG1101 cancelled", n.Body));
        await Send(w);
        Assert.Equal(["t1"], Pushed(w));

        // The inbox shows it, until "Got it".
        var inbox = (await Json(await w.Teacher1.GetAsync("/api/teach/me"))).GetProperty("notices").EnumerateArray().ToList();
        Assert.Equal("other-teacher", Assert.Single(inbox).GetProperty("kind").GetString());
        Assert.Empty((await Json(await w.Teacher2.GetAsync("/api/teach/me"))).GetProperty("notices").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/teach/notices/seen", new { ids = new[] { inbox[0].GetProperty("id").GetInt32() } })).StatusCode);
        Assert.Empty((await Json(await w.Teacher1.GetAsync("/api/teach/me"))).GetProperty("notices").EnumerateArray());

        // Staff undo it: the maker is told too, and their device gets it.
        await End(w, cancel, "undone-by-staff");
        var undone = (await w.Notices(TeachNotice.ToTeacher)).Where(n => n.Kind == "undone").ToList();
        Assert.Equal([w.Teacher1Id, w.Teacher2Id, thirdId], undone.Select(n => n.TeacherId!.Value).Order());
        Assert.All(undone, n => Assert.EndsWith("was undone by Classy staff", n.Title));
        await Send(w);
        Assert.Equal(["t1", "t1", "t2"], Pushed(w).Order());
        Assert.Equal("undone", Assert.Single((await Json(await w.Teacher2.GetAsync("/api/teach/me"))).GetProperty("notices").EnumerateArray()).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task The_sixth_change_of_an_Oman_day_tells_staff_once()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var owner = await w.SignInStudentOwner();
        await On(owner, "/me", "owner");
        var day = DateOnly.FromDateTime(w.Time.GetUtcNow().UtcDateTime).AddDays(1);
        DateTimeOffset Utc(DateOnly d, int h, int m) => new(d.ToDateTime(new TimeOnly(h, m)), TimeSpan.Zero);
        var n = 0;
        async Task Change(int count)
        {
            for (var i = 0; i < count; i++, n++)
                await w.Teacher1.Made(Extra([s.Be], w.Date(1, 1 + n / 6), $"{10 + n % 6}:00", $"{10 + n % 6}:45"));
        }
        Task<List<TeachNotice>> Staff() => w.Notices(TeachNotice.Staff);

        // 19:30 UTC is 23:30 in Oman: five changes late on one Oman day.
        w.Time.Set(Utc(day, 19, 30));
        await Change(5);
        // 20:30 UTC is 00:30 the next Oman day. The count starts again, though the UTC date is the same.
        w.Time.Set(Utc(day, 20, 30));
        await Change(3);
        Assert.Empty(await Staff());
        // 23:30 UTC is 03:30 in Oman, still that day: the sixth change of it raises the alert.
        w.Time.Set(Utc(day, 23, 30));
        await Change(2);
        Assert.Empty(await Staff());
        await Change(1);
        var alert = Assert.Single(await Staff());
        Assert.Equal(("many-changes", $"{TeachWorld.Teacher1Name} has made 6 class changes today", "/admin/teacher-changes", $"busy-{w.Teacher1Id}-{day.AddDays(1):yyyyMMdd}"),
            (alert.Kind, alert.Title, alert.Url, alert.Tag));
        // Past midnight UTC it is still the same Oman day: no second alert, and nobody is stopped.
        w.Time.Set(Utc(day.AddDays(1), 0, 30));
        await Change(2);
        Assert.Single(await Staff());
        Assert.Equal(day.AddDays(1), await w.Db(db => db.Teachers.Where(t => t.Id == w.Teacher1Id).Select(t => t.BusyAlertOn).SingleAsync()));

        // Owners who are students get it on their devices.
        await Send(w);
        Assert.Equal(["owner"], Pushed(w));

        // What an owner does through "view as" is not the teacher's doing.
        var viewing = TeachWorld.ViewAs(w.Owner, TeacherKeys.For(new Teacher { Id = w.Teacher2Id }));
        for (var i = 0; i < 7; i++)
            await viewing.Made(Extra([s.Cs1], w.Date(1, 3), $"{10 + i}:00", $"{10 + i}:45"));
        Assert.Single(await Staff());
    }

    [Fact]
    public async Task Teacher_reminders_one_per_real_class_none_for_office_hours_or_a_cancelled_class()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (sun, next) = (w.Date(0, 0), w.Date(1, 0));
        await Settings(w.Teacher1, "/teach", 15);
        var slot = (await Json(await w.Teacher1.PostAsJsonAsync("/api/teach/slots", new { title = "Dentist", day = 0, start = "12:00", end = "13:00" }))).GetProperty("id").GetInt32();
        await Json(await w.Teacher1.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-204", note = "", hours = new[] { new { day = 0, start = "14:00", end = "15:00", room = "B-204" } } }));

        // No device, no reminders.
        Assert.Empty(await Due(w, At(sun, 7, 44, 30), At(sun, 7, 45, 10)));
        await On(w.Teacher1, "/teach", "t1");

        // One reminder for the class under three codes, 15 minutes before; not a moment earlier or later.
        var (teacher, note) = Assert.Single(await Due(w, At(sun, 7, 44, 30), At(sun, 7, 45, 10)));
        Assert.Equal(w.Teacher1Id, teacher.Id);
        Assert.Equal(new Note("BE1101 · IBSM1101 · LOG1101 in 15 min", "Business Economics · 08:00 · A101", $"/teach?session=s{s.Be}.{Iso(sun)}.08:00", $"tclass-{s.Be}-{Iso(sun)}-0800", 900), note);
        Assert.Empty(await Due(w, At(sun, 7, 43, 50), At(sun, 7, 44, 30)));
        Assert.Empty(await Due(w, At(sun, 7, 45, 10), At(sun, 7, 45, 40)));

        // An own slot has one; office hours never do.
        Assert.Equal(new Note("Dentist in 15 min", "12:00", "/teach", $"tslot-{slot}-{Iso(sun)}", 900), Assert.Single(await Due(w, At(sun, 11, 44, 30), At(sun, 11, 45, 10))).Note);
        Assert.Empty(await Due(w, At(sun, 13, 44, 30), At(sun, 13, 45, 10)));

        // Cancelled: no reminder.
        await w.Teacher1.Made(Cancel(s.Be, sun));
        Assert.Empty(await Due(w, At(sun, 7, 44, 30), At(sun, 7, 45, 10)));

        // Moved to a room to be announced: still one class, reminded at its new time only.
        await w.Teacher1.Made(Move(s.Be, next, "08:00", w.Date(1, 3), "10:00", room: ""));
        Assert.Empty(await Due(w, At(next, 7, 44, 30), At(next, 7, 45, 10)));
        var moved = Assert.Single(await Due(w, At(w.Date(1, 3), 9, 44, 30), At(w.Date(1, 3), 9, 45, 10))).Note;
        Assert.Equal(("BE1101 · IBSM1101 · LOG1101 in 15 min", "Business Economics · 10:00 · room to be announced", $"/teach?session=s{s.Be}.{Iso(w.Date(1, 3))}.10:00"),
            (moved.Title, moved.Body, moved.Url));

        // Reminders off: none.
        await Settings(w.Teacher1, "/teach", null);
        Assert.Empty(await Due(w, At(w.Date(1, 3), 9, 44, 30), At(w.Date(1, 3), 9, 45, 10)));
    }

    [Fact]
    public async Task A_teacher_and_a_student_with_the_same_id_each_get_only_their_own()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        // The mix-up this guards against: teacher 1's reminder going to student 1's phone.
        Assert.Equal(w.Teacher1Id, w.StudentRowIds[0]);
        var sun = w.Date(0, 0);
        await On(w.Students[0], "/me", "s0");
        await Settings(w.Students[0], "/me", 15);
        await On(w.Teacher1, "/teach", "t1");
        await Settings(w.Teacher1, "/teach", 15);
        Assert.Equal(1, (await Json(await w.Teacher1.GetAsync("/api/teach/notifications"))).GetProperty("devices").GetInt32());
        Assert.Equal(1, (await Json(await w.Students[0].GetAsync("/api/me/notifications"))).GetProperty("devices").GetInt32());

        // The test push.
        Assert.Equal(1, (await Json(await w.Teacher1.PostAsync("/api/teach/push/test", null))).GetProperty("sent").GetInt32());
        Assert.Equal(["t1"], Pushed(w));
        Assert.Equal(1, (await Json(await w.Students[0].PostAsync("/api/me/push/test", null))).GetProperty("sent").GetInt32());
        Assert.Equal(["t1", "s0"], Pushed(w));
        Assert.Equal(HttpStatusCode.Conflict, (await w.Teacher2.PostAsync("/api/teach/push/test", null)).StatusCode);

        // A notice to teacher 1: Teacher 2 cancels next week's class for the code student 1 (no device) picked.
        await w.Teacher2.AddSection(s.Ibsm);
        await w.Teacher2.Made(Cancel(s.Ibsm, w.Date(1, 0), sectionIds: [s.Ibsm]));
        var worker = Worker(w);
        var last = await worker.RoundAsync(w.Time.GetUtcNow().AddSeconds(-30), w.Time.GetUtcNow(), default);
        Assert.Equal(["t1", "s0", "t1"], Pushed(w));

        // Both have the Sunday 08:00 class and want to know 15 minutes before: one reminder each, on their own device.
        w.Time.Set(At(sun, 7, 44, 30));
        last = await worker.RoundAsync(last, w.Time.GetUtcNow(), default);
        Assert.Equal(3, Pushed(w).Count);
        w.Time.Set(At(sun, 7, 45, 10));
        last = await worker.RoundAsync(last, w.Time.GetUtcNow(), default);
        Assert.Equal(["s0", "t1"], Pushed(w).Skip(3).Order());
        w.Time.Set(At(sun, 7, 45, 40));
        await worker.RoundAsync(last, w.Time.GetUtcNow(), default);
        Assert.Equal(5, Pushed(w).Count);
    }

    [Fact]
    public async Task A_teachers_reminder_is_not_sent_again_when_a_round_is_repeated_or_the_worker_restarts()
    {
        using var w = await TeachWorld.Create();
        var sun = w.Date(0, 0);
        await On(w.Teacher1, "/teach", "t1");
        await Settings(w.Teacher1, "/teach", 15);

        w.Time.Set(At(sun, 7, 45, 10));
        var worker = Worker(w);
        await worker.RoundAsync(At(sun, 7, 44, 30), w.Time.GetUtcNow(), default);
        Assert.Equal(["t1"], Pushed(w));

        // The same window again (the students' part failed and is repeated), by this worker and by a second process.
        await worker.RoundAsync(At(sun, 7, 44, 30), w.Time.GetUtcNow(), default);
        await Task.WhenAll(Worker(w).RoundAsync(At(sun, 7, 44, 30), w.Time.GetUtcNow(), default), Worker(w).RoundAsync(At(sun, 7, 44, 30), w.Time.GetUtcNow(), default));
        Assert.Equal(["t1"], Pushed(w));

        // A restart picks up where the students' reminders stopped; the teachers' own mark is further on.
        w.Time.Set(At(sun, 7, 45, 40));
        var restarted = Worker(w);
        await restarted.RoundAsync(At(sun, 7, 40), w.Time.GetUtcNow(), default);
        Assert.Equal(["t1"], Pushed(w));

        // After a long outage a teacher's reminder is caught up on like a student's, while the class hasn't started.
        var next = w.Date(1, 0);
        w.Time.Set(At(next, 7, 50));
        await Worker(w).RoundAsync(await Worker(w).RemindersFromAsync(w.Time.GetUtcNow(), default), w.Time.GetUtcNow(), default);
        Assert.Equal(["t1", "t1"], Pushed(w));
    }

    [Fact]
    public async Task A_failure_in_the_teacher_parts_never_stops_or_repeats_student_reminders()
    {
        using var w = await TeachWorld.Create();
        var sun = w.Date(0, 0);
        await On(w.Students[0], "/me", "s0");
        await Settings(w.Students[0], "/me", 15);
        await Settings(w.Teacher1, "/teach", 15);
        // The teachers' devices can't be read at all: both teacher parts of the round fail.
        await w.Db(db => db.Database.ExecuteSqlRawAsync("ALTER TABLE \"TeacherPushSubscriptions\" RENAME TO \"Broken\""));
        await w.Db(async db =>
        {
            db.TeachNotices.Add(new TeachNotice { Audience = TeachNotice.ToTeacher, TeacherId = w.Teacher1Id, Kind = "undone", Title = "X", Body = "", Url = "/teach", Tag = "t", CreatedAt = w.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        });

        w.Time.Set(At(sun, 7, 45, 10));
        var worker = Worker(w);
        var last = await worker.RoundAsync(At(sun, 7, 44, 30), w.Time.GetUtcNow(), default);
        Assert.Equal(w.Time.GetUtcNow(), last);
        Assert.Equal(["s0"], Pushed(w));
        w.Time.Set(At(sun, 7, 45, 40));
        Assert.Equal(w.Time.GetUtcNow(), await worker.RoundAsync(last, w.Time.GetUtcNow(), default));
        Assert.Equal(["s0"], Pushed(w));
    }

    [Fact]
    public async Task A_teaching_assistants_devices_are_their_student_devices()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var profile = await w.Services.AddTeachingStudent(w.StudentRowIds[2], "Tara Assistant");
        var assistant = w.Students[2];
        await assistant.AddSection(s.Cs1);

        // Turned on from the teacher side, the device is still kept under the student.
        await On(assistant, "/teach", "ta");
        Assert.Equal((1, 0), await w.Db(async db => (await db.PushSubscriptions.CountAsync(p => p.StudentId == w.StudentRowIds[2]), await db.TeacherPushSubscriptions.CountAsync())));
        await Settings(assistant, "/teach", 15);
        var settings = await Json(await assistant.GetAsync("/api/teach/notifications"));
        Assert.Equal((15, 1), (settings.GetProperty("remindMinutes").GetInt32(), settings.GetProperty("devices").GetInt32()));
        // Their own student settings are another matter.
        Assert.Equal(JsonValueKind.Null, (await Json(await assistant.GetAsync("/api/me/notifications"))).GetProperty("remindMinutes").ValueKind);

        // A notice (the named teacher cancels next week's class) and the test push reach that device.
        await w.Teacher2.Made(Cancel(s.Cs1, w.Date(1, 1)));
        Assert.Equal(profile.Id, Assert.Single(await w.Notices(TeachNotice.ToTeacher)).TeacherId);
        await Send(w);
        Assert.Equal(["ta"], Pushed(w));
        Assert.Equal(1, (await Json(await assistant.PostAsync("/api/teach/push/test", null))).GetProperty("sent").GetInt32());

        // So does the reminder for the class they added: Monday 08:00.
        var mon = w.Date(0, 1);
        Assert.Equal(profile.Id, Assert.Single(await Due(w, At(mon, 7, 44, 30), At(mon, 7, 45, 10))).Teacher.Id);
        w.Time.Set(At(mon, 7, 45, 10));
        await Worker(w).RoundAsync(At(mon, 7, 44, 30), w.Time.GetUtcNow(), default);
        Assert.Equal(["ta", "ta", "ta"], Pushed(w));

        // Turning it off from the teacher side removes their own row only.
        await On(w.Students[0], "/me", "s0");
        Assert.Equal(HttpStatusCode.NoContent, (await assistant.PostAsJsonAsync("/api/teach/push/remove", new { endpoint = Address("s0") })).StatusCode);
        Assert.Equal(2, await w.Db(db => db.PushSubscriptions.CountAsync()));
        Assert.Equal(HttpStatusCode.NoContent, (await assistant.PostAsJsonAsync("/api/teach/push/remove", new { endpoint = Address("ta") })).StatusCode);
        Assert.Equal("s0", await w.Db(async db => (await db.PushSubscriptions.SingleAsync()).Endpoint[^2..]));
    }

    [Fact]
    public async Task A_device_belongs_to_whoever_turned_it_on_last()
    {
        using var w = await TeachWorld.Create();
        var device = Device("shared");
        Task<(int Student, int Teacher)> Rows() => w.Db(async db => (await db.PushSubscriptions.CountAsync(), await db.TeacherPushSubscriptions.CountAsync()));

        Assert.Equal(HttpStatusCode.NoContent, (await w.Students[0].PostAsJsonAsync("/api/me/push", device)).StatusCode);
        Assert.Equal((1, 0), await Rows());
        // The teacher signs in on that browser and turns notifications on: the student stops getting theirs there.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/teach/push", device)).StatusCode);
        Assert.Equal((0, 1), await Rows());
        // Sent again (the app does on every start): still one row.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/teach/push", device)).StatusCode);
        Assert.Equal((0, 1), await Rows());
        // Another teacher on that browser takes it over.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher2.PostAsJsonAsync("/api/teach/push", device)).StatusCode);
        Assert.Equal(w.Teacher2Id, await w.Db(db => db.TeacherPushSubscriptions.Select(p => p.TeacherId).SingleAsync()));
        // And back to the student.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Students[0].PostAsJsonAsync("/api/me/push", device)).StatusCode);
        Assert.Equal((1, 0), await Rows());

        // The same checks as for a student's device, and the same settings rules.
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Teacher1.PostAsJsonAsync("/api/teach/push", new { endpoint = "https://evil.example/x", keys = new { p256dh = "k", auth = "a" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Teacher1.PutAsJsonAsync("/api/teach/notifications", new { remindMinutes = 7, changes = true })).StatusCode);
        await Settings(w.Teacher1, "/teach", 30, changes: false);
        var settings = await Json(await w.Teacher1.GetAsync("/api/teach/notifications"));
        Assert.Equal(["remindMinutes", "changes", "devices"], settings.EnumerateObject().Select(p => p.Name));
        Assert.Equal((30, false, 0), (settings.GetProperty("remindMinutes").GetInt32(), settings.GetProperty("changes").GetBoolean(), settings.GetProperty("devices").GetInt32()));
    }

    [Fact]
    public async Task The_teachers_calendar_link_works_until_it_is_reset_or_the_account_is_removed()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var anyone = w.Browser();
        async Task<(HttpStatusCode Status, string Text)> Get(string url)
        {
            var r = await anyone.GetAsync(new Uri(url).PathAndQuery);
            return (r.StatusCode, await r.Content.ReadAsStringAsync());
        }
        static int Count(string text, string part) => text.Split(part).Length - 1;

        await w.Teacher1.PostAsJsonAsync("/api/teach/slots", new { title = "Dentist", day = 0, start = "12:00", end = "13:00", date = Iso(w.Date(0, 0)) });
        await Json(await w.Teacher1.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-204", note = "", hours = new[] { new { day = 1, start = "14:00", end = "15:00", room = "B-204" }, new { day = 1, start = "16:00", end = "17:00", room = "B-204" } } }));
        var links = await Json(await w.Teacher1.GetAsync("/api/teach/calendar"));
        var (url, textUrl) = (links.GetProperty("url").GetString()!, links.GetProperty("textUrl").GetString()!);
        Assert.Equal(url, (await Json(await w.Teacher1.GetAsync("/api/teach/calendar"))).GetProperty("url").GetString());

        // One entry per real class (three codes, two weeks: two entries), office hours and the own slot.
        var (status, ics) = await Get(url);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("X-WR-CALNAME:Classy Teaching", ics);
        Assert.Equal(2, Count(ics, "SUMMARY:Business Economics"));
        Assert.Contains("BE1101 · IBSM1101 · LOG1101", ics.Replace("\r\n ", ""));
        Assert.Equal(1, Count(ics, "SUMMARY:Dentist"));
        Assert.Contains("UID:tslot-", ics);
        Assert.True(Count(ics, "SUMMARY:Office hours") >= 2);
        var uids = ics.Replace("\r\n ", "").Split("\r\n").Where(l => l.StartsWith("UID:")).ToList();
        Assert.Equal(uids.Count, uids.Distinct().Count());

        // A cancelled class stays, said to be cancelled.
        await w.Teacher1.Made(Cancel(s.Be, w.Date(0, 0)));
        (_, ics) = await Get(url);
        Assert.Equal(1, Count(ics, "SUMMARY:Cancelled: Business Economics"));
        Assert.DoesNotContain("STATUS:CANCELLED", ics);
        var (_, text) = await Get($"{textUrl}?from={Iso(w.Date(0, 0))}&days=3");
        Assert.Contains($"CANCELLED by {TeachWorld.Teacher1Name}: BE1101 · IBSM1101 · LOG1101 Business Economics", text);
        Assert.Contains("Office hours · Room B-204 · not a class", text);
        Assert.DoesNotContain("The student", text);

        // A teacher with no classes yet.
        var third = await w.SignInTeacher("third.person@gutech.edu.om");
        var (_, empty) = await Get((await Json(await third.GetAsync("/api/teach/calendar"))).GetProperty("textUrl").GetString()!);
        Assert.Contains("No classes on this timetable yet.", empty);

        // A student's link is still the student's calendar.
        var (_, students) = await Get((await Json(await w.Students[0].GetAsync("/api/me/calendar"))).GetProperty("url").GetString()!);
        Assert.Contains("X-WR-CALNAME:Classy Schedule", students);

        // Reset: the old link is dead, the new one works.
        var fresh = (await Json(await w.Teacher1.PostAsync("/api/teach/calendar/reset", null))).GetProperty("url").GetString()!;
        Assert.NotEqual(url, fresh);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(url)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(textUrl)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get(fresh)).Status);

        // The account is removed: the link dies with it.
        await w.Db(db => db.Teachers.Where(t => t.Id == w.Teacher1Id).ExecuteDeleteAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Get(fresh)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(fresh.Replace(".ics", ".txt"))).Status);
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task A_claim_made_late_in_a_slow_round_is_stamped_with_the_time_it_was_made()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        await On(w.Students[2], "/me", "s2");
        await w.Teacher2.Made(Cancel(s.Cs2, w.Date(0, 2)));
        var id = Assert.Single(await w.Notices(TeachNotice.Students)).Id;

        // The worker's round started three minutes ago and only now gets to the notices.
        var started = w.Time.GetUtcNow();
        w.Time.Advance(TimeSpan.FromMinutes(3));
        Task<int> Late() => w.Db(db => TeachNotices.RoundAsync(db, w.Services.GetRequiredService<PushSender>(), started, default, w.Time));
        Assert.Equal(1, await Late());
        Assert.Equal(w.Time.GetUtcNow(), Assert.Single(await w.Notices(TeachNotice.Students)).ClaimedAt);

        // Still being sent (not marked sent yet): another slow round, of another process, leaves the fresh claim alone.
        await w.Db(db => db.TeachNotices.Where(n => n.Id == id).ExecuteUpdateAsync(u => u.SetProperty(n => n.SentAt, (DateTimeOffset?)null)));
        Assert.Equal(0, await Late());
        Assert.Equal(["s2"], Pushed(w));
    }

    /// <summary>A push service where the device "dead" has unsubscribed, and its owner removes it at that very moment.</summary>
    sealed class DeadDevice(TeachWorld w) : HttpMessageHandler, IHttpClientFactory
    {
        public readonly List<string> Sent = [];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var address = request.RequestUri!.ToString();
            if (!address.EndsWith("/dead", StringComparison.Ordinal))
            {
                lock (Sent) Sent.Add(address[(address.LastIndexOf('/') + 1)..]);
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            await w.Db(db => db.PushSubscriptions.Where(x => x.Endpoint == address).ExecuteDeleteAsync());
            return new HttpResponseMessage(HttpStatusCode.Gone);
        }
    }

    [Fact]
    public async Task A_dead_device_its_owner_removed_at_the_same_moment_does_not_send_the_notice_again()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        await On(w.Students[2], "/me", "dead");
        await On(w.Students[2], "/me", "s2");
        await w.Teacher2.Made(Cancel(s.Cs2, w.Date(0, 2)));
        var pushes = new DeadDevice(w);
        var sender = new PushSender(pushes, w.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), NullLogger<PushSender>.Instance);
        Task<int> Round() => w.Db(db => TeachNotices.RoundAsync(db, sender, w.Time.GetUtcNow(), default));

        Assert.Equal(1, await Round());
        Assert.NotNull(Assert.Single(await w.Notices(TeachNotice.Students)).SentAt);
        // Not again once the claim has run out.
        w.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(0, await Round());
        Assert.Equal(["s2"], pushes.Sent);
        Assert.Equal(1, await w.Db(db => db.PushSubscriptions.CountAsync()));
    }

    [Fact]
    public async Task Working_out_teachers_reminders_leaves_the_semester_untracked()
    {
        using var w = await TeachWorld.Create();
        var sun = w.Date(0, 0);
        await On(w.Teacher1, "/teach", "t1");
        await Settings(w.Teacher1, "/teach", 15);
        await w.Db(async db =>
        {
            Assert.NotEmpty(await TeachReminders.DueAsync(db, At(sun, 7, 44, 30), At(sun, 7, 45, 10), default));
            Assert.Empty(db.ChangeTracker.Entries<Section>());
            Assert.Empty(db.ChangeTracker.Entries<Course>());
            Assert.Empty(db.ChangeTracker.Entries<Meeting>());
        });
    }
}
