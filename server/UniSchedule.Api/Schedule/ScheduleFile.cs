using System.Globalization;
using System.Text;
using ClosedXML.Excel;

namespace UniSchedule.Api.Schedule;

/// <summary>One spreadsheet row: one meeting pattern of one section.</summary>
public record ScheduleRow(
    int Line,
    string Code, string Name, string Majors, string Type, string Year, string Credits,
    string Section, string Instructor, string Days, string Time, string Room, string Date = "");

/// <summary>
/// Reads the semester upload: .xlsx, .csv, or text pasted straight from Excel (tab-separated).
/// The first row is the header; columns are found by name, in any order.
/// </summary>
public static class ScheduleFile
{
    static readonly Dictionary<string, string[]> Columns = new()
    {
        ["code"] = ["code", "course code", "course", "coursecode", "course_code", "course no", "course number"],
        ["name"] = ["name", "course name", "title", "course title", "coursename", "course_name"],
        ["major"] = ["major", "majors", "program", "programme", "programs"],
        ["type"] = ["type", "course type", "category", "kind"],
        ["year"] = ["year", "major year", "study year", "level", "year level"],
        ["credits"] = ["credits", "credit", "cr", "credit hours", "ch"],
        ["section"] = ["section", "sec", "section number", "section no", "group"],
        ["instructor"] = ["instructor", "lecturer", "teacher", "professor", "faculty", "instructor name"],
        ["days"] = ["days", "day"],
        ["time"] = ["time", "times", "timing", "slot", "time slot"],
        ["start"] = ["start", "start time", "from"],
        ["end"] = ["end", "end time", "to"],
        ["room"] = ["room", "location", "venue", "classroom", "hall"],
        ["date"] = ["date", "class date", "session date", "on"],
    };

    /// <summary>Days can be left out when a Date column gives each class its date.</summary>
    public static readonly string[] Required = ["code", "section"];

    public record ReadResult(List<ScheduleRow> Rows, List<string> Problems);

    public static ReadResult Read(Stream stream, string fileName)
    {
        if (fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            using var book = new XLWorkbook(stream);
            var sheet = book.Worksheets.First();
            var used = sheet.RangeUsed();
            if (used is null) return new([], ["The spreadsheet is empty."]);
            var table = used.Rows().Select(r => r.Cells(1, used.ColumnCount()).Select(CellText).ToList()).ToList();
            // Row numbers as the sheet shows them, even when the header isn't on row 1.
            return FromTable(table, used.RangeAddress.FirstAddress.RowNumber);
        }
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return ReadText(reader.ReadToEnd());
    }

    /// <summary>
    /// A real Excel date or time is read by its value, not as Excel shows it: the display format is often
    /// month-first or a pattern like "h:mm tt". Dates come out as 2026-09-21 and times as 13:30.
    /// </summary>
    static string CellText(IXLCell c)
    {
        if (c.DataType == XLDataType.TimeSpan) return c.GetTimeSpan().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        if (c.DataType != XLDataType.DateTime) return c.GetFormattedString().Trim();
        var d = c.GetDateTime();
        // Excel keeps a time on its own as a fraction of day 0 (30 Dec 1899).
        if (d.Year < 1900) return d.ToString("HH:mm", CultureInfo.InvariantCulture);
        return d.TimeOfDay == TimeSpan.Zero
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static ReadResult ReadText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var firstLine = lines.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var delimiter = new[] { '\t', ',', ';' }.OrderByDescending(d => firstLine.Count(c => c == d)).First();
        return FromTable(SplitDelimited(lines, delimiter));
    }

    /// <summary><paramref name="firstLine"/>: the line number of the table's first row.</summary>
    static ReadResult FromTable(List<List<string>> table, int firstLine = 1)
    {
        // Blank rows are skipped but keep their place, so "Row N" in a message is the row the admin sees.
        var lines = table.Select((r, i) => (Cells: r, Line: i + firstLine)).Where(x => x.Cells.Any(c => c.Length > 0)).ToList();
        if (lines.Count == 0) return new([], ["The file is empty."]);

        var header = lines[0].Cells.Select(h => h.Trim().ToLowerInvariant().Replace("_", " ")).ToList();
        var index = new Dictionary<string, int>();
        foreach (var (key, names) in Columns)
        {
            var i = header.FindIndex(h => names.Contains(h));
            if (i >= 0) index[key] = i;
        }
        var problems = new List<string>();
        var missing = Required.Where(r => !index.ContainsKey(r)).ToList();
        if (!index.ContainsKey("days") && !index.ContainsKey("date")) missing.Add("days");
        if (!index.ContainsKey("time") && !(index.ContainsKey("start") && index.ContainsKey("end"))) missing.Add("time");
        if (missing.Count > 0)
        {
            problems.Add($"Missing column{(missing.Count > 1 ? "s" : "")}: {string.Join(", ", missing)}. The first row must be the header.");
            return new([], problems);
        }

        string Cell(List<string> row, string key) =>
            index.TryGetValue(key, out var i) && i < row.Count ? row[i].Trim() : "";

        var rows = new List<ScheduleRow>();
        for (var r = 1; r < lines.Count; r++)
        {
            var row = lines[r].Cells;
            var time = Cell(row, "time");
            if (time.Length == 0 && Cell(row, "start").Length > 0) time = $"{Cell(row, "start")} - {Cell(row, "end")}";
            rows.Add(new ScheduleRow(lines[r].Line,
                // Every kind of space goes (a non-breaking one pasted from the web too), so "CS 201" and "CS201" are one course.
                string.Concat(Cell(row, "code").Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant(), Cell(row, "name"), Cell(row, "major"), Cell(row, "type"),
                Cell(row, "year"), Cell(row, "credits"), Cell(row, "section"), Cell(row, "instructor"),
                Cell(row, "days"), time, Cell(row, "room"), Cell(row, "date")));
        }
        return new(rows, problems);
    }

    /// <summary>RFC 4180-style split: quoted fields may hold the delimiter, quotes ("") and line breaks.</summary>
    static List<List<string>> SplitDelimited(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
