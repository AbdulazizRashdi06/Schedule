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

    /// <summary>Owner IDs listed in config (Owners:Ids, comma-separated) are always owners.</summary>
    public static bool IsConfiguredOwner(IConfiguration config, string universityId) =>
        (config["Owners:Ids"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StudentIds.Normalize).Contains(universityId);

    public static void MapAuth(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth");

        auth.MapPost("/request-code", async (RequestCodeBody body, AppDb db, IEmailSender email, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            var id = StudentIds.Normalize(body.UniversityId);
            if (id is null) return Errors.BadRequest("Enter your university ID like 24-0161.");

            var now = clock.GetUtcNow();
            var recent = await db.LoginCodes.Where(c => c.UniversityId == id && c.CreatedAt > now - LoginCodes.Window)
                .OrderByDescending(c => c.CreatedAt).Select(c => c.CreatedAt).ToListAsync(ct);
            if (recent.Count > 0 && now - recent[0] < LoginCodes.ResendAfter)
            {
                var wait = (int)Math.Ceiling((LoginCodes.ResendAfter - (now - recent[0])).TotalSeconds);
                return Errors.TooMany($"Wait {wait} seconds before asking for a new code.");
            }
            if (recent.Count >= LoginCodes.MaxPerWindow)
                return Errors.TooMany("Too many codes for this ID. Wait 15 minutes and try again.");

            var code = LoginCodes.Generate();
            var login = new LoginCode
            {
                UniversityId = id,
                CodeHash = LoginCodes.Hash(id, code),
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

            // Test mode (Auth:TestMode) shows the code in the app while real email isn't set up.
            // Never for owners: their accounts can see every student.
            var isOwner = IsConfiguredOwner(config, id) || await db.Students.AnyAsync(s => s.UniversityId == id && s.IsOwner, ct);
            var showCode = config.GetValue<bool>("Auth:TestMode") && !isOwner;
            return Results.Ok(new { email = address, resendAfterSeconds = (int)LoginCodes.ResendAfter.TotalSeconds, testCode = showCode ? code : null });
        }).RequireRateLimiting(RequestCodeLimit);

        auth.MapPost("/verify", async (VerifyBody body, AppDb db, TimeProvider clock, HttpContext http, IConfiguration config, CancellationToken ct) =>
        {
            var id = StudentIds.Normalize(body.UniversityId);
            var code = body.Code?.Trim();
            if (id is null || !LoginCodes.IsWellFormed(code)) return Errors.BadRequest("Enter the 6-digit code from your email.");

            var now = clock.GetUtcNow();
            // Only the newest live code counts; asking for a new one retires the old.
            var login = await db.LoginCodes
                .Where(c => c.UniversityId == id && c.UsedAt == null)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (login is null || login.ExpiresAt <= now)
                return Errors.BadRequest("That code has expired. Send a new one.");
            if (login.Attempts >= LoginCodes.MaxAttempts)
                return Errors.BadRequest("Too many wrong tries. Send a new code.");

            login.Attempts++;
            if (!LoginCodes.Verify(login.CodeHash, id, code!))
            {
                await db.SaveChangesAsync(ct);
                var left = LoginCodes.MaxAttempts - login.Attempts;
                return Errors.BadRequest(left > 0 ? $"Wrong code. {left} {(left == 1 ? "try" : "tries")} left." : "Wrong code. Send a new one.");
            }

            login.UsedAt = now;
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
            return Results.Ok(new { isNew });
        }).RequireRateLimiting(VerifyLimit);

        auth.MapPost("/admin-login", async (AdminLoginBody body, AppDb db, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            var username = body.Username?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(body.Password))
                return Errors.BadRequest("Enter your username and password.");
            var now = clock.GetUtcNow();
            var user = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username, ct);
            if (user is null) return Errors.Unauthorized("Wrong username or password.");
            if (user.LockedUntil > now) return Errors.TooMany("Too many wrong passwords. Try again in 15 minutes.");

            var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, body.Password);
            if (result == PasswordVerificationResult.Failed)
            {
                user.FailedLogins++;
                if (user.FailedLogins >= 5) { user.LockedUntil = now.AddMinutes(15); user.FailedLogins = 0; }
                await db.SaveChangesAsync(ct);
                return Errors.Unauthorized("Wrong username or password.");
            }
            if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, body.Password);
            user.FailedLogins = 0;
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
            if (pw.Length < 10) return Errors.BadRequest("Use at least 10 characters.");
            if (pw == body.CurrentPassword) return Errors.BadRequest("Pick a different password from the temporary one.");
            user.PasswordHash = Hasher.HashPassword(user, pw);
            user.MustChangePassword = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

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
                return Results.Ok(new { kind = "student", name = s.UniversityId, role = http.User.Role(), mustChangePassword = false });
            }
            var a = await db.AdminUsers.Where(x => x.Id == id).Select(x => new { x.Username, x.MustChangePassword }).FirstAsync(ct);
            return Results.Ok(new { kind = "admin", name = a.Username, role = http.User.Role(), mustChangePassword = a.MustChangePassword });
        });
    }
}
