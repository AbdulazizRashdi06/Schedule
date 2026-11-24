using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
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
}
