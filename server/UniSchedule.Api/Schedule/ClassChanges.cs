using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Teachers;

namespace UniSchedule.Api.Schedule;

/// <summary>
/// Teachers' changes to single sessions (cancelled, moved, another room, an extra class), read in one place. Every
/// surface that shows a timetable (the home, reminders, calendar feeds, groups, absences, free rooms) asks the
/// <see cref="ChangeSet"/> made here, so they can't disagree about what is on.
/// </summary>
public static class ClassChanges
{
    /// <summary>The all-changes-off switch in <c>AppSettings</c>: "true" or "false". Missing = on.</summary>
    public const string EnabledSetting = "teachers.changes.enabled";
    public const string Cancel = "cancel", Move = "move", Room = "room", Extra = "extra";

    /// <summary>A room as it is compared everywhere in the teacher features: "A 101" is "a101".</summary>
    public static string RoomKey(string? room) => string.Concat((room ?? "").Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();

    /// <summary>Whether teachers may change classes right now (owners can switch it all off).</summary>
    public static async Task<bool> EnabledAsync(AppDb db, CancellationToken ct) =>
        await db.AppSettings.Where(x => x.Key == EnabledSetting).Select(x => x.Value).FirstOrDefaultAsync(ct) != "false";

    /// <summary>The live changes of these sections (meetings loaded), ready to apply. One query; normally finds nothing.</summary>
    public static async Task<ChangeSet> LoadAsync(AppDb db, int semesterId, IReadOnlyCollection<Section> sections, CancellationToken ct)
    {
        if (sections.Count == 0) return ChangeSet.None;
        // Read as stored, never from rows a request is still editing.
        var live = await db.ClassChanges.AsNoTracking().Where(c => c.SemesterId == semesterId && c.EndedAt == null).ToListAsync(ct);
        if (live.Count == 0) return ChangeSet.None;
        var ids = sections.Select(s => s.Id).ToHashSet();
        var mine = live.Where(c => c.SectionId is int id && ids.Contains(id)).ToList();
        if (mine.Count == 0) return ChangeSet.None;
        var semester = await db.Semesters.AsNoTracking().FirstAsync(s => s.Id == semesterId, ct);
        return new ChangeSet(mine, sections, await SemesterSpan.ForAsync(db, semester, ct));
    }

    /// <summary>
    /// The calendar id of a university session, exactly as the calendar feed writes it: one id for a dated class, one
    /// per date for a weekly one. A moved or re-roomed class keeps it, so calendar apps move the entry in place.
    /// </summary>
    public static string Uid(int sectionId, Meeting m, DateOnly date) => m.Date is null
        ? string.Create(CultureInfo.InvariantCulture, $"class-{sectionId}-{m.Id}-{date:yyyyMMdd}@classy")
        : string.Create(CultureInfo.InvariantCulture, $"class-{sectionId}-{m.Id}@classy");

    /// <summary>The calendar id of a session a change put on.</summary>
    public static string Uid(Added a) => a.Base is not null && a.Change is { SectionId: int section, OrigDate: DateOnly on }
        ? Uid(section, a.Base, on)
        : string.Create(CultureInfo.InvariantCulture, $"change-{a.Change.Id}@classy");

    // ---- When the university timetable changes under a teacher's change ----

    /// <summary>Why a change ended, or what its teacher is told, when the timetable changed under it.</summary>
    public const string Merged = "merged", DroppedMoved = "dropped-moved", DroppedRemoved = "dropped-removed", DroppedDayOff = "dropped-day-off",
        RoomDiffers = "room-differs", Dropped = "dropped";
    /// <summary>Who ended a change the timetable ended (an import, a section edit, a day off, a weeks edit).</summary>
    public const string BySystem = "system";

    static readonly TimeSpan Oman = TimeSpan.FromHours(4);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static bool On(ParsedMeeting m, DateOnly date, SemesterSpan span) => m.Date == date || (m.Date is null && span.Weekly(m.Day, date, true));

    /// <summary>The meetings a change is about in a meeting list (the base of 6.1): same start, on its date. Room and end are never part of it.</summary>
    static List<ParsedMeeting> BasesOf(ClassChange c, IEnumerable<ParsedMeeting> meetings, SemesterSpan span) =>
        c.Kind != Extra && c is { OrigDate: DateOnly on, OrigStart: TimeOnly at }
            ? meetings.Where(m => m.Start == at && On(m, on, span)).OrderBy(m => m.Room, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>The meetings at the time a move or an extra class put the class: the university lists that time too.</summary>
    static List<ParsedMeeting> AdoptedOf(ClassChange c, IEnumerable<ParsedMeeting> meetings, SemesterSpan span) =>
        c.Kind is Move or Extra && c is { NewDate: DateOnly on, NewStart: TimeOnly at }
            ? meetings.Where(m => m.Start == at && On(m, on, span)).OrderBy(m => m.Room, StringComparer.Ordinal).ToList()
            : [];

    /// <summary>The university's session is what the teacher made: the same end, and the same room (or the teacher's is still to be announced).</summary>
    static bool SameAs(ClassChange c, ParsedMeeting m) =>
        m.End == c.NewEnd && (string.IsNullOrWhiteSpace(c.NewRoom) || RoomKey(m.Room) == RoomKey(c.NewRoom));

    /// <summary>The last date a change is about. Changes that are over are history: nothing decides them again.</summary>
    public static DateOnly Latest(ClassChange c) =>
        c.NewDate is DateOnly to && (c.OrigDate is not DateOnly from || to > from) ? to : c.OrigDate ?? DateOnly.MinValue;

    static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(Oman).DateTime);

    /// <summary>
    /// What a new meeting list does to one live change. Pure: saves nothing. A room-only, end-time-only or
    /// instructor-only change by the university never drops a teacher's change, because none of those is in the key.
    /// The reason is how the change ends (merged, dropped-moved), or "room-differs" when it stays and its teacher is
    /// told that the university now lists another room.
    /// </summary>
    public static (Outcome What, string? Reason) Decide(ClassChange c, IReadOnlyList<ParsedMeeting> oldMeetings, IReadOnlyList<ParsedMeeting> newMeetings, SemesterSpan span)
    {
        var bases = BasesOf(c, newMeetings, span);
        var adopted = AdoptedOf(c, newMeetings, span);
        var same = adopted.Any(m => SameAs(c, m));
        switch (c.Kind)
        {
            case Cancel:
                if (bases.Count > 0) return (Outcome.Keep, null);
                // Only a session that appeared at a new time on that day means the class moved. A room or end-time
                // change on that date, or anything on another date, does not.
                var on = c.OrigDate!.Value;
                var before = oldMeetings.Where(m => On(m, on, span)).Select(m => m.Start).ToHashSet();
                return newMeetings.Any(m => On(m, on, span) && !before.Contains(m.Start)) ? (Outcome.Drop, DroppedMoved) : (Outcome.Merge, Merged);
            case Move:
                if (bases.Count > 0)
                    // The university newly lists the teacher's new time as well, in another room or with another end:
                    // students see the university's session there (no doubles), so the teacher is told.
                    return adopted.Count > 0 && !same && AdoptedOf(c, oldMeetings, span).Count == 0 ? (Outcome.Keep, RoomDiffers) : (Outcome.Keep, null);
                return adopted.Count == 0 ? (Outcome.Drop, DroppedMoved) : same ? (Outcome.Merge, Merged) : (Outcome.MergeTell, Merged);
            case Room:
                if (bases.Count == 0) return (Outcome.Drop, DroppedMoved);
                if (bases.Count == 1 && RoomKey(bases[0].Room) == RoomKey(c.NewRoom)) return (Outcome.Merge, Merged);
                return RoomKey(bases[0].Room) != RoomKey(c.OrigRoom) ? (Outcome.KeepTellRoom, RoomDiffers) : (Outcome.Keep, null);
            default:
                return adopted.Count == 0 ? (Outcome.Keep, null) : same ? (Outcome.Merge, Merged) : (Outcome.MergeTell, Merged);
        }
    }

    /// <summary>What students see of a section: its meetings minus the dates changes skip, plus the sessions changes put on.</summary>
    static HashSet<string> Seen(IReadOnlyList<ParsedMeeting> meetings, IEnumerable<ClassChange> live, SemesterSpan span)
    {
        static string Key(string kind, string on, TimeOnly start, TimeOnly end, string? room) =>
            string.Create(Inv, $"{kind}|{on}|{start:HH\\:mm}|{end:HH\\:mm}|{RoomKey(room)}");
        var skipped = new HashSet<(ParsedMeeting Meeting, DateOnly Date)>();
        var put = new List<(DateOnly Date, TimeOnly Start, TimeOnly End, string Room)>();
        foreach (var c in live.OrderBy(c => c.Id))
        {
            if (c.Kind == Extra)
            {
                if (c is { NewDate: DateOnly date, NewStart: TimeOnly start, NewEnd: TimeOnly end }) put.Add((date, start, end, c.NewRoom ?? ""));
                continue;
            }
            var bases = BasesOf(c, meetings, span);
            // No such session: the change shows nowhere (the read-time guard of the change set).
            if (bases.Count == 0) continue;
            foreach (var b in bases) skipped.Add((b, c.OrigDate!.Value));
            if (c.Kind == Move && c is { NewDate: DateOnly to, NewStart: TimeOnly at, NewEnd: TimeOnly until }) put.Add((to, at, until, c.NewRoom ?? ""));
            else if (c.Kind == Room) put.Add((c.OrigDate!.Value, bases[0].Start, bases[0].End, c.NewRoom ?? ""));
        }
        var seen = new HashSet<string>();
        foreach (var m in meetings)
        {
            if (m.Date is DateOnly on)
            {
                if (!skipped.Contains((m, on))) seen.Add(Key("d", PlannerIso(on), m.Start, m.End, m.Room));
                continue;
            }
            seen.Add(Key("w", m.Day.ToString(Inv), m.Start, m.End, m.Room));
            foreach (var (_, date) in skipped.Where(x => x.Meeting == m)) seen.Add(Key("off", PlannerIso(date), m.Start, m.End, m.Room));
        }
        // No doubles, as in the change set: left out when a real meeting is still on at that date and start.
        foreach (var a in put)
            if (!meetings.Any(m => m.Start == a.Start && On(m, a.Date, span) && !skipped.Contains((m, a.Date))))
                seen.Add(Key("d", PlannerIso(a.Date), a.Start, a.End, a.Room));
        return seen;
    }

    static string PlannerIso(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    /// <summary>
    /// Whether students see the same sessions before and after a timetable change: base meetings minus skipped dates,
    /// plus added sessions, compared by date (or weekday), start, end and room. When they do, nobody is told
    /// "{Code} changed": the university only caught up with what the teacher already did.
    /// </summary>
    public static bool SameForStudents(IReadOnlyList<ParsedMeeting> oldMeetings, IEnumerable<ClassChange> liveBefore,
        IReadOnlyList<ParsedMeeting> newMeetings, IEnumerable<ClassChange> liveAfter, SemesterSpan span) =>
        Seen(oldMeetings, liveBefore, span).SetEquals(Seen(newMeetings, liveAfter, span));

    /// <summary>
    /// Starts one timetable change (an apply, or a section edit): the semester's live changes, loaded once as rows
    /// this request may edit, and the list of what teachers are to be told. Null when the semester has no live
    /// changes, no added or hidden sections and nobody holds a name: then there is nothing to do, and the caller
    /// skips the whole block.
    /// </summary>
    public static async Task<ChangeTell?> StartAsync(AppDb db, Semester semester, DateTimeOffset now, CancellationToken ct)
    {
        var live = await db.ClassChanges.Where(c => c.SemesterId == semester.Id && c.EndedAt == null).ToListAsync(ct);
        if (live.Count == 0 && !await db.TeacherSections.AnyAsync(r => r.SemesterId == semester.Id, ct) && !await db.TeacherNames.AnyAsync(ct)) return null;
        return new ChangeTell(semester, await SemesterSpan.ForAsync(db, semester, ct), Today(now), live);
    }

    static string Did(ClassChange c) => c.Kind switch { Cancel => "cancellation", Move => "move", Room => "room change", _ => "extra class" };
    static string In(string? room) => string.IsNullOrWhiteSpace(room) ? "with no room yet" : $"in {room.Trim()}";
    static string RoomName(string? room) => string.IsNullOrWhiteSpace(room) ? "to be announced" : room.Trim();

    /// <summary>What a change is, for staff: "Cancelled Tue 2 Dec 10:00".</summary>
    public static string What(ClassChange c) => c.Kind switch
    {
        Cancel => $"Cancelled {TeachNotices.When(c.OrigDate!.Value, c.OrigStart!.Value)}",
        Move => $"Moved {TeachNotices.When(c.OrigDate!.Value, c.OrigStart!.Value)} → {TeachNotices.When(c.NewDate!.Value, c.NewStart!.Value)}",
        Room => $"Room {RoomName(c.NewRoom)} for {TeachNotices.When(c.OrigDate!.Value, c.OrigStart!.Value)}",
        _ => $"Extra class {TeachNotices.When(c.NewDate!.Value, c.NewStart!.Value)}",
    };

    /// <summary>
    /// What a new meeting list would do to a section's live changes, for the preview staff see before applying:
    /// exactly the changes <see cref="ReconcileAsync"/> (or, with no new list, <see cref="EndSectionAsync"/>) will
    /// end or tell a teacher about. Saves nothing.
    /// </summary>
    /// <param name="newMeetings">Null when the section is going away.</param>
    public static IEnumerable<TeacherChangeEffect> Effects(IEnumerable<ClassChange> live, IReadOnlyList<ParsedMeeting> oldMeetings,
        IReadOnlyList<ParsedMeeting>? newMeetings, SemesterSpan span, DateTimeOffset now)
    {
        var today = Today(now);
        foreach (var c in live.Where(c => c.EndedAt is null && Latest(c) >= today).OrderBy(c => c.Id))
        {
            var effect = Dropped;
            if (newMeetings is not null)
            {
                var (what, reason) = Decide(c, oldMeetings, newMeetings, span);
                if (what == Outcome.Keep && reason is null) continue;
                effect = what switch { Outcome.Drop => Dropped, Outcome.Merge or Outcome.MergeTell => Merged, _ => RoomDiffers };
            }
            yield return new(effect, c.CourseCode, c.SectionNumber, What(c), c.ByName);
        }
    }

    /// <summary>
    /// Applies <see cref="Decide"/> to a section's live changes when its meetings or instructor change, before the
    /// caller replaces them: ends the ones that merge or drop, refreshes the snapshots of those that stay, and adds
    /// what teachers are to be told to <paramref name="tell"/> (the caller calls <see cref="TellTeachersAsync"/> once,
    /// after its last section). Only changes whose latest date is today or later are decided; older ones are history.
    /// Returns true when the section is not to be stamped as changed for students: it had live changes, its
    /// instructor is the same, and students see the same sessions as before.
    /// </summary>
    /// <param name="summary">What changed, as students are told (ScheduleImport.DescribeChange).</param>
    public static async Task<bool> ReconcileAsync(AppDb db, Semester semester, Course course, Section section, string oldInstructor, string newInstructor,
        IReadOnlyList<ParsedMeeting> oldMeetings, IReadOnlyList<ParsedMeeting> newMeetings, string? summary, ChangeTell tell, DateTimeOffset now, CancellationToken ct)
    {
        var span = tell.Span;
        var live = tell.LiveOf(section.Id);
        var (oldKey, newKey) = (TeacherNames.NameKey(oldInstructor), TeacherNames.NameKey(newInstructor));
        var before = live.Count == 0 ? null : Seen(oldMeetings, live, span);
        var ended = new List<(ClassChange Change, string Reason)>();
        void Note(string kind, ClassChange c, string title, string body, string url) =>
            tell.Events.Add(new(kind, c, section.Id, oldKey, newKey, title, body, url, false));

        foreach (var c in live.Where(c => Latest(c) >= tell.Today).ToList())
        {
            var (what, reason) = Decide(c, oldMeetings, newMeetings, span);
            var session = BasesOf(c, newMeetings, span).FirstOrDefault();
            var listed = AdoptedOf(c, newMeetings, span) is { Count: > 0 } adopted ? adopted.FirstOrDefault(m => !SameAs(c, m)) ?? adopted[0] : null;
            switch (what)
            {
                case Outcome.Keep or Outcome.KeepTellRoom:
                    if (session is null) break;
                    if (what == Outcome.KeepTellRoom)
                        Note(RoomDiffers, c, "{0}: the university lists another room",
                            $"It now says {RoomName(session.Room)} for {TeachNotices.When(c.OrigDate!.Value, c.OrigStart!.Value)}. Students still see your room, {RoomName(c.NewRoom)}. Undo your change to use the university's room.",
                            TeachNotices.TeacherUrl(section.Id, c.OrigDate!.Value, c.OrigStart!.Value));
                    else if (reason == RoomDiffers && listed is not null)
                        Note(RoomDiffers, c, "{0}: the university lists it at your new time",
                            $"It now says {TeachNotices.When(c.NewDate!.Value, c.NewStart!.Value)} {In(listed.Room)}. Students see that room.",
                            TeachNotices.TeacherUrl(section.Id, c.NewDate!.Value, c.NewStart!.Value));
                    // The snapshot follows the university: its end and room can change under a change.
                    (c.OrigEnd, c.OrigRoom) = (session.End, session.Room);
                    if (c.Kind == Room) c.NewEnd = session.End;
                    break;
                case Outcome.Merge:
                    ended.Add((c, reason!));
                    break;
                case Outcome.MergeTell:
                    ended.Add((c, reason!));
                    Note(Merged, c, c.Kind == Extra ? "{0}: the university lists it too" : "{0}: the university moved it too",
                        $"It now lists {TeachNotices.When(c.NewDate!.Value, c.NewStart!.Value)} {In(listed?.Room)}. Students see that.",
                        TeachNotices.TeacherUrl(section.Id, c.NewDate!.Value, c.NewStart!.Value));
                    break;
                default:
                    ended.Add((c, reason!));
                    Note(Dropped, c, "Your change to {0} was dropped",
                        $"The university timetable changed {TeachNotices.When(c.OrigDate!.Value, c.OrigStart!.Value)}. Your {Did(c)} no longer applies; check the new time.", "/teach");
                    break;
            }
        }
        // Neither reason writes "back on" or "undone": the timetable itself now says what is on.
        foreach (var group in ended.GroupBy(e => e.Reason))
            await ChangeRules.EndAsync(db, group.Select(e => e.Change).ToList(), group.Key, BySystem, now, ct);
        live.RemoveAll(c => c.EndedAt is not null);

        var quiet = before is not null && string.Equals(oldInstructor, newInstructor, StringComparison.Ordinal) && before.SetEquals(Seen(newMeetings, live, span));
        if (!semester.IsCurrent) return quiet;
        // Given to another teacher: the changes stay, and both teachers are told.
        if (oldKey != newKey)
            tell.Reassigned.Add(new(section.Id, course.Code, section.Number, oldInstructor.Trim(), newInstructor.Trim(), live.Where(c => ChangeRules.CanUndo(c, now)).ToList()));
        // Teachers get change alerts like students: collected now, so a teacher who was just replaced is still found.
        if (!quiet) tell.Changed.Add(new(section.Id, course.Code, section.Number, summary ?? "", oldKey, newKey));
        return quiet;
    }

    /// <summary>
    /// A section is going away: every live change on it ends before it is removed (the upcoming ones with a notice to
    /// their teachers), and accounts that added the section are told it is gone. The text copies on the rows keep the
    /// log readable afterwards.
    /// </summary>
    public static async Task EndSectionAsync(AppDb db, Course course, Section section, string reason, ChangeTell tell, DateTimeOffset now, CancellationToken ct)
    {
        var live = tell.LiveOf(section.Id);
        var key = TeacherNames.NameKey(section.Instructor);
        foreach (var c in live.Where(c => Latest(c) >= tell.Today))
            tell.Events.Add(new(Dropped, c, section.Id, key, key, "Your change to {0} was dropped",
                $"{course.Code} section {section.Number} is no longer on the timetable.", "/teach", true));
        await ChangeRules.EndAsync(db, live.ToList(), reason, BySystem, now, ct);
        live.Clear();
        if (tell.Semester.IsCurrent) tell.Removed.Add(new(section.Id, course.Code, section.Number, "", key, key));
    }

    /// <summary>
    /// After a day off was added or the teaching weeks changed: a weekly session can stop existing, on a date that
    /// is now off or in a week that is no longer taught. Ends every live cancel, move and room change that isn't over
    /// and now has no session to be about, tells its teachers, and tells students when a move they were told about
    /// is dropped (the class at its new time is gone; no section is stamped, so nothing else would say so). Saves.
    /// </summary>
    public static async Task<int> EndWithoutBaseAsync(AppDb db, Semester semester, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var live = await db.ClassChanges.Where(c => c.SemesterId == semester.Id && c.EndedAt == null && c.Kind != Extra && c.SectionId != null).ToListAsync(ct);
        if (live.Count == 0) return 0;
        var ids = live.Select(c => c.SectionId!.Value).Distinct().ToList();
        var sections = await db.Sections.AsNoTracking().Where(s => ids.Contains(s.Id)).Include(s => s.Meetings).ToDictionaryAsync(s => s.Id, ct);
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var gone = live.Where(c => ChangeRules.LastStartUtc(c) > now && sections.TryGetValue(c.SectionId!.Value, out var s)
            && BasesOf(c, s.Meetings.Select(ParsedMeeting.Of), span).Count == 0).ToList();
        if (gone.Count == 0) return 0;

        var tell = new ChangeTell(semester, span, Today(now), []);
        await ChangeRules.EndAsync(db, gone, reason, BySystem, now, ct);
        foreach (var c in gone)
        {
            var section = sections[c.SectionId!.Value];
            var key = TeacherNames.NameKey(section.Instructor);
            var day = TeachNotices.Day(c.OrigDate!.Value);
            tell.Events.Add(new(Dropped, c, section.Id, key, key, "Your change to {0} was dropped",
                reason == DroppedDayOff ? $"{day} is now a day off." : $"{day} is no longer in the teaching weeks.", "/teach", false));
            // Announced: the newest notice left for the session (the unsent ones went when it ended) is a "change".
            if (c.Kind != Move || !await TeachNotices.AnnouncedAsync(db, section.Id, c.Tag, ct)) continue;
            db.TeachNotices.Add(new TeachNotice
            {
                Audience = TeachNotice.Students, SectionId = section.Id, GroupId = c.GroupId, Kind = Dropped, CreatedAt = now,
                Title = TeachNotices.Cut($"{c.CourseCode}: the class moved to {TeachNotices.When(c.NewDate!.Value, c.NewStart!.Value)} is off", 120),
                Body = reason == DroppedDayOff ? $"{day} is now a day off." : "That week is no longer a teaching week.",
                Url = "/", Tag = c.Tag, ExpiresAt = ChangeRules.StartUtc(c.NewDate!.Value, c.NewStart!.Value),
            });
        }
        await TellTeachersAsync(db, tell, now, ct);
        await db.SaveChangesAsync(ct);
        return gone.Count;
    }

    static string Sentence(IReadOnlyList<SectionEvent> sections, string one, string many)
    {
        if (sections.Count == 1) return $"{sections[0].Code} section {sections[0].Number} {one}";
        var names = sections.Select(s => $"{s.Code} {s.Number}").ToList();
        return $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]} {many}";
    }

    static string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n.ToString(Inv)} {one}s";

    /// <summary>
    /// Writes the teacher notices of one timetable change, without repeats: one per teacher, kind and change (naming
    /// every code of the class) for dropped, room-differs and merged; one "reassigned" per teacher and pair of names;
    /// one "section-removed" per teacher; and one "uni-changed" per teacher for the classes that changed and that the
    /// teacher got none of the other kinds about. The recipients of an event are its maker and every account on the
    /// section: linked to the old or the new instructor's name, or added it. The caller saves.
    /// </summary>
    public static async Task TellTeachersAsync(AppDb db, ChangeTell tell, DateTimeOffset now, CancellationToken ct)
    {
        if (tell.Events.Count + tell.Changed.Count + tell.Reassigned.Count + tell.Removed.Count == 0) return;
        var holders = (await db.TeacherNames.AsNoTracking().Where(n => n.State == TeacherName.Auto || n.State == TeacherName.Approved)
            .Select(n => new { n.NameKey, n.TeacherId }).ToListAsync(ct)).GroupBy(n => n.NameKey).ToDictionary(g => g.Key, g => g.First().TeacherId);
        var rows = await db.TeacherSections.AsNoTracking().Where(r => r.SemesterId == tell.Semester.Id && r.SectionId != null)
            .Select(r => new { r.TeacherId, SectionId = r.SectionId!.Value, r.Mode }).ToListAsync(ct);
        var adders = rows.Where(r => r.Mode == TeacherSection.AddedMode).ToLookup(r => r.SectionId, r => r.TeacherId);
        var hidden = rows.Where(r => r.Mode == TeacherSection.HiddenMode).Select(r => (r.TeacherId, r.SectionId)).ToHashSet();
        var teachers = await db.Teachers.AsNoTracking().Select(t => new { t.Id, t.MuteChanges }).ToDictionaryAsync(t => t.Id, t => t.MuteChanges, ct);

        IEnumerable<int> OnSection(int sectionId, string oldKey, string newKey) =>
            new[] { oldKey, newKey }.Where(k => k.Length > 0 && holders.ContainsKey(k)).Select(k => holders[k]).Concat(adders[sectionId]).Distinct();
        // What each teacher was already told about, so "your class changed" isn't said on top of it.
        var covered = new HashSet<(int TeacherId, int SectionId)>();
        void Add(int teacherId, string kind, string? group, int? sectionId, string title, string body, string url, string tag, DateTimeOffset? expires) =>
            db.TeachNotices.Add(new TeachNotice
            {
                Audience = TeachNotice.ToTeacher, TeacherId = teacherId, SectionId = sectionId, GroupId = group, Kind = kind, CreatedAt = now,
                Title = TeachNotices.Cut(title, 120), Body = TeachNotices.Cut(body, 400), Url = url, Tag = TeachNotices.Cut(tag, 64), ExpiresAt = expires,
            });

        foreach (var group in tell.Events.GroupBy(e => (e.Change.GroupId, e.Kind)))
        {
            var ordered = group.OrderBy(e => e.Change.CourseCode, StringComparer.Ordinal).ThenBy(e => e.SectionId).ToList();
            var lead = ordered[0];
            var codes = string.Join(" · ", ordered.Select(e => e.Change.CourseCode).Distinct());
            var told = ordered.SelectMany(e => OnSection(e.SectionId, e.OldKey, e.NewKey))
                .Concat(ordered.Where(e => e.Change.TeacherId is not null).Select(e => e.Change.TeacherId!.Value)).Distinct().Where(teachers.ContainsKey).ToList();
            foreach (var teacherId in told)
            {
                Add(teacherId, lead.Kind, lead.Change.GroupId, lead.Removed ? null : lead.SectionId, string.Format(Inv, lead.Title, codes), lead.Body, lead.Url,
                    $"t{lead.Kind}-{lead.Change.GroupId}", ChangeRules.LastStartUtc(lead.Change));
                foreach (var e in ordered) covered.Add((teacherId, e.SectionId));
            }
        }

        foreach (var pair in tell.Reassigned.GroupBy(r => (Old: TeacherNames.NameKey(r.OldName), New: TeacherNames.NameKey(r.NewName))))
        {
            int? was = holders.TryGetValue(pair.Key.Old, out var a) ? a : null, takes = holders.TryGetValue(pair.Key.New, out var b) ? b : null;
            // One account holding both spellings wasn't given anything, or taken off anything.
            if (was == takes) continue;
            var sections = pair.OrderBy(r => r.Code, StringComparer.Ordinal).ThenBy(r => r.Number, StringComparer.Ordinal)
                .Select(r => new SectionEvent(r.SectionId, r.Code, r.Number, "", pair.Key.Old, pair.Key.New)).ToList();
            var (oldName, newName) = (pair.First().OldName, pair.First().NewName);
            var upcoming = pair.SelectMany(r => r.Upcoming).ToList();
            if (was is int old && teachers.ContainsKey(old))
            {
                var mine = upcoming.Where(c => c.TeacherId == old).Select(c => c.GroupId).Distinct().Count();
                Add(old, "reassigned", null, sections[0].SectionId,
                    newName.Length > 0 ? Sentence(sections, $"is now listed under {newName}.", $"are now listed under {newName}.")
                        : Sentence(sections, "is no longer listed under your name.", "are no longer listed under your name."),
                    mine > 0 ? $"Your {Count(mine, "upcoming change")} {(mine == 1 ? "stays" : "stay")} until someone undoes {(mine == 1 ? "it" : "them")}." : "",
                    "/teach", $"treassigned-{old.ToString(Inv)}-{sections[0].SectionId.ToString(Inv)}", null);
                foreach (var s in sections) covered.Add((old, s.SectionId));
            }
            if (takes is int next && teachers.ContainsKey(next))
            {
                var all = upcoming.Select(c => c.GroupId).Distinct().Count();
                Add(next, "reassigned", null, sections[0].SectionId, Sentence(sections, "is now under your name.", "are now under your name."),
                    all > 0 ? $"{(sections.Count == 1 ? "It has" : "They have")} {Count(all, "upcoming change")}{(oldName.Length > 0 ? $" made by {oldName}" : "")}. You can undo them from the class." : "",
                    "/teach", $"treassigned-{next.ToString(Inv)}-{sections[0].SectionId.ToString(Inv)}", null);
                foreach (var s in sections) covered.Add((next, s.SectionId));
            }
        }

        foreach (var mine in tell.Removed.SelectMany(s => adders[s.SectionId].Distinct().Select(t => (TeacherId: t, Section: s)))
            .Where(x => teachers.ContainsKey(x.TeacherId) && !covered.Contains((x.TeacherId, x.Section.SectionId))).GroupBy(x => x.TeacherId))
        {
            var sections = mine.Select(x => x.Section).OrderBy(s => s.Code, StringComparer.Ordinal).ThenBy(s => s.Number, StringComparer.Ordinal).ToList();
            Add(mine.Key, "section-removed", null, null, Sentence(sections, "is no longer on the timetable.", "are no longer on the timetable."),
                "The university removed it. It comes back to your classes by itself if it returns.", "/teach", $"tremoved-{mine.Key.ToString(Inv)}", null);
        }

        foreach (var mine in tell.Changed.SelectMany(s => OnSection(s.SectionId, s.OldKey, s.NewKey).Select(t => (TeacherId: t, Section: s)))
            .Where(x => teachers.TryGetValue(x.TeacherId, out var muted) && !muted && !hidden.Contains((x.TeacherId, x.Section.SectionId))
                && !covered.Contains((x.TeacherId, x.Section.SectionId))).GroupBy(x => x.TeacherId))
        {
            var sections = mine.Select(x => x.Section).DistinctBy(s => s.SectionId).OrderBy(s => s.Code, StringComparer.Ordinal).ThenBy(s => s.Number, StringComparer.Ordinal).ToList();
            var (title, body) = sections.Count == 1
                ? ($"{sections[0].Code} changed", sections[0].Summary)
                : ($"{sections.Count.ToString(Inv)} of your classes changed", string.Join(", ", sections.Select(s => $"{s.Code} {s.Number}")));
            Add(mine.Key, "uni-changed", null, sections.Count == 1 ? sections[0].SectionId : null, title, body, "/teach", $"tuni-{mine.Key.ToString(Inv)}", null);
        }
    }

    /// <summary>
    /// After an apply is saved: sections a teacher added that the university removed and has now brought back are
    /// theirs again (matched by course code and section number), and a held name that left the timetable gets a hint
    /// when every section that carried it now carries one single other name ("Raed Bolbol → Raed Bolbul. Same
    /// person?"). The hint is never taken by itself: staff accept it with one tap. Saves.
    /// </summary>
    public static async Task AfterApplyAsync(AppDb db, Semester semester, ChangeTell tell, CancellationToken ct)
    {
        var sections = await db.Sections.Where(s => s.Course.SemesterId == semester.Id).Select(s => new { s.Id, s.Course.Code, s.Number, s.Instructor }).ToListAsync(ct);
        static (string, string) Text(string code, string number) => (code.ToUpperInvariant(), number.ToUpperInvariant());

        var lost = await db.TeacherSections.Where(r => r.SemesterId == semester.Id && r.SectionId == null).ToListAsync(ct);
        if (lost.Count > 0)
        {
            var byText = sections.GroupBy(s => Text(s.Code, s.Number)).ToDictionary(g => g.Key, g => g.First().Id);
            foreach (var row in lost)
                if (byText.TryGetValue(Text(row.CourseCode, row.SectionNumber), out var id)) row.SectionId = id;
        }

        // Names are those of the published timetable: a draft semester suggests nothing.
        if (semester.IsCurrent)
        {
            var links = await db.TeacherNames.Where(n => n.State == TeacherName.Auto || n.State == TeacherName.Approved).ToListAsync(ct);
            var held = links.Select(n => n.NameKey).ToHashSet();
            var written = sections.Select(s => TeacherNames.NameKey(s.Instructor)).ToHashSet();
            var after = sections.GroupBy(s => Text(s.Code, s.Number)).ToDictionary(g => g.Key, g => g.First().Instructor);
            foreach (var link in links)
            {
                // Back on the timetable (or never off it): nothing to suggest.
                if (written.Contains(link.NameKey))
                {
                    link.SuggestedName = null;
                    continue;
                }
                var carried = tell.Before.Where(s => TeacherNames.NameKey(s.Instructor) == link.NameKey).ToList();
                if (carried.Count == 0) continue;
                var names = carried.Select(s => after.GetValueOrDefault(Text(s.Code, s.Number))).ToList();
                if (names.Any(n => n is null) || names.Select(n => TeacherNames.NameKey(n)).Distinct().Count() != 1) continue;
                var name = names[0]!.Trim();
                if (name.Length is 0 or > 120 || TeacherNames.IsPlaceholder(name) || held.Contains(TeacherNames.NameKey(name))) continue;
                link.SuggestedName = name;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>How many teacher changes the timetable dropped since <paramref name="since"/>: what an apply answers with.</summary>
    public static async Task<int> DroppedSinceAsync(AppDb db, int semesterId, DateTimeOffset since, CancellationToken ct)
    {
        since = since.ToUniversalTime();
        var today = Today(since);
        return (await db.ClassChanges.AsNoTracking().Where(c => c.SemesterId == semesterId && c.EndedBy == BySystem && c.EndedAt >= since
                && (c.EndReason == DroppedMoved || c.EndReason == DroppedRemoved)).ToListAsync(ct))
            .Count(c => Latest(c) >= today);
    }
}

/// <summary>What a new meeting list does to one live change (<see cref="ClassChanges.Decide"/>).</summary>
public enum Outcome { Keep, KeepTellRoom, Merge, MergeTell, Drop }

/// <summary>One thing teachers are to be told about a change of theirs. <paramref name="Title"/> has {0} for the codes of the class.</summary>
/// <param name="Removed">The section is going away: the notice can't point at it.</param>
sealed record ChangeEvent(string Kind, ClassChange Change, int SectionId, string OldKey, string NewKey, string Title, string Body, string Url, bool Removed);

/// <summary>A section the timetable changed or removed, with the name keys it was and is listed under.</summary>
sealed record SectionEvent(int SectionId, string Code, string Number, string Summary, string OldKey, string NewKey);

/// <summary>A section given to another teacher, with the live changes on it that can still be undone.</summary>
sealed record ReassignEvent(int SectionId, string Code, string Number, string OldName, string NewName, List<ClassChange> Upcoming);

/// <summary>
/// One timetable change in progress (an apply, or a section edit): the semester's live changes by section, and what
/// teachers are to be told once it is done. Owned by the caller; filled by <see cref="ClassChanges.ReconcileAsync"/>
/// and <see cref="ClassChanges.EndSectionAsync"/>, written by <see cref="ClassChanges.TellTeachersAsync"/>.
/// </summary>
public sealed class ChangeTell
{
    readonly Dictionary<int, List<ClassChange>> live;

    internal ChangeTell(Semester semester, SemesterSpan span, DateOnly today, List<ClassChange> live)
    {
        (Semester, Span, Today) = (semester, span, today);
        this.live = live.Where(c => c.SectionId is not null).GroupBy(c => c.SectionId!.Value).ToDictionary(g => g.Key, g => g.ToList());
    }

    internal Semester Semester { get; }
    internal SemesterSpan Span { get; }
    /// <summary>Today in Oman: changes whose last date is before it are history.</summary>
    internal DateOnly Today { get; }
    internal List<ChangeEvent> Events { get; } = [];
    internal List<SectionEvent> Changed { get; } = [];
    internal List<SectionEvent> Removed { get; } = [];
    internal List<ReassignEvent> Reassigned { get; } = [];
    /// <summary>Every section's instructor before the apply, for the re-spelling hints.</summary>
    internal List<(string Code, string Number, string Instructor)> Before { get; private set; } = [];

    /// <summary>The live changes of a section, as rows this request may edit.</summary>
    internal List<ClassChange> LiveOf(int sectionId) => live.TryGetValue(sectionId, out var rows) ? rows : live[sectionId] = [];

    /// <summary>Keeps who each section was listed under before the apply changes it.</summary>
    public void Remember(IEnumerable<Course> courses) =>
        Before = courses.SelectMany(c => c.Sections.Select(s => (c.Code, s.Number, s.Instructor))).ToList();
}

/// <summary>A session a change puts on: the class at its new time or room (<paramref name="Base"/> is the university session it came from), or an extra class (no base).</summary>
public sealed record Added(ClassChange Change, DateOnly Date, TimeOnly Start, TimeOnly End, string Room, Meeting? Base);

/// <summary>A session that stays visible though it isn't on: a cancelled one, or the old slot of a moved one.</summary>
public sealed record Off(ClassChange Change, DateOnly Date, TimeOnly Start, TimeOnly End, string Room, Meeting Base);

/// <summary>The live changes of some sections, applied to their meetings. The only reader of live changes.</summary>
public sealed class ChangeSet
{
    public static readonly ChangeSet None = new([], [], null);

    static readonly IReadOnlySet<DateOnly> NoDates = new HashSet<DateOnly>();

    readonly Dictionary<int, HashSet<DateOnly>> skip = [];
    readonly Dictionary<int, List<Added>> added = [];
    readonly Dictionary<int, List<Off>> off = [];
    readonly Dictionary<int, List<ClassChange>> shown = [];
    readonly Dictionary<int, Meeting> bases = [];
    readonly Dictionary<(int Section, DateOnly Date, TimeOnly Start), ClassChange> sessions = [];
    readonly List<ClassChange> all;

    /// <param name="live">Live changes (not ended) of these sections.</param>
    /// <param name="sections">The sections, with their meetings loaded.</param>
    public ChangeSet(IReadOnlyCollection<ClassChange> live, IReadOnlyCollection<Section> sections, SemesterSpan? span)
    {
        all = live.Where(c => c.SectionId is not null).OrderBy(c => c.Id).ToList();
        if (all.Count == 0 || span is null) return;
        var byId = sections.DistinctBy(s => s.Id).ToDictionary(s => s.Id);
        var put = new List<(Section Section, Added Session)>();
        foreach (var c in all)
        {
            if (!byId.TryGetValue(c.SectionId!.Value, out var section)) continue;
            if (c.Kind == ClassChanges.Extra)
            {
                if (c is not { NewDate: DateOnly date, NewStart: TimeOnly start, NewEnd: TimeOnly end }) continue;
                put.Add((section, new(c, date, start, end, c.NewRoom ?? "", null)));
                Add(shown, section.Id, c);
                continue;
            }
            if (c is not { OrigDate: DateOnly on, OrigStart: TimeOnly at }) continue;
            sessions[(section.Id, on, at)] = c;
            // Every meeting at that start on that date: normally one. Room and end time are never part of the match,
            // so a room-only or end-only change by the university keeps the teacher's change.
            var matched = section.Meetings.Where(m => m.Start == at && Happens(span, m, on))
                .OrderBy(m => m.Room, StringComparer.Ordinal).ThenBy(m => m.Id).ToList();
            // No such session any more: the change is left out of everything, so it can never draw on top of a
            // session that is gone, and can never make one come back.
            if (matched.Count == 0) continue;
            var first = matched[0];
            bases[c.Id] = first;
            foreach (var m in matched) (skip.TryGetValue(m.Id, out var dates) ? dates : skip[m.Id] = []).Add(on);
            Add(shown, section.Id, c);
            switch (c.Kind)
            {
                case ClassChanges.Cancel:
                    Add(off, section.Id, new Off(c, on, first.Start, first.End, first.Room, first));
                    break;
                case ClassChanges.Move when c is { NewDate: DateOnly date, NewStart: TimeOnly start, NewEnd: TimeOnly end }:
                    Add(off, section.Id, new Off(c, on, first.Start, first.End, first.Room, first));
                    put.Add((section, new(c, date, start, end, c.NewRoom ?? "", first)));
                    break;
                case ClassChanges.Room:
                    // The university's time: when it later changes only the end, the re-roomed session follows.
                    put.Add((section, new(c, on, first.Start, first.End, c.NewRoom ?? "", first)));
                    break;
            }
        }
        // No doubles: a session a change puts on is left out when the section has a real meeting then that is still on
        // (the university adopted the new time while the old one is still listed).
        foreach (var (section, a) in put)
            if (!section.Meetings.Any(m => m.Start == a.Start && Happens(span, m, a.Date) && !Skip(section, m).Contains(a.Date)))
                Add(added, section.Id, a);
    }

    static void Add<T>(Dictionary<int, List<T>> table, int key, T value) => (table.TryGetValue(key, out var list) ? list : table[key] = []).Add(value);

    /// <summary>Whether a meeting is held on a date, as the university lists it: set for that date, or weekly on that weekday in the teaching weeks.</summary>
    public static bool Happens(SemesterSpan span, Meeting m, DateOnly date) => m.Date == date || (m.Date is null && span.Weekly(m.Day, date, true));

    public bool Empty => all.Count == 0;

    /// <summary>Dates this meeting doesn't happen as the timetable has it: cancelled, moved away, or put in another room.</summary>
    public IReadOnlySet<DateOnly> Skip(Section s, Meeting m) => skip.TryGetValue(m.Id, out var dates) ? dates : NoDates;

    /// <summary>Sessions a change puts on: the class at its new time or room, and extra classes.</summary>
    public IReadOnlyList<Added> AddedTo(Section s) => added.TryGetValue(s.Id, out var list) ? list : [];

    /// <summary>Sessions that stay visible though they aren't on: cancelled ones and the old slot of a moved one.</summary>
    public IReadOnlyList<Off> OffIn(Section s) => off.TryGetValue(s.Id, out var list) ? list : [];

    /// <summary>Every live change of these sections, for the marks people see.</summary>
    public IReadOnlyList<ClassChange> Shown(IEnumerable<Section> sections) =>
        sections.Select(s => s.Id).Distinct().SelectMany(id => shown.TryGetValue(id, out var list) ? list : []).OrderBy(c => c.Id).ToList();

    /// <summary>Whether a rep's event on this session gives way to a teacher's change.</summary>
    public bool Shadows(int sectionId, DateOnly date, TimeOnly start) => sessions.TryGetValue((sectionId, date, start), out var c) && bases.ContainsKey(c.Id);

    /// <summary>The live change on this session of the section as the university lists it (never an extra class), if any.</summary>
    public ClassChange? On(int sectionId, DateOnly date, TimeOnly start) => sessions.GetValueOrDefault((sectionId, date, start));

    /// <summary>The university session a change is about: null for an extra class, and for a change whose session is gone.</summary>
    public Meeting? BaseOf(ClassChange c) => bases.GetValueOrDefault(c.Id);

    /// <summary>Every live row of the sections, also those left out of what people see because their session is gone.</summary>
    public IReadOnlyList<ClassChange> All => all;
}
