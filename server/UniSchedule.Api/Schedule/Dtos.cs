using UniSchedule.Api.Data;

namespace UniSchedule.Api.Schedule;

public record MeetingDto(int Day, string Start, string End, string Room);
public record SectionDto(int Id, string Number, string Instructor, List<MeetingDto> Meetings, string? ChangedAt, string? ChangeSummary);
public record CourseDto(int Id, string Code, string Name, string Type, int? Year, int? Credits, List<string> Majors, List<SectionDto> Sections);

public static class Dto
{
    public static MeetingDto Of(Meeting m) => new(m.Day, Timeslots.Format(m.Start), Timeslots.Format(m.End), m.Room);

    public static SectionDto Of(Section s) => new(s.Id, s.Number, s.Instructor,
        s.Meetings.OrderBy(m => m.Day).ThenBy(m => m.Start).Select(Of).ToList(),
        s.ChangedAt?.ToString("O"), s.ChangeSummary);

    public static CourseDto Of(Course c) => new(c.Id, c.Code, c.Name, c.Type.ToString(), c.Year, c.Credits,
        c.Majors.Select(m => m.Major.Code).OrderBy(x => x).ToList(),
        c.Sections.OrderBy(s => s.Number.PadLeft(4, '0')).Select(Of).ToList());

    public static object Of(Semester s) => new { s.Id, s.Name, s.Term, s.Year, s.IsCurrent, publishedAt = s.PublishedAt };
}

public static class Clashes
{
    public static bool Overlap(Meeting a, Meeting b) => a.Day == b.Day && a.Start < b.End && b.Start < a.End;

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
