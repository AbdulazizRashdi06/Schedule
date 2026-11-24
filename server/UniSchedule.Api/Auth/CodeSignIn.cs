using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Auth;

/// <summary>
/// Asking for and checking a sign-in code, for any key: a student's university ID or a teacher's staff email. The
/// limits are the same for both; only the word in the messages ("ID" or "email") and the tries per code differ.
/// Sending the email and the per-address guess limits stay with each endpoint.
/// </summary>
public static class CodeSignIn
{
    /// <summary>Tries per code for a staff email. Lower than a student's five: a guessed code there can change classes.</summary>
    public const int EmailMaxAttempts = 3;

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

    /// <summary>Asks for a code for a key (a university ID or a staff email). Returns the code to send, or the error to answer with.</summary>
    public static async Task<(LoginCode? Login, string? Code, IResult? Error)> RequestAsync(AppDb db, HttpContext http, TimeProvider clock, string key, string noun, bool trusted, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var client = SignInCookies.LoginClient(http);
        // Codes older than every limit are no use to anyone.
        var dayAgo = now - LoginCodes.Day;
        await db.LoginCodes.Where(c => c.UniversityId == key && c.CreatedAt <= dayAgo).ExecuteDeleteAsync(ct);

        // The resend wait and the 15-minute cap are per browser, so someone else asking for codes can't use them up.
        var recent = await db.LoginCodes.Where(c => c.UniversityId == key && c.ClientHash == client && c.CreatedAt > now - LoginCodes.Window)
            .OrderByDescending(c => c.CreatedAt).Select(c => c.CreatedAt).ToListAsync(ct);
        if (recent.Count > 0 && now - recent[0] < LoginCodes.ResendAfter)
        {
            var wait = (int)Math.Ceiling((LoginCodes.ResendAfter - (now - recent[0])).TotalSeconds);
            return (null, null, Errors.TooMany($"Wait {wait} seconds before asking for a new code."));
        }
        if (recent.Count >= LoginCodes.MaxPerWindow)
            return (null, null, Errors.TooMany($"Too many codes for this {noun}. Wait 15 minutes and try again."));

        if (!trusted)
        {
            var day = await db.LoginCodes.Where(c => c.UniversityId == key).OrderBy(c => c.CreatedAt)
                .Select(c => new { c.CreatedAt, Wrong = c.UsedAt == null ? c.Attempts : c.Attempts - 1 }).ToListAsync(ct);
            if (day.Count >= LoginCodes.MaxPerDay)
                return (null, null, Errors.TooMany($"This {noun} has had too many sign-in codes today. Try again in {Hours(HoursUntilUnder(day, d => d.CreatedAt, _ => 1, LoginCodes.MaxPerDay, now))}, or sign in on a device you've used before."));
            if (day.Sum(d => d.Wrong) >= LoginCodes.MaxWrongPerDay)
                return (null, null, Errors.TooMany($"Too many wrong codes for this {noun} today. Try again in {Hours(HoursUntilUnder(day, d => d.CreatedAt, d => d.Wrong, LoginCodes.MaxWrongPerDay, now))}, or sign in on a device you've used before."));
        }

        var code = LoginCodes.Generate();
        var login = new LoginCode
        {
            UniversityId = key,
            CodeHash = LoginCodes.Hash(key, code),
            ClientHash = client,
            CreatedAt = now,
            ExpiresAt = now + LoginCodes.Lifetime,
        };
        db.LoginCodes.Add(login);
        await db.SaveChangesAsync(ct);
        return (login, code, null);
    }

    /// <summary>After the email went out: this browser's earlier codes stop working (they still count towards the limits).</summary>
    public static Task RetireOthersAsync(AppDb db, LoginCode login, string key, DateTimeOffset now) =>
        db.LoginCodes.Where(c => c.UniversityId == key && c.ClientHash == login.ClientHash && c.Id != login.Id && c.UsedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresAt, now), CancellationToken.None);

    /// <summary>A stored hash that no code matches: for a code row that only exists so the limits behave as usual.</summary>
    public static string UnmatchableHash() =>
        $"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))}.{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";

    /// <summary>Checks a code. Error is null when it was right (and is now used). WrongCode is true when a real try was used on a wrong code.</summary>
    public static async Task<(IResult? Error, bool WrongCode)> VerifyAsync(AppDb db, HttpContext http, TimeProvider clock, string key, string? code, string noun, int maxAttempts, bool trusted, CancellationToken ct)
    {
        if (!LoginCodes.IsWellFormed(code)) return (Errors.BadRequest("Enter the 6-digit code from your email."), false);
        var now = clock.GetUtcNow();
        // A code only works in the browser that asked for it, and only this browser's newest code counts:
        // asking for a new one retires the old.
        var client = SignInCookies.ExistingLoginClient(http);
        if (client is null) return (Errors.BadRequest("That code has expired. Send a new one."), false);
        var login = await db.LoginCodes
            .Where(c => c.UniversityId == key && c.ClientHash == client && c.UsedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (login is null || login.ExpiresAt <= now)
            return (Errors.BadRequest("That code has expired. Send a new one."), false);
        if (login.Attempts >= maxAttempts)
            return (Errors.BadRequest("Too many wrong tries. Send a new code."), false);

        // Each try is claimed in the database before the code is checked, so guesses sent in parallel can't share a
        // try: once they are used, every other request finds none left. Away from a browser this key has signed
        // in on before, the claim also needs the key to have had fewer than 30 wrong codes in the last day.
        var dayAgo = now - LoginCodes.Day;
        var claimed = await db.LoginCodes
            .Where(c => c.Id == login.Id && c.UsedAt == null && c.Attempts < maxAttempts)
            .Where(c => trusted || db.LoginCodes.Where(o => o.UniversityId == key && o.CreatedAt > dayAgo)
                .Sum(o => o.UsedAt == null ? o.Attempts : o.Attempts - 1) < LoginCodes.MaxWrongPerDay)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Attempts, c => c.Attempts + 1), ct);
        if (claimed == 0)
        {
            var wrongToday = await db.LoginCodes.Where(o => o.UniversityId == key && o.CreatedAt > dayAgo)
                .SumAsync(o => o.UsedAt == null ? o.Attempts : o.Attempts - 1, ct);
            return (!trusted && wrongToday >= LoginCodes.MaxWrongPerDay
                ? Errors.TooMany($"Too many wrong codes for this {noun} today. Try again later, or sign in on a device you've used before.")
                : Errors.BadRequest("Too many wrong tries. Send a new code."), false);
        }
        if (!LoginCodes.Verify(login.CodeHash, key, code!))
        {
            var tries = await db.LoginCodes.Where(c => c.Id == login.Id).Select(c => c.Attempts).FirstAsync(ct);
            var left = maxAttempts - tries;
            return (Errors.BadRequest(left > 0 ? $"Wrong code. {left} {(left == 1 ? "try" : "tries")} left." : "Wrong code. Send a new one."), true);
        }

        // A code signs in once, even if two requests get here together.
        var won = await db.LoginCodes.Where(c => c.Id == login.Id && c.UsedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.UsedAt, now), ct);
        return won == 0 ? (Errors.BadRequest("That code has expired. Send a new one."), false) : (null, false);
    }
}
