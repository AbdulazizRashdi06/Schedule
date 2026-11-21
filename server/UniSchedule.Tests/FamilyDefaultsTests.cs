using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task Changing_year_after_setup_swaps_core_courses_and_keeps_the_rest()
    {
        var (_, _, cs, _, courses) = await Setup("FY", 2081);
        var s = await Student(cs, 1);
        Assert.Equal([courses["FY101"]], await Picked(s));

        // Add the life skill, finish setup.
        var options = (await Json(await s.GetAsync("/api/me/options"))).GetProperty("courses").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetInt32(), c => c.GetProperty("sections")[0].GetProperty("id").GetInt32());
        var picks = new[] { courses["FY101"], courses["FYLS"] }.Select(id => new { courseId = id, sectionId = options[id] }).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await s.PutAsJsonAsync("/api/me/picks", new { picks, step = "ls" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.PostAsync("/api/me/finish", null)).StatusCode);

        // Picked the wrong year: Year 2's core replaces Year 1's, the life skill stays, setup stays done.
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/profile", new { year = 2 })).StatusCode);
        Assert.Equal([courses["FY201"], courses["FYLS"]], (await Picked(s)).Order());
        var me = await Json(await s.GetAsync("/api/me"));
        Assert.True(me.GetProperty("setupDone").GetBoolean());
        Assert.Equal(2, me.GetProperty("year").GetInt32());

        // Saving the same year again changes nothing.
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/profile", new { year = 2 })).StatusCode);
        Assert.Equal([courses["FY201"], courses["FYLS"]], (await Picked(s)).Order());
    }

    [Fact]
    public async Task A_major_cannot_take_its_own_code_as_its_family_and_one_that_did_can_still_be_edited()
    {
        var (owner, _, cs, _, _) = await Setup("FG", 2047);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/admin/majors/{cs}", new { code = "FGX", name = "Computer Science", family = "FGX" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/admin/majors", new { code = "FGZ", name = "Zed", family = "FGZ" })).StatusCode);
        // Its old code is free once it has a new one.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/majors/{cs}", new { code = "FGCS2", name = "Computer Science", family = "FGCS" })).StatusCode);

        // One left like that by the old rules can still be renamed and taken out of it.
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDb>().Majors.Where(m => m.Id == cs).ExecuteUpdateAsync(u => u.SetProperty(m => m.Family, "FGCS2"));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/majors/{cs}", new { code = "FGCS2", name = "Renamed", family = "" })).StatusCode);

        // A call that sends no family keeps the one the major has, so it can't be renamed to that family's name either.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/majors/{cs}", new { code = "FGCS2", name = "Renamed", family = "FGSOLO" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/admin/majors/{cs}", new { code = "FGSOLO", name = "Renamed" })).StatusCode);
        var kept = (await Json(await owner.GetAsync("/api/admin/majors"))).EnumerateArray().First(m => m.GetProperty("id").GetInt32() == cs);
        Assert.Equal(("FGCS2", "FGSOLO"), (kept.GetProperty("code").GetString(), kept.GetProperty("family").GetString()));
        // One stuck like that can still be edited without sending a family.
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDb>().Majors.Where(m => m.Id == cs).ExecuteUpdateAsync(u => u.SetProperty(m => m.Family, "FGCS2"));
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/majors/{cs}", new { code = "FGCS2", name = "Renamed again" })).StatusCode);
    }

    [Fact]
    public async Task Renaming_or_leaving_a_family_takes_its_defaults_along_and_the_grid_still_saves()
    {
        var (owner, semester, cs, ai, courses) = await Setup("FH", 2048);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new[] { new { id = "FH101", major = "FHCOMP", year = 1 } })).StatusCode);
        async Task<List<string>> Listed() => (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/defaults"))).EnumerateArray()
            .Select(d => $"{d.GetProperty("id").GetString()}|{d.GetProperty("major").GetString()}|{d.GetProperty("year").GetInt32()}").ToList();
        async Task SaveBack()
        {
            var rows = (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/defaults"))).EnumerateArray()
                .Select(d => new { id = d.GetProperty("id").GetString(), major = d.GetProperty("major").GetString(), year = d.GetProperty("year").GetInt32() }).ToList();
            var saved = await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", rows);
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        }
        Task<HttpResponseMessage> Move(int major, string code, string family) => owner.PutAsJsonAsync($"/api/admin/majors/{major}", new { code, name = code, family });

        // One member moves first: FHCOMP still has AI, so its defaults stay.
        Assert.Equal(HttpStatusCode.OK, (await Move(cs, "FHCS", "FHCOMPUTING")).StatusCode);
        Assert.Equal(["FH101|FHCOMP|1"], await Listed());
        // The last one: the defaults follow to the new name.
        Assert.Equal(HttpStatusCode.OK, (await Move(ai, "FHAI", "FHCOMPUTING")).StatusCode);
        Assert.Equal(["FH101|FHCOMPUTING|1"], await Listed());
        await SaveBack();
        Assert.Equal([courses["FH101"]], await Picked(await Student(ai, 1)));

        // Leaving the family for none: the last member keeps them as its own.
        Assert.Equal(HttpStatusCode.OK, (await Move(cs, "FHCS", "")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(ai, "FHAI", "")).StatusCode);
        Assert.Equal(["FH101|FHAI|1"], await Listed());
        await SaveBack();

        // Deleting a family's last major drops the family's defaults.
        var solo = (await Json(await owner.PostAsJsonAsync("/api/admin/majors", new { code = "FHSOLO", name = "Solo", family = "FHONE" }))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new[] { new { id = "FH101", major = "FHAI", year = 1 }, new { id = "FH201", major = "FHONE", year = 2 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/admin/majors/{solo}")).StatusCode);
        Assert.Equal(["FH101|FHAI|1"], await Listed());
        await SaveBack();
    }

    [Fact]
    public async Task Joining_a_family_keeps_the_majors_defaults_as_its_own_and_changes_nobody_elses()
    {
        var (owner, semester, cs, ai, courses) = await Setup("FJ", 2049);
        // CS alone in FJCOMP with year 1 defaults; AI alone in FJDATA with year 1 and year 2 defaults.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/majors/{ai}", new { code = "FJAI", name = "AI", family = "FJDATA" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new[]
        {
            new { id = "FJ101", major = "FJCOMP", year = 1 }, new { id = "FJ102", major = "FJDATA", year = 1 }, new { id = "FJ102", major = "FJDATA", year = 2 },
        })).StatusCode);
        // AI joins CS's family: its defaults become its own, and none are dropped.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/majors/{ai}", new { code = "FJAI", name = "AI", family = "FJCOMP" })).StatusCode);
        var listed = (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/defaults"))).EnumerateArray()
            .Select(d => $"{d.GetProperty("id").GetString()}|{d.GetProperty("major").GetString()}|{d.GetProperty("year").GetInt32()}").ToList();
        Assert.Equal(["FJ102|FJAI|1", "FJ102|FJAI|2", "FJ101|FJCOMP|1"], listed);

        // CS in year 2 still gets the upload's course, not AI's old family default; each keeps its year 1 courses.
        Assert.Equal([courses["FJ201"]], await Picked(await Student(cs, 2)));
        Assert.Equal([courses["FJ101"]], await Picked(await Student(cs, 1)));
        Assert.Equal([courses["FJ102"]], await Picked(await Student(ai, 2)));
    }

    [Fact]
    public async Task Defaults_left_under_a_family_nobody_is_in_are_not_listed_and_give_way_to_a_major_moving_there()
    {
        var (owner, semester, cs, ai, courses) = await Setup("FK", 2053);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new[] { new { id = "FK101", major = "FKCOMP", year = 1 } })).StatusCode);
        // Left by the old rules: a family no major is in any more.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.CourseDefaults.Add(new CourseDefault { SemesterId = semester, CourseId = courses["FK201"], Family = "FKOLD", Year = 1 });
            await db.SaveChangesAsync();
        }
        async Task<List<string>> Listed() => (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/defaults"))).EnumerateArray()
            .Select(d => $"{d.GetProperty("id").GetString()}|{d.GetProperty("major").GetString()}|{d.GetProperty("year").GetInt32()}").ToList();
        Assert.Equal(["FK101|FKCOMP|1"], await Listed());

        // The family's last major moves to the old name: its defaults win over what was left there.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/majors/{cs}", new { code = "FKCS", name = "CS", family = "FKOTHER" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/majors/{ai}", new { code = "FKAI", name = "AI", family = "FKOLD" })).StatusCode);
        Assert.Equal(["FK101|FKOLD|1"], await Listed());
        Assert.Equal([courses["FK101"]], await Picked(await Student(ai, 1)));
    }

    [Fact]
    public async Task The_electives_panel_counts_what_the_family_allows()
    {
        var c = await Owner("owner-family-fi");
        var csv = "code,name,major,type,year,section,instructor,days,time,room\n" +
            "FI101,Programming,FICS,Core,1,1,A,Sun,08:00-09:30,R1\n" +
            "FI102,Intro to AI,FIAI,Core,1,1,B,Mon,08:00-09:30,R2\n" +
            "FIO1,Other,FIOTH,Core,1,1,B,Mon,10:00-11:30,R2\n" +
            "FIE1,Listed for CS,FICS,Elective,,1,C,Tue,08:00-09:30,R3\n" +
            "FIE2,Listed for another,FIOTH,Elective,,1,D,Wed,08:00-09:30,R4\n" +
            "FIE3,Closed,FIOTH,Elective,,1,D,Thu,08:00-09:30,R4\n";
        var sem = (await Json(await c.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2052 }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "fi.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);
        var majors = (await Json(await c.GetAsync("/api/admin/majors"))).EnumerateArray().ToDictionary(m => m.GetProperty("code").GetString()!, m => m.GetProperty("id").GetInt32());
        foreach (var code in new[] { "FICS", "FIAI" })
            Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/admin/majors/{majors[code]}", new { code, name = code, family = "FICOMP" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/admin/majors/{majors["FICS"]}/electives", new { codes = new[] { "FIE2" } })).StatusCode);

        var panel = (await Json(await c.GetAsync($"/api/admin/majors/{majors["FIAI"]}/electives?semesterId={sem}"))).GetProperty("electives").EnumerateArray()
            .ToDictionary(e => e.GetProperty("code").GetString()!, e => e.GetProperty("source").GetString());
        Assert.Equal("upload", panel["FIE1"]);
        Assert.Equal("family", panel["FIE2"]);
        Assert.Equal("no", panel["FIE3"]);
    }
}
