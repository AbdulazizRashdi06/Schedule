using System.Text.RegularExpressions;

namespace UniSchedule.Api.Auth;

/// <summary>University IDs look like "24-0161": intake year, dash, four digits.</summary>
public static partial class StudentIds
{
    public const string EmailDomain = "student.gutech.edu.om";

    [GeneratedRegex(@"^\d{2}-\d{4}$")]
    private static partial Regex Format();

    /// <summary>Accepts "24-0161", " 24 0161 " or "240161"; returns "24-0161" or null.</summary>
    public static string? Normalize(string? raw)
    {
        if (raw is null) return null;
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        var trimmed = raw.Trim();
        if (digits.Length != 6 || trimmed.Any(c => !char.IsAsciiDigit(c) && c != '-' && c != ' ')) return null;
        var id = $"{digits[..2]}-{digits[2..]}";
        return Format().IsMatch(id) ? id : null;
    }

    public static string EmailFor(string id) => $"{id.ToLowerInvariant()}@{EmailDomain}";

    /// <summary>Intake calendar year, e.g. 2024 for "24-0161".</summary>
    public static int IntakeYear(string id) => 2000 + int.Parse(id[..2]);

    /// <summary>
    /// Study year for an academic year that started in <paramref name="academicStartYear"/>, clamped to 0–5.
    /// 0 is the foundation year: everyone starts there, so "24-" students are in Year 2 during 2026/27.
    /// </summary>
    public static int PredictYear(string id, int academicStartYear) =>
        Math.Clamp(academicStartYear - IntakeYear(id), 0, 5);
}
