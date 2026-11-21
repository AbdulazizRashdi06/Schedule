using System.Globalization;
using System.Text.RegularExpressions;

namespace UniSchedule.Api.Schedule;

/// <summary>Reads the day and time cells registrars write in many ways: "Sun/Tue", "MW", "UTR", "8:00 AM – 9:15 AM", "0800-0915".</summary>
public static partial class Timeslots
{
    public static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    static readonly Dictionary<string, int> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sun"] = 0, ["sunday"] = 0, ["su"] = 0, ["u"] = 0,
        ["mon"] = 1, ["monday"] = 1, ["mo"] = 1, ["m"] = 1,
        ["tue"] = 2, ["tues"] = 2, ["tuesday"] = 2, ["tu"] = 2, ["t"] = 2,
        ["wed"] = 3, ["wednesday"] = 3, ["we"] = 3, ["w"] = 3,
        ["thu"] = 4, ["thur"] = 4, ["thurs"] = 4, ["thursday"] = 4, ["th"] = 4, ["r"] = 4,
        ["fri"] = 5, ["friday"] = 5, ["fr"] = 5, ["f"] = 5,
        ["sat"] = 6, ["saturday"] = 6, ["sa"] = 6,
    };

    static readonly int LongestWord = Words.Keys.Max(k => k.Length);

    /// <summary>Returns the distinct days in week order, or null when a part isn't a day.</summary>
    public static List<int>? ParseDays(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return null;
        var days = new SortedSet<int>();
        foreach (var token in Regex.Split(cell.Trim(), @"[\s,/;&+|\-]+|\band\b", RegexOptions.IgnoreCase))
        {
            if (token.Length == 0) continue;
            if (Words.TryGetValue(token, out var d)) { days.Add(d); continue; }
            // Compact forms: "MW", "TTh", "UTR", "SuTuTh".
            var compact = ParseCompact(token);
            if (compact is null) return null;
            days.UnionWith(compact);
        }
        return days.Count == 0 ? null : [.. days];
    }

    static List<int>? ParseCompact(string token)
    {
        var result = new List<int>();
        var i = 0;
        while (i < token.Length)
        {
            // Longest name first, so "TuThu" is Tue+Thu and not Tue+Th+u (Sunday), and "FSat" is Fri+Sat.
            var len = Math.Min(LongestWord, token.Length - i);
            while (len > 0 && !Words.ContainsKey(token.Substring(i, len))) len--;
            if (len == 0) return null;
            result.Add(Words[token.Substring(i, len)]);
            i += len;
        }
        return result;
    }

    // [0-9], not \d: \d also matches Arabic-Indic digits, which int.Parse can't read.
    [GeneratedRegex(@"^\s*([0-9]{1,2})(?:[:.h]?([0-9]{2}))?\s*(am|pm|a\.m\.|p\.m\.)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OneTime();

    /// <summary>Parses "8:00 AM - 9:15 AM", "08:00-09:15", "0800–0915", "2-3:30" (afternoon assumed for 1–6 without am/pm or a leading zero).</summary>
    public static (TimeOnly Start, TimeOnly End)? ParseRange(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return null;
        var parts = Regex.Split(cell.Trim(), @"\s*(?:-|–|—|to)\s*", RegexOptions.IgnoreCase);
        if (parts.Length != 2) return null;
        var a = ParsePart(parts[0]);
        var b = ParsePart(parts[1]);
        if (a is null || b is null) return null;
        var (ah, am, ap, apad) = a.Value;
        var (bh, bm, bp, bpad) = b.Value;
        // "9:30 - 10:45 AM": the second half's am/pm applies to the first when it fits.
        ap ??= bp is not null && Apply(ah, bp) * 60 + am <= Apply(bh, bp) * 60 + bm ? bp : null;
        // "05:00" written with a leading zero is a 24-hour time (Classy's own export), so it isn't moved to the afternoon...
        var start = Resolve(ah, am, ap, afternoon: !apad);
        var end = Resolve(bh, bm, bp, afternoon: !bpad);
        // ...unless only the afternoon makes sense, as in "11:30-01:00".
        if ((apad || bpad) && (start is null || end is null || end <= start))
        {
            start = Resolve(ah, am, ap, afternoon: true);
            end = Resolve(bh, bm, bp, afternoon: true);
        }
        if (start is null || end is null || end <= start) return null;
        return (start.Value, end.Value);
    }

    /// <summary><c>Padded</c>: the hour has a leading zero, like "05".</summary>
    static (int H, int M, string? AmPm, bool Padded)? ParsePart(string s)
    {
        var m = OneTime().Match(s);
        if (!m.Success) return null;
        var digits = m.Groups[1].Value;
        int h, min;
        if (!m.Groups[2].Success && digits.Length > 2) return null;
        h = int.Parse(digits, CultureInfo.InvariantCulture);
        min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var ampm = m.Groups[3].Success ? (m.Groups[3].Value.StartsWith('a') || m.Groups[3].Value.StartsWith('A') ? "am" : "pm") : null;
        return (h, min, ampm, digits.Length == 2 && digits[0] == '0');
    }

    static int Apply(int h, string? ampm) => ampm switch
    {
        "am" => h == 12 ? 0 : h,
        "pm" => h == 12 ? 12 : h + 12,
        _ => h,
    };

    static TimeOnly? Resolve(int h, int m, string? ampm, bool afternoon)
    {
        if (m > 59) return null;
        if (ampm is not null)
        {
            if (h is < 1 or > 12) return null;
            h = Apply(h, ampm);
        }
        else if (afternoon && h is >= 1 and <= 6) h += 12; // classes don't start at 2 a.m.
        if (h > 23) return null;
        return new TimeOnly(h, m);
    }

    /// <summary>Also accepts "0800-0930" by inserting the colon first.</summary>
    public static (TimeOnly Start, TimeOnly End)? ParseRangeLoose(string? cell)
    {
        if (cell is null) return null;
        var fixedCell = Regex.Replace(cell, @"\b([0-9]{2})([0-9]{2})\b", "$1:$2");
        return ParseRange(fixedCell);
    }

    public static string Format(TimeOnly t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
}
