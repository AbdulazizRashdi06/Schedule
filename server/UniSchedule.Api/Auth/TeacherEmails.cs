using System.Text.RegularExpressions;

namespace UniSchedule.Api.Auth;

/// <summary>Staff addresses look like "firstname.lastname@gutech.edu.om". Teachers sign in with theirs.</summary>
public static partial class TeacherEmails
{
    public const string Domain = "gutech.edu.om";
    const int MaxLength = 100;

    // Letters and digits with single dots, dashes or underscores between them, at exactly the staff domain.
    // No comma or space can get through: the address is a key in the comma-joined trusted-device cookie.
    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*@gutech\.edu\.om\z")]
    private static partial Regex Format();

    /// <summary>Accepts " Raed.Bolbol@GUtech.edu.om "; returns "raed.bolbol@gutech.edu.om", or null for anything that
    /// isn't a staff address (student addresses, look-alike hosts, double dots).</summary>
    public static string? Normalize(string? raw)
    {
        if (raw is null) return null;
        var email = raw.Trim().ToLowerInvariant();
        return email.Length <= MaxLength && Format().IsMatch(email) ? email : null;
    }

    /// <summary>Whether what was typed is a student address, so the sign-in screen can say where students sign in.</summary>
    public static bool IsStudentAddress(string? raw) =>
        raw is not null && raw.Trim().EndsWith("@" + StudentIds.EmailDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>"raed.bolbol" from "raed.bolbol@gutech.edu.om".</summary>
    public static string LocalPart(string email) => email.Split('@')[0];

    /// <summary>The name in the address: "raed.bolbol@…" is "Raed Bolbol", "raed.bolbol.2@…" is "Raed Bolbol 2".</summary>
    public static string DisplayName(string email) =>
        string.Join(' ', LocalPart(email).Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
