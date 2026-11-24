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
            var meetings = semester is null ? [] : await db.Sections.Where(s => s.Course.SemesterId == semester.Id)
                .SelectMany(s => s.Meetings).Select(m => new { m.Room, m.Day, m.Date, m.Start, m.End }).ToListAsync(ct);

            // Every room the timetable uses, under its first spelling.
            var rooms = meetings.Select(m => m.Room.Trim()).Where(r => !NotARoom(r))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // On a day off weekly classes don't meet; a class set for that date still does.
            var dayOff = semester is null ? null : await db.DaysOff.Where(d => d.SemesterId == semester.Id && d.Date == day).Select(d => d.Name).FirstOrDefaultAsync(ct);
            // What's on in each room that day, as students' timetables have it: weekly classes only in the teaching weeks,
            // and with the planner on, its published changes (cancelled and moved classes, added classes and exams).
            List<(string Room, TimeOnly Start, TimeOnly End)> occupied = [];
            if (semester is not null && await PlannerEngine.Enabled(db, config, ct))
                occupied = (await PlannerEngine.Calendar(db, semester.Id, day.Value, day.Value, null, ct)).Where(e => !e.Stale)
                    .Select(e => (e.Room, TimeOnly.ParseExact(e.Start, "HH:mm", CultureInfo.InvariantCulture), TimeOnly.ParseExact(e.End, "HH:mm", CultureInfo.InvariantCulture))).ToList();
            else if (semester is not null)
            {
                var span = await SemesterSpan.ForAsync(db, semester, ct);
                occupied = meetings.Where(m => m.Date == day || (m.Date is null && span.Weekly(m.Day, day.Value, true)))
                    .Select(m => (m.Room, m.Start, m.End)).ToList();
            }
            var thatDay = occupied.ToLookup(m => m.Room.Trim(), StringComparer.OrdinalIgnoreCase);

            var free = new List<(string Room, string Building, string? Until)>();
            var busy = 0;
            foreach (var room in rooms)
            {
                var classes = thatDay[room];
                if (classes.Any(m => m.Start <= time && time < m.End)) { busy++; continue; }
                // Free until its next class that day, or for the rest of the day.
                var next = classes.Where(m => m.Start > time).Select(m => (TimeOnly?)m.Start).Min();
                free.Add((room, Building(room), next is null ? null : Timeslots.Format(next.Value)));
            }

            return Results.Ok(new
            {
                date = day.Value.ToString("yyyy-MM-dd"),
                at = Timeslots.Format(time),
                // On a day with no classes at all (a weekend), every room shows as free: the page says so.
                classesThatDay = thatDay.Count > 0,
                dayOff,
                busy,
                rooms = free.OrderBy(r => r.Building).ThenBy(r => r.Room, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new { room = r.Room, building = r.Building, freeUntil = r.Until }),
            });
        }).RequireAuthorization(Session.SignedInPolicy);
    }
}
