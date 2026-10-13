using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>Runs the whole API in memory against a throwaway SQLite file.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "unischedule-tests-" + Guid.NewGuid().ToString("N"));
    public const string OwnerId = "22-0001";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Data:Path", Path.Combine(dir, "test.db"));
        builder.UseSetting("Auth:TestMode", "true");
        builder.UseSetting("Owners:Ids", OwnerId);
        builder.UseSetting("RateLimits:RequestCode", "1000");
        builder.UseSetting("RateLimits:Verify", "1000");
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    public async Task<AdminUser> AddAdmin(string username, string password, AdminRole role, bool mustChange)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var user = new AdminUser { Username = username, Role = role, MustChangePassword = mustChange, CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = AuthEndpoints.HashPassword(user, password);
        db.AdminUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(dir, true); } catch (IOException) { /* SQLite may still hold the file briefly */ }
    }
}

public class AuthFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 1000;
    static string NewId() => $"24-{Interlocked.Increment(ref next):D4}";

    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<HttpClient> SignInStudent(string id)
    {
        var c = factory.Client();
        var sent = await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }));
        var code = sent.GetProperty("testCode").GetString();
        var r = await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return c;
    }

    [Fact]
    public async Task Health_is_public()
    {
        var r = await factory.Client().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Student_signs_in_with_a_code_and_gets_a_session()
    {
        var id = NewId();
        var c = factory.Client();
        var sent = await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id.Replace("-", " ") }));
        Assert.Equal($"{id}@student.gutech.edu.om", sent.GetProperty("email").GetString());
        var code = sent.GetProperty("testCode").GetString();
        Assert.Matches(@"^\d{6}$", code);

        var verify = await Json(await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code }));
        Assert.True(verify.GetProperty("isNew").GetBoolean());

        var session = await Json(await c.GetAsync("/api/auth/session"));
        Assert.Equal("student", session.GetProperty("kind").GetString());
        var me = await Json(await c.GetAsync("/api/me"));
        Assert.Equal(id, me.GetProperty("universityId").GetString());
        Assert.Equal("major", me.GetProperty("step").GetString());

        // A code works once.
        var again = await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Bad_ids_and_codes_are_refused()
    {
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = "12345" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/verify", new { universityId = "24-0001", code = "12ab56" })).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_codes_burn_the_code()
    {
        var id = NewId();
        var c = factory.Client();
        var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString()!;
        var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < LoginCodes.MaxAttempts; i++)
        {
            var r = await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code = wrong });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        var right = await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code });
        Assert.Equal(HttpStatusCode.BadRequest, right.StatusCode);
        Assert.Contains("new code", (await Json(right)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_second_code_within_a_minute_is_refused()
    {
        var id = NewId();
        var c = factory.Client();
        await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id });
        var r = await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id });
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
    }

    [Fact]
    public async Task Test_mode_never_shows_an_owners_code()
    {
        var sent = await Json(await factory.Client().PostAsJsonAsync("/api/auth/request-code", new { universityId = ApiFactory.OwnerId }));
        Assert.Equal(JsonValueKind.Null, sent.GetProperty("testCode").ValueKind);
    }

    [Fact]
    public async Task Students_cannot_reach_the_dashboard_and_guests_cannot_reach_anything()
    {
        var student = await SignInStudent(NewId());
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/admin/students")).StatusCode);
        var guest = factory.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/api/nope")).StatusCode);
    }

    [Fact]
    public async Task Rsa_must_change_the_temporary_password_before_using_the_dashboard()
    {
        await factory.AddAdmin("rsa-new", "temp-pass-1", AdminRole.Rsa, mustChange: true);
        var c = factory.Client();
        var login = await Json(await c.PostAsJsonAsync("/api/auth/admin-login", new { username = "RSA-new", password = "temp-pass-1" }));
        Assert.True(login.GetProperty("mustChangePassword").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/admin/stats")).StatusCode);

        var tooShort = await c.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "temp-pass-1", newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        var ok = await c.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "temp-pass-1", newPassword = "a-much-better-password" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/admin/stats")).StatusCode);
        // RSA never sees student data and can't create owners.
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/admin/students")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/admin/accounts", new { username = "boss", role = "owner" })).StatusCode);
        var rsa = await Json(await c.PostAsJsonAsync("/api/admin/accounts", new { username = "rsa-helper", role = "rsa" }));
        Assert.False(string.IsNullOrEmpty(rsa.GetProperty("temporaryPassword").GetString()));
    }

    [Fact]
    public async Task Rsa_cannot_remove_an_owner_account()
    {
        var owner = await factory.AddAdmin("owner-x", "owner-password-1", AdminRole.Owner, mustChange: false);
        await factory.AddAdmin("rsa-x", "rsa-password-1", AdminRole.Rsa, mustChange: false);
        var c = factory.Client();
        await c.PostAsJsonAsync("/api/auth/admin-login", new { username = "rsa-x", password = "rsa-password-1" });
        Assert.Equal(HttpStatusCode.Forbidden, (await c.DeleteAsync($"/api/admin/accounts/{owner.Id}")).StatusCode);
    }

    [Fact]
    public async Task Owner_can_see_students()
    {
        await factory.AddAdmin("owner-y", "owner-password-1", AdminRole.Owner, mustChange: false);
        var c = factory.Client();
        await c.PostAsJsonAsync("/api/auth/admin-login", new { username = "owner-y", password = "owner-password-1" });
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/admin/students")).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await factory.AddAdmin("rsa-lock", "right-password-1", AdminRole.Rsa, mustChange: false);
        var c = factory.Client();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username = "rsa-lock", password = "wrong" })).StatusCode);
        var locked = await c.PostAsJsonAsync("/api/auth/admin-login", new { username = "rsa-lock", password = "right-password-1" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task Removed_student_is_signed_out_at_once()
    {
        var id = NewId();
        var c = await SignInStudent(id);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Students.Remove(db.Students.Single(s => s.UniversityId == id));
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/me")).StatusCode);
    }
}
