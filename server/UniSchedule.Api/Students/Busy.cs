using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Students;

/// <summary>When students are busy on given dates: their classes (dated, or weekly on that weekday) and their own slots.</summary>
public static class Busy
{
    /// <summary>One busy stretch, in minutes since midnight. <paramref name="IsClass"/> is false for a student's own slot.</summary>
    public record Block(int Start, int End, string Label, string Room, bool IsClass);

    /// <summary>Uni hours the free-time search looks at: 08:00 to 18:15.</summary>
    public const int DayStart = 8 * 60, DayEnd = 18 * 60 + 15;

    static int Min(TimeOnly t) => t.Hour * 60 + t.Minute;

    /// <summary>
    /// Student id → date → busy blocks, sorted by start. Dates with nothing are left out. Same as their timetables:
    /// days off, the semester's weeks and (with <paramref name="planner"/>) the batch reps' changes apply.
    /// </summary>
    public static async Task<Dictionary<int, Dictionary<DateOnly, List<Block>>>> ForAsync(AppDb db, IReadOnlyCollection<int> studentIds, Semester semester,
        DateOnly from, DateOnly to, bool classesOnly, bool planner, CancellationToken ct)
    {
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var items = await StudentSchedule.ForAsync(db, semester.Id, studentIds, slots: !classesOnly, planner, ct);

        var result = studentIds.Distinct().ToDictionary(id => id, _ => new Dictionary<DateOnly, List<Block>>());
        void Add(int student, DateOnly date, Block b) =>
            (result[student].TryGetValue(date, out var list) ? list : result[student][date] = []).Add(b);

        for (var d = from; d <= to; d = d.AddDays(1))
            foreach (var i in items.Where(i => span.Happens(i, d)))
                Add(i.StudentId, d, new Block(Min(i.Start), Min(i.End), i.IsClass ? i.Code : i.Name, i.Room, i.IsClass));
        foreach (var days in result.Values)
        foreach (var list in days.Values)
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>A stretch of the day and who is busy in it.</summary>
    public record Stretch(int Start, int End, IReadOnlySet<int> BusyMembers);

    /// <summary>
    /// Splits uni hours into stretches where the same members are busy, joining neighbours with the same set.
    /// <paramref name="busy"/> maps each member to their blocks that day.
    /// </summary>
    public static List<Stretch> Stretches(Dictionary<int, List<Block>> busy)
    {
        var cuts = new SortedSet<int> { DayStart, DayEnd };
        foreach (var b in busy.Values.SelectMany(x => x))
        {
            if (b.Start > DayStart && b.Start < DayEnd) cuts.Add(b.Start);
            if (b.End > DayStart && b.End < DayEnd) cuts.Add(b.End);
        }
        var points = cuts.ToList();
        var output = new List<Stretch>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var (a, z) = (points[i], points[i + 1]);
            var who = busy.Where(kv => kv.Value.Any(b => b.Start < z && a < b.End)).Select(kv => kv.Key).ToHashSet();
            if (output.Count > 0 && output[^1].BusyMembers.SetEquals(who)) output[^1] = output[^1] with { End = z };
            else output.Add(new Stretch(a, z, who));
        }
        return output;
    }
}
