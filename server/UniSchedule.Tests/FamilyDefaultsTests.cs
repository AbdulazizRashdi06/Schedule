using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>Major families share courses; defaults come from the major, then its family, then the upload.</summary>
public class FamilyDefaultsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 9000;
    static string NewId() => $"25-{Interlocked.Increment(ref next):D4}";
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<HttpClient> Owner(string username)
    {
        await factory.AddAdmin(username, "owner-password-1", AdminRole.Owner, mustChange: false);
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "owner-password-1" })).StatusCode);
        return c;
    }

    async Task<HttpClient> Student(int majorId, int year)
    {
        var id = NewId();
        var c = factory.Client();
        var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/me/profile", new { majorId, year })).StatusCode);
        return c;
    }

    /// <summary>Uploads and publishes a semester with majors F{tag}CS and F{tag}AI, both in family F{tag}COMP.</summary>
    async Task<(HttpClient Owner, int Semester, int Cs, int Ai, Dictionary<string, int> Courses)> Setup(string tag, int year)
    {
        var c = await Owner($"owner-family-{tag.ToLowerInvariant()}");
        var csv = "code,name,major,type,year,section,instructor,days,time,room\n" +
            $"{tag}101,Programming,{tag}CS,Core,1,1,A,Sun,08:00-09:30,R1\n" +
            $"{tag}102,Intro to AI,{tag}AI,Core,1,1,B,Mon,08:00-09:30,R2\n" +
            $"{tag}201,Data Structures,{tag}CS,Core,2,1,C,Tue,08:00-09:30,R3\n" +
            $"{tag}LS,English,,LS,,1,D,Thu,08:00-09:30,R4\n";
        var id = (await Json(await c.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "fall.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/admin/semesters/{id}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/admin/semesters/{id}/publish", null)).StatusCode);

        var majors = (await Json(await c.GetAsync("/api/admin/majors"))).EnumerateArray().ToList();
        int MajorId(string code) => majors.First(m => m.GetProperty("code").GetString() == code).GetProperty("id").GetInt32();
        var cs = MajorId($"{tag}CS");
        var ai = MajorId($"{tag}AI");
        foreach (var (m, name) in new[] { (cs, "Computer Science"), (ai, "Artificial Intelligence") })
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/admin/majors/{m}", new { code = m == cs ? $"{tag}CS" : $"{tag}AI", name, family = $"{tag}COMP" })).StatusCode);

        var courses = (await Json(await c.GetAsync($"/api/admin/semesters/{id}/courses"))).EnumerateArray()
            .ToDictionary(x => x.GetProperty("code").GetString()!, x => x.GetProperty("id").GetInt32());
        return (c, id, cs, ai, courses);
    }

    static async Task<HashSet<int>> Picked(HttpClient student) =>
        (await Json(await student.GetAsync("/api/me/picks"))).EnumerateArray().Select(p => p.GetProperty("courseId").GetInt32()).ToHashSet();

    [Fact]
    public async Task Majors_in_a_family_see_each_others_core_courses()
    {
        var (_, _, _, ai, courses) = await Setup("FA", 2041);
        var student = await Student(ai, 1);
        var options = (await Json(await student.GetAsync("/api/me/options"))).GetProperty("courses").EnumerateArray()
            .Select(x => x.GetProperty("code").GetString()).ToHashSet();
        Assert.Contains("FA101", options);
        Assert.Contains("FA201", options);
        Assert.Contains("FA102", options);
        Assert.Equal(courses.Count, options.Count);
    }

    [Fact]
    public async Task Defaults_come_from_the_major_then_the_family_then_the_upload()
    {
        var (owner, semester, cs, ai, courses) = await Setup("FB", 2042);
        var saved = await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new object[]
        {
            new { id = "fb101", major = "FBCOMP", year = 1 },
            new { id = "FB 102", major = "FBAI", year = 1 },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2, (await Json(saved)).GetProperty("saved").GetInt32());

        // CS has no defaults of its own, so it gets the family's.
        Assert.Equal([courses["FB101"]], await Picked(await Student(cs, 1)));
        // AI's own defaults replace the family's.
        Assert.Equal([courses["FB102"]], await Picked(await Student(ai, 1)));
        // No defaults for year 2: the upload's core courses for the major and year.
        Assert.Equal([courses["FB201"]], await Picked(await Student(cs, 2)));

        var listed = (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/defaults"))).EnumerateArray()
            .Select(d => $"{d.GetProperty("id").GetString()}|{d.GetProperty("major").GetString()}|{d.GetProperty("year").GetInt32()}").ToList();
        Assert.Equal(["FB102|FBAI|1", "FB101|FBCOMP|1"], listed);
    }

    [Fact]
    public async Task Confirming_the_year_again_picks_up_defaults_saved_later()
    {
        var (owner, semester, _, ai, courses) = await Setup("FE", 2045);
        var student = await Student(ai, 1);
        Assert.Equal([courses["FE102"]], await Picked(student));

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new[] { new { id = "FE101", major = "FEAI", year = 1 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await student.PutAsJsonAsync("/api/me/profile", new { year = 1 })).StatusCode);
        Assert.Equal([courses["FE101"]], await Picked(student));
    }

    [Fact]
    public async Task A_course_placed_in_another_year_takes_that_year()
    {
        var (owner, semester, _, _, _) = await Setup("FF", 2046);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new object[]
        {
            new { id = "FF201", major = "FFCOMP", year = 3 },
            new { id = "FF201", major = "FFAI", year = 3 },
            new { id = "FF201", major = "FFCS", year = 2 },
        })).StatusCode);
        var years = (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/courses"))).EnumerateArray()
            .ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("year").ValueKind == JsonValueKind.Number ? c.GetProperty("year").GetInt32() : (int?)null);
        Assert.Equal(3, years["FF201"]);
        // Not in any default: keeps the upload's year.
        Assert.Equal(1, years["FF101"]);
    }

    [Fact]
    public async Task A_list_with_bad_rows_saves_nothing()
    {
        var (owner, semester, _, _, _) = await Setup("FC", 2043);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new[] { new { id = "FC101", major = "FCCS", year = 1 } })).StatusCode);

        var bad = await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new object[]
        {
            new { id = "FC102", major = "FCAI", year = 1 },
            new { id = "NOPE 1 999", major = "FCAI", year = 1 },
            new { id = "FC101", major = "MARS", year = 1 },
            new { id = "FC101", major = "FCCS", year = 9 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(3, (await Json(bad)).GetProperty("problems").GetArrayLength());
        Assert.Equal(1, (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/defaults"))).GetArrayLength());
    }

    [Fact]
    public async Task A_family_cannot_use_a_majors_code()
    {
        var (owner, _, cs, _, _) = await Setup("FD", 2044);
        var r = await owner.PutAsJsonAsync($"/api/admin/majors/{cs}", new { code = "FDCS", name = "Computer Science", family = "FDAI" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
