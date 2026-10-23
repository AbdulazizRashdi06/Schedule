using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Tests;

/// <summary>A course ending in -L is the lab of the course without it, and follows it.</summary>
public class LabTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 7000;
    static string NewId() => $"25-{Interlocked.Increment(ref next):D4}";
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Theory]
    [InlineData("CS 1 102-L", "CS1102")]
    [InlineData("cs1102-l", "CS1102")]
    [InlineData("CS 1 102", null)]
    [InlineData("-L", null)]
    public void Lab_codes_name_their_course(string code, string? parent) => Assert.Equal(parent, Labs.ParentKey(code));

    async Task<(HttpClient Owner, int Semester, int Major, Dictionary<string, JsonElement> Courses)> Setup(string tag, int year)
    {
        await factory.AddAdmin($"owner-lab-{tag.ToLowerInvariant()}", "owner-password-1", AdminRole.Owner, mustChange: false);
        var c = factory.Client();
        await c.PostAsJsonAsync("/api/auth/admin-login", new { username = $"owner-lab-{tag.ToLowerInvariant()}", password = "owner-password-1" });
        // The lab is listed with the wrong year and no major: it should take its course's.
        var csv = "code,name,major,type,year,section,instructor,days,time,room\n" +
            $"{tag}1102,Programming,{tag}CS,Core,1,1,A,Sun,08:00-09:30,R1\n" +
            $"{tag}1102-L,Programming Lab,,Elective,3,1,A,Mon,08:00-09:30,Lab 1\n" +
            $"{tag}2201,Data Structures,{tag}CS,Core,2,1,C,Tue,08:00-09:30,R3\n";
        var id = (await Json(await c.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "fall.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/admin/semesters/{id}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/admin/semesters/{id}/publish", null)).StatusCode);
        var major = (await Json(await c.GetAsync("/api/admin/majors"))).EnumerateArray().First(m => m.GetProperty("code").GetString() == $"{tag}CS").GetProperty("id").GetInt32();
        var courses = (await Json(await c.GetAsync($"/api/admin/semesters/{id}/courses"))).EnumerateArray().ToDictionary(x => x.GetProperty("code").GetString()!);
        return (c, id, major, courses);
    }

    async Task<HashSet<int>> TickedFor(int major, int year)
    {
        var id = NewId();
        var s = factory.Client();
        var code = (await Json(await s.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        await s.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code });
        await s.PutAsJsonAsync("/api/me/profile", new { majorId = major, year });
        await s.GetAsync("/api/me");
        return (await Json(await s.GetAsync("/api/me/picks"))).EnumerateArray().Select(p => p.GetProperty("courseId").GetInt32()).ToHashSet();
    }

    [Fact]
    public async Task A_lab_takes_its_courses_year_type_and_majors_and_is_ticked_with_it()
    {
        var (_, _, major, courses) = await Setup("LA", 2041);
        var lab = courses["LA1102-L"];
        Assert.Equal("Core", lab.GetProperty("type").GetString());
        Assert.Equal(1, lab.GetProperty("year").GetInt32());
        Assert.Equal(["LACS"], lab.GetProperty("majors").EnumerateArray().Select(m => m.GetString()));

        // No defaults set: the upload's year 1 core courses, and the lab with its course.
        var ticked = await TickedFor(major, 1);
        Assert.Contains(courses["LA1102"].GetProperty("id").GetInt32(), ticked);
        Assert.Contains(lab.GetProperty("id").GetInt32(), ticked);
    }

    [Fact]
    public async Task Defaults_copy_a_course_onto_its_lab()
    {
        var (owner, semester, major, courses) = await Setup("LB", 2042);
        // The course moves to year 2; a row for the lab itself is ignored.
        var saved = await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new object[]
        {
            new { id = "LB1102", major = "LBCS", year = 2 },
            new { id = "LB2201", major = "LBCS", year = 2 },
            new { id = "LB1102-L", major = "LBCS", year = 4 },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var defaults = (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/defaults"))).EnumerateArray()
            .Select(d => (d.GetProperty("id").GetString(), d.GetProperty("year").GetInt32())).ToList();
        Assert.Contains(("LB1102-L", 2), defaults);
        Assert.DoesNotContain(("LB1102-L", 4), defaults);

        var after = (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/courses"))).EnumerateArray().ToDictionary(x => x.GetProperty("code").GetString()!);
        Assert.Equal(2, after["LB1102-L"].GetProperty("year").GetInt32());
        Assert.Contains(courses["LB1102-L"].GetProperty("id").GetInt32(), await TickedFor(major, 2));
    }
}
