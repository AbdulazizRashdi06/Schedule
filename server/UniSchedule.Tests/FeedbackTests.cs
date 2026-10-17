using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    async Task<HttpClient> Student()
    {
        var id = $"25-{Interlocked.Increment(ref next):D4}";
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
}
