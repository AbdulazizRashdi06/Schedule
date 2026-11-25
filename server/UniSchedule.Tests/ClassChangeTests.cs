using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.ChangeKit;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// A teacher's changes to single sessions: cancel, move, another room, an extra class. What is stored, what is
/// refused, what only warns, who may undo or edit and until when, and what students and other teachers are told.
/// </summary>
public class ClassChangeTests
{
    static object Cancel(int section, DateOnly date, string start = "08:00", int[]? sectionIds = null, string? note = null, bool alsoOther = false) =>
        new { kind = "cancel", sectionId = section, date = Iso(date), start, sectionIds, note, alsoOther };

    static object Move(int section, DateOnly date, string start, DateOnly to, string newStart, string? newEnd = null, string? room = null,
        int[]? sectionIds = null, string? note = null, bool alsoOther = false) =>
        new { kind = "move", sectionId = section, date = Iso(date), start, sectionIds, newDate = Iso(to), newStart, newEnd, room, note, alsoOther };

    static object Room(int section, DateOnly date, string start, string? room, int[]? sectionIds = null, string? note = null) =>
        new { kind = "room", sectionId = section, date = Iso(date), start, sectionIds, room, note };

    static object Extra(int[]? sectionIds, DateOnly to, string newStart, string newEnd, string? room = null, string? type = "lecture", string? note = null) =>
        new { kind = "extra", sectionIds, newDate = Iso(to), newStart, newEnd, room, type, note };

    static async Task Refused(Task<HttpResponseMessage> request, HttpStatusCode status, string? error = null)
    {
        var r = await request;
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"{(int)r.StatusCode}, not {(int)status}: {text}");
        if (error is not null) Assert.Equal(error, JsonDocument.Parse(text).RootElement.GetProperty("error").GetString());
    }

    static string Day(DateOnly d) => d.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
    static List<string> Names(JsonElement o) => o.EnumerateObject().Select(p => p.Name).ToList();
    static List<string> Warnings(JsonElement preview) => preview.GetProperty("warnings").EnumerateArray().Select(x => x.GetProperty("code").GetString()!).ToList();
    static string Warning(JsonElement preview, string code) =>
        preview.GetProperty("warnings").EnumerateArray().Single(x => x.GetProperty("code").GetString() == code).GetProperty("text").GetString()!;
    static async Task<List<JsonElement>> List(HttpClient c) => (await Json(await c.GetAsync("/api/teach/changes"))).GetProperty("changes").EnumerateArray().ToList();

    sealed record Ids(int Be, int Ibsm, int Log, int Be2, int Cs1, int Cs2);

    static async Task<Ids> Sections(TeachWorld w) => new(await w.Section("BE1101", "1"), await w.Section("IBSM1101", "1"), await w.Section("LOG1101", "1"),
        await w.Section("BE1101", "2"), await w.Section("CS2201", "1"), await w.Section("CS2201", "2"));

    [Fact]
    public async Task Cancel_move_room_and_extra_are_stored_and_written_as_notices()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (sun, mon, tue, wed, thu) = (w.Date(0, 0), w.Date(0, 1), w.Date(0, 2), w.Date(0, 3), w.Date(0, 4));
        var now = w.Time.GetUtcNow();

        // Cancel: one tap covers the three codes of the one real class.
        var cancelled = await Json(await w.Teacher1.Change(Cancel(s.Be, sun, note: "  Guest\u0007 lecture  ")));
        var items = cancelled.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(["BE1101", "IBSM1101", "LOG1101"], items.Select(c => c.GetProperty("code").GetString()));
        Assert.Equal(["id", "group", "kind", "courseId", "sectionId", "code", "type", "from", "to", "note", "by", "at", "canUndo", "canEdit", "mine", "flagged"], Names(items[0]));
        Assert.Equal(("cancel", s.Be, "lecture", "Guest lecture", TeachWorld.Teacher1Name),
            (items[0].GetProperty("kind").GetString(), items[0].GetProperty("sectionId").GetInt32(), items[0].GetProperty("type").GetString(),
                items[0].GetProperty("note").GetString(), items[0].GetProperty("by").GetString()));
        Assert.Equal((Iso(sun), "08:00", "09:30", "A101"), (items[0].GetProperty("from").GetProperty("date").GetString(), items[0].GetProperty("from").GetProperty("start").GetString(),
            items[0].GetProperty("from").GetProperty("end").GetString(), items[0].GetProperty("from").GetProperty("room").GetString()));
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("to").ValueKind);
        Assert.All(items, c => Assert.True(c.GetProperty("canUndo").GetBoolean() && c.GetProperty("canEdit").GetBoolean() && c.GetProperty("mine").GetBoolean() && !c.GetProperty("flagged").GetBoolean()));

        var rows = await w.Rows();
        Assert.Equal(3, rows.Count);
        Assert.Single(rows.Select(r => r.GroupId).Distinct());
        Assert.Equal(Group(cancelled), rows[0].GroupId);
        var be = rows.Single(r => r.SectionId == s.Be);
        Assert.Equal((w.Semester, "BE1101", "Business Economics", "1", "cancel", "lecture"), (be.SemesterId, be.CourseCode, be.CourseName, be.SectionNumber, be.Kind, be.SessionType));
        Assert.Equal((sun, new TimeOnly(8, 0), new TimeOnly(9, 30), "A101"), (be.OrigDate!.Value, be.OrigStart!.Value, be.OrigEnd!.Value, be.OrigRoom));
        Assert.True(be is { NewDate: null, NewStart: null, NewEnd: null, NewRoom: null, EndedAt: null, OnBehalfOf: null, Flagged: false });
        Assert.Equal($"tchange-{s.Be}-{sun:yyyyMMdd}-0800", be.Tag);
        Assert.Equal((w.Teacher1Id, "teacher", w.Teacher1Id, TeachWorld.Teacher1Email, TeachWorld.Teacher1Name, TeachWorld.Teacher1Name),
            (be.TeacherId!.Value, be.ByKind, be.ById, be.ByLabel, be.ByName, be.NamedTeacher));
        // Stored in UTC, like every time that is compared in a query.
        Assert.Equal((now, TimeSpan.Zero), (be.CreatedAt, be.CreatedAt.Offset));

        // One notice per section, written with the change and not sent yet.
        var told = await w.Notices(TeachNotice.Students);
        Assert.Equal(3, told.Count);
        var toBe = told.Single(n => n.SectionId == s.Be);
        Assert.Equal(("change", "BE1101 cancelled", $"Business Economics · {Day(sun)} 08:00 · Cancelled by {TeachWorld.Teacher1Name}: Guest lecture"), (toBe.Kind, toBe.Title, toBe.Body));
        Assert.Equal(($"/?session={await w.CourseOf(s.Be)}.{Iso(sun)}.08:00", be.Tag, be.GroupId), (toBe.Url, toBe.Tag, toBe.GroupId));
        Assert.Equal((OmanTime(sun, 8).ToUniversalTime(), TimeSpan.Zero), (toBe.ExpiresAt!.Value, toBe.ExpiresAt.Value.Offset));
        Assert.True(toBe is { SentAt: null, ClaimedAt: null, TeacherId: null });
        // Nobody else teaches these three sections, and the maker isn't told about their own change.
        Assert.Empty(await w.Notices(TeachNotice.ToTeacher));

        // Move: the usual length is kept when no end is sent.
        var moved = await Json(await w.Teacher2.Change(Move(s.Cs1, mon, "08:00", wed, "10:00", room: " C 3 ")));
        var to = Assert.Single(moved.GetProperty("changes").EnumerateArray()).GetProperty("to");
        Assert.Equal((Iso(wed), "10:00", "11:30", "C 3"), (to.GetProperty("date").GetString(), to.GetProperty("start").GetString(), to.GetProperty("end").GetString(), to.GetProperty("room").GetString()));
        var move = (await w.Rows()).Single(r => r.Kind == "move");
        Assert.Equal((mon, new TimeOnly(8, 0), wed, new TimeOnly(10, 0), new TimeOnly(11, 30), "C 3"), (move.OrigDate!.Value, move.OrigStart!.Value, move.NewDate!.Value, move.NewStart!.Value, move.NewEnd!.Value, move.NewRoom));
        var toCs = (await w.Notices(TeachNotice.Students)).Single(n => n.GroupId == move.GroupId);
        Assert.Equal(("CS2201 moved", $"{Day(mon)} 08:00 → {Day(wed)} 10:00, room C 3 · Moved by {TeachWorld.Teacher2Name}"), (toCs.Title, toCs.Body));
        // It opens the class where it is now, and is worth sending until the later of the two times.
        Assert.Equal(($"/?session={await w.CourseOf(s.Cs1)}.{Iso(wed)}.10:00", OmanTime(wed, 10).ToUniversalTime()), (toCs.Url, toCs.ExpiresAt!.Value));

        // Room: no room given means "to be announced". The time stays the university's.
        await Json(await w.Teacher2.Change(Room(s.Cs2, tue, "08:00", null)));
        var room = (await w.Rows()).Single(r => r.Kind == "room");
        Assert.Equal((tue, new TimeOnly(8, 0), new TimeOnly(9, 30), "", "B105"), (room.NewDate!.Value, room.NewStart!.Value, room.NewEnd!.Value, room.NewRoom, room.OrigRoom));
        var toRoom = (await w.Notices(TeachNotice.Students)).Single(n => n.GroupId == room.GroupId);
        Assert.Equal(("CS2201 room changed", $"{Day(tue)} 08:00 · now in a room to be announced (was B105) · {TeachWorld.Teacher2Name}"), (toRoom.Title, toRoom.Body));

        // Extra: for the sections ticked, of the type chosen.
        var added = await Json(await w.Teacher2.Change(Extra([s.Cs1, s.Cs2], thu, "15:00", "16:30", "B105", "lab", "Revision")));
        Assert.Equal(2, added.GetProperty("changes").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, added.GetProperty("changes")[0].GetProperty("from").ValueKind);
        var extras = (await w.Rows()).Where(r => r.Kind == "extra").ToList();
        Assert.Equal(2, extras.Count);
        Assert.All(extras, r => Assert.True(r is { OrigDate: null, OrigStart: null, OrigEnd: null, OrigRoom: null, SessionType: "lab", NewRoom: "B105", Note: "Revision" }));
        Assert.Equal($"tchange-{s.Cs1}-x{thu:yyyyMMdd}-1500", extras.Single(r => r.SectionId == s.Cs1).Tag);
        var toExtra = (await w.Notices(TeachNotice.Students)).Single(n => n.GroupId == extras[0].GroupId && n.SectionId == s.Cs1);
        Assert.Equal(("CS2201 extra class", $"{Day(thu)} 15:00–16:30, room B105 · Lab · Added by {TeachWorld.Teacher2Name}: Revision", $"/?session={await w.CourseOf(s.Cs1)}.{Iso(thu)}.15:00"),
            (toExtra.Title, toExtra.Body, toExtra.Url));

        // Each teacher's list: their own, in the order the classes come.
        var mine = await List(w.Teacher2);
        Assert.Equal(["room", "move", "extra", "extra"], mine.Select(c => c.GetProperty("kind").GetString()));
        Assert.All(mine, c => Assert.True(c.GetProperty("canUndo").GetBoolean() && c.GetProperty("canEdit").GetBoolean() && c.GetProperty("mine").GetBoolean()));
        Assert.Equal(["cancel", "cancel", "cancel"], (await List(w.Teacher1)).Select(c => c.GetProperty("kind").GetString()));
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task Everything_the_rules_refuse_is_refused()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (sun, next) = (w.Date(0, 0), w.Date(1, 0));
        var t1 = w.Teacher1;

        // 5: the kind, the note, the room, the type of an extra class.
        await Refused(t1.Change(new { kind = "postpone", sectionId = s.Be, date = Iso(sun), start = "08:00" }), HttpStatusCode.BadRequest);
        await Refused(t1.Change(Cancel(s.Be, sun, note: new string('n', 201))), HttpStatusCode.BadRequest);
        await Refused(t1.Change(Room(s.Be, sun, "08:00", new string('r', 61))), HttpStatusCode.BadRequest);
        await Refused(t1.Change(Extra([s.Be], w.Date(0, 3), "10:00", "11:00", type: "seminar")), HttpStatusCode.BadRequest);
        await Refused(t1.Change(Extra([s.Be], w.Date(0, 3), "10:00", "11:00", type: null)), HttpStatusCode.BadRequest);
        // A note of exactly 200 and a room of exactly 60 are fine (seen in the preview, which saves nothing).
        await Json(await t1.Preview(Room(s.Be, sun, "08:00", new string('r', 60), note: new string('n', 200))));

        // 6: a class that isn't on their timetable, named as the session or among the codes.
        await Refused(t1.Change(Cancel(s.Cs1, w.Date(0, 1))), HttpStatusCode.Forbidden, ChangeRules.NotYours);
        await Refused(t1.Change(Cancel(s.Be, sun, sectionIds: [s.Be, s.Cs1])), HttpStatusCode.Forbidden, ChangeRules.NotYours);
        await Refused(t1.Change(Extra([s.Be, s.Cs1], w.Date(0, 3), "10:00", "11:00")), HttpStatusCode.Forbidden, ChangeRules.NotYours);

        // 7: no session then.
        await Refused(t1.Change(Cancel(s.Be, w.Date(0, 1))), HttpStatusCode.BadRequest, ChangeRules.NoSession);
        await Refused(t1.Change(Cancel(s.Be, sun, "09:00")), HttpStatusCode.BadRequest, ChangeRules.NoSession);
        await Refused(t1.Change(new { kind = "cancel", sectionId = s.Be, start = "08:00" }), HttpStatusCode.BadRequest, ChangeRules.NoSession);
        await Refused(t1.Change(new { kind = "cancel", date = Iso(sun), start = "08:00" }), HttpStatusCode.BadRequest, ChangeRules.NoSession);
        // A weekly class meets on its weekday inside the teaching weeks, and not on a day off.
        await w.AddMeeting(s.Be, null, 3, "10:00", "11:00", "A105");
        await Json(await w.Owner.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/days-off", new { date = Iso(w.Date(1, 3)), name = "National Day" }));
        await Json(await t1.Preview(Cancel(s.Be, w.Date(0, 3), "10:00")));
        await Refused(t1.Change(Cancel(s.Be, w.Date(1, 3), "10:00")), HttpStatusCode.BadRequest, ChangeRules.NoSession);
        await Refused(t1.Change(Cancel(s.Be, w.Date(8, 3), "10:00")), HttpStatusCode.BadRequest, ChangeRules.NoSession);
        await Refused(t1.Change(Cancel(s.Be, w.Date(0, 4), "10:00")), HttpStatusCode.BadRequest, ChangeRules.NoSession);

        // 8: a code that is theirs but isn't this real class.
        await t1.AddSection(s.Be2);
        await Refused(t1.Change(Cancel(s.Be, sun, sectionIds: [s.Be, s.Be2])), HttpStatusCode.BadRequest);
        await Refused(t1.Change(Cancel(s.Be, sun, sectionIds: [])), HttpStatusCode.BadRequest);

        // 11: a move's new date and time.
        object To(string? date, string? start, string? end) => new { kind = "move", sectionId = s.Be, date = Iso(next), start = "08:00", newDate = date, newStart = start, newEnd = end };
        await Refused(t1.Change(To("soon", "10:00", null)), HttpStatusCode.BadRequest);
        await Refused(t1.Change(To(null, null, null)), HttpStatusCode.BadRequest);
        await Refused(t1.Change(To(Iso(w.Date(1, 2)), "25:00", null)), HttpStatusCode.BadRequest);
        await Refused(t1.Change(To(Iso(w.Date(1, 2)), "10:00", "10:00")), HttpStatusCode.BadRequest);
        await Refused(t1.Change(To(Iso(w.Date(1, 2)), "10:00", "09:00")), HttpStatusCode.BadRequest);
        await Refused(t1.Change(To(Iso(w.Date(1, 2)), "10:00", "10:14")), HttpStatusCode.BadRequest);
        await Refused(t1.Change(To(Iso(w.Date(1, 2)), "08:00", "18:01")), HttpStatusCode.BadRequest);
        await Refused(t1.Change(To(Iso(w.OmanToday.AddDays(-1)), "10:00", null)), HttpStatusCode.BadRequest);
        await Refused(t1.Change(To(Iso(w.OmanToday.AddDays(191)), "10:00", null)), HttpStatusCode.BadRequest);
        // 12 and 13: a move to where it already is, a room it is already in (however the room is spelled).
        await Refused(t1.Change(Move(s.Be, next, "08:00", next, "08:00", room: "B202")), HttpStatusCode.BadRequest, "Pick a different time, or use Change room.");
        await Refused(t1.Change(Room(s.Be, next, "08:00", "a 101")), HttpStatusCode.BadRequest, "That's the room it's already in.");
        // 14: an extra class for nobody, and a second one at the same time.
        await Refused(t1.Change(Extra([], w.Date(0, 4), "15:00", "16:00")), HttpStatusCode.BadRequest);
        await Refused(t1.Change(Extra(null, w.Date(0, 4), "15:00", "16:00")), HttpStatusCode.BadRequest);
        await Json(await t1.Change(Extra([s.Be], w.Date(0, 4), "15:00", "16:00")));
        await Refused(t1.Change(Extra([s.Be, s.Ibsm], w.Date(0, 4), "15:00", "17:00")), HttpStatusCode.Conflict);
        Assert.Single(await w.Rows());

        // 10: a second change on a session that has one, whichever of its codes is named.
        var group = await t1.Made(Cancel(s.Be, sun));
        foreach (var body in new[] { Cancel(s.Be, sun), Room(s.Ibsm, sun, "08:00", "B202"), Cancel(s.Log, sun, sectionIds: [s.Log]) })
        {
            var again = await t1.Change(body);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            var answer = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal((ChangeRules.AlreadyChanged, group), (answer.GetProperty("error").GetString(), answer.GetProperty("groupId").GetString()));
        }

        // 15: nothing live under that name.
        await Refused(t1.Undo(new string('0', 32)), HttpStatusCode.Conflict, ChangeRules.Gone);
        await Refused(t1.Replace(new string('0', 32), Cancel(s.Be, sun)), HttpStatusCode.Conflict, ChangeRules.Gone);
        await Refused(t1.Undo("nope"), HttpStatusCode.Conflict, ChangeRules.Gone);
        Assert.Equal(4, (await w.Rows()).Count);

        // 9: a class that has started. One minute before, it can still be changed.
        w.GoTo(next, 7, 55);
        t1 = await w.Fresh(TeachWorld.Teacher1Email);
        w.GoTo(next, 7, 59);
        Assert.Equal(JsonValueKind.Null, (await Json(await t1.Preview(Cancel(s.Be, next)))).GetProperty("blocked").ValueKind);
        w.GoTo(next, 8, 0);
        Assert.Equal("started", (await Json(await t1.Preview(Cancel(s.Be, next)))).GetProperty("blocked").GetString());
        await Refused(t1.Change(Cancel(s.Be, next)), HttpStatusCode.BadRequest, ChangeRules.Started);
        await Refused(t1.Change(Room(s.Be, next, "08:00", "B202")), HttpStatusCode.BadRequest, ChangeRules.Started);
        Assert.Equal(4, (await w.Rows()).Count);
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task With_no_semester_published_nothing_can_be_changed()
    {
        using var w = await TeachWorld.Create(seed: false);
        var teacher = await w.SignInTeacher("new.teacher@gutech.edu.om");
        await Refused(teacher.Change(Cancel(1, w.OmanToday.AddDays(3))), HttpStatusCode.Conflict);
        await Refused(teacher.Preview(Cancel(1, w.OmanToday.AddDays(3))), HttpStatusCode.Conflict);
        await Refused(teacher.Undo(new string('a', 32)), HttpStatusCode.Conflict);
        Assert.Empty((await Json(await teacher.GetAsync("/api/teach/changes"))).GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task A_change_reaches_the_ticked_codes_only_and_the_rest_can_follow()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var sun = w.Date(0, 0);

        var first = await Json(await w.Teacher1.Change(Cancel(s.Be, sun, sectionIds: [s.Be, s.Ibsm])));
        Assert.Equal([s.Be, s.Ibsm], first.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("sectionId").GetInt32()));
        Assert.Equal([s.Be, s.Ibsm], (await w.Live()).Select(r => r.SectionId!.Value));
        // The students of the third code still have their class.
        Assert.DoesNotContain(Sessions(await Json(await w.Students[0].GetAsync("/api/me/timetable")), sun, sun), x => x.Code == "BE1101");
        Assert.Contains(Sessions(await Json(await w.Students[2].GetAsync("/api/me/timetable")), sun, sun), x => x.Code == "LOG1101");

        // From the third code's block: it alone can be ticked, and the other two are listed for information.
        var preview = await Json(await w.Teacher1.Preview(Cancel(s.Log, sun)));
        var codes = preview.GetProperty("codes").EnumerateArray().ToList();
        Assert.Equal(["LOG1101", "BE1101", "IBSM1101"], codes.Select(c => c.GetProperty("code").GetString()));
        Assert.Equal(JsonValueKind.Null, codes[0].GetProperty("changedBy").ValueKind);
        Assert.All(codes.Skip(1), c => Assert.Equal(Group(first), c.GetProperty("changedBy").GetString()));
        Assert.Equal(1, preview.GetProperty("size").GetInt32());
        Assert.DoesNotContain("replaces", Warnings(preview));
        // A code that has its own change can't be ticked from here.
        await Refused(w.Teacher1.Change(Cancel(s.Log, sun, sectionIds: [s.Log, s.Be])), HttpStatusCode.BadRequest);

        // A preview of a session that has a change is a preview of replacing it.
        var replacing = await Json(await w.Teacher1.Preview(Room(s.Be, sun, "08:00", "B202")));
        Assert.Equal(["BE1101", "IBSM1101"], replacing.GetProperty("codes").EnumerateArray().Select(c => c.GetProperty("code").GetString()));
        Assert.Equal($"This replaces: Cancelled by {TeachWorld.Teacher1Name}.", Warning(replacing, "replaces"));

        // With the default ticks the third code is cancelled by itself, in a group of its own.
        var third = await Json(await w.Teacher1.Change(Cancel(s.Log, sun)));
        Assert.Equal(s.Log, Assert.Single(third.GetProperty("changes").EnumerateArray()).GetProperty("sectionId").GetInt32());
        Assert.NotEqual(Group(first), Group(third));
        Assert.Equal(3, (await w.Live()).Count);
        Assert.DoesNotContain(Sessions(await Json(await w.Students[2].GetAsync("/api/me/timetable")), sun, sun), x => x.Code == "LOG1101");
    }

    [Fact]
    public async Task Also_the_other_sessions_that_day_changes_every_one_of_them()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        foreach (var week in new[] { 0, 1 })
        {
            await w.AddMeeting(s.Cs1, w.Date(week, 1), 0, "10:00", "11:00", "B105");
            await w.AddMeeting(s.Cs1, w.Date(week, 1), 0, "12:00", "13:00", "B106");
        }
        var (mon, next) = (w.Date(0, 1), w.Date(1, 1));

        var others = (await Json(await w.Teacher2.Preview(Cancel(s.Cs1, mon)))).GetProperty("others").EnumerateArray().ToList();
        Assert.Equal([("10:00", "11:00", "B105"), ("12:00", "13:00", "B106")],
            others.Select(o => (o.GetProperty("start").GetString(), o.GetProperty("end").GetString(), o.GetProperty("room").GetString())));

        // Three sessions that day: three groups, all listed, each undone on its own.
        var cancelled = await Json(await w.Teacher2.Change(Cancel(s.Cs1, mon, alsoOther: true)));
        Assert.Equal(3, cancelled.GetProperty("changes").GetArrayLength());
        Assert.Equal(3, Groups(cancelled).Count);
        Assert.Equal([new TimeOnly(8, 0), new TimeOnly(10, 0), new TimeOnly(12, 0)], (await w.Live()).Select(r => r.OrigStart!.Value));
        Assert.Empty(Sessions(await Json(await w.Students[0].GetAsync("/api/me/timetable")), mon, mon));
        // Nothing is left to add to.
        Assert.Empty((await Json(await w.Teacher2.Preview(Cancel(s.Cs1, mon)))).GetProperty("others").EnumerateArray());
        foreach (var group in Groups(cancelled)) await Json(await w.Teacher2.Undo(group));
        Assert.Empty(await w.Live());

        // A move shifts the others by the same number of days and minutes; each keeps its own room.
        var moved = await Json(await w.Teacher2.Change(Move(s.Cs1, next, "08:00", w.Date(1, 2), "09:00", room: "C1", alsoOther: true)));
        Assert.Equal(3, Groups(moved).Count);
        Assert.Equal([("09:00", "10:30", "C1"), ("11:00", "12:00", "B105"), ("13:00", "14:00", "B106")],
            moved.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("to")).OrderBy(t => t.GetProperty("start").GetString())
                .Select(t => (t.GetProperty("start").GetString(), t.GetProperty("end").GetString(), t.GetProperty("room").GetString())));
        Assert.All(moved.GetProperty("changes").EnumerateArray(), c => Assert.Equal(Iso(w.Date(1, 2)), c.GetProperty("to").GetProperty("date").GetString()));
        Assert.Equal(3, Sessions(await Json(await w.Students[0].GetAsync("/api/me/timetable")), w.Date(1, 2), w.Date(1, 2)).Count);
        foreach (var group in Groups(moved)) await Json(await w.Teacher2.Undo(group));

        // A room change gives every session that day the same room.
        var rooms = await Json(await w.Teacher2.Change(new { kind = "room", sectionId = s.Cs1, date = Iso(next), start = "10:00", room = "C7", alsoOther = true }));
        Assert.Equal(3, Groups(rooms).Count);
        Assert.All(rooms.GetProperty("changes").EnumerateArray(), c => Assert.Equal("C7", c.GetProperty("to").GetProperty("room").GetString()));
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task Replacing_a_change_keeps_its_history_and_puts_an_unticked_code_back_on()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (sun, wed) = (w.Date(0, 0), w.Date(0, 3));
        var first = await w.Teacher1.Made(Cancel(s.Be, sun, note: "A"));

        // Edited before anything was sent: the cancel is never announced, the move is, once.
        w.Time.Advance(TimeSpan.FromMinutes(1));
        var second = Group(await Json(await w.Teacher1.Replace(first, new { kind = "move", sectionIds = new[] { s.Be, s.Ibsm }, newDate = Iso(wed), newStart = "10:00", room = "B202", note = "B" })));
        Assert.NotEqual(first, second);
        var rows = await w.Rows();
        Assert.Equal(5, rows.Count);
        var old = rows.Where(r => r.GroupId == first).ToList();
        Assert.Equal([("replaced", s.Be), ("replaced", s.Ibsm), ("undone", s.Log)], old.Select(r => (r.EndReason!, r.SectionId!.Value)));
        Assert.All(old, r => Assert.Equal(($"teacher:{TeachWorld.Teacher1Email}", w.Time.GetUtcNow()), (r.EndedBy, r.EndedAt!.Value)));
        var live = rows.Where(r => r.GroupId == second).ToList();
        Assert.Equal(2, live.Count);
        foreach (var r in live)
        {
            var before = old.Single(o => o.SectionId == r.SectionId);
            // The same session, so the same tag on students' phones.
            Assert.Equal((before.Tag, before.OrigDate, before.OrigStart, before.OrigEnd, before.OrigRoom, "move", "B", "lecture"), (r.Tag, r.OrigDate, r.OrigStart, r.OrigEnd, r.OrigRoom, r.Kind, r.Note, r.SessionType));
            Assert.True(r is { EndedAt: null, NewRoom: "B202" });
        }
        var told = await w.Notices(TeachNotice.Students);
        Assert.Equal([("BE1101 moved", s.Be), ("IBSM1101 moved", s.Ibsm)], told.Select(n => (n.Title, n.SectionId!.Value)));
        Assert.All(told, n => Assert.Equal(("change", second), (n.Kind, n.GroupId)));
        // The third code's students were never told anything, and its class is on.
        Assert.Contains(Sessions(await Json(await w.Students[2].GetAsync("/api/me/timetable")), sun, sun), x => x.Code == "LOG1101");

        // After it was sent: an unticked code is told its class is back on, the other gets the new details.
        await w.MarkSent();
        var third = Group(await Json(await w.Teacher1.Replace(second, new { kind = "cancel", sectionIds = new[] { s.Be } })));
        told = await w.Notices(TeachNotice.Students);
        var back = told.Single(n => n.Kind == "back-on");
        Assert.Equal((s.Ibsm, "IBSM1101 is back on", $"{Day(sun)} 08:00, room A101, as on the timetable.", $"tchange-{s.Ibsm}-{sun:yyyyMMdd}-0800"), (back.SectionId!.Value, back.Title, back.Body, back.Tag));
        Assert.Equal(($"/?session={await w.CourseOf(s.Ibsm)}.{Iso(sun)}.08:00", OmanTime(wed, 10).ToUniversalTime()), (back.Url, back.ExpiresAt!.Value));
        Assert.Equal(["BE1101 moved", "BE1101 cancelled"], told.Where(n => n.SectionId == s.Be).Select(n => n.Title));
        Assert.Equal(third, told.Last(n => n.SectionId == s.Be).GroupId);
        Assert.Null(told.Last(n => n.SectionId == s.Be).SentAt);
        Assert.Equal(["replaced", "replaced", "undone", "replaced", "undone", null], (await w.Rows()).Select(r => r.EndReason));

        // What was replaced is gone for good; an extra class and a session's change don't turn into each other.
        await Refused(w.Teacher1.Replace(first, new { kind = "cancel" }), HttpStatusCode.Conflict, ChangeRules.Gone);
        await Refused(w.Teacher1.Replace(second, new { kind = "cancel" }), HttpStatusCode.Conflict, ChangeRules.Gone);
        await Refused(w.Teacher1.Replace(third, new { kind = "extra", newDate = Iso(wed), newStart = "10:00", newEnd = "11:00", type = "lab" }), HttpStatusCode.BadRequest);
        await Refused(w.Teacher1.Replace(third, new { kind = "cancel", sectionIds = new[] { s.Ibsm } }), HttpStatusCode.BadRequest);
        await Refused(w.Teacher1.Replace(third, new { kind = "cancel", sectionIds = Array.Empty<int>() }), HttpStatusCode.BadRequest);

        // An extra class is edited the same way: another time, another type, fewer sections.
        var extra = await w.Teacher2.Made(Extra([s.Cs1, s.Cs2], w.Date(0, 4), "15:00", "16:00", "B105", "lab"));
        var edited = await Json(await w.Teacher2.Replace(extra, new { kind = "extra", sectionIds = new[] { s.Cs1 }, newDate = Iso(w.Date(0, 4)), newStart = "16:00", newEnd = "17:00", room = "B105", type = "tutorial" }));
        var item = Assert.Single(edited.GetProperty("changes").EnumerateArray());
        Assert.Equal(("tutorial", "16:00"), (item.GetProperty("type").GetString(), item.GetProperty("to").GetProperty("start").GetString()));
        var kept = (await w.Live()).Single(r => r.Kind == "extra");
        Assert.Equal($"tchange-{s.Cs1}-x{w.Date(0, 4):yyyyMMdd}-1500", kept.Tag);
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task Undo_ends_when_the_class_starts_and_for_a_move_at_the_earlier_of_the_two_starts()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (mon, tue, thu) = (w.Date(0, 1), w.Date(0, 2), w.Date(0, 4));
        var cancel = await w.Teacher2.Made(Cancel(s.Cs1, mon));
        var room = await w.Teacher2.Made(Room(s.Cs2, tue, "08:00", "B200"));
        // Next week's class, moved to this Thursday: the new time comes first.
        var move = await w.Teacher1.Made(Move(s.Be, w.Date(1, 0), "08:00", thu, "10:00"));
        var extra = await w.Teacher2.Made(Extra([s.Cs1], thu, "15:00", "16:00"));
        var late = await w.Teacher2.Made(Extra([s.Cs2], thu, "15:00", "16:00"));
        async Task<(bool Undo, bool Edit)> May(HttpClient c, string group)
        {
            var item = (await List(c)).First(x => x.GetProperty("group").GetString() == group);
            return (item.GetProperty("canUndo").GetBoolean(), item.GetProperty("canEdit").GetBoolean());
        }

        // A cancel: until the class starts, to the minute.
        w.GoTo(mon, 7, 58);
        var t2 = await w.Fresh(TeachWorld.Teacher2Email);
        Assert.Equal((true, true), await May(t2, cancel));
        w.GoTo(mon, 8, 0);
        Assert.Equal((false, false), await May(t2, cancel));
        await Refused(t2.Undo(cancel), HttpStatusCode.BadRequest, ChangeRules.Started);
        await Refused(t2.Replace(cancel, new { kind = "room", room = "B200" }), HttpStatusCode.BadRequest, ChangeRules.Started);

        // A room change: the same.
        w.GoTo(tue, 7, 59);
        await Json(await t2.Undo(room));
        Assert.Equal("undone", (await w.Rows()).Single(r => r.GroupId == room).EndReason);

        // A move: its new time has come, so it stays, though the old slot is days ahead.
        w.GoTo(thu, 9, 58);
        var t1 = await w.Fresh(TeachWorld.Teacher1Email);
        Assert.Equal((true, true), await May(t1, move));
        w.GoTo(thu, 10, 0);
        Assert.Equal((false, false), await May(t1, move));
        await Refused(t1.Undo(move), HttpStatusCode.BadRequest, ChangeRules.Started);
        await Refused(t1.Replace(move, new { kind = "cancel" }), HttpStatusCode.BadRequest, ChangeRules.Started);

        // An extra class: until it starts.
        w.GoTo(thu, 14, 58);
        t2 = await w.Fresh(TeachWorld.Teacher2Email);
        await Json(await t2.Undo(extra));
        w.GoTo(thu, 15, 0);
        await Refused(t2.Undo(late), HttpStatusCode.BadRequest, ChangeRules.Started);
        Assert.Equal((false, false), await May(t2, late));
        Assert.Equal(3, (await w.Live()).Select(r => r.GroupId).Distinct().Count());
    }

    [Fact]
    public async Task A_moved_class_can_still_be_moved_or_cancelled_after_its_old_time_but_not_undone()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (sun, thu) = (w.Date(0, 0), w.Date(0, 4));
        var move = await w.Teacher1.Made(Move(s.Be, sun, "08:00", thu, "10:00", room: "B202"));
        await w.MarkSent();

        w.GoTo(sun, 8, 0);
        var t1 = await w.Fresh(TeachWorld.Teacher1Email);
        var item = (await List(t1)).First();
        Assert.Equal((false, true), (item.GetProperty("canUndo").GetBoolean(), item.GetProperty("canEdit").GetBoolean()));
        await Refused(t1.Undo(move), HttpStatusCode.BadRequest, ChangeRules.Started);
        // Never back to a room change: that would put the class at a time that has gone. Nor back to its old time.
        await Refused(t1.Replace(move, new { kind = "room", room = "C9" }), HttpStatusCode.BadRequest);
        await Refused(t1.Replace(move, new { kind = "move", newDate = Iso(sun), newStart = "08:00" }), HttpStatusCode.BadRequest);
        await Refused(t1.Replace(move, new { kind = "move", newDate = Iso(sun), newStart = "07:00" }), HttpStatusCode.BadRequest);
        // A code can't be taken out any more: that would be an undo.
        await Refused(t1.Replace(move, new { kind = "move", sectionIds = new[] { s.Be }, newDate = Iso(thu), newStart = "12:00" }), HttpStatusCode.BadRequest);

        // Moved again, then "change room" (a move that keeps the time), then cancelled.
        var again = Group(await Json(await t1.Replace(move, new { kind = "move", newDate = Iso(thu), newStart = "12:00", room = "B202" })));
        var roomed = await Json(await t1.Replace(again, new { kind = "move", newDate = Iso(thu), newStart = "12:00", newEnd = "13:30", room = "C9" }));
        Assert.All(roomed.GetProperty("changes").EnumerateArray(), c => Assert.Equal(("12:00", "C9", false, true),
            (c.GetProperty("to").GetProperty("start").GetString(), c.GetProperty("to").GetProperty("room").GetString(), c.GetProperty("canUndo").GetBoolean(), c.GetProperty("canEdit").GetBoolean())));
        var cancelled = await Json(await t1.Replace(Group(roomed), new { kind = "cancel", note = "Sorry" }));
        // That cancel is final: its original start has passed.
        Assert.All(cancelled.GetProperty("changes").EnumerateArray(), c => Assert.Equal(("cancel", false, false),
            (c.GetProperty("kind").GetString(), c.GetProperty("canUndo").GetBoolean(), c.GetProperty("canEdit").GetBoolean())));
        await Refused(t1.Undo(Group(cancelled)), HttpStatusCode.BadRequest, ChangeRules.Started);
        await Refused(t1.Replace(Group(cancelled), new { kind = "cancel" }), HttpStatusCode.BadRequest, ChangeRules.Started);

        // Students are told about the moved session, and it is worth sending until that session's time.
        var notice = (await w.Notices(TeachNotice.Students)).Last(n => n.SectionId == s.Be);
        Assert.Equal(("BE1101 cancelled", $"{Day(thu)} 12:00 (moved from {Day(sun)} 08:00) · Cancelled by {TeachWorld.Teacher1Name}: Sorry"), (notice.Title, notice.Body));
        Assert.Equal(OmanTime(thu, 12).ToUniversalTime(), notice.ExpiresAt!.Value);
        var rows = await w.Rows();
        Assert.Equal(12, rows.Count);
        Assert.Equal(3, rows.Count(r => r.EndedAt is null));
        Assert.All(rows, r => Assert.Equal((sun, new TimeOnly(8, 0)), (r.OrigDate!.Value, r.OrigStart!.Value)));
        Assert.Single(rows.Select(r => (r.SectionId, r.Tag)).Where(x => x.SectionId == s.Be).Distinct());
    }

    [Fact]
    public async Task The_maker_and_anyone_who_may_change_the_class_can_undo_or_edit_and_nobody_else()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (mon, tue) = (w.Date(0, 1), w.Date(0, 2));
        var stranger = await w.SignInTeacher("nobody.special@gutech.edu.om");

        // A class under another teacher's name: flagged, and the named teacher is told, once.
        await w.Teacher1.AddSection(s.Cs1);
        var change = await w.Teacher1.Made(Cancel(s.Cs1, mon));
        var row = (await w.Rows()).Single();
        Assert.True(row.Flagged);
        Assert.Equal((TeachWorld.Teacher2Name, TeachWorld.Teacher1Name), (row.NamedTeacher, row.ByName));
        var told = Assert.Single(await w.Notices(TeachNotice.ToTeacher));
        Assert.Equal((w.Teacher2Id, "other-teacher", $"{TeachWorld.Teacher1Name} changed your class", change), (told.TeacherId!.Value, told.Kind, told.Title, told.GroupId));
        Assert.StartsWith("CS2201 cancelled · Data Structures", told.Body);
        Assert.Equal($"/teach?session=s{s.Cs1}.{Iso(mon)}.08:00", told.Url);
        // Their own class is not flagged.
        await w.Teacher2.Made(Cancel(s.Cs2, w.Date(1, 2)));
        Assert.False((await w.Rows()).Last().Flagged);

        // Someone with nothing to do with the class can neither undo nor edit, and doesn't see it.
        await Refused(stranger.Undo(change), HttpStatusCode.Forbidden, ChangeRules.NotYours);
        await Refused(stranger.Replace(change, new { kind = "room", room = "B200" }), HttpStatusCode.Forbidden, ChangeRules.NotYours);
        Assert.Empty(await List(stranger));
        Assert.Null((await w.Rows()).First().EndedAt);

        // The named teacher sees it as someone else's, and can undo it. The maker is told.
        var theirs = (await List(w.Teacher2)).Single(c => c.GetProperty("group").GetString() == change);
        Assert.Equal((false, true, true, true), (theirs.GetProperty("mine").GetBoolean(), theirs.GetProperty("canUndo").GetBoolean(), theirs.GetProperty("canEdit").GetBoolean(), theirs.GetProperty("flagged").GetBoolean()));
        await Json(await w.Teacher2.Undo(change));
        var ended = (await w.Rows()).First();
        Assert.Equal(("undone", $"teacher:{TeachWorld.Teacher2Email}"), (ended.EndReason, ended.EndedBy));
        var undone = Assert.Single(await w.Notices(TeachNotice.ToTeacher), n => n.Kind == "undone");
        Assert.Equal((w.Teacher1Id, $"CS2201: the change on {Day(mon)} was undone by {TeachWorld.Teacher2Name}"), (undone.TeacherId!.Value, undone.Title));

        // The named teacher who hid the class can still undo a change to it.
        await w.Teacher2.AddSection(s.Cs2, on: false);
        await w.Teacher1.AddSection(s.Cs2);
        var onHidden = await w.Teacher1.Made(Cancel(s.Cs2, tue));
        Assert.DoesNotContain((await Json(await w.Teacher2.GetAsync("/api/teach/timetable"))).GetProperty("classes").EnumerateArray(), c => c.GetProperty("section").GetProperty("id").GetInt32() == s.Cs2);
        await Json(await w.Teacher2.Undo(onHidden));

        // The maker can undo their own change after taking the class off their list.
        var own = await w.Teacher1.Made(Room(s.Cs2, tue, "08:00", "B200"));
        await w.Teacher1.AddSection(s.Cs2, on: false);
        await Refused(w.Teacher1.Change(Cancel(s.Cs2, w.Date(1, 2))), HttpStatusCode.Forbidden, ChangeRules.NotYours);
        Assert.True((await List(w.Teacher1)).Single(c => c.GetProperty("group").GetString() == own).GetProperty("canUndo").GetBoolean());
        await Json(await w.Teacher1.Undo(own));

        // A second teacher on the section can replace the first one's change, and the first is told.
        await stranger.AddSection(s.Cs1);
        var named = await w.Teacher2.Made(Cancel(s.Cs1, w.Date(1, 1)));
        var replaced = await Json(await stranger.Replace(named, new { kind = "room", room = "B200" }));
        Assert.True(replaced.GetProperty("changes")[0].GetProperty("mine").GetBoolean());
        var latest = (await w.Notices(TeachNotice.ToTeacher)).Where(n => n.GroupId == Group(replaced)).ToList();
        // The named teacher and the other co-teacher (teacher 1 added the section too); never the one who did it.
        Assert.Equal([w.Teacher1Id, w.Teacher2Id], latest.Select(n => n.TeacherId!.Value).Order());
        Assert.All(latest, n => Assert.Equal(("other-teacher", "Nobody Special changed your class"), (n.Kind, n.Title)));
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task One_change_to_a_class_under_three_codes_is_one_notice_for_each_other_teacher()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var sun = w.Date(0, 0);
        foreach (var id in new[] { s.Be, s.Ibsm, s.Log }) await w.Teacher2.AddSection(id);
        var group = await w.Teacher2.Made(Cancel(s.Be, sun));
        var told = Assert.Single(await w.Notices(TeachNotice.ToTeacher));
        Assert.Equal((w.Teacher1Id, "other-teacher", group, s.Be), (told.TeacherId!.Value, told.Kind, told.GroupId, told.SectionId!.Value));
        Assert.StartsWith("BE1101 · IBSM1101 · LOG1101 cancelled · ", told.Body);
        Assert.Equal(3, (await w.Notices(TeachNotice.Students)).Count);
        Assert.All(await w.Rows(), r => Assert.True(r.Flagged));

        await Json(await w.Teacher1.Undo(group));
        var undone = Assert.Single(await w.Notices(TeachNotice.ToTeacher), n => n.Kind == "undone");
        Assert.Equal((w.Teacher2Id, $"BE1101 · IBSM1101 · LOG1101: the change on {Day(sun)} was undone by {TeachWorld.Teacher1Name}"), (undone.TeacherId!.Value, undone.Title));
        // Nothing had been sent, so the students hear nothing at all.
        Assert.Empty(await w.Notices(TeachNotice.Students));
    }

    [Fact]
    public async Task A_frozen_teacher_changes_nothing_and_the_switch_stops_new_changes_but_not_undo()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (sun, next) = (w.Date(0, 0), w.Date(1, 0));
        var group = await w.Teacher1.Made(Cancel(s.Be, sun));

        // Frozen: no numbers, no create, no edit, no undo.
        await w.Db(db => db.Teachers.Where(t => t.Id == w.Teacher1Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.FrozenAt, w.Time.GetUtcNow()).SetProperty(t => t.FrozenBy, "test")));
        var preview = await w.Teacher1.Preview(Cancel(s.Be, next));
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Equal(["error"], Names(JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement));
        await Refused(w.Teacher1.Preview(Cancel(s.Be, next)), HttpStatusCode.Forbidden, TeachActor.Frozen);
        await Refused(w.Teacher1.Change(Cancel(s.Be, next)), HttpStatusCode.Forbidden, TeachActor.Frozen);
        await Refused(w.Teacher1.Replace(group, new { kind = "room", room = "B202" }), HttpStatusCode.Forbidden, TeachActor.Frozen);
        await Refused(w.Teacher1.Undo(group), HttpStatusCode.Forbidden, TeachActor.Frozen);
        Assert.All(await List(w.Teacher1), c => Assert.False(c.GetProperty("canUndo").GetBoolean() || c.GetProperty("canEdit").GetBoolean()));
        Assert.Equal(3, (await w.Live()).Count);
        await w.Db(db => db.Teachers.Where(t => t.Id == w.Teacher1Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.FrozenAt, (DateTimeOffset?)null)));

        // Switched off: nothing new, nothing edited; undo still works, since it only restores the timetable.
        Assert.True((await Json(await w.Teacher1.GetAsync("/api/teach/me"))).GetProperty("changesEnabled").GetBoolean());
        await w.SetSwitch(false);
        Assert.False((await Json(await w.Teacher1.GetAsync("/api/teach/me"))).GetProperty("changesEnabled").GetBoolean());
        Assert.Equal("switched-off", (await Json(await w.Teacher1.Preview(Cancel(s.Be, next)))).GetProperty("blocked").GetString());
        await Refused(w.Teacher1.Change(Cancel(s.Be, next)), HttpStatusCode.Conflict, ChangeRules.SwitchedOff);
        await Refused(w.Teacher1.Replace(group, new { kind = "room", room = "B202" }), HttpStatusCode.Conflict, ChangeRules.SwitchedOff);
        Assert.All(await List(w.Teacher1), c => Assert.True(c.GetProperty("canUndo").GetBoolean() && !c.GetProperty("canEdit").GetBoolean()));

        // An owner looking in passes the switch, and what they do is kept under their name.
        var owner = TeachWorld.ViewAs(w.Owner, TeacherKeys.ForName(TeachWorld.Teacher1Name));
        Assert.Equal(JsonValueKind.Null, (await Json(await owner.Preview(Cancel(s.Be, next)))).GetProperty("blocked").ValueKind);
        var byOwner = await Json(await owner.Change(Cancel(s.Be, next, note: "Hall closed")));
        Assert.All(byOwner.GetProperty("changes").EnumerateArray(), c => Assert.Equal((TeachActor.StaffName, true, true, true),
            (c.GetProperty("by").GetString(), c.GetProperty("flagged").GetBoolean(), c.GetProperty("canEdit").GetBoolean(), c.GetProperty("mine").GetBoolean())));
        var row = (await w.Live()).Last();
        var admin = await w.Db(db => db.AdminUsers.Where(a => a.Username == TeachWorld.OwnerName).Select(a => a.Id).SingleAsync());
        Assert.Equal(("admin", admin, TeachWorld.OwnerName, TeachActor.StaffName, TeachWorld.Teacher1Name, w.Teacher1Id, true),
            (row.ByKind, row.ById, row.ByLabel, row.ByName, row.OnBehalfOf, row.TeacherId!.Value, row.Flagged));
        var toStudents = (await w.Notices(TeachNotice.Students)).Last();
        Assert.EndsWith($"Cancelled by {TeachActor.StaffName} for {TeachWorld.Teacher1Name}: Hall closed", toStudents.Body);
        // The teacher whose view it was is told too.
        var toTeacher = Assert.Single(await w.Notices(TeachNotice.ToTeacher));
        Assert.Equal((w.Teacher1Id, $"{TeachActor.StaffName} changed your class"), (toTeacher.TeacherId!.Value, toTeacher.Title));
        // The teacher sees it as someone else's, and may undo it.
        var seen = (await List(w.Teacher1)).Last();
        Assert.Equal((false, true), (seen.GetProperty("mine").GetBoolean(), seen.GetProperty("canUndo").GetBoolean()));

        await Json(await w.Teacher1.Undo(group));
        await Json(await owner.Undo(Group(byOwner)));
        Assert.Empty(await w.Live());
        Assert.Equal($"staff:{TeachWorld.OwnerName}", (await w.Rows()).Last().EndedBy);
        Assert.Contains(await w.Notices(TeachNotice.ToTeacher), n => n.Kind == "undone" && n.TeacherId == w.Teacher1Id && n.Title.EndsWith($"was undone by {TeachActor.StaffName}"));

        await w.SetSwitch(true);
        await Json(await w.Teacher1.Change(Cancel(s.Be, next)));
    }

    [Fact]
    public async Task The_preview_gives_exact_numbers_and_every_warning_and_saves_nothing()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (sun, mon, wed, next) = (w.Date(0, 0), w.Date(0, 1), w.Date(0, 3), w.Date(1, 0));
        var t1 = w.Teacher1;

        var plain = await Json(await t1.Preview(Cancel(s.Be, sun)));
        Assert.Equal(["codes", "size", "others", "needsCode", "blocked", "problem", "warnings"], Names(plain));
        var codes = plain.GetProperty("codes").EnumerateArray().ToList();
        Assert.Equal(["sectionId", "code", "number", "students", "named", "yours", "changedBy"], Names(codes[0]));
        Assert.Equal([(s.Be, "BE1101", "1", 1, TeachWorld.Teacher1Name, true), (s.Ibsm, "IBSM1101", "1", 1, TeachWorld.Teacher1Name, true), (s.Log, "LOG1101", "1", 1, TeachWorld.Teacher1Name, true)],
            codes.Select(c => (c.GetProperty("sectionId").GetInt32(), c.GetProperty("code").GetString(), c.GetProperty("number").GetString(), c.GetProperty("students").GetInt32(),
                c.GetProperty("named").GetString(), c.GetProperty("yours").GetBoolean())));
        Assert.Equal((3, false, JsonValueKind.Null), (plain.GetProperty("size").GetInt32(), plain.GetProperty("needsCode").GetBoolean(), plain.GetProperty("blocked").ValueKind));
        Assert.Empty(plain.GetProperty("others").EnumerateArray());
        Assert.Empty(Warnings(plain));
        // The size is of the ticked codes, and equals what the timetable shows for the merged class.
        Assert.Equal(2, (await Json(await t1.Preview(Cancel(s.Be, sun, sectionIds: [s.Be, s.Ibsm])))).GetProperty("size").GetInt32());
        var sizes = (await Json(await t1.GetAsync("/api/teach/timetable"))).GetProperty("mergeSizes");
        Assert.Equal(plain.GetProperty("size").GetInt32(), sizes.GetProperty($"{Iso(sun)}|08:00|09:30|a101").GetInt32());

        // Clash: two of the three have CS2201 on Monday at 08:00. A student's own slot is not a class.
        await Json(await w.Students[2].PostAsJsonAsync("/api/me/slots", new { title = "Gym", day = 1, start = "08:00", end = "09:00", date = Iso(mon), place = "Hall" }));
        var clash = await Json(await t1.Preview(Move(s.Be, sun, "08:00", mon, "08:00", room: "b 105")));
        Assert.Equal("2 of 3 Classy users have another class then.", Warning(clash, "clash"));
        Assert.Equal("b 105 is taken then by CS2201 (until 09:30).", Warning(clash, "room-taken"));
        Assert.Equal(["clash", "room-taken"], Warnings(clash));
        // Only the codes ticked are counted.
        Assert.Equal("1 of 1 Classy users have another class then.", Warning(await Json(await t1.Preview(Move(s.Be, sun, "08:00", mon, "08:00", sectionIds: [s.Be]))), "clash"));
        Assert.DoesNotContain("clash", Warnings(await Json(await t1.Preview(Move(s.Be, sun, "08:00", mon, "08:00", sectionIds: [s.Log])))));
        // The class being moved is no clash with itself, and doesn't take its own room from itself.
        Assert.Empty(Warnings(await Json(await t1.Preview(Move(s.Be, sun, "08:00", sun, "08:30", "10:00", "A101")))));
        // A room change into a taken room warns; into a free one doesn't. A class under someone else's name says so.
        await t1.AddSection(s.Be2);
        await w.AddMeeting(s.Cs1, mon, 0, "10:00", "11:00", "C5");
        var taken = await Json(await t1.Preview(Room(s.Be2, mon, "09:45", "C5")));
        Assert.Equal("C5 is taken then by CS2201 (until 11:00).", Warning(taken, "room-taken"));
        Assert.Equal($"Listed under {TeachWorld.UnclaimedName}. They'll be told.", Warning(taken, "not-yours"));
        Assert.Equal(["not-yours"], Warnings(await Json(await t1.Preview(Room(s.Be2, mon, "09:45", "A101")))));

        Assert.Equal("That's outside 08:00–18:15.", Warning(await Json(await t1.Preview(Move(s.Be, sun, "08:00", wed, "07:00"))), "outside-hours"));
        Assert.Equal(["outside-hours"], Warnings(await Json(await t1.Preview(Move(s.Be, sun, "08:00", wed, "17:30", "19:00")))));
        Assert.Equal("That's a Friday.", Warning(await Json(await t1.Preview(Move(s.Be, sun, "08:00", w.Date(0, 5), "10:00"))), "weekend"));
        Assert.Equal(["weekend"], Warnings(await Json(await t1.Preview(Move(s.Be, sun, "08:00", w.Date(0, 6), "10:00")))));
        await Json(await w.Owner.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/days-off", new { date = Iso(w.Date(1, 3)), name = "National Day" }));
        Assert.Equal($"{w.Date(1, 3).ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)} is National Day, a day off.", Warning(await Json(await t1.Preview(Move(s.Be, sun, "08:00", w.Date(1, 3), "10:00"))), "day-off"));
        Assert.Equal("That's outside the teaching weeks.", Warning(await Json(await t1.Preview(Move(s.Be, sun, "08:00", w.Date(8, 3), "10:00"))), "outside-weeks"));
        // Onto a time the class already meets at.
        Assert.Contains("same-class", Warnings(await Json(await t1.Preview(Move(s.Be, sun, "08:00", next, "08:00")))));
        Assert.Equal("This class already has a session then.", Warning(await Json(await t1.Preview(Extra([s.Be], next, "08:00", "09:00"))), "same-class"));

        // An extra class: the codes are exactly the sections named, and the size counts each student once.
        await t1.AddSection(s.Cs1);
        var extra = await Json(await t1.Preview(Extra([s.Be, s.Cs1], wed, "10:00", "11:00", "A101")));
        Assert.Equal([s.Be, s.Cs1], extra.GetProperty("codes").EnumerateArray().Select(c => c.GetProperty("sectionId").GetInt32()));
        Assert.Equal(2, extra.GetProperty("size").GetInt32());
        Assert.Equal("Listed under Dr. Huda Al-Kindi. They'll be told.", Warning(extra, "not-yours"));
        Assert.False(extra.GetProperty("codes")[1].GetProperty("yours").GetBoolean());
        // Before a time is picked there is nothing to warn about, and nothing wrong yet.
        var empty = await Json(await t1.Preview(new { kind = "extra", sectionIds = new[] { s.Be }, type = "lecture" }));
        Assert.Equal((JsonValueKind.Null, 1), (empty.GetProperty("problem").ValueKind, empty.GetProperty("size").GetInt32()));
        Assert.Equal("Pick a different time, or use Change room.", (await Json(await t1.Preview(Move(s.Be, sun, "08:00", sun, "08:00")))).GetProperty("problem").GetString());

        // Numbers are only ever given for classes on the teacher's own timetable.
        await Refused(t1.Preview(Cancel(s.Cs2, w.Date(0, 2))), HttpStatusCode.Forbidden, ChangeRules.NotYours);
        await Refused(t1.Preview(Extra([s.Be, s.Cs2], wed, "10:00", "11:00")), HttpStatusCode.Forbidden, ChangeRules.NotYours);

        // The room picker: free for the whole class, and the class being changed doesn't block its own room.
        var free = await Json(await w.Teacher2.GetAsync($"/api/teach/rooms?date={Iso(mon)}&start=08:00&end=09:30"));
        Assert.Equal(["date", "at", "classesThatDay", "dayOff", "busy", "rooms"], Names(free));
        List<string> Rooms(JsonElement answer) => answer.GetProperty("rooms").EnumerateArray().Select(r => r.GetProperty("room").GetString()!).ToList();
        Assert.DoesNotContain("B105", Rooms(free));
        Assert.Contains("A101", Rooms(free));
        // A102 is free at 08:00 but taken from 09:45: not free for a class that runs to 10:00.
        Assert.Contains("A102", Rooms(free));
        Assert.DoesNotContain("A102", Rooms(await Json(await w.Teacher2.GetAsync($"/api/teach/rooms?date={Iso(mon)}&start=08:00&end=10:00"))));
        Assert.Contains("B105", Rooms(await Json(await w.Teacher2.GetAsync($"/api/teach/rooms?date={Iso(mon)}&start=08:00&end=09:30&sectionId={s.Cs1}&origDate={Iso(mon)}&origStart=08:00"))));
        await Refused(w.Teacher2.GetAsync($"/api/teach/rooms?date={Iso(mon)}&start=08:00&end=09:30&sectionId={s.Be}&origDate={Iso(sun)}&origStart=08:00"), HttpStatusCode.Forbidden);
        await Refused(w.Teacher2.GetAsync($"/api/teach/rooms?date={Iso(mon)}&start=10:00&end=09:30"), HttpStatusCode.BadRequest);

        // Short notice: under two hours before the class.
        w.GoTo(sun, 7, 45);
        t1 = await w.Fresh(TeachWorld.Teacher1Email);
        w.GoTo(sun, 7, 50);
        Assert.Equal("This starts in 10 minutes; students may not see it in time.", Warning(await Json(await t1.Preview(Cancel(s.Be, sun))), "short-notice"));
        Assert.DoesNotContain("short-notice", Warnings(await Json(await t1.Preview(Cancel(s.Be, next)))));
        Assert.Contains("short-notice", Warnings(await Json(await t1.Preview(Move(s.Be, next, "08:00", sun, "09:00")))));

        Assert.Empty(await w.Rows());
        Assert.Empty(await w.Notices(TeachNotice.Students));
    }

    [Fact]
    public async Task No_student_id_or_name_ever_reaches_a_teacher_or_a_notice()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var mon = w.Date(0, 1);
        // A student with teaching rights (the owner typed their name), signed in after the grant.
        await w.Services.AddTeachingStudent(w.StudentRowIds[0], "Aisha Helper");
        w.Time.Advance(TimeSpan.FromSeconds(61));
        var assistant = await w.SignInStudent(TeachWorld.StudentIdsList[0]);
        await assistant.AddSection(s.Cs1);

        var change = await w.Teacher2.Made(Cancel(s.Cs1, mon, note: "Away"));
        await w.MarkSent();
        var payloads = new List<string>
        {
            await (await assistant.Preview(Move(s.Cs1, mon, "08:00", w.Date(0, 3), "08:00"))).Content.ReadAsStringAsync(),
            await (await assistant.Undo(change)).Content.ReadAsStringAsync(),
            await (await assistant.Change(Cancel(s.Cs1, w.Date(1, 1), note: "Lab closed"))).Content.ReadAsStringAsync(),
        };
        var rows = await w.Rows();
        // The log knows who it was; what people are shown is only the name the owner typed.
        Assert.Equal($"assistant:{TeachWorld.StudentIdsList[0]}", rows[0].EndedBy);
        Assert.Equal(("student", w.StudentRowIds[0], TeachWorld.StudentIdsList[0], "Aisha Helper", true), (rows[1].ByKind, rows[1].ById, rows[1].ByLabel, rows[1].ByName, rows[1].Flagged));
        Assert.Contains(await w.Notices(TeachNotice.ToTeacher), n => n.Kind == "undone" && n.TeacherId == w.Teacher2Id && n.Title.EndsWith("was undone by Aisha Helper"));
        Assert.Contains(await w.Notices(TeachNotice.Students), n => n.Kind == "back-on");
        Assert.Contains(await w.Notices(TeachNotice.Students), n => n.Body.EndsWith("Cancelled by Aisha Helper: Lab closed"));

        foreach (var client in new[] { w.Teacher2, assistant })
        foreach (var path in new[] { "/api/teach/timetable", "/api/teach/changes", "/api/teach/sections?q=CS2201", $"/api/teach/rooms?date={Iso(mon)}&start=08:00&end=09:30" })
            payloads.Add(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
        payloads.Add(await (await w.Teacher2.GetAsync("/api/teach/me")).Content.ReadAsStringAsync());
        payloads.Add(await (await w.Teacher2.Preview(Move(s.Cs1, w.Date(1, 1), "08:00", w.Date(1, 3), "08:00"))).Content.ReadAsStringAsync());
        payloads.Add(await (await w.Students[1].GetAsync("/api/me/timetable")).Content.ReadAsStringAsync());
        foreach (var n in await w.Db(db => db.TeachNotices.AsNoTracking().ToListAsync()))
            payloads.Add(string.Join("\n", n.Title, n.Body, n.Url, n.Tag, n.ByLabel));

        var universityId = new Regex(@"(?<!\d)\d{2}-\d{4}(?!\d)");
        Assert.True(payloads.Count > 15);
        foreach (var text in payloads)
        {
            Assert.False(universityId.IsMatch(text), text);
            Assert.All(TeachWorld.StudentIdsList, id => Assert.DoesNotContain(id, text));
        }
    }

    [Fact]
    public async Task A_class_under_three_codes_moved_to_no_room_is_still_one_block()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (sun, wed) = (w.Date(0, 0), w.Date(0, 3));
        var group = await w.Teacher1.Made(Move(s.Be, sun, "08:00", wed, "10:00", room: ""));

        var tt = await Json(await w.Teacher1.GetAsync("/api/teach/timetable"));
        var meetings = tt.GetProperty("classes").EnumerateArray().SelectMany(c => c.GetProperty("section").GetProperty("meetings").EnumerateArray()).ToList();
        var put = meetings.Where(m => m.TryGetProperty("changeId", out _)).ToList();
        Assert.Equal(3, put.Count);
        // One key for the three, though "to be announced" is no room at all.
        Assert.All(put, m => Assert.Equal(($"g:{group}", "", Iso(wed), "10:00", "11:30", "lecture", 3),
            (m.GetProperty("merge").GetString(), m.GetProperty("room").GetString(), m.GetProperty("date").GetString(), m.GetProperty("start").GetString(),
                m.GetProperty("end").GetString(), m.GetProperty("type").GetString(), m.GetProperty("day").GetInt32())));
        Assert.Equal(3, tt.GetProperty("mergeSizes").GetProperty($"g:{group}").GetInt32());
        // The old slot is off for all three, and still carries the university's key.
        var old = meetings.Where(m => m.GetProperty("date").GetString() == Iso(sun)).ToList();
        Assert.Equal(3, old.Count);
        Assert.All(old, m => Assert.Equal((Iso(sun), $"{Iso(sun)}|08:00|09:30|a101"), (string.Join(",", m.GetProperty("excludedDates").EnumerateArray().Select(d => d.GetString())), m.GetProperty("merge").GetString())));
        var changes = tt.GetProperty("changes").EnumerateArray().ToList();
        Assert.Equal(3, changes.Count);
        Assert.All(changes, c => Assert.True(c.GetProperty("canUndo").GetBoolean() && c.GetProperty("canEdit").GetBoolean() && c.GetProperty("mine").GetBoolean()));

        // Reminders and feeds see one class, named by its three codes.
        var items = await w.Db(async db =>
        {
            var teacher = await db.Teachers.SingleAsync(t => t.Id == w.Teacher1Id);
            return (await TeacherSchedule.ItemsAsync(db, await db.Semesters.SingleAsync(x => x.Id == w.Semester), [teacher], slots: false, planner: false, default))[teacher.Id];
        });
        var moved = Assert.Single(items, i => i.Date == wed);
        Assert.Equal(("BE1101 · IBSM1101 · LOG1101", new TimeOnly(10, 0), "", "move"), (moved.Code, moved.Start, moved.Room, moved.ChangeKind));
        Assert.DoesNotContain(items, i => i.Date == sun && !i.Skip.Contains(sun));

        // Edited: the block goes by the new change's group.
        var alone = Group(await Json(await w.Teacher1.Replace(group, new { kind = "move", newDate = Iso(wed), newStart = "10:00", room = "B202" })));
        tt = await Json(await w.Teacher1.GetAsync("/api/teach/timetable"));
        Assert.Equal(3, tt.GetProperty("mergeSizes").GetProperty($"g:{alone}").GetInt32());
        Assert.False(tt.GetProperty("mergeSizes").TryGetProperty($"g:{group}", out _));
    }

    [Fact]
    public async Task A_session_listed_in_two_rooms_is_changed_as_one()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var mon = w.Date(0, 1);
        await w.Teacher1.AddSection(s.Be2);
        // The university lists the same class at the same time in a second room.
        await w.AddMeeting(s.Be2, mon, 0, "09:45", "11:15", "A103");

        // "The room it's already in" can't be said of a class in two rooms.
        var changed = await Json(await w.Teacher1.Change(Room(s.Be2, mon, "09:45", "A102")));
        Assert.Single(changed.GetProperty("changes").EnumerateArray());
        var row = Assert.Single(await w.Live());
        Assert.Equal(("A102", "A102"), (row.OrigRoom, row.NewRoom));

        var be2 = (await Json(await w.Teacher1.GetAsync("/api/teach/timetable"))).GetProperty("classes").EnumerateArray().Single(c => c.GetProperty("section").GetProperty("id").GetInt32() == s.Be2);
        var that = be2.GetProperty("section").GetProperty("meetings").EnumerateArray().Where(m => m.GetProperty("date").GetString() == Iso(mon)).ToList();
        // Both listed meetings are off, and one session is on in their place.
        Assert.Equal(3, that.Count);
        Assert.Equal(2, that.Count(m => !m.TryGetProperty("changeId", out _) && m.GetProperty("excludedDates").EnumerateArray().Any(d => d.GetString() == Iso(mon))));
        Assert.Equal("A102", that.Single(m => m.TryGetProperty("changeId", out _)).GetProperty("room").GetString());
        var free = await Json(await w.Students[0].GetAsync($"/api/rooms/free?date={Iso(mon)}&at=10:00"));
        Assert.Contains(free.GetProperty("rooms").EnumerateArray(), r => r.GetProperty("room").GetString() == "A103");
        Assert.DoesNotContain(free.GetProperty("rooms").EnumerateArray(), r => r.GetProperty("room").GetString() == "A102");

        // Cancelling it takes both away.
        await Json(await w.Teacher1.Replace(row.GroupId, new { kind = "cancel" }));
        Assert.Empty(Sessions(await Json(await w.Teacher1.GetAsync("/api/teach/timetable")), mon, mon));
    }

    [Fact]
    public async Task Someone_with_one_code_of_a_merged_class_undoes_or_edits_that_code_only()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var sun = w.Date(0, 0);
        // A second teacher has added one of the three codes.
        var other = await w.SignInTeacher("co.teacher@gutech.edu.om");
        await other.AddSection(s.Ibsm);
        var group = await w.Teacher1.Made(Cancel(s.Be, sun));
        // They see their code's change, and its numbers only.
        Assert.Equal([s.Ibsm], (await List(other)).Select(c => c.GetProperty("sectionId").GetInt32()));
        var preview = await Json(await other.Preview(Room(s.Ibsm, sun, "08:00", "B202")));
        Assert.Equal([s.Ibsm], preview.GetProperty("codes").EnumerateArray().Select(c => c.GetProperty("sectionId").GetInt32()));
        Assert.Equal(1, preview.GetProperty("size").GetInt32());
        await Refused(other.Replace(group, new { kind = "room", room = "B202", sectionIds = new[] { s.Ibsm, s.Be } }), HttpStatusCode.BadRequest);

        // Editing it gives their code a change of its own; the other two stay cancelled.
        var edited = await Json(await other.Replace(group, new { kind = "room", room = "B202" }));
        Assert.Equal(s.Ibsm, Assert.Single(edited.GetProperty("changes").EnumerateArray()).GetProperty("sectionId").GetInt32());
        Assert.Equal([("cancel", s.Be, group), ("cancel", s.Log, group), ("room", s.Ibsm, Group(edited))], (await w.Live()).Select(r => (r.Kind, r.SectionId!.Value, r.GroupId)));
        // Undoing it puts their class back, and nothing of the rest is theirs to undo.
        await Json(await other.Undo(Group(edited)));
        await Refused(other.Undo(group), HttpStatusCode.Forbidden, ChangeRules.NotYours);
        Assert.Equal([s.Be, s.Log], (await w.Live()).Select(r => r.SectionId!.Value));

        // The other way round: their change to all they have (one code) is undone by the named teacher.
        var theirs = await other.Made(Cancel(s.Ibsm, w.Date(1, 0)));
        await Json(await w.Teacher1.Undo(theirs));
        // And an undo by them of a whole-class change touches their code alone.
        var whole = await w.Teacher1.Made(Room(s.Be, w.Date(1, 0), "08:00", "B202"));
        await Json(await other.Undo(whole));
        Assert.Equal([s.Be, s.Log], (await w.Live()).Where(r => r.GroupId == whole).Select(r => r.SectionId!.Value));
        Assert.Equal("undone", (await w.Rows()).Single(r => r.GroupId == whole && r.SectionId == s.Ibsm).EndReason);
        // The preview of a change someone else made never gives numbers for a class that isn't on the asker's timetable.
        await Refused(other.Preview(Cancel(s.Be, w.Date(1, 0))), HttpStatusCode.Forbidden, ChangeRules.NotYours);
    }

    [Fact]
    public async Task A_weekly_class_under_three_codes_with_one_code_off_on_a_date_is_named_by_the_codes_still_on()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        foreach (var id in new[] { s.Be, s.Ibsm, s.Log }) await w.AddMeeting(id, null, 3, "10:00", "11:00", "A105");
        var (first, second) = (w.Date(0, 3), w.Date(1, 3));
        await w.Teacher1.Made(Cancel(s.Be, first, "10:00", sectionIds: [s.Be]));
        await w.Teacher1.Made(Cancel(s.Be, second, "10:00"));

        var cancelled = new List<(int TeacherId, UniSchedule.Api.Schedule.StudentSchedule.Item Item)>();
        var items = await w.Db(async db =>
        {
            var teacher = await db.Teachers.SingleAsync(t => t.Id == w.Teacher1Id);
            return (await TeacherSchedule.ItemsAsync(db, await db.Semesters.SingleAsync(x => x.Id == w.Semester), [teacher], slots: false, planner: false, default, cancelled))[teacher.Id];
        });
        // The weekly class is one item for the three codes, off on both dates.
        var weekly = Assert.Single(items, i => i.Date is null);
        Assert.Equal(("BE1101 · IBSM1101 · LOG1101", 3, new TimeOnly(10, 0), s.Be), (weekly.Code, weekly.Day, weekly.Start, weekly.SectionId!.Value));
        Assert.True(weekly.Skip.Contains(first) && weekly.Skip.Contains(second));
        // On the date two codes still meet, they have an item of their own, led by the first of them.
        var still = Assert.Single(items, i => i.Date == first);
        Assert.Equal(("IBSM1101 · LOG1101", new TimeOnly(10, 0), "A105", s.Ibsm), (still.Code, still.Start, still.Room, still.SectionId!.Value));
        Assert.DoesNotContain(items, i => i.Date == second);
        // The sessions that are off: one per teacher action, never among the items.
        Assert.Equal([("BE1101", first), ("BE1101 · IBSM1101 · LOG1101", second)], cancelled.Select(c => (c.Item.Code, c.Item.Date!.Value)).OrderBy(c => c.Item2));
        Assert.All(cancelled, c => Assert.Equal((w.Teacher1Id, "cancel", 0), (c.TeacherId, c.Item.ChangeKind, c.Item.StudentId)));
        Assert.All(items, i => Assert.Equal(0, i.StudentId));

        // The teacher's timetable gives every code the same weekly key, so the app can do the same.
        var tt = await Json(await w.Teacher1.GetAsync("/api/teach/timetable"));
        var keys = tt.GetProperty("classes").EnumerateArray().SelectMany(c => c.GetProperty("section").GetProperty("meetings").EnumerateArray())
            .Where(m => m.GetProperty("date").ValueKind == JsonValueKind.Null).Select(m => m.GetProperty("merge").GetString()).ToList();
        Assert.Equal(["w3|10:00|11:00|a105", "w3|10:00|11:00|a105", "w3|10:00|11:00|a105"], keys);
    }
}
