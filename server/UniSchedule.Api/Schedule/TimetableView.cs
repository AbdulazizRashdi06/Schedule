using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Teachers;

namespace UniSchedule.Api.Schedule;

/// <summary>
/// One class on a timetable. A student's has <paramref name="Notes"/> (their note counts); a teacher's has
/// <paramref name="Students"/> (how many Classy users picked it), <paramref name="Own"/> and <paramref name="Added"/>.
/// </summary>
public sealed record ClassRow(Course Course, Section Section, bool Changed, object? Notes, int? Students, bool? Own, bool? Added);

/// <summary>
/// The timetable JSON the home draws. The student's and the teacher's are made here, in one place, so the app reads
/// both with the same code and they can't drift apart. Old cached copies of the app read the student one: its keys and
/// their values stay as they always were, and everything newer is added beside them.
/// </summary>
public static class TimetableView
{
    /// <summary>The answer while no semester is published.</summary>
    public static Dictionary<string, object?> Empty() => new()
    {
        ["semester"] = null,
        ["classes"] = Array.Empty<object>(),
        ["needsSection"] = Array.Empty<object>(),
        ["clashes"] = Array.Empty<int[]>(),
        ["slots"] = Array.Empty<object>(),
    };

    /// <summary>The timetable JSON the home draws: for a student's picks, or for a teacher's sections.</summary>
    /// <param name="planner">The batch reps' published events for these classes, and the dates each class is off because of one.</param>
    /// <param name="teacher">Who is looking, on the teacher side; null for a student.</param>
    public static async Task<Dictionary<string, object?>> BuildAsync(AppDb db, Semester semester, IReadOnlyList<ClassRow> classes,
        IEnumerable<object> slots, (List<PlannerOccurrence> Events, Dictionary<string, List<string>> Excluded) planner,
        IEnumerable<object> needsSection, TeachActor? teacher, CancellationToken ct)
    {
        // Weekly classes skip the planner's cancellations and the semester's days off; dated ones keep their date.
        // The semester's first and last teaching day (its days off come with it).
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var daysOff = span.DaysOff;
        List<string>? Skipped(Section section, Meeting m)
        {
            var skipped = (planner.Excluded.GetValueOrDefault(PlannerEngine.Fingerprint(section, m)) ?? [])
                .Concat(m.Date is null ? DaysOff.Skipped(daysOff, m.Day) : []).Distinct().Order().ToList();
            return skipped.Count == 0 ? null : skipped;
        }
        SectionDto EffectiveSection(Section section) => Dto.Of(section) with
        {
            Meetings = section.Meetings.OrderBy(m => m.Date).ThenBy(m => m.Day).ThenBy(m => m.Start).Select(m => Dto.Of(m) with
            {
                ExcludedDates = Skipped(section, m),
                // Only the teacher's timetable puts one real class under several codes together.
                Merge = teacher is null ? null : TeacherSchedule.MergeKey(m.Date, m.Day, m.Start, m.End, m.Room),
            }).ToList(),
        };

        var sections = classes.Select(c => c.Section).ToList();
        var teachers = await TeacherPages.RefsAsync(db, sections, ct);
        // Each class is a dictionary, so a teacher's has no "notes" key at all and a student's none of the teacher's three.
        Dictionary<string, object?> Class(ClassRow c)
        {
            var row = new Dictionary<string, object?>
            {
                ["courseId"] = c.Course.Id,
                ["code"] = c.Course.Code,
                ["name"] = c.Course.Name,
                ["type"] = c.Course.Type.ToString(),
                ["credits"] = c.Course.Credits,
                ["section"] = EffectiveSection(c.Section),
                ["changed"] = c.Changed,
            };
            if (teacher is null) row["notes"] = c.Notes;
            else
            {
                row["students"] = c.Students ?? 0;
                row["own"] = c.Own ?? false;
                row["added"] = c.Added ?? false;
            }
            row["teachers"] = teachers.GetValueOrDefault(c.Section.Id) ?? [];
            return row;
        }

        return new()
        {
            ["semester"] = new { semester.Id, semester.Name },
            ["span"] = new { first = span.First.ToString("yyyy-MM-dd"), last = span.Last.ToString("yyyy-MM-dd") },
            ["daysOff"] = daysOff.OrderBy(d => d.Key).Select(d => new { date = d.Key.ToString("yyyy-MM-dd"), name = d.Value }).ToList(),
            ["plannerEvents"] = planner.Events,
            ["slots"] = slots.ToList(),
            ["classes"] = classes.Select(Class).ToList(),
            ["needsSection"] = needsSection.ToList(),
            // The app works clashes out from the sessions it draws; a teacher's merged classes are no clash.
            ["clashes"] = teacher is null ? Clashes.Find(sections) : [],
            // Teachers' class changes. There are none yet.
            ["changes"] = Array.Empty<object>(),
        };
    }
}
