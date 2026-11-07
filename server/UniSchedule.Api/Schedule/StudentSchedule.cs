using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;

namespace UniSchedule.Api.Schedule;

/// <summary>
/// When a semester's weekly classes meet: from week 1 to the last week, except on days off. Week 1 and the last week are
/// the ones staff set; without them, from the term's usual start (1 September, 1 February or 1 June) for
/// <see cref="DefaultWeeks"/> weeks.
/// </summary>
/// <param name="WeeksKnown">Week 1 is set or comes from dated classes, so week numbers mean something.</param>
/// <param name="LastWeek">The last teaching week's number, when known.</param>
public sealed record SemesterSpan(DateOnly First, DateOnly Last, Dictionary<DateOnly, string> DaysOff, bool WeeksKnown, int? LastWeek)
{
    public const int DefaultWeeks = 16;
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);

    public static async Task<SemesterSpan> ForAsync(AppDb db, Semester s, CancellationToken ct)
    {
        // Not guessed from dated classes (one dated lab would make the whole semester a week long), nor from when it was
        // published (a semester published in week 5 still had weeks 1 to 4).
        var first = SemesterWeeks.Sunday(s.Week1Start ?? new DateOnly(s.Year, s.Term == "Fall" ? 9 : s.Term == "Summer" ? 6 : 2, 1));
        var weeks = s.Week1Start is not null && s.LastWeek is int set ? set : DefaultWeeks;
        return new(first, first.AddDays(weeks * 7 - 1), await UniSchedule.Api.Schedule.DaysOff.ForAsync(db, s.Id, ct),
            s.Week1Start is not null, s.Week1Start is not null ? s.LastWeek : null);
    }

    /// <summary>"week 3" for a date in the teaching weeks, when they're known.</summary>
    public string? Week(DateOnly d) => !WeeksKnown || d < First || d > Last ? null : $"week {(d.DayNumber - First.DayNumber) / 7 + 1}";

    /// <summary>Whether a weekly class (or own slot) on <paramref name="weekday"/> meets on <paramref name="date"/>.</summary>
    public bool Weekly(int weekday, DateOnly date, bool isClass) =>
        weekday == (int)date.DayOfWeek && date >= First && date <= Last && !(isClass && DaysOff.ContainsKey(date));
}

/// <summary>
/// What a student has on: their classes, their own slots and the batch reps' published events, with days off and
/// cancelled or moved classes taken out. The timetable, calendar feeds, reminders and groups all read it from here, so
/// they agree.
/// </summary>
public static class StudentSchedule
{
    public enum Source { Class, Slot, Planner }

    /// <summary>
    /// One class, own slot or planner event: on <paramref name="Date"/>, or every week on <paramref name="Day"/>.
    /// <paramref name="Skip"/>: dates it doesn't happen because a planner event cancelled or moved it.
    /// </summary>
    public sealed record Item(int StudentId, Source From, DateOnly? Date, int Day, TimeOnly Start, TimeOnly End, IReadOnlySet<DateOnly> Skip,
        int? CourseId, string Code, string Name, string Room, string? Section, string? Instructor, int? SectionId, int? MeetingId,
        int? SlotId, string? Note, int? EventId, string? EventKind)
    {
        /// <summary>A class or a planner event; false for the student's own slot.</summary>
        public bool IsClass => From != Source.Slot;
    }

    static readonly IReadOnlySet<DateOnly> None = new HashSet<DateOnly>();

    /// <summary>Whether an item happens on a date.</summary>
    public static bool Happens(this SemesterSpan span, Item i, DateOnly date) =>
        !i.Skip.Contains(date) && (i.Date is DateOnly on ? on == date : span.Weekly(i.Day, date, i.From == Source.Class));

    /// <summary>
    /// Everything the given students have in the semester. <paramref name="slots"/>: include own slots.
    /// <paramref name="planner"/>: apply the semester planner (when it's turned on).
    /// </summary>
    public static async Task<List<Item>> ForAsync(AppDb db, int semesterId, IReadOnlyCollection<int> studentIds, bool slots, bool planner, CancellationToken ct)
    {
        var picks = await db.Picks.Where(p => studentIds.Contains(p.StudentId) && p.Course.SemesterId == semesterId && p.SectionId != null)
            .Include(p => p.Course).Include(p => p.Section).ThenInclude(x => x!.Meetings)
            .AsSplitQuery().ToListAsync(ct);
        var plan = planner ? await PlannerEngine.PublishedAsync(db, semesterId, ct) : null;
        var items = new List<Item>();
        foreach (var mine in picks.GroupBy(p => p.StudentId))
        {
            var (events, excluded) = plan?.For(mine.ToList()) ?? ([], []);
            foreach (var p in mine)
            foreach (var m in p.Section!.Meetings)
            {
                var skip = excluded.TryGetValue(PlannerEngine.Fingerprint(p.Section, m), out var dates)
                    ? dates.Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToHashSet()
                    : None;
                items.Add(new(p.StudentId, Source.Class, m.Date, m.Day, m.Start, m.End, skip, p.CourseId, p.Course.Code, p.Course.Name, m.Room,
                    p.Section.Number, p.Section.Instructor, p.Section.Id, m.Id, null, null, null, null));
            }
            foreach (var e in events)
            {
                var date = DateOnly.ParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                items.Add(new(mine.Key, Source.Planner, date, (int)date.DayOfWeek, TimeOnly.ParseExact(e.Start, "HH:mm", CultureInfo.InvariantCulture),
                    TimeOnly.ParseExact(e.End, "HH:mm", CultureInfo.InvariantCulture), None, e.CourseId, e.Code, e.Title, e.Room,
                    null, null, e.SectionId, null, null, e.Notes, e.Id, e.Kind));
            }
        }
        if (slots)
            foreach (var s in await db.CustomSlots.Where(x => studentIds.Contains(x.StudentId) && x.SemesterId == semesterId).ToListAsync(ct))
                items.Add(new(s.StudentId, Source.Slot, s.Date, s.Day, s.Start, s.End, None, null, s.Title, s.Title, s.Place,
                    null, null, null, null, s.Id, s.Note, null, null));
        return items;
    }
}
