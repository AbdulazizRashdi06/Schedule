using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Auth;

public static class AuthEndpoints
{
    public const string RequestCodeLimit = "auth-request-code";
    public const string VerifyLimit = "auth-verify";

    public record RequestCodeBody(string? UniversityId);
    public record VerifyBody(string? UniversityId, string? Code);
    public record AdminLoginBody(string? Username, string? Password);
    public record ChangePasswordBody(string? CurrentPassword, string? NewPassword);

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
    const string WrongPassword = "Wrong username or password.", TooManyPasswords = "Too many wrong passwords. Try again in 15 minutes.";

    /// <summary>Hours until enough of the oldest of these (oldest first, each weighing <c>weight</c>) are a day old to
    /// bring the total under <c>limit</c>.</summary>
    static int HoursUntilUnder<T>(List<T> rows, Func<T, DateTimeOffset> createdAt, Func<T, int> weight, int limit, DateTimeOffset now)
    {
        var over = rows.Sum(weight) - limit + 1;
        foreach (var r in rows)
        {
            over -= weight(r);
            if (over <= 0) return Math.Max(1, (int)Math.Ceiling((createdAt(r) + LoginCodes.Day - now).TotalHours));
        }
        return 24;
    }

    static string Hours(int n) => n == 1 ? "1 hour" : $"{n} hours";

    /// <summary>Owner IDs listed in config (Owners:Ids, comma-separated) are always owners.</summary>
    public static bool IsConfiguredOwner(IConfiguration config, string universityId) =>
        (config["Owners:Ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StudentIds.Normalize).Contains(universityId);

    /// <summary>The rules the change-password screen checks as you type: 12+ characters, a number, not the username.</summary>
    public static string? PasswordProblem(string password, string username)
    {
        if (password.Length < 12) return "Use at least 12 characters.";
        if (!password.Any(char.IsAsciiDigit)) return "Add at least one number.";
        if (password.Contains(username, StringComparison.OrdinalIgnoreCase)) return "Don't put your username in your password.";
        return null;
    }

    public static void MapAuth(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapPost("/request-code", async (RequestCodeBody body, AppDb db, IEmailSender email, TimeProvider clock, IConfiguration config, HttpContext http, CancellationToken ct) =>
        {
            var id = StudentIds.Normalize(body.UniversityId);
            if (id is null) return Errors.BadRequest("Enter your university ID like 24-0161.");

            var now = clock.GetUtcNow();
            var client = SignInCookies.LoginClient(http);
            // Codes older than every limit are no use to anyone.
            var dayAgo = now - LoginCodes.Day;
            await db.LoginCodes.Where(c => c.UniversityId == id && c.CreatedAt <= dayAgo).ExecuteDeleteAsync(ct);

            // The resend wait and the 15-minute cap are per browser, so someone else asking for codes can't use them up.
            var recent = await db.LoginCodes.Where(c => c.UniversityId == id && c.ClientHash == client && c.CreatedAt > now - LoginCodes.Window)
                .OrderByDescending(c => c.CreatedAt).Select(c => c.CreatedAt).ToListAsync(ct);
            if (recent.Count > 0 && now - recent[0] < LoginCodes.ResendAfter)
            {
                var wait = (int)Math.Ceiling((LoginCodes.ResendAfter - (now - recent[0])).TotalSeconds);
                return Errors.TooMany($"Wait {wait} seconds before asking for a new code.");
            }
            if (recent.Count >= LoginCodes.MaxPerWindow)
                return Errors.TooMany("Too many codes for this ID. Wait 15 minutes and try again.");

            if (!SignInCookies.IsTrustedDevice(http, id))
            {
                var day = await db.LoginCodes.Where(c => c.UniversityId == id).OrderBy(c => c.CreatedAt)
                    .Select(c => new { c.CreatedAt, Wrong = c.UsedAt == null ? c.Attempts : c.Attempts - 1 }).ToListAsync(ct);
                if (day.Count >= LoginCodes.MaxPerDay)
                    return Errors.TooMany($"This ID has had too many sign-in codes today. Try again in {Hours(HoursUntilUnder(day, d => d.CreatedAt, _ => 1, LoginCodes.MaxPerDay, now))}, or sign in on a device you've used before.");
                if (day.Sum(d => d.Wrong) >= LoginCodes.MaxWrongPerDay)
                    return Errors.TooMany($"Too many wrong codes for this ID today. Try again in {Hours(HoursUntilUnder(day, d => d.CreatedAt, d => d.Wrong, LoginCodes.MaxWrongPerDay, now))}, or sign in on a device you've used before.");
            }

            var code = LoginCodes.Generate();
            var login = new LoginCode
            {
                UniversityId = id,
                CodeHash = LoginCodes.Hash(id, code),
                ClientHash = client,
                CreatedAt = now,
                ExpiresAt = now + LoginCodes.Lifetime,
            };
            db.LoginCodes.Add(login);
            await db.SaveChangesAsync(ct);
            var address = StudentIds.EmailFor(id);
            try
            {
                await email.SendSignInCodeAsync(address, code, ct);
            }
            catch (Exception e) when (e is EmailNotSentException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                db.LoginCodes.Remove(login);
                await db.SaveChangesAsync(CancellationToken.None);
                return Errors.Unavailable("We couldn't send the email just now. Try again in a minute.");
            }
            // The new code replaces this browser's earlier ones (they still count towards the limits).
            await db.LoginCodes.Where(c => c.UniversityId == id && c.ClientHash == client && c.Id != login.Id && c.UsedAt == null && c.ExpiresAt > now)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresAt, now), CancellationToken.None);

            // Test mode (Auth:TestMode) shows the code in the app while real email isn't set up.
            // Never for owners (their accounts can see every student) or batch reps (they publish onto other students' timetables).
            var isOwner = IsConfiguredOwner(config, id) || await db.Students.AnyAsync(s => s.UniversityId == id && s.IsOwner, ct);
            var isRep = await db.BatchRepAssignments.AnyAsync(r => db.Students.Any(s => s.Id == r.StudentId && s.UniversityId == id), ct);
            var showCode = config.GetValue<bool>("Auth:TestMode") && !isOwner && !isRep;
            return Results.Ok(new { email = address, resendAfterSeconds = (int)LoginCodes.ResendAfter.TotalSeconds, testCode = showCode ? code : null });
        }).RequireRateLimiting(RequestCodeLimit);

        auth.MapPost("/verify", async (VerifyBody body, AppDb db, TimeProvider clock, HttpContext http, IConfiguration config, WrongCodeLimiter wrongCodes, CancellationToken ct) =>
        {
            var id = StudentIds.Normalize(body.UniversityId);
            var code = body.Code?.Trim();
            if (id is null || !LoginCodes.IsWellFormed(code)) return Errors.BadRequest("Enter the 6-digit code from your email.");
            if (wrongCodes.IsBlocked(http))
                return Errors.TooMany("Too many wrong codes from this network. Try again in an hour.");

            var now = clock.GetUtcNow();
            // A code only works in the browser that asked for it, and only this browser's newest code counts:
            // asking for a new one retires the old.
            var client = SignInCookies.ExistingLoginClient(http);
            if (client is null) return Errors.BadRequest("That code has expired. Send a new one.");
            var login = await db.LoginCodes
                .Where(c => c.UniversityId == id && c.ClientHash == client && c.UsedAt == null)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (login is null || login.ExpiresAt <= now)
                return Errors.BadRequest("That code has expired. Send a new one.");
            if (login.Attempts >= LoginCodes.MaxAttempts)
                return Errors.BadRequest("Too many wrong tries. Send a new code.");

            // Each try is claimed in the database before the code is checked, so guesses sent in parallel can't share a
            // try: once the five are used, every other request finds none left. Away from a browser this ID has signed
            // in on before, the claim also needs the ID to have had fewer than 30 wrong codes in the last day.
            var trusted = SignInCookies.IsTrustedDevice(http, id);
            var dayAgo = now - LoginCodes.Day;
            var claimed = await db.LoginCodes
                .Where(c => c.Id == login.Id && c.UsedAt == null && c.Attempts < LoginCodes.MaxAttempts)
                .Where(c => trusted || db.LoginCodes.Where(o => o.UniversityId == id && o.CreatedAt > dayAgo)
                    .Sum(o => o.UsedAt == null ? o.Attempts : o.Attempts - 1) < LoginCodes.MaxWrongPerDay)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Attempts, c => c.Attempts + 1), ct);
            if (claimed == 0)
            {
                var wrongToday = await db.LoginCodes.Where(o => o.UniversityId == id && o.CreatedAt > dayAgo)
                    .SumAsync(o => o.UsedAt == null ? o.Attempts : o.Attempts - 1, ct);
                return !trusted && wrongToday >= LoginCodes.MaxWrongPerDay
                    ? Errors.TooMany("Too many wrong codes for this ID today. Try again later, or sign in on a device you've used before.")
                    : Errors.BadRequest("Too many wrong tries. Send a new code.");
            }
            if (!LoginCodes.Verify(login.CodeHash, id, code!))
            {
                wrongCodes.Record(http);
                var tries = await db.LoginCodes.Where(c => c.Id == login.Id).Select(c => c.Attempts).FirstAsync(ct);
                var left = LoginCodes.MaxAttempts - tries;
                return Errors.BadRequest(left > 0 ? $"Wrong code. {left} {(left == 1 ? "try" : "tries")} left." : "Wrong code. Send a new one.");
            }

            // A code signs in once, even if two requests get here together.
            var won = await db.LoginCodes.Where(c => c.Id == login.Id && c.UsedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.UsedAt, now), ct);
            if (won == 0) return Errors.BadRequest("That code has expired. Send a new one.");
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

            await Session.SignInStudentAsync(http, student);
            SignInCookies.RememberDevice(http, id);
            return Results.Ok(new { isNew });
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
            // The try is taken before the slow password check, so tries sent together can't all slip past the limit.
            var client = $"{username}|{http.Connection.RemoteIpAddress}";
            if (!tries.PerClient.TryAdd(client, now)) return Errors.TooMany(TooManyPasswords);

            // Usernames that don't exist go through the same steps (a dummy hash and an account lock kept in memory),
            // so no answer or timing says which usernames are real.
            var user = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username, ct);
            var result = Hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, body.Password);
            if (user is null || result == PasswordVerificationResult.Failed)
            {
                // The account lock only refuses wrong passwords (the right one still signs in), so an outsider can't
                // keep the owner out. Wrong tries while it's on aren't counted.
                if (user is null)
                {
                    if (tries.UnknownAccounts.IsLocked(username, now)) return Errors.TooMany(TooManyPasswords);
                    tries.UnknownAccounts.TryAdd(username, now);
                    return Errors.Unauthorized(WrongPassword);
                }
                var windowStart = now - AdminLock;
                // Counted in one statement, so wrong passwords sent together are all counted.
                var counted = await db.AdminUsers.Where(a => a.Id == user.Id && (a.LockedUntil == null || a.LockedUntil <= now))
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(a => a.FailedLogins, a => a.FailedSince == null || a.FailedSince <= windowStart ? 1 : a.FailedLogins + 1)
                        .SetProperty(a => a.FailedSince, a => a.FailedSince == null || a.FailedSince <= windowStart ? now : a.FailedSince), ct);
                if (counted == 0) return Errors.TooMany(TooManyPasswords);
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
        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // Who is signed in: the app uses this to route between student and dashboard screens.
        auth.MapGet("/session", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            if (http.User.Identity?.IsAuthenticated != true) return Results.Ok(new { kind = (string?)null });
            var id = http.User.Id();
            if (http.User.IsStudent())
            {
                var s = await db.Students.Where(x => x.Id == id).Select(x => new { x.UniversityId }).FirstAsync(ct);
                return Results.Ok(new { kind = "student", name = s.UniversityId, role = http.User.Role(), mustChangePassword = false,
                    batchRep = await UniSchedule.Api.Planner.PlannerEndpoints.RepInfo(db, id, ct) });
            }
            var a = await db.AdminUsers.Where(x => x.Id == id).Select(x => new { x.Username, x.MustChangePassword }).FirstAsync(ct);
            return Results.Ok(new { kind = "admin", name = a.Username, role = http.User.Role(), mustChangePassword = a.MustChangePassword });
        });
    }
}
