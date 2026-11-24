using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>Teachers sign in with a code emailed to their staff address. The code is never in an answer, even in test mode.</summary>
public class TeacherSignInTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next;
    /// <summary>A staff address nobody else in these tests uses (letters only, so it can never look like a timetable name with digits).</summary>
    static string NewEmail()
    {
        var n = Interlocked.Increment(ref next);
        return $"sign.in{(char)('a' + n / 26 % 26)}{(char)('a' + n % 26)}@gutech.edu.om";
    }

    static async Task<JsonElement> Session(HttpClient c) => await Json(await c.GetAsync("/api/auth/session"));
    static async Task<string?> Kind(HttpClient c) => (await Session(c)).GetProperty("kind").GetString();
    static List<string> Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToList();
    static Task<HttpResponseMessage> RequestCode(HttpClient c, string? email) => c.PostAsJsonAsync("/api/auth/teacher/request-code", new { email });

    [Fact]
    public async Task An_emailed_code_signs_a_teacher_in_and_is_never_in_an_answer()
    {
        var email = NewEmail();
        var c = factory.Client();
        // Test mode is on for this host: students get their code on screen, teachers never do.
        var sent = await RequestCode(c, $"  {email.ToUpperInvariant()} ");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var text = await sent.Content.ReadAsStringAsync();
        var answer = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["email", "resendAfterSeconds"], Names(answer));
        Assert.Equal(email, answer.GetProperty("email").GetString());
        Assert.Equal(60, answer.GetProperty("resendAfterSeconds").GetInt32());
        var code = factory.Emails.CodeFor(email);
        Assert.Matches(@"^\d{6}$", code);
        Assert.DoesNotContain(code, text);
        Assert.DoesNotContain("testCode", text, StringComparison.OrdinalIgnoreCase);

        var verified = await VerifyTeacher(c, email, code);
        var verifyText = await verified.Content.ReadAsStringAsync();
        var verify = await Json(verified);
        Assert.Equal(["isNew", "status"], Names(verify));
        Assert.True(verify.GetProperty("isNew").GetBoolean());
        Assert.Equal("pick-name", verify.GetProperty("status").GetString());
        Assert.DoesNotContain(code, verifyText);

        var session = await Session(c);
        Assert.Equal(["kind", "name", "role", "mustChangePassword", "teaching", "codeFresh", "teacher"], Names(session));
        Assert.Equal("teacher", session.GetProperty("kind").GetString());
        Assert.Equal(email, session.GetProperty("name").GetString());
        Assert.Equal("teacher", session.GetProperty("role").GetString());
        Assert.False(session.GetProperty("mustChangePassword").GetBoolean());
        Assert.True(session.GetProperty("teaching").GetBoolean());
        Assert.True(session.GetProperty("codeFresh").GetBoolean());
        var id = await factory.Services.Db(db => db.Teachers.Where(t => t.Email == email).Select(t => t.Id).SingleAsync());
        Assert.Equal(id, session.GetProperty("teacher").GetProperty("id").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("teacher").GetProperty("displayName").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/teach/me")).StatusCode);

        // A code works once, and the account is the same one next time.
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyTeacher(c, email, code)).StatusCode);
        Assert.False((await SignInTeacher(factory.Client(), factory.Emails, email)).GetProperty("isNew").GetBoolean());
        Assert.Equal(1, await factory.Services.Db(db => db.Teachers.CountAsync(t => t.Email == email)));

        // Signing out ends it.
        await c.PostAsync("/api/auth/logout", null);
        Assert.Null(await Kind(c));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/teach/me")).StatusCode);
    }

    [Theory]
    [InlineData("24-0161@student.gutech.edu.om", "student address")]
    [InlineData("Someone@Student.GUtech.edu.om", "student address")]
    [InlineData("karim.hadad@gmail.com", "staff email")]
    [InlineData("karim.hadad@gutech.edu.om.evil.com", "staff email")]
    [InlineData("karim..hadad@gutech.edu.om", "staff email")]
    [InlineData("karim,hadad@gutech.edu.om", "staff email")]
    [InlineData("24-0161", "staff email")]
    [InlineData("", "staff email")]
    [InlineData(null, "staff email")]
    public async Task Student_and_foreign_addresses_are_refused(string? typed, string says)
    {
        var c = factory.Client();
        var r = await RequestCode(c, typed);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains(says, await Error(r));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/teacher/verify", new { email = typed, code = "123456" })).StatusCode);
        if (typed is { Length: > 0 }) Assert.Equal(0, factory.Emails.Codes(typed.ToLowerInvariant()));
    }

    [Fact]
    public async Task A_blocked_email_gets_the_normal_answer_a_cant_sign_in_email_and_no_code()
    {
        string blocked = NewEmail(), unknown = NewEmail();
        await factory.Services.Db(async db =>
        {
            db.BlockedEmails.Add(new BlockedEmail { Email = blocked, BlockedAt = DateTimeOffset.UtcNow, BlockedBy = "test" });
            await db.SaveChangesAsync();
        });

        // Word for word the answer any address gets, so nobody can test which addresses were removed.
        var c = factory.Client();
        var forBlocked = await RequestCode(c, blocked);
        var forUnknown = await RequestCode(factory.Client(), unknown);
        Assert.Equal(HttpStatusCode.OK, forBlocked.StatusCode);
        Assert.Equal((await forUnknown.Content.ReadAsStringAsync()).Replace(unknown, blocked), await forBlocked.Content.ReadAsStringAsync());

        // The blocked person is told in their own mailbox; no code goes out.
        Assert.True(factory.Emails.CannotSignIn(blocked));
        Assert.Equal(0, factory.Emails.Codes(blocked));
        Assert.False(factory.Emails.CannotSignIn(unknown));
        Assert.Equal(1, factory.Emails.Codes(unknown));
        // The limits behave as for anyone: a code row is kept, and a second request must wait.
        Assert.Equal(1, await factory.Services.Db(db => db.LoginCodes.CountAsync(x => x.UniversityId == blocked)));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await RequestCode(c, blocked)).StatusCode);

        // Verify answers as for any wrong code, whatever is typed.
        var wrong = await VerifyTeacher(c, blocked, "123456");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("Wrong code. 2 tries left.", await Error(wrong));
        Assert.Null(await Kind(c));
        Assert.Equal(0, await factory.Services.Db(db => db.Teachers.CountAsync(t => t.Email == blocked)));
    }

    [Fact]
    public async Task A_code_asked_for_before_a_block_does_not_sign_in_after_it()
    {
        var email = NewEmail();
        var c = factory.Client();
        var code = await RequestTeacherCode(c, factory.Emails, email);
        await factory.Services.Db(async db =>
        {
            db.BlockedEmails.Add(new BlockedEmail { Email = email, BlockedAt = DateTimeOffset.UtcNow, BlockedBy = "test" });
            await db.SaveChangesAsync();
        });
        var r = await VerifyTeacher(c, email, code);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("expired", await Error(r));
        Assert.Null(await Kind(c));
        Assert.Equal(0, await factory.Services.Db(db => db.Teachers.CountAsync(t => t.Email == email)));
    }

    [Fact]
    public async Task An_email_code_takes_three_tries()
    {
        var email = NewEmail();
        var c = factory.Client();
        var code = await RequestTeacherCode(c, factory.Emails, email);
        var wrong = WrongFor(code);
        Assert.Equal("Wrong code. 2 tries left.", await Error(await VerifyTeacher(c, email, wrong)));
        Assert.Equal("Wrong code. 1 try left.", await Error(await VerifyTeacher(c, email, wrong)));
        Assert.Equal("Wrong code. Send a new one.", await Error(await VerifyTeacher(c, email, wrong)));
        // The right code is no use now. (Students keep five tries: AuthFlowTests.)
        var right = await VerifyTeacher(c, email, code);
        Assert.Equal(HttpStatusCode.BadRequest, right.StatusCode);
        Assert.Contains("new code", await Error(right));
        Assert.Null(await Kind(c));
        // Badly formed codes are refused without using a try.
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyTeacher(factory.Client(), email, "12ab56")).StatusCode);
    }

    [Fact]
    public async Task The_resend_wait_the_browser_binding_and_the_daily_limits_hold_for_an_email()
    {
        using var w = await TeachWorld.Create(seed: false);
        var email = NewEmail();
        var mine = w.Browser();
        var code = await RequestTeacherCode(mine, w.Emails, email);
        // A second code within a minute is refused.
        var soon = await RequestCode(mine, email);
        Assert.Equal(HttpStatusCode.TooManyRequests, soon.StatusCode);
        Assert.Contains("Wait", await Error(soon));

        // A code only works in the browser that asked for it, and nobody else can burn it.
        var outsider = w.Browser();
        Assert.Contains("expired", await Error(await VerifyTeacher(outsider, email, code)));
        for (var i = 0; i < 5; i++) Assert.Contains("expired", await Error(await VerifyTeacher(outsider, email, WrongFor(code))));
        // Four codes in 15 minutes per browser.
        for (var i = 0; i < LoginCodes.MaxPerWindow; i++)
        {
            await RequestTeacherCode(outsider, w.Emails, email);
            w.Time.Advance(TimeSpan.FromSeconds(61));
        }
        var fifth = await RequestCode(outsider, email);
        Assert.Equal(HttpStatusCode.TooManyRequests, fifth.StatusCode);
        Assert.Equal("Too many codes for this email. Wait 15 minutes and try again.", await Error(fifth));

        // The outsider's codes retired nothing of the teacher's: a new code here still signs in.
        code = await RequestTeacherCode(mine, w.Emails, email);
        Assert.Equal(HttpStatusCode.OK, (await VerifyTeacher(mine, email, code)).StatusCode);
        await mine.PostAsync("/api/auth/logout", null);

        // Twenty codes a day for one address, except on a browser it has signed in on.
        var soFar = await w.Db(db => db.LoginCodes.CountAsync(x => x.UniversityId == email));
        for (var i = soFar; i < LoginCodes.MaxPerDay; i++) await RequestTeacherCode(w.Browser(), w.Emails, email);
        var refused = await RequestCode(w.Browser(), email);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.StartsWith("This email has had too many sign-in codes today.", await Error(refused));
        w.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.OK, (await VerifyTeacher(mine, email, await RequestTeacherCode(mine, w.Emails, email))).StatusCode);
        w.Time.Advance(LoginCodes.Day);
        await RequestTeacherCode(w.Browser(), w.Emails, email);
    }

    [Fact]
    public async Task One_address_can_only_ask_for_so_many_teacher_codes()
    {
        using var w = await TeachWorld.Create(seed: false, b => b.UseSetting("RateLimits:TeacherRequestCode", "3"));
        const string ip = "203.0.113.30";
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await RequestCode(w.Browser(ip), NewEmail())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await RequestCode(w.Browser(ip), NewEmail())).StatusCode);
        // Students on that network, and teachers on another, are not held back by it.
        Assert.Equal(HttpStatusCode.OK, (await w.Browser(ip).PostAsJsonAsync("/api/auth/request-code", new { universityId = "25-8801" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RequestCode(w.Browser("203.0.113.31"), NewEmail())).StatusCode);
    }

    /// <summary>Uses up a code's three tries on wrong codes for a new address, from a new browser at <paramref name="ip"/>.</summary>
    static async Task ThreeWrong(TeachWorld w, string ip, int tries = 3)
    {
        var email = NewEmail();
        var c = w.Browser(ip);
        var code = await RequestTeacherCode(c, w.Emails, email);
        for (var i = 0; i < tries; i++) Assert.Equal(HttpStatusCode.BadRequest, (await VerifyTeacher(c, email, WrongFor(code))).StatusCode);
    }

    [Fact]
    public async Task Thirty_wrong_codes_from_one_address_stop_its_teacher_sign_ins_and_nothing_else()
    {
        using var w = await TeachWorld.Create(seed: false);
        const string ip = "203.0.113.40";
        for (var i = 0; i < 10; i++) await ThreeWrong(w, ip);

        // The right code is refused there now, on any browser the address hasn't signed in on.
        var email = NewEmail();
        var c = w.Browser(ip);
        var code = await RequestTeacherCode(c, w.Emails, email);
        var refused = await VerifyTeacher(c, email, code);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("Too many wrong codes from this network. Try again in an hour, or sign in on a device you've used before.", await Error(refused));

        // Students on that network sign in as before, and so do teachers elsewhere.
        await w.SignInStudent("25-8802", ip);
        await w.SignInTeacher(NewEmail(), "203.0.113.41");

        // The count falls after the hour. (The refused try used none of the code's tries, but the code has expired.)
        w.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.OK, (await VerifyTeacher(c, email, await RequestTeacherCode(c, w.Emails, email))).StatusCode);
    }

    [Fact]
    public async Task Forty_wrong_codes_for_addresses_with_no_account_pause_new_sign_ins_only_and_tell_staff_once()
    {
        using var w = await TeachWorld.Create(seed: false);
        var existing = NewEmail();
        await w.SignInTeacher(existing, "198.51.100.1");
        Task<int> Alerts() => w.Db(db => db.TeachNotices.CountAsync(n => n.Kind == "sign-in-guessing"));

        // From many addresses, so no single one reaches its own limit: 39 wrong codes, and nothing is paused.
        for (var i = 0; i < 13; i++) await ThreeWrong(w, $"198.51.100.{10 + i}");
        Assert.Equal(0, await Alerts());
        var fresh = NewEmail();
        var c = w.Browser("198.51.100.100");
        var code = await RequestTeacherCode(c, w.Emails, fresh);

        // The fortieth starts the pause.
        await ThreeWrong(w, "198.51.100.101", tries: 1);
        var paused = await VerifyTeacher(c, fresh, code);
        Assert.Equal(HttpStatusCode.TooManyRequests, paused.StatusCode);
        Assert.Equal("New teacher sign-ins are paused for an hour. Try again later.", await Error(paused));
        Assert.Equal(0, await w.Db(db => db.Teachers.CountAsync(t => t.Email == fresh)));

        // Staff are told once, however many more tries come.
        var notice = await w.Db(db => db.TeachNotices.SingleAsync(n => n.Kind == "sign-in-guessing"));
        Assert.Equal(TeachNotice.Staff, notice.Audience);
        Assert.Equal("Many wrong teacher sign-in codes. New teacher sign-ins are paused for an hour.", notice.Body);
        Assert.Null(notice.SentAt);
        Assert.Null(notice.TeacherId);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.TooManyRequests, (await VerifyTeacher(w.Browser($"198.51.100.{110 + i}"), NewEmail(), "123456")).StatusCode);
        Assert.Equal(1, await Alerts());

        // A teacher who already has an account still signs in, wrong tries and all.
        var back = w.Browser("198.51.100.120");
        var backCode = await RequestTeacherCode(back, w.Emails, existing);
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyTeacher(back, existing, WrongFor(backCode))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VerifyTeacher(back, existing, backCode)).StatusCode);
        Assert.Equal(1, await Alerts());
        // Students are not affected.
        await w.SignInStudent("25-8803", "198.51.100.100");

        // After the hour new addresses sign in again.
        w.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.OK, (await VerifyTeacher(c, fresh, await RequestTeacherCode(c, w.Emails, fresh))).StatusCode);
    }

    [Fact]
    public async Task Thirty_wrong_codes_on_a_trusted_browser_end_that_addresses_trust_even_with_the_sign_in_cookie_cleared()
    {
        using var w = await TeachWorld.Create(seed: false, b => b.UseSetting("RateLimits:TeacherWrongCodesPerHour", "3"));
        const string ip = "203.0.113.60";
        var email = NewEmail();
        var mine = w.Browser(ip);
        var signedIn = await VerifyTeacher(mine, email, await RequestTeacherCode(mine, w.Emails, email));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        // What stays on a lecture-room PC after the sign-in cookies are cleared: only "this address signed in here".
        var device = signedIn.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(SignInCookies.DeviceCookie + "=")).Split(';')[0];
        HttpClient Cleared()
        {
            var c = w.Browser(ip);
            c.DefaultRequestHeaders.Add("Cookie", device);
            return c;
        }

        // Someone else uses up the network's wrong codes: a stranger there is stopped, the trusted browser is not.
        await ThreeWrong(w, ip);
        var other = NewEmail();
        var stranger = w.Browser(ip);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await VerifyTeacher(stranger, other, await RequestTeacherCode(stranger, w.Emails, other))).StatusCode);
        var kept = Cleared();
        Assert.Equal(HttpStatusCode.OK, (await VerifyTeacher(kept, email, await RequestTeacherCode(kept, w.Emails, email))).StatusCode);

        // Thirty wrong codes for the address on its trusted browser: none is refused as "too many" on the way.
        for (var round = 0; round < LoginCodes.MaxWrongPerDay / CodeSignIn.EmailMaxAttempts; round++)
        {
            w.Time.Advance(TimeSpan.FromMinutes(16));
            var code = await RequestTeacherCode(mine, w.Emails, email);
            for (var i = 0; i < CodeSignIn.EmailMaxAttempts; i++)
                Assert.Equal(HttpStatusCode.BadRequest, (await VerifyTeacher(mine, email, WrongFor(code))).StatusCode);
        }

        // Now it is held to the daily caps like any other browser, and clearing the sign-in cookie doesn't reset that.
        w.Time.Advance(TimeSpan.FromMinutes(16));
        foreach (var browser in new[] { mine, Cleared(), w.Browser(ip) })
        {
            var refused = await RequestCode(browser, email);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.StartsWith("Too many wrong codes for this email today.", await Error(refused));
        }

        // A day later the count has fallen and the browser is trusted again.
        w.Time.Advance(LoginCodes.Day);
        Assert.Equal(HttpStatusCode.OK, (await VerifyTeacher(mine, email, await RequestTeacherCode(mine, w.Emails, email))).StatusCode);
    }

    [Fact]
    public async Task Signing_out_everywhere_ends_other_sign_ins_keeps_this_one_and_gives_no_fresh_code()
    {
        using var w = await TeachWorld.Create(seed: false);
        var email = NewEmail();
        HttpClient here = await w.SignInTeacher(email), there = await w.SignInTeacher(email), third = await w.SignInTeacher(email);
        // More than a week on, this browser's code is stale.
        w.Time.Advance(TimeSpan.FromDays(8));
        Assert.False((await Session(here)).GetProperty("codeFresh").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await here.PostAsync("/api/teach/sign-out-everywhere", null)).StatusCode);
        var session = await Session(here);
        Assert.Equal("teacher", session.GetProperty("kind").GetString());
        Assert.False(session.GetProperty("codeFresh").GetBoolean());
        Assert.False((await Json(await here.GetAsync("/api/teach/me"))).GetProperty("codeFresh").GetBoolean());
        Assert.Null(await Kind(there));
        Assert.Equal(HttpStatusCode.Unauthorized, (await there.GetAsync("/api/teach/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await third.GetAsync("/api/teach/me")).StatusCode);
        Assert.Equal(1, await w.Db(db => db.Teachers.Where(t => t.Email == email).Select(t => t.SessionVersion).SingleAsync()));

        // A new sign-in is a new code.
        var again = await w.SignInTeacher(email);
        Assert.True((await Session(again)).GetProperty("codeFresh").GetBoolean());

        // Staff end every sign-in of the account (the dashboard does this by raising the version); the account stays.
        await w.Db(db => db.Teachers.Where(t => t.Email == email).ExecuteUpdateAsync(u => u.SetProperty(t => t.SessionVersion, t => t.SessionVersion + 1)));
        Assert.Null(await Kind(here));
        Assert.Null(await Kind(again));
        Assert.False((await SignInTeacher(w.Browser(), w.Emails, email)).GetProperty("isNew").GetBoolean());
    }

    [Fact]
    public async Task A_removed_teacher_is_signed_out_at_once()
    {
        var email = NewEmail();
        var c = await SignInTeacher(factory, email);
        Assert.Equal("teacher", await Kind(c));
        await factory.Services.Db(db => db.Teachers.Where(t => t.Email == email).ExecuteDeleteAsync());
        Assert.Null(await Kind(c));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/teach/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/rooms/free")).StatusCode);
    }

    static TeacherPushSubscription TeacherDevice(int teacherId, string endpoint) =>
        new() { TeacherId = teacherId, Endpoint = endpoint, P256dh = "p", Auth = "a", CreatedAt = DateTimeOffset.UtcNow };
    static PushSubscription StudentDevice(int studentId, string endpoint) =>
        new() { StudentId = studentId, Endpoint = endpoint, P256dh = "p", Auth = "a", CreatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public async Task Logout_drops_only_the_teachers_own_device()
    {
        using var w = await TeachWorld.Create();
        // Student 1 and teacher 1 share an id, so a mix-up between the two tables would show here.
        Assert.Equal(w.Teacher1Id, w.StudentRowIds[0]);
        const string mine = "https://push.example/teacher-1", theirs = "https://push.example/teacher-2", students = "https://push.example/student-1";
        await w.Db(async db =>
        {
            db.TeacherPushSubscriptions.AddRange(TeacherDevice(w.Teacher1Id, mine), TeacherDevice(w.Teacher2Id, theirs));
            db.PushSubscriptions.Add(StudentDevice(w.StudentRowIds[0], students));
            await db.SaveChangesAsync();
        });
        Task<List<string>> Left() => w.Db(async db =>
            (await db.TeacherPushSubscriptions.Select(x => x.Endpoint).ToListAsync()).Concat(await db.PushSubscriptions.Select(x => x.Endpoint).ToListAsync()).Order().ToList());

        // Naming someone else's device removes nothing.
        foreach (var endpoint in new[] { theirs, students })
        {
            var c = await w.SignInTeacher(TeachWorld.Teacher1Email);
            Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/auth/logout", new { endpoint })).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/teach/me")).StatusCode);
            Assert.Equal([students, mine, theirs], await Left());
        }
        // A student naming the teacher's device removes nothing either.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Students[0].PostAsJsonAsync("/api/auth/logout", new { endpoint = mine })).StatusCode);
        Assert.Equal([students, mine, theirs], await Left());

        Assert.Equal(HttpStatusCode.NoContent, (await w.Teacher1.PostAsJsonAsync("/api/auth/logout", new { endpoint = mine })).StatusCode);
        Assert.Equal([students, theirs], await Left());
    }

    [Fact]
    public async Task A_device_belongs_to_whoever_signed_in_on_it_last()
    {
        using var w = await TeachWorld.Create();
        const string device = "https://push.example/shared-device", other = "https://push.example/other-device";
        async Task<(int Students, int Teachers)> On(string endpoint) =>
            (await w.Db(db => db.PushSubscriptions.CountAsync(x => x.Endpoint == endpoint)), await w.Db(db => db.TeacherPushSubscriptions.CountAsync(x => x.Endpoint == endpoint)));

        // A student's device, then a teacher signs in on it: the student stops getting notifications there.
        await w.Db(async db =>
        {
            db.PushSubscriptions.AddRange(StudentDevice(w.StudentRowIds[0], device), StudentDevice(w.StudentRowIds[1], other));
            db.TeacherPushSubscriptions.Add(TeacherDevice(w.Teacher2Id, "https://push.example/teacher-2"));
            await db.SaveChangesAsync();
        });
        var c = w.Browser();
        await Json(await VerifyTeacher(c, TeachWorld.Teacher1Email, await RequestTeacherCode(c, w.Emails, TeachWorld.Teacher1Email), device));
        Assert.Equal((0, 0), await On(device));
        Assert.Equal((1, 0), await On(other));

        // Another teacher's device, then this teacher signs in on it; their own device stays theirs.
        await w.Db(async db =>
        {
            db.TeacherPushSubscriptions.AddRange(TeacherDevice(w.Teacher2Id, device), TeacherDevice(w.Teacher1Id, "https://push.example/teacher-1"));
            await db.SaveChangesAsync();
        });
        w.Time.Advance(TimeSpan.FromSeconds(61));
        await Json(await VerifyTeacher(c, TeachWorld.Teacher1Email, await RequestTeacherCode(c, w.Emails, TeachWorld.Teacher1Email), device));
        Assert.Equal((0, 0), await On(device));
        w.Time.Advance(TimeSpan.FromSeconds(61));
        await Json(await VerifyTeacher(c, TeachWorld.Teacher1Email, await RequestTeacherCode(c, w.Emails, TeachWorld.Teacher1Email), "https://push.example/teacher-1"));
        Assert.Equal((0, 1), await On("https://push.example/teacher-1"));

        // A teacher's device, then a student signs in on it.
        await w.Db(async db =>
        {
            db.TeacherPushSubscriptions.Add(TeacherDevice(w.Teacher1Id, device));
            await db.SaveChangesAsync();
        });
        var s = w.Browser();
        var sent = await Json(await s.PostAsJsonAsync("/api/auth/request-code", new { universityId = TeachWorld.StudentIdsList[0] }));
        await Json(await s.PostAsJsonAsync("/api/auth/verify", new { universityId = TeachWorld.StudentIdsList[0], code = sent.GetProperty("testCode").GetString(), endpoint = device }));
        Assert.Equal((0, 0), await On(device));
        Assert.Equal((0, 1), await On("https://push.example/teacher-2"));
        Assert.Equal((1, 0), await On(other));
    }

    [Fact]
    public async Task Student_sign_in_answers_are_unchanged_and_a_student_with_teaching_rights_gets_no_code_on_screen()
    {
        const string id = "25-8810";
        var c = factory.Client();
        var sent = await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }));
        Assert.Equal(["email", "resendAfterSeconds", "testCode"], Names(sent));
        Assert.Equal($"{id}@student.gutech.edu.om", sent.GetProperty("email").GetString());
        Assert.Equal(60, sent.GetProperty("resendAfterSeconds").GetInt32());
        Assert.Matches(@"^\d{6}$", sent.GetProperty("testCode").GetString());
        var verify = await Json(await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code = sent.GetProperty("testCode").GetString() }));
        Assert.Equal(["isNew"], Names(verify));
        Assert.True(verify.GetProperty("isNew").GetBoolean());

        // The session keeps its fields and gains two.
        var session = await Session(c);
        Assert.Equal(["kind", "name", "role", "mustChangePassword", "batchRep", "teaching", "codeFresh"], Names(session));
        Assert.Equal(("student", id, JsonValueKind.Null, JsonValueKind.False), (session.GetProperty("kind").GetString(), session.GetProperty("name").GetString(), session.GetProperty("role").ValueKind, session.GetProperty("mustChangePassword").ValueKind));
        Assert.False(session.GetProperty("teaching").GetBoolean());
        Assert.True(session.GetProperty("codeFresh").GetBoolean());
        // A dashboard account's session is exactly as before.
        await factory.AddAdmin("sign-in-rsa", "sign-in-password-1", AdminRole.Rsa, mustChange: false);
        var rsa = await SignInAdmin(factory.Client(), "sign-in-rsa", "sign-in-password-1");
        Assert.Equal(["kind", "name", "role", "mustChangePassword"], Names(await Session(rsa)));
        Assert.Equal(["kind"], Names(await Session(factory.Client())));

        // Given teaching rights, the student can change classes: no code on screen any more, only by email.
        var studentId = await factory.Services.Db(db => db.Students.Where(s => s.UniversityId == id).Select(s => s.Id).SingleAsync());
        await factory.Services.AddTeachingStudent(studentId, "Lab Helper");
        var again = factory.Client();
        var hidden = await Json(await again.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }));
        Assert.Equal(["email", "resendAfterSeconds", "testCode"], Names(hidden));
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("testCode").ValueKind);
        await Json(await again.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code = factory.Emails.CodeFor($"{id}@student.gutech.edu.om") }));
        Assert.True((await Session(again)).GetProperty("teaching").GetBoolean());
    }

    [Fact]
    public async Task The_teacher_pages_load_the_app()
    {
        var root = Path.Combine(Path.GetTempPath(), "unischedule-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<!doctype html><title>Classy</title>");
        try
        {
            using var own = new ApiFactory();
            using var app = own.WithWebHostBuilder(b => b.UseWebRoot(root));
            var c = app.CreateClient();
            foreach (var path in new[] { "/teach", "/teach/name", "/teach/classes" })
            {
                var r = await c.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                Assert.Contains("<title>Classy</title>", await r.Content.ReadAsStringAsync());
                Assert.True(r.Headers.CacheControl?.NoCache == true, $"{path} should send Cache-Control: no-cache");
            }
            // The API under it is not the app: signed out it answers 401, and an unknown route 404.
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/teach/me")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/teach/nothing-here")).StatusCode);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
