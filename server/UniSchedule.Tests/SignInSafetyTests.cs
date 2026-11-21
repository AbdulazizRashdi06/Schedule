using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>The API with a clock the test moves, and clients that can each come from their own address.</summary>
sealed class ClockedApi : IDisposable
{
    public sealed class Clock : TimeProvider
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() { lock (this) return now; }
        public void Advance(TimeSpan by) { lock (this) now += by; }
    }

    readonly ApiFactory root = new();
    public readonly WebApplicationFactory<Program> App;
    public readonly Clock Time = new();

    public ClockedApi(int wrongCodesPerHour = 300)
    {
        App = root.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimits:WrongCodesPerHour", wrongCodesPerHour.ToString());
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Time));
        });
    }

    /// <summary>A browser of its own (own cookies), seen as coming from <paramref name="ip"/>.</summary>
    public HttpClient Browser(string? ip = null)
    {
        var c = App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        if (ip is not null) c.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        return c;
    }

    public async Task<T> Db<T>(Func<AppDb, Task<T>> f)
    {
        using var scope = App.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    public void Dispose() { App.Dispose(); root.Dispose(); }
}

public class SignInSafetyTests
{
    static int next = 5000;
    static string NewId() => $"25-{Interlocked.Increment(ref next):D4}";

    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
    static async Task<string> Error(HttpResponseMessage r) => (await Json(r)).GetProperty("error").GetString()!;

    static async Task<string> RequestCode(HttpClient c, string id)
    {
        var r = await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("testCode").GetString()!;
    }

    static Task<HttpResponseMessage> Verify(HttpClient c, string id, string code) =>
        c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code });

    static string WrongFor(string code) => code == "000000" ? "111111" : "000000";

    [Fact]
    public async Task A_code_only_works_in_the_browser_that_asked_and_nobody_else_can_burn_it()
    {
        using var api = new ClockedApi();
        var id = NewId();
        var victim = api.Browser();
        var code = await RequestCode(victim, id);

        // Someone else with the right code, or guessing, gets nowhere and uses none of the victim's tries.
        var outsider = api.Browser();
        var stolen = await Verify(outsider, id, code);
        Assert.Equal(HttpStatusCode.BadRequest, stolen.StatusCode);
        Assert.Contains("expired", await Error(stolen));
        for (var i = 0; i < 10; i++)
            Assert.Contains("expired", await Error(await Verify(outsider, id, WrongFor(code))));

        // Asking for codes uses up only the outsider's own limits.
        for (var i = 0; i < LoginCodes.MaxPerWindow; i++)
        {
            await RequestCode(outsider, id);
            api.Time.Advance(TimeSpan.FromSeconds(61));
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await outsider.PostAsJsonAsync("/api/auth/request-code", new { universityId = id })).StatusCode);
        await RequestCode(api.Browser(), id);

        Assert.Equal(HttpStatusCode.OK, (await Verify(victim, id, code)).StatusCode);
    }

    [Fact]
    public async Task A_new_code_retires_the_older_one_for_good()
    {
        using var api = new ClockedApi();
        var id = NewId();
        var c = api.Browser();
        var first = await RequestCode(c, id);
        api.Time.Advance(TimeSpan.FromSeconds(61));
        var second = await RequestCode(c, id);
        if (first == second) return; // one in a million: nothing to tell apart

        // While the newer code is live, the older one is just a wrong code.
        Assert.Equal(HttpStatusCode.BadRequest, (await Verify(c, id, first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Verify(c, id, second)).StatusCode);
        // Once the newer code is used, the older one still doesn't come back.
        var again = await Verify(c, id, first);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("expired", await Error(again));
    }

    [Fact]
    public async Task One_ID_gets_at_most_20_codes_a_day_except_on_a_browser_it_signed_in_on()
    {
        using var api = new ClockedApi();
        var id = NewId();
        var mine = api.Browser();
        Assert.Equal(HttpStatusCode.OK, (await Verify(mine, id, await RequestCode(mine, id))).StatusCode);
        // Signing out doesn't make it a stranger's browser.
        await mine.PostAsync("/api/auth/logout", null);

        for (var i = 1; i < LoginCodes.MaxPerDay; i++) await RequestCode(api.Browser(), id);
        var refused = await api.Browser().PostAsJsonAsync("/api/auth/request-code", new { universityId = id });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var message = await Error(refused);
        Assert.Contains("hours", message);
        Assert.DoesNotContain("second", message); // the app would show it as a countdown

        api.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await Verify(mine, id, await RequestCode(mine, id))).StatusCode);

        api.Time.Advance(LoginCodes.Day);
        await RequestCode(api.Browser(), id);
    }

    [Fact]
    public async Task One_ID_takes_at_most_30_wrong_codes_a_day_except_on_a_browser_it_signed_in_on()
    {
        using var api = new ClockedApi();
        var id = NewId();
        var mine = api.Browser();
        Assert.Equal(HttpStatusCode.OK, (await Verify(mine, id, await RequestCode(mine, id))).StatusCode);
        var waiting = api.Browser();
        var waitingCode = await RequestCode(waiting, id);

        for (var i = 0; i < LoginCodes.MaxWrongPerDay / LoginCodes.MaxAttempts; i++)
        {
            var guesser = api.Browser();
            var code = await RequestCode(guesser, id);
            for (var t = 0; t < LoginCodes.MaxAttempts; t++)
                Assert.Equal(HttpStatusCode.BadRequest, (await Verify(guesser, id, WrongFor(code))).StatusCode);
        }

        var more = await api.Browser().PostAsJsonAsync("/api/auth/request-code", new { universityId = id });
        Assert.Equal(HttpStatusCode.TooManyRequests, more.StatusCode);
        Assert.Contains("wrong codes", await Error(more));
        var late = await Verify(waiting, id, waitingCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, late.StatusCode);
        Assert.DoesNotMatch("expired|new code|new one", await Error(late));

        api.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await Verify(mine, id, await RequestCode(mine, id))).StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_are_capped_per_address_per_hour()
    {
        using var api = new ClockedApi(wrongCodesPerHour: 3);
        var spray = api.Browser("203.0.113.7");
        for (var i = 0; i < 3; i++)
        {
            var id = NewId();
            Assert.Contains("tries left", await Error(await Verify(spray, id, WrongFor(await RequestCode(spray, id)))));
        }
        var target = NewId();
        var code = await RequestCode(spray, target);
        var refused = await Verify(spray, target, code);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("network", await Error(refused));

        // Another address is unaffected, and the first one recovers after the hour.
        var other = api.Browser("203.0.113.8");
        var otherId = NewId();
        Assert.Equal(HttpStatusCode.OK, (await Verify(other, otherId, await RequestCode(other, otherId))).StatusCode);
        api.Time.Advance(TimeSpan.FromHours(2));
        var fresh = await RequestCode(spray, target);
        Assert.Equal(HttpStatusCode.OK, (await Verify(spray, target, fresh)).StatusCode);
    }

    [Fact]
    public async Task Day_old_codes_are_deleted()
    {
        using var api = new ClockedApi();
        string a = NewId(), b = NewId();
        await RequestCode(api.Browser(), a);
        await RequestCode(api.Browser(), b);
        api.Time.Advance(LoginCodes.Day + TimeSpan.FromMinutes(1));

        // Asking again clears that ID's old codes...
        await RequestCode(api.Browser(), a);
        Assert.Equal(1, await api.Db(db => db.LoginCodes.CountAsync(c => c.UniversityId == a)));
        Assert.Equal(1, await api.Db(db => db.LoginCodes.CountAsync(c => c.UniversityId == b)));
        // ...and the hourly sweep clears everyone's.
        await api.Db(db => LoginCodes.Purge(db, api.Time.GetUtcNow(), CancellationToken.None));
        Assert.Equal(0, await api.Db(db => db.LoginCodes.CountAsync(c => c.UniversityId == b)));
        Assert.Equal(1, await api.Db(db => db.LoginCodes.CountAsync(c => c.UniversityId == a)));
    }
}

public class AdminLoginSafetyTests
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    static async Task<(HttpStatusCode, string)> Login(ClockedApi api, string ip, string username, string password)
    {
        var r = await api.Browser(ip).PostAsJsonAsync("/api/auth/admin-login", new { username, password });
        var body = await Json(r);
        return (r.StatusCode, body.TryGetProperty("error", out var e) ? e.GetString()! : "");
    }

    static async Task AddAdmin(ClockedApi api, string username, string password)
    {
        using var scope = api.App.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var user = new AdminUser { Username = username, Role = AdminRole.Rsa, MustChangePassword = false, CreatedAt = DateTimeOffset.UtcNow };
        user.PasswordHash = AuthEndpoints.HashPassword(user, password);
        db.AdminUsers.Add(user);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Over_long_usernames_and_passwords_are_refused_without_being_counted()
    {
        using var api = new ClockedApi();
        await AddAdmin(api, "rsa-long", "right-password-1");
        var huge = new string('a', 65);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(api, "198.51.100.1", huge + i, "x")).Item1);
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(api, "198.51.100.1", "rsa-long", new string('p', 257))).Item1);
        }
        Assert.Equal(HttpStatusCode.OK, (await Login(api, "198.51.100.1", "rsa-long", "right-password-1")).Item1);
    }

    [Fact]
    public void The_tries_table_forgets_old_entries_and_stays_bounded()
    {
        var t0 = DateTimeOffset.UtcNow;
        var table = new TryTable(5, TimeSpan.FromMinutes(15), maxEntries: 3);
        foreach (var key in new[] { "a", "b", "c", "d" }) table.TryAdd(key, t0);
        Assert.Equal(3, table.Count);
        table.TryAdd("e", t0 + TimeSpan.FromMinutes(16));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task The_right_password_works_during_an_account_lock_and_unknown_names_answer_the_same()
    {
        using var api = new ClockedApi();
        await AddAdmin(api, "rsa-held", "right-password-1");
        foreach (var name in new[] { "rsa-held", "rsa-ghost" })
            for (var ip = 1; ip <= 4; ip++)
                for (var i = 0; i < 5; i++)
                    Assert.Equal(HttpStatusCode.Unauthorized, (await Login(api, $"198.51.100.{ip}", name, "wrong-guess")).Item1);

        // From an address that never tried: both locked, word for word the same.
        var real = await Login(api, "198.51.100.9", "rsa-held", "wrong-guess");
        var ghost = await Login(api, "198.51.100.9", "rsa-ghost", "wrong-guess");
        Assert.Equal(HttpStatusCode.TooManyRequests, real.Item1);
        Assert.Equal(real, ghost);

        // The lock only turns away wrong passwords.
        Assert.Equal(HttpStatusCode.OK, (await Login(api, "198.51.100.10", "rsa-held", "right-password-1")).Item1);
    }

    [Fact]
    public async Task Wrong_passwords_older_than_15_minutes_do_not_count_towards_the_account_lock()
    {
        using var api = new ClockedApi();
        await AddAdmin(api, "rsa-old", "right-password-1");
        for (var i = 0; i < AuthEndpoints.AdminTriesPerAccount - 1; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await Login(api, $"198.51.100.{i / 5 + 1}", "rsa-old", "wrong-guess")).Item1);
        api.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(api, "198.51.100.20", "rsa-old", "wrong-guess")).Item1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(api, "198.51.100.21", "rsa-old", "wrong-guess")).Item1);
    }

    [Fact]
    public async Task Wrong_passwords_sent_together_are_all_counted()
    {
        using var api = new ClockedApi();
        await AddAdmin(api, "rsa-burst", "right-password-1");
        var spread = await Task.WhenAll(Enumerable.Range(1, AuthEndpoints.AdminTriesPerAccount)
            .Select(i => Login(api, $"198.51.101.{i}", "rsa-burst", "wrong-guess")));
        Assert.All(spread, r => Assert.Equal(HttpStatusCode.Unauthorized, r.Item1));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(api, "198.51.101.200", "rsa-burst", "wrong-guess")).Item1);
        Assert.NotNull(await api.Db(db => db.AdminUsers.Where(a => a.Username == "rsa-burst").Select(a => a.LockedUntil).FirstAsync()));

        // From one address, only five get as far as the password check.
        await AddAdmin(api, "rsa-burst2", "right-password-1");
        var together = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Login(api, "198.51.102.1", "rsa-burst2", "wrong-guess")));
        Assert.Equal(5, together.Count(r => r.Item1 == HttpStatusCode.Unauthorized));
    }
}

public class CrossSiteTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    static HttpRequestMessage Post(string url, HttpContent? body, string? origin = null, string? fetchSite = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
        if (origin is not null) m.Headers.Add("Origin", origin);
        if (fetchSite is not null) m.Headers.Add("Sec-Fetch-Site", fetchSite);
        return m;
    }

    [Fact]
    public async Task Signed_in_requests_from_another_site_are_refused()
    {
        await factory.AddAdmin("owner-csrf", "owner-password-1", AdminRole.Owner, mustChange: false);
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username = "owner-csrf", password = "owner-password-1" })).StatusCode);
        var sem = (await Json(await c.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2031 }))).GetProperty("id").GetInt32();
        MultipartFormDataContent File() => new() { { new ByteArrayContent("code,name,major,type,year,section,instructor,days,time,room\nLS101,Academic English,,LS,,1,Ms. D,Thu,08:00-09:30,A203\n"u8.ToArray()), "file", "evil.csv" } };
        var url = $"/api/admin/semesters/{sem}/upload/apply";

        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post(url, File(), origin: "https://evil.tryzarqa.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post(url, File(), fetchSite: "same-site"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post(url, File(), origin: "https://attacker.example", fetchSite: "cross-site"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Post($"/api/admin/semesters/{sem}/publish", null, origin: "null"))).StatusCode);

        // The app itself, and clients that aren't browser pages, still work.
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post(url, File(), origin: "http://localhost", fetchSite: "same-origin"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Post($"/api/admin/semesters/{sem}/publish", null))).StatusCode);
        // So does the Classy Sync extension's Connect button.
        var connect = Post("/api/admin/sync-tokens", JsonContent.Create(new { name = "Classy Sync" }), origin: "chrome-extension://abcdefghijklmnop", fetchSite: "none");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(connect)).StatusCode);
    }
}
