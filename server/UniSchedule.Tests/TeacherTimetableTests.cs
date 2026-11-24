using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// The teaching timetable: the sections under a teacher's names, plus the ones they added, minus the ones they hid;
/// one real class under several codes as one block; own slots and office hours beside them.
/// </summary>
public class TeacherTimetableTests
{
    static async Task<JsonElement> Timetable(HttpClient c) => await Json(await c.GetAsync("/api/teach/timetable"));
    static async Task<JsonElement> Sections(HttpClient c, string? q = null) =>
        await Json(await c.GetAsync("/api/teach/sections" + (q is null ? "" : "?q=" + Uri.EscapeDataString(q))));
    static Task<HttpResponseMessage> Tick(HttpClient c, int sectionId, bool on) => c.PutAsJsonAsync($"/api/teach/sections/{sectionId}", new { on });

    /// <summary>"BE1101/1" for each class on a timetable, in the order sent.</summary>
    static List<string> Classes(JsonElement timetable) => timetable.GetProperty("classes").EnumerateArray()
        .Select(c => $"{c.GetProperty("code").GetString()}/{c.GetProperty("section").GetProperty("number").GetString()}").ToList();

    static JsonElement Class(JsonElement timetable, string code, string number = "1") => timetable.GetProperty("classes").EnumerateArray()
        .Single(c => c.GetProperty("code").GetString() == code && c.GetProperty("section").GetProperty("number").GetString() == number);

    static List<string> Mine(JsonElement sections) => sections.GetProperty("mine").EnumerateArray()
        .Select(r => $"{r.GetProperty("code").GetString()}/{r.GetProperty("number").GetString()}:{r.GetProperty("state").GetString()}").ToList();

    [Fact]
    public async Task The_timetable_is_the_named_sections_plus_added_minus_hidden()
    {
        using var w = await TeachWorld.Create();
        var tt = await Timetable(w.Teacher1);
        Assert.Equal(["BE1101/1", "IBSM1101/1", "LOG1101/1"], Classes(tt));
        Assert.All(tt.GetProperty("classes").EnumerateArray(), c => Assert.Equal((true, false), (c.GetProperty("own").GetBoolean(), c.GetProperty("added").GetBoolean())));
        Assert.Equal(["CS2201/1", "CS2201/2"], Classes(await Timetable(w.Teacher2)));
        Assert.Equal(w.Semester, tt.GetProperty("semester").GetProperty("id").GetInt32());

        // Adding a section under someone else's name: on the timetable at once.
        var other = await w.Section("BE1101", "2");
        var row = await Json(await Tick(w.Teacher1, other, true));
        Assert.Equal((other, "BE1101", "2", TeachWorld.UnclaimedName, "Mon 09:45", "added"),
            (row.GetProperty("id").GetInt32(), row.GetProperty("code").GetString(), row.GetProperty("number").GetString(), row.GetProperty("instructor").GetString(),
                row.GetProperty("when").GetString(), row.GetProperty("state").GetString()));
        tt = await Timetable(w.Teacher1);
        Assert.Equal(["BE1101/1", "BE1101/2", "IBSM1101/1", "LOG1101/1"], Classes(tt));
        Assert.Equal((false, true, 0), (Class(tt, "BE1101", "2").GetProperty("own").GetBoolean(), Class(tt, "BE1101", "2").GetProperty("added").GetBoolean(),
            Class(tt, "BE1101", "2").GetProperty("students").GetInt32()));
        var added = await w.Db(db => db.TeacherSections.AsNoTracking().SingleAsync());
        Assert.Equal((w.Teacher1Id, w.Semester, (int?)other, "BE1101", "2", TeacherSection.AddedMode, (string?)null),
            (added.TeacherId, added.SemesterId, added.SectionId, added.CourseCode, added.SectionNumber, added.Mode, added.By));
        // Ticking it again changes nothing.
        await Json(await Tick(w.Teacher1, other, true));
        Assert.Equal(1, await w.Db(db => db.TeacherSections.CountAsync()));

        // Hiding a section under their name: gone for them only, and still theirs in "Edit my classes".
        var ibsm = await w.Section("IBSM1101", "1");
        Assert.Equal("hidden", (await Json(await Tick(w.Teacher1, ibsm, false))).GetProperty("state").GetString());
        Assert.Equal(["BE1101/1", "BE1101/2", "LOG1101/1"], Classes(await Timetable(w.Teacher1)));
        Assert.Equal(["BE1101/1:own", "BE1101/2:added", "IBSM1101/1:hidden", "LOG1101/1:own"], Mine(await Sections(w.Teacher1)));
        // "May change" keeps the hidden one: the named teacher can always undo a change to it.
        var may = await w.Db(async db => (await TeacherSchedule.SectionsAsync(db, w.Semester, await db.Teachers.SingleAsync(t => t.Id == w.Teacher1Id),
            [TeacherNames.NameKey(TeachWorld.Teacher1Name)], CancellationToken.None)).Select(t => (t.Section.Id, t.Own, t.Added, t.Hidden)).ToList());
        Assert.Contains((ibsm, true, false, true), may);
        Assert.Contains((other, false, true, false), may);
        Assert.Equal(4, may.Count);
        // The student who takes it still has it.
        Assert.Contains("IBSM1101/1", Classes(await Json(await w.Students[1].GetAsync("/api/me/timetable"))));

        // And back: the hidden one returns, the added one goes.
        Assert.Equal("own", (await Json(await Tick(w.Teacher1, ibsm, true))).GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, (await Json(await Tick(w.Teacher1, other, false))).GetProperty("state").ValueKind);
        Assert.Equal(["BE1101/1", "IBSM1101/1", "LOG1101/1"], Classes(await Timetable(w.Teacher1)));
        Assert.Equal(0, await w.Db(db => db.TeacherSections.CountAsync()));
        // Unticking what was never ticked is fine too.
        await Json(await Tick(w.Teacher1, other, false));
        await Json(await Tick(w.Teacher1, ibsm, true));
        Assert.Equal(0, await w.Db(db => db.TeacherSections.CountAsync()));

        // A section must be on this semester's timetable, and the body must say on or off.
        Assert.Equal(HttpStatusCode.NotFound, (await Tick(w.Teacher1, 9999, true)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Teacher1.PutAsJsonAsync($"/api/teach/sections/{other}", new { })).StatusCode);
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task Several_names_give_the_sections_of_each()
    {
        using var w = await TeachWorld.Create();
        // Staff attach a second name (however it is spelt).
        await w.Db(async db =>
        {
            db.TeacherNames.Add(new TeacherName
            {
                TeacherId = w.Teacher1Id, Name = "MARYAM  nasser", NameKey = TeacherNames.NameKey(TeachWorld.UnclaimedName), State = TeacherName.Approved,
                How = TeacherName.ByStaff, CreatedAt = w.Time.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        });
        var tt = await Timetable(w.Teacher1);
        Assert.Equal(["BE1101/1", "BE1101/2", "IBSM1101/1", "LOG1101/1"], Classes(tt));
        // Two sections of one course are two entries.
        Assert.Equal(2, tt.GetProperty("classes").EnumerateArray().Count(c => c.GetProperty("courseId").GetInt32() == Class(tt, "BE1101").GetProperty("courseId").GetInt32()));
        Assert.True(Class(tt, "BE1101", "2").GetProperty("own").GetBoolean());
        // Under their name now, so off hides it rather than removing anything.
        Assert.Equal("hidden", (await Json(await Tick(w.Teacher1, await w.Section("BE1101", "2"), false))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_teacher_waiting_for_a_name_sees_only_what_they_added()
    {
        using var w = await TeachWorld.Create();
        const string email = "noor.saleh@gutech.edu.om";
        var c = await w.SignInTeacher(email);
        Assert.Equal("pick-name", (await Json(await c.GetAsync("/api/teach/me"))).GetProperty("status").GetString());
        Assert.Equal("pending", (await Json(await c.PostAsJsonAsync("/api/teach/names", new { name = TeachWorld.UnclaimedName }))).GetProperty("status").GetString());

        // The name they asked for gives nothing yet.
        var tt = await Timetable(c);
        Assert.Empty(tt.GetProperty("classes").EnumerateArray());
        Assert.Empty((await Sections(c)).GetProperty("mine").EnumerateArray());

        // What they add is theirs at once, the class they are waiting for included.
        var waitingFor = await w.Section("BE1101", "2");
        Assert.Equal("added", (await Json(await Tick(c, waitingFor, true))).GetProperty("state").GetString());
        await Json(await Tick(c, await w.Section("CS2201", "1"), true));
        tt = await Timetable(c);
        Assert.Equal(["BE1101/2", "CS2201/1"], Classes(tt));
        Assert.All(tt.GetProperty("classes").EnumerateArray(), x => Assert.Equal((false, true), (x.GetProperty("own").GetBoolean(), x.GetProperty("added").GetBoolean())));
        Assert.Equal(2, Class(tt, "CS2201").GetProperty("students").GetInt32());
        // The other teacher's timetable is untouched.
        Assert.Equal(["CS2201/1", "CS2201/2"], Classes(await Timetable(w.Teacher2)));
    }

    [Fact]
    public async Task A_teaching_assistant_with_no_names_starts_with_an_empty_timetable()
    {
        using var w = await TeachWorld.Create();
        await w.Services.AddTeachingStudent(w.StudentRowIds[1], "Lab Helper");
        var assistant = w.Students[1];
        var me = await Json(await assistant.GetAsync("/api/teach/me"));
        Assert.Equal(("student", "none"), (me.GetProperty("kind").GetString(), me.GetProperty("status").GetString()));
        var tt = await Timetable(assistant);
        Assert.Empty(tt.GetProperty("classes").EnumerateArray());
        Assert.Empty(tt.GetProperty("slots").EnumerateArray());
        Assert.Empty(tt.GetProperty("officeHours").EnumerateArray());
        // Their own classes as a student are not their teaching timetable, and stay where they were.
        Assert.Equal(["CS2201/1", "IBSM1101/1"], Classes(await Json(await assistant.GetAsync("/api/me/timetable"))));

        // They add the class they help with.
        await Json(await Tick(assistant, await w.Section("CS2201-L", "1"), true));
        Assert.Equal(["CS2201-L/1"], Classes(await Timetable(assistant)));
    }

    [Fact]
    public async Task The_shape_is_the_students_with_sizes_in_place_of_notes()
    {
        using var w = await TeachWorld.Create();
        // A note a student wrote on the class: teachers never see anything of it.
        var be = await w.Section("BE1101", "1");
        var courseId = await w.Db(db => db.Sections.Where(s => s.Id == be).Select(s => s.CourseId).SingleAsync());
        await Json(await w.Students[0].PostAsJsonAsync("/api/me/notes", new { courseId, text = "Bring the calculator", isPublic = true }));

        var student = await Json(await w.Students[0].GetAsync("/api/me/timetable"));
        var raw = await (await w.Teacher1.GetAsync("/api/teach/timetable")).Content.ReadAsStringAsync();
        var teacher = JsonDocument.Parse(raw).RootElement;

        // Every key the student's timetable has, with the same kind of value; the teacher's extras beside them.
        var studentKeys = student.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["semester", "span", "daysOff", "plannerEvents", "slots", "classes", "needsSection", "clashes", "changes"], studentKeys);
        Assert.Equal([.. studentKeys, "officeHours", "mergeSizes", "removed"], teacher.EnumerateObject().Select(p => p.Name));
        foreach (var key in studentKeys)
            Assert.Equal(student.GetProperty(key).ValueKind, teacher.GetProperty(key).ValueKind);
        Assert.Equal(student.GetProperty("span").GetRawText(), teacher.GetProperty("span").GetRawText());
        Assert.Equal(student.GetProperty("semester").GetRawText(), teacher.GetProperty("semester").GetRawText());
        Assert.Empty(teacher.GetProperty("needsSection").EnumerateArray());
        Assert.Empty(teacher.GetProperty("clashes").EnumerateArray());
        Assert.Empty(teacher.GetProperty("changes").EnumerateArray());

        var theirs = Class(student, "BE1101");
        var mine = Class(teacher, "BE1101");
        Assert.Equal(["courseId", "code", "name", "type", "credits", "section", "changed", "notes", "teachers"], theirs.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["courseId", "code", "name", "type", "credits", "section", "changed", "students", "own", "added", "teachers"], mine.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, theirs.GetProperty("notes").GetProperty("classWide").GetInt32());
        foreach (var key in new[] { "courseId", "code", "name", "type", "credits", "changed", "teachers" })
            Assert.Equal(theirs.GetProperty(key).GetRawText(), mine.GetProperty(key).GetRawText());
        // The section is the same but for the merge key on each meeting.
        Assert.Equal(theirs.GetProperty("section").EnumerateObject().Select(p => p.Name), mine.GetProperty("section").EnumerateObject().Select(p => p.Name));
        var meeting = mine.GetProperty("section").GetProperty("meetings")[0];
        Assert.Equal(["day", "start", "end", "room", "date", "excludedDates", "merge"], meeting.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["day", "start", "end", "room", "date", "excludedDates"], theirs.GetProperty("section").GetProperty("meetings")[0].EnumerateObject().Select(p => p.Name));

        // Nothing of students' notes, and nobody's university ID, anywhere in what a teacher gets.
        Assert.DoesNotContain("notes", raw);
        Assert.DoesNotContain("calculator", raw);
        Assert.DoesNotMatch(@"\b\d{2}-\d{4}\b", raw);
        foreach (var id in TeachWorld.StudentIdsList) Assert.DoesNotContain(id, raw);

        // Class size is the exact number of Classy users who picked the section.
        Assert.Equal([1, 1, 1], teacher.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("students").GetInt32()));
        var huda = await Timetable(w.Teacher2);
        Assert.Equal((2, 1), (Class(huda, "CS2201", "1").GetProperty("students").GetInt32(), Class(huda, "CS2201", "2").GetProperty("students").GetInt32()));
    }

    [Fact]
    public async Task One_real_class_under_three_codes_is_one_block()
    {
        using var w = await TeachWorld.Create();
        // Student 1 takes two of the three codes: counted once.
        await w.Db(async db =>
        {
            var ibsm = await db.Sections.SingleAsync(s => s.Course.Code == "IBSM1101");
            db.Picks.Add(new Pick { StudentId = w.StudentRowIds[0], CourseId = ibsm.CourseId, SectionId = ibsm.Id });
            await db.SaveChangesAsync();
        });
        var tt = await Timetable(w.Teacher1);
        string[] Keys(string code) => Class(tt, code).GetProperty("section").GetProperty("meetings").EnumerateArray()
            .Select(m => m.TryGetProperty("merge", out var k) ? k.GetString()! : "").ToArray();

        // The same key on each code, one per date.
        var first = $"{w.Date(0, 0):yyyy-MM-dd}|08:00|09:30|a101";
        var second = $"{w.Date(1, 0):yyyy-MM-dd}|08:00|09:30|a101";
        Assert.Equal([first, second], Keys("BE1101"));
        Assert.Equal(Keys("BE1101"), Keys("IBSM1101"));
        Assert.Equal(Keys("BE1101"), Keys("LOG1101"));

        // Its size is the distinct Classy users over the three codes (1 + 2 + 1 picks, 3 people).
        Assert.Equal([1, 2, 1], tt.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("students").GetInt32()));
        var sizes = tt.GetProperty("mergeSizes");
        Assert.Equal([first, second], sizes.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal((3, 3), (sizes.GetProperty(first).GetInt32(), sizes.GetProperty(second).GetInt32()));

        // The server's one definition of the merged class: the other two codes, on a date it meets.
        await w.Db(async db =>
        {
            var semester = await db.Semesters.SingleAsync(s => s.Id == w.Semester);
            var span = await SemesterSpan.ForAsync(db, semester, CancellationToken.None);
            var taught = await TeacherSchedule.SectionsAsync(db, w.Semester, null, [TeacherNames.NameKey(TeachWorld.Teacher1Name)], CancellationToken.None);
            var be = taught.Single(t => t.Course.Code == "BE1101").Section.Id;
            var siblings = TeacherSchedule.Siblings(taught, span, be, w.Date(0, 0), new TimeOnly(8, 0));
            Assert.Equal(["IBSM1101", "LOG1101"], siblings.Select(t => t.Course.Code));
            Assert.Equal(["BE1101", "IBSM1101"], TeacherSchedule.Siblings(taught, span, taught.Single(t => t.Course.Code == "LOG1101").Section.Id, w.Date(1, 0), new TimeOnly(8, 0)).Select(t => t.Course.Code));
            // No session then: nothing to be merged with.
            Assert.Empty(TeacherSchedule.Siblings(taught, span, be, w.Date(0, 1), new TimeOnly(8, 0)));
            Assert.Empty(TeacherSchedule.Siblings(taught, span, be, w.Date(0, 0), new TimeOnly(9, 45)));
            // A code that isn't on this timetable is no sibling.
            var two = taught.Where(t => t.Course.Code != "LOG1101").ToList();
            Assert.Equal(["IBSM1101"], TeacherSchedule.Siblings(two, span, be, w.Date(0, 0), new TimeOnly(8, 0)).Select(t => t.Course.Code));

            // The same items students' reminders and feeds are made of: one per real class, led by the first code.
            var teacher = await db.Teachers.SingleAsync(t => t.Id == w.Teacher1Id);
            var items = (await TeacherSchedule.ItemsAsync(db, semester, [teacher], slots: true, planner: false, CancellationToken.None))[w.Teacher1Id];
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal((0, "BE1101 · IBSM1101 · LOG1101", be, "A101"), (i.StudentId, i.Code, i.SectionId, i.Room)));
            Assert.Equal([w.Date(0, 0), w.Date(1, 0)], items.Select(i => i.Date!.Value).Order());
        });

        // Hiding one code takes it out of the block and of its size.
        await Json(await Tick(w.Teacher1, await w.Section("LOG1101", "1"), false));
        tt = await Timetable(w.Teacher1);
        Assert.Equal(2, tt.GetProperty("mergeSizes").GetProperty(first).GetInt32());
    }

    [Fact]
    public async Task Two_sections_of_one_course_and_classes_without_a_room_are_not_merged()
    {
        using var w = await TeachWorld.Create();
        var online = await w.Section("LS1001", "1");
        await w.Db(async db =>
        {
            // Section 2 also meets when and where section 1 does; and section 1 has an online hour beside the online course's.
            var one = await db.Sections.Include(s => s.Meetings).SingleAsync(s => s.Course.Code == "CS2201" && s.Number == "1");
            var two = await db.Sections.Include(s => s.Meetings).SingleAsync(s => s.Course.Code == "CS2201" && s.Number == "2");
            two.Meetings.Add(new Meeting { Day = 1, Date = w.Date(0, 1), Start = new(8, 0), End = new(9, 30), Room = "B105" });
            one.Meetings.Add(new Meeting { Day = 2, Date = w.Date(0, 2), Start = new(13, 15), End = new(14, 45), Room = " online " });
            await db.SaveChangesAsync();
        });
        await Json(await Tick(w.Teacher2, online, true));
        var tt = await Timetable(w.Teacher2);
        Assert.Equal(["CS2201/1", "CS2201/2", "LS1001/1"], Classes(tt));

        // The two sections share a key (same time and room) but are two classes: no merged size, and no siblings.
        Assert.Empty(tt.GetProperty("mergeSizes").EnumerateObject());
        // Online classes carry no key at all, though both are "Online" at the same time.
        Assert.All(Class(tt, "LS1001").GetProperty("section").GetProperty("meetings").EnumerateArray(), m => Assert.False(m.TryGetProperty("merge", out _)));
        Assert.Contains(Class(tt, "CS2201", "1").GetProperty("section").GetProperty("meetings").EnumerateArray(), m => !m.TryGetProperty("merge", out _));
        Assert.Null(TeacherSchedule.MergeKey(null, 2, new(13, 15), new(14, 45), "Online"));
        Assert.Null(TeacherSchedule.MergeKey(null, 2, new(13, 15), new(14, 45), "  "));
        Assert.Null(TeacherSchedule.MergeKey(null, 2, new(13, 15), new(14, 45), "Field Trip"));
        Assert.Equal("w2|13:15|14:45|labl2", TeacherSchedule.MergeKey(null, 2, new(13, 15), new(14, 45), " Lab  L2 "));
        Assert.Equal("2026-11-29|08:00|09:30|a101", TeacherSchedule.MergeKey(new(2026, 11, 29), 0, new(8, 0), new(9, 30), "A 101"));

        await w.Db(async db =>
        {
            var span = await SemesterSpan.ForAsync(db, await db.Semesters.SingleAsync(s => s.Id == w.Semester), CancellationToken.None);
            var teacher = await db.Teachers.SingleAsync(t => t.Id == w.Teacher2Id);
            var taught = await TeacherSchedule.SectionsAsync(db, w.Semester, teacher, [TeacherNames.NameKey(TeachWorld.Teacher2Name)], CancellationToken.None);
            var one = taught.Single(t => t.Course.Code == "CS2201" && t.Section.Number == "1").Section.Id;
            Assert.Empty(TeacherSchedule.Siblings(taught, span, one, w.Date(0, 1), new TimeOnly(8, 0)));
            Assert.Empty(TeacherSchedule.Siblings(taught, span, one, w.Date(0, 2), new TimeOnly(13, 15)));
            Assert.Empty(TeacherSchedule.Siblings(taught, span, online, w.Date(0, 2), new TimeOnly(13, 15)));
        });
    }

    [Fact]
    public async Task A_new_semester_starts_with_no_added_sections_and_offers_last_semesters()
    {
        using var w = await TeachWorld.Create();
        var added = await w.Section("BE1101", "2");
        var lab = await w.Section("CS2201-L", "1");
        await Json(await Tick(w.Teacher1, added, true));
        await Json(await Tick(w.Teacher1, lab, true));
        Assert.Empty((await Sections(w.Teacher1)).GetProperty("previous").EnumerateArray());

        // The university removes a section they added: it is listed as removed, by its text.
        await w.Db(db => db.Sections.Where(s => s.Id == lab).ExecuteDeleteAsync());
        var tt = await Timetable(w.Teacher1);
        Assert.Equal(["BE1101/1", "BE1101/2", "IBSM1101/1", "LOG1101/1"], Classes(tt));
        var removed = Assert.Single(tt.GetProperty("removed").EnumerateArray());
        Assert.Equal(("CS2201-L", "1"), (removed.GetProperty("code").GetString(), removed.GetProperty("number").GetString()));
        await w.AssertNoOrphans();

        // A new semester is published. BE1101 section 2 is offered again; the lab isn't there, and LOG1101 is theirs by name.
        var next = await w.Db(async db =>
        {
            await db.Semesters.ExecuteUpdateAsync(u => u.SetProperty(s => s.IsCurrent, false));
            var semester = new Semester { Name = "Spring 2027", Term = "Spring", Year = 2027, IsCurrent = true, PublishedAt = w.Time.GetUtcNow(), Week1Start = w.FirstSunday.AddDays(140), LastWeek = 14 };
            Section Weekly(string number, string instructor, int day) => new()
            {
                Number = number, Instructor = instructor, Meetings = [new Meeting { Day = day, Start = new(8, 0), End = new(9, 30), Room = "C1" }, new Meeting { Day = day + 2, Start = new(11, 30), End = new(13, 0), Room = "C1" }],
            };
            semester.Courses.Add(new Course { Code = "BE1101", Name = "Business Economics", Type = CourseType.Core, Sections = [Weekly("1", "Someone Else", 0), Weekly("2", "Someone Else", 1)] });
            semester.Courses.Add(new Course { Code = "LOG1101", Name = "Logistics", Type = CourseType.Core, Sections = [Weekly("1", TeachWorld.Teacher1Name, 2)] });
            db.Semesters.Add(semester);
            await db.SaveChangesAsync();
            return semester.Id;
        });
        tt = await Timetable(w.Teacher1);
        Assert.Equal(next, tt.GetProperty("semester").GetProperty("id").GetInt32());
        // The name link carried over by its text; nothing added did.
        Assert.Equal(["LOG1101/1"], Classes(tt));
        Assert.Empty(tt.GetProperty("removed").EnumerateArray());
        Assert.Equal("w2|08:00|09:30|c1", Class(tt, "LOG1101").GetProperty("section").GetProperty("meetings")[0].GetProperty("merge").GetString());
        var sections = await Sections(w.Teacher1);
        Assert.Equal(["LOG1101/1:own"], Mine(sections));
        var previous = Assert.Single(sections.GetProperty("previous").EnumerateArray());
        Assert.Equal(("BE1101", "2"), (previous.GetProperty("code").GetString(), previous.GetProperty("number").GetString()));

        // Ticking it again: on the timetable, and no longer offered.
        var again = previous.GetProperty("id").GetInt32();
        Assert.NotEqual(added, again);
        Assert.Equal("Mon 08:00 · Wed 11:30", (await Json(await Tick(w.Teacher1, again, true))).GetProperty("when").GetString());
        Assert.Equal(["BE1101/2", "LOG1101/1"], Classes(await Timetable(w.Teacher1)));
        Assert.Empty((await Sections(w.Teacher1)).GetProperty("previous").EnumerateArray());
        // Last semester's section is not this semester's.
        Assert.Equal(HttpStatusCode.NotFound, (await Tick(w.Teacher1, added, true)).StatusCode);
    }

    [Fact]
    public async Task Edit_my_classes_searches_every_course_by_code_name_or_teacher()
    {
        using var w = await TeachWorld.Create();
        var all = await Sections(w.Teacher1);
        Assert.Equal(["semester", "mine", "previous", "courses"], all.EnumerateObject().Select(p => p.Name));
        Assert.Equal(w.Semester, all.GetProperty("semester").GetProperty("id").GetInt32());
        // Without a search only my own are sent.
        Assert.Empty(all.GetProperty("courses").EnumerateArray());
        Assert.Equal(["BE1101/1:own", "IBSM1101/1:own", "LOG1101/1:own"], Mine(all));
        Assert.Equal(["id", "code", "number", "instructor", "when", "state"], all.GetProperty("mine")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("Sun 08:00", all.GetProperty("mine")[0].GetProperty("when").GetString());

        List<string> Found(JsonElement r) => r.GetProperty("courses").EnumerateArray().SelectMany(c => c.GetProperty("sections").EnumerateArray()
            .Select(s => $"{c.GetProperty("code").GetString()}/{s.GetProperty("number").GetString()}:{s.GetProperty("state").GetString()}")).ToList();
        // By code (spaces ignored), by course name, by teacher.
        Assert.Equal(["BE1101/1:own", "BE1101/2:"], Found(await Sections(w.Teacher1, "be 1101")));
        Assert.Equal(["CS2201/1:", "CS2201/2:", "CS2201-L/1:"], Found(await Sections(w.Teacher1, "data struct")));
        Assert.Equal(["CS2201/1:", "CS2201/2:"], Found(await Sections(w.Teacher1, "al-kindi")));
        Assert.Empty(Found(await Sections(w.Teacher1, "nothing like this")));
        var course = (await Sections(w.Teacher1, "LS1001")).GetProperty("courses")[0];
        Assert.Equal(["id", "code", "name", "sections"], course.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Academic English", course.GetProperty("name").GetString());

        // The states follow what the teacher ticks.
        await Json(await Tick(w.Teacher1, await w.Section("BE1101", "2"), true));
        await Json(await Tick(w.Teacher1, await w.Section("BE1101", "1"), false));
        Assert.Equal(["BE1101/1:hidden", "BE1101/2:added"], Found(await Sections(w.Teacher1, "BE1101")));
    }

    [Fact]
    public async Task At_most_sixty_sections_can_be_added_in_a_semester()
    {
        using var w = await TeachWorld.Create();
        await w.Db(async db =>
        {
            db.TeacherSections.AddRange(Enumerable.Range(1, TeachEndpoints.MaxAddedSections).Select(i => new TeacherSection
            {
                TeacherId = w.Teacher1Id, SemesterId = w.Semester, CourseCode = $"XX{i:D4}", SectionNumber = "1", Mode = TeacherSection.AddedMode, CreatedAt = w.Time.GetUtcNow(),
            }));
            await db.SaveChangesAsync();
        });
        var r = await Tick(w.Teacher1, await w.Section("BE1101", "2"), true);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("You can add up to 60 classes.", await Error(r));
        // Hiding one of their own is not adding.
        await Json(await Tick(w.Teacher1, await w.Section("BE1101", "1"), false));
        // Another teacher has their own sixty.
        await Json(await Tick(w.Teacher2, await w.Section("BE1101", "2"), true));
    }

    [Fact]
    public async Task A_frozen_account_cannot_add_a_section_but_can_hide_and_remove()
    {
        using var w = await TeachWorld.Create();
        var other = await w.Section("BE1101", "2");
        var lab = await w.Section("CS2201-L", "1");
        await Json(await Tick(w.Teacher1, other, true));
        await w.Db(db => db.Teachers.Where(t => t.Id == w.Teacher1Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.FrozenAt, w.Time.GetUtcNow()).SetProperty(t => t.FrozenBy, "test")));
        Assert.True((await Json(await w.Teacher1.GetAsync("/api/teach/me"))).GetProperty("frozen").GetBoolean());

        var refused = await Tick(w.Teacher1, lab, true);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Your class changes are paused. Ask the Classy team.", await Error(refused));
        Assert.Equal(1, await w.Db(db => db.TeacherSections.CountAsync()));

        // Everything that takes them off a class still works, and so does reading.
        var own = await w.Section("LOG1101", "1");
        Assert.Equal("hidden", (await Json(await Tick(w.Teacher1, own, false))).GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, (await Json(await Tick(w.Teacher1, other, false))).GetProperty("state").ValueKind);
        Assert.Equal(["BE1101/1", "IBSM1101/1"], Classes(await Timetable(w.Teacher1)));
        // Bringing back a class under their own name is not adding.
        Assert.Equal("own", (await Json(await Tick(w.Teacher1, own, true))).GetProperty("state").GetString());
        // The other teacher is not frozen.
        await Json(await Tick(w.Teacher2, lab, true));
    }

    [Fact]
    public async Task Own_slots_are_added_changed_and_removed_like_a_students()
    {
        using var w = await TeachWorld.Create();
        Assert.Empty((await Json(await w.Teacher1.GetAsync("/api/teach/slots"))).EnumerateArray());
        var slot = await Json(await w.Teacher1.PostAsJsonAsync("/api/teach/slots", new { title = " Department meeting ", day = 3, start = "14:00", end = "15:00", place = "B-204", note = "Bring the report" }));
        // The same answer a student's slot gives.
        var students = await Json(await w.Students[0].PostAsJsonAsync("/api/me/slots", new { title = "Gym", day = 3, start = "14:00", end = "15:00" }));
        Assert.Equal(students.EnumerateObject().Select(p => p.Name), slot.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("Department meeting", 3, "14:00", "15:00", "B-204", "Bring the report"),
            (slot.GetProperty("title").GetString(), slot.GetProperty("day").GetInt32(), slot.GetProperty("start").GetString(), slot.GetProperty("end").GetString(),
                slot.GetProperty("place").GetString(), slot.GetProperty("note").GetString()));
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("date").ValueKind);
        var id = slot.GetProperty("id").GetInt32();

        // A dated one takes its weekday from the date.
        var dated = await Json(await w.Teacher1.PostAsJsonAsync("/api/teach/slots", new { title = "Viva", date = w.Date(0, 4).ToString("yyyy-MM-dd"), start = "9:00", end = "10:00" }));
        Assert.Equal((4, w.Date(0, 4).ToString("yyyy-MM-dd")), (dated.GetProperty("day").GetInt32(), dated.GetProperty("date").GetString()));

        // The same checks as a student's.
        foreach (var bad in new object[]
        {
            new { title = "", day = 1, start = "08:00", end = "09:00" },
            new { title = new string('x', 61), day = 1, start = "08:00", end = "09:00" },
            new { title = "X", day = 7, start = "08:00", end = "09:00" },
            new { title = "X", day = 1, start = "09:00", end = "09:00" },
            new { title = "X", day = 1, start = "soon", end = "09:00" },
            new { title = "X", date = "someday", start = "08:00", end = "09:00" },
            new { title = "X", day = 1, start = "08:00", end = "09:00", place = new string('p', 61) },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.Teacher1.PostAsJsonAsync("/api/teach/slots", bad)).StatusCode);

        var changed = await Json(await w.Teacher1.PutAsJsonAsync($"/api/teach/slots/{id}", new { title = "Department meeting", day = 4, start = "13:15", end = "14:45" }));
        Assert.Equal((4, "13:15", ""), (changed.GetProperty("day").GetInt32(), changed.GetProperty("start").GetString(), changed.GetProperty("place").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Teacher1.PutAsJsonAsync($"/api/teach/slots/{id}", new { title = "" })).StatusCode);

        // They show on the timetable, in the students' shape, and nowhere near a student's own slots.
        var slots = (await Timetable(w.Teacher1)).GetProperty("slots");
        Assert.Equal(["Department meeting", "Viva"], slots.EnumerateArray().Select(s => s.GetProperty("title").GetString()));
        Assert.Equal(slots.GetRawText(), await (await w.Teacher1.GetAsync("/api/teach/slots")).Content.ReadAsStringAsync());
        Assert.Equal(["Gym"], (await Json(await w.Students[0].GetAsync("/api/me/slots"))).EnumerateArray().Select(s => s.GetProperty("title").GetString()));
        Assert.Empty((await Timetable(w.Teacher2)).GetProperty("slots").EnumerateArray());
        Assert.Equal(1, await w.Db(db => db.CustomSlots.CountAsync()));

        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.DeleteAsync($"/api/teach/slots/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Teacher1.DeleteAsync($"/api/teach/slots/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Teacher1.PutAsJsonAsync($"/api/teach/slots/{id}", new { title = "X", day = 1, start = "08:00", end = "09:00" })).StatusCode);
        Assert.Single((await Json(await w.Teacher1.GetAsync("/api/teach/slots"))).EnumerateArray());
    }

    [Fact]
    public async Task Office_hours_are_on_the_timetable_and_skip_days_off()
    {
        using var w = await TeachWorld.Create();
        Assert.Empty((await Timetable(w.Teacher1)).GetProperty("officeHours").EnumerateArray());
        await Json(await w.Teacher1.PutAsJsonAsync("/api/teach/office", new
        {
            officeRoom = "B-204", note = "", showEmail = false,
            hours = new[] { new { day = 2, start = "12:00", end = "13:00", room = "B-204" }, new { day = 0, start = "10:00", end = "11:00", room = "Library" } },
        }));
        // A Sunday and a Monday off: only the Sunday hour skips one.
        var sunday = w.Date(1, 0).ToString("yyyy-MM-dd");
        await Json(await w.Owner.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/days-off", new { date = sunday, to = w.Date(1, 1).ToString("yyyy-MM-dd"), name = "Break" }));

        var tt = await Timetable(w.Teacher1);
        var hours = tt.GetProperty("officeHours").EnumerateArray().ToList();
        Assert.Equal(2, hours.Count);
        Assert.Equal(["id", "day", "start", "end", "room", "excludedDates"], hours[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal((0, "10:00", "11:00", "Library"), (hours[0].GetProperty("day").GetInt32(), hours[0].GetProperty("start").GetString(), hours[0].GetProperty("end").GetString(), hours[0].GetProperty("room").GetString()));
        Assert.Equal([sunday], hours[0].GetProperty("excludedDates").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(2, hours[1].GetProperty("day").GetInt32());
        Assert.Empty(hours[1].GetProperty("excludedDates").EnumerateArray());
        Assert.Equal(2, tt.GetProperty("daysOff").GetArrayLength());
        // Another teacher's timetable has none of them.
        Assert.Empty((await Timetable(w.Teacher2)).GetProperty("officeHours").EnumerateArray());

        // Among the teacher's items they are slots named "Office hours" that skip the days off.
        await w.Db(async db =>
        {
            var semester = await db.Semesters.SingleAsync(s => s.Id == w.Semester);
            var teachers = await db.Teachers.Where(t => t.Email != null).ToListAsync();
            var items = await TeacherSchedule.ItemsAsync(db, semester, teachers, slots: false, planner: false, CancellationToken.None);
            var office = items[w.Teacher1Id].Where(i => i.EventKind == TeacherSchedule.OfficeKind).OrderBy(i => i.Day).ToList();
            Assert.Equal(2, office.Count);
            Assert.All(office, i => Assert.Equal((StudentSchedule.Source.Slot, "Office hours", (DateOnly?)null), (i.From, i.Name, i.Date)));
            Assert.Equal([w.Date(1, 0)], office[0].Skip);
            Assert.Empty(office[1].Skip);
            Assert.DoesNotContain(items[w.Teacher2Id], i => i.EventKind == TeacherSchedule.OfficeKind);
            Assert.Equal(4, items[w.Teacher2Id].Count);
        });
    }

    [Fact]
    public async Task Opening_a_class_clears_its_changed_badge()
    {
        using var w = await TeachWorld.Create();
        var be = await w.Section("BE1101", "1");
        var log = await w.Section("LOG1101", "1");
        Assert.All((await Timetable(w.Teacher1)).GetProperty("classes").EnumerateArray(), c => Assert.False(c.GetProperty("changed").GetBoolean()));

        // The university changes two of the classes after the account was made.
        w.Time.Advance(TimeSpan.FromMinutes(10));
        await w.Db(db => db.Sections.Where(s => s.Id == be || s.Id == log)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ChangedAt, w.Time.GetUtcNow()).SetProperty(s => s.ChangeSummary, "Room A101 → A105")));
        w.Time.Advance(TimeSpan.FromMinutes(1));
        var tt = await Timetable(w.Teacher1);
        Assert.Equal([true, false, true], tt.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("changed").GetBoolean()));
        Assert.Equal("Room A101 → A105", Class(tt, "BE1101").GetProperty("section").GetProperty("changeSummary").GetString());

        // An owner looking in sees no badge and clears none.
        var viewing = TeachWorld.ViewAs(w.Owner, TeacherKeys.For(new Teacher { Id = w.Teacher1Id }));
        Assert.All((await Timetable(viewing)).GetProperty("classes").EnumerateArray(), c => Assert.False(c.GetProperty("changed").GetBoolean()));
        Assert.Equal(HttpStatusCode.NoContent, (await viewing.PostAsJsonAsync("/api/teach/seen", new { sectionId = be })).StatusCode);
        Assert.Equal(0, await w.Db(db => db.TeacherSeens.CountAsync()));

        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/teach/seen", new { sectionId = be })).StatusCode);
        Assert.Equal([false, false, true], (await Timetable(w.Teacher1)).GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("changed").GetBoolean()));
        // Again, a class that isn't theirs, and nothing at all: no error, no row.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/teach/seen", new { sectionId = be })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/teach/seen", new { sectionId = await w.Section("CS2201", "1") })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/teach/seen", new { })).StatusCode);
        Assert.Equal((w.Teacher1Id, be), await w.Db(async db => { var s = await db.TeacherSeens.SingleAsync(); return (s.TeacherId, s.SectionId); }));

        // A later change brings the badge back.
        w.Time.Advance(TimeSpan.FromMinutes(5));
        await w.Db(db => db.Sections.Where(s => s.Id == be).ExecuteUpdateAsync(u => u.SetProperty(s => s.ChangedAt, w.Time.GetUtcNow())));
        Assert.True(Class(await Timetable(w.Teacher1), "BE1101").GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task Batch_reps_events_show_only_while_the_planner_is_on()
    {
        using var w = await TeachWorld.Create();
        var day = w.Date(0, 3).ToString("yyyy-MM-dd");
        await w.Db(async db =>
        {
            var be = await db.Sections.Include(s => s.Meetings).SingleAsync(s => s.Course.Code == "BE1101" && s.Number == "1");
            var cs = await db.Sections.SingleAsync(s => s.Course.Code == "CS2201" && s.Number == "1");
            var first = be.Meetings.OrderBy(m => m.Date).First();
            // A quiz for BE1101, and the rep cancels the first BE1101 class; an exam for the other teacher's course.
            db.PlannerEvents.Add(new PlannerEvent { SemesterId = w.Semester, UpdatedAt = w.Time.GetUtcNow(), PayloadJson = PlannerJson.Write(new EventSpec("class", "add", "Quiz review", day, "15:00", "16:00", "A105", [new(be.CourseId, [be.Id])])) });
            db.PlannerEvents.Add(new PlannerEvent { SemesterId = w.Semester, UpdatedAt = w.Time.GetUtcNow(), PayloadJson = PlannerJson.Write(new EventSpec("exam", "add", "Midterm", day, "09:00", "11:00", "Hall", [new(cs.CourseId, [])])) });
            var date = first.Date!.Value.ToString("yyyy-MM-dd");
            db.PlannerEvents.Add(new PlannerEvent
            {
                SemesterId = w.Semester, UpdatedAt = w.Time.GetUtcNow(),
                PayloadJson = PlannerJson.Write(new EventSpec("class", "cancel", "Cancelled", date, "08:00", "09:30", "A101", [new(be.CourseId, [be.Id])],
                    Source: new(be.CourseId, be.Id, PlannerEngine.Fingerprint(be, first), date, "08:00", "09:30"))),
            });
            await db.SaveChangesAsync();
        });

        // Off: nothing of the planner.
        var tt = await Timetable(w.Teacher1);
        Assert.Empty(tt.GetProperty("plannerEvents").EnumerateArray());
        Assert.All(Class(tt, "BE1101").GetProperty("section").GetProperty("meetings").EnumerateArray(), m => Assert.Equal(JsonValueKind.Null, m.GetProperty("excludedDates").ValueKind));

        await w.Db(async db =>
        {
            var setting = await db.AppSettings.FindAsync("planner.enabled");
            if (setting is null) db.AppSettings.Add(setting = new AppSetting { Key = "planner.enabled", UpdatedBy = "test", UpdatedAt = w.Time.GetUtcNow() });
            setting.Value = "true";
            await db.SaveChangesAsync();
        });
        tt = await Timetable(w.Teacher1);
        var quiz = Assert.Single(tt.GetProperty("plannerEvents").EnumerateArray());
        Assert.Equal(("Quiz review", day, "BE1101"), (quiz.GetProperty("title").GetString(), quiz.GetProperty("date").GetString(), quiz.GetProperty("code").GetString()));
        // The same events, in the same shape, the section's students get; and the class the rep cancelled is off for both.
        var student = await Json(await w.Students[0].GetAsync("/api/me/timetable"));
        Assert.Contains(student.GetProperty("plannerEvents").EnumerateArray(), e => e.GetRawText() == quiz.GetRawText());
        var meetings = Class(tt, "BE1101").GetProperty("section").GetProperty("meetings");
        Assert.Equal([w.Date(0, 0).ToString("yyyy-MM-dd")], meetings[0].GetProperty("excludedDates").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(JsonValueKind.Null, meetings[1].GetProperty("excludedDates").ValueKind);
        Assert.Equal(Class(student, "BE1101").GetProperty("section").GetProperty("meetings")[0].GetProperty("excludedDates").GetRawText(), meetings[0].GetProperty("excludedDates").GetRawText());
        // The other teacher gets the exam of their course.
        Assert.Equal("Midterm", Assert.Single((await Timetable(w.Teacher2)).GetProperty("plannerEvents").EnumerateArray()).GetProperty("title").GetString());

        // Among the teacher's items: on the first Sunday only the two codes the rep left alone still meet.
        await w.Db(async db =>
        {
            var semester = await db.Semesters.SingleAsync(s => s.Id == w.Semester);
            var span = await SemesterSpan.ForAsync(db, semester, CancellationToken.None);
            var teacher = await db.Teachers.SingleAsync(t => t.Id == w.Teacher1Id);
            var items = (await TeacherSchedule.ItemsAsync(db, semester, [teacher], slots: false, planner: true, CancellationToken.None))[w.Teacher1Id];
            Assert.Equal(["IBSM1101 · LOG1101"], items.Where(i => span.Happens(i, w.Date(0, 0))).Select(i => i.Code));
            Assert.Equal(["BE1101 · IBSM1101 · LOG1101"], items.Where(i => span.Happens(i, w.Date(1, 0))).Select(i => i.Code));
            Assert.Equal(["Quiz review"], items.Where(i => span.Happens(i, w.Date(0, 3))).Select(i => i.Name));
            // Without the planner the class is whole.
            items = (await TeacherSchedule.ItemsAsync(db, semester, [teacher], slots: false, planner: false, CancellationToken.None))[w.Teacher1Id];
            Assert.Equal(["BE1101 · IBSM1101 · LOG1101"], items.Where(i => span.Happens(i, w.Date(0, 0))).Select(i => i.Code));
        });
    }

    [Fact]
    public async Task With_no_semester_published_the_timetable_is_empty()
    {
        using var w = await TeachWorld.Create(seed: false);
        var c = await w.SignInTeacher("noor.saleh@gutech.edu.om");
        var tt = await Timetable(c);
        Assert.Equal(JsonValueKind.Null, tt.GetProperty("semester").ValueKind);
        foreach (var key in new[] { "classes", "needsSection", "clashes", "slots", "officeHours", "changes", "removed" })
            Assert.Empty(tt.GetProperty(key).EnumerateArray());
        Assert.Empty(tt.GetProperty("mergeSizes").EnumerateObject());
        var sections = await Sections(c, "anything");
        Assert.Equal(JsonValueKind.Null, sections.GetProperty("semester").ValueKind);
        Assert.Empty(sections.GetProperty("courses").EnumerateArray());
        Assert.Empty((await Json(await c.GetAsync("/api/teach/slots"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await Tick(c, 1, true)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/teach/slots", new { title = "X", day = 1, start = "08:00", end = "09:00" })).StatusCode);
    }
}
