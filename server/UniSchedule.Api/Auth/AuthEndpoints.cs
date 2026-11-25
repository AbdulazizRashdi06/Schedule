using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Teachers;

namespace UniSchedule.Api.Auth;

public static class AuthEndpoints
{
    public const string RequestCodeLimit = "auth-request-code";
    public const string VerifyLimit = "auth-verify";
    /// <summary>Per IP, lower than the student cap: each request sends mail to a guessable staff mailbox.</summary>
    public const string TeacherRequestCodeLimit = "auth-teacher-request-code";

    public record RequestCodeBody(string? UniversityId);
    /// <param name="Endpoint">This device's notification endpoint, if it has one: it belongs to whoever just signed in.</param>
    public record VerifyBody(string? UniversityId, string? Code, string? Endpoint = null);
    public record TeacherRequestCodeBody(string? Email);
    public record TeacherVerifyBody(string? Email, string? Code, string? Endpoint = null);
    public record AdminLoginBody(string? Username, string? Password);
    public record ChangePasswordBody(string? CurrentPassword, string? NewPassword);
    public record LogoutBody(string? Endpoint);

    /// <summary>The notification endpoint a sign-out names, if any. The body is optional, and a bad one never stops the sign-out.</summary>
    static async Task<string?> LogoutEndpoint(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.HasJsonContentType()) return null;
        try
        {
            return (await http.Request.ReadFromJsonAsync<LogoutBody>(ct))?.Endpoint is { Length: > 0 } e ? e : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    static readonly PasswordHasher<AdminUser> Hasher = new();
    public static string HashPassword(AdminUser user, string password) => Hasher.HashPassword(user, password);

    /// <summary>Checked against when the username doesn't exist, so that answer takes as long as a real one.</summary>
    static readonly string DummyHash = Hasher.HashPassword(null!, "no-such-account-" + Guid.NewGuid());

    static readonly TimeSpan AdminLock = TimeSpan.FromMinutes(15);
    const int AdminTriesPerClient = 5;
    /// <summary>Wrong passwords on one account from anywhere in 15 minutes before the account itself locks for 15 minutes.</summary>
    public const int AdminTriesPerAccount = 20;

    /// <summary>Dashboard password tries kept in memory (one per app).</summary>
    public sealed class AdminLoginTries
    {
        /// <summary>Per username and client: five in 15 minutes lock that pair out for 15 minutes.</summary>
        public TryTable PerClient { get; } = new(AdminTriesPerClient, AdminLock);
        /// <summary>The account lock for usernames that don't exist, so they answer just like real ones.</summary>
        public TryTable UnknownAccounts { get; } = new(AdminTriesPerAccount, AdminLock);
    }

    // No real username or password is anywhere near this long (usernames are 3-32 characters).
    const int MaxUsernameLength = 64, MaxPasswordLength = 256;
    public const int MaxAuthBody = 4096;
    const string WrongPassword = "Wrong username or password.", TooManyPasswords = "Too many wrong passwords. Try again in 15 minutes.";
    const string TooManyPasswordsAccount = "Too many wrong passwords for this account. Try again in 15 minutes, or sign in on a browser you've used before.";

    /// <summary>How a dashboard account is listed in the trusted-browser cookie (usernames have no colons, IDs neither).</summary>
    static string AdminDeviceKey(string username) => "admin:" + username;

    /// <summary>Owner IDs listed in config (Owners:Ids, comma-separated) are always owners.</summary>
    public static bool IsConfiguredOwner(IConfiguration config, string universityId) =>
        (config["Owners:Ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StudentIds.Normalize).Contains(universityId);

    /// <summary>The rules the change-password screen checks as you type: 12+ characters, a number, not the username.</summary>
    public static string? PasswordProblem(string password, string username)
    {
        if (password.Length < 12) return "Use at least 12 characters.";
        // Sign-in refuses anything longer, so a longer one could never be used.
        if (password.Length > MaxPasswordLength) return $"Use at most {MaxPasswordLength} characters.";
        if (!password.Any(char.IsAsciiDigit)) return "Add at least one number.";
        if (password.Contains(username, StringComparison.OrdinalIgnoreCase)) return "Don't put your username in your password.";
        return null;
    }

    public static void MapAuth(this RouteGroupBuilder api)
    {
        // Every sign-in body is a few short fields: a bigger one is refused before it's read.
        var auth = api.MapGroup("/auth").WithMetadata(new RequestSizeLimitAttribute(MaxAuthBody));

        auth.MapPost("/request-code", async (RequestCodeBody body, AppDb db, IEmailSender email, TimeProvider clock, IConfiguration config, HttpContext http, CancellationToken ct) =>
        {
            var id = StudentIds.Normalize(body.UniversityId);
            if (id is null) return Errors.BadRequest("Enter your university ID like 24-0161.");

            var (login, code, error) = await CodeSignIn.RequestAsync(db, http, clock, id, "ID", SignInCookies.IsTrustedDevice(http, id), ct);
            if (error is not null) return error;
            var address = StudentIds.EmailFor(id);
            try
            {
                await email.SendSignInCodeAsync(address, code!, ct);
            }
            catch (Exception e) when (e is EmailNotSentException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                db.LoginCodes.Remove(login!);
                await db.SaveChangesAsync(CancellationToken.None);
                return Errors.Unavailable("We couldn't send the email just now. Try again in a minute.");
            }
            // The new code replaces this browser's earlier ones (they still count towards the limits).
            await CodeSignIn.RetireOthersAsync(db, login!, id, login!.CreatedAt);

            // Test mode (Auth:TestMode) shows the code in the app while real email isn't set up.
            // Never for owners (their accounts can see every student), batch reps (they publish onto other students'
            // timetables) or students with teaching rights (they can change classes).
            var isOwner = IsConfiguredOwner(config, id) || await db.Students.AnyAsync(s => s.UniversityId == id && s.IsOwner, ct);
            var isRep = await db.BatchRepAssignments.AnyAsync(r => db.Students.Any(s => s.Id == r.StudentId && s.UniversityId == id), ct);
            var teaches = await db.Teachers.AnyAsync(t => db.Students.Any(s => s.Id == t.StudentId && s.UniversityId == id), ct);
            var showCode = config.GetValue<bool>("Auth:TestMode") && !isOwner && !isRep && !teaches;
            return Results.Ok(new { email = address, resendAfterSeconds = (int)LoginCodes.ResendAfter.TotalSeconds, testCode = showCode ? code : null });
        }).RequireRateLimiting(RequestCodeLimit);

        auth.MapPost("/verify", async (VerifyBody body, AppDb db, TimeProvider clock, HttpContext http, IConfiguration config, WrongCodeLimiter wrongCodes, CancellationToken ct) =>
        {
            var id = StudentIds.Normalize(body.UniversityId);
            var code = body.Code?.Trim();
            if (id is null || !LoginCodes.IsWellFormed(code)) return Errors.BadRequest("Enter the 6-digit code from your email.");
            // The per-address cap is for guessing at other people's IDs, so it skips browsers this ID has signed in on.
            var trusted = SignInCookies.IsTrustedDevice(http, id);
            if (!trusted && wrongCodes.IsBlocked(http))
                return Errors.TooMany("Too many wrong codes from this network. Try again in an hour, or sign in on a device you've used before.");

            var (error, wrongCode) = await CodeSignIn.VerifyAsync(db, http, clock, id, code, "ID", LoginCodes.MaxAttempts, trusted, ct);
            if (wrongCode && !trusted) wrongCodes.Record(http);
            if (error is not null) return error;

            var now = clock.GetUtcNow();
            var student = await db.Students.FirstOrDefaultAsync(s => s.UniversityId == id, ct);
            var isNew = student is null;
            if (student is null)
            {
                student = new Student { UniversityId = id, Email = StudentIds.EmailFor(id), CreatedAt = now };
                db.Students.Add(student);
            }
            if (IsConfiguredOwner(config, id)) student.IsOwner = true;
            student.LastSeenAt = now;
            await db.SaveChangesAsync(ct);

            // The device belongs to whoever just signed in: a teacher who used it before stops getting notifications on it.
            if (body.Endpoint is { Length: > 0 } endpoint)
                await db.TeacherPushSubscriptions.Where(x => x.Endpoint == endpoint).ExecuteDeleteAsync(ct);

            await Session.SignInStudentAsync(http, student, now);
            SignInCookies.RememberDevice(http, id);
            return Results.Ok(new { isNew });
        }).RequireRateLimiting(VerifyLimit);

        // ---- Teachers: a code to a staff address (@gutech.edu.om exactly). The answer never holds the code, in any mode. ----
        auth.MapPost("/teacher/request-code", async (TeacherRequestCodeBody body, AppDb db, IEmailSender sender, TimeProvider clock, HttpContext http, TeacherCodeLimiter limiter, CancellationToken ct) =>
        {
            var email = TeacherEmails.Normalize(body.Email);
            if (email is null)
                return Errors.BadRequest(TeacherEmails.IsStudentAddress(body.Email)
                    ? "That's a student address. Students sign in with their university ID."
                    : "Enter your GUtech staff email, like firstname.lastname@gutech.edu.om.");

            var trusted = SignInCookies.IsTrustedDevice(http, email) && !limiter.TrustSpent(email);
            var (login, code, error) = await CodeSignIn.RequestAsync(db, http, clock, email, "email", trusted, ct);
            if (error is not null) return error;
            // Known, unknown and blocked addresses get the same answer, so nobody can test which staff addresses were
            // removed from Classy. A blocked one goes through the same limits, but its stored code matches nothing and
            // the email says it can't sign in: the blocked person is told, in their own mailbox only.
            var blocked = await db.BlockedEmails.AnyAsync(b => b.Email == email, ct);
            if (blocked)
            {
                login!.CodeHash = CodeSignIn.UnmatchableHash();
                await db.SaveChangesAsync(ct);
            }
            try
            {
                if (blocked) await sender.SendCannotSignInAsync(email, ct);
                else await sender.SendSignInCodeAsync(email, code!, ct);
            }
            catch (Exception e) when (e is EmailNotSentException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                db.LoginCodes.Remove(login!);
                await db.SaveChangesAsync(CancellationToken.None);
                return Errors.Unavailable("We couldn't send the email just now. Try again in a minute.");
            }
            await CodeSignIn.RetireOthersAsync(db, login!, email, login!.CreatedAt);
            return Results.Ok(new { email, resendAfterSeconds = (int)LoginCodes.ResendAfter.TotalSeconds });
        }).RequireRateLimiting(TeacherRequestCodeLimit);

        auth.MapPost("/teacher/verify", async (TeacherVerifyBody body, AppDb db, TimeProvider clock, HttpContext http, TeacherCodeLimiter limiter, CancellationToken ct) =>
        {
            var email = TeacherEmails.Normalize(body.Email);
            var code = body.Code?.Trim();
            if (email is null || !LoginCodes.IsWellFormed(code)) return Errors.BadRequest("Enter the 6-digit code from your email.");
            // A made-up address is a new key every time, so wrong codes for addresses with no account are counted
            // together. Past the limit, first-time sign-ins wait; teachers who already have an account are not affected.
            var hasAccount = await db.Teachers.AnyAsync(t => t.Email == email, ct);
            // A browser this email signed in on isn't held to the per-address cap, until it has had 30 wrong codes in a
            // day itself: a lecture-room PC that once signed a teacher in can't guess without limit. The try is
            // counted before its code is checked, so tries sent together can't overshoot the caps.
            var (slot, refused) = limiter.Reserve(http, email, SignInCookies.IsTrustedDevice(http, email), hasAccount);
            if (slot is null)
                return Errors.TooMany(refused == TeacherCodeLimiter.Refusal.Network
                    ? "Too many wrong codes from this network. Try again in an hour, or sign in on a device you've used before."
                    : "New teacher sign-ins are paused for an hour. Try again later.");

            IResult? error;
            bool wrongCode;
            try
            {
                (error, wrongCode) = await CodeSignIn.VerifyAsync(db, http, clock, email, code, "email", CodeSignIn.EmailMaxAttempts, slot.Trusted, ct);
            }
            catch
            {
                limiter.Release(slot);
                throw;
            }
            var now = clock.GetUtcNow();
            if (!wrongCode) limiter.Release(slot);
            else if (limiter.Confirm(slot))
            {
                // Staff are told once when the pause starts (pushed to owners' devices, and shown on the dashboard).
                db.TeachNotices.Add(new TeachNotice
                {
                    Audience = TeachNotice.Staff, Kind = "sign-in-guessing", CreatedAt = now, ExpiresAt = now.AddHours(1),
                    Title = "Teacher sign-ins paused",
                    Body = "Many wrong teacher sign-in codes. New teacher sign-ins are paused for an hour.",
                    Url = "/admin/teachers", Tag = "sign-in-guessing",
                });
                await db.SaveChangesAsync(CancellationToken.None);
            }
            if (error is not null) return error;
            // A code asked for just before the address was blocked signs nobody in.
            if (await db.BlockedEmails.AnyAsync(b => b.Email == email, ct))
                return Errors.BadRequest("That code has expired. Send a new one.");

            var teacher = await db.Teachers.FirstOrDefaultAsync(t => t.Email == email && t.StudentId == null, ct);
            var isNew = teacher is null;
            if (teacher is null)
            {
                teacher = new Teacher { Email = email, CreatedAt = now, LastSeenAt = now };
                db.Teachers.Add(teacher);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Another browser finished signing this address in for the first time in the same moment.
                    db.ChangeTracker.Clear();
                    teacher = await db.Teachers.FirstAsync(t => t.Email == email && t.StudentId == null, ct);
                    isNew = false;
                }
            }
            teacher.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
            // Links the account to its timetable name when the email clearly matches exactly one.
            await TeacherNames.LinkOnSignInAsync(db, teacher, now, ct);

            // The device belongs to whoever just signed in.
            if (body.Endpoint is { Length: > 0 } endpoint)
            {
                await db.PushSubscriptions.Where(x => x.Endpoint == endpoint).ExecuteDeleteAsync(ct);
                await db.TeacherPushSubscriptions.Where(x => x.Endpoint == endpoint && x.TeacherId != teacher.Id).ExecuteDeleteAsync(ct);
            }

            await Session.SignInTeacherAsync(http, teacher, now);
            SignInCookies.RememberDevice(http, email);
            var names = await db.TeacherNames.Where(n => n.TeacherId == teacher.Id).ToListAsync(ct);
            return Results.Ok(new { isNew, status = TeacherNames.Status(teacher, names) });
        }).RequireRateLimiting(VerifyLimit);

        auth.MapPost("/admin-login", async (AdminLoginBody body, AppDb db, TimeProvider clock, HttpContext http, AdminLoginTries tries, CancellationToken ct) =>
        {
            // Refused before anything is stored or hashed, so huge made-up usernames can't fill memory.
            if (body.Username?.Length > MaxUsernameLength || body.Password?.Length > MaxPasswordLength)
                return Errors.Unauthorized(WrongPassword);
            var username = body.Username?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(body.Password))
                return Errors.BadRequest("Enter your username and password.");
            var now = clock.GetUtcNow();
            // A browser that has signed in to this account before is counted on its own, not by address, so someone on
            // the same network (all of campus Wi-Fi shares one IP) can't lock the owner out of it.
            var trusted = SignInCookies.IsTrustedDevice(http, AdminDeviceKey(username));
            // The try is taken before the slow password check, so tries sent together can't all slip past the limit.
            var client = trusted ? $"{username}|trusted" : $"{username}|{ClientAddress.Key(http)}";
            if (!tries.PerClient.TryAdd(client, now)) return Errors.TooMany(TooManyPasswords);

            // Usernames that don't exist go through the same steps (an account lock kept in memory and a dummy hash),
            // so no answer or timing says which usernames are real.
            var user = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username, ct);
            var locked = user is null ? tries.UnknownAccounts.IsLocked(username, now) : user.LockedUntil > now;
            // While the account is locked, browsers that never signed in to it are refused even the right password, so
            // the lock caps guessing from every address together. The owner's own browsers still get in.
            if (locked && !trusted) return Errors.TooMany(TooManyPasswordsAccount);
            var result = Hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, body.Password);
            if (user is null || result == PasswordVerificationResult.Failed)
            {
                // Wrong tries while the lock is on aren't counted.
                if (locked) return Errors.TooMany(TooManyPasswordsAccount);
                if (user is null)
                {
                    tries.UnknownAccounts.TryAdd(username, now);
                    return Errors.Unauthorized(WrongPassword);
                }
                var windowStart = now - AdminLock;
                // Counted in one statement, so wrong passwords sent together are all counted.
                var counted = await db.AdminUsers.Where(a => a.Id == user.Id && (a.LockedUntil == null || a.LockedUntil <= now))
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(a => a.FailedLogins, a => a.FailedSince == null || a.FailedSince <= windowStart ? 1 : a.FailedLogins + 1)
                        .SetProperty(a => a.FailedSince, a => a.FailedSince == null || a.FailedSince <= windowStart ? now : a.FailedSince), ct);
                if (counted == 0) return Errors.TooMany(TooManyPasswordsAccount);
                await db.AdminUsers.Where(a => a.Id == user.Id && a.FailedLogins >= AdminTriesPerAccount)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(a => a.LockedUntil, now + AdminLock)
                        .SetProperty(a => a.FailedLogins, 0)
                        .SetProperty(a => a.FailedSince, (DateTimeOffset?)null), ct);
                return Errors.Unauthorized(WrongPassword);
            }
            if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, body.Password);
            tries.PerClient.Clear(client);
            user.FailedLogins = 0;
            user.FailedSince = null;
            user.LockedUntil = null;
            user.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
            await Session.SignInAdminAsync(http, user);
            SignInCookies.RememberDevice(http, AdminDeviceKey(username));
            return Results.Ok(new { mustChangePassword = user.MustChangePassword });
        }).RequireRateLimiting(VerifyLimit);

        auth.MapPost("/change-password", async (ChangePasswordBody body, AppDb db, HttpContext http, CancellationToken ct) =>
        {
            if (http.User.Kind() != "admin") return Errors.Forbidden("Only dashboard accounts have a password.");
            var user = await db.AdminUsers.FindAsync([http.User.Id()], ct);
            if (user is null) return Errors.Unauthorized("Sign in again.");
            if (Hasher.VerifyHashedPassword(user, user.PasswordHash, body.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
                return Errors.BadRequest("Your current password is wrong.");
            var pw = body.NewPassword ?? "";
            var problem = PasswordProblem(pw, user.Username);
            if (problem is not null) return Errors.BadRequest(problem);
            if (pw == body.CurrentPassword) return Errors.BadRequest("Pick a different password from the temporary one.");
            user.PasswordHash = Hasher.HashPassword(user, pw);
            user.MustChangePassword = false;
            // Signs out every other place this account is signed in; this one gets a fresh cookie and stays.
            user.SessionVersion++;
            await db.SaveChangesAsync(ct);
            await Session.SignInAdminAsync(http, user);
            return Results.NoContent();
        }).RequireAuthorization();

        // Leaves the trusted-device cookie: signing out doesn't make this a stranger's browser.
        // A student's device can send its notification endpoint, so it stops getting their notifications in the same
        // step: if the sign-out doesn't get through, the device keeps them along with the sign-in. The same for a
        // teacher's device (a teaching assistant's devices are their student devices).
        auth.MapPost("/logout", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            if ((http.User.IsStudent() || http.User.IsTeacher()) && await LogoutEndpoint(http, ct) is { } endpoint)
            {
                var id = http.User.Id();
                if (http.User.IsStudent())
                    await db.PushSubscriptions.Where(x => x.StudentId == id && x.Endpoint == endpoint).ExecuteDeleteAsync(ct);
                else
                    await db.TeacherPushSubscriptions.Where(x => x.TeacherId == id && x.Endpoint == endpoint).ExecuteDeleteAsync(ct);
            }
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // Who is signed in: the app uses this to route between student, teacher and dashboard screens.
        // One branch per kind, with no fall-through: a kind this doesn't know is nobody.
        auth.MapGet("/session", async (HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (http.User.Identity?.IsAuthenticated != true) return Results.Ok(new { kind = (string?)null });
            var id = http.User.Id();
            var now = clock.GetUtcNow();
            if (http.User.IsStudent())
            {
                var s = await db.Students.Where(x => x.Id == id).Select(x => new { x.UniversityId }).FirstAsync(ct);
                // A student with teaching rights: the code that lets them change a class must be newer than the grant.
                var teachingSince = await db.Teachers.Where(t => t.StudentId == id).Select(t => (DateTimeOffset?)t.CreatedAt).FirstOrDefaultAsync(ct);
                return Results.Ok(new { kind = "student", name = s.UniversityId, role = http.User.Role(), mustChangePassword = false,
                    batchRep = await UniSchedule.Api.Planner.PlannerEndpoints.RepInfo(db, id, ct),
                    teaching = teachingSince is not null, codeFresh = http.User.CodeIsFresh(now, teachingSince) });
            }
            if (http.User.IsTeacher())
            {
                var t = await db.Teachers.FirstAsync(x => x.Id == id, ct);
                var names = await db.TeacherNames.Where(n => n.TeacherId == id).ToListAsync(ct);
                return Results.Ok(new { kind = "teacher", name = t.Email, role = "teacher", mustChangePassword = false,
                    teaching = true, codeFresh = http.User.CodeIsFresh(now),
                    teacher = new { id = t.Id, displayName = await TeacherNames.ShownAsync(db, t, names, ct) } });
            }
            if (http.User.IsAdmin())
            {
                var a = await db.AdminUsers.Where(x => x.Id == id).Select(x => new { x.Username, x.MustChangePassword }).FirstAsync(ct);
                return Results.Ok(new { kind = "admin", name = a.Username, role = http.User.Role(), mustChangePassword = a.MustChangePassword });
            }
            return Results.Ok(new { kind = (string?)null });
        });
    }
}
