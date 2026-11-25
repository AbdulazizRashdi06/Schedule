using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Teachers;

/// <summary>
/// One section a teacher has to do with: <paramref name="Own"/> = listed under one of their names,
/// <paramref name="Added"/> = they added it, <paramref name="Hidden"/> = they took it off their timetable.
/// </summary>
public sealed record Taught(Course Course, Section Section, bool Own, bool Added, bool Hidden);

/// <summary>
/// Which classes are a teacher's, and which of them are one real class. This is the only definition of "on my
/// timetable" (not hidden), of "may change" (every row, hidden or not) and of a merged class (<see cref="Siblings"/>).
/// </summary>
public static class TeacherSchedule
{
    /// <summary>Marks an office-hours item among a teacher's items (<see cref="StudentSchedule.Item.EventKind"/>).</summary>
    public const string OfficeKind = "office";
    public const string OfficeTitle = "Office hours";

    static readonly IReadOnlySet<DateOnly> NoDates = new HashSet<DateOnly>();

    /// <summary>
    /// The key sessions of one real class share: the same date (or weekday, for a weekly class), times and room. Null
    /// when the room is blank or not a campus room (online, a field trip…): those are never merged.
    /// </summary>
    public static string? MergeKey(DateOnly? date, int day, TimeOnly start, TimeOnly end, string room) =>
        FreeRooms.NotARoom(room) ? null
            : $"{(date is DateOnly on ? PlannerEngine.Iso(on) : "w" + day.ToString(CultureInfo.InvariantCulture))}|{Timeslots.Format(start)}|{Timeslots.Format(end)}|{ClassChanges.RoomKey(room)}";

    static string Padded(Section s) => s.Number.PadLeft(4, '0');

    /// <summary>The key of a session a change put on: one teacher action is one real class, whatever room it was given.</summary>
    public static string GroupKey(ClassChange c) => "g:" + c.GroupId;

    /// <param name="tracked">False for a caller that only reads (the reminders worker): the same objects, one per row, not kept by the context.</param>
    internal static Task<List<Section>> SemesterSectionsAsync(AppDb db, int semesterId, CancellationToken ct, bool tracked = true) =>
        (tracked ? db.Sections : db.Sections.AsNoTrackingWithIdentityResolution())
            .Where(s => s.Course.SemesterId == semesterId).Include(s => s.Course).Include(s => s.Meetings).AsSplitQuery().ToListAsync(ct);

    /// <summary>The sections a teacher has to do with this semester: under their names, plus those they added. Hidden ones are marked, not removed.</summary>
    public static Task<List<Taught>> SectionsAsync(AppDb db, int semesterId, TeachActor actor, CancellationToken ct) =>
        SectionsAsync(db, semesterId, actor.Profile, actor.NameKeys, ct);

    /// <summary>The same for an account (or, with no <paramref name="profile"/>, a bare timetable name) and the name keys whose sections are theirs.</summary>
    public static async Task<List<Taught>> SectionsAsync(AppDb db, int semesterId, Teacher? profile, IReadOnlyCollection<string> nameKeys, CancellationToken ct) =>
        await SectionsAsync(db, semesterId, profile, nameKeys, await SemesterSectionsAsync(db, semesterId, ct), ct);

    /// <summary>The same, from the semester's sections already loaded (with course and meetings).</summary>
    internal static async Task<List<Taught>> SectionsAsync(AppDb db, int semesterId, Teacher? profile, IReadOnlyCollection<string> nameKeys,
        IReadOnlyCollection<Section> sections, CancellationToken ct)
    {
        List<TeacherSection> rows = profile is null ? [] : await db.TeacherSections
            .Where(r => r.TeacherId == profile.Id && r.SemesterId == semesterId && r.SectionId != null).ToListAsync(ct);
        return Mark(sections, nameKeys, rows);
    }

    static List<Taught> Mark(IEnumerable<Section> sections, IReadOnlyCollection<string> nameKeys, IReadOnlyCollection<TeacherSection> rows)
    {
        var keys = nameKeys.Where(k => k.Length > 0).ToHashSet();
        var added = rows.Where(r => r.Mode == TeacherSection.AddedMode && r.SectionId is not null).Select(r => r.SectionId!.Value).ToHashSet();
        var hidden = rows.Where(r => r.Mode == TeacherSection.HiddenMode && r.SectionId is not null).Select(r => r.SectionId!.Value).ToHashSet();
        var taught = new List<Taught>();
        foreach (var s in sections)
        {
            var own = keys.Count > 0 && keys.Contains(TeacherNames.NameKey(s.Instructor));
            if (own || added.Contains(s.Id)) taught.Add(new(s.Course, s, own, added.Contains(s.Id), hidden.Contains(s.Id)));
        }
        return taught.OrderBy(t => t.Course.Code, StringComparer.Ordinal).ThenBy(t => Padded(t.Section), StringComparer.Ordinal).ThenBy(t => t.Section.Id).ToList();
    }

    /// <summary>
    /// The accounts behind these sections: who holds each instructor's name (by name key, an auto or approved link),
    /// and who added each section.
    /// </summary>
    internal static async Task<(Dictionary<string, Teacher> Holders, ILookup<int, Teacher> Adders)> AccountsAsync(AppDb db,
        IReadOnlyCollection<(int Id, string Instructor)> sections, CancellationToken ct)
    {
        var keys = sections.Select(s => TeacherNames.NameKey(s.Instructor)).Where(k => k.Length > 0).Distinct().ToList();
        var ids = sections.Select(s => s.Id).Distinct().ToList();
        var held = keys.Count == 0 ? [] : await (
            from n in db.TeacherNames
            where keys.Contains(n.NameKey) && (n.State == TeacherName.Auto || n.State == TeacherName.Approved)
            join t in db.Teachers on n.TeacherId equals t.Id
            select new { n.NameKey, Teacher = t }).ToListAsync(ct);
        var added = ids.Count == 0 ? [] : await (
            from r in db.TeacherSections
            where r.Mode == TeacherSection.AddedMode && r.SectionId != null && ids.Contains(r.SectionId.Value)
            join t in db.Teachers on r.TeacherId equals t.Id
            select new { SectionId = r.SectionId!.Value, Teacher = t }).ToListAsync(ct);
        // One account per name: the unique index on held links says so.
        return (held.GroupBy(x => x.NameKey).ToDictionary(g => g.Key, g => g.First().Teacher),
            added.OrderBy(x => x.Teacher.Id).ToLookup(x => x.SectionId, x => x.Teacher));
    }

    /// <summary>The teaching accounts that have each section (named by an auto or approved link, or added it).</summary>
    public static async Task<ILookup<int, Teacher>> TeachersOfAsync(AppDb db, int semesterId, IReadOnlyCollection<int> sectionIds, CancellationToken ct)
    {
        var sections = (await db.Sections.Where(s => sectionIds.Contains(s.Id) && s.Course.SemesterId == semesterId)
            .Select(s => new { s.Id, s.Instructor }).ToListAsync(ct)).Select(s => (s.Id, s.Instructor)).ToList();
        var (holders, adders) = await AccountsAsync(db, sections, ct);
        return sections
            .SelectMany(s => (holders.TryGetValue(TeacherNames.NameKey(s.Instructor), out var named) ? [named] : Array.Empty<Teacher>())
                .Concat(adders[s.Id]).DistinctBy(t => t.Id).Select(t => (SectionId: s.Id, Teacher: t)))
            .ToLookup(x => x.SectionId, x => x.Teacher);
    }

    /// <summary>Classy users per section: how many students picked it.</summary>
    public static async Task<Dictionary<int, int>> SizesAsync(AppDb db, IReadOnlyCollection<int> sectionIds, CancellationToken ct) =>
        sectionIds.Count == 0 ? [] : await db.Picks.Where(p => p.SectionId != null && sectionIds.Contains(p.SectionId.Value))
            .GroupBy(p => p.SectionId!.Value).Select(g => new { g.Key, Count = g.Select(p => p.StudentId).Distinct().Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    /// <summary>
    /// One real class: the sessions that share a merge key, one section of each course (two sections of one course at
    /// the same time are two classes, and show as a clash). The members are in course-code order, so the first is the lead.
    /// </summary>
    internal sealed record Block(string? Key, IReadOnlyList<(Taught Class, Meeting Meeting)> Members)
    {
        public (Taught Class, Meeting Meeting) Lead => Members[0];
        public bool Merged => Members.Count > 1;
    }

    /// <summary>The codes of one real class as people read them: "BE1101 · IBSM1101 · LOG1101".</summary>
    internal static string Codes(IEnumerable<Taught> members) => string.Join(" · ", members.Select(m => m.Course.Code).Distinct());

    /// <summary>Every session of these sections, with those of one real class put together.</summary>
    internal static List<Block> Blocks(IEnumerable<Taught> taught)
    {
        var blocks = new List<Block>();
        var keyed = new List<(string Key, Taught Class, Meeting Meeting)>();
        foreach (var t in taught)
        foreach (var m in t.Section.Meetings)
        {
            if (MergeKey(m.Date, m.Day, m.Start, m.End, m.Room) is { } key) keyed.Add((key, t, m));
            else blocks.Add(new(null, [(t, m)]));
        }
        foreach (var sameKey in keyed.GroupBy(x => x.Key))
        {
            // The nth section of each course goes into the nth block, so no block holds two sections of one course.
            var ranked = sameKey.GroupBy(x => x.Class.Course.Id)
                .SelectMany(course => course.OrderBy(x => Padded(x.Class.Section), StringComparer.Ordinal).ThenBy(x => x.Class.Section.Id).ThenBy(x => x.Meeting.Id)
                    .Select((x, rank) => (x.Class, x.Meeting, Rank: rank)));
            foreach (var block in ranked.GroupBy(x => x.Rank).OrderBy(g => g.Key))
                blocks.Add(new(sameKey.Key, block.OrderBy(x => x.Class.Course.Code, StringComparer.Ordinal).ThenBy(x => x.Class.Section.Id)
                    .Select(x => (x.Class, x.Meeting)).ToList()));
        }
        return blocks;
    }

    /// <summary>
    /// One session a change put on (the class at its new time or room, or an extra class), with every section of the
    /// change's group that is among the sections asked about. The members are in course-code order, so the first is the lead.
    /// </summary>
    internal sealed record PutOn(string Key, IReadOnlyList<(Taught Class, Added Session)> Members)
    {
        public (Taught Class, Added Session) Lead => Members[0];
        public bool Merged => Members.Count > 1;
    }

    /// <summary>The sessions changes put on for these sections, one per teacher action.</summary>
    internal static List<PutOn> PutOnBlocks(IEnumerable<Taught> taught, ChangeSet changes)
    {
        if (changes.Empty) return [];
        return taught.DistinctBy(t => t.Section.Id).SelectMany(t => changes.AddedTo(t.Section).Select(a => (Class: t, Session: a)))
            .GroupBy(x => (x.Session.Change.GroupId, x.Session.Date, x.Session.Start))
            .Select(g => new PutOn(GroupKey(g.First().Session.Change),
                g.OrderBy(x => x.Class.Course.Code, StringComparer.Ordinal).ThenBy(x => x.Class.Section.Id).ToList()))
            .ToList();
    }

    /// <summary>Whether a meeting is held on a date, as the university lists it: set for that date, or weekly on that weekday in the teaching weeks.</summary>
    internal static bool On(SemesterSpan span, Meeting m, DateOnly date) => ChangeSet.Happens(span, m, date);

    /// <summary>The same with no teacher's change in play.</summary>
    public static List<Taught> Siblings(IReadOnlyList<Taught> taught, SemesterSpan span, int sectionId, DateOnly date, TimeOnly start) =>
        Siblings(taught, span, ChangeSet.None, sectionId, date, start);

    /// <summary>
    /// The other course codes of one real class. This is the server's only definition of a merged class.
    /// For a session as the university lists it: the sections among <paramref name="taught"/>, of a different course,
    /// that have a session on that date with the same merge key as this section's session at <paramref name="start"/>
    /// and no live change on it. For a session that has a live change, or that a change put on: the sections of that
    /// change's group. So a code that already has its own change is reached through that change, never as a sibling.
    /// A section the teacher hid ("not mine") is on no block of their timetable, so it is never a sibling by the
    /// timetable alone; it still belongs to a change it is already part of.
    /// </summary>
    public static List<Taught> Siblings(IReadOnlyList<Taught> taught, SemesterSpan span, ChangeSet changes, int sectionId, DateOnly date, TimeOnly start)
    {
        var changed = changes.On(sectionId, date, start)
            ?? taught.Where(t => t.Section.Id == sectionId).SelectMany(t => changes.AddedTo(t.Section))
                .Where(a => a.Date == date && a.Start == start).Select(a => a.Change).FirstOrDefault();
        if (changed is not null)
            return taught.Where(t => t.Section.Id != sectionId && changes.All.Any(c => c.GroupId == changed.GroupId && c.SectionId == t.Section.Id))
                .DistinctBy(t => t.Section.Id).ToList();
        if (taught.Any(t => t.Section.Id == sectionId && t.Hidden)) return [];
        return Blocks(taught.Where(t => !t.Hidden))
            .Where(b => b.Members.Any(m => m.Class.Section.Id == sectionId && m.Meeting.Start == start && On(span, m.Meeting, date)))
            .SelectMany(b => b.Members).Select(m => m.Class)
            .Where(t => t.Section.Id != sectionId && changes.On(t.Section.Id, date, start) is null)
            .DistinctBy(t => t.Section.Id).ToList();
    }

    /// <summary>
    /// How many Classy users each merged class has: the distinct students over its sections, by merge key. A class a
    /// change put on counts under its own key ("g:" and the change's group).
    /// </summary>
    public static async Task<Dictionary<string, int>> MergeSizesAsync(AppDb db, IReadOnlyList<Taught> taught, ChangeSet changes, CancellationToken ct)
    {
        var merged = Blocks(taught).Where(b => b.Merged).DistinctBy(b => b.Key)
            .Select(b => (Key: b.Key!, Sections: b.Members.Select(m => m.Class.Section.Id).ToList()))
            .Concat(PutOnBlocks(taught, changes).Where(b => b.Merged).DistinctBy(b => b.Key)
                .Select(b => (b.Key, Sections: b.Members.Select(m => m.Class.Section.Id).ToList()))).ToList();
        var ids = merged.SelectMany(b => b.Sections).Distinct().ToList();
        if (ids.Count == 0) return [];
        // Only ever counted: nothing about who they are leaves this function.
        var picked = (await db.Picks.Where(p => p.SectionId != null && ids.Contains(p.SectionId.Value))
            .Select(p => new { SectionId = p.SectionId!.Value, p.StudentId }).ToListAsync(ct)).ToLookup(p => p.SectionId, p => p.StudentId);
        return merged.ToDictionary(b => b.Key, b => b.Sections.SelectMany(id => picked[id]).Distinct().Count());
    }

    /// <summary>Pick-shaped objects for the sections, to ask the reps' published plan what reaches them. Never saved.</summary>
    internal static List<Pick> AsPicks(IEnumerable<Taught> taught) =>
        taught.Select(t => new Pick { CourseId = t.Course.Id, SectionId = t.Section.Id, Course = t.Course }).ToList();

    /// <summary>
    /// What each teacher has on: their classes (one item per real class), own slots, office hours and reps' events. The
    /// same items the student paths use. The key is the teacher id; every item has student id 0, which is no student, and
    /// these lists are never mixed with student lists. Office hours are slot items marked <see cref="OfficeKind"/>.
    /// </summary>
    /// <param name="cancelled">Filled with the sessions that stay visible though they aren't on: cancelled ones, and the
    /// old slot of a moved one. One per real class; never in the returned lists.</param>
    /// <param name="tracked">False for a caller that only reads (the reminders worker): the semester's sections aren't kept by the context.</param>
    public static async Task<Dictionary<int, List<StudentSchedule.Item>>> ItemsAsync(AppDb db, Semester semester, IReadOnlyCollection<Teacher> teachers,
        bool slots, bool planner, CancellationToken ct, List<(int TeacherId, StudentSchedule.Item Item)>? cancelled = null, bool tracked = true)
    {
        var result = teachers.Select(t => t.Id).Distinct().ToDictionary(id => id, _ => new List<StudentSchedule.Item>());
        if (result.Count == 0) return result;
        var ids = result.Keys.ToList();
        var sections = await SemesterSectionsAsync(db, semester.Id, ct, tracked);
        var keys = (await db.TeacherNames.Where(n => ids.Contains(n.TeacherId) && (n.State == TeacherName.Auto || n.State == TeacherName.Approved))
            .Select(n => new { n.TeacherId, n.NameKey }).ToListAsync(ct)).ToLookup(n => n.TeacherId, n => n.NameKey);
        var rows = (await db.TeacherSections.Where(r => ids.Contains(r.TeacherId) && r.SemesterId == semester.Id && r.SectionId != null).ToListAsync(ct))
            .ToLookup(r => r.TeacherId);
        var hours = (await db.OfficeHours.Where(h => ids.Contains(h.TeacherId)).ToListAsync(ct)).ToLookup(h => h.TeacherId);
        var own = slots
            ? (await db.TeacherSlots.Where(s => ids.Contains(s.TeacherId) && s.SemesterId == semester.Id).ToListAsync(ct)).ToLookup(s => s.TeacherId)
            : null;
        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var plan = planner ? await PlannerEngine.PublishedAsync(db, semester.Id, ct) : null;
        // Teachers' changes, through the same reader the student paths use.
        var changes = await ClassChanges.LoadAsync(db, semester.Id, sections, ct);

        foreach (var (id, items) in result)
        {
            var on = Mark(sections, keys[id].ToList(), rows[id].ToList()).Where(t => !t.Hidden).ToList();
            var (events, excluded) = plan?.For(AsPicks(on), changes.Empty ? null : changes.Shadows) ?? ([], []);
            items.AddRange(ClassItems(on, span, (s, m) =>
            {
                IReadOnlySet<DateOnly> skip = excluded.TryGetValue(PlannerEngine.Fingerprint(s, m), out var dates)
                    ? dates.Select(d => DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToHashSet()
                    : NoDates;
                return changes.Skip(s, m) is { Count: > 0 } changed ? skip.Count == 0 ? changed : skip.Union(changed).ToHashSet() : skip;
            }));
            // The class at its new time or room, and extra classes: one item per teacher action, whatever its room.
            foreach (var block in PutOnBlocks(on, changes))
            {
                var (lead, a) = block.Lead;
                items.Add(new(0, StudentSchedule.Source.Class, a.Date, (int)a.Date.DayOfWeek, a.Start, a.End, NoDates, lead.Course.Id,
                    Codes(block.Members.Select(m => m.Class)), lead.Course.Name, a.Room, lead.Section.Number, lead.Section.Instructor, lead.Section.Id, null, null,
                    a.Change.Note, null, null, a.Change.Id, a.Change.Kind, a.Change.ByName, ClassChanges.Uid(a), a.Change.SessionType, StudentSchedule.From(a.Change)));
            }
            if (cancelled is not null)
                foreach (var group in on.SelectMany(t => changes.OffIn(t.Section).Select(o => (Class: t, Session: o))).GroupBy(x => x.Session.Change.GroupId))
                {
                    var members = group.OrderBy(x => x.Class.Course.Code, StringComparer.Ordinal).ThenBy(x => x.Class.Section.Id).ToList();
                    var (lead, o) = members[0];
                    cancelled.Add((id, new(0, StudentSchedule.Source.Class, o.Date, (int)o.Date.DayOfWeek, o.Start, o.End, NoDates, lead.Course.Id,
                        Codes(members.Select(m => m.Class)), lead.Course.Name, o.Room, lead.Section.Number, lead.Section.Instructor, lead.Section.Id, o.Base.Id, null,
                        o.Change.Note, null, null, o.Change.Id, o.Change.Kind, o.Change.ByName, ClassChanges.Uid(lead.Section.Id, o.Base, o.Date), o.Change.SessionType)));
                }
            foreach (var e in events)
            {
                var date = DateOnly.ParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                items.Add(new(0, StudentSchedule.Source.Planner, date, (int)date.DayOfWeek, TimeOnly.ParseExact(e.Start, "HH:mm", CultureInfo.InvariantCulture),
                    TimeOnly.ParseExact(e.End, "HH:mm", CultureInfo.InvariantCulture), NoDates, e.CourseId, e.Code, e.Title, e.Room,
                    null, null, e.SectionId, null, null, e.Notes, e.Id, e.Kind));
            }
            // Weekly slots already stop outside the teaching weeks; office hours also skip the days off.
            foreach (var h in hours[id].OrderBy(h => h.Day).ThenBy(h => h.Start))
                items.Add(new(0, StudentSchedule.Source.Slot, null, h.Day, h.Start, h.End, span.DaysOff.Keys.Where(d => (int)d.DayOfWeek == h.Day).ToHashSet(),
                    null, OfficeTitle, OfficeTitle, h.Room, null, null, null, null, null, null, null, OfficeKind));
            if (own is not null)
                foreach (var s in own[id])
                    items.Add(new(0, StudentSchedule.Source.Slot, s.Date, s.Day, s.Start, s.End, NoDates, null, s.Title, s.Title, s.Place,
                        null, null, null, null, s.Id, s.Note, null, null));
        }
        return result;
    }

    /// <summary>
    /// One item per real class. When the codes of a merged class skip different dates, the weekly item skips all of
    /// them and each date some codes still meet on gets an item of its own, naming only those codes.
    /// </summary>
    static IEnumerable<StudentSchedule.Item> ClassItems(IReadOnlyList<Taught> on, SemesterSpan span, Func<Section, Meeting, IReadOnlySet<DateOnly>> skipOf)
    {
        foreach (var block in Blocks(on))
        {
            var members = block.Members.Select(m => (m.Class, m.Meeting, Skip: skipOf(m.Class.Section, m.Meeting))).ToList();
            var lead = members[0].Meeting;
            if (members.All(m => m.Skip.SetEquals(members[0].Skip)))
            {
                yield return Item(members.Select(m => m.Class).ToList(), lead, lead.Date, members[0].Skip);
                continue;
            }
            var union = members.SelectMany(m => m.Skip).ToHashSet();
            if (lead.Date is DateOnly dated)
            {
                var still = members.Where(m => !m.Skip.Contains(dated)).Select(m => m.Class).ToList();
                yield return still.Count > 0 ? Item(still, members.First(m => !m.Skip.Contains(dated)).Meeting, dated, NoDates) : Item(members.Select(m => m.Class).ToList(), lead, dated, union);
                continue;
            }
            yield return Item(members.Select(m => m.Class).ToList(), lead, null, union);
            foreach (var date in union.Order())
            {
                var still = members.Where(m => !m.Skip.Contains(date)).ToList();
                // Dated here, but a session of a weekly class: it keeps that date's calendar id, not one id for every such date.
                if (still.Count > 0 && span.Weekly(lead.Day, date, true))
                    yield return Item(still.Select(m => m.Class).ToList(), still[0].Meeting, date, NoDates) with
                    {
                        Uid = ClassChanges.Uid(still[0].Class.Section.Id, still[0].Meeting, date),
                    };
            }
        }
    }

    static StudentSchedule.Item Item(IReadOnlyList<Taught> members, Meeting m, DateOnly? date, IReadOnlySet<DateOnly> skip)
    {
        var lead = members[0];
        return new(0, StudentSchedule.Source.Class, date, date is DateOnly on ? (int)on.DayOfWeek : m.Day, m.Start, m.End, skip, lead.Course.Id,
            Codes(members), lead.Course.Name, m.Room, lead.Section.Number, lead.Section.Instructor, lead.Section.Id, m.Id, null, null, null, null);
    }
}
