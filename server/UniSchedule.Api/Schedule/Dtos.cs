using UniSchedule.Api.Data;

namespace UniSchedule.Api.Schedule;

/// <summary><paramref name="Date"/>: "2026-09-21" for a class on that date only; null = every week.</summary>
public record MeetingDto(int Day, string Start, string End, string Room, string? Date, List<string>? ExcludedDates = null);
public record SectionDto(int Id, string Number, string Instructor, List<MeetingDto> Meetings, string? ChangedAt, string? ChangeSummary);
public record CourseDto(int Id, string Code, string Name, string Type, int? Year, int? Credits, List<string> Majors, List<SectionDto> Sections);

public static class Dto
{
    public static MeetingDto Of(Meeting m) => new(m.Day, Timeslots.Format(m.Start), Timeslots.Format(m.End), m.Room, m.Date?.ToString("yyyy-MM-dd"));

    public static SectionDto Of(Section s) => new(s.Id, s.Number, s.Instructor,
        s.Meetings.OrderBy(m => m.Date).ThenBy(m => m.Day).ThenBy(m => m.Start).Select(Of).ToList(),
        s.ChangedAt?.ToString("O"), s.ChangeSummary);

    public static CourseDto Of(Course c) => new(c.Id, c.Code, c.Name, c.Type.ToString(), c.Year, c.Credits,
        c.Majors.Select(m => m.Major.Code).OrderBy(x => x).ToList(),
        c.Sections.OrderBy(s => s.Number.PadLeft(4, '0')).Select(Of).ToList());

    public static object Of(Semester s) => new { s.Id, s.Name, s.Term, s.Year, s.IsCurrent, publishedAt = s.PublishedAt };
}

/// <summary>
/// Teaching weeks of a semester: set by an admin, or taken from its dated classes. The timetable's span
/// (<see cref="SemesterSpan"/>) is worked out by <see cref="TeachingAsync"/> too, so the week labels and the weeks
/// classes meet in always agree.
/// </summary>
public static class SemesterWeeks
{
    /// <param name="Set">An admin set week 1 (and maybe the last week).</param>
    public record Weeks(string? Week1Start, int? LastWeek, bool Set);

    /// <summary>Week 1's Sunday, how many teaching weeks there are, and whether the week numbers mean anything.</summary>
    public record Teaching(DateOnly First, int Count, bool Known);

    public static DateOnly Sunday(DateOnly d) => d.AddDays(-(int)d.DayOfWeek);

    public static async Task<Weeks> ForAsync(Microsoft.EntityFrameworkCore.DbContext db, Semester s, CancellationToken ct)
    {
        var t = await TeachingAsync(db, s, ct);
        return t.Known ? new(t.First.ToString("yyyy-MM-dd"), t.Count, s.Week1Start is not null) : new(null, null, false);
    }

    /// <summary>
    /// Week 1 is the one staff set. Without it, it's the week of the first dated class when most sections are dated (then
    /// the dated classes are the timetable, and a weekly tutorial staff added to one section doesn't change that); when
    /// most are weekly, a dated lab says nothing about when term starts, so it's the term's usual start (1 September,
    /// 1 February or 1 June) and the weeks aren't numbered. The last week is the one staff set (only with week 1: a last
    /// week counts from it), else the week of the last dated class when most sections are dated, else
    /// <see cref="SemesterSpan.DefaultWeeks"/> weeks on.
    /// </summary>
    public static async Task<Teaching> TeachingAsync(Microsoft.EntityFrameworkCore.DbContext db, Semester s, CancellationToken ct)
    {
        var meetings = db.Set<Meeting>().Where(m => m.Date != null && db.Set<Section>().Any(x => x.Id == m.SectionId && x.Course.SemesterId == s.Id));
        var sections = db.Set<Section>().Where(x => x.Course.SemesterId == s.Id && x.Meetings.Any());
        DateOnly? min = null, max = null;
        if ((s.Week1Start is null || s.LastWeek is null)
            && await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(sections, x => x.Meetings.All(m => m.Date != null), ct)
               > await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.CountAsync(sections, x => x.Meetings.Any(m => m.Date == null), ct))
        {
            min = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.MinAsync(meetings, m => m.Date, ct);
            max = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.MaxAsync(meetings, m => m.Date, ct);
        }
        var first = Sunday(s.Week1Start ?? min ?? new DateOnly(s.Year, s.Term == "Fall" ? 9 : s.Term == "Summer" ? 6 : 2, 1));
        var fromDates = max is DateOnly last && (Sunday(last).DayNumber - first.DayNumber) / 7 + 1 is var n && n >= 1 ? n : (int?)null;
        var count = (s.Week1Start is not null ? s.LastWeek : null) ?? fromDates ?? SemesterSpan.DefaultWeeks;
        return new(first, count, s.Week1Start is not null || min is not null);
    }
}

public static class Clashes
{
    /// <summary>Same weekday and times overlap; two dated classes must also share the date.</summary>
    public static bool Overlap(Meeting a, Meeting b) =>
        a.Day == b.Day && a.Start < b.End && b.Start < a.End && (a.Date is null || b.Date is null || a.Date == b.Date);

    public static bool Overlap(Section a, Section b) => a.Meetings.Any(x => b.Meetings.Any(y => Overlap(x, y)));

    /// <summary>Pairs of section ids whose meetings overlap.</summary>
    public static List<int[]> Find(IReadOnlyList<Section> sections)
    {
        var result = new List<int[]>();
        for (var i = 0; i < sections.Count; i++)
            for (var j = i + 1; j < sections.Count; j++)
                if (Overlap(sections[i], sections[j])) result.Add([sections[i].Id, sections[j].Id]);
        return result;
    }
}
