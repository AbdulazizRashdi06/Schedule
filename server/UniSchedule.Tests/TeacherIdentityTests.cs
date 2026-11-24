using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// Teachers, students and dashboard accounts are told apart everywhere: a teacher's cookie opens nothing of a
/// student's or of the dashboard, /api/teach opens only for someone who teaches, and an owner's "view as" is theirs alone.
/// </summary>
public class TeacherIdentityTests
{
    static async Task<JsonElement> Me(HttpClient c) => await Json(await c.GetAsync("/api/teach/me"));

    /// <summary>Every mapped route under a prefix, with its route values filled in.</summary>
    static List<(HttpMethod Method, string Path)> Routes(TeachWorld w, params string[] prefixes) =>
        w.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && !raw.Contains("**") && prefixes.Any(p => raw == p || raw.StartsWith(p + "/")))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => (new HttpMethod(m), Regex.Replace(e.RoutePattern.RawText!, @"\{[^}]+\}", "1").TrimEnd('/'))))
            .Distinct().ToList();

    static async Task<HttpStatusCode> Send(HttpClient c, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get) request.Content = JsonContent.Create(new { });
        return (await c.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task A_teacher_opens_nothing_of_a_students_or_of_the_dashboard()
    {
        using var w = await TeachWorld.Create();
        // Teacher 1 and student 1 share an id and have different data, so reading the wrong table would show.
        Assert.Equal(1, w.Teacher1Id);
        Assert.Equal(1, w.StudentRowIds[0]);

        var routes = Routes(w, "/api/me", "/api/admin", "/api/rep", "/api/groups");
        Assert.True(routes.Count > 100, $"Only {routes.Count} routes found.");
        Assert.Contains(routes, r => r.Path == "/api/me/timetable");
        Assert.Contains(routes, r => r.Path == "/api/admin/students");
        Assert.Contains(routes, r => r.Path == "/api/rep/bootstrap");
        foreach (var (method, path) in routes)
        {
            var status = await Send(w.Teacher1, method, path);
            Assert.True(status == HttpStatusCode.Forbidden, $"{method} {path} answered {(int)status} to a teacher.");
        }
        // Dashboard passwords are not theirs either.
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Teacher1.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "x", newPassword = "y" })).StatusCode);

        // What any signed-in person may read stays open to them.
        Assert.Equal(HttpStatusCode.OK, (await w.Teacher1.GetAsync("/api/rooms/free")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Teacher1.GetAsync("/api/majors")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Teacher1.GetAsync("/api/teach/me")).StatusCode);
        // And the student with the same id is still themselves.
        Assert.Equal(TeachWorld.StudentIdsList[0], (await Json(await w.Students[0].GetAsync("/api/me"))).GetProperty("universityId").GetString());
        Assert.Equal(2, (await Json(await w.Students[0].GetAsync("/api/me/timetable"))).GetProperty("classes").GetArrayLength());
    }

    [Fact]
    public async Task Teach_routes_open_only_for_someone_who_teaches()
    {
        using var w = await TeachWorld.Create();
        var routes = Routes(w, "/api/teach");
        Assert.Contains((HttpMethod.Get, "/api/teach/me"), routes);
        Assert.Contains((HttpMethod.Post, "/api/teach/names"), routes);
        var signedOut = w.Browser();
        foreach (var (method, path) in routes)
        {
            // A plain student, an RSA and a dashboard owner without "view as" are refused; nobody signed in is 401.
            foreach (var (who, c) in new[] { ("a student", w.Students[1]), ("an RSA", w.Rsa), ("a dashboard owner", w.Owner) })
            {
                var status = await Send(c, method, path);
                Assert.True(status == HttpStatusCode.Forbidden, $"{method} {path} answered {(int)status} to {who}.");
            }
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(signedOut, method, path));
        }
        Assert.Equal("Teaching isn't turned on for this account.", await Error(await w.Students[1].GetAsync("/api/teach/me")));
        Assert.Equal("Open the teacher view from the dashboard.", await Error(await w.Owner.GetAsync("/api/teach/me")));

        var me = await Me(w.Teacher1);
        Assert.Equal(["id", "email", "name", "kind", "as", "personal", "semester", "plannerEnabled", "status", "names", "frozen", "changesEnabled", "codeFresh", "notices"],
            me.EnumerateObject().Select(p => p.Name));
        Assert.Equal((w.Teacher1Id, TeachWorld.Teacher1Email, TeachWorld.Teacher1Name, "staff", "linked"),
            (me.GetProperty("id").GetInt32(), me.GetProperty("email").GetString(), me.GetProperty("name").GetString(), me.GetProperty("kind").GetString(), me.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, me.GetProperty("as").ValueKind);
        Assert.True(me.GetProperty("personal").GetBoolean());
        Assert.Equal(w.Semester, me.GetProperty("semester").GetProperty("id").GetInt32());
        Assert.True(me.GetProperty("semester").TryGetProperty("weeks", out _));
        Assert.False(me.GetProperty("plannerEnabled").GetBoolean());
        Assert.False(me.GetProperty("frozen").GetBoolean());
        Assert.True(me.GetProperty("changesEnabled").GetBoolean());
        Assert.True(me.GetProperty("codeFresh").GetBoolean());
        Assert.Empty(me.GetProperty("notices").EnumerateArray());
        // The other teacher is the other teacher.
        Assert.Equal((w.Teacher2Id, TeachWorld.Teacher2Name), ((await Me(w.Teacher2)).GetProperty("id").GetInt32(), (await Me(w.Teacher2)).GetProperty("name").GetString()));
    }

    [Fact]
    public async Task A_student_with_teaching_rights_reaches_the_teacher_side_and_is_still_a_student()
    {
        using var w = await TeachWorld.Create();
        var student = w.Students[0];
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/teach/me")).StatusCode);
        Assert.False((await Json(await student.GetAsync("/api/auth/session"))).GetProperty("teaching").GetBoolean());

        // The grant comes after this browser's sign-in.
        w.Time.Advance(TimeSpan.FromMinutes(5));
        var profile = await w.Services.AddTeachingStudent(w.StudentRowIds[0], "Lab Helper");
        var me = await Me(student);
        Assert.Equal((profile.Id, "Lab Helper", "student", "none"),
            (me.GetProperty("id").GetInt32(), me.GetProperty("name").GetString(), me.GetProperty("kind").GetString(), me.GetProperty("status").GetString()));
        Assert.Equal(JsonValueKind.Null, me.GetProperty("email").ValueKind);
        Assert.True(me.GetProperty("personal").GetBoolean());
        // Never the id of the staff account that happens to have the student's id.
        Assert.NotEqual(w.StudentRowIds[0], profile.Id);

        // Still a student everywhere else, with their own data.
        var session = await Json(await student.GetAsync("/api/auth/session"));
        Assert.Equal(("student", TeachWorld.StudentIdsList[0]), (session.GetProperty("kind").GetString(), session.GetProperty("name").GetString()));
        Assert.True(session.GetProperty("teaching").GetBoolean());
        Assert.Equal(TeachWorld.StudentIdsList[0], (await Json(await student.GetAsync("/api/me"))).GetProperty("universityId").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/admin/stats")).StatusCode);
        // The other students got nothing from it.
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Students[1].GetAsync("/api/teach/me")).StatusCode);

        // A code entered before the grant doesn't count as fresh: a new one is needed before the first class change.
        Assert.False(session.GetProperty("codeFresh").GetBoolean());
        Assert.False(me.GetProperty("codeFresh").GetBoolean());
        await SignInStudent(student, w.Emails, TeachWorld.StudentIdsList[0]);
        Assert.True((await Json(await student.GetAsync("/api/auth/session"))).GetProperty("codeFresh").GetBoolean());
        Assert.True((await Me(student)).GetProperty("codeFresh").GetBoolean());

        // The name step and "sign out everywhere" are for staff accounts: owners attach a teaching assistant's names.
        Assert.Equal(HttpStatusCode.Conflict, (await student.GetAsync("/api/teach/names")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await student.PostAsJsonAsync("/api/teach/names", new { none = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await student.PostAsync("/api/teach/sign-out-everywhere", null)).StatusCode);
        Assert.Equal("none", (await Me(student)).GetProperty("status").GetString());
        // A name an owner attached makes them linked, and never asks them to pick.
        await w.Db(async db =>
        {
            db.TeacherNames.Add(new TeacherName { TeacherId = profile.Id, Name = TeachWorld.UnclaimedName, NameKey = TeacherNames.NameKey(TeachWorld.UnclaimedName), State = TeacherName.Approved, How = TeacherName.ByStaff });
            await db.SaveChangesAsync();
        });
        me = await Me(student);
        Assert.Equal(("linked", TeachWorld.UnclaimedName), (me.GetProperty("status").GetString(), me.GetProperty("name").GetString()));

        // Teaching rights taken away: the teacher side closes, the student account stays.
        await w.Db(db => db.Teachers.Where(t => t.Id == profile.Id).ExecuteDeleteAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/teach/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync("/api/me")).StatusCode);
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task Removing_a_student_removes_their_teaching_profile()
    {
        using var w = await TeachWorld.Create();
        var profile = await w.Services.AddTeachingStudent(w.StudentRowIds[2], "Lab Helper");
        await w.Db(db => db.Students.Where(s => s.Id == w.StudentRowIds[2]).ExecuteDeleteAsync());
        Assert.False(await w.Db(db => db.Teachers.AnyAsync(t => t.Id == profile.Id)));
        Assert.Equal(2, await w.Db(db => db.Teachers.CountAsync()));
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task View_as_works_for_owners_by_account_and_by_name()
    {
        using var w = await TeachWorld.Create();
        var studentOwner = await w.SignInStudentOwner();
        foreach (var owner in new[] { w.Owner, studentOwner })
        {
            // By account.
            var me = await Me(TeachWorld.ViewAs(owner, TeacherKeys.For(new Teacher { Id = w.Teacher1Id })));
            Assert.Equal((w.Teacher1Id, TeachWorld.Teacher1Email, TeachWorld.Teacher1Name, "view", TeachWorld.Teacher1Name, "linked"),
                (me.GetProperty("id").GetInt32(), me.GetProperty("email").GetString(), me.GetProperty("name").GetString(), me.GetProperty("kind").GetString(),
                    me.GetProperty("as").GetString(), me.GetProperty("status").GetString()));
            Assert.False(me.GetProperty("personal").GetBoolean());
            Assert.True(me.GetProperty("codeFresh").GetBoolean());

            // By name, however it is spelt: the account that holds it.
            me = await Me(TeachWorld.ViewAs(owner, TeacherKeys.ForName("dr huda  AL KINDI")));
            Assert.Equal((w.Teacher2Id, "view", TeachWorld.Teacher2Name), (me.GetProperty("id").GetInt32(), me.GetProperty("kind").GetString(), me.GetProperty("as").GetString()));

            // A name nobody has claimed, and a placeholder: owners manage those sections this way.
            foreach (var name in new[] { TeachWorld.UnclaimedName, TeachWorld.PlaceholderName })
            {
                me = await Me(TeachWorld.ViewAs(owner, TeacherKeys.ForName(name)));
                Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, name, "view", name, "linked"),
                    (me.GetProperty("id").ValueKind, me.GetProperty("email").ValueKind, me.GetProperty("name").GetString(), me.GetProperty("kind").GetString(),
                        me.GetProperty("as").GetString(), me.GetProperty("status").GetString()));
                Assert.Empty(me.GetProperty("names").EnumerateArray());
                Assert.Empty(me.GetProperty("notices").EnumerateArray());
            }

            // Nothing there.
            foreach (var key in new[] { "t-999", TeacherKeys.ForName("Nobody Here"), "n-", "n-!!!", "x-1", "t-abc", "1" })
                Assert.Equal(HttpStatusCode.NotFound, (await TeachWorld.ViewAs(owner, key).GetAsync("/api/teach/me")).StatusCode);
            owner.DefaultRequestHeaders.Remove(TeachActor.ViewAsHeader);
        }

        // A student owner without the header is a student like any other: no teaching unless it is turned on for them.
        Assert.Equal(HttpStatusCode.Forbidden, (await studentOwner.GetAsync("/api/teach/me")).StatusCode);
        // Looking in links nothing and changes nothing on the account.
        Assert.Equal(2, await w.Db(db => db.TeacherNames.CountAsync()));
    }

    [Fact]
    public async Task View_as_is_refused_for_everyone_who_is_not_an_owner()
    {
        using var w = await TeachWorld.Create();
        await w.Services.AddTeachingStudent(w.StudentRowIds[1], "Lab Helper");
        var key = TeacherKeys.For(new Teacher { Id = w.Teacher2Id });
        foreach (var (who, c) in new[] { ("an RSA", w.Rsa), ("a teacher", w.Teacher1), ("a student", w.Students[0]), ("a teaching assistant", w.Students[1]) })
        {
            var r = await TeachWorld.ViewAs(c, key).GetAsync("/api/teach/me");
            Assert.True(r.StatusCode == HttpStatusCode.Forbidden, $"View as answered {(int)r.StatusCode} to {who}.");
            Assert.Equal("Only owners can open another teacher's view.", await Error(r));
            // Their own key is no way round it.
            Assert.Equal(HttpStatusCode.Forbidden, (await TeachWorld.ViewAs(c, TeacherKeys.For(new Teacher { Id = w.Teacher1Id })).GetAsync("/api/teach/me")).StatusCode);
            c.DefaultRequestHeaders.Remove(TeachActor.ViewAsHeader);
        }
        // Without the header each is who they were.
        Assert.Equal(w.Teacher1Id, (await Me(w.Teacher1)).GetProperty("id").GetInt32());
        Assert.Equal("student", (await Me(w.Students[1])).GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await TeachWorld.ViewAs(w.Browser(), key).GetAsync("/api/teach/me")).StatusCode);
    }

    static TeachNotice Notice(int teacherId, DateTimeOffset at, string title = "Classy staff undid your change") => new()
    {
        Audience = TeachNotice.ToTeacher, TeacherId = teacherId, Kind = "undone", Title = title, Body = "BE1101 on Sunday is back on.", Url = "/teach", Tag = "t", CreatedAt = at,
    };

    [Fact]
    public async Task Personal_routes_answer_409_in_view_as_and_looking_in_changes_nothing()
    {
        using var w = await TeachWorld.Create();
        var notice = await w.Db(async db =>
        {
            var n = Notice(w.Teacher1Id, w.Time.GetUtcNow());
            db.TeachNotices.Add(n);
            await db.SaveChangesAsync();
            return n.Id;
        });
        var studentOwner = await w.SignInStudentOwner();
        foreach (var owner in new[] { w.Owner, studentOwner })
        {
            var viewing = TeachWorld.ViewAs(owner, TeacherKeys.For(new Teacher { Id = w.Teacher1Id }));
            // Reads and writes alike.
            foreach (var r in new[]
            {
                await viewing.GetAsync("/api/teach/names"),
                await viewing.PostAsJsonAsync("/api/teach/names", new { name = TeachWorld.UnclaimedName }),
                await viewing.PostAsJsonAsync("/api/teach/names", new { none = true }),
                await viewing.PostAsJsonAsync("/api/teach/notices/seen", new { ids = new[] { notice } }),
                await viewing.PostAsync("/api/teach/sign-out-everywhere", null),
            })
            {
                Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
                Assert.Equal("Not available while viewing as a teacher.", await Error(r));
            }
            // The teacher's notices show to the owner, and stay unseen.
            Assert.Equal(notice, Assert.Single((await Me(viewing)).GetProperty("notices").EnumerateArray()).GetProperty("id").GetInt32());
        }

        var teacher = await w.Db(db => db.Teachers.AsNoTracking().SingleAsync(t => t.Id == w.Teacher1Id));
        Assert.Equal((0, (DateTimeOffset?)null), (teacher.SessionVersion, teacher.NameSkippedAt));
        Assert.Equal(TeachWorld.Teacher1Name, (await w.Db(db => db.TeacherNames.SingleAsync(n => n.TeacherId == w.Teacher1Id))).Name);
        Assert.Null(await w.Db(db => db.TeachNotices.Where(n => n.Id == notice).Select(n => n.SeenAt).SingleAsync()));
        // The teacher is still signed in.
        Assert.Equal(notice, Assert.Single((await Me(w.Teacher1)).GetProperty("notices").EnumerateArray()).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Marking_notices_seen_touches_only_the_callers_own()
    {
        using var w = await TeachWorld.Create();
        var now = w.Time.GetUtcNow();
        var (mine, old, theirs, forStaff) = await w.Db(async db =>
        {
            TeachNotice a = Notice(w.Teacher1Id, now, "Newest"), b = Notice(w.Teacher1Id, now.AddDays(-15)), c = Notice(w.Teacher2Id, now);
            var d = new TeachNotice { Audience = TeachNotice.Staff, Kind = "many-changes", Title = "Busy", Body = "x", Url = "/admin", Tag = "s", CreatedAt = now };
            db.TeachNotices.AddRange(b, c, d, a);
            await db.SaveChangesAsync();
            return (a.Id, b.Id, c.Id, d.Id);
        });

        // The inbox: unseen notices of the last 14 days, newest first, with their text.
        var inbox = (await Me(w.Teacher1)).GetProperty("notices");
        var shown = Assert.Single(inbox.EnumerateArray());
        Assert.Equal(["id", "kind", "title", "body", "url", "at"], shown.EnumerateObject().Select(p => p.Name));
        Assert.Equal((mine, "undone", "Newest", "BE1101 on Sunday is back on.", "/teach"),
            (shown.GetProperty("id").GetInt32(), shown.GetProperty("kind").GetString(), shown.GetProperty("title").GetString(), shown.GetProperty("body").GetString(), shown.GetProperty("url").GetString()));
        Assert.Equal(theirs, Assert.Single((await Me(w.Teacher2)).GetProperty("notices").EnumerateArray()).GetProperty("id").GetInt32());

        // Counting ids clears nobody else's inbox.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/teach/notices/seen", new { ids = new[] { mine, theirs, forStaff, 9999 } })).StatusCode);
        var seen = await w.Db(db => db.TeachNotices.Where(n => n.SeenAt != null).Select(n => n.Id).ToListAsync());
        Assert.Equal([mine], seen);
        Assert.Empty((await Me(w.Teacher1)).GetProperty("notices").EnumerateArray());
        Assert.Single((await Me(w.Teacher2)).GetProperty("notices").EnumerateArray());
        _ = old;

        // Between 1 and 100 ids.
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Teacher1.PostAsJsonAsync("/api/teach/notices/seen", new { ids = Array.Empty<int>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Teacher1.PostAsJsonAsync("/api/teach/notices/seen", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Teacher1.PostAsJsonAsync("/api/teach/notices/seen", new { ids = Enumerable.Range(1, 101) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/teach/notices/seen", new { ids = Enumerable.Range(1, 100) })).StatusCode);
        Assert.Null(await w.Db(db => db.TeachNotices.Where(n => n.Id == theirs).Select(n => n.SeenAt).SingleAsync()));
    }

    [Fact]
    public async Task What_an_owner_does_through_view_as_is_kept_under_the_owner_and_the_teacher_is_told()
    {
        using var w = await TeachWorld.Create();
        var other = await w.Section("BE1101", "2");
        var own = await w.Section("LOG1101", "1");
        var studentOwner = await w.SignInStudentOwner();
        var key = TeacherKeys.For(new Teacher { Id = w.Teacher1Id });

        // A dashboard owner adds a class to the teacher's timetable; a student owner hides one under the teacher's name.
        var row = await Json(await TeachWorld.ViewAs(w.Owner, key).PutAsJsonAsync($"/api/teach/sections/{other}", new { on = true }));
        Assert.Equal("added", row.GetProperty("state").GetString());
        Assert.Equal("hidden", (await Json(await TeachWorld.ViewAs(studentOwner, key).PutAsJsonAsync($"/api/teach/sections/{own}", new { on = false }))).GetProperty("state").GetString());
        var rows = await w.Db(db => db.TeacherSections.AsNoTracking().OrderBy(r => r.Id).ToListAsync());
        Assert.Equal([(w.Teacher1Id, (int?)other, TeacherSection.AddedMode, (string?)TeachWorld.OwnerName), (w.Teacher1Id, own, TeacherSection.HiddenMode, ApiFactory.OwnerId)],
            rows.Select(r => (r.TeacherId, r.SectionId, r.Mode, r.By)));
        // The owner's own account got nothing, and the teacher sees it on their timetable.
        var classes = (await Json(await w.Teacher1.GetAsync("/api/teach/timetable"))).GetProperty("classes").EnumerateArray()
            .Select(c => $"{c.GetProperty("code").GetString()}/{c.GetProperty("section").GetProperty("number").GetString()}").ToList();
        Assert.Equal(["BE1101/1", "BE1101/2", "IBSM1101/1"], classes);
        // What the teacher ticks themselves carries nobody's name.
        await Json(await w.Teacher1.PutAsJsonAsync($"/api/teach/sections/{await w.Section("CS2201", "1")}", new { on = true }));
        Assert.Null(await w.Db(db => db.TeacherSections.Where(r => r.CourseCode == "CS2201").Select(r => r.By).SingleAsync()));

        // The owner edits the office hours.
        var office = await Json(await TeachWorld.ViewAs(w.Owner, key).PutAsJsonAsync("/api/teach/office",
            new { officeRoom = "B-204", note = "", hours = new[] { new { day = 1, start = "10:00", end = "11:00", room = "B-204" } } }));
        Assert.Equal((TeachWorld.Teacher1Name, "B-204", false), (office.GetProperty("name").GetString(), office.GetProperty("officeRoom").GetString(), office.GetProperty("showEmail").GetBoolean()));
        Assert.Equal(w.Teacher1Id, await w.Db(db => db.OfficeHours.Select(h => h.TeacherId).SingleAsync()));

        // Each edit is a notice in the teacher's inbox, with the owner's label kept for the log and one tag for the run.
        var notices = await w.Db(db => db.TeachNotices.AsNoTracking().OrderBy(n => n.Id).ToListAsync());
        Assert.Equal(
        [
            ("Classy staff added BE1101 section 2 to your classes", TeachWorld.OwnerName),
            ("Classy staff hid LOG1101 section 1 on your timetable", ApiFactory.OwnerId),
            ("Classy staff changed your office hours", TeachWorld.OwnerName),
        ], notices.Select(n => (n.Title, n.ByLabel!)));
        Assert.All(notices, n => Assert.Equal((TeachNotice.ToTeacher, (int?)w.Teacher1Id, "staff-edit", $"staff-edit-{w.Teacher1Id}", (DateTimeOffset?)null),
            (n.Audience, n.TeacherId, n.Kind, n.Tag, n.SentAt)));
        // The teacher sees them; who it was is never in what they are sent.
        var inbox = await (await w.Teacher1.GetAsync("/api/teach/me")).Content.ReadAsStringAsync();
        Assert.Equal(3, JsonDocument.Parse(inbox).RootElement.GetProperty("notices").GetArrayLength());
        Assert.DoesNotContain(TeachWorld.OwnerName, inbox);
        Assert.DoesNotContain(ApiFactory.OwnerId, inbox);
        // A tick that changes nothing tells nobody anything; nor does anything the teacher does themselves.
        await Json(await TeachWorld.ViewAs(w.Owner, key).PutAsJsonAsync($"/api/teach/sections/{other}", new { on = true }));
        await Json(await w.Teacher1.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-205" }));
        Assert.Equal(3, await w.Db(db => db.TeachNotices.CountAsync()));
    }

    [Fact]
    public async Task An_owner_can_never_make_a_teachers_email_show()
    {
        using var w = await TeachWorld.Create();
        var viewing = TeachWorld.ViewAs(w.Owner, TeacherKeys.For(new Teacher { Id = w.Teacher1Id }));
        var refused = await viewing.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-204", showEmail = true });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("Only the teacher can show their email.", await Error(refused));
        // Nothing of that save was kept, and nobody was told.
        var teacher = await w.Db(db => db.Teachers.AsNoTracking().SingleAsync(t => t.Id == w.Teacher1Id));
        Assert.Equal(("", false), (teacher.OfficeRoom, teacher.ShowEmail));
        Assert.Equal(0, await w.Db(db => db.TeachNotices.CountAsync()));

        // The teacher turns it on. An owner's save leaves it as it is (sending true again is not turning it on)...
        await Json(await w.Teacher1.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-204", showEmail = true }));
        Assert.True((await Json(await viewing.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-205", showEmail = true }))).GetProperty("showEmail").GetBoolean());
        Assert.True((await Json(await viewing.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-205" }))).GetProperty("showEmail").GetBoolean());
        // ...and may turn it off, but then not back on.
        Assert.False((await Json(await viewing.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-205", showEmail = false }))).GetProperty("showEmail").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await viewing.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-205", showEmail = true })).StatusCode);
        Assert.False(await w.Db(db => db.Teachers.Where(t => t.Id == w.Teacher1Id).Select(t => t.ShowEmail).SingleAsync()));
    }

    [Fact]
    public async Task Viewing_a_name_nobody_claimed_shows_its_classes_and_has_no_account_to_edit()
    {
        using var w = await TeachWorld.Create();
        foreach (var (name, code) in new[] { (TeachWorld.UnclaimedName, "BE1101"), (TeachWorld.PlaceholderName, "CS2201-L") })
        {
            var viewing = TeachWorld.ViewAs(w.Owner, TeacherKeys.ForName(name));
            var tt = await Json(await viewing.GetAsync("/api/teach/timetable"));
            var only = Assert.Single(tt.GetProperty("classes").EnumerateArray());
            Assert.Equal((code, true, false), (only.GetProperty("code").GetString(), only.GetProperty("own").GetBoolean(), only.GetProperty("added").GetBoolean()));
            Assert.Empty(tt.GetProperty("slots").EnumerateArray());
            Assert.Empty(tt.GetProperty("officeHours").EnumerateArray());
            Assert.Single((await Json(await viewing.GetAsync("/api/teach/sections"))).GetProperty("mine").EnumerateArray());

            // Sections, office hours and details belong to an account.
            foreach (var r in new[]
            {
                await viewing.PutAsJsonAsync($"/api/teach/sections/{await w.Section("CS2201", "1")}", new { on = true }),
                await viewing.GetAsync("/api/teach/office"),
                await viewing.PutAsJsonAsync("/api/teach/office", new { officeRoom = "B-204" }),
            })
            {
                Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
                Assert.Equal("This name has no Classy account yet.", await Error(r));
            }
            Assert.Equal(HttpStatusCode.NoContent, (await viewing.PostAsJsonAsync("/api/teach/seen", new { sectionId = await w.Section(code, code == "BE1101" ? "2" : "1") })).StatusCode);
        }
        Assert.Equal((0, 0, 0, 0), await w.Db(async db => (await db.TeacherSections.CountAsync(), await db.OfficeHours.CountAsync(), await db.TeachNotices.CountAsync(), await db.TeacherSeens.CountAsync())));
    }

    [Fact]
    public async Task Own_slots_are_private_to_their_teacher()
    {
        using var w = await TeachWorld.Create();
        var body = new { title = "Dentist", day = 2, start = "15:00", end = "16:00" };
        var mine = (await Json(await w.Teacher1.PostAsJsonAsync("/api/teach/slots", body))).GetProperty("id").GetInt32();
        // Student 1 has a slot with the same row id in their own table.
        Assert.Equal(mine, (await Json(await w.Students[0].PostAsJsonAsync("/api/me/slots", new { title = "Gym", day = 2, start = "15:00", end = "16:00" }))).GetProperty("id").GetInt32());

        // Another teacher's slot id is a 404, to read into, change or remove.
        Assert.Equal(HttpStatusCode.NotFound, (await w.Teacher2.PutAsJsonAsync($"/api/teach/slots/{mine}", new { title = "Taken", day = 2, start = "15:00", end = "16:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Teacher2.DeleteAsync($"/api/teach/slots/{mine}")).StatusCode);
        Assert.Empty((await Json(await w.Teacher2.GetAsync("/api/teach/slots"))).EnumerateArray());
        // A teaching assistant's are their teaching profile's, not their student slots.
        await w.Services.AddTeachingStudent(w.StudentRowIds[0], "Lab Helper");
        Assert.Empty((await Json(await w.Students[0].GetAsync("/api/teach/slots"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Students[0].DeleteAsync($"/api/teach/slots/{mine}")).StatusCode);
        var theirs = (await Json(await w.Students[0].PostAsJsonAsync("/api/teach/slots", body))).GetProperty("id").GetInt32();
        Assert.Equal("Gym", Assert.Single((await Json(await w.Students[0].GetAsync("/api/me/slots"))).EnumerateArray()).GetProperty("title").GetString());

        // An owner looking in neither sees nor touches them: 409, reads and writes alike, and none on the timetable.
        var studentOwner = await w.SignInStudentOwner();
        foreach (var owner in new[] { w.Owner, studentOwner })
        {
            var viewing = TeachWorld.ViewAs(owner, TeacherKeys.For(new Teacher { Id = w.Teacher1Id }));
            foreach (var r in new[]
            {
                await viewing.GetAsync("/api/teach/slots"),
                await viewing.PostAsJsonAsync("/api/teach/slots", body),
                await viewing.PutAsJsonAsync($"/api/teach/slots/{mine}", body),
                await viewing.DeleteAsync($"/api/teach/slots/{mine}"),
            })
            {
                Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
                Assert.Equal("Not available while viewing as a teacher.", await Error(r));
            }
            var tt = await Json(await viewing.GetAsync("/api/teach/timetable"));
            Assert.Equal(3, tt.GetProperty("classes").GetArrayLength());
            Assert.Empty(tt.GetProperty("slots").EnumerateArray());
        }

        // Everything is where it was.
        var slots = await w.Db(db => db.TeacherSlots.AsNoTracking().OrderBy(s => s.Id).ToListAsync());
        Assert.Equal([(mine, w.Teacher1Id, "Dentist"), (theirs, slots[^1].TeacherId, "Dentist")], slots.Select(s => (s.Id, s.TeacherId, s.Title)));
        Assert.NotEqual(w.Teacher1Id, slots[^1].TeacherId);
        Assert.Equal("Dentist", Assert.Single((await Json(await w.Teacher1.GetAsync("/api/teach/timetable"))).GetProperty("slots").EnumerateArray()).GetProperty("title").GetString());
    }

    static HttpContext As(string kind, int id) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(Session.KindClaim, kind)], CookieAuthenticationDefaults.AuthenticationScheme)),
    };

    [Fact]
    public async Task ActorName_names_each_kind_of_sign_in_from_its_own_table()
    {
        using var w = await TeachWorld.Create();
        var admin = await w.Db(db => db.AdminUsers.Where(a => a.Username == TeachWorld.OwnerName).Select(a => a.Id).SingleAsync());
        await w.Db(async db =>
        {
            // Id 1 is a different person in each table.
            Assert.Equal(TeachWorld.Teacher1Email, await OwnerEndpoints.ActorName(As("teacher", 1), db, CancellationToken.None));
            Assert.Equal(TeachWorld.StudentIdsList[0], await OwnerEndpoints.ActorName(As("student", 1), db, CancellationToken.None));
            Assert.Equal(TeachWorld.OwnerName, await OwnerEndpoints.ActorName(As("admin", admin), db, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => OwnerEndpoints.ActorName(As("visitor", 1), db, CancellationToken.None));
        });
    }

    [Fact]
    public void A_code_is_fresh_for_seven_days_and_only_from_after_a_grant()
    {
        var now = new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
        ClaimsPrincipal With(DateTimeOffset? codeAt) => new(new ClaimsIdentity(
            codeAt is null ? [] : [new Claim(Session.CodeAtClaim, codeAt.Value.ToUnixTimeSeconds().ToString())], "test"));

        Assert.True(With(now).CodeIsFresh(now));
        Assert.True(With(now.AddDays(-7)).CodeIsFresh(now));
        Assert.False(With(now.AddDays(-7).AddSeconds(-1)).CodeIsFresh(now));
        // A cookie from before the claim existed is stale.
        Assert.False(With(null).CodeIsFresh(now));
        Assert.Null(With(null).CodeAt());
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity([new Claim(Session.CodeAtClaim, "soon")], "test")).CodeIsFresh(now));
        // A student given teaching rights: the code must be from the grant on.
        var grant = now.AddHours(-1);
        Assert.False(With(grant.AddSeconds(-1)).CodeIsFresh(now, grant));
        Assert.True(With(grant).CodeIsFresh(now, grant));
        Assert.True(With(grant.AddMinutes(1)).CodeIsFresh(now, grant));
        Assert.False(With(now.AddDays(-8)).CodeIsFresh(now, now.AddDays(-9)));
    }
}
