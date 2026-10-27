using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Students;

/// <summary>When students are busy on given dates: their classes (dated, or weekly on that weekday) and their own slots.</summary>
public static class Busy
{
    /// <summary>One busy stretch, in minutes since midnight. <paramref name="IsClass"/> is false for a student's own slot.</summary>
    public record Block(int Start, int End, string Label, string Room, bool IsClass);

    /// <summary>Uni hours the free-time search looks at: 08:00 to 18:15.</summary>
    public const int DayStart = 8 * 60, DayEnd = 18 * 60 + 15;

    static int Min(TimeOnly t) => t.Hour * 60 + t.Minute;

    /// <summary>Student id → date → busy blocks, sorted by start. Dates with nothing are left out.</summary>
    public static async Task<Dictionary<int, Dictionary<DateOnly, List<Block>>>> ForAsync(AppDb db, IReadOnlyCollection<int> studentIds, int semesterId,
        DateOnly from, DateOnly to, bool classesOnly, CancellationToken ct)
    {
        var picks = await db.Picks.Where(p => studentIds.Contains(p.StudentId) && p.Course.SemesterId == semesterId && p.SectionId != null)
            .Select(p => new { p.StudentId, p.Course.Code, Meetings = p.Section!.Meetings.Select(m => new { m.Day, m.Start, m.End, m.Room, m.Date }).ToList() })
            .ToListAsync(ct);
        var slots = classesOnly ? [] : await db.CustomSlots.Where(s => studentIds.Contains(s.StudentId) && s.SemesterId == semesterId).ToListAsync(ct);

        var result = studentIds.Distinct().ToDictionary(id => id, _ => new Dictionary<DateOnly, List<Block>>());
        void Add(int student, DateOnly date, Block b) =>
            (result[student].TryGetValue(date, out var list) ? list : result[student][date] = []).Add(b);

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var weekday = (int)d.DayOfWeek;
            foreach (var p in picks)
            foreach (var m in p.Meetings.Where(m => m.Date == d || (m.Date is null && m.Day == weekday)))
                Add(p.StudentId, d, new Block(Min(m.Start), Min(m.End), p.Code, m.Room, true));
            foreach (var s in slots.Where(s => s.Date == d || (s.Date is null && s.Day == weekday)))
                Add(s.StudentId, d, new Block(Min(s.Start), Min(s.End), s.Title, s.Place, false));
        }
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
