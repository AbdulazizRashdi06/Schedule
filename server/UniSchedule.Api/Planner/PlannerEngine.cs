using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Planner;

public static class PlannerEngine
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool Date(string? value, out DateOnly date) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    public static bool Time(string? value, out TimeOnly time) => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    // Invariant, so stored fingerprints keep matching whatever locale the server runs with (and match the ones already stored).
    public static string Fingerprint(Section s, Meeting m) => Hash(string.Create(CultureInfo.InvariantCulture, $"{s.CourseId}|{s.Id}|{m.Day}|{m.Start}|{m.End}|{m.Room}|{m.Date}"));
    /// <summary>A date the planner works with: far enough from the ends of the calendar that a week either side still exists.</summary>
    public static bool Sane(DateOnly date) => date.Year is >= 2000 and <= 2100;
    public static DateOnly OmanToday(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(4)).DateTime);

    public static async Task<bool> Enabled(AppDb db, IConfiguration config, CancellationToken ct) =>
        (await db.AppSettings.Where(x => x.Key == "planner.enabled").Select(x => x.Value).FirstOrDefaultAsync(ct)) is { } setting
            ? setting == "true" : config.GetValue<bool>("Planner:Enabled");

    public static async Task<string?> ValidateTargets(AppDb db, int semester, PlannerTarget[]? targets, CancellationToken ct)
    {
        if (targets is null || targets.Length is 0 or > 100 || targets.Any(t => t is null || t.SectionIds is null)) return "Choose at least one course and valid sections.";
        if (targets.Select(t => t.CourseId).Distinct().Count() != targets.Length) return "Choose each course once.";
        var courses = await db.Courses.Include(c => c.Sections).Where(c => c.SemesterId == semester && targets.Select(t => t.CourseId).Contains(c.Id)).ToListAsync(ct);
        foreach (var t in targets)
        {
            var c = courses.FirstOrDefault(c => c.Id == t.CourseId);
            if (c is null || c.Sections.Count == 0) return "A selected course is no longer available in this semester.";
            if (t.SectionIds.Any(id => !c.Sections.Any(s => s.Id == id))) return $"Choose sections belonging to {c.Code}.";
        }
        return null;
    }

    public static async Task<string?> Validate(AppDb db, int semester, EventSpec spec, CancellationToken ct)
    {
        if (spec.Kind is not ("exam" or "class") || spec.Action is not ("add" or "replace" or "cancel")) return "Choose an exam or class and a valid action.";
        if (string.IsNullOrWhiteSpace(spec.Title) || spec.Title.Length > 120) return "Give this event a title (up to 120 characters).";
        if (!Date(spec.Date, out var date) || !Time(spec.Start, out var start) || !Time(spec.End, out var end) || end <= start) return "Choose a date and an end time later than the start.";
        if (!Sane(date)) return "Choose a date between 2000 and 2100.";
        if (spec.Room is null || spec.Room.Length > 100 || spec.Notes?.Length > 2000) return "Keep the location under 100 characters and notes under 2,000.";
        if (spec.RepeatUntil is not null && (!Date(spec.RepeatUntil, out var until) || until < date || until.DayNumber - date.DayNumber > 190)) return "Weekly recurrence must end within 190 days of its first date.";
        if (spec.Kind == "exam" && (spec.RepeatUntil is not null || spec.Source is not null || spec.Action != "add")) return "Exams happen on one date; edit or withdraw the published exam to change it.";
        if (spec.Action != "add" && spec.Source is null) return "Choose the university class occurrence being changed.";
        if (spec.Action == "add" && spec.Source is not null) return "A new event cannot replace a university class.";
        var problem = await ValidateTargets(db, semester, spec.Targets, ct);
        if (problem is not null) return problem;
        if (spec.Source is { } source)
        {
            if (spec.Action == "cancel" && spec.Date != source.Date) return "A cancellation must use the original class occurrence's date.";
            if (!Date(source.Date, out var original) || (spec.RepeatUntil is not null && Math.Abs(original.DayNumber - date.DayNumber) > 7)) return "Choose a valid original class date; recurring moves must stay within its week.";
            if (spec.Targets.Length != 1 || spec.Targets[0].CourseId != source.CourseId || !spec.Targets[0].SectionIds.SequenceEqual([source.SectionId])) return "A class change applies only to the original class section.";
            var section = await db.Sections.Include(s => s.Meetings).FirstOrDefaultAsync(s => s.Id == source.SectionId && s.CourseId == source.CourseId && s.Course.SemesterId == semester, ct);
            if (section is null || !section.Meetings.Any(m => Fingerprint(section, m) == source.Fingerprint && (m.Date == original || (m.Date is null && m.Day == (int)original.DayOfWeek)))) return "The university class changed since this proposal was made. Select the updated class and review again.";
            if (spec.RepeatUntil is not null && section.Meetings.Any(m => Fingerprint(section, m) == source.Fingerprint && m.Date is not null)) return "A dated university class can only be changed once.";
        }
        return null;
    }

    /// <summary>
    /// The dates an event happens: its first date as typed, then every week until RepeatUntil. Given the semester's
    /// <paramref name="span"/>, the weekly repeats skip days off and dates outside the teaching weeks, as university
    /// classes do. The first date is kept, so a make-up class put on a holiday still happens.
    /// </summary>
    public static IEnumerable<DateOnly> Dates(EventSpec spec, SemesterSpan? span = null) => Weeks(spec, span).Where(w => w.Shown).Select(w => w.Date);

    /// <summary>
    /// Each week of an event, with whether it is shown. A change to a university class follows that class: a week whose
    /// original class falls on a day off is left out. A week whose new date falls on one is not shown, but its original
    /// class still doesn't happen, so a moved class never comes back on its old day.
    /// </summary>
    public static IEnumerable<(DateOnly Date, bool Shown)> Weeks(EventSpec spec, SemesterSpan? span = null)
    {
        if (!Date(spec.Date, out var first)) yield break;
        var until = Date(spec.RepeatUntil, out var last) ? last : first;
        var original = spec.Source is { } source && Date(source.Date, out var o) ? o : (DateOnly?)null;
        bool Teaching(DateOnly d) => d >= span!.First && d <= span.Last && !span.DaysOff.ContainsKey(d);
        for (var d = first; d <= until && d.DayNumber - first.DayNumber <= 190; d = d.AddDays(7))
        {
            if (d == first || span is null) yield return (d, true);
            else if (original is not { } from) { if (Teaching(d)) yield return (d, true); }
            else if (Teaching(from.AddDays(d.DayNumber - first.DayNumber))) yield return (d, Teaching(d));
            // A draft stored before dates were checked can end in the last week there is.
            if (d.DayNumber > DateOnly.MaxValue.DayNumber - 7) yield break;
        }
    }

    public static bool SharedTargets(PlannerTarget[] a, PlannerTarget[] b) => a.Any(x => b.Any(y => x.CourseId == y.CourseId && (x.SectionIds.Length == 0 || y.SectionIds.Length == 0 || x.SectionIds.Intersect(y.SectionIds).Any())));
    public static string ExamIdentity(CalendarEntry entry) => entry.EventId is int id ? $"event:{id}" : entry.ProposalId is int proposal ? $"proposal:{proposal}" : entry.Key;

    public static async Task<List<CalendarEntry>> Calendar(AppDb db, int semesterId, DateOnly from, DateOnly to, int? workspaceId, CancellationToken ct, int? excludeProposal = null, int? excludeEvent = null,
        Func<int, DateOnly, TimeOnly, bool>? shadowed = null)
    {
        var semester = await db.Semesters.FirstAsync(s => s.Id == semesterId, ct);
        // The same weeks and days off students' timetables use.
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var sections = await db.Sections.Include(s => s.Course).Include(s => s.Meetings).Where(s => s.Course.SemesterId == semesterId).ToListAsync(ct);
        var fingerprints = sections.SelectMany(s => s.Meetings.Select(m => Fingerprint(s, m))).ToHashSet();
        var output = new List<CalendarEntry>();
        foreach (var s in sections)
        foreach (var m in s.Meetings)
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (!(m.Date == d || (m.Date is null && span.Weekly(m.Day, d, true)))) continue;
            var fingerprint = Fingerprint(s, m);
            var source = new PlannerSource(s.CourseId, s.Id, fingerprint, Iso(d), Timeslots.Format(m.Start), Timeslots.Format(m.End));
            output.Add(new($"import:{fingerprint}:{Iso(d)}", null, null, 0, "class", s.Course.Name, Iso(d), source.Start, source.End, m.Room, [new(s.CourseId, [s.Id])], "university", source));
        }
        var published = await db.PlannerEvents.Where(e => e.SemesterId == semesterId && !e.Withdrawn && e.Id != excludeEvent).ToListAsync(ct);
        foreach (var e in published) Apply(PlannerJson.Read<EventSpec>(e.PayloadJson), e.Id, null, e.Version, "published", true);
        if (workspaceId is not null)
        {
            var drafts = await db.PlannerProposals.Where(p => p.WorkspaceId == workspaceId && p.Status == "draft" && p.Id != excludeProposal).ToListAsync(ct);
            foreach (var p in drafts) Apply(PlannerJson.Read<EventSpec>(p.PayloadJson), p.EventId, p.Id, p.Version, "draft", false);
        }
        return output.OrderBy(e => e.Date).ThenBy(e => e.Start).ToList();

        void Apply(EventSpec spec, int? eventId, int? proposalId, int version, string status, bool effective)
        {
            var stale = spec.Source is { } src && !fingerprints.Contains(src.Fingerprint);
            var first = DateOnly.Parse(spec.Date, CultureInfo.InvariantCulture);
            foreach (var (d, shown) in Weeks(spec, span))
            {
                if (effective && !stale && spec.Source is { } original && Date(original.Date, out var originalDate))
                {
                    var replacedDate = originalDate.AddDays(d.DayNumber - first.DayNumber);
                    // A teacher changed that session: this week of the rep's event gives way to it.
                    if (shadowed is not null && Time(original.Start, out var originalStart) && shadowed(original.SectionId, replacedDate, originalStart)) continue;
                    output.RemoveAll(x => x.Key == $"import:{original.Fingerprint}:{Iso(replacedDate)}");
                }
                // A published cancellation hides its class; once the class changed it no longer applies, so it's shown for review.
                if (!shown || d < from || d > to || (effective && !stale && spec.Action == "cancel")) continue;
                output.Add(new($"{status}:{proposalId ?? eventId}:{Iso(d)}", eventId, proposalId, version, spec.Kind, spec.Title, Iso(d), spec.Start, spec.End, spec.Room, spec.Targets, spec.Action == "cancel" ? effective ? "cancelled" : "cancelled-draft" : status, spec.Source, stale, spec.Notes));
            }
        }
    }

    public static async Task<PlannerAnalysis> Analyze(AppDb db, PlannerWorkspace workspace, EventSpec spec, CancellationToken ct, int? proposalId = null, int? eventId = null)
    {
        var semester = await db.Semesters.FirstAsync(s => s.Id == workspace.SemesterId, ct);
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var occurrences = Dates(spec, span).ToList();
        if (occurrences.Count == 0 || !Sane(occurrences[0]) || !Sane(occurrences[^1])) return new("", [], ["Choose a valid date."]);
        var entries = await Calendar(db, workspace.SemesterId, occurrences[0].AddDays(-6), occurrences[^1].AddDays(6), workspace.Id, ct, proposalId, eventId);
        var courseNames = await db.Courses.Where(c => c.SemesterId == workspace.SemesterId).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var conflicts = new List<PlannerConflict>();
        var coverage = new List<string>();
        var selected = spec.Targets.Select(t => t.CourseId).ToArray();
        // Cohorts are course selections, not student rosters; include every batch containing the event's course.
        var batches = await db.PlannerBatches.Where(b => b.WorkspaceId == workspace.Id).ToListAsync(ct);
        var cohort = batches.Select(b => PlannerJson.Read<PlannerTarget[]>(b.TargetsJson)).Where(t => t.Any(x => selected.Contains(x.CourseId))).SelectMany(x => x).Concat(spec.Targets).ToArray();
        var courseSections = await db.Sections.Where(s => selected.Contains(s.CourseId)).Select(s => new { s.Id, s.CourseId, Count = s.Meetings.Count }).ToListAsync(ct);
        if (spec.Targets.Any(t => !courseSections.Any(s => s.CourseId == t.CourseId && s.Count > 0 && (t.SectionIds.Length == 0 || t.SectionIds.Contains(s.Id))))) coverage.Add("Some selected courses or sections have no class times. Availability is not confirmed for missing timetables.");
        foreach (var t in spec.Targets)
            if (courseSections.Any(s => s.CourseId == t.CourseId && s.Count == 0 && (t.SectionIds.Length == 0 || t.SectionIds.Contains(s.Id)))) coverage.Add("A selected section has no class times. Check its timetable before publishing.");
        if (spec.Action != "cancel")
        {
            foreach (var d in occurrences.Where(span.DaysOff.ContainsKey)) coverage.Add($"{Iso(d)} is {span.DaysOff[d]}, a day off.");
            // Exams after the last teaching week are the exam period; classes then, or anything before week 1, are unusual.
            if (span.WeeksKnown && (occurrences[0] < span.First || (spec.Kind == "class" && occurrences[0] > span.Last))) coverage.Add($"{Iso(occurrences[0])} is outside the semester's teaching weeks.");
            var weeks = Weeks(spec, span).ToList();
            var skipped = Dates(spec).Except(weeks.Select(x => x.Date)).ToList();
            if (skipped.Count > 0) coverage.Add($"The weekly repeat skips days off and dates outside the teaching weeks: {string.Join(", ", skipped.Select(Iso))}.");
            var dropped = weeks.Where(x => !x.Shown).Select(x => Iso(x.Date)).ToList();
            if (dropped.Count > 0) coverage.Add($"The moved class would fall on a day off or outside the teaching weeks on {string.Join(", ", dropped)}, so the class doesn't happen those weeks.");
        }
        foreach (var date in occurrences)
        {
            var iso = Iso(date);
            var start = TimeOnly.Parse(spec.Start, CultureInfo.InvariantCulture);
            var end = TimeOnly.Parse(spec.End, CultureInfo.InvariantCulture);
            foreach (var e in entries.Where(e => !e.Stale && e.Status != "cancelled-draft" && e.Date == iso))
            {
                if (spec.Source is { } source && Date(source.Date, out var originalDate)
                    && e.Key == $"import:{source.Fingerprint}:{Iso(originalDate.AddDays(date.DayNumber - occurrences[0].DayNumber))}") continue;
                var overlap = start < TimeOnly.Parse(e.End, CultureInfo.InvariantCulture) && TimeOnly.Parse(e.Start, CultureInfo.InvariantCulture) < end;
                var room = spec.Room.Length > 0 && string.Equals(spec.Room.Trim(), e.Room.Trim(), StringComparison.OrdinalIgnoreCase);
                if (spec.Action != "cancel" && overlap && (SharedTargets(cohort, e.Targets) || room)) conflicts.Add(new("overlap", iso, $"{e.Title} ({e.Start}–{e.End}){(room ? $" uses {e.Room}" : " overlaps this batch")}{(e.Status == "draft" ? " · draft" : "")}", e.Targets.Select(t => courseNames.GetValueOrDefault(t.CourseId, "Course")).ToArray(), e.Targets.SelectMany(t => t.SectionIds).Distinct().ToArray()));
            }
            if (spec.Kind != "exam" || spec.Action == "cancel") continue;
            // Other drafts of the exam being edited are versions of this exam, counted once as its +1.
            var exams = entries.Where(e => e.Kind == "exam" && !e.Stale && SharedTargets(cohort, e.Targets) && (eventId is null || e.EventId != eventId)).DistinctBy(e => e.Key).ToList();
            if (exams.Where(e => e.Date == iso).Select(ExamIdentity).Distinct().Count() + 1 >= workspace.DailyExamWarning) conflicts.Add(new("daily-load", iso, $"This would bring the batch to at least {workspace.DailyExamWarning} exams on one day.", [], []));
            // Test every rolling seven-day window containing the proposed exam.
            if (Enumerable.Range(0, 7).Any(back => exams.Where(e => Date(e.Date, out var d) && d >= date.AddDays(-back) && d <= date.AddDays(6 - back)).Select(ExamIdentity).Distinct().Count() + 1 >= workspace.WeeklyExamWarning)) conflicts.Add(new("weekly-load", iso, $"This would bring the batch to at least {workspace.WeeklyExamWarning} exams within seven days.", [], []));
        }
        var token = Hash(PlannerJson.Write(new { spec, workspace.DailyExamWarning, workspace.WeeklyExamWarning, entries, conflicts, coverage }));
        return new(token, conflicts, coverage.Distinct().ToList());
    }

    /// <summary>The published events of a semester that still match the timetable, loaded once for any number of students.</summary>
    public static async Task<PublishedPlan> PublishedAsync(AppDb db, int semester, CancellationToken ct)
    {
        var published = await db.PlannerEvents.Where(e => e.SemesterId == semester && !e.Withdrawn).ToListAsync(ct);
        if (published.Count == 0) return new([], null);
        var span = await SemesterSpan.ForAsync(db, await db.Semesters.FirstAsync(s => s.Id == semester, ct), ct);
        var sections = await db.Sections.Include(s => s.Meetings).Where(s => s.Course.SemesterId == semester).ToListAsync(ct);
        var fingerprints = sections.SelectMany(s => s.Meetings.Select(m => Fingerprint(s, m))).ToHashSet();
        return new(published.Select(e => (e, PlannerJson.Read<EventSpec>(e.PayloadJson)))
            .Where(x => x.Item2.Source is not { } source || fingerprints.Contains(source.Fingerprint)).ToList(), span);
    }

    /// <summary>Published events and cancelled or moved university classes, as one student sees them.</summary>
    public static async Task<(List<PlannerOccurrence> Events, Dictionary<string, List<string>> Excluded)> StudentView(AppDb db, int semester, List<Pick> picks, CancellationToken ct) =>
        (await PublishedAsync(db, semester, ct)).For(picks);
}

/// <summary>One date of a published planner event, for a student who takes one of its courses.</summary>
public record PlannerOccurrence(int Id, int Version, string Kind, string Title, string Date, string Start, string End, string Room,
    string? Notes, int CourseId, string Code, string CourseName, int? SectionId, DateTimeOffset UpdatedAt);

public sealed class PublishedPlan(List<(PlannerEvent Event, EventSpec Spec)> events, SemesterSpan? span)
{
    static DateOnly Day(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// For one student's picks (with their courses loaded): the events they get, and the dates each university class
    /// (by fingerprint) doesn't happen because an event cancelled or moved it.
    /// </summary>
    /// <param name="shadowed">Whether a teacher changed a session (section, its original date, its start): the rep's
    /// event on it is then skipped for that week, with no exclusion and no replacement. The event is never edited, so
    /// its other weeks are untouched and it comes back if the teacher undoes.</param>
    public (List<PlannerOccurrence> Events, Dictionary<string, List<string>> Excluded) For(IReadOnlyList<Pick> picks, Func<int, DateOnly, TimeOnly, bool>? shadowed = null)
    {
        var output = new List<PlannerOccurrence>();
        var excluded = new Dictionary<string, List<string>>();
        foreach (var (e, spec) in events)
        {
            var matching = picks.Where(p => spec.Targets.Any(t => t.CourseId == p.CourseId && (t.SectionIds.Length == 0 || (p.SectionId is int sid && t.SectionIds.Contains(sid))))).ToList();
            if (matching.Count == 0) continue;
            foreach (var (d, shown) in PlannerEngine.Weeks(spec, span))
            {
                if (spec.Source is { } original)
                {
                    var originalDate = Day(original.Date).AddDays(d.DayNumber - Day(spec.Date).DayNumber);
                    if (shadowed is not null && PlannerEngine.Time(original.Start, out var originalStart) && shadowed(original.SectionId, originalDate, originalStart)) continue;
                    var key = original.Fingerprint;
                    if (!excluded.ContainsKey(key)) excluded[key] = [];
                    excluded[key].Add(PlannerEngine.Iso(originalDate));
                }
                if (!shown || spec.Action == "cancel") continue;
                var pick = matching[0]; // One shared event, even when a student takes more than one target course.
                output.Add(new(e.Id, e.Version, spec.Kind, spec.Title, PlannerEngine.Iso(d), spec.Start, spec.End, spec.Room, spec.Notes,
                    pick.CourseId, pick.Course.Code, pick.Course.Name, pick.SectionId, e.UpdatedAt));
            }
        }
        return (output, excluded);
    }
}
