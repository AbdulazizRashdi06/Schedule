using System.Text;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Schedule;

/// <summary>A semester's classes, one row per class, for the admin export and the public timetable API.</summary>
public static class TimetableExport
{
    public record Row(string Code, string Name, List<string> Majors, string Type, int? Year, int? Credits,
        string Section, string Instructor, string? Date, int Day, string Start, string End, string Room);

    public static async Task<List<Row>> RowsAsync(AppDb db, int semesterId, CancellationToken ct)
    {
        var courses = await db.Courses.Where(c => c.SemesterId == semesterId)
            .Include(c => c.Majors).ThenInclude(m => m.Major)
            .Include(c => c.Sections).ThenInclude(s => s.Meetings)
            .AsSplitQuery().OrderBy(c => c.Code).ToListAsync(ct);
        return courses.SelectMany(c => c.Sections.OrderBy(s => s.Number).SelectMany(s =>
            s.Meetings.OrderBy(m => m.Date).ThenBy(m => m.Day).ThenBy(m => m.Start).Select(m => new Row(
                c.Code, c.Name, c.Majors.Select(x => x.Major.Code).Order().ToList(), c.Type.ToString(), c.Year, c.Credits,
                s.Number, s.Instructor, m.Date?.ToString("yyyy-MM-dd"), m.Day, Timeslots.Format(m.Start), Timeslots.Format(m.End), m.Room)))).ToList();
    }

    static string Cell(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    /// <summary>The upload's own columns, so the file can be edited and uploaded back. Starts with a byte-order mark for Excel.</summary>
    public static byte[] Csv(IEnumerable<Row> rows)
    {
        var csv = new StringBuilder("Course Code,Course Name,Major,Type,Year,Credits,Section,Instructor,Date,Days,Time,Room\r\n");
        foreach (var r in rows)
            csv.Append(string.Join(",", new[]
            {
                r.Code, r.Name, string.Join(", ", r.Majors), r.Type, r.Year switch { null => "", 0 => "Foundation", var y => y.ToString()! },
                r.Credits?.ToString() ?? "", r.Section, r.Instructor, r.Date ?? "", r.Date is null ? Timeslots.DayNames[r.Day] : "",
                $"{r.Start}-{r.End}", r.Room,
            }.Select(Cell))).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    public static string FileName(Semester s) =>
        $"classy-{string.Concat(s.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).ToLowerInvariant()}-classes.csv";
}
