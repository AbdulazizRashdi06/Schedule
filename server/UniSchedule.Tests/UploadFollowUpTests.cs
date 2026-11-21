using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>What an upload leaves behind: courses with no sections, default-course settings, families, odd input.</summary>
public class UploadFollowUpTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 7000;
    static string NewId() => $"26-{Interlocked.Increment(ref next):D4}";
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    const string Header = "code,name,major,type,year,section,instructor,days,time,room\n";

    async Task<HttpClient> Owner(string username)
    {
        await factory.AddAdmin(username, "owner-password-1", AdminRole.Owner, mustChange: false);
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "owner-password-1" })).StatusCode);
        return c;
    }

    async Task<HttpClient> Student(int? majorId = null, int? year = null)
    {
        var id = NewId();
        var c = factory.Client();
        var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).StatusCode);
        if (majorId is not null) Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/me/profile", new { majorId, year })).StatusCode);
        return c;
    }

    static MultipartFormDataContent File(string csv) =>
        new() { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "fall.csv" } };

    static async Task<JsonElement> Upload(HttpClient owner, int semester, string csv, string step = "apply")
    {
        using var file = File(csv);
        var r = await owner.PostAsync($"/api/admin/semesters/{semester}/upload/{step}", file);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Json(r);
    }

    async Task<(HttpClient Owner, int Semester, int Major)> Setup(string tag, int year, string csv)
    {
        var owner = await Owner($"owner-upload-{tag.ToLowerInvariant()}");
        var id = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year }))).GetProperty("id").GetInt32();
        await Upload(owner, id, csv);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{id}/publish", null)).StatusCode);
        var major = (await Json(await owner.GetAsync("/api/admin/majors"))).EnumerateArray()
            .First(m => m.GetProperty("code").GetString() == $"{tag}M").GetProperty("id").GetInt32();
        return (owner, id, major);
    }

    static async Task<List<JsonElement>> Picks(HttpClient student) => (await Json(await student.GetAsync("/api/me/picks"))).EnumerateArray().ToList();

    async Task<int> CourseId(string code)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDb>().Courses.Where(c => c.Code == code).Select(c => c.Id).SingleAsync();
    }

    [Fact]
    public async Task A_course_left_with_no_sections_is_not_ticked_and_does_not_block_finishing()
    {
        var full = Header +
            "ZA101,Programming,ZAM,Core,1,1,A,Sun,08:00-09:30,R1\n" +
            "ZA101,Programming,ZAM,Core,1,2,A,Mon,08:00-09:30,R1\n" +
            "ZA102,Maths,ZAM,Core,1,1,B,Tue,08:00-09:30,R2\n";
        var (owner, semester, major) = await Setup("ZA", 2051, full);
        // Someone picked ZA102, so dropping it from the timetable keeps it with no sections.
        var first = await Student(major, 1);
        Assert.Equal(2, (await Picks(first)).Count);
        await Upload(owner, semester, string.Join("\n", full.Split('\n').Where(l => !l.StartsWith("ZA102"))));

        var za101 = await CourseId("ZA101");
        var za102 = await CourseId("ZA102");
        var student = await Student(major, 1);
        Assert.Equal([za101], (await Picks(student)).Select(p => p.GetProperty("courseId").GetInt32()));

        // A pick of it made before (ticked with no section) is dropped on finishing instead of asking for a section.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var me = (await Json(await student.GetAsync("/api/me"))).GetProperty("universityId").GetString();
            var sid = await db.Students.Where(s => s.UniversityId == me).Select(s => s.Id).SingleAsync();
            db.Picks.Add(new Pick { StudentId = sid, CourseId = za102 });
            await db.SaveChangesAsync();
        }
        var section = (await Json(await student.GetAsync("/api/me/options"))).GetProperty("courses").EnumerateArray()
            .First(c => c.GetProperty("id").GetInt32() == za101).GetProperty("sections")[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await student.PutAsJsonAsync("/api/me/picks", new
        {
            picks = new object[] { new { courseId = za101, sectionId = section }, new { courseId = za102, sectionId = (int?)null } },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await student.PostAsync("/api/me/finish", null)).StatusCode);
        Assert.Equal([za101], (await Picks(student)).Select(p => p.GetProperty("courseId").GetInt32()));
    }

    [Fact]
    public async Task Default_settings_survive_an_upload_without_the_course_and_apply_when_it_returns()
    {
        var full = Header +
            "KA101,Programming,KAM,Core,1,1,A,Sun,08:00-09:30,R1\n" +
            "KA201,Data Structures,KAM,Core,2,1,B,Tue,08:00-09:30,R2\n";
        var (owner, semester, major) = await Setup("KA", 2052, full);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new[]
        {
            new { id = "KA101", major = "KAM", year = 1 },
            new { id = "KA201", major = "KAM", year = 1 },
        })).StatusCode);
        var withoutKa201 = string.Join("\n", full.Split('\n').Where(l => !l.StartsWith("KA201")));
        var preview = await Upload(owner, semester, withoutKa201, "preview");
        Assert.Contains("default-course settings are kept", preview.GetProperty("removed")[0].GetProperty("detail").GetString());
        await Upload(owner, semester, withoutKa201);

        static async Task<List<string>> Defaults(HttpClient owner, int semester) =>
            (await Json(await owner.GetAsync($"/api/admin/semesters/{semester}/defaults"))).EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToList();
        Assert.Equal(["KA101", "KA201"], await Defaults(owner, semester));
        // While it has no sections it isn't ticked.
        var ka101 = await CourseId("KA101");
        var ka201 = await CourseId("KA201");
        Assert.Equal([ka101], (await Picks(await Student(major, 1))).Select(p => p.GetProperty("courseId").GetInt32()));

        await Upload(owner, semester, full);
        Assert.Equal(["KA101", "KA201"], await Defaults(owner, semester));
        Assert.Equal(new[] { ka101, ka201 }.Order(), (await Picks(await Student(major, 1))).Select(p => p.GetProperty("courseId").GetInt32()).Order());
    }

    [Fact]
    public async Task A_family_code_in_the_major_column_is_refused_and_defaults_never_move_to_a_major_of_that_name()
    {
        var (owner, semester, major) = await Setup("NQ", 2053, Header + "NQ101,Programming,NQM,Core,1,1,A,Sun,08:00-09:30,R1\n");
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/majors/{major}", new { code = "NQM", name = "NQ major", family = "NQCOMP" })).StatusCode);

        var bad = Header + "NQ101,Programming,NQM,Core,1,1,A,Sun,08:00-09:30,R1\nNQ301,Shared,NQCOMP,Core,3,1,A,Mon,08:00-09:30,R1\n";
        var preview = await Upload(owner, semester, bad, "preview");
        Assert.False(preview.GetProperty("canApply").GetBoolean());
        Assert.Empty(preview.GetProperty("newMajors").EnumerateArray());
        var error = preview.GetProperty("errors")[0];
        Assert.Equal(3, error.GetProperty("line").GetInt32());
        Assert.Contains("family", error.GetProperty("message").GetString());
        using (var file = File(bad))
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync($"/api/admin/semesters/{semester}/upload/apply", file)).StatusCode);
        Assert.DoesNotContain((await Json(await owner.GetAsync("/api/admin/majors"))).EnumerateArray(), m => m.GetProperty("code").GetString() == "NQCOMP");

        // A major already named like the family (made before this check) doesn't quietly take the family's defaults.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Majors.Add(new Major { Code = "NQCOMP", Name = "NQCOMP" });
            await db.SaveChangesAsync();
        }
        var put = await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new[] { new { id = "NQ101", major = "NQCOMP", year = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("both a major and a family", (await Json(put)).GetProperty("problems")[0].GetString());
    }

    [Fact]
    public async Task Two_stored_codes_that_differ_only_by_spacing_do_not_break_saving_defaults()
    {
        var (owner, semester, _) = await Setup("NB", 2054, Header + "NB1101,Programming,NBM,Core,1,1,A,Sun,08:00-09:30,R1\n");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Courses.Add(new Course { SemesterId = semester, Code = "NB 1101", Name = "Programming" });
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", Array.Empty<object>())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{semester}/defaults", new[] { new { id = "NB 1101", major = "NBM", year = 1 } })).StatusCode);
    }

    [Fact]
    public async Task Dates_and_times_in_arabic_digits_are_refused_not_a_crash()
    {
        var owner = await Owner("owner-upload-digits");
        var id = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2055 }))).GetProperty("id").GetInt32();
        var preview = await Upload(owner, id, Header + "LS101,English,,LS,,1,A,Sun,٠٨:٠٠-٠٩:٣٠,R1\n", "preview");
        Assert.False(preview.GetProperty("canApply").GetBoolean());
        Assert.Equal(2, preview.GetProperty("errors")[0].GetProperty("line").GetInt32());

        var student = await Student();
        Assert.Equal(HttpStatusCode.BadRequest, (await student.GetAsync("/api/rooms/free?date=٢٠٢٦-١١-٠٤")).StatusCode);
    }
}
