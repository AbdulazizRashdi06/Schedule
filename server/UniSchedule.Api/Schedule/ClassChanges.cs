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
