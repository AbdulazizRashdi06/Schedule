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
