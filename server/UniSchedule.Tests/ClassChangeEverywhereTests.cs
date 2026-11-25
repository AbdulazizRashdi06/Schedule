using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.ChangeKit;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// A teacher's change to a class shows the same everywhere a student looks. Old cached copies of the app keep reading
/// the student timetable, so first of all its shape is pinned here: everything it always had, where it always was.
/// </summary>
public class ClassChangeEverywhereTests
{
    static List<string> Names(JsonElement o) => o.EnumerateObject().Select(p => p.Name).ToList();

    /// <summary>The old keys, in their old order, come first; anything after them must be one of the named additions.</summary>
    static void AssertKeys(JsonElement o, string[] old, params string[] added)
    {
        var names = Names(o);
        Assert.Equal(old, names.Take(old.Length));
        Assert.All(names.Skip(old.Length), n => Assert.Contains(n, added));
    }

    static void AssertKinds(JsonElement o, params (string Name, JsonValueKind[] Kinds)[] expected)
    {
        foreach (var (name, kinds) in expected)
            Assert.True(kinds.Contains(o.GetProperty(name).ValueKind), $"{name} is {o.GetProperty(name).ValueKind}.");
    }

    static readonly JsonValueKind[] Text = [JsonValueKind.String], Number = [JsonValueKind.Number], Flag = [JsonValueKind.True, JsonValueKind.False],
        List = [JsonValueKind.Array], Thing = [JsonValueKind.Object], TextOrNull = [JsonValueKind.String, JsonValueKind.Null],
        NumberOrNull = [JsonValueKind.Number, JsonValueKind.Null], ListOrNull = [JsonValueKind.Array, JsonValueKind.Null];

    [Fact]
    public async Task The_student_timetable_keeps_its_old_property_set_and_types()
    {
        using var w = await TeachWorld.Create();
        var student = w.Students[0];
        var be = await w.Section("BE1101", "1");
        var cs = await w.Section("CS2201", "1");
        // Something in every part of it: a slot, a note, a day off, a class that changed, a clash, a course with no section picked.
        await Json(await student.PostAsJsonAsync("/api/me/slots", new { title = "Gym", day = 3, start = "16:00", end = "17:00", place = "Hall", note = "Shoes" }));
        var courseId = await w.Db(db => db.Sections.Where(s => s.Id == be).Select(s => s.CourseId).SingleAsync());
        await Json(await student.PostAsJsonAsync("/api/me/notes", new { courseId, text = "Whole course" }));
        await Json(await student.PostAsJsonAsync("/api/me/notes", new { courseId, text = "That day", date = w.Date(0, 0).ToString("yyyy-MM-dd") }));
        await Json(await w.Owner.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/days-off", new { date = w.Date(1, 4).ToString("yyyy-MM-dd"), name = "National Day" }));
        await w.Db(async db =>
        {
            var one = await db.Sections.Include(s => s.Meetings).SingleAsync(s => s.Id == cs);
            // A weekly hour on Thursdays (it skips the day off), and a dated one on top of the BE1101 class.
            one.Meetings.Add(new Meeting { Day = 4, Start = new(10, 0), End = new(11, 0), Room = "B105" });
            one.Meetings.Add(new Meeting { Day = 0, Date = w.Date(0, 0), Start = new(9, 0), End = new(10, 0), Room = "B105" });
            one.ChangedAt = w.Time.GetUtcNow();
            one.ChangeSummary = "Thursday 10:00 added";
            var lab = await db.Courses.SingleAsync(c => c.Code == "CS2201-L");
            db.Picks.Add(new Pick { StudentId = w.StudentRowIds[0], CourseId = lab.Id, SectionId = null, SectionRemoved = true });
            await db.SaveChangesAsync();
        });

        var tt = await Json(await student.GetAsync("/api/me/timetable"));
        AssertKeys(tt, ["semester", "span", "daysOff", "plannerEvents", "slots", "classes", "needsSection", "clashes"], "changes");
        AssertKinds(tt, ("semester", Thing), ("span", Thing), ("daysOff", List), ("plannerEvents", List), ("slots", List), ("classes", List), ("needsSection", List), ("clashes", List));
        Assert.Equal(["id", "name"], Names(tt.GetProperty("semester")));
        AssertKinds(tt.GetProperty("semester"), ("id", Number), ("name", Text));
        Assert.Equal(["first", "last"], Names(tt.GetProperty("span")));
        AssertKinds(tt.GetProperty("span"), ("first", Text), ("last", Text));
        var dayOff = Assert.Single(tt.GetProperty("daysOff").EnumerateArray());
        Assert.Equal(["date", "name"], Names(dayOff));
        Assert.Equal((w.Date(1, 4).ToString("yyyy-MM-dd"), "National Day"), (dayOff.GetProperty("date").GetString(), dayOff.GetProperty("name").GetString()));

        var slot = Assert.Single(tt.GetProperty("slots").EnumerateArray());
        Assert.Equal(["id", "title", "day", "start", "end", "date", "place", "note"], Names(slot));
        AssertKinds(slot, ("id", Number), ("title", Text), ("day", Number), ("start", Text), ("end", Text), ("date", TextOrNull), ("place", Text), ("note", TextOrNull));
        Assert.Equal(slot.GetRawText(), (await Json(await student.GetAsync("/api/me/slots")))[0].GetRawText());

        // Classes in course-code order, each with everything it always had.
        Assert.Equal(["BE1101", "CS2201"], tt.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("code").GetString()));
        foreach (var c in tt.GetProperty("classes").EnumerateArray())
        {
            AssertKeys(c, ["courseId", "code", "name", "type", "credits", "section", "changed", "notes"], "teachers");
            AssertKinds(c, ("courseId", Number), ("code", Text), ("name", Text), ("type", Text), ("credits", NumberOrNull), ("section", Thing), ("changed", Flag), ("notes", Thing));
            Assert.Equal(["classWide", "dates"], Names(c.GetProperty("notes")));
            AssertKinds(c.GetProperty("notes"), ("classWide", Number), ("dates", Thing));
            var section = c.GetProperty("section");
            Assert.Equal(["id", "number", "instructor", "meetings", "changedAt", "changeSummary"], Names(section));
            AssertKinds(section, ("id", Number), ("number", Text), ("instructor", Text), ("meetings", List), ("changedAt", TextOrNull), ("changeSummary", TextOrNull));
            // With no teacher's change, a meeting is exactly what it was: nothing newer is even mentioned.
            foreach (var m in section.GetProperty("meetings").EnumerateArray())
            {
                Assert.Equal(["day", "start", "end", "room", "date", "excludedDates"], Names(m));
                AssertKinds(m, ("day", Number), ("start", Text), ("end", Text), ("room", Text), ("date", TextOrNull), ("excludedDates", ListOrNull));
            }
        }
        var first = tt.GetProperty("classes")[0];
        Assert.Equal((courseId, "Business Economics", "Core", false), (first.GetProperty("courseId").GetInt32(), first.GetProperty("name").GetString(), first.GetProperty("type").GetString(), first.GetProperty("changed").GetBoolean()));
        Assert.Equal(1, first.GetProperty("notes").GetProperty("classWide").GetInt32());
        Assert.Equal(1, first.GetProperty("notes").GetProperty("dates").GetProperty(w.Date(0, 0).ToString("yyyy-MM-dd")).GetInt32());
        Assert.Equal((be, "1", TeachWorld.Teacher1Name), (first.GetProperty("section").GetProperty("id").GetInt32(), first.GetProperty("section").GetProperty("number").GetString(), first.GetProperty("section").GetProperty("instructor").GetString()));
        var meeting = first.GetProperty("section").GetProperty("meetings")[0];
        Assert.Equal((0, "08:00", "09:30", "A101", w.Date(0, 0).ToString("yyyy-MM-dd"), JsonValueKind.Null),
            (meeting.GetProperty("day").GetInt32(), meeting.GetProperty("start").GetString(), meeting.GetProperty("end").GetString(), meeting.GetProperty("room").GetString(),
                meeting.GetProperty("date").GetString(), meeting.GetProperty("excludedDates").ValueKind));

        // The changed class: its badge and summary, and the weekly hour skipping the day off.
        var second = tt.GetProperty("classes")[1];
        Assert.True(second.GetProperty("changed").GetBoolean());
        Assert.Equal("Thursday 10:00 added", second.GetProperty("section").GetProperty("changeSummary").GetString());
        var weekly = second.GetProperty("section").GetProperty("meetings").EnumerateArray().Single(m => m.GetProperty("date").ValueKind == JsonValueKind.Null);
        Assert.Equal((4, "10:00"), (weekly.GetProperty("day").GetInt32(), weekly.GetProperty("start").GetString()));
        Assert.Equal([w.Date(1, 4).ToString("yyyy-MM-dd")], weekly.GetProperty("excludedDates").EnumerateArray().Select(d => d.GetString()));

        var needs = Assert.Single(tt.GetProperty("needsSection").EnumerateArray());
        Assert.Equal(["courseId", "code", "name", "removed"], Names(needs));
        Assert.Equal(("CS2201-L", "Data Structures Lab", true), (needs.GetProperty("code").GetString(), needs.GetProperty("name").GetString(), needs.GetProperty("removed").GetBoolean()));
        Assert.Equal([be, cs], Assert.Single(tt.GetProperty("clashes").EnumerateArray()).EnumerateArray().Select(i => i.GetInt32()));

        // What is new sits beside it: no teacher has changed anything, and each class names its teachers.
        Assert.Empty(tt.GetProperty("changes").EnumerateArray());
        Assert.All(tt.GetProperty("classes").EnumerateArray(), c => Assert.Equal(JsonValueKind.Array, c.GetProperty("teachers").ValueKind));

        // Opening the class still clears its badge.
        await student.PostAsJsonAsync("/api/me/seen", new { sectionId = cs });
        Assert.False((await Json(await student.GetAsync("/api/me/timetable"))).GetProperty("classes")[1].GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task With_no_semester_published_the_student_timetable_is_what_it_was()
    {
        using var w = await TeachWorld.Create(seed: false);
        var student = await w.SignInStudent("26-7009");
        var tt = await Json(await student.GetAsync("/api/me/timetable"));
        Assert.Equal(["semester", "classes", "needsSection", "clashes", "slots"], Names(tt));
        Assert.Equal(JsonValueKind.Null, tt.GetProperty("semester").ValueKind);
        foreach (var key in new[] { "classes", "needsSection", "clashes", "slots" }) Assert.Empty(tt.GetProperty(key).EnumerateArray());
    }

    // ---- A teacher's change, everywhere a student looks (the planner is off throughout) ----

    static string Day(DateOnly d) => d.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
    static async Task<JsonElement> Timetable(HttpClient c) => await Json(await c.GetAsync("/api/me/timetable"));
    static JsonElement ClassOf(JsonElement timetable, string code) => timetable.GetProperty("classes").EnumerateArray().Single(c => c.GetProperty("code").GetString() == code);
    static List<JsonElement> Meetings(JsonElement timetable, string code) => ClassOf(timetable, code).GetProperty("section").GetProperty("meetings").EnumerateArray().ToList();
    static string Excluded(JsonElement meeting) => meeting.GetProperty("excludedDates").ValueKind == JsonValueKind.Null ? ""
        : string.Join(",", meeting.GetProperty("excludedDates").EnumerateArray().Select(d => d.GetString()));

    /// <summary>The student's calendar file (lines unfolded) and text feed, as a calendar app and an assistant fetch them.</summary>
    static async Task<(string Ics, string Text)> Feeds(TeachWorld w, HttpClient student, DateOnly from, int days)
    {
        var links = await Json(await student.GetAsync("/api/me/calendar"));
        var app = w.Browser();
        var ics = await (await app.GetAsync(new Uri(links.GetProperty("url").GetString()!).PathAndQuery)).Content.ReadAsStringAsync();
        var text = await (await app.GetAsync(new Uri(links.GetProperty("textUrl").GetString()!).PathAndQuery + $"?from={Iso(from)}&days={days}")).Content.ReadAsStringAsync();
        return (ics.Replace("\r\n ", ""), text.Replace("\r\n", "\n"));
    }

    /// <summary>The events of a calendar file: each one's lines by name (the part before the first ":" or ";").</summary>
    static List<Dictionary<string, string>> Events(string ics) => ics.Split("BEGIN:VEVENT").Skip(1)
        .Select(e => e.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).TakeWhile(l => l != "END:VEVENT")
            .ToDictionary(l => l[..l.IndexOfAny([':', ';'])], l => l[(l.IndexOf(':') + 1)..])).ToList();

    /// <summary>One day of the text feed: its heading and the lines under it.</summary>
    static string DayOf(string text, DateOnly d) =>
        text.Split("\n\n").Single(block => block.StartsWith(d.ToString("ddd d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)));

    static async Task<List<string>> FreeRoomsAt(HttpClient c, DateOnly date, string at) =>
        (await Json(await c.GetAsync($"/api/rooms/free?date={Iso(date)}&at={at}"))).GetProperty("rooms").EnumerateArray().Select(r => r.GetProperty("room").GetString()!).ToList();

    /// <summary>Reminders 15 minutes before, for a student with a device.</summary>
    static Task Remind(TeachWorld w, int student) => w.Db(async db =>
    {
        await db.Students.Where(x => x.Id == student).ExecuteUpdateAsync(u => u.SetProperty(x => x.RemindMinutes, 15));
        db.PushSubscriptions.Add(new PushSubscription { StudentId = student, Endpoint = $"https://fcm.googleapis.com/fcm/send/everywhere-{student}", P256dh = "k", Auth = "a", CreatedAt = w.Time.GetUtcNow() });
        await db.SaveChangesAsync();
    });

    /// <summary>The reminders that come due for a class starting at that time, for one student.</summary>
    static async Task<List<Note>> Due(TeachWorld w, int student, DateOnly date, int hour, int minute)
    {
        var at = OmanTime(date, hour, minute).AddMinutes(-15);
        return await w.Db(async db => (await Notifications.DueReminders(db, at.AddSeconds(-10), at.AddSeconds(20), default)).Where(d => d.StudentId == student).Select(d => d.Note).ToList());
    }

    static Task<HttpResponseMessage> Missed(HttpClient student, int courseId, DateOnly date, string start, bool missed = true) =>
        student.PutAsJsonAsync("/api/me/absences", new { courseId, date = Iso(date), start, missed });

    static async Task<List<(int Course, string Date, string Start)>> Absent(HttpClient student) =>
        (await Json(await student.GetAsync("/api/me/absences"))).GetProperty("absences").EnumerateArray()
            .Select(a => (a.GetProperty("courseId").GetInt32(), a.GetProperty("date").GetString()!, a.GetProperty("start").GetString()!)).ToList();

    [Fact]
    public async Task A_cancelled_class_is_off_everywhere_and_undo_brings_it_back()
    {
        using var w = await TeachWorld.Create();
        var student = w.Students[0];
        var me = w.StudentRowIds[0];
        var be = await w.Section("BE1101", "1");
        var course = await w.CourseOf(be);
        var meeting = await w.Db(db => db.Meetings.Where(m => m.SectionId == be && m.Date == w.Date(0, 0)).Select(m => m.Id).SingleAsync());
        var sun = w.Date(0, 0);
        await Remind(w, me);
        var friends = (await Json(await student.PostAsJsonAsync("/api/me/groups", new { name = "Study", kind = "friends", displayName = "Me" }))).GetProperty("id").GetInt32();
        var car = (await Json(await student.PostAsJsonAsync("/api/me/groups", new { name = "Car", kind = "car", displayName = "Me" }))).GetProperty("id").GetInt32();
        JsonElement DayIn(JsonElement week) => week.GetProperty("days").EnumerateArray().Single(d => d.GetProperty("date").GetString() == Iso(sun));

        // Before: the class is on everywhere.
        var before = await Timetable(student);
        var feedsBefore = await Feeds(w, student, sun, 1);
        Assert.Contains(Sessions(before, sun, sun), x => x == ("BE1101", sun, "08:00", "09:30", "A101"));
        Assert.Equal("BE1101 in 15 min", Assert.Single(await Due(w, me, sun, 8, 0)).Title);
        Assert.DoesNotContain("A101", await FreeRoomsAt(student, sun, "08:30"));
        Assert.Equal("09:30", DayIn(await Json(await student.GetAsync($"/api/me/groups/{friends}/week?start={Iso(sun)}"))).GetProperty("free")[0].GetProperty("start").GetString());
        Assert.Equal(JsonValueKind.Object, DayIn(await Json(await student.GetAsync($"/api/me/groups/{car}/rides?start={Iso(sun)}"))).GetProperty("ride").ValueKind);

        var group = await w.Teacher1.Made(new { kind = "cancel", sectionId = be, date = Iso(sun), start = "08:00", note = "Guest lecture" });

        // The timetable: the session's date is excluded, nothing is added, and the change is listed for the marks.
        var tt = await Timetable(student);
        var listed = Meetings(tt, "BE1101");
        Assert.Equal(2, listed.Count);
        Assert.Equal((Iso(sun), Iso(sun)), (listed[0].GetProperty("date").GetString(), Excluded(listed[0])));
        Assert.Equal("", Excluded(listed[1]));
        Assert.All(listed, m => Assert.Equal(["day", "start", "end", "room", "date", "excludedDates"], Names(m)));
        var change = Assert.Single(tt.GetProperty("changes").EnumerateArray());
        // A student is never told what a teacher may do with it.
        Assert.Equal(["id", "group", "kind", "courseId", "sectionId", "code", "type", "from", "to", "note", "by", "at"], Names(change));
        Assert.Equal((group, "cancel", course, be, "BE1101", "lecture", "Guest lecture", TeachWorld.Teacher1Name, JsonValueKind.Null),
            (change.GetProperty("group").GetString(), change.GetProperty("kind").GetString(), change.GetProperty("courseId").GetInt32(), change.GetProperty("sectionId").GetInt32(),
                change.GetProperty("code").GetString(), change.GetProperty("type").GetString(), change.GetProperty("note").GetString(), change.GetProperty("by").GetString(), change.GetProperty("to").ValueKind));
        Assert.Equal($"{{\"date\":\"{Iso(sun)}\",\"start\":\"08:00\",\"end\":\"09:30\",\"room\":\"A101\"}}", change.GetProperty("from").GetRawText());
        // A copy of the app that knows nothing of changes just has no class then; everything else is as it was.
        Assert.DoesNotContain(Sessions(tt, sun, sun), x => x.Code == "BE1101");
        Assert.Equal(Sessions(before, w.Date(-5, 0), w.Date(9, 0)).Where(x => x != ("BE1101", sun, "08:00", "09:30", "A101")), Sessions(tt, w.Date(-5, 0), w.Date(9, 0)));

        // No reminder, a free room, free time for groups, no ride.
        Assert.Empty(await Due(w, me, sun, 8, 0));
        Assert.Contains("A101", await FreeRoomsAt(student, sun, "08:30"));
        Assert.Equal("08:00", DayIn(await Json(await student.GetAsync($"/api/me/groups/{friends}/week?start={Iso(sun)}"))).GetProperty("free")[0].GetProperty("start").GetString());
        Assert.Equal(JsonValueKind.Null, DayIn(await Json(await student.GetAsync($"/api/me/groups/{car}/rides?start={Iso(sun)}"))).GetProperty("ride").ValueKind);

        // The calendar keeps it, under its own id, with "Cancelled:" in front and marked as free time. Never STATUS:CANCELLED.
        var (ics, text) = await Feeds(w, student, sun, 1);
        var cancelled = Assert.Single(Events(ics), e => e["UID"] == $"class-{be}-{meeting}@classy");
        Assert.Equal(("Cancelled: Business Economics · Lecture", $"{sun:yyyyMMdd}T080000", "TRANSPARENT", $"Cancelled by {TeachWorld.Teacher1Name}. Guest lecture"),
            (cancelled["SUMMARY"], cancelled["DTSTART"], cancelled["TRANSP"], cancelled["DESCRIPTION"]));
        Assert.DoesNotContain("STATUS", ics);
        Assert.Equal(Events(feedsBefore.Ics).Count, Events(ics).Count);
        Assert.Contains($"- 08:00–09:30  CANCELLED by {TeachWorld.Teacher1Name}: BE1101 Business Economics · Guest lecture", DayOf(text, sun));
        Assert.DoesNotContain("Lecture", DayOf(text, sun));

        // Undo: everything is exactly as it was.
        await Json(await w.Teacher1.Undo(group));
        Assert.Equal(before.GetRawText(), (await Timetable(student)).GetRawText());
        Assert.Equal(feedsBefore, await Feeds(w, student, sun, 1));
        Assert.Single(await Due(w, me, sun, 8, 0));
        Assert.DoesNotContain("A101", await FreeRoomsAt(student, sun, "08:30"));

        // Cancelled again. Once the day has come it can't be marked missed, and a mark from before no longer counts.
        await w.Teacher1.Made(new { kind = "cancel", sectionId = be, date = Iso(sun), start = "08:00" });
        await w.Db(async db =>
        {
            db.Absences.Add(new Absence { StudentId = me, CourseId = course, Date = sun, Start = new(8, 0), CreatedAt = w.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        });
        w.GoTo(sun, 12, 0);
        Assert.Empty(await Absent(student));
        // Unmarking is always allowed; marking it again is not.
        Assert.Equal(HttpStatusCode.NoContent, (await Missed(student, course, sun, "08:00", missed: false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Missed(student, course, sun, "08:00")).StatusCode);
        Assert.Equal(0, await w.Db(db => db.Absences.CountAsync()));
        // Last week's cancelled class still shows as cancelled.
        w.GoTo(w.Date(1, 2), 12, 0);
        Assert.Single((await Timetable(student)).GetProperty("changes").EnumerateArray());
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task A_moved_class_is_at_its_new_time_everywhere_under_the_same_calendar_id()
    {
        using var w = await TeachWorld.Create();
        var student = w.Students[0];
        var me = w.StudentRowIds[0];
        var cs = await w.Section("CS2201", "1");
        var course = await w.CourseOf(cs);
        var (mon, wed) = (w.Date(0, 1), w.Date(0, 3));
        var meeting = await w.Db(db => db.Meetings.Where(m => m.SectionId == cs && m.Date == mon).Select(m => m.Id).SingleAsync());
        await Remind(w, me);
        var before = await Timetable(student);
        var feedsBefore = await Feeds(w, student, mon, 3);
        // No class on Wednesday yet, so no note can be pinned to it.
        Assert.Equal(HttpStatusCode.BadRequest, (await student.PostAsJsonAsync("/api/me/notes", new { courseId = course, text = "Bring laptop", date = Iso(wed) })).StatusCode);
        Assert.Contains("A102", await FreeRoomsAt(student, wed, "10:30"));

        var body = new { kind = "move", sectionId = cs, date = Iso(mon), start = "08:00", newDate = Iso(wed), newStart = "10:00", room = "a 102", note = "Swap" };
        var group = await w.Teacher2.Made(body);
        var id = (await w.Live()).Single().Id;

        var tt = await Timetable(student);
        var listed = Meetings(tt, "CS2201");
        Assert.Equal(3, listed.Count);
        // The old slot is excluded; the class at its new time is one more dated meeting, which every copy of the app draws.
        Assert.Equal((Iso(mon), Iso(mon)), (listed[0].GetProperty("date").GetString(), Excluded(listed[0])));
        var put = listed[1];
        Assert.Equal(["day", "start", "end", "room", "date", "excludedDates", "changeId", "type"], Names(put));
        Assert.Equal((3, "10:00", "11:30", "a 102", Iso(wed), JsonValueKind.Null, id, "lecture"),
            (put.GetProperty("day").GetInt32(), put.GetProperty("start").GetString(), put.GetProperty("end").GetString(), put.GetProperty("room").GetString(),
                put.GetProperty("date").GetString(), put.GetProperty("excludedDates").ValueKind, put.GetProperty("changeId").GetInt32(), put.GetProperty("type").GetString()));
        var change = Assert.Single(tt.GetProperty("changes").EnumerateArray());
        Assert.Equal(("move", id, $"{{\"date\":\"{Iso(mon)}\",\"start\":\"08:00\",\"end\":\"09:30\",\"room\":\"B105\"}}", $"{{\"date\":\"{Iso(wed)}\",\"start\":\"10:00\",\"end\":\"11:30\",\"room\":\"a 102\"}}"),
            (change.GetProperty("kind").GetString(), change.GetProperty("id").GetInt32(), change.GetProperty("from").GetRawText(), change.GetProperty("to").GetRawText()));
        Assert.Equal([("CS2201", wed, "10:00", "11:30", "a 102")], Sessions(tt, mon, wed).Where(x => x.Code == "CS2201"));
        // The raw clash list is the university's, as before.
        Assert.Equal(before.GetProperty("clashes").GetRawText(), tt.GetProperty("clashes").GetRawText());

        // One reminder, before the new time, opening the class where it is now.
        Assert.Empty(await Due(w, me, mon, 8, 0));
        var due = Assert.Single(await Due(w, me, wed, 10, 0));
        Assert.Equal(("CS2201 in 15 min", $"/?session={course}.{Iso(wed)}.10:00", $"class-{course}-{Iso(wed)}-1000"), (due.Title, due.Url, due.Tag));

        // The calendar entry moves in place: the same id, at the new time, saying what happened. Nothing is left on Monday.
        var (ics, text) = await Feeds(w, student, mon, 3);
        var moved = Assert.Single(Events(ics), e => e["UID"] == $"class-{cs}-{meeting}@classy");
        Assert.Equal(($"{wed:yyyyMMdd}T100000", "Data Structures · Lecture", "a 102"), (moved["DTSTART"], moved["SUMMARY"], moved["LOCATION"]));
        Assert.Contains($"Moved from {Day(mon)} 08:00 by {TeachWorld.Teacher2Name}", moved["DESCRIPTION"]);
        Assert.EndsWith("Swap", moved["DESCRIPTION"]);
        Assert.Equal(Events(feedsBefore.Ics).Count, Events(ics).Count);
        Assert.DoesNotContain(Events(ics), e => e["DTSTART"] == $"{mon:yyyyMMdd}T080000");
        Assert.Contains("- No classes", DayOf(text, mon));
        Assert.Contains($"- 10:00–11:30  CS2201 Data Structures · Lecture · Room a 102 · Section 1 · {TeachWorld.Teacher2Name} · moved from Mon 08:00 · Swap", DayOf(text, wed));

        // Rooms: the old one is free then; the typed one is taken at the new time, however it was spelled.
        Assert.Contains("B105", await FreeRoomsAt(student, mon, "08:30"));
        Assert.DoesNotContain("A102", await FreeRoomsAt(student, wed, "10:30"));
        Assert.DoesNotContain("a 102", await FreeRoomsAt(student, wed, "12:00"));
        // A note can be pinned to the day it moved to.
        await Json(await student.PostAsJsonAsync("/api/me/notes", new { courseId = course, text = "Bring laptop", date = Iso(wed) }));
        // Groups: free on Monday morning, busy on Wednesday at ten.
        var friends = (await Json(await student.PostAsJsonAsync("/api/me/groups", new { name = "Study", kind = "friends", displayName = "Me" }))).GetProperty("id").GetInt32();
        var week = (await Json(await student.GetAsync($"/api/me/groups/{friends}/week?start={Iso(w.Date(0, 0))}"))).GetProperty("days").EnumerateArray().ToList();
        Assert.Equal("08:00", week.Single(d => d.GetProperty("date").GetString() == Iso(mon)).GetProperty("free")[0].GetProperty("start").GetString());
        Assert.DoesNotContain(week.Single(d => d.GetProperty("date").GetString() == Iso(wed)).GetProperty("free").EnumerateArray(),
            f => string.CompareOrdinal(f.GetProperty("start").GetString(), "10:00") <= 0 && string.CompareOrdinal(f.GetProperty("end").GetString(), "11:30") >= 0);

        // Undo restores every one of them (the note stays: it is the student's).
        await Json(await w.Teacher2.Undo(group));
        Assert.Equal(Sessions(before, w.Date(-5, 0), w.Date(9, 0)), Sessions(await Timetable(student), w.Date(-5, 0), w.Date(9, 0)));
        Assert.Empty((await Timetable(student)).GetProperty("changes").EnumerateArray());
        Assert.Equal(feedsBefore, await Feeds(w, student, mon, 3));
        Assert.Single(await Due(w, me, mon, 8, 0));
        Assert.Empty(await Due(w, me, wed, 10, 0));
        Assert.DoesNotContain("B105", await FreeRoomsAt(student, mon, "08:30"));
        Assert.Contains("A102", await FreeRoomsAt(student, wed, "10:30"));

        // Moved again. After the day: it can be marked missed where it was held, not where it was listed.
        await w.Teacher2.Made(body);
        w.GoTo(wed, 13, 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await Missed(student, course, mon, "08:00")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Missed(student, course, wed, "10:00")).StatusCode);
        Assert.Equal([(course, Iso(wed), "10:00")], await Absent(student));
    }

    [Fact]
    public async Task A_room_change_shows_the_class_once_and_follows_the_universitys_end_time()
    {
        using var w = await TeachWorld.Create();
        var student = w.Students[2];
        var cs = await w.Section("CS2201", "2");
        var tue = w.Date(0, 2);
        var meeting = await w.Db(db => db.Meetings.Where(m => m.SectionId == cs && m.Date == tue).Select(m => m.Id).SingleAsync());
        var before = await Timetable(student);

        var group = await w.Teacher2.Made(new { kind = "room", sectionId = cs, date = Iso(tue), start = "08:00", room = "A102" });
        var tt = await Timetable(student);
        Assert.Equal([("CS2201", tue, "08:00", "09:30", "A102")], Sessions(tt, tue, tue).Where(x => x.Code == "CS2201"));
        var change = Assert.Single(tt.GetProperty("changes").EnumerateArray());
        Assert.Equal(("room", "B105", "A102", "08:00"), (change.GetProperty("kind").GetString(), change.GetProperty("from").GetProperty("room").GetString(),
            change.GetProperty("to").GetProperty("room").GetString(), change.GetProperty("to").GetProperty("start").GetString()));
        var (ics, text) = await Feeds(w, student, tue, 1);
        var roomed = Assert.Single(Events(ics), e => e["UID"] == $"class-{cs}-{meeting}@classy");
        Assert.Equal(("A102", $"{tue:yyyyMMdd}T080000"), (roomed["LOCATION"], roomed["DTSTART"]));
        Assert.Contains($"Room changed by {TeachWorld.Teacher2Name}", roomed["DESCRIPTION"]);
        Assert.Single(Events(ics), e => e["DTSTART"] == $"{tue:yyyyMMdd}T080000");
        Assert.Contains("Room A102", DayOf(text, tue));
        Assert.Contains("room changed", DayOf(text, tue));
        Assert.Contains("B105", await FreeRoomsAt(student, tue, "08:30"));
        Assert.DoesNotContain("A102", await FreeRoomsAt(student, tue, "08:30"));

        // The university then lengthens the class: the re-roomed session follows, since its time was never the teacher's.
        await w.Db(db => db.Meetings.Where(m => m.Id == meeting).ExecuteUpdateAsync(u => u.SetProperty(m => m.End, new TimeOnly(10, 0))));
        tt = await Timetable(student);
        Assert.Equal([("CS2201", tue, "08:00", "10:00", "A102")], Sessions(tt, tue, tue).Where(x => x.Code == "CS2201"));
        Assert.Equal(("10:00", "10:00"), (tt.GetProperty("changes")[0].GetProperty("to").GetProperty("end").GetString(), tt.GetProperty("changes")[0].GetProperty("from").GetProperty("end").GetString()));
        Assert.DoesNotContain("A102", await FreeRoomsAt(student, tue, "09:45"));
        await w.Db(db => db.Meetings.Where(m => m.Id == meeting).ExecuteUpdateAsync(u => u.SetProperty(m => m.End, new TimeOnly(9, 30))));

        await Json(await w.Teacher2.Undo(group));
        Assert.Equal(before.GetRawText(), (await Timetable(student)).GetRawText());
        Assert.DoesNotContain("B105", await FreeRoomsAt(student, tue, "08:30"));
    }

    [Fact]
    public async Task An_extra_class_is_a_class_everywhere()
    {
        using var w = await TeachWorld.Create();
        var student = w.Students[0];
        var me = w.StudentRowIds[0];
        var cs = await w.Section("CS2201", "1");
        var course = await w.CourseOf(cs);
        var thu = w.Date(0, 4);
        await Remind(w, me);
        var before = await Timetable(student);

        var group = await w.Teacher2.Made(new { kind = "extra", sectionIds = new[] { cs }, newDate = Iso(thu), newStart = "15:00", newEnd = "16:30", room = "B105", type = "lab", note = "Revision" });
        var id = (await w.Live()).Single().Id;
        var tt = await Timetable(student);
        var put = Meetings(tt, "CS2201").Single(m => m.TryGetProperty("changeId", out _));
        Assert.Equal((4, "15:00", "16:30", "B105", Iso(thu), id, "lab"), (put.GetProperty("day").GetInt32(), put.GetProperty("start").GetString(), put.GetProperty("end").GetString(),
            put.GetProperty("room").GetString(), put.GetProperty("date").GetString(), put.GetProperty("changeId").GetInt32(), put.GetProperty("type").GetString()));
        var change = Assert.Single(tt.GetProperty("changes").EnumerateArray());
        Assert.Equal(("extra", "lab", JsonValueKind.Null, "15:00"), (change.GetProperty("kind").GetString(), change.GetProperty("type").GetString(), change.GetProperty("from").ValueKind, change.GetProperty("to").GetProperty("start").GetString()));
        Assert.Contains(Sessions(tt, thu, thu), x => x == ("CS2201", thu, "15:00", "16:30", "B105"));

        Assert.Equal("CS2201 in 15 min", Assert.Single(await Due(w, me, thu, 15, 0)).Title);
        var (ics, text) = await Feeds(w, student, thu, 1);
        var extra = Assert.Single(Events(ics), e => e["UID"] == $"change-{id}@classy");
        // A lab because the teacher said so, not because of its room.
        Assert.Equal(("Data Structures · Lab", $"{thu:yyyyMMdd}T150000"), (extra["SUMMARY"], extra["DTSTART"]));
        Assert.Contains($"Extra class added by {TeachWorld.Teacher2Name}", extra["DESCRIPTION"]);
        Assert.Contains("- 15:00–16:30  CS2201 Data Structures · Lab · Room B105", DayOf(text, thu));
        Assert.Contains("extra class · Revision", DayOf(text, thu));
        Assert.DoesNotContain("B105", await FreeRoomsAt(student, thu, "15:30"));

        await Json(await w.Teacher2.Undo(group));
        Assert.Equal(before.GetRawText(), (await Timetable(student)).GetRawText());
        Assert.Empty(await Due(w, me, thu, 15, 0));
        Assert.Contains("B105", await FreeRoomsAt(student, thu, "15:30"));

        // Students can mark it missed like any class.
        await w.Teacher2.Made(new { kind = "extra", sectionIds = new[] { cs }, newDate = Iso(thu), newStart = "15:00", newEnd = "16:30", room = "B105", type = "lab" });
        w.GoTo(thu, 17, 0);
        Assert.Equal(HttpStatusCode.NoContent, (await Missed(student, course, thu, "15:00")).StatusCode);
        Assert.Equal([(course, Iso(thu), "15:00")], await Absent(student));
    }

    [Fact]
    public async Task Two_sessions_swapped_with_each_other_both_show_and_an_extra_class_fits_on_a_cancelled_slot()
    {
        using var w = await TeachWorld.Create();
        var (cs1, cs2) = (await w.Section("CS2201", "1"), await w.Section("CS2201", "2"));
        var course = await w.CourseOf(cs1);
        var (mon, tue) = (w.Date(0, 1), w.Date(0, 2));
        await w.AddMeeting(cs1, mon, 0, "10:00", "11:30", "B106");

        // The 08:00 session goes to 10:00 and the 10:00 one to 08:00, each keeping its room.
        await w.Teacher2.Made(new { kind = "move", sectionId = cs1, date = Iso(mon), start = "08:00", newDate = Iso(mon), newStart = "10:00", room = "B105" });
        await w.Teacher2.Made(new { kind = "move", sectionId = cs1, date = Iso(mon), start = "10:00", newDate = Iso(mon), newStart = "08:00", room = "B106" });
        var tt = await Timetable(w.Students[0]);
        Assert.Equal([("CS2201", mon, "08:00", "09:30", "B106"), ("CS2201", mon, "10:00", "11:30", "B105")], Sessions(tt, mon, mon));
        Assert.Equal(2, tt.GetProperty("changes").GetArrayLength());
        var (ics, _) = await Feeds(w, w.Students[0], mon, 1);
        Assert.Equal(2, Events(ics).Count(e => e["DTSTART"].StartsWith($"{mon:yyyyMMdd}")));

        // A session missed in a swapped slot is a missed session, though a class was also moved away from that slot.
        w.GoTo(mon, 12, 0);
        Assert.Equal(HttpStatusCode.NoContent, (await Missed(w.Students[0], course, mon, "08:00")).StatusCode);
        Assert.Equal([(course, Iso(mon), "08:00")], await Absent(w.Students[0]));

        // A cancelled slot is free for an extra class, which shows.
        var teacher = await w.Fresh(TeachWorld.Teacher2Email);
        await teacher.Made(new { kind = "cancel", sectionId = cs2, date = Iso(tue), start = "08:00" });
        await teacher.Made(new { kind = "extra", sectionIds = new[] { cs2 }, newDate = Iso(tue), newStart = "08:00", newEnd = "09:00", room = "B105", type = "tutorial" });
        Assert.Equal([("CS2201", tue, "08:00", "09:00", "B105")], Sessions(await Timetable(w.Students[2]), tue, tue));
        w.GoTo(tue, 12, 0);
        Assert.Equal(HttpStatusCode.NoContent, (await Missed(w.Students[2], course, tue, "08:00")).StatusCode);
        Assert.Single(await Absent(w.Students[2]));
    }

    [Fact]
    public async Task The_timetable_the_schedule_and_the_rooms_agree_on_every_date()
    {
        using var w = await TeachWorld.Create();
        var ids = new Dictionary<string, int>();
        foreach (var (code, number) in new[] { ("BE1101", "1"), ("IBSM1101", "1"), ("LOG1101", "1"), ("BE1101", "2"), ("CS2201", "1"), ("CS2201", "2"), ("CS2201-L", "1"), ("LS1001", "1") })
            ids[$"{code}/{number}"] = await w.Section(code, number);
        var (cs1, cs2) = (ids["CS2201/1"], ids["CS2201/2"]);
        // Weekly classes too, a day off in the middle of them, and every section picked by someone.
        var weekly = await w.AddMeeting(cs2, null, 4, "12:00", "13:30", "B200");
        await w.AddMeeting(ids["LS1001/1"], null, 3, "14:00", "15:00", "Online");
        await w.AddMeeting(cs1, w.Date(0, 1), 0, "10:00", "11:30", "B106");
        await Json(await w.Owner.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/days-off", new { date = Iso(w.Date(1, 4)), name = "National Day" }));
        await w.Db(async db =>
        {
            foreach (var key in new[] { "CS2201-L/1", "LS1001/1" })
                db.Picks.Add(new Pick { StudentId = w.StudentRowIds[2], CourseId = await db.Sections.Where(x => x.Id == ids[key]).Select(x => x.CourseId).SingleAsync(), SectionId = ids[key] });
            db.Picks.Add(new Pick { StudentId = w.StudentRowIds[1], CourseId = await db.Sections.Where(x => x.Id == ids["LS1001/1"]).Select(x => x.CourseId).SingleAsync(), SectionId = ids["LS1001/1"] });
            await db.SaveChangesAsync();
        });
        var third = await w.SignInStudent("26-7004");
        await w.Db(async db =>
        {
            var s = await db.Students.SingleAsync(x => x.UniversityId == "26-7004");
            (s.MajorId, s.Year, s.SetupSemesterId, s.SetupStep, s.SetupCompletedAt) = ((await db.Majors.FirstAsync(m => m.Code == "CS")).Id, 1, w.Semester, "done", w.Time.GetUtcNow());
            db.Picks.Add(new Pick { StudentId = s.Id, CourseId = await db.Sections.Where(x => x.Id == ids["BE1101/2"]).Select(x => x.CourseId).SingleAsync(), SectionId = ids["BE1101/2"] });
            await db.SaveChangesAsync();
        });
        var students = w.Students.Append(third).ToList();
        var rowIds = await w.Db(db => db.Students.Where(x => x.UniversityId.StartsWith("26-70")).OrderBy(x => x.UniversityId).Select(x => x.Id).ToListAsync());
        var stranger = await w.SignInTeacher("guest.lecturer@gutech.edu.om");
        foreach (var id in ids.Values) await stranger.AddSection(id);

        async Task Check()
        {
            var (from, to) = (w.Date(-5, 0), w.Date(11, 6));
            var drawn = new List<(string Code, DateOnly Date, string Start, string End, string Room)>();
            for (var i = 0; i < students.Count; i++)
            {
                var sessions = Sessions(await Timetable(students[i]), from, to);
                var items = await w.Db(async db =>
                {
                    var semester = await db.Semesters.SingleAsync(x => x.Id == w.Semester);
                    var span = await SemesterSpan.ForAsync(db, semester, default);
                    var all = await StudentSchedule.ForAsync(db, semester.Id, [rowIds[i]], slots: false, planner: false, default);
                    var on = new List<(string, DateOnly, string, string, string)>();
                    for (var d = from; d <= to; d = d.AddDays(1))
                        on.AddRange(all.Where(x => span.Happens(x, d)).Select(x => (x.Code, d, Timeslots.Format(x.Start), Timeslots.Format(x.End), x.Room)));
                    return on.OrderBy(x => x.Item2).ThenBy(x => x.Item3).ThenBy(x => x.Item1).ToList();
                });
                Assert.Equal(items, sessions);
                drawn.AddRange(sessions);
            }
            Assert.NotEmpty(drawn);
            // Between them the students have every section, so the rooms their sessions take are the rooms taken.
            await w.Db(async db =>
            {
                var semester = await db.Semesters.SingleAsync(x => x.Id == w.Semester);
                var config = w.Services.GetRequiredService<IConfiguration>();
                for (var d = from; d <= to; d = d.AddDays(1))
                {
                    var taken = (await FreeRooms.OccupiedAsync(db, semester, d, config, default)).Occupied.Where(o => !string.IsNullOrWhiteSpace(o.Room))
                        .Select(o => (ClassChanges.RoomKey(o.Room), Timeslots.Format(o.Start), Timeslots.Format(o.End))).Distinct().Order().ToList();
                    var held = drawn.Where(x => x.Date == d && !string.IsNullOrWhiteSpace(x.Room))
                        .Select(x => (ClassChanges.RoomKey(x.Room), x.Start, x.End)).Distinct().Order().ToList();
                    Assert.Equal(held, taken);
                }
            });
        }

        await Check();
        var be = ids["BE1101/1"];
        // Every kind of change, on dated and weekly classes, some of them on top of each other.
        await w.Teacher1.Made(new { kind = "cancel", sectionId = be, date = Iso(w.Date(0, 0)), start = "08:00", sectionIds = new[] { be, ids["IBSM1101/1"] } });
        await w.Teacher1.Made(new { kind = "move", sectionId = be, date = Iso(w.Date(1, 0)), start = "08:00", newDate = Iso(w.Date(1, 5)), newStart = "09:00", room = "" });
        await w.Teacher2.Made(new { kind = "move", sectionId = cs1, date = Iso(w.Date(0, 1)), start = "08:00", newDate = Iso(w.Date(0, 1)), newStart = "10:00", room = "B105" });
        await w.Teacher2.Made(new { kind = "move", sectionId = cs1, date = Iso(w.Date(0, 1)), start = "10:00", newDate = Iso(w.Date(0, 1)), newStart = "08:00", room = "b 106" });
        await w.Teacher2.Made(new { kind = "room", sectionId = cs2, date = Iso(w.Date(0, 2)), start = "08:00", room = "A 101" });
        await w.Teacher2.Made(new { kind = "cancel", sectionId = cs2, date = Iso(w.Date(0, 4)), start = "12:00" });
        await w.Teacher2.Made(new { kind = "move", sectionId = cs2, date = Iso(w.Date(2, 4)), start = "12:00", newDate = Iso(w.Date(10, 1)), newStart = "16:00", room = "Lab L2" });
        await w.Teacher2.Made(new { kind = "room", sectionId = cs2, date = Iso(w.Date(3, 4)), start = "12:00", room = "" });
        await w.Teacher2.Made(new { kind = "extra", sectionIds = new[] { cs1, cs2 }, newDate = Iso(w.Date(1, 4)), newStart = "09:00", newEnd = "10:00", room = "B105", type = "tutorial" });
        await w.Teacher2.Made(new { kind = "extra", sectionIds = new[] { cs2 }, newDate = Iso(w.Date(0, 4)), newStart = "12:00", newEnd = "13:00", room = "B200", type = "lecture" });
        await stranger.Made(new { kind = "cancel", sectionId = ids["LS1001/1"], date = Iso(w.Date(0, 3)), start = "14:00" });
        await stranger.Made(new { kind = "move", sectionId = ids["CS2201-L/1"], date = Iso(w.Date(0, 2)), start = "11:30", newDate = Iso(w.Date(0, 2)), newStart = "08:00", room = "B105" });
        await stranger.Made(new { kind = "room", sectionId = ids["BE1101/2"], date = Iso(w.Date(1, 1)), start = "09:45", room = "Online" });
        Assert.Equal(17, (await w.Live()).Count);
        await Check();

        // A weekly class moved on one date keeps that date's calendar id.
        var (ics, _) = await Feeds(w, w.Students[2], w.Date(0, 0), 1);
        var moved = Assert.Single(Events(ics), e => e["UID"] == $"class-{cs2}-{weekly}-{w.Date(2, 4):yyyyMMdd}@classy");
        Assert.Equal($"{w.Date(10, 1):yyyyMMdd}T160000", moved["DTSTART"]);
        Assert.Equal("Cancelled: Data Structures · Lecture", Assert.Single(Events(ics), e => e["UID"] == $"class-{cs2}-{weekly}-{w.Date(0, 4):yyyyMMdd}@classy")["SUMMARY"]);
        // A session whose university meeting is gone (the upload removed it) draws nothing, anywhere.
        await w.Db(db => db.Meetings.Where(m => m.SectionId == cs1 && m.Date == w.Date(0, 1) && m.Start == new TimeOnly(8, 0)).ExecuteDeleteAsync());
        await Check();
        Assert.DoesNotContain(Sessions(await Timetable(w.Students[0]), w.Date(0, 1), w.Date(0, 1)), x => x.Start == "10:00" && x.Room == "B105");

        // Undo them all: back to the university's timetable.
        var owner = TeachWorld.ViewAs(w.Owner, TeacherKeys.ForName(TeachWorld.Teacher2Name));
        foreach (var group in (await w.Live()).Select(c => c.GroupId).Distinct()) await Json(await owner.Undo(group));
        Assert.Empty(await w.Live());
        await Check();
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task The_teacher_page_shows_this_weeks_classes_with_what_changed()
    {
        using var w = await TeachWorld.Create();
        var (be, cs1, cs2) = (await w.Section("BE1101", "1"), await w.Section("CS2201", "1"), await w.Section("CS2201", "2"));
        var (sun, mon, tue, wed, thu) = (w.Date(0, 0), w.Date(0, 1), w.Date(0, 2), w.Date(0, 3), w.Date(0, 4));
        // The week of the classes has begun: "this week" is theirs.
        w.GoTo(sun, 6, 0);
        var (t1, t2) = (await w.Fresh(TeachWorld.Teacher1Email), await w.Fresh(TeachWorld.Teacher2Email));
        await t1.Made(new { kind = "room", sectionId = be, date = Iso(sun), start = "08:00", room = "B202", note = "Projector" });
        await t2.Made(new { kind = "cancel", sectionId = cs1, date = Iso(mon), start = "08:00", note = "Away" });
        await t2.Made(new { kind = "move", sectionId = cs2, date = Iso(tue), start = "08:00", newDate = Iso(thu), newStart = "10:00", room = "" });
        await t2.Made(new { kind = "extra", sectionIds = new[] { cs1, cs2 }, newDate = Iso(wed), newStart = "15:00", newEnd = "16:00", room = "B105", type = "lab" });

        static List<(string Codes, string Date, string Start, string Room, string Type, string? Status, string? Note)> Rows(JsonElement page) =>
            page.GetProperty("classes").EnumerateArray().Select(c => (string.Join("+", c.GetProperty("codes").EnumerateArray().Select(x => x.GetString())), c.GetProperty("date").GetString()!,
                c.GetProperty("start").GetString()!, c.GetProperty("room").GetString()!, c.GetProperty("type").GetString()!, c.GetProperty("status").GetString(), c.GetProperty("note").GetString())).ToList();
        var student = w.Students[2];
        // A cancelled class stays in its place; a moved one is listed where it is now; one real class is one row.
        var second = await Json(await student.GetAsync($"/api/teachers/{TeacherKeys.ForName(TeachWorld.Teacher2Name)}"));
        Assert.Equal((Iso(sun), Iso(w.Date(0, 6))), (second.GetProperty("week").GetProperty("from").GetString(), second.GetProperty("week").GetProperty("to").GetString()));
        Assert.Equal([("CS2201", Iso(mon), "08:00", "B105", "lecture", "cancelled", "Away"), ("CS2201", Iso(wed), "15:00", "B105", "lab", "extra", null), ("CS2201", Iso(thu), "10:00", "", "lecture", "moved", null)], Rows(second));
        var first = await Json(await student.GetAsync($"/api/teachers/{TeacherKeys.ForName(TeachWorld.Teacher1Name)}"));
        Assert.Equal([("BE1101+IBSM1101+LOG1101", Iso(sun), "08:00", "B202", "lecture", "room", "Projector")], Rows(first));

        // What the teacher's reminders and calendar will be made from: the same sessions, one per real class.
        var off = new List<(int TeacherId, StudentSchedule.Item Item)>();
        var items = await w.Db(async db =>
        {
            var teacher = await db.Teachers.SingleAsync(t => t.Id == w.Teacher2Id);
            return (await TeacherSchedule.ItemsAsync(db, await db.Semesters.SingleAsync(x => x.Id == w.Semester), [teacher], slots: true, planner: false, default, off))[teacher.Id];
        });
        var span = await w.Db(async db => await SemesterSpan.ForAsync(db, await db.Semesters.SingleAsync(x => x.Id == w.Semester), default));
        var week = Enumerable.Range(0, 7).Select(i => sun.AddDays(i)).SelectMany(d => items.Where(i => span.Happens(i, d)).Select(i => (i.Code, d, i.Start, i.Room, i.ChangeKind))).ToList();
        Assert.Equal([("CS2201", wed, new TimeOnly(15, 0), "B105", "extra"), ("CS2201", thu, new TimeOnly(10, 0), "", "move")], week);
        Assert.Equal([("cancel", mon), ("move", tue)], off.Select(o => (o.Item.ChangeKind!, o.Item.Date!.Value)).OrderBy(o => o.Item2));
    }
}
