using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace UniSchedule.Api.Auth;

/// <summary>
/// Two small cookies for sign-in, both only sent to /api/auth.
/// "unischedule-login" names this browser, so a code only works in the browser that asked for it.
/// "unischedule-device" lists the IDs (and dashboard accounts, as "admin:username") that have signed in on this
/// browser (up to five), so the per-ID daily limits, the per-address wrong-code cap and the dashboard account lock
/// can't shut that browser out. Signing out keeps it: it isn't a sign-in.
/// </summary>
public static class SignInCookies
{
    public const string LoginCookie = "unischedule-login";
    public const string DeviceCookie = "unischedule-device";
    const string CookiePath = "/api/auth";
    const int DeviceIdsMax = 5;
    const string DevicePurpose = "UniSchedule.TrustedDevice";

    /// <summary>This browser's sign-in client (as a hash), giving it a new cookie if it has none.</summary>
    public static string LoginClient(HttpContext http)
    {
        var token = http.Request.Cookies[LoginCookie];
        if (!IsWellFormed(token)) token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        http.Response.Cookies.Append(LoginCookie, token!, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = CookiePath,
            MaxAge = LoginCodes.Day,
        });
        return Hash(token!);
    }

    /// <summary>This browser's sign-in client (as a hash), or null if it never asked for a code.</summary>
    public static string? ExistingLoginClient(HttpContext http)
    {
        var token = http.Request.Cookies[LoginCookie];
        return IsWellFormed(token) ? Hash(token!) : null;
    }

    /// <summary>Whether this ID has signed in on this browser before.</summary>
    public static bool IsTrustedDevice(HttpContext http, string universityId) => DeviceIds(http).Contains(universityId);

    /// <summary>Adds the ID to this browser's list after a sign-in (newest first, five at most).</summary>
    public static void RememberDevice(HttpContext http, string universityId)
    {
        var ids = DeviceIds(http).Where(x => x != universityId).Prepend(universityId).Take(DeviceIdsMax);
        http.Response.Cookies.Append(DeviceCookie, Protector(http).Protect(string.Join(',', ids)), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = CookiePath,
            MaxAge = TimeSpan.FromDays(365),
        });
    }

    static List<string> DeviceIds(HttpContext http)
    {
        var raw = http.Request.Cookies[DeviceCookie];
        if (string.IsNullOrEmpty(raw) || raw.Length > 4096) return [];
        try
        {
            return [.. Protector(http).Unprotect(raw).Split(',', StringSplitOptions.RemoveEmptyEntries).Take(DeviceIdsMax)];
        }
        catch (CryptographicException) { return []; } // made up, or from before the keys changed
    }

    static IDataProtector Protector(HttpContext http) =>
        http.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(DevicePurpose);

    /// <summary>32 random bytes in base64url: 43 characters.</summary>
    static bool IsWellFormed(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
