using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>A canned outside API: URL → JSON reply. Remembers what was asked for.</summary>
public sealed class FakeApi : IConnectionFetcher
{
    public readonly Dictionary<string, string> Replies = [];
    public readonly List<(string Url, string Header, string Value)> Calls = [];

    public Task<string> GetAsync(Uri url, string headerName, string headerValue, CancellationToken ct)
    {
        Calls.Add((url.AbsoluteUri, headerName, headerValue));
        return Replies.TryGetValue(url.AbsoluteUri, out var json) ? Task.FromResult(json) : throw new ConnectionException("The API answered 404 Not Found.");
    }
}

/// <summary>Outside APIs: owners map any JSON timetable or student-courses API; GUtech can push enrollments; students import their courses.</summary>
public class ConnectionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 9500;
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    readonly FakeApi fake = new();

    Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> App() =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IConnectionFetcher>(fake)));

    static async Task<HttpClient> Staff(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string username, AdminRole role)
    {
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var user = new AdminUser { Username = username, Role = role, MustChangePassword = false, CreatedAt = DateTimeOffset.UtcNow };
            user.PasswordHash = AuthEndpoints.HashPassword(user, "staff-password-1");
            db.AdminUsers.Add(user);
            await db.SaveChangesAsync();
        }
        var c = app.CreateClient(new() { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "staff-password-1" })).StatusCode);
        return c;
    }

    static async Task<(HttpClient Client, string Id)> Student(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var id = $"25-{Interlocked.Increment(ref next):D4}";
        var c = app.CreateClient(new() { HandleCookies = true });
        var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).StatusCode);
        return (c, id);
    }

    static async Task<int> PublishedSemester(HttpClient owner, int year, string csv)
    {
        var made = await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year });
        Assert.True(made.IsSuccessStatusCode, $"{(int)made.StatusCode} {await made.Content.ReadAsStringAsync()}");
        var sem = (await Json(made)).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "c.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/publish", null)).StatusCode);
        return sem;
    }

    [Fact]
    public async Task Any_json_timetable_api_becomes_a_pending_sync()
    {
        using var app = App();
        var owner = await Staff(app, "owner-conn-tt", AdminRole.Owner);
        var rsa = await Staff(app, "rsa-conn-tt", AdminRole.Rsa);
        var sem = await PublishedSemester(owner, 2091,
            "code,name,major,type,year,section,instructor,date,days,time,room\nNC1101,Net Course,NCM,Core,1,1,A,2026-10-12,,08:00-09:30,R1\n");

        // Nested reply: courses → sections → classes, with a weekly class written as a days list and one time range.
        fake.Replies["https://api.test/timetable?term=Fall%202091"] = """
            {"data":{"courses":[
              {"courseCode":"NC 1 101","title":"Net Course","sections":[{"no":"1","teacher":{"name":"A"},
                "classes":[{"date":"2026-10-12","from":"08:00","to":"09:30","room":"R9"}]}]},
              {"courseCode":"NC 1 102","title":"New Course","sections":[{"no":2,"teacher":{"name":"B"},
                "classes":[{"days":["Sun","Tue"],"time":"10:00-11:30","room":"R2"}]}]}]}}
            """;
        var body = new
        {
            url = "https://api.test/timetable?term={semester}", headerName = "X-Api-Key", headerValue = "secret-123", listPath = "data.courses.*.sections.*.classes",
            fields = new Dictionary<string, string>
            {
                ["code"] = "courseCode", ["name"] = "title", ["section"] = "no", ["instructor"] = "teacher.name", ["date"] = "date",
                ["days"] = "days", ["start"] = "from", ["end"] = "to", ["time"] = "time", ["room"] = "room",
            },
            enabled = true, autoDaily = false,
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await rsa.PutAsJsonAsync("/api/admin/connections/timetable", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/admin/connections/timetable", body with { fields = new Dictionary<string, string> { ["code"] = "courseCode" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/admin/connections/timetable", body with { url = "http://api.test/x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/admin/connections/timetable", body)).StatusCode);

        // The saved secret is never sent back, but it is sent to the API.
        var listed = (await owner.GetStringAsync("/api/admin/connections"));
        Assert.DoesNotContain("secret-123", listed);
        Assert.Contains("\"hasSecret\":true", listed);

        var tested = await owner.PostAsJsonAsync("/api/admin/connections/timetable/test", new { semesterId = sem });
        Assert.True(tested.IsSuccessStatusCode, await tested.Content.ReadAsStringAsync() + " " + string.Join(",", fake.Calls.Select(c => c.Url)));
        var test = await Json(tested);
        Assert.Equal(2, test.GetProperty("count").GetInt32());
        Assert.Equal(2, test.GetProperty("courses").GetInt32());
        Assert.Equal(0, test.GetProperty("noWhen").GetInt32());
        Assert.Equal(("X-Api-Key", "secret-123"), (fake.Calls[0].Header, fake.Calls[0].Value));

        var run = await owner.PostAsJsonAsync("/api/admin/connections/timetable/run", new { semesterId = sem });
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.Equal(1, (await Json(run)).GetProperty("added").GetInt32());

        // Waits for staff, like any sync.
        var pending = (await Json(await rsa.GetAsync($"/api/admin/semesters/{sem}/sync"))).GetProperty("pending");
        Assert.Equal("Timetable API", pending.GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.OK, (await rsa.PostAsJsonAsync($"/api/admin/semesters/{sem}/sync/apply", new { id = pending.GetProperty("id").GetInt32() })).StatusCode);

        var courses = (await Json(await rsa.GetAsync($"/api/admin/semesters/{sem}/courses"))).EnumerateArray().ToList();
        Assert.Equal(["NC1101", "NC1102"], courses.Select(c => c.GetProperty("code").GetString()).Order());
        var added = courses.First(c => c.GetProperty("code").GetString() == "NC1102").GetProperty("id").GetInt32();
        var meetings = (await Json(await rsa.GetAsync($"/api/admin/courses/{added}"))).GetProperty("course").GetProperty("sections")[0].GetProperty("meetings");
        Assert.Equal(2, meetings.GetArrayLength());

        var after = (await Json(await owner.GetAsync("/api/admin/connections"))).EnumerateArray().First(c => c.GetProperty("kind").GetString() == "timetable");
        Assert.Contains("Waiting to be applied", after.GetProperty("lastResult").GetString());

        // An API that's down is reported, nothing is kept.
        fake.Replies.Clear();
        var down = await owner.PostAsJsonAsync("/api/admin/connections/timetable/run", new { semesterId = sem });
        Assert.Equal(HttpStatusCode.BadRequest, down.StatusCode);
        Assert.Contains("404", (await Json(down)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Students_import_their_courses_from_an_api_or_pushed_enrollments()
    {
        using var app = App();
        var owner = await Staff(app, "owner-conn-st", AdminRole.Owner);
        await PublishedSemester(owner, 2092,
            "code,name,major,type,year,section,instructor,date,days,time,room\n" +
            "SC1101,One,SCM,Core,1,1,A,,Sun,08:00-09:30,R1\n" +
            "SC1101,One,SCM,Core,1,2,B,,Mon,08:00-09:30,R1\n" +
            "SC1102,Two,SCM,Core,1,1,C,,Tue,08:00-09:30,R2\n" +
            "SC1103,Three,SCM,Core,1,1,D,,Wed,08:00-09:30,R3\n" +
            "SC1103,Three,SCM,Core,1,2,E,,Thu,08:00-09:30,R3\n");
        var (student, id) = await Student(app);
        var major = (await Json(await owner.GetAsync("/api/admin/majors"))).EnumerateArray().First(m => m.GetProperty("code").GetString() == "SCM").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await student.PutAsJsonAsync("/api/me/profile", new { majorId = major, year = 1 })).StatusCode);
        Assert.False((await Json(await student.GetAsync("/api/me"))).GetProperty("canImport").GetBoolean());

        // 1. A student-courses API with {studentId} in the address.
        fake.Replies[$"https://api.test/students/{id}/courses"] = """
            {"enrolments":[{"course":"SC 1 101","group":"Sec 2"},{"course":"SC 1 103","group":"9"},{"course":"XX 9 999","group":"1"}]}
            """;
        var body = new
        {
            url = "https://api.test/students/{studentId}/courses", headerName = "", listPath = "enrolments",
            fields = new Dictionary<string, string> { ["code"] = "course", ["section"] = "group" }, enabled = true, autoDaily = false,
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/admin/connections/student-courses", body with { url = "https://api.test/students" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/admin/connections/student-courses", body)).StatusCode);
        var tried = (await Json(await owner.PostAsJsonAsync("/api/admin/connections/student-courses/test", new { universityId = id }))).EnumerateArray().ToList();
        Assert.Equal([true, true, false], tried.Select(t => t.GetProperty("inClassy").GetBoolean()));

        Assert.True((await Json(await student.GetAsync("/api/me"))).GetProperty("canImport").GetBoolean());
        var imported = await Json(await student.PostAsync("/api/me/import-courses", null));
        Assert.Equal(2, imported.GetProperty("imported").GetInt32());
        Assert.Equal(["XX 9 999"], imported.GetProperty("notFound").EnumerateArray().Select(x => x.GetString()));
        // Section 9 doesn't exist, so SC1103 waits for the student to pick one.
        Assert.Equal(["SC1103"], imported.GetProperty("noSection").EnumerateArray().Select(x => x.GetString()));
        var picks = (await Json(await student.GetAsync("/api/me/picks"))).EnumerateArray().ToList();
        Assert.Equal(2, picks.Count);
        Assert.Single(picks, p => p.GetProperty("sectionId").ValueKind == JsonValueKind.Null);
        // Imported courses stay editable: saving them back is accepted.
        var back = picks.Select(p => new { courseId = p.GetProperty("courseId").GetInt32(), sectionId = (int?)null }).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await student.PutAsJsonAsync("/api/me/picks", new { picks = back, step = "core" })).StatusCode);

        // 2. API off: GUtech pushes enrollments with a sync token instead.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/admin/connections/student-courses", body with { enabled = false })).StatusCode);
        var token = (await Json(await owner.PostAsJsonAsync("/api/admin/sync-tokens", new { name = "GUtech" }))).GetProperty("token").GetString();
        var gutech = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await gutech.PostAsJsonAsync("/api/sync/enrollments", new { students = Array.Empty<object>() })).StatusCode);
        gutech.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var push = await gutech.PostAsJsonAsync("/api/sync/enrollments", new
        {
            students = new[] { new { universityId = id, courses = new[] { new { code = "SC1102", section = "1" } } } },
        });
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.PostAsync("/api/me/import-courses", null)).StatusCode);
        var now = (await Json(await student.GetAsync("/api/me/picks"))).EnumerateArray().ToList();
        Assert.Single(now);
        Assert.NotEqual(JsonValueKind.Null, now[0].GetProperty("sectionId").ValueKind);

        // Someone GUtech has nothing for.
        var (other, _) = await Student(app);
        Assert.False((await Json(await other.GetAsync("/api/me"))).GetProperty("canImport").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync("/api/me/import-courses", null)).StatusCode);
    }

    [Theory]
    [InlineData("""{"items":[{"c":"A1"},{"c":"B2"}]}""", "items", 2)]
    [InlineData("""[{"c":"A1"}]""", "", 1)]
    [InlineData("""{"a":[{"b":[{"c":"A1"},{"c":"A2"}]},{"b":[{"c":"A3"}]}]}""", "a.*.b", 3)]
    public void Lists_are_found_by_path(string json, string path, int count) =>
        Assert.Equal(count, Connections.Extract(json, path, new() { ["code"] = "c" }).Count);

    [Fact]
    public void A_missing_list_or_bad_json_is_explained()
    {
        Assert.Throws<ConnectionException>(() => Connections.Extract("{\"items\":[]}", "rows", new() { ["code"] = "c" }));
        Assert.Throws<ConnectionException>(() => Connections.Extract("<html>", "", new() { ["code"] = "c" }));
    }
}
