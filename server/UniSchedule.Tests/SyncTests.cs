using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>The Classy Sync extension: tokens, which courses to fetch, and a pending sync staff apply.</summary>
public class SyncTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<HttpClient> Staff(string username, AdminRole role)
    {
        await factory.AddAdmin(username, "staff-password-1", role, mustChange: false);
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "staff-password-1" })).StatusCode);
        return c;
    }

    [Theory]
    [InlineData("CS1107", "CS 1 107")]
    [InlineData("CS 1 107", "CS 1 107")]
    [InlineData("IBSM-FE340", "IBSM-FE 340")]
    [InlineData("LOG-E 464", "LOG-E 464")]
    [InlineData("LS1003", "LS 1 003")]
    [InlineData("CS1102-L", "CS 1 102-L")]
    [InlineData("CS 1 102-L", "CS 1 102-L")]
    public void Codes_are_written_the_EduWave_way(string code, string eduwave) => Assert.Equal(eduwave, SyncEndpoints.EduWaveCode(code));

    [Fact]
    public void Extra_codes_come_from_config()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:ExtraCodes"] = "CS1102-L, LOG2201-L" }).Build();
        Assert.Equal(["CS1102-L", "LOG2201-L"], SyncEndpoints.ExtraCodes(config));
        Assert.Equal("CS 1 102-L", SyncEndpoints.EduWaveCode(SyncEndpoints.ExtraCodes(config)[0]));
    }

    [Fact]
    public async Task A_sync_waits_for_staff_and_keeps_failed_courses()
    {
        var owner = await Staff("owner-sync", AdminRole.Owner);
        var rsa = await Staff("rsa-sync", AdminRole.Rsa);
        var csv = "code,name,major,type,year,credits,section,instructor,date,days,time,room\n" +
            "SY1101,Sync Course,SYM,Core,1,5,1,A,2026-10-12,,08:00-09:30,R1\n" +
            "SY1102,Kept Course,SYM,Core,1,,1,B,,Tue,09:45-11:15,R2\n" +
            "SY3201,Gone Course,SYM,Core,3,,1,C,,Wed,08:00-09:30,R3\n";
        var sem = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2071 }))).GetProperty("id").GetInt32();
        using (var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "s.csv" } })
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);

        // Only owners make tokens; the extension needs one.
        Assert.Equal(HttpStatusCode.Forbidden, (await rsa.PostAsJsonAsync("/api/admin/sync-tokens", new { name = "x" })).StatusCode);
        var ext = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ext.GetAsync($"/api/sync/courses?semesterId={sem}")).StatusCode);
        var token = (await Json(await owner.PostAsJsonAsync("/api/admin/sync-tokens", new { name = "My laptop" }))).GetProperty("token").GetString()!;
        Assert.StartsWith("csync_", token);
        ext.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var courses = (await Json(await ext.GetAsync($"/api/sync/courses?semesterId={sem}"))).GetProperty("courses").EnumerateArray().ToList();
        Assert.Equal(["SY 1 101", "SY 1 102", "SY 3 201"], courses.Select(c => c.GetProperty("eduwave").GetString()));
        // The token can't do anything else.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ext.GetAsync("/api/admin/semesters")).StatusCode);

        // SY1101 moved room and got a new date, SY1102 failed (kept), SY3201 is gone, SY2301 is new.
        var sent = await ext.PostAsJsonAsync("/api/sync/timetable", new
        {
            semesterId = sem,
            rows = new[]
            {
                new { code = "SY 1 101", name = "", section = "1", instructor = "A", date = "2026-10-12", start = "08:00", end = "09:30", room = "R9" },
                new { code = "SY 1 101", name = "", section = "1", instructor = "A", date = "2026-10-19", start = "08:00", end = "09:30", room = "R9" },
                new { code = "SY 2 301", name = "New Course", section = "1", instructor = "D", date = "2026-10-13", start = "11:30", end = "13:00", room = "R4" },
            },
            failed = new[] { "SY 1 102" },
        });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var summary = await Json(sent);
        Assert.Equal(1, summary.GetProperty("added").GetInt32());
        Assert.Equal(1, summary.GetProperty("updated").GetInt32());
        Assert.Equal(1, summary.GetProperty("removed").GetInt32());

        // Nothing changes until staff apply it.
        var list = (await Json(await rsa.GetAsync($"/api/admin/semesters/{sem}/courses"))).EnumerateArray().Select(c => c.GetProperty("code").GetString()).ToList();
        Assert.Contains("SY3201", list);
        var pending = (await Json(await rsa.GetAsync($"/api/admin/semesters/{sem}/sync"))).GetProperty("pending");
        Assert.Equal("My laptop", pending.GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.OK, (await rsa.PostAsJsonAsync($"/api/admin/semesters/{sem}/sync/apply", new { id = pending.GetProperty("id").GetInt32() })).StatusCode);

        var after = (await Json(await rsa.GetAsync($"/api/admin/semesters/{sem}/courses"))).EnumerateArray().ToList();
        Assert.Equal(["SY1101", "SY1102", "SY2301"], after.Select(c => c.GetProperty("code").GetString()).Order());
        var kept = after.First(c => c.GetProperty("code").GetString() == "SY1101");
        Assert.Equal("Sync Course", kept.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, (await Json(await rsa.GetAsync($"/api/admin/semesters/{sem}/sync"))).GetProperty("pending").ValueKind);

        // A sync where most courses failed isn't kept.
        var bad = await ext.PostAsJsonAsync("/api/sync/timetable", new
        {
            semesterId = sem,
            rows = new[] { new { code = "SY 1 101", name = "", section = "1", instructor = "A", date = "2026-10-12", start = "08:00", end = "09:30", room = "R9" } },
            failed = new[] { "SY 1 102", "SY 2 301", "X1", "X2" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // A revoked token stops working.
        var id = (await Json(await owner.GetAsync("/api/admin/sync-tokens"))).EnumerateArray().First().GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/admin/sync-tokens/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ext.GetAsync($"/api/sync/courses?semesterId={sem}")).StatusCode);
    }
}
