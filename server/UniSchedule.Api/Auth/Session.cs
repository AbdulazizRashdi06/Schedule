using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Teachers;

namespace UniSchedule.Api.Auth;

/// <summary>
/// One cookie for every kind of sign-in. The cookie only names who you are ("student", "teacher" or "admin" + id);
/// roles are read from the database on every request, so removing someone takes effect at once.
/// </summary>
public static class Session
{
    public const string KindClaim = "kind";
    public const string RoleClaim = "app-role";
    public const string MustChangeClaim = "must-change-password";

    public const string StudentPolicy = "student";
    /// <summary>Owners and RSA (dashboard access).</summary>
    public const string StaffPolicy = "staff";
    public const string OwnerPolicy = "owner";
    public const string RepPolicy = "batch-rep";
    /// <summary>Anyone signed in who doesn't still have to change a temporary password.</summary>
    public const string SignedInPolicy = "signed-in";

    /// <summary>The account's session version (<see cref="AdminUser.SessionVersion"/>, <see cref="Teacher.SessionVersion"/>)
    /// when the cookie was made. Missing = 0.</summary>
    public const string VersionClaim = "session-version";

    /// <summary>When this browser last entered an emailed code (unix seconds). Students and teachers only; cookies
    /// from before it existed have none and count as stale.</summary>
    public const string CodeAtClaim = "code-at";

    /// <summary>Changing a class asks for a new emailed code when the last one is older than this.</summary>
    public static readonly TimeSpan CodeFreshFor = TimeSpan.FromDays(7);

    /// <summary><paramref name="codeAt"/> is when the code was entered (now, at sign-in).</summary>
    public static Task SignInStudentAsync(HttpContext http, Student s, DateTimeOffset codeAt) => SignIn(http, "student", s.Id, CodeClaim(codeAt));
    /// <summary>Signs a staff account in. <paramref name="codeAt"/> is when the code was entered: now at sign-in, the
    /// cookie's own time when the cookie is only re-issued.</summary>
    public static Task SignInTeacherAsync(HttpContext http, Teacher t, DateTimeOffset codeAt) =>
        SignIn(http, "teacher", t.Id, CodeClaim(codeAt), VersionClaimOf(t.SessionVersion));
    public static Task SignInAdminAsync(HttpContext http, AdminUser a) => SignIn(http, "admin", a.Id, VersionClaimOf(a.SessionVersion));

    static Claim VersionClaimOf(int version) => new(VersionClaim, version.ToString(CultureInfo.InvariantCulture));
    static Claim CodeClaim(DateTimeOffset at) => new(CodeAtClaim, at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

    private static Task SignIn(HttpContext http, string kind, int id, params Claim[] extra)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(KindClaim, kind), .. extra],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    public static int Id(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("not signed in"));

    public static string? Kind(this ClaimsPrincipal user) => user.FindFirstValue(KindClaim);
    public static bool IsStudent(this ClaimsPrincipal user) => user.Kind() == "student";
    /// <summary>A staff account signed in by email. A student with teaching rights is still a student here.</summary>
    public static bool IsTeacher(this ClaimsPrincipal user) => user.Kind() == "teacher";
    /// <summary>A dashboard account (username and password).</summary>
    public static bool IsAdmin(this ClaimsPrincipal user) => user.Kind() == "admin";
    public static string? Role(this ClaimsPrincipal user) => user.FindFirstValue(RoleClaim);
    public static bool IsOwner(this ClaimsPrincipal user) => user.Role() == "owner";

    /// <summary>When this browser last entered an emailed code, or null (an old cookie, or a dashboard account).</summary>
    public static DateTimeOffset? CodeAt(this ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(CodeAtClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    /// <summary>
    /// Whether this browser's last emailed code is new enough to change a class: at most 7 days old and, when
    /// <paramref name="since"/> is given (the moment a student was given teaching rights), not from before it.
    /// </summary>
    public static bool CodeIsFresh(this ClaimsPrincipal user, DateTimeOffset now, DateTimeOffset? since = null) =>
        user.CodeAt() is DateTimeOffset at && now - at <= CodeFreshFor
            && (since is null || at.ToUnixTimeSeconds() >= since.Value.ToUnixTimeSeconds());

    /// <summary>"Last seen" is written at most this often, so active counts stay current without a write per request.</summary>
    public static readonly TimeSpan SeenEvery = TimeSpan.FromMinutes(1);

    /// <summary>Checks the cookie's account still exists, attaches its current role, and keeps "last seen" up to date.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
        var now = ctx.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var stale = now - SeenEvery;
        var principal = ctx.Principal!;
        var id = principal.Id();
        var identity = (ClaimsIdentity)principal.Identity!;
        if (principal.Kind() == "student")
        {
            var s = await db.Students.Where(x => x.Id == id).Select(x => new { x.IsOwner, x.LastSeenAt, Rep = db.BatchRepAssignments.Any(r => r.StudentId == x.Id) }).FirstOrDefaultAsync();
            if (s is null) { await Reject(ctx); return; }
            if (s.IsOwner) identity.AddClaim(new Claim(RoleClaim, "owner"));
            else if (s.Rep) identity.AddClaim(new Claim(RoleClaim, "batch-rep"));
            if (s.LastSeenAt is null || s.LastSeenAt < stale)
                await db.Students.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, now));
        }
        else if (principal.Kind() == "admin")
        {
            var a = await db.AdminUsers.Where(x => x.Id == id).Select(x => new { x.Role, x.MustChangePassword, x.LastSeenAt, x.SessionVersion }).FirstOrDefaultAsync();
            if (a is null) { await Reject(ctx); return; }
            // A password change or reset since this cookie was made signs it out. Cookies from before versions existed count as 0.
            if (VersionOf(principal) != a.SessionVersion) { await Reject(ctx); return; }
            if (a.LastSeenAt is null || a.LastSeenAt < stale)
                await db.AdminUsers.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, now));
            identity.AddClaim(new Claim(RoleClaim, a.Role == AdminRole.Owner ? "owner" : "rsa"));
            if (a.MustChangePassword) identity.AddClaim(new Claim(MustChangeClaim, "1"));
        }
        else if (principal.Kind() == "teacher")
        {
            // Staff accounts only: a teacher cookie can never reach a student's teaching profile. A removed teacher is
            // signed out here on their next request.
            var t = await db.Teachers.Where(x => x.Id == id && x.StudentId == null).Select(x => new { x.LastSeenAt, x.SessionVersion }).FirstOrDefaultAsync();
            if (t is null) { await Reject(ctx); return; }
            // "Sign out everywhere" (by the teacher or by staff) since this cookie was made signs it out.
            if (VersionOf(principal) != t.SessionVersion) { await Reject(ctx); return; }
            // Its own role: never owner, rsa or batch-rep, so no dashboard or planner policy lets it in.
            identity.AddClaim(new Claim(RoleClaim, "teacher"));
            if (t.LastSeenAt is null || t.LastSeenAt < stale)
                await db.Teachers.Where(x => x.Id == id).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastSeenAt, now));
        }
        else await Reject(ctx);
    }

    static int VersionOf(ClaimsPrincipal principal) =>
        int.TryParse(principal.FindFirstValue(VersionClaim), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
