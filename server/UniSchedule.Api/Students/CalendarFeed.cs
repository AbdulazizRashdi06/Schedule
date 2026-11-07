using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Students;

/// <summary>
/// A student's timetable as a calendar feed (iCalendar), so Apple, Google, Outlook and Samsung calendars can subscribe to
/// it and keep up with changes. Calendar apps can't sign in, so the link carries a long secret; the student can reset it.
/// </summary>
public static partial class CalendarFeed
{
    /// <summary>Weekly classes repeat for this long when the semester's teaching weeks aren't set.</summary>
    const int DefaultWeeks = 16;
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);

    static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    static string Link(HttpRequest req, string token) => $"{req.Scheme}://{req.Host}/api/calendar/{token}.ics";

    [GeneratedRegex(@"\blab", RegexOptions.IgnoreCase)]
    private static partial Regex LabRoom();
    [GeneratedRegex(@"\btut", RegexOptions.IgnoreCase)]
    private static partial Regex TutorialRoom();

    /// <summary>The class type, read from the room the way the app does: "Lab L2" is a lab, "Tut 3" a tutorial.</summary>
    static string Kind(string room) => LabRoom().IsMatch(room) ? "Lab" : TutorialRoom().IsMatch(room) ? "Tutorial" : "Lecture";

    public static void MapCalendar(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me/calendar").RequireAuthorization(Session.StudentPolicy);

        // The student's feed link, made the first time they ask for it.
        me.MapGet("", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            if (s.CalendarToken is null)
            {
                s.CalendarToken = NewToken();
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(new { url = Link(http.Request, s.CalendarToken) });
        });

        // A new link; calendars subscribed with the old one stop updating.
        me.MapPost("/reset", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            s.CalendarToken = NewToken();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { url = Link(http.Request, s.CalendarToken) });
        });

        // The feed itself, fetched by calendar apps with the secret in the link.
        api.MapGet("/calendar/{token}.ics", async (string token, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var s = token.Length >= 40 ? await db.Students.FirstOrDefaultAsync(x => x.CalendarToken == token, ct) : null;
            if (s is null) return Errors.NotFound("This calendar link was reset or doesn't exist. Add the new one from Classy.");
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            return Results.Text(await Build(db, s.Id, semester, clock.GetUtcNow(), ct), "text/calendar; charset=utf-8");
        }).RequireRateLimiting(PublicTimetableEndpoints.Limit);
    }

    /// <summary>The iCalendar text: the student's classes and own slots in the current semester, in Oman time.</summary>
    public static async Task<string> Build(AppDb db, int studentId, Semester? semester, DateTimeOffset now, CancellationToken ct)
    {
        var ics = new Ics();
        ics.Line("BEGIN:VCALENDAR");
        ics.Line("VERSION:2.0");
        ics.Line("PRODID:-//Classy//Timetable//EN");
        ics.Line("CALSCALE:GREGORIAN");
        ics.Line("METHOD:PUBLISH");
        ics.Line("X-WR-CALNAME:Classy");
        ics.Line("X-WR-TIMEZONE:Asia/Muscat");
        // A hint to refresh hourly; each calendar app decides for itself in the end.
        ics.Line("REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        ics.Line("X-PUBLISHED-TTL:PT1H");
        // Oman has no daylight saving: one fixed offset.
        foreach (var l in new[] { "BEGIN:VTIMEZONE", "TZID:Asia/Muscat", "BEGIN:STANDARD", "DTSTART:19700101T000000", "TZOFFSETFROM:+0400", "TZOFFSETTO:+0400", "TZNAME:+04", "END:STANDARD", "END:VTIMEZONE" })
            ics.Line(l);

        if (semester is not null)
        {
            // Weekly classes run from week 1 to the last week; without set weeks, from the week it was published for 16 weeks.
            var first = SemesterWeeks.Sunday(semester.Week1Start
                ?? DateOnly.FromDateTime((semester.PublishedAt ?? now).ToOffset(Oman).DateTime));
            var last = first.AddDays((semester.Week1Start is not null && semester.LastWeek is int weeks ? weeks : DefaultWeeks) * 7 - 1);
            var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

            var picks = await db.Picks.Where(p => p.StudentId == studentId && p.Course.SemesterId == semester.Id && p.SectionId != null)
                .Include(p => p.Course).Include(p => p.Section).ThenInclude(x => x!.Meetings)
                .AsSplitQuery().ToListAsync(ct);
            foreach (var p in picks.OrderBy(p => p.Course.Code))
            foreach (var m in p.Section!.Meetings)
            {
                var details = string.Join(" · ", new[] { p.Course.Code, $"Section {p.Section.Number}", p.Section.Instructor }.Where(x => !string.IsNullOrWhiteSpace(x)));
                Event(ics, $"class-{p.Section.Id}-{m.Id}@classy", stamp, $"{p.Course.Name} · {Kind(m.Room)}", m.Room, details,
                    m.Date, m.Day, m.Start, m.End, first, last);
            }

            var slots = await db.CustomSlots.Where(x => x.StudentId == studentId && x.SemesterId == semester.Id).ToListAsync(ct);
            foreach (var slot in slots)
                Event(ics, $"slot-{slot.Id}@classy", stamp, slot.Title, slot.Place, slot.Note ?? "", slot.Date, slot.Day, slot.Start, slot.End, first, last);
        }

        ics.Line("END:VCALENDAR");
        return ics.ToString();
    }

    /// <summary>One event: on its date, or weekly on its weekday from the semester's first week to its last.</summary>
    static void Event(Ics ics, string uid, string stamp, string summary, string location, string description,
        DateOnly? date, int day, TimeOnly start, TimeOnly end, DateOnly first, DateOnly last)
    {
        var on = date ?? first.AddDays(day);
        if (date is null && on > last) return;
        ics.Line("BEGIN:VEVENT");
        ics.Line($"UID:{uid}");
        ics.Line($"DTSTAMP:{stamp}");
        ics.Line($"DTSTART;TZID=Asia/Muscat:{on:yyyyMMdd}T{start:HHmmss}");
        ics.Line($"DTEND;TZID=Asia/Muscat:{on:yyyyMMdd}T{end:HHmmss}");
        if (date is null)
        {
            // UNTIL is in UTC: the end of the last day in Oman.
            var until = new DateTimeOffset(last.ToDateTime(new TimeOnly(23, 59, 59)), Oman).UtcDateTime;
            ics.Line($"RRULE:FREQ=WEEKLY;UNTIL={until:yyyyMMdd'T'HHmmss'Z'}");
        }
        ics.Line($"SUMMARY:{Ics.Escape(summary)}");
        if (!string.IsNullOrWhiteSpace(location)) ics.Line($"LOCATION:{Ics.Escape(location)}");
        if (!string.IsNullOrWhiteSpace(description)) ics.Line($"DESCRIPTION:{Ics.Escape(description)}");
        ics.Line("END:VEVENT");
    }

    /// <summary>iCalendar text: CRLF line ends, and lines folded at 75 bytes as the format requires.</summary>
    sealed class Ics
    {
        readonly StringBuilder text = new();

        public static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

        public void Line(string line)
        {
            // By whole characters, so a fold never splits one (an Arabic letter or an emoji in a slot's name).
            var bytes = 0;
            foreach (var rune in line.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > 75)
                {
                    text.Append("\r\n ");
                    bytes = 1;
                }
                text.Append(rune.ToString());
                bytes += rune.Utf8SequenceLength;
            }
            text.Append("\r\n");
        }

        public override string ToString() => text.ToString();
    }
}
