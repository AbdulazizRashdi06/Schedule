using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Teachers;

/// <summary>Who is looking at a change on the teacher side, to say what they may still do with it.</summary>
/// <param name="Enabled">Class changes are switched on.</param>
/// <param name="MayChange">The sections this teacher may change.</param>
public sealed record ChangeViewer(TeachActor Actor, bool Enabled, DateTimeOffset Now, IReadOnlySet<int> MayChange);

/// <summary>
/// The rules of a teacher's change to a class: what is refused, what only warns, who may undo or edit and until when,
/// and what a write does. The endpoints and the dashboard call these; nothing else writes or ends a change.
/// </summary>
public static class ChangeRules
{
    /// <summary>What the app sends to preview, make or replace a change.</summary>
    /// <param name="SectionId">With <paramref name="Date"/> and <paramref name="Start"/>: the session, as the university lists it.</param>
    /// <param name="SectionIds">The course codes ticked. Left out: all of them.</param>
    /// <param name="AlsoOther">Also the section's other sessions that day.</param>
    /// <param name="Room">"" or null = to be announced.</param>
    /// <param name="Type">An extra class only: lecture, lab or tutorial.</param>
    /// <param name="GroupId">Preview only: the change being replaced.</param>
    public record Body(string? Kind, int? SectionId, string? Date, string? Start, List<int>? SectionIds, bool? AlsoOther,
        string? NewDate, string? NewStart, string? NewEnd, string? Room, string? Type, string? Note, string? GroupId);

    public const string SwitchedOff = "Class changes are switched off for now.";
    public const string NotYours = "That class isn't on your timetable. Add it under Edit my classes first.";
    public const string NoSession = "This class has no session then.";
    public const string Started = "This class has already started.";
    public const string AlreadyChanged = "This session already has a change. Edit or undo it.";
    public const string Gone = "Someone already changed this. Reload.";
    public const int MaxNote = 200, MaxRoom = 60, MinMinutes = 15, MaxMinutes = 600, MaxDaysAhead = 190, ShortNoticeMinutes = 120, ListDays = 14;

    static readonly TimeSpan Oman = TimeSpan.FromHours(4);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The start of a session as a UTC instant. Every "started" test and every stored expiry uses it.</summary>
    public static DateTimeOffset StartUtc(DateOnly date, TimeOnly start) => new DateTimeOffset(date.ToDateTime(start), Oman).ToUniversalTime();

    static DateTimeOffset OrigStartUtc(ClassChange c) => StartUtc(c.OrigDate!.Value, c.OrigStart!.Value);
    static DateTimeOffset NewStartUtc(ClassChange c) => StartUtc(c.NewDate!.Value, c.NewStart!.Value);

    /// <summary>
    /// Until when a change can be undone: a cancel or a room change before the original start, a move before the
    /// earlier of the old and the new start, an extra class before its start. The same limit applies to staff.
    /// </summary>
    public static bool CanUndo(ClassChange c, DateTimeOffset now) => c.Kind switch
    {
        ClassChanges.Extra => now < NewStartUtc(c),
        ClassChanges.Move => now < OrigStartUtc(c) && now < NewStartUtc(c),
        _ => now < OrigStartUtc(c),
    };

    /// <summary>
    /// Until when a change can be replaced: a cancel or a room change before the original start; a move or an extra
    /// class before the start of the class as it now stands. So a class moved from Sunday to Thursday can still be moved
    /// again or cancelled on Monday, though it can no longer be undone.
    /// </summary>
    public static bool CanEdit(ClassChange c, DateTimeOffset now) =>
        c.Kind is ClassChanges.Move or ClassChanges.Extra ? now < NewStartUtc(c) : now < OrigStartUtc(c);

    /// <summary>The last start a change is about: after it, nothing about the change is news to anyone.</summary>
    public static DateTimeOffset LastStartUtc(ClassChange c) => c.Kind switch
    {
        ClassChanges.Extra => NewStartUtc(c),
        ClassChanges.Move => OrigStartUtc(c) > NewStartUtc(c) ? OrigStartUtc(c) : NewStartUtc(c),
        _ => OrigStartUtc(c),
    };

    /// <summary>Who ended a change, for the log only: never put in a notice.</summary>
    public static string EndedBy(TeachActor actor) =>
        actor.ViewAs || actor.ByKind == "admin" ? $"staff:{actor.ByLabel}" : actor.ByKind == "student" ? $"assistant:{actor.ByLabel}" : $"teacher:{actor.ByLabel}";

    static bool Mine(ClassChange c, TeachActor actor) => c.ByKind == actor.ByKind && c.ById == actor.ById;

    /// <summary>The maker, anyone who may change the section, and an owner looking in may replace or undo a change.</summary>
    static bool MayTouch(ClassChange c, TeachActor actor, IReadOnlySet<int> mayChange) =>
        actor.ViewAs || Mine(c, actor) || (c.SectionId is int id && mayChange.Contains(id));

    /// <summary>Plain text of a limited length: trimmed, with control characters taken out.</summary>
    static string Plain(string? text) => new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

    /// <summary>
    /// A change as the app reads it (the timetable's <c>changes</c>, and every answer here). With a
    /// <paramref name="viewer"/> it also says what that teacher may do with it.
    /// </summary>
    public static Dictionary<string, object?> View(ClassChange c, ChangeSet changes, int courseId, ChangeViewer? viewer)
    {
        var session = changes.BaseOf(c);
        var extra = c.Kind == ClassChanges.Extra;
        static object At(DateOnly date, TimeOnly start, TimeOnly end, string room) =>
            new { date = PlannerEngine.Iso(date), start = Timeslots.Format(start), end = Timeslots.Format(end), room };
        var row = new Dictionary<string, object?>
        {
            ["id"] = c.Id,
            ["group"] = c.GroupId,
            ["kind"] = c.Kind,
            ["courseId"] = courseId,
            ["sectionId"] = c.SectionId,
            ["code"] = c.CourseCode,
            // Sent, not guessed from the room: a lab moved to a typed room stays a lab.
            ["type"] = c.SessionType,
            // The session as the university lists it now (its end and room can change under a change).
            ["from"] = extra ? null : At(c.OrigDate!.Value, c.OrigStart!.Value, session?.End ?? c.OrigEnd ?? c.OrigStart.Value, session?.Room ?? c.OrigRoom ?? ""),
            ["to"] = c.Kind switch
            {
                ClassChanges.Cancel => null,
                // A room change keeps the university's time.
                ClassChanges.Room => At(c.OrigDate!.Value, c.OrigStart!.Value, session?.End ?? c.NewEnd ?? c.OrigStart.Value, c.NewRoom ?? ""),
                _ => At(c.NewDate!.Value, c.NewStart!.Value, c.NewEnd!.Value, c.NewRoom ?? ""),
            },
            ["note"] = c.Note,
            ["by"] = c.ByName,
            ["at"] = c.CreatedAt,
        };
        if (viewer is null) return row;
        var actor = viewer.Actor;
        var may = MayTouch(c, actor, viewer.MayChange) && actor.Profile?.FrozenAt is null;
        row["canUndo"] = may && CanUndo(c, viewer.Now);
        row["canEdit"] = may && (viewer.Enabled || actor.ViewAs) && CanEdit(c, viewer.Now);
        row["mine"] = Mine(c, actor);
        row["flagged"] = c.Flagged;
        return row;
    }

    // ---- What a request means ----

    /// <summary>The timetable a change is checked against, read once inside the write's transaction.</summary>
    sealed record World(Semester Semester, SemesterSpan Span, List<Section> Sections, List<Taught> Taught, ChangeSet Changes)
    {
        public IReadOnlySet<int> MayChange { get; } = Taught.Select(t => t.Section.Id).ToHashSet();
        public Taught? Find(int sectionId) => Taught.FirstOrDefault(t => t.Section.Id == sectionId)
            ?? (Sections.FirstOrDefault(s => s.Id == sectionId) is { } s ? new Taught(s.Course, s, false, false, false) : null);
    }

    static async Task<World> LoadAsync(AppDb db, Semester semester, TeachActor actor, CancellationToken ct)
    {
        var sections = await TeacherSchedule.SemesterSectionsAsync(db, semester.Id, ct);
        return new(semester, await SemesterSpan.ForAsync(db, semester, ct), sections,
            await TeacherSchedule.SectionsAsync(db, semester.Id, actor.Profile, actor.NameKeys, sections, ct),
            await ClassChanges.LoadAsync(db, semester.Id, sections, ct));
    }

    /// <summary>One section of a change: its class, the university session it is about (none for an extra class), and the row being replaced.</summary>
    sealed record Target(Taught Class, Meeting? Session, ClassChange? Old);

    /// <summary>Another session of the same section that day: not started, not changed.</summary>
    sealed record Other(TimeOnly Start, TimeOnly End, string Room);

    /// <summary>What a request asks for, worked out against the timetable: checks 5 to 8 passed.</summary>
    sealed class Draft
    {
        public string Kind = "";
        public string? Note;
        public string Room = "";
        public string SessionType = SessionKinds.Lecture;
        /// <summary>The session as the university lists it (not for an extra class).</summary>
        public Taught? Main;
        public DateOnly? Date;
        public TimeOnly? Start;
        public Meeting? Session;
        /// <summary>The codes that can be ticked, the ones ticked, and those of the same university class that already have their own change.</summary>
        public List<Target> Tickable = [], Ticked = [];
        public List<(Taught Class, string GroupId)> Elsewhere = [];
        /// <summary>The live rows being replaced (none on a create), and those of them whose code was unticked.</summary>
        public List<ClassChange> Replacing = [], Unticked = [];
        public List<Other> Others = [];
        /// <summary>The session, as it now stands, has started (check 9).</summary>
        public bool HasStarted;
        /// <summary>The session already has a live change (check 10): its group.</summary>
        public string? Conflict;
        public DateOnly? NewDate;
        public TimeOnly? NewStart, NewEnd;
        /// <summary>A move or an extra class with no date and time sent yet (a preview before the teacher picked one).</summary>
        public bool NoTimeYet;
        /// <summary>Checks 11 to 14: what is wrong with the new time or room, and the status a write answers with.</summary>
        public string? Problem;
        public int ProblemStatus = StatusCodes.Status400BadRequest;

        public void Fail(string problem, int status = StatusCodes.Status400BadRequest)
        {
            if (Problem is not null) return;
            (Problem, ProblemStatus) = (problem, status);
        }
    }

    static bool TryDate(string? s, out DateOnly date) => PlannerEngine.Date(s?.Trim(), out date) && PlannerEngine.Sane(date);
    static bool TryTime(string? s, out TimeOnly time) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm"], Inv, DateTimeStyles.None, out time);

    /// <summary>Every meeting of the section at that start on that date, first by room: the session a change is keyed on.</summary>
    static List<Meeting> SessionsOf(World w, Section s, DateOnly date, TimeOnly start) =>
        s.Meetings.Where(m => m.Start == start && ChangeSet.Happens(w.Span, m, date)).OrderBy(m => m.Room, StringComparer.Ordinal).ThenBy(m => m.Id).ToList();

    /// <summary>Check 11: the new date and time of a move or an extra class.</summary>
    static void ReadNewTime(Draft d, Body body, TimeSpan? usual, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(body.NewDate) && string.IsNullOrWhiteSpace(body.NewStart))
        {
            d.NoTimeYet = true;
            d.Fail("Pick a date and a time.");
            return;
        }
        if (!TryDate(body.NewDate, out var date) || !TryTime(body.NewStart, out var start))
        {
            d.Fail("Pick a date like 2026-12-02 and a time like 11:30.");
            return;
        }
        TimeOnly end;
        if (!string.IsNullOrWhiteSpace(body.NewEnd))
        {
            if (!TryTime(body.NewEnd, out end))
            {
                d.Fail("Pick an end time like 13:00.");
                return;
            }
        }
        else if (usual is TimeSpan length && start.ToTimeSpan() + length < TimeSpan.FromDays(1)) end = start.Add(length);
        else
        {
            d.Fail("Pick an end time.");
            return;
        }
        (d.NewDate, d.NewStart, d.NewEnd) = (date, start, end);
        var minutes = (end.ToTimeSpan() - start.ToTimeSpan()).TotalMinutes;
        if (minutes <= 0) d.Fail("The class ends before it starts.");
        else if (minutes is < MinMinutes or > MaxMinutes) d.Fail("A class is between 15 minutes and 10 hours long.");
        else if (StartUtc(date, start) <= now) d.Fail("Pick a time that hasn't passed.");
        else if (date > DateOnly.FromDateTime(now.ToOffset(Oman).DateTime).AddDays(MaxDaysAhead)) d.Fail($"Pick a date in the next {MaxDaysAhead} days.");
    }

    /// <summary>
    /// Works out what a request asks for and runs checks 5 to 8 (and 15 for a replacement) as hard refusals. What
    /// comes after them (started, already changed, a bad new time or room) is left on the draft: a write refuses it,
    /// a preview reports it.
    /// </summary>
    /// <param name="groupId">The change being replaced, or null for a new one.</param>
    static (Draft? Draft, IResult? Refusal) Resolve(World w, TeachActor actor, Body body, string? groupId, DateTimeOffset now, bool preview)
    {
        var d = new Draft { Kind = body.Kind?.Trim().ToLowerInvariant() ?? "" };
        if (d.Kind is not (ClassChanges.Cancel or ClassChanges.Move or ClassChanges.Room or ClassChanges.Extra))
            return (null, Errors.BadRequest("Choose cancel, move, room or extra."));
        var note = Plain(body.Note);
        if (note.Length > MaxNote) return (null, Errors.BadRequest($"Keep the note under {MaxNote} characters."));
        d.Note = note.Length == 0 ? null : note;
        d.Room = Plain(body.Room);
        if (d.Room.Length > MaxRoom) return (null, Errors.BadRequest($"Keep the room under {MaxRoom} characters."));
        var type = body.Type?.Trim().ToLowerInvariant();
        var extra = d.Kind == ClassChanges.Extra;
        if (extra && !(SessionKinds.Valid(type) || (groupId is not null && type is null))) return (null, Errors.BadRequest("Pick lecture, lab or tutorial."));
        var asked = body.SectionIds?.Distinct().ToList();

        if (groupId is not null) return Replacement(w, actor, body, d, groupId, asked, type, now);

        // Check 6: every section named must be one the actor may change.
        if ((asked ?? []).Concat(extra || body.SectionId is null ? [] : [body.SectionId.Value]).Any(id => !w.MayChange.Contains(id)))
            return (null, Errors.Forbidden(NotYours));

        if (extra)
        {
            d.SessionType = type!;
            d.Tickable = d.Ticked = (asked ?? []).Select(id => new Target(w.Find(id)!, null, null)).OrderBy(t => t.Class.Course.Code, StringComparer.Ordinal).ToList();
            ReadNewTime(d, body, null, now);
            // Check 14.
            if (d.Ticked.Count == 0) d.Fail("Pick the classes this extra class is for.");
            else if (d.NewDate is not null && w.Changes.All.Any(c => c.Kind == ClassChanges.Extra && c.NewDate == d.NewDate && c.NewStart == d.NewStart
                         && d.Ticked.Any(t => t.Class.Section.Id == c.SectionId)))
                d.Fail("This class already has an extra class then.", StatusCodes.Status409Conflict);
            return (d, null);
        }

        // Check 7: the session exists, as the university lists it.
        if (body.SectionId is not int sectionId || !TryDate(body.Date, out var date) || !TryTime(body.Start, out var start)) return (null, Errors.BadRequest(NoSession));
        var main = w.Find(sectionId)!;
        var sessions = SessionsOf(w, main.Section, date, start);
        if (sessions.Count == 0) return (null, Errors.BadRequest(NoSession));

        if (w.Changes.On(sectionId, date, start) is { } existing)
        {
            // A preview of a session that already has a change is a preview of replacing it.
            if (preview) return Replacement(w, actor, body, d, existing.GroupId, asked, type, now);
            (d.Main, d.Date, d.Start, d.Session, d.Conflict) = (main, date, start, sessions[0], existing.GroupId);
            d.HasStarted = StartUtc(date, start) <= now;
            return (d, null);
        }

        // Check 8: the codes ticked are this session's own section and its siblings. The server decides which
        // sections are one real class; a code that already has its own change is never among them.
        var siblings = TeacherSchedule.Siblings(w.Taught, w.Span, w.Changes, sectionId, date, start);
        d.Tickable = new[] { main }.Concat(siblings).OrderBy(t => t.Course.Code, StringComparer.Ordinal).ThenBy(t => t.Section.Id)
            .Select(t => new Target(t, SessionsOf(w, t.Section, date, start).FirstOrDefault(), null)).Where(t => t.Session is not null).ToList();
        if (asked is not null && asked.Any(id => !d.Tickable.Any(t => t.Class.Section.Id == id)))
            return (null, Errors.BadRequest("Those classes aren't the same class as this session."));
        d.Ticked = asked is null ? d.Tickable : d.Tickable.Where(t => asked.Contains(t.Class.Section.Id)).ToList();
        if (d.Ticked.Count == 0) return (null, Errors.BadRequest("Tick at least one class."));
        d.Elsewhere = TeacherSchedule.Siblings(w.Taught, w.Span, sectionId, date, start)
            .Where(t => w.Changes.On(t.Section.Id, date, start) is not null).Select(t => (t, w.Changes.On(t.Section.Id, date, start)!.GroupId)).ToList();

        (d.Main, d.Date, d.Start, d.Session) = (main, date, start, sessions[0]);
        d.SessionType = SessionKinds.Of(sessions[0].Room);
        d.HasStarted = StartUtc(date, start) <= now;
        d.Others = OthersOf(w, main.Section, date, start, now);
        NewValues(d, body, sessions, date, start, now);
        return (d, null);
    }

    /// <summary>Every other session of the section that day that hasn't started and has no change.</summary>
    static List<Other> OthersOf(World w, Section s, DateOnly date, TimeOnly start, DateTimeOffset now) =>
        s.Meetings.Where(m => m.Start != start && ChangeSet.Happens(w.Span, m, date) && StartUtc(date, m.Start) > now && w.Changes.On(s.Id, date, m.Start) is null)
            .OrderBy(m => m.Start).ThenBy(m => m.Room, StringComparer.Ordinal).DistinctBy(m => m.Start).Select(m => new Other(m.Start, m.End, m.Room)).ToList();

    /// <summary>Checks 11 to 13 for a cancel, move or room change of the session at (<paramref name="date"/>, <paramref name="start"/>).</summary>
    static void NewValues(Draft d, Body body, List<Meeting> sessions, DateOnly date, TimeOnly start, DateTimeOffset now)
    {
        switch (d.Kind)
        {
            case ClassChanges.Move:
                ReadNewTime(d, body, sessions[0].End.ToTimeSpan() - sessions[0].Start.ToTimeSpan(), now);
                if (d.NewDate == date && d.NewStart == start) d.Fail("Pick a different time, or use Change room.");
                break;
            case ClassChanges.Room:
                (d.NewDate, d.NewStart, d.NewEnd) = (date, sessions[0].Start, sessions[0].End);
                if (sessions.Count == 1 && ClassChanges.RoomKey(d.Room) == ClassChanges.RoomKey(sessions[0].Room)) d.Fail("That's the room it's already in.");
                break;
        }
    }

    /// <summary>The draft of replacing a group (the rows of it the actor may touch): the session is the group's own, and codes may be unticked.</summary>
    static (Draft? Draft, IResult? Refusal) Replacement(World w, TeachActor actor, Body body, Draft d, string groupId, List<int>? asked, string? type, DateTimeOffset now)
    {
        // Check 15.
        var group = w.Changes.All.Where(c => c.GroupId == groupId).OrderBy(c => c.CourseCode, StringComparer.Ordinal).ThenBy(c => c.SectionId).ToList();
        if (group.Count == 0) return (null, Errors.Conflict(Gone));
        // Rights are per section: someone who may change only some of the group's classes edits those, and the rest
        // of the change stays as it is.
        var rows = group.Where(c => MayTouch(c, actor, w.MayChange)).ToList();
        if (rows.Count == 0) return (null, Errors.Forbidden(NotYours));
        var was = rows[0];
        var extra = was.Kind == ClassChanges.Extra;
        if (extra != (d.Kind == ClassChanges.Extra))
            return (null, Errors.BadRequest(extra ? "An extra class can only be edited or removed." : "Add an extra class from the timetable instead."));
        if (asked is not null && asked.Any(id => !rows.Any(c => c.SectionId == id))) return (null, Errors.BadRequest("Those classes aren't part of this change."));

        d.Replacing = rows;
        d.Tickable = rows.Select(c => new Target(w.Find(c.SectionId!.Value)!, w.Changes.BaseOf(c), c)).ToList();
        d.Ticked = asked is null ? d.Tickable : d.Tickable.Where(t => asked.Contains(t.Class.Section.Id)).ToList();
        if (d.Ticked.Count == 0) return (null, Errors.BadRequest("Tick at least one class, or undo the change."));
        d.Unticked = d.Tickable.Except(d.Ticked).Select(t => t.Old!).ToList();
        // Unticking a code puts its class back on, which is an undo.
        if (d.Unticked.Any(c => !CanUndo(c, now))) return (null, Errors.BadRequest("A class that has started can't be taken out of this change."));
        d.SessionType = extra && type is not null ? type : was.SessionType;
        d.HasStarted = rows.Any(c => !CanEdit(c, now));
        if (extra)
        {
            ReadNewTime(d, body, null, now);
            if (d.NewDate is not null && w.Changes.All.Any(c => c.Kind == ClassChanges.Extra && c.GroupId != groupId && c.NewDate == d.NewDate && c.NewStart == d.NewStart
                    && d.Ticked.Any(t => t.Class.Section.Id == c.SectionId)))
                d.Fail("This class already has an extra class then.", StatusCodes.Status409Conflict);
            return (d, null);
        }

        var lead = d.Ticked[0];
        var (date, start) = (was.OrigDate!.Value, was.OrigStart!.Value);
        // The university timetable no longer has the session (nothing shows this change any more): it can only be undone.
        if (d.Ticked.Any(t => t.Session is null)) return (null, Errors.Conflict("The university timetable changed this class. Undo this change instead."));
        (d.Main, d.Date, d.Start, d.Session) = (lead.Class, date, start, lead.Session);
        // Once the original time of a moved class has passed, it can move again or be cancelled, but never go back
        // to a room change: that would put the class at a time that has gone.
        if (d.Kind == ClassChanges.Room && StartUtc(date, start) <= now) d.Fail("The original time has passed. Move the class again or cancel it.");
        NewValues(d, body, SessionsOf(w, lead.Class.Section, date, start), date, start, now);
        return (d, null);
    }

    // ---- Preview ----

    /// <summary>
    /// How many Classy users the ticked sections have, each counted once, and how many of them have another class at
    /// the new time. Classes only, never their private own slots. Only integers leave this function.
    /// </summary>
    /// <param name="at">The new time, or null for the size alone.</param>
    /// <param name="original">The session being changed, which is not a clash with itself.</param>
    /// <param name="replacing">The ids of the changes being replaced: what they put on is not a clash either.</param>
    public static async Task<(int Size, int Clash)> NumbersAsync(AppDb db, int semesterId, IReadOnlyCollection<int> sectionIds,
        (DateOnly Date, TimeOnly Start, TimeOnly End)? at, (DateOnly Date, TimeOnly Start)? original, IReadOnlySet<int> replacing, bool planner, CancellationToken ct)
    {
        var students = sectionIds.Count == 0 ? [] : await db.Picks.Where(p => p.SectionId != null && sectionIds.Contains(p.SectionId.Value))
            .Select(p => p.StudentId).Distinct().ToListAsync(ct);
        if (students.Count == 0 || at is not { } slot) return (students.Count, 0);
        var semester = await db.Semesters.AsNoTracking().FirstAsync(s => s.Id == semesterId, ct);
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var items = await StudentSchedule.ForAsync(db, semesterId, students, slots: false, planner, ct);
        bool Same(StudentSchedule.Item i) => i.ChangeId is int id ? replacing.Contains(id)
            : original is { } o && slot.Date == o.Date && i.Start == o.Start && i.SectionId is int section && sectionIds.Contains(section);
        var clash = items.Where(i => i.Start < slot.End && slot.Start < i.End && span.Happens(i, slot.Date) && !Same(i))
            .Select(i => i.StudentId).Distinct().Count();
        return (students.Count, clash);
    }

    static string Describe(ClassChange c) => c.Kind switch
    {
        ClassChanges.Cancel => $"Cancelled by {c.ByName}",
        ClassChanges.Move => $"Moved to {TeachNotices.When(c.NewDate!.Value, c.NewStart!.Value)} by {c.ByName}",
        ClassChanges.Room => $"Room changed to {(string.IsNullOrWhiteSpace(c.NewRoom) ? "be announced" : c.NewRoom)} by {c.ByName}",
        _ => $"Extra class added by {c.ByName}",
    };

    /// <summary>
    /// The numbers and warnings for a change, saving nothing. It refuses what a write would for the things that guard
    /// numbers (a frozen account, a section the actor may not change), and reports the rest.
    /// </summary>
    public static async Task<IResult> PreviewAsync(AppDb db, TeachActor actor, Body body, DateTimeOffset now, bool codeFresh, IConfiguration config, CancellationToken ct)
    {
        if (actor.Profile?.FrozenAt is not null) return Errors.Forbidden(TeachActor.Frozen);
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        if (semester is null) return Errors.Conflict("No semester has been published yet.");
        var w = await LoadAsync(db, semester, actor, ct);
        var (d, refusal) = Resolve(w, actor, body, string.IsNullOrWhiteSpace(body.GroupId) ? null : body.GroupId.Trim(), now, preview: true);
        if (d is null) return refusal!;

        var planner = await PlannerEngine.Enabled(db, config, ct);
        var ticked = d.Ticked.Select(t => t.Class.Section.Id).ToList();
        var replacing = d.Replacing.Select(c => c.Id).ToHashSet();
        var moves = d.Kind is ClassChanges.Move or ClassChanges.Extra;
        // The slot the class would take: a move's or an extra class's new time, a room change's own time.
        (DateOnly Date, TimeOnly Start, TimeOnly End)? slot = d.Problem is null && d.NewDate is DateOnly on ? (on, d.NewStart!.Value, d.NewEnd!.Value) : null;
        (DateOnly Date, TimeOnly Start)? original = d.Date is DateOnly date ? (date, d.Start!.Value) : null;
        // Numbers are only ever given for sections on the actor's own timetable (the maker of a change may have taken
        // its class off their list since).
        var counted = ticked.Where(w.MayChange.Contains).ToList();
        var (size, clash) = await NumbersAsync(db, semester.Id, counted, moves ? slot : null, original, replacing, planner, ct);
        var sizes = await TeacherSchedule.SizesAsync(db, d.Tickable.Select(t => t.Class.Section.Id).Concat(d.Elsewhere.Select(e => e.Class.Section.Id)).Where(w.MayChange.Contains).ToList(), ct);

        var warnings = new List<object>();
        void Warn(string code, string text) => warnings.Add(new { code, text });
        if (d.Replacing.Count > 0) Warn("replaces", $"This replaces: {Describe(d.Replacing[0])}.");
        if (slot is { } s)
        {
            if (moves && clash > 0) Warn("clash", $"{clash} of {size} Classy users have another class then.");
            if (d.Kind != ClassChanges.Cancel && !FreeRooms.NotARoom(d.Room))
            {
                var key = ClassChanges.RoomKey(d.Room);
                // The class being changed doesn't take its own room from itself.
                bool Own(FreeRooms.Occupied o) => o.SectionId is int id && ticked.Contains(id)
                    && (o.Change is { } c ? replacing.Contains(c.Id) : original is { } from && s.Date == from.Date && o.Start == from.Start);
                var taken = (await FreeRooms.OccupiedAsync(db, semester, s.Date, config, ct)).Occupied
                    .Where(o => ClassChanges.RoomKey(o.Room) == key && o.Start < s.End && s.Start < o.End && !Own(o)).OrderBy(o => o.Start).FirstOrDefault();
                if (taken is not null) Warn("room-taken", $"{d.Room} is taken then by {taken.Label} (until {Timeslots.Format(taken.End)}).");
            }
            if (moves)
            {
                if (s.Start.ToTimeSpan().TotalMinutes < Busy.DayStart || s.End.ToTimeSpan().TotalMinutes > Busy.DayEnd) Warn("outside-hours", "That's outside 08:00–18:15.");
                if (s.Date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday) Warn("weekend", $"That's a {s.Date.DayOfWeek}.");
                if (w.Span.DaysOff.TryGetValue(s.Date, out var dayOff)) Warn("day-off", $"{s.Date.ToString("d MMM", Inv)} is {dayOff}, a day off.");
                if (s.Date < w.Span.First || s.Date > w.Span.Last) Warn("outside-weeks", "That's outside the teaching weeks.");
                // A session of a ticked class that is still on at that date and start: one the university lists and
                // no change skips, or one a change put there.
                if (d.Ticked.Any(t => t.Class.Section.Meetings.Any(m => m.Start == s.Start && ChangeSet.Happens(w.Span, m, s.Date) && !w.Changes.Skip(t.Class.Section, m).Contains(s.Date))
                        || w.Changes.AddedTo(t.Class.Section).Any(a => a.Date == s.Date && a.Start == s.Start && !replacing.Contains(a.Change.Id))))
                    Warn("same-class", "This class already has a session then.");
            }
        }
        // The earliest start this touches, as the class stands now or at its new time.
        var starts = new List<DateTimeOffset>();
        if (d.Replacing.Count > 0) starts.Add(d.Replacing[0].Kind is ClassChanges.Move or ClassChanges.Extra ? NewStartUtc(d.Replacing[0]) : OrigStartUtc(d.Replacing[0]));
        else if (original is { } o) starts.Add(StartUtc(o.Date, o.Start));
        if (moves && slot is { } to) starts.Add(StartUtc(to.Date, to.Start));
        if (starts.Where(x => x > now).Select(x => (x - now).TotalMinutes).DefaultIfEmpty(double.MaxValue).Min() is var minutes and < ShortNoticeMinutes)
            Warn("short-notice", $"This starts in {Math.Max(1, (int)Math.Ceiling(minutes))} minutes; students may not see it in time.");
        var keys = actor.NameKeys.Where(k => k.Length > 0).ToHashSet();
        if (d.Ticked.Select(t => t.Class.Section.Instructor.Trim()).FirstOrDefault(i => !keys.Contains(TeacherNames.NameKey(i))) is { } named)
            Warn("not-yours", named.Length == 0 ? "This class isn't listed under your name." : $"Listed under {named}. They'll be told.");

        object Code(Taught t, string? changedBy) => new
        {
            sectionId = t.Section.Id, code = t.Course.Code, number = t.Section.Number, students = sizes.GetValueOrDefault(t.Section.Id),
            named = t.Section.Instructor, yours = keys.Contains(TeacherNames.NameKey(t.Section.Instructor)), changedBy,
        };
        return Results.Ok(new
        {
            codes = d.Tickable.Select(t => Code(t.Class, null)).Concat(d.Elsewhere.Select(e => Code(e.Class, e.GroupId))),
            size,
            others = d.Others.Select(x => new { start = Timeslots.Format(x.Start), end = Timeslots.Format(x.End), room = x.Room }),
            needsCode = !codeFresh,
            blocked = d.HasStarted ? "started" : !actor.ViewAs && !await ClassChanges.EnabledAsync(db, ct) ? "switched-off" : null,
            // What a write would refuse about the new time or room, in words; null when nothing or when no time was picked yet.
            problem = d.NoTimeYet ? null : d.Problem,
            warnings,
        });
    }

    // ---- Writes: each runs inside the caller's transaction, which was opened before anything was read ----

    static ClassChange Row(World w, TeachActor actor, Draft d, string groupId, Taught t, Meeting? session, DateOnly? date,
        DateOnly? newDate, TimeOnly? newStart, TimeOnly? newEnd, string? room, DateTimeOffset now)
    {
        var keys = actor.NameKeys.Where(k => k.Length > 0).ToHashSet();
        return new ClassChange
        {
            SemesterId = w.Semester.Id, SectionId = t.Section.Id, CourseCode = t.Course.Code, CourseName = t.Course.Name, SectionNumber = t.Section.Number,
            GroupId = groupId, Kind = d.Kind,
            SessionType = session is null ? d.SessionType : SessionKinds.Of(session.Room),
            OrigDate = session is null ? null : date, OrigStart = session?.Start, OrigEnd = session?.End, OrigRoom = session?.Room,
            NewDate = newDate, NewStart = newStart, NewEnd = newEnd, NewRoom = room, Note = d.Note,
            Tag = session is null ? TeachNotices.Tag(t.Section.Id, newDate!.Value, newStart!.Value, true) : TeachNotices.Tag(t.Section.Id, date!.Value, session.Start, false),
            TeacherId = actor.Profile?.Id, ByKind = actor.ByKind, ById = actor.ById, ByLabel = actor.ByLabel, ByName = actor.ByName, OnBehalfOf = actor.OnBehalfOf,
            NamedTeacher = t.Section.Instructor,
            // Flagged for staff: the maker isn't the class's named teacher, or it was made through "view as".
            Flagged = actor.ViewAs || !keys.Contains(TeacherNames.NameKey(t.Section.Instructor)),
            CreatedAt = now,
        };
    }

    static IResult Refuse(Draft d) => d.ProblemStatus == StatusCodes.Status409Conflict ? Errors.Conflict(d.Problem!) : Errors.BadRequest(d.Problem!);

    /// <summary>The account that acted and so isn't told about its own action; an owner looking in is nobody's account.</summary>
    static int? Acting(TeachActor actor) => actor.ViewAs ? null : actor.Profile?.Id;

    static async Task<IResult> AnswerAsync(AppDb db, World w, TeachActor actor, IReadOnlyCollection<string> groups, DateTimeOffset now, CancellationToken ct)
    {
        var changes = await ClassChanges.LoadAsync(db, w.Semester.Id, w.Sections, ct);
        var viewer = new ChangeViewer(actor, await ClassChanges.EnabledAsync(db, ct), now, w.MayChange);
        return Results.Ok(new
        {
            changes = changes.Shown(w.Sections).Where(c => groups.Contains(c.GroupId))
                .Select(c => View(c, changes, w.Sections.First(s => s.Id == c.SectionId).CourseId, viewer)).ToList(),
        });
    }

    /// <summary>Checks 2 to 4, shared by create and replace. Check 1 (a fresh code) is the endpoint's.</summary>
    static async Task<(Semester? Semester, IResult? Refusal)> MayWriteAsync(AppDb db, TeachActor actor, CancellationToken ct)
    {
        // Owners looking in pass the switch, as they pass the planner switch, so the flow can be tried while it is off.
        if (!actor.ViewAs && !await ClassChanges.EnabledAsync(db, ct)) return (null, Errors.Conflict(SwitchedOff));
        if (actor.Profile?.FrozenAt is not null) return (null, Errors.Forbidden(TeachActor.Frozen));
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        return semester is null ? (null, Errors.Conflict("No semester has been published yet.")) : (semester, null);
    }

    /// <summary>
    /// Makes a change: one row per ticked section, sharing a group, and their notices. With <c>alsoOther</c>, one more
    /// group for each other session of the section that day. The answer lists every change made.
    /// </summary>
    public static async Task<IResult> CreateAsync(AppDb db, TeachActor actor, Body body, DateTimeOffset now, CancellationToken ct)
    {
        var (semester, refusal) = await MayWriteAsync(db, actor, ct);
        if (semester is null) return refusal!;
        var w = await LoadAsync(db, semester, actor, ct);
        (var d, refusal) = Resolve(w, actor, body, null, now, preview: false);
        if (d is null) return refusal!;
        if (d.HasStarted) return Errors.BadRequest(Started);
        if (d.Conflict is not null) return Results.Json(new { error = AlreadyChanged, groupId = d.Conflict }, statusCode: StatusCodes.Status409Conflict);
        if (d.Problem is not null) return Refuse(d);

        var groups = new List<List<ClassChange>>();
        void Group(IEnumerable<ClassChange> rows) => groups.Add(rows.ToList());
        string NewGroup() => Guid.NewGuid().ToString("N");
        if (d.Kind == ClassChanges.Extra)
        {
            var id = NewGroup();
            Group(d.Ticked.Select(t => Row(w, actor, d, id, t.Class, null, null, d.NewDate, d.NewStart, d.NewEnd, d.Room, now)));
        }
        else
        {
            var (date, start) = (d.Date!.Value, d.Start!.Value);
            var id = NewGroup();
            Group(d.Ticked.Select(t => d.Kind switch
            {
                ClassChanges.Cancel => Row(w, actor, d, id, t.Class, t.Session, date, null, null, null, null, now),
                ClassChanges.Room => Row(w, actor, d, id, t.Class, t.Session, date, date, t.Session!.Start, t.Session.End, d.Room, now),
                _ => Row(w, actor, d, id, t.Class, t.Session, date, d.NewDate, d.NewStart, d.NewEnd, d.Room, now),
            }));

            if (body.AlsoOther == true)
            {
                // Every other session that day, of every ticked section, that hasn't started and has no change. The
                // sections that share one of them (one real class) change it together, as one group.
                var days = d.Kind == ClassChanges.Move ? d.NewDate!.Value.DayNumber - date.DayNumber : 0;
                var shift = d.Kind == ClassChanges.Move ? d.NewStart!.Value.ToTimeSpan() - start.ToTimeSpan() : TimeSpan.Zero;
                var pending = d.Ticked.SelectMany(t => OthersOf(w, t.Class.Section, date, start, now).Select(o => (t.Class, o.Start)))
                    .OrderBy(x => x.Start).ThenBy(x => x.Class.Course.Code, StringComparer.Ordinal).ToList();
                while (pending.Count > 0)
                {
                    var (first, at) = pending[0];
                    var together = TeacherSchedule.Siblings(w.Taught, w.Span, w.Changes, first.Section.Id, date, at).Select(t => t.Section.Id).Append(first.Section.Id).ToHashSet();
                    var members = pending.Where(x => x.Start == at && together.Contains(x.Class.Section.Id)).ToList();
                    pending.RemoveAll(members.Contains);
                    var other = NewGroup();
                    var rows = new List<ClassChange>();
                    foreach (var (t, _) in members)
                    {
                        var session = SessionsOf(w, t.Section, date, at)[0];
                        if (d.Kind == ClassChanges.Move)
                        {
                            var (from, to) = (session.Start.ToTimeSpan() + shift, session.End.ToTimeSpan() + shift);
                            if (from < TimeSpan.Zero || to >= TimeSpan.FromDays(1)) return Errors.BadRequest("The other session that day can't be moved the same way: it would run past midnight.");
                            if (StartUtc(date.AddDays(days), TimeOnly.FromTimeSpan(from)) <= now) return Errors.BadRequest("The other session that day can't be moved the same way: its new time has passed.");
                            // It keeps its own room, shifted by the same number of days and minutes.
                            rows.Add(Row(w, actor, d, other, t, session, date, date.AddDays(days), TimeOnly.FromTimeSpan(from), TimeOnly.FromTimeSpan(to), session.Room, now));
                        }
                        else if (d.Kind == ClassChanges.Room) rows.Add(Row(w, actor, d, other, t, session, date, date, session.Start, session.End, d.Room, now));
                        else rows.Add(Row(w, actor, d, other, t, session, date, null, null, null, null, now));
                    }
                    Group(rows);
                }
            }
        }

        foreach (var rows in groups)
        {
            db.ClassChanges.AddRange(rows);
            await TeachNotices.AddChangeAsync(db, semester.Id, rows.Select(c => (c, w.Find(c.SectionId!.Value)!.Course.Id)).ToList(),
                new Dictionary<int, ClassChange>(), Acting(actor), now, ct);
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The index that allows one live change per session said no. Nothing of this request stays.
            await db.Database.RollbackTransactionAsync(ct);
            return Errors.Conflict(AlreadyChanged);
        }
        return await AnswerAsync(db, w, actor, groups.Select(g => g[0].GroupId).ToList(), now, ct);
    }

    /// <summary>
    /// Edits a change without undoing it first: each section still ticked gets a new row in a new group (the old one
    /// is kept, ended as "replaced"), and an unticked one is back on. Students get one notification with the new details.
    /// </summary>
    public static async Task<IResult> ReplaceAsync(AppDb db, TeachActor actor, string groupId, Body body, DateTimeOffset now, CancellationToken ct)
    {
        var (semester, refusal) = await MayWriteAsync(db, actor, ct);
        if (semester is null) return refusal!;
        var w = await LoadAsync(db, semester, actor, ct);
        (var d, refusal) = Resolve(w, actor, body, groupId, now, preview: false);
        if (d is null) return refusal!;
        if (d.HasStarted) return Errors.BadRequest(Started);
        if (d.Problem is not null) return Refuse(d);

        // The same rows, as rows this request may edit.
        var ids = d.Replacing.Select(c => c.Id).ToList();
        var live = await db.ClassChanges.Where(c => ids.Contains(c.Id) && c.EndedAt == null).ToListAsync(ct);
        if (live.Count != ids.Count) return Errors.Conflict(Gone);
        var by = EndedBy(actor);
        var unticked = d.Unticked.Select(c => c.Id).ToHashSet();
        await EndAsync(db, live.Where(c => unticked.Contains(c.Id)).ToList(), "undone", by, now, ct, actor.ByName, Acting(actor));
        await EndAsync(db, live.Where(c => !unticked.Contains(c.Id)).ToList(), "replaced", by, now, ct);
        // The old rows stop being live before the new ones take their place on the session.
        await db.SaveChangesAsync(ct);

        var group = Guid.NewGuid().ToString("N");
        var rows = new List<ClassChange>();
        var previous = new Dictionary<int, ClassChange>();
        foreach (var t in d.Ticked)
        {
            var old = t.Old!;
            var row = d.Kind switch
            {
                ClassChanges.Extra => Row(w, actor, d, group, t.Class, null, null, d.NewDate, d.NewStart, d.NewEnd, d.Room, now),
                ClassChanges.Cancel => Row(w, actor, d, group, t.Class, t.Session, old.OrigDate, null, null, null, null, now),
                ClassChanges.Room => Row(w, actor, d, group, t.Class, t.Session, old.OrigDate, old.OrigDate, t.Session!.Start, t.Session.End, d.Room, now),
                _ => Row(w, actor, d, group, t.Class, t.Session, old.OrigDate, d.NewDate, d.NewStart, d.NewEnd, d.Room, now),
            };
            // Still the same session on students' phones, and still the kind of class it was.
            (row.Tag, row.SessionType) = (old.Tag, d.Kind == ClassChanges.Extra ? d.SessionType : old.SessionType);
            rows.Add(row);
            previous[t.Class.Section.Id] = old;
        }
        db.ClassChanges.AddRange(rows);
        await TeachNotices.AddChangeAsync(db, semester.Id, rows.Select(c => (c, w.Find(c.SectionId!.Value)!.Course.Id)).ToList(), previous, Acting(actor), now, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Nothing of this request stays: the old rows are live again.
            await db.Database.RollbackTransactionAsync(ct);
            return Errors.Conflict(AlreadyChanged);
        }
        return await AnswerAsync(db, w, actor, [group], now, ct);
    }

    /// <summary>
    /// Undoes a change: every live row of the group (that the actor may touch) ends, and the class is as the
    /// university lists it again. Works while changes are switched off (it only restores the timetable), but not for
    /// a frozen account.
    /// </summary>
    public static async Task<IResult> UndoAsync(AppDb db, TeachActor actor, string groupId, DateTimeOffset now, CancellationToken ct)
    {
        if (actor.Profile?.FrozenAt is not null) return Errors.Forbidden(TeachActor.Frozen);
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        if (semester is null) return Errors.Conflict("No semester has been published yet.");
        var group = await db.ClassChanges.Where(c => c.GroupId == groupId && c.SemesterId == semester.Id && c.EndedAt == null).ToListAsync(ct);
        // A second tap finds nothing left to undo, and tells nobody.
        if (group.Count == 0) return Errors.Conflict(Gone);
        var mayChange = (await TeacherSchedule.SectionsAsync(db, semester.Id, actor, ct)).Select(t => t.Section.Id).ToHashSet();
        // Rights are per section: the classes of the group the actor may touch are put back; any others stay changed.
        var live = group.Where(c => MayTouch(c, actor, mayChange)).ToList();
        if (live.Count == 0) return Errors.Forbidden(NotYours);
        if (live.Any(c => !CanUndo(c, now))) return Errors.BadRequest(Started);
        await EndAsync(db, live, "undone", EndedBy(actor), now, ct, actor.ByName, Acting(actor));
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { changes = Array.Empty<object>() });
    }

    /// <summary>
    /// Ends changes. The only way a change ends: teachers, staff, teacher removal and the university timetable all
    /// come through here. It always first removes the notices to students about those sessions that nobody has
    /// started sending, so a change that ends before it was announced is never announced afterwards. For the three
    /// undo reasons (undone, undone-by-staff, undone-on-remove) it then writes "back on" to the students who were
    /// told, and "undone" to the teachers; for every other reason (replaced, merged, dropped-…) it writes nothing, and
    /// the caller writes the notices that are due. The caller saves, inside its own transaction.
    /// </summary>
    /// <param name="by">For the log: teacher:{email}, assistant:{university ID}, staff:{name} or system.</param>
    /// <param name="byName">Who did it as people see them, for the notices of an undo. Left out: "Classy staff".</param>
    /// <param name="actingTeacherId">The teaching account that did it, which isn't told about its own undo.</param>
    public static async Task EndAsync(AppDb db, IReadOnlyCollection<ClassChange> rows, string reason, string by, DateTimeOffset now,
        CancellationToken ct = default, string? byName = null, int? actingTeacherId = null)
    {
        var live = rows.Where(c => c.EndedAt is null).ToList();
        if (live.Count == 0) return;
        await TeachNotices.ClearUnsentAsync(db, live, ct);
        if (reason is "undone" or "undone-by-staff" or "undone-on-remove")
            await TeachNotices.AddUndoneAsync(db, live, byName ?? TeachActor.StaffName, actingTeacherId, now, ct);
        foreach (var c in live) (c.EndedAt, c.EndedBy, c.EndReason) = (now, by, reason);
    }

    /// <summary>
    /// The acting teacher's list: the live changes they made, or on sections they may change, from the last 14 days on.
    /// </summary>
    public static async Task<IResult> ListAsync(AppDb db, TeachActor actor, DateTimeOffset now, CancellationToken ct)
    {
        var semester = await StudentEndpoints.CurrentSemester(db, ct);
        if (semester is null) return Results.Ok(new { changes = Array.Empty<object>() });
        var w = await LoadAsync(db, semester, actor, ct);
        var since = DateOnly.FromDateTime(now.ToOffset(Oman).DateTime).AddDays(-ListDays);
        static DateOnly Last(ClassChange c) => c.NewDate is DateOnly to && (c.OrigDate is not DateOnly from || to > from) ? to : c.OrigDate!.Value;
        var viewer = new ChangeViewer(actor, await ClassChanges.EnabledAsync(db, ct), now, w.MayChange);
        return Results.Ok(new
        {
            changes = w.Changes.Shown(w.Sections)
                .Where(c => (Mine(c, actor) || w.MayChange.Contains(c.SectionId!.Value)) && Last(c) >= since)
                .OrderBy(c => c.Kind is ClassChanges.Move or ClassChanges.Extra ? NewStartUtc(c) : OrigStartUtc(c)).ThenBy(c => c.CourseCode, StringComparer.Ordinal).ThenBy(c => c.Id)
                .Select(c => View(c, w.Changes, w.Sections.First(s => s.Id == c.SectionId).CourseId, viewer)).ToList(),
        });
    }
}
