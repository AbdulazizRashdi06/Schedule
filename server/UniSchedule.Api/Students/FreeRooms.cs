using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Students;

/// <summary>
/// Rooms with no class at a given time, worked out from the published timetable: every room any class uses this
/// semester, minus the ones with a class then, and how long each stays free. Timetable data only, nothing about students.
/// </summary>
public static partial class FreeRooms
{
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);

    /// <summary>Room cells that aren't rooms: "TBA", "N/A", "-", "Online".</summary>
    [GeneratedRegex(@"^(tba|tbd|n/?a|-+|online)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotARoom();

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
        api.MapGet("/rooms/free", async (string? date, string? at, AppDb db, TimeProvider clock, CancellationToken ct) =>
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
            var rooms = meetings.Select(m => m.Room.Trim()).Where(r => r.Length > 0 && !NotARoom().IsMatch(r))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var weekday = (int)day.Value.DayOfWeek;
            var thatDay = meetings.Where(m => m.Date == day || (m.Date is null && m.Day == weekday))
                .ToLookup(m => m.Room.Trim(), StringComparer.OrdinalIgnoreCase);

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
                busy,
                rooms = free.OrderBy(r => r.Building).ThenBy(r => r.Room, StringComparer.OrdinalIgnoreCase)
                    .Select(r => new { room = r.Room, building = r.Building, freeUntil = r.Until }),
            });
        }).RequireAuthorization();
    }
}
