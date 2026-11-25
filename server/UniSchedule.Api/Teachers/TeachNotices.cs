using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Teachers;

/// <summary>
/// The notices written with each class change: the texts, and the rows put in the outbox (<see cref="TeachNotice"/>)
/// in the same transaction as the change. Students get one row per section; a teacher gets one row per action,
/// naming every code of the class. Only shown names ever go into a notice, never an email, username or university ID.
/// <see cref="RoundAsync"/> then sends them: writing a row is all it takes to tell someone.
/// </summary>
public static class TeachNotices
{
    public const string Change = "change", BackOn = "back-on", OtherTeacher = "other-teacher", Undone = "undone";

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The tag that identifies a session on a phone, so a later state replaces the earlier one: from the original date
    /// and start, or for an extra class from its first new time. Stored on the change; a replacement copies it.
    /// </summary>
    public static string Tag(int sectionId, DateOnly date, TimeOnly start, bool extra) =>
        string.Create(Inv, $"tchange-{sectionId}-{(extra ? "x" : "")}{date:yyyyMMdd}-{start:HHmm}");

    /// <summary>"Tue 2 Dec 10:00".</summary>
    public static string When(DateOnly date, TimeOnly time) => string.Create(Inv, $"{date:ddd d MMM} {time:HH:mm}");
    internal static string Day(DateOnly date) => date.ToString("ddd d MMM", Inv);
    static string RoomText(string? room) => string.IsNullOrWhiteSpace(room) ? "room to be announced" : $"room {room.Trim()}";
    internal static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>Who did it, as students read it: the shown name, and for an owner's "view as" the name it was done for.</summary>
    static string Who(ClassChange c) => c.OnBehalfOf is null ? c.ByName : $"{c.ByName} for {c.OnBehalfOf}";
    static string WithNote(string text, ClassChange c) => string.IsNullOrWhiteSpace(c.Note) ? text : $"{text}: {c.Note}";

    /// <summary>What a change says, with <paramref name="code"/> the course code (or the codes of one real class).</summary>
    /// <param name="previous">The change this one replaces, if any: cancelling a moved class is told as about the moved session.</param>
    public static (string Title, string Body) Text(ClassChange c, string code, ClassChange? previous = null) => c.Kind switch
    {
        ClassChanges.Cancel when previous is { Kind: ClassChanges.Move, NewDate: DateOnly movedTo, NewStart: TimeOnly movedAt } =>
            ($"{code} cancelled", WithNote($"{When(movedTo, movedAt)} (moved from {When(c.OrigDate!.Value, c.OrigStart!.Value)}) · Cancelled by {Who(c)}", c)),
        ClassChanges.Cancel => ($"{code} cancelled", WithNote($"{c.CourseName} · {When(c.OrigDate!.Value, c.OrigStart!.Value)} · Cancelled by {Who(c)}", c)),
        ClassChanges.Move => ($"{code} moved",
            WithNote($"{When(c.OrigDate!.Value, c.OrigStart!.Value)} → {When(c.NewDate!.Value, c.NewStart!.Value)}, {RoomText(c.NewRoom)} · Moved by {Who(c)}", c)),
        ClassChanges.Room => ($"{code} room changed",
            WithNote($"{When(c.OrigDate!.Value, c.OrigStart!.Value)} · now in {(string.IsNullOrWhiteSpace(c.NewRoom) ? "a room to be announced" : c.NewRoom)}{(string.IsNullOrWhiteSpace(c.OrigRoom) ? "" : $" (was {c.OrigRoom})")} · {Who(c)}", c)),
        _ => ($"{code} extra class",
            WithNote($"{When(c.NewDate!.Value, c.NewStart!.Value)}–{c.NewEnd!.Value.ToString("HH:mm", Inv)}, {RoomText(c.NewRoom)} · {SessionKinds.Label(c.SessionType)} · Added by {Who(c)}", c)),
    };

    /// <summary>The date and start of the session a notice opens: where the class is now (its new time for a move or an extra class).</summary>
    static (DateOnly Date, TimeOnly Start) Stands(ClassChange c) =>
        c.Kind is ClassChanges.Move or ClassChanges.Extra ? (c.NewDate!.Value, c.NewStart!.Value) : (c.OrigDate!.Value, c.OrigStart!.Value);

    static string StudentUrl(int courseId, DateOnly date, TimeOnly start) => string.Create(Inv, $"/?session={courseId}.{PlannerEngine.Iso(date)}.{Timeslots.Format(start)}");
    internal static string TeacherUrl(int sectionId, DateOnly date, TimeOnly start) => string.Create(Inv, $"/teach?session=s{sectionId}.{PlannerEngine.Iso(date)}.{Timeslots.Format(start)}");

    /// <summary>
    /// Removes the notices to students about these sessions that nobody has started sending. So a change that ends,
    /// or is edited, before its notice went out is never announced as it was.
    /// </summary>
    public static async Task ClearUnsentAsync(AppDb db, IEnumerable<ClassChange> rows, CancellationToken ct)
    {
        foreach (var (sectionId, tag) in rows.Where(r => r.SectionId is not null).Select(r => (r.SectionId!.Value, r.Tag)).Distinct())
            await db.TeachNotices.Where(n => n.Audience == TeachNotice.Students && n.SectionId == sectionId && n.Tag == tag && n.ClaimedAt == null)
                .ExecuteDeleteAsync(ct);
    }

    /// <summary>Whether students were told about a change to this session: the newest notice left for it is a "change".</summary>
    public static async Task<bool> AnnouncedAsync(AppDb db, int sectionId, string tag, CancellationToken ct) =>
        await db.TeachNotices.Where(n => n.Audience == TeachNotice.Students && n.SectionId == sectionId && n.Tag == tag)
            .OrderByDescending(n => n.Id).Select(n => n.Kind).FirstOrDefaultAsync(ct) == Change;

    /// <summary>The teaching accounts that may change one of these sections, each once.</summary>
    static async Task<List<int>> TeachersAsync(AppDb db, int semesterId, IReadOnlyCollection<int> sectionIds, CancellationToken ct) =>
        (await TeacherSchedule.TeachersOfAsync(db, semesterId, sectionIds, ct)).SelectMany(g => g).Select(t => t.Id).Distinct().ToList();

    /// <summary>
    /// The notices of one teacher action (a new change, or a replacement): a "change" to the students of each section,
    /// after the unsent ones about the session are removed, so they get one notification with the newest details; and
    /// one "other-teacher" to every other teaching account that may change one of the sections.
    /// </summary>
    /// <param name="group">The new rows of one group, each with its section's course id.</param>
    /// <param name="previous">By section: the row this action replaced.</param>
    /// <param name="actingTeacherId">The account that did it, which isn't told; null when an owner did it through "view as" (the viewed account is told too).</param>
    public static async Task AddChangeAsync(AppDb db, int semesterId, IReadOnlyList<(ClassChange Change, int CourseId)> group,
        IReadOnlyDictionary<int, ClassChange> previous, int? actingTeacherId, DateTimeOffset now, CancellationToken ct)
    {
        if (group.Count == 0) return;
        await ClearUnsentAsync(db, group.Select(g => g.Change), ct);
        foreach (var (c, courseId) in group)
        {
            var before = previous.GetValueOrDefault(c.SectionId!.Value);
            var (title, body) = Text(c, c.CourseCode, before);
            var (date, start) = Stands(c);
            db.TeachNotices.Add(new TeachNotice
            {
                Audience = TeachNotice.Students, SectionId = c.SectionId, GroupId = c.GroupId, Kind = Change, CreatedAt = now,
                Title = Cut(title, 120), Body = Cut(body, 400), Url = StudentUrl(courseId, date, start), Tag = c.Tag,
                ExpiresAt = Latest(ChangeRules.LastStartUtc(c), before is null ? null : ChangeRules.LastStartUtc(before)),
            });
        }

        // One row per teacher for the whole action, naming every code: the lead is the code that sorts first.
        var ordered = group.Select(g => g.Change).OrderBy(c => c.CourseCode, StringComparer.Ordinal).ThenBy(c => c.SectionId).ToList();
        var lead = ordered[0];
        var codes = string.Join(" · ", ordered.Select(c => c.CourseCode).Distinct());
        var (heading, text) = Text(lead, codes, previous.GetValueOrDefault(lead.SectionId!.Value));
        var (on, at) = Stands(lead);
        var expires = Latest(ChangeRules.LastStartUtc(lead), previous.GetValueOrDefault(lead.SectionId!.Value) is { } old ? ChangeRules.LastStartUtc(old) : null);
        foreach (var teacherId in (await TeachersAsync(db, semesterId, ordered.Select(c => c.SectionId!.Value).ToList(), ct)).Where(id => id != actingTeacherId))
            db.TeachNotices.Add(new TeachNotice
            {
                Audience = TeachNotice.ToTeacher, TeacherId = teacherId, SectionId = lead.SectionId, GroupId = lead.GroupId, Kind = OtherTeacher, CreatedAt = now,
                Title = Cut($"{lead.ByName} changed your class", 120), Body = Cut($"{heading} · {text}", 400),
                Url = TeacherUrl(lead.SectionId!.Value, on, at), Tag = lead.Tag, ExpiresAt = expires,
            });
    }

    static DateTimeOffset Latest(DateTimeOffset a, DateTimeOffset? b) => b is DateTimeOffset other && other > a ? other : a;

    /// <summary>
    /// The notices of an undo, for rows about to be ended: "back on" to the students of each section that were told
    /// about the change, and one "undone" per group to the other teaching accounts and, when someone else undid it,
    /// to its maker. Call after <see cref="ClearUnsentAsync"/>.
    /// </summary>
    /// <param name="name">Who undid it, as people see them: a shown name, or "Classy staff".</param>
    /// <param name="actingTeacherId">The account that undid it, which isn't told; null for staff.</param>
    public static async Task AddUndoneAsync(AppDb db, IReadOnlyCollection<ClassChange> rows, string name, int? actingTeacherId, DateTimeOffset now, CancellationToken ct)
    {
        var live = rows.Where(r => r.SectionId is not null).ToList();
        if (live.Count == 0) return;
        var ids = live.Select(r => r.SectionId!.Value).Distinct().ToList();
        var courses = await db.Sections.Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.CourseId }).ToDictionaryAsync(s => s.Id, s => s.CourseId, ct);
        foreach (var c in live)
        {
            if (!courses.TryGetValue(c.SectionId!.Value, out var courseId) || !await AnnouncedAsync(db, c.SectionId.Value, c.Tag, ct)) continue;
            var extra = c.Kind == ClassChanges.Extra;
            var (date, start) = extra ? (c.NewDate!.Value, c.NewStart!.Value) : (c.OrigDate!.Value, c.OrigStart!.Value);
            db.TeachNotices.Add(new TeachNotice
            {
                Audience = TeachNotice.Students, SectionId = c.SectionId, GroupId = c.GroupId, Kind = BackOn, CreatedAt = now,
                Title = Cut(extra ? $"{c.CourseCode} extra class removed" : $"{c.CourseCode} is back on", 120),
                Body = Cut(extra ? $"{When(date, start)} is off." : $"{When(date, start)}, {RoomText(c.OrigRoom)}, as on the timetable.", 400),
                Url = StudentUrl(courseId, date, start), Tag = c.Tag, ExpiresAt = ChangeRules.LastStartUtc(c),
            });
        }

        foreach (var group in live.GroupBy(r => r.GroupId))
        {
            var ordered = group.OrderBy(c => c.CourseCode, StringComparer.Ordinal).ThenBy(c => c.SectionId).ToList();
            var lead = ordered[0];
            var codes = string.Join(" · ", ordered.Select(c => c.CourseCode).Distinct());
            var day = Day(lead.Kind == ClassChanges.Extra ? lead.NewDate!.Value : lead.OrigDate!.Value);
            var (on, at) = lead.Kind == ClassChanges.Extra ? (lead.NewDate!.Value, lead.NewStart!.Value) : (lead.OrigDate!.Value, lead.OrigStart!.Value);
            var told = (await TeachersAsync(db, lead.SemesterId, ordered.Select(c => c.SectionId!.Value).ToList(), ct))
                .Concat(ordered.Where(c => c.TeacherId is not null).Select(c => c.TeacherId!.Value)).Distinct().Where(id => id != actingTeacherId);
            foreach (var teacherId in told)
                db.TeachNotices.Add(new TeachNotice
                {
                    Audience = TeachNotice.ToTeacher, TeacherId = teacherId, SectionId = lead.SectionId, GroupId = lead.GroupId, Kind = Undone, CreatedAt = now,
                    Title = Cut($"{codes}: the change on {day} was undone by {name}", 120), Body = "",
                    Url = TeacherUrl(lead.SectionId!.Value, on, at), Tag = lead.Tag, ExpiresAt = ChangeRules.LastStartUtc(lead),
                });
        }
    }

    /// <summary>How many notices one round sends at most; the rest wait for the next round, 30 seconds later.</summary>
    public const int RoundSize = 200;
    /// <summary>How long a claim holds. A notice claimed longer ago and still not sent was dropped mid-send (a crash): it is sent again.</summary>
    public static readonly TimeSpan ClaimFor = TimeSpan.FromMinutes(2);
    const int LongestTtl = 3 * 86_400;

    /// <summary>
    /// Sends the notices that are waiting, oldest first. Each is claimed before it is sent, with one conditional
    /// update, so two rounds at once (or an old and a new process) never both send it, and an undo that lands
    /// mid-send knows students were told. Without a crash each notice is pushed once; one whose sender died between
    /// claiming and sending goes out again after <see cref="ClaimFor"/> (the tag replaces it on the phone).
    /// A notice past its <c>ExpiresAt</c> (the class has started) is marked sent without a push; a teacher's still
    /// shows in their inbox. Returns how many notices were pushed.
    /// </summary>
    public static async Task<int> RoundAsync(AppDb db, PushSender push, DateTimeOffset now, CancellationToken ct)
    {
        // Stored times are UTC, and the database compares them as stored.
        now = now.ToUniversalTime();
        var stale = now - ClaimFor;
        var waiting = await db.TeachNotices.AsNoTracking().Where(n => n.SentAt == null).OrderBy(n => n.Id).Take(RoundSize).ToListAsync(ct);
        var pushed = 0;
        foreach (var n in waiting)
        {
            // Nobody left to tell: the class has started, or the section (and so its students) is gone.
            if (n.ExpiresAt <= now || (n.Audience == TeachNotice.Students && n.SectionId is null) || (n.Audience == TeachNotice.ToTeacher && n.TeacherId is null))
            {
                await db.TeachNotices.Where(x => x.Id == n.Id && x.SentAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.SentAt, now), ct);
                continue;
            }
            if (n.Audience == TeachNotice.Students)
            {
                // The states of one session reach phones in the order they happened. An earlier one that another
                // round is still sending goes first: this one waits for the next round.
                if (await db.TeachNotices.AnyAsync(x => x.Audience == TeachNotice.Students && x.SectionId == n.SectionId && x.Tag == n.Tag
                        && x.Id < n.Id && x.SentAt == null && x.ClaimedAt != null && x.ClaimedAt >= stale, ct))
                    continue;
                // A later state is waiting too (this one's sender died after claiming it): only the later one is
                // sent, so "cancelled" never arrives on top of "back on". Left alone while someone holds the claim.
                if (await db.TeachNotices.AnyAsync(x => x.Audience == TeachNotice.Students && x.SectionId == n.SectionId && x.Tag == n.Tag && x.Id > n.Id, ct))
                {
                    await db.TeachNotices.Where(x => x.Id == n.Id && x.SentAt == null && (x.ClaimedAt == null || x.ClaimedAt < stale))
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.SentAt, now), ct);
                    continue;
                }
            }

            // The claim: one statement, so of two rounds only one gets the row. A row an undo or an edit deleted
            // meanwhile is claimed by nobody and never sent.
            if (await db.TeachNotices.Where(x => x.Id == n.Id && x.SentAt == null && (x.ClaimedAt == null || x.ClaimedAt < stale))
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.ClaimedAt, now), ct) != 1)
                continue;

            var ttl = n.ExpiresAt is DateTimeOffset until ? (int)Math.Clamp((until - now).TotalSeconds, 60, LongestTtl) : LongestTtl;
            var note = new Note(n.Title, n.Body, n.Url, n.Tag, ttl);
            switch (n.Audience)
            {
                case TeachNotice.Students:
                    // Everyone who picked the section, has a device and didn't mute changes: who a timetable change is told to.
                    var students = await db.Picks.Where(p => p.SectionId == n.SectionId && !p.Student.MuteChanges && db.PushSubscriptions.Any(x => x.StudentId == p.StudentId))
                        .Select(p => p.StudentId).Distinct().ToListAsync(ct);
                    if (students.Count > 0) await push.SendAsync(db, students, note, ct);
                    break;
                case TeachNotice.ToTeacher:
                    if (await db.Teachers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == n.TeacherId, ct) is { } teacher)
                        await push.SendToTeacherAsync(db, teacher, note, ct);
                    break;
                case TeachNotice.Staff:
                    // Owners who are students have devices. RSA and dashboard owners are told on the dashboard.
                    var owners = await db.Students.Where(s => s.IsOwner && db.PushSubscriptions.Any(x => x.StudentId == s.Id)).Select(s => s.Id).ToListAsync(ct);
                    if (owners.Count > 0) await push.SendAsync(db, owners, note, ct);
                    break;
            }
            await db.TeachNotices.Where(x => x.Id == n.Id && x.SentAt == null).ExecuteUpdateAsync(u => u.SetProperty(x => x.SentAt, now), ct);
            pushed++;
        }
        return pushed;
    }
}
