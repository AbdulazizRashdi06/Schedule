using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>"Rate Classy": staff push a message, each student answers once, staff see anonymous results.</summary>
public class FeedbackTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 8000;
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<HttpClient> Staff(string username, AdminRole role)
    {
        await factory.AddAdmin(username, "staff-password-1", role, mustChange: false);
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "staff-password-1" })).StatusCode);
        return c;
    }

    async Task<HttpClient> Student(string? universityId = null)
    {
        var id = universityId ?? $"25-{Interlocked.Increment(ref next):D4}";
        var c = factory.Client();
        var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).StatusCode);
        return c;
    }

    static async Task<JsonElement> Prompt(HttpClient c) => (await Json(await c.GetAsync("/api/me/feedback"))).GetProperty("prompt");

    [Fact]
    public async Task Students_rate_once_and_staff_see_anonymous_results()
    {
        var rsa = await Staff("rsa-feedback", AdminRole.Rsa);
        var a = await Student();
        var b = await Student();
        var c = await Student();
        Assert.Equal(JsonValueKind.Null, (await Prompt(a)).ValueKind);

        // Students can't push or read results.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PostAsJsonAsync("/api/admin/feedback", new { message = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rsa.PostAsJsonAsync("/api/admin/feedback", new { message = " " })).StatusCode);

        var first = (await Json(await rsa.PostAsJsonAsync("/api/admin/feedback", new { message = "Classy is in beta. How is it?" }))).GetProperty("id").GetInt32();
        var p = await Prompt(a);
        Assert.Equal(first, p.GetProperty("id").GetInt32());
        Assert.Equal("Classy is in beta. How is it?", p.GetProperty("message").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/me/feedback", new { promptId = first, rating = 6 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsJsonAsync("/api/me/feedback", new { promptId = first, rating = 5, comment = "  Love it  " })).StatusCode);
        // A second answer is ignored.
        Assert.Equal(HttpStatusCode.NoContent, (await a.PostAsJsonAsync("/api/me/feedback", new { promptId = first, rating = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.PostAsJsonAsync("/api/me/feedback", new { promptId = first, rating = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/me/feedback", new { promptId = first, dismissed = true })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Prompt(a)).ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Prompt(c)).ValueKind);

        var results = (await Json(await rsa.GetAsync("/api/admin/feedback"))).EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == first);
        Assert.Equal(2, results.GetProperty("ratings").GetInt32());
        Assert.Equal(1, results.GetProperty("notNow").GetInt32());
        Assert.Equal(4.0, results.GetProperty("average").GetDouble());
        Assert.Equal([0, 0, 1, 0, 1], results.GetProperty("stars").EnumerateArray().Select(x => x.GetInt32()));
        var comment = results.GetProperty("comments").EnumerateArray().Single();
        Assert.Equal("Love it", comment.GetProperty("comment").GetString());
        Assert.DoesNotContain("25-", results.GetRawText());

        // Pushing a new one closes the old one and asks everyone again.
        var second = (await Json(await rsa.PostAsJsonAsync("/api/admin/feedback", new { message = "Rate the new week view" }))).GetProperty("id").GetInt32();
        Assert.Equal(second, (await Prompt(a)).GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync("/api/me/feedback", new { promptId = first, rating = 2 })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await rsa.PostAsync($"/api/admin/feedback/{second}/close", null)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Prompt(b)).ValueKind);
    }

    [Fact]
    public async Task Rsa_sees_an_owner_instead_of_a_student_owners_id()
    {
        // An owner who signs in as a student.
        const string ownerId = "25-7999";
        var studentOwner = await Student(ownerId);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDb>().Students.Where(s => s.UniversityId == ownerId).ExecuteUpdateAsync(u => u.SetProperty(s => s.IsOwner, true));
        var rsa = await Staff("rsa-feedback-by", AdminRole.Rsa);
        var owner = await Staff("owner-feedback-by", AdminRole.Owner);
        var prompt = (await Json(await studentOwner.PostAsJsonAsync("/api/admin/feedback", new { message = "Rate us" }))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await studentOwner.PostAsJsonAsync("/api/admin/accounts", new { username = "rsa-made-by-student", role = "rsa" })).StatusCode);
        var sem = (await Json(await studentOwner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2071 }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent("code,name,major,type,year,section,instructor,days,time,room\nFB101,By,FBM,Core,1,1,A,Sun,08:00-09:30,R1\n"u8.ToArray()), "file", "by.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await studentOwner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);

        async Task<string[]> By(HttpClient c) =>
        [
            (await Json(await c.GetAsync("/api/admin/feedback"))).EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == prompt).GetProperty("by").GetString()!,
            (await Json(await c.GetAsync("/api/admin/accounts"))).GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("username").GetString() == "rsa-made-by-student").GetProperty("createdBy").GetString()!,
            (await Json(await c.GetAsync($"/api/admin/stats?semesterId={sem}"))).GetProperty("recentUploads")[0].GetProperty("by").GetString()!,
        ];
        Assert.Equal(["an owner", "an owner", "an owner"], await By(rsa));
        Assert.Equal([ownerId, ownerId, ownerId], await By(owner));
        // Dashboard usernames aren't student data.
        var byRsa = (await Json(await rsa.PostAsJsonAsync("/api/admin/feedback", new { message = "Rate us again" }))).GetProperty("id").GetInt32();
        Assert.Equal("rsa-feedback-by", (await Json(await rsa.GetAsync("/api/admin/feedback"))).EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == byRsa).GetProperty("by").GetString());
        // Nothing left live for the other tests.
        Assert.Equal(HttpStatusCode.NoContent, (await rsa.PostAsync($"/api/admin/feedback/{byRsa}/close", null)).StatusCode);
    }

    [Fact]
    public async Task An_old_open_report_stays_listed_and_the_counts_cover_every_report()
    {
        var rsa = await Staff("rsa-reports-many", AdminRole.Rsa);
        var student = await Student();
        Assert.Equal(HttpStatusCode.NoContent, (await student.PostAsJsonAsync("/api/me/reports", new { text = "OLD-OPEN-REPORT" })).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var by = await db.ProblemReports.Where(r => r.Text == "OLD-OPEN-REPORT").Select(r => r.StudentId).FirstAsync();
            var now = DateTimeOffset.UtcNow;
            db.ProblemReports.AddRange(Enumerable.Range(0, 320).Select(i => new ProblemReport { StudentId = by, Text = $"newer {i}", At = now, ResolvedAt = now }));
            await db.SaveChangesAsync();
        }
        var list = await Json(await rsa.GetAsync("/api/admin/feedback/reports"));
        var reports = list.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal(300, reports.Count);
        Assert.Contains(reports, r => r.GetProperty("text").GetString() == "OLD-OPEN-REPORT");
        // Newest first.
        Assert.True(reports.Zip(reports.Skip(1)).All(x => x.First.GetProperty("id").GetInt32() > x.Second.GetProperty("id").GetInt32()));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            Assert.Equal(await db.ProblemReports.CountAsync(), list.GetProperty("total").GetInt32());
            Assert.Equal(await db.ProblemReports.CountAsync(r => r.ResolvedAt == null), list.GetProperty("open").GetInt32());
        }
        Assert.True(list.GetProperty("total").GetInt32() > 320);
    }
}
