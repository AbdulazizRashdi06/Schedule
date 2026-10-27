using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>Groups: joining by invite, privacy between members, and when everyone is free.</summary>
public class GroupTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 9700;
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<HttpClient> Owner(string username)
    {
        await factory.AddAdmin(username, "owner-password-1", AdminRole.Owner, mustChange: false);
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "owner-password-1" })).StatusCode);
        return c;
    }

    async Task<HttpClient> Student()
    {
        var id = $"25-{Interlocked.Increment(ref next):D4}";
        var c = factory.Client();
        var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).StatusCode);
        return c;
    }

    record World(int Major, Dictionary<string, (int Course, int Section)> Courses);

    /// <summary>GA101 every Sunday 08:00–09:30, GA102 once on Mon 19 Oct 2026 11:30–13:00. Published.</summary>
    async Task<World> Setup()
    {
        var owner = await Owner("owner-groups");
        var csv = "code,name,major,type,year,section,instructor,date,days,time,room\n" +
            "GA101,Weekly,GAM,Core,1,1,A,,Sun,08:00-09:30,R1\n" +
            "GA102,Dated,GAM,Core,1,1,B,2026-10-19,,11:30-13:00,R2\n";
        var sem = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2095 }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "g.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/publish", null)).StatusCode);
        var major = (await Json(await owner.GetAsync("/api/admin/majors"))).EnumerateArray().First(m => m.GetProperty("code").GetString() == "GAM").GetProperty("id").GetInt32();
        var courses = new Dictionary<string, (int, int)>();
        foreach (var c in (await Json(await owner.GetAsync($"/api/admin/semesters/{sem}/courses"))).EnumerateArray())
        {
            var id = c.GetProperty("id").GetInt32();
            var section = (await Json(await owner.GetAsync($"/api/admin/courses/{id}"))).GetProperty("course").GetProperty("sections")[0].GetProperty("id").GetInt32();
            courses[c.GetProperty("code").GetString()!] = (id, section);
        }
        return new(major, courses);
    }

    static async Task Takes(HttpClient s, World w, params string[] codes)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/profile", new { majorId = w.Major, year = 1 })).StatusCode);
        var picks = codes.Select(c => new { courseId = w.Courses[c].Course, sectionId = (int?)w.Courses[c].Section }).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await s.PutAsJsonAsync("/api/me/picks", new { picks, step = "core" })).StatusCode);
    }

    static JsonElement Day(JsonElement week, string date) => week.GetProperty("days").EnumerateArray().First(d => d.GetProperty("date").GetString() == date);
    static List<string> Windows(JsonElement day, string kind) =>
        day.GetProperty(kind).EnumerateArray().Select(x => $"{x.GetProperty("start").GetString()}-{x.GetProperty("end").GetString()}").ToList();

    [Fact]
    public async Task Members_see_when_everyone_is_free_but_not_each_others_classes()
    {
        var w = await Setup();
        var ali = await Student();
        var bader = await Student();
        var sara = await Student();
        var outsider = await Student();
        await Takes(ali, w, "GA101");
        await Takes(bader, w, "GA102");

        // Bader's own slot on Sunday afternoon.
        Assert.Equal(HttpStatusCode.OK, (await bader.PostAsJsonAsync("/api/me/slots", new { title = "Gym", day = 0, start = "13:15", end = "14:45" })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await ali.PostAsJsonAsync("/api/me/groups", new { name = "", kind = "friends", displayName = "Ali" })).StatusCode);
        var group = await Json(await ali.PostAsJsonAsync("/api/me/groups", new { name = "Study group", kind = "friends", displayName = "Ali" }));
        var id = group.GetProperty("id").GetInt32();
        var code = group.GetProperty("inviteCode").GetString();

        var preview = await Json(await bader.GetAsync($"/api/groups/invite/{code}"));
        Assert.Equal("Study group", preview.GetProperty("name").GetString());
        Assert.False(preview.GetProperty("joined").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await bader.PostAsJsonAsync($"/api/groups/invite/{code}/join", new { displayName = "Bader" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sara.PostAsJsonAsync($"/api/groups/invite/{code}/join", new { displayName = "Sara" })).StatusCode);

        // Outsiders can't see the group; members never see university IDs.
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/me/groups/{id}/week?start=2026-10-18")).StatusCode);
        var detail = await ali.GetStringAsync($"/api/me/groups/{id}");
        Assert.DoesNotContain("25-", detail);

        var week = await Json(await sara.GetAsync($"/api/me/groups/{id}/week?start=2026-10-21"));
        Assert.Equal("2026-10-18", week.GetProperty("start").GetString());
        var sunday = Day(week, "2026-10-18");
        Assert.Equal(["09:30-13:15", "14:45-18:15"], Windows(sunday, "free"));
        Assert.Equal(["08:00-09:30", "13:15-14:45"], Windows(sunday, "nearly"));
        Assert.Equal(["Ali"], sunday.GetProperty("nearly")[0].GetProperty("busy").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["08:00-11:30", "13:00-18:15"], Windows(Day(week, "2026-10-19"), "free"));
        Assert.Equal(0, sunday.GetProperty("classes").GetArrayLength());

        // Showing classes is each member's own choice; own slots stay private as "Busy".
        Assert.Equal(HttpStatusCode.OK, (await ali.PutAsJsonAsync($"/api/me/groups/{id}/me", new { displayName = "Ali", showClasses = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bader.PutAsJsonAsync($"/api/me/groups/{id}/me", new { displayName = "Bader", showClasses = true })).StatusCode);
        var shown = Day(await Json(await sara.GetAsync($"/api/me/groups/{id}/week?start=2026-10-18")), "2026-10-18").GetProperty("classes").EnumerateArray().ToList();
        Assert.Equal(["GA101", "Busy"], shown.Select(c => c.GetProperty("label").GetString()));
        Assert.DoesNotContain(shown, c => c.GetProperty("label").GetString() == "Gym");

        // A new link stops the old one.
        var fresh = (await Json(await sara.PostAsync($"/api/me/groups/{id}/invite", null))).GetProperty("inviteCode").GetString();
        Assert.NotEqual(code, fresh);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"/api/groups/invite/{code}/join", new { displayName = "X" })).StatusCode);

        // Leaving: the group goes when its last member leaves.
        foreach (var s in new[] { ali, bader })
            Assert.Equal(HttpStatusCode.NoContent, (await s.DeleteAsync($"/api/me/groups/{id}/me")).StatusCode);
        Assert.Single((await Json(await sara.GetAsync("/api/me/groups"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await sara.DeleteAsync($"/api/me/groups/{id}/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sara.GetAsync($"/api/groups/invite/{fresh}")).StatusCode);
    }

    [Fact]
    public async Task A_group_holds_twelve_people()
    {
        var first = await Student();
        var code = (await Json(await first.PostAsJsonAsync("/api/me/groups", new { name = "Big", kind = "friends", displayName = "P1" }))).GetProperty("inviteCode").GetString();
        for (var i = 2; i <= 12; i++)
            Assert.Equal(HttpStatusCode.OK, (await (await Student()).PostAsJsonAsync($"/api/groups/invite/{code}/join", new { displayName = $"P{i}" })).StatusCode);
        var late = await Student();
        Assert.True((await Json(await late.GetAsync($"/api/groups/invite/{code}"))).GetProperty("full").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await late.PostAsJsonAsync($"/api/groups/invite/{code}/join", new { displayName = "P13" })).StatusCode);
    }
}
