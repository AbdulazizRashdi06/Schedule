using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// What students read about teachers: office hours and details on every class, and the teacher page. An email shows
/// only when the teacher turned it on, and an account students see nowhere has no page.
/// </summary>
public class TeacherPagesTests
{
    static object Hour(int day, string start, string end, string room = "B-204") => new { day, start, end, room };
    static Task<HttpResponseMessage> SaveOffice(HttpClient c, string officeRoom = "B-204", string note = "", bool? showEmail = null, params object[] hours) =>
        c.PutAsJsonAsync("/api/teach/office", new { officeRoom, note, showEmail, hours });
    static async Task<JsonElement> StudentClass(HttpClient c, string code) => (await Json(await c.GetAsync("/api/me/timetable")))
        .GetProperty("classes").EnumerateArray().Single(x => x.GetProperty("code").GetString() == code);
    static Task<HttpResponseMessage> Page(HttpClient c, string key) => c.GetAsync($"/api/teachers/{key}");
    static string Key(int teacherId) => TeacherKeys.For(new Teacher { Id = teacherId });

    [Fact]
    public async Task Office_hours_and_details_save_within_their_limits()
    {
        using var w = await TeachWorld.Create();
        var office = await Json(await w.Teacher1.GetAsync("/api/teach/office"));
        Assert.Equal(["name", "email", "officeRoom", "note", "showEmail", "hours"], office.EnumerateObject().Select(p => p.Name));
        Assert.Equal((TeachWorld.Teacher1Name, TeachWorld.Teacher1Email, "", "", false),
            (office.GetProperty("name").GetString(), office.GetProperty("email").GetString(), office.GetProperty("officeRoom").GetString(), office.GetProperty("note").GetString(), office.GetProperty("showEmail").GetBoolean()));
        Assert.Empty(office.GetProperty("hours").EnumerateArray());

        office = await Json(await SaveOffice(w.Teacher1, " B-204 ", " Knock\u0007 first ", true, Hour(2, "12:00", "13:00"), Hour(0, "9:00", "10:30", " Library ")));
        Assert.Equal(("B-204", "Knock first", true), (office.GetProperty("officeRoom").GetString(), office.GetProperty("note").GetString(), office.GetProperty("showEmail").GetBoolean()));
        var hours = office.GetProperty("hours").EnumerateArray().ToList();
        Assert.Equal(["id", "day", "start", "end", "room"], hours[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal([(0, "09:00", "10:30", "Library"), (2, "12:00", "13:00", "B-204")],
            hours.Select(h => (h.GetProperty("day").GetInt32(), h.GetProperty("start").GetString(), h.GetProperty("end").GetString(), h.GetProperty("room").GetString())));
        Assert.Equal(office.GetRawText(), (await Json(await w.Teacher1.GetAsync("/api/teach/office"))).GetRawText());

        // A save replaces the list; leaving showEmail out keeps it.
        office = await Json(await w.Teacher1.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-204", note = "Away this week", hours = new[] { Hour(4, "08:00", "09:00") } }));
        Assert.Equal(4, Assert.Single(office.GetProperty("hours").EnumerateArray()).GetProperty("day").GetInt32());
        Assert.True(office.GetProperty("showEmail").GetBoolean());
        Assert.Equal(1, await w.Db(db => db.OfficeHours.CountAsync()));

        // The limits: ten rows, an end after the start, a real day and time, and short text.
        foreach (var r in new[]
        {
            await SaveOffice(w.Teacher1, hours: Enumerable.Range(0, 11).Select(i => Hour(i % 5, "08:00", "09:00")).ToArray()),
            await SaveOffice(w.Teacher1, hours: Hour(1, "10:00", "10:00")),
            await SaveOffice(w.Teacher1, hours: Hour(1, "11:00", "10:00")),
            await SaveOffice(w.Teacher1, hours: Hour(7, "10:00", "11:00")),
            await SaveOffice(w.Teacher1, hours: Hour(-1, "10:00", "11:00")),
            await SaveOffice(w.Teacher1, hours: Hour(1, "ten", "11:00")),
            await SaveOffice(w.Teacher1, hours: new { start = "10:00", end = "11:00" }),
            await SaveOffice(w.Teacher1, hours: Hour(1, "10:00", "11:00", new string('r', 61))),
            await SaveOffice(w.Teacher1, officeRoom: new string('o', 61)),
            await SaveOffice(w.Teacher1, note: new string('n', 201)),
        })
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        // Nothing of a refused save is kept.
        Assert.Equal(office.GetRawText(), (await Json(await w.Teacher1.GetAsync("/api/teach/office"))).GetRawText());
        // Exactly at the limits is fine.
        await Json(await SaveOffice(w.Teacher1, new string('o', 60), new string('n', 200), null, Enumerable.Range(0, 10).Select(i => Hour(i % 5, "08:00", "09:00", new string('r', 60))).ToArray()));
        Assert.Equal(10, await w.Db(db => db.OfficeHours.CountAsync(h => h.TeacherId == w.Teacher1Id)));
        // The other teacher's are their own.
        Assert.Empty((await Json(await w.Teacher2.GetAsync("/api/teach/office"))).GetProperty("hours").EnumerateArray());
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task A_frozen_account_cannot_save_office_hours()
    {
        using var w = await TeachWorld.Create();
        await Json(await SaveOffice(w.Teacher1, "B-204", "Knock first", false, Hour(1, "10:00", "11:00")));
        await w.Db(db => db.Teachers.Where(t => t.Id == w.Teacher1Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.FrozenAt, w.Time.GetUtcNow())));

        var r = await SaveOffice(w.Teacher1, "Somewhere else", "Free pizza", true);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("Your class changes are paused. Ask the Classy team.", await Error(r));
        // An owner looking in can't do it for them either.
        Assert.Equal(HttpStatusCode.Forbidden, (await SaveOffice(TeachWorld.ViewAs(w.Owner, Key(w.Teacher1Id)), "Somewhere else")).StatusCode);
        // What was there stays, and they can still read it.
        var office = await Json(await w.Teacher1.GetAsync("/api/teach/office"));
        Assert.Equal(("B-204", "Knock first", false, 1), (office.GetProperty("officeRoom").GetString(), office.GetProperty("note").GetString(), office.GetProperty("showEmail").GetBoolean(), office.GetProperty("hours").GetArrayLength()));
        Assert.Equal(0, await w.Db(db => db.TeachNotices.CountAsync()));
    }

    [Fact]
    public async Task Every_class_a_student_sees_lists_its_teachers()
    {
        using var w = await TeachWorld.Create();
        // Student 1 also takes the lab (under a placeholder) and the online course (nobody named).
        await w.Db(async db =>
        {
            foreach (var code in new[] { "CS2201-L", "LS1001" })
            {
                var section = await db.Sections.SingleAsync(s => s.Course.Code == code);
                db.Picks.Add(new Pick { StudentId = w.StudentRowIds[0], CourseId = section.CourseId, SectionId = section.Id });
            }
            await db.SaveChangesAsync();
        });
        var student = w.Students[0];

        // The named teacher, with the details of the account holding the name. No email unless they turned it on.
        await Json(await SaveOffice(w.Teacher1, "B-204", "Knock first", false, Hour(2, "12:00", "13:00"), Hour(0, "10:00", "11:00", "Library")));
        var named = Assert.Single((await StudentClass(student, "BE1101")).GetProperty("teachers").EnumerateArray());
        Assert.Equal(["name", "key", "tappable", "listed", "officeRoom", "note", "email", "officeHours"], named.EnumerateObject().Select(p => p.Name));
        Assert.Equal((TeachWorld.Teacher1Name, TeacherKeys.ForName(TeachWorld.Teacher1Name), true, true, "B-204", "Knock first"),
            (named.GetProperty("name").GetString(), named.GetProperty("key").GetString(), named.GetProperty("tappable").GetBoolean(), named.GetProperty("listed").GetBoolean(),
                named.GetProperty("officeRoom").GetString(), named.GetProperty("note").GetString()));
        Assert.Equal(JsonValueKind.Null, named.GetProperty("email").ValueKind);
        Assert.Equal(["day", "start", "end", "room"], named.GetProperty("officeHours")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal([(0, "10:00", "11:00", "Library"), (2, "12:00", "13:00", "B-204")], named.GetProperty("officeHours").EnumerateArray()
            .Select(h => (h.GetProperty("day").GetInt32(), h.GetProperty("start").GetString(), h.GetProperty("end").GetString(), h.GetProperty("room").GetString())));
        await Json(await SaveOffice(w.Teacher1, "B-204", "Knock first", true));
        Assert.Equal(TeachWorld.Teacher1Email, (await StudentClass(student, "BE1101")).GetProperty("teachers")[0].GetProperty("email").GetString());

        // A teacher who never opened their office page: named, with empty details. One who never signed in: the name only.
        var huda = Assert.Single((await StudentClass(student, "CS2201")).GetProperty("teachers").EnumerateArray());
        Assert.Equal((TeachWorld.Teacher2Name, "", 0), (huda.GetProperty("name").GetString(), huda.GetProperty("officeRoom").GetString(), huda.GetProperty("officeHours").GetArrayLength()));
        await w.Db(db => db.Teachers.Where(t => t.Id == w.Teacher2Id).ExecuteDeleteAsync());
        huda = Assert.Single((await StudentClass(student, "CS2201")).GetProperty("teachers").EnumerateArray());
        Assert.Equal(["name", "key", "tappable", "listed"], huda.EnumerateObject().Select(p => p.Name));
        Assert.Equal((TeachWorld.Teacher2Name, TeacherKeys.ForName(TeachWorld.Teacher2Name), true, true),
            (huda.GetProperty("name").GetString(), huda.GetProperty("key").GetString(), huda.GetProperty("tappable").GetBoolean(), huda.GetProperty("listed").GetBoolean()));

        // A placeholder is plain text; a class with nobody named lists nobody.
        var placeholder = Assert.Single((await StudentClass(student, "CS2201-L")).GetProperty("teachers").EnumerateArray());
        Assert.Equal((TeachWorld.PlaceholderName, false, true), (placeholder.GetProperty("name").GetString(), placeholder.GetProperty("tappable").GetBoolean(), placeholder.GetProperty("listed").GetBoolean()));
        Assert.False(placeholder.TryGetProperty("officeHours", out _));
        Assert.Empty((await StudentClass(student, "LS1001")).GetProperty("teachers").EnumerateArray());

        // Someone who added the class shows on it straight away, after the named teacher, under their shown name.
        var helper = await w.SignInTeacher("noor.saleh@gutech.edu.om");
        var helperId = await w.Db(db => db.Teachers.Where(t => t.Email == "noor.saleh@gutech.edu.om").Select(t => t.Id).SingleAsync());
        await Json(await helper.PutAsJsonAsync($"/api/teach/sections/{await w.Section("BE1101", "1")}", new { on = true }));
        await Json(await helper.PutAsJsonAsync($"/api/teach/sections/{await w.Section("LS1001", "1")}", new { on = true }));
        await Json(await SaveOffice(helper, "C-12", "", false, Hour(3, "14:00", "15:00", "C-12")));
        var teachers = (await StudentClass(student, "BE1101")).GetProperty("teachers").EnumerateArray().ToList();
        Assert.Equal([TeachWorld.Teacher1Name, "Noor Saleh"], teachers.Select(t => t.GetProperty("name").GetString()));
        Assert.Equal((Key(helperId), true, false, "C-12", 1), (teachers[1].GetProperty("key").GetString(), teachers[1].GetProperty("tappable").GetBoolean(), teachers[1].GetProperty("listed").GetBoolean(),
            teachers[1].GetProperty("officeRoom").GetString(), teachers[1].GetProperty("officeHours").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, teachers[1].GetProperty("email").ValueKind);
        Assert.Equal("Noor Saleh", Assert.Single((await StudentClass(student, "LS1001")).GetProperty("teachers").EnumerateArray()).GetProperty("name").GetString());
        // The teacher's own timetable lists them the same way.
        var own = (await Json(await w.Teacher1.GetAsync("/api/teach/timetable"))).GetProperty("classes").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "BE1101");
        Assert.Equal((await StudentClass(student, "BE1101")).GetProperty("teachers").GetRawText(), own.GetProperty("teachers").GetRawText());
        // Hiding it takes them off again. A class they don't have never had them.
        await Json(await helper.PutAsJsonAsync($"/api/teach/sections/{await w.Section("BE1101", "1")}", new { on = false }));
        Assert.Single((await StudentClass(student, "BE1101")).GetProperty("teachers").EnumerateArray());

        // Nothing here names a student.
        var raw = await (await student.GetAsync("/api/me/timetable")).Content.ReadAsStringAsync();
        foreach (var id in TeachWorld.StudentIdsList) Assert.DoesNotContain(id, raw);
    }

    [Fact]
    public async Task The_teacher_page_shows_details_and_this_weeks_classes()
    {
        using var w = await TeachWorld.Create();
        await Json(await SaveOffice(w.Teacher1, "B-204", "Knock first", false, Hour(0, "10:00", "11:00", "Library")));
        // Monday of the first week of classes, Oman time.
        w.Time.Set(new DateTimeOffset(w.Date(0, 1).ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(4)));
        var student = await w.SignInStudent(TeachWorld.StudentIdsList[0]);

        // By name, as a class page links to it, however the name is spelt.
        var page = await Json(await Page(student, TeacherKeys.ForName("salim al-harthy")));
        Assert.Equal(["name", "hasAccount", "officeRoom", "note", "email", "officeHours", "week", "classes"], page.EnumerateObject().Select(p => p.Name));
        Assert.Equal((TeachWorld.Teacher1Name, true, "B-204", "Knock first"),
            (page.GetProperty("name").GetString(), page.GetProperty("hasAccount").GetBoolean(), page.GetProperty("officeRoom").GetString(), page.GetProperty("note").GetString()));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("email").ValueKind);
        var hour = Assert.Single(page.GetProperty("officeHours").EnumerateArray());
        Assert.Equal(["day", "start", "end", "room"], hour.EnumerateObject().Select(p => p.Name));
        Assert.Equal((w.Date(0, 0).ToString("yyyy-MM-dd"), w.Date(0, 6).ToString("yyyy-MM-dd")), (page.GetProperty("week").GetProperty("from").GetString(), page.GetProperty("week").GetProperty("to").GetString()));

        // This week only, and the class under three codes is one row.
        var row = Assert.Single(page.GetProperty("classes").EnumerateArray());
        Assert.Equal(["codes", "name", "section", "date", "start", "end", "room", "type", "status", "note"], row.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["BE1101", "IBSM1101", "LOG1101"], row.GetProperty("codes").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(("Business Economics", "1", w.Date(0, 0).ToString("yyyy-MM-dd"), "08:00", "09:30", "A101", "lecture"),
            (row.GetProperty("name").GetString(), row.GetProperty("section").GetString(), row.GetProperty("date").GetString(), row.GetProperty("start").GetString(),
                row.GetProperty("end").GetString(), row.GetProperty("room").GetString(), row.GetProperty("type").GetString()));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (row.GetProperty("status").ValueKind, row.GetProperty("note").ValueKind));

        // By account it is the same page, and the email shows once the teacher turns it on.
        Assert.Equal(page.GetRawText(), (await Json(await Page(student, Key(w.Teacher1Id)))).GetRawText());
        await Json(await SaveOffice(w.Teacher1, "B-204", "Knock first", true, Hour(0, "10:00", "11:00", "Library")));
        Assert.Equal(TeachWorld.Teacher1Email, (await Json(await Page(student, Key(w.Teacher1Id)))).GetProperty("email").GetString());

        // The account's whole timetable: what it added shows (a lab, by its room), what it hid doesn't.
        await Json(await w.Teacher2.PutAsJsonAsync($"/api/teach/sections/{await w.Section("CS2201-L", "1")}", new { on = true }));
        await Json(await w.Teacher2.PutAsJsonAsync($"/api/teach/sections/{await w.Section("CS2201", "2")}", new { on = false }));
        page = await Json(await Page(student, Key(w.Teacher2Id)));
        Assert.Equal([("CS2201", "1", w.Date(0, 1).ToString("yyyy-MM-dd"), "08:00", "lecture"), ("CS2201-L", "1", w.Date(0, 2).ToString("yyyy-MM-dd"), "11:30", "lab")],
            page.GetProperty("classes").EnumerateArray().Select(c => (c.GetProperty("codes")[0].GetString(), c.GetProperty("section").GetString(), c.GetProperty("date").GetString(),
                c.GetProperty("start").GetString(), c.GetProperty("type").GetString())));
        Assert.Equal((TeachWorld.Teacher2Name, "", 0), (page.GetProperty("name").GetString(), page.GetProperty("officeRoom").GetString(), page.GetProperty("officeHours").GetArrayLength()));

        // A teacher who never signed in still has a page with their classes this week.
        page = await Json(await Page(student, TeacherKeys.ForName(TeachWorld.UnclaimedName)));
        Assert.Equal((TeachWorld.UnclaimedName, false, "", ""), (page.GetProperty("name").GetString(), page.GetProperty("hasAccount").GetBoolean(), page.GetProperty("officeRoom").GetString(), page.GetProperty("note").GetString()));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("email").ValueKind);
        Assert.Empty(page.GetProperty("officeHours").EnumerateArray());
        Assert.Equal((w.Date(0, 1).ToString("yyyy-MM-dd"), "09:45", "A102"), page.GetProperty("classes").EnumerateArray()
            .Select(c => (c.GetProperty("date").GetString(), c.GetProperty("start").GetString(), c.GetProperty("room").GetString())).Single());

        // A placeholder, a name that isn't on the timetable and nonsense have no page.
        foreach (var key in new[] { TeacherKeys.ForName(TeachWorld.PlaceholderName), TeacherKeys.ForName("Nobody Here"), TeacherKeys.ForName(" "), "n-", "n-!!!", "t-999", "t-abc", "x" })
            Assert.Equal(HttpStatusCode.NotFound, (await Page(student, key)).StatusCode);
        // Anyone signed in may read a page; nobody signed out.
        Assert.Equal(HttpStatusCode.OK, (await Page(w.Teacher2, Key(w.Teacher1Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Page(w.Rsa, TeacherKeys.ForName(TeachWorld.Teacher1Name))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Page(w.Browser(), Key(w.Teacher1Id))).StatusCode);
        // Nothing on a page says anything about students.
        var raw = await (await Page(student, Key(w.Teacher1Id))).Content.ReadAsStringAsync();
        foreach (var id in TeachWorld.StudentIdsList) Assert.DoesNotContain(id, raw);

        // The week after next has no classes for anyone here.
        w.Time.Set(new DateTimeOffset(w.Date(2, 0).ToDateTime(new TimeOnly(0, 30)), TimeSpan.FromHours(4)));
        page = await Json(await Page(w.Teacher1, Key(w.Teacher1Id)));
        Assert.Equal(w.Date(2, 0).ToString("yyyy-MM-dd"), page.GetProperty("week").GetProperty("from").GetString());
        Assert.Empty(page.GetProperty("classes").EnumerateArray());
    }

    [Fact]
    public async Task An_account_students_see_nowhere_has_no_page()
    {
        using var w = await TeachWorld.Create();
        // IT signs in: a staff address, no name on the timetable, nothing added.
        const string email = "it.support@gutech.edu.om";
        var it = await w.SignInTeacher(email);
        var id = await w.Db(db => db.Teachers.Where(t => t.Email == email).Select(t => t.Id).SingleAsync());
        await Json(await SaveOffice(it, "Server room", "", true));
        var student = w.Students[0];

        // Counting ids lists nobody and reveals no address.
        Assert.Equal(HttpStatusCode.NotFound, (await Page(student, Key(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Page(w.Teacher1, Key(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Page(w.Rsa, Key(id))).StatusCode);
        Assert.Equal("That teacher isn't on Classy.", await Error(await Page(student, Key(id))));
        Assert.Equal(await Error(await Page(student, "t-999")), await Error(await Page(student, Key(id))));
        // The account itself ("See my page as students do") and owners may.
        var own = await Json(await Page(it, Key(id)));
        Assert.Equal(("It Support", true, email), (own.GetProperty("name").GetString(), own.GetProperty("hasAccount").GetBoolean(), own.GetProperty("email").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await Page(w.Owner, Key(id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Page(await w.SignInStudentOwner(), Key(id))).StatusCode);

        // The same for a teaching assistant with nothing added: a page for themselves only.
        var assistant = await w.Services.AddTeachingStudent(w.StudentRowIds[1], "Lab Helper");
        Assert.Equal(HttpStatusCode.NotFound, (await Page(student, Key(assistant.Id))).StatusCode);
        Assert.Equal("Lab Helper", (await Json(await Page(w.Students[1], Key(assistant.Id)))).GetProperty("name").GetString());
        // Another student is not that profile's owner.
        Assert.Equal(HttpStatusCode.NotFound, (await Page(w.Students[2], Key(assistant.Id))).StatusCode);

        // Once it adds a section students see it on that class, so it has a page.
        await Json(await it.PutAsJsonAsync($"/api/teach/sections/{await w.Section("CS2201", "1")}", new { on = true }));
        var page = await Json(await Page(student, Key(id)));
        Assert.Equal(("It Support", email, "Server room"), (page.GetProperty("name").GetString(), page.GetProperty("email").GetString(), page.GetProperty("officeRoom").GetString()));
        await Json(await it.PutAsJsonAsync($"/api/teach/sections/{await w.Section("CS2201", "1")}", new { on = false }));
        Assert.Equal(HttpStatusCode.NotFound, (await Page(student, Key(id))).StatusCode);

        // A linked account whose name isn't on this semester's timetable is seen nowhere either.
        await w.Db(async db =>
        {
            db.TeacherNames.Add(new TeacherName { TeacherId = id, Name = "Ivan Tariq", NameKey = "ivan tariq", State = TeacherName.Approved, How = TeacherName.ByStaff, CreatedAt = w.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.NotFound, (await Page(student, Key(id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Page(student, TeacherKeys.ForName("Ivan Tariq"))).StatusCode);
    }
}
