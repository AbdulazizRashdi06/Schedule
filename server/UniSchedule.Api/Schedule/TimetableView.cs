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
    /// <param name="viewer">On the teacher side: what this teacher may do with each change (undo, edit).</param>
    public static async Task<Dictionary<string, object?>> BuildAsync(AppDb db, Semester semester, IReadOnlyList<ClassRow> classes,
        IEnumerable<object> slots, (List<PlannerOccurrence> Events, Dictionary<string, List<string>> Excluded) planner,
        IEnumerable<object> needsSection, TeachActor? teacher, CancellationToken ct, ChangeViewer? viewer = null)
    {
        // Weekly classes skip the planner's cancellations and the semester's days off; dated ones keep their date.
        // The semester's first and last teaching day (its days off come with it).
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var daysOff = span.DaysOff;
        var sections = classes.Select(c => c.Section).ToList();
        // Teachers' changes. Old cached copies of the app know nothing about them and stay correct: the original
        // session is in its meeting's excludedDates, and the class at its new time or room, or an extra class, is one
        // more dated meeting. The newer app reads "changes" to draw the marks.
        var changes = await ClassChanges.LoadAsync(db, semester.Id, sections, ct);
        List<string>? Skipped(Section section, Meeting m)
        {
            var skipped = (planner.Excluded.GetValueOrDefault(PlannerEngine.Fingerprint(section, m)) ?? [])
                .Concat(m.Date is null ? DaysOff.Skipped(daysOff, m.Day) : [])
                .Concat(changes.Skip(section, m).Select(PlannerEngine.Iso)).Distinct().Order().ToList();
            return skipped.Count == 0 ? null : skipped;
        }
        SectionDto EffectiveSection(Section section) => Dto.Of(section) with
        {
            Meetings = section.Meetings.Select(m => (m.Date, m.Day, m.Start, Dto: Dto.Of(m) with
                {
                    ExcludedDates = Skipped(section, m),
                    // Only the teacher's timetable puts one real class under several codes together.
                    Merge = teacher is null ? null : TeacherSchedule.MergeKey(m.Date, m.Day, m.Start, m.End, m.Room),
                }))
                .Concat(changes.AddedTo(section).Select(a => ((DateOnly?)a.Date, (int)a.Date.DayOfWeek, a.Start,
                    new MeetingDto((int)a.Date.DayOfWeek, Timeslots.Format(a.Start), Timeslots.Format(a.End), a.Room, PlannerEngine.Iso(a.Date), null,
                        a.Change.Id, a.Change.SessionType, teacher is null ? null : TeacherSchedule.GroupKey(a.Change)))))
                .OrderBy(x => x.Item1).ThenBy(x => x.Item2).ThenBy(x => x.Item3).Select(x => x.Item4).ToList(),
        };

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
            // Teachers' live changes to these classes, past ones too, so last week still shows Cancelled.
            ["changes"] = changes.Shown(sections).Select(c => ChangeRules.View(c, changes, sections.First(s => s.Id == c.SectionId).CourseId, viewer)).ToList(),
        };
    }
}
