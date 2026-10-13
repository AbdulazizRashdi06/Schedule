using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Auth;

/// <summary>
/// One cookie for both kinds of sign-in. The cookie only names who you are ("student" or "admin" + id);
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

    public static Task SignInStudentAsync(HttpContext http, Student s) => SignIn(http, "student", s.Id);
    public static Task SignInAdminAsync(HttpContext http, AdminUser a) => SignIn(http, "admin", a.Id);

    private static Task SignIn(HttpContext http, string kind, int id)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(KindClaim, kind)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    public static int Id(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("not signed in"));

    public static string? Kind(this ClaimsPrincipal user) => user.FindFirstValue(KindClaim);
    public static bool IsStudent(this ClaimsPrincipal user) => user.Kind() == "student";
    public static string? Role(this ClaimsPrincipal user) => user.FindFirstValue(RoleClaim);
    public static bool IsOwner(this ClaimsPrincipal user) => user.Role() == "owner";

    /// <summary>Checks the cookie's account still exists and attaches its current role.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
        var principal = ctx.Principal!;
        var id = principal.Id();
        var identity = (ClaimsIdentity)principal.Identity!;
        if (principal.Kind() == "student")
        {
            var s = await db.Students.Where(x => x.Id == id).Select(x => new { x.IsOwner }).FirstOrDefaultAsync();
            if (s is null) { await Reject(ctx); return; }
            if (s.IsOwner) identity.AddClaim(new Claim(RoleClaim, "owner"));
        }
        else if (principal.Kind() == "admin")
        {
            var a = await db.AdminUsers.Where(x => x.Id == id).Select(x => new { x.Role, x.MustChangePassword }).FirstOrDefaultAsync();
            if (a is null) { await Reject(ctx); return; }
            identity.AddClaim(new Claim(RoleClaim, a.Role == AdminRole.Owner ? "owner" : "rsa"));
            if (a.MustChangePassword) identity.AddClaim(new Claim(MustChangeClaim, "1"));
        }
        else await Reject(ctx);
    }

    private static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
