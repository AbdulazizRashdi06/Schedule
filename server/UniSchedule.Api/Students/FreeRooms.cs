using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Students;

/// <summary>
/// Rooms with no class at a given time, worked out from the published timetable (and the batch reps' published changes
/// when the planner is on): every room any class uses this semester, minus the ones with a class or exam then, and how
/// long each stays free. Nothing about students.
/// </summary>
public static partial class FreeRooms
{
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);

    /// <summary>Placeholder locations, online classes and activities without a campus room.</summary>
    [GeneratedRegex(@"^(?:tba|tbd|n/?a|-+|online|coursera|field[\s-]*trips?|excursions?|internships?|thesis|theses)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotARoomPattern();

    /// <summary>Whether a class's location is no campus room: blank, a placeholder, online, or an activity elsewhere.</summary>
    internal static bool NotARoom(string? room) => room?.Trim() is not { Length: > 0 } r || NotARoomPattern().IsMatch(r);

    [GeneratedRegex(@"^[A-Za-z]+")]
    private static partial Regex Letters();

    /// <summary>The building a room is grouped under: its leading letters ("A101" → "A", "Lab L2" → "Lab"), else "Other".</summary>
    public static string Building(string room)
    {
        var m = Letters().Match(room);
        if (!m.Success) return "Other";
        var letters = m.Value;
        return letters.Length <= 2 ? letters.ToUpperInvariant() : char.ToUpperInvariant(letters[0]) + letters[1..].ToLowerInvariant();
    }

    /// <summary>One class or exam in a room on a date. <paramref name="Change"/>: the teacher's change that put it there, if one did.</summary>
    public sealed record Occupied(string Room, TimeOnly Start, TimeOnly End, string Label, int? SectionId, Teachers.ClassChange? Change = null);

    /// <summary>
    /// What is in each room on a date: classes in the teaching weeks, reps' changes when the planner is on, and
    /// teachers' changes always. <c>Rooms</c> is every room the timetable uses, under its first spelling: a room a
    /// teacher typed occupies, but is never added to that list.
    /// </summary>
    public static async Task<(List<string> Rooms, List<Occupied> Occupied, string? DayOff)> OccupiedAsync(AppDb db, Semester? semester, DateOnly day, IConfiguration config, CancellationToken ct)
    {
        if (semester is null) return ([], [], null);
        var sections = await db.Sections.Where(s => s.Course.SemesterId == semester.Id).Include(s => s.Course).Include(s => s.Meetings).AsSplitQuery().ToListAsync(ct);
        // Every room the timetable uses, under its first spelling.
        var rooms = sections.SelectMany(s => s.Meetings).OrderBy(m => m.Id).Select(m => m.Room.Trim()).Where(r => !NotARoom(r))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // On a day off weekly classes don't meet; a class set for that date still does.
        var dayOff = await db.DaysOff.Where(d => d.SemesterId == semester.Id && d.Date == day).Select(d => d.Name).FirstOrDefaultAsync(ct);
        // Teachers' changes come through the one reader the timetables use, so this page can't disagree with them.
        var changes = await ClassChanges.LoadAsync(db, semester.Id, sections, ct);
        var byId = sections.ToDictionary(s => s.Id);
        // What's on in each room that day, as students' timetables have it: weekly classes only in the teaching weeks,
        // and with the planner on, its published changes (cancelled and moved classes, added classes and exams).
        List<Occupied> occupied = [];
        if (await PlannerEngine.Enabled(db, config, ct))
        {
            foreach (var e in (await PlannerEngine.Calendar(db, semester.Id, day, day, null, ct, shadowed: changes.Empty ? null : changes.Shadows)).Where(e => !e.Stale))
            {
                var start = TimeOnly.ParseExact(e.Start, "HH:mm", CultureInfo.InvariantCulture);
                // A university class is matched by its section and start, as a teacher's change is keyed.
                var section = e.Status == "university" && e.Source is { } source ? byId.GetValueOrDefault(source.SectionId) : null;
                if (section is not null && changes.Shadows(section.Id, day, start)) continue;
                occupied.Add(new(e.Room, start, TimeOnly.ParseExact(e.End, "HH:mm", CultureInfo.InvariantCulture), section?.Course.Code ?? e.Title, section?.Id));
            }
        }
        else
        {
            var span = await SemesterSpan.ForAsync(db, semester, ct);
            foreach (var s in sections)
            foreach (var m in s.Meetings)
                if (ChangeSet.Happens(span, m, day) && !changes.Skip(s, m).Contains(day))
                    occupied.Add(new(m.Room, m.Start, m.End, s.Course.Code, s.Id));
        }
        // The class at its new time or room, and extra classes. "To be announced" takes no room.
        foreach (var s in sections)
        foreach (var a in changes.AddedTo(s))
            if (a.Date == day && !string.IsNullOrWhiteSpace(a.Room))
                occupied.Add(new(a.Room, a.Start, a.End, s.Course.Code, s.Id, a.Change));
        return (rooms, occupied, dayOff);
    }

    /// <summary>The rooms free at a time, each with when its next class that day starts, and how many are busy.</summary>
    static (List<(string Room, string Building, string? Until)> Free, int Busy, bool Any) FreeAt(List<string> rooms, IEnumerable<Occupied> occupied, TimeOnly from, TimeOnly to)
    {
        // Rooms are compared without their spaces or case: "A 101" is "a101".
        var thatDay = occupied.ToLookup(m => ClassChanges.RoomKey(m.Room));
        var free = new List<(string Room, string Building, string? Until)>();
        var busy = 0;
        foreach (var room in rooms)
        {
            var classes = thatDay[ClassChanges.RoomKey(room)];
            // Busy when a class overlaps the interval; for a single moment, when one has started and hasn't ended.
            if (classes.Any(m => (m.Start < to || m.Start == from) && from < m.End)) { busy++; continue; }
            // Free until its next class that day, or for the rest of the day.
            var next = classes.Where(m => m.Start >= to && m.Start > from).Select(m => (TimeOnly?)m.Start).Min();
            free.Add((room, Building(room), next is null ? null : Timeslots.Format(next.Value)));
        }
        return (free, busy, thatDay.Count > 0);
    }

    static object Answer(DateOnly day, TimeOnly time, string? dayOff, (List<(string Room, string Building, string? Until)> Free, int Busy, bool Any) found) => new
    {
        date = day.ToString("yyyy-MM-dd"),
        at = Timeslots.Format(time),
        // On a day with no classes at all (a weekend), every room shows as free: the page says so.
        classesThatDay = found.Any,
        dayOff,
        busy = found.Busy,
        rooms = found.Free.OrderBy(r => r.Building).ThenBy(r => r.Room, StringComparer.OrdinalIgnoreCase)
            .Select(r => new { room = r.Room, building = r.Building, freeUntil = r.Until }),
    };

    /// <summary>
    /// For a teacher picking a room: the rooms free for the whole of an interval on a date, in the shape of
    /// <c>/rooms/free</c>. <paramref name="ignore"/> leaves out what the class being changed occupies itself.
    /// </summary>
    public static async Task<object> FreeForAsync(AppDb db, Semester? semester, DateOnly day, TimeOnly start, TimeOnly end, Func<Occupied, bool> ignore, IConfiguration config, CancellationToken ct)
    {
        var (rooms, occupied, dayOff) = await OccupiedAsync(db, semester, day, config, ct);
        return Answer(day, start, dayOff, FreeAt(rooms, occupied.Where(o => !ignore(o)), start, end));
    }

    public static void MapFreeRooms(this RouteGroupBuilder api)
    {
        // Free rooms on a date (default today) at a time (default now), both in Oman time.
        api.MapGet("/rooms/free", async (string? date, string? at, AppDb db, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().ToOffset(Oman);
            var day = date is null ? DateOnly.FromDateTime(now.DateTime) : ScheduleImport.ParseDate(date);
            if (day is null) return Errors.BadRequest("Pick a date like 2026-11-04.");
            var time = TimeOnly.FromDateTime(now.DateTime);
            if (at is not null && !TimeOnly.TryParseExact(at, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
                return Errors.BadRequest("Pick a time like 13:15.");

            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            var (rooms, occupied, dayOff) = await OccupiedAsync(db, semester, day.Value, config, ct);
            // Busy at that moment: a class that started and hasn't ended.
            return Results.Ok(Answer(day.Value, time, dayOff, FreeAt(rooms, occupied, time, time)));
        }).RequireAuthorization(Session.SignedInPolicy);
    }
}
