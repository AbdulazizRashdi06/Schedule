using System.Globalization;
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

    static string Link(HttpRequest req, string token, string ext = "ics") => $"{req.Scheme}://{req.Host}/api/calendar/{token}.{ext}";

    /// <summary>The feed link, and the same link as plain text for AI assistants such as ChatGPT.</summary>
    static object Links(HttpRequest req, string token) => new { url = Link(req, token), textUrl = Link(req, token, "txt") };

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
            return Results.Ok(Links(http.Request, s.CalendarToken));
        });

        // A new link; calendars subscribed with the old one stop updating.
        me.MapPost("/reset", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            s.CalendarToken = NewToken();
            await db.SaveChangesAsync(ct);
            return Results.Ok(Links(http.Request, s.CalendarToken));
        });

        // The feed itself, fetched by calendar apps with the secret in the link.
        api.MapGet("/calendar/{token}.ics", async (string token, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var s = token.Length >= 40 ? await db.Students.FirstOrDefaultAsync(x => x.CalendarToken == token, ct) : null;
            if (s is null) return Errors.NotFound("This calendar link was reset or doesn't exist. Add the new one from Classy.");
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            return Results.Text(await Build(db, s.Id, semester, clock.GetUtcNow(), ct), "text/calendar; charset=utf-8");
        }).RequireRateLimiting(PublicTimetableEndpoints.Limit);

        // The same timetable as plain text, for an AI assistant the student told to open this link when they ask about
        // their classes. Two weeks from today by default; ?from=YYYY-MM-DD&days=N for other dates.
        api.MapGet("/calendar/{token}.txt", async (string token, string? from, int? days, HttpContext http, AppDb db, TimeProvider clock, ILoggerFactory logs, CancellationToken ct) =>
        {
            // Which app reads it (ChatGPT's fetcher, a browser), to see whether assistants fetch it themselves. No token or student.
            logs.CreateLogger("CalendarText").LogInformation("Timetable text read by {Agent}", http.Request.Headers.UserAgent.ToString());
            const string text = "text/plain; charset=utf-8";
            var s = token.Length >= 40 ? await db.Students.FirstOrDefaultAsync(x => x.CalendarToken == token, ct) : null;
            if (s is null)
                return Results.Text("This Classy link was reset or doesn't exist. Get the new one in Classy: Menu, then Calendar & ChatGPT.", text, statusCode: StatusCodes.Status404NotFound);
            DateOnly? start = null;
            if (from is not null && (start = ScheduleImport.ParseDate(from)) is null)
                return Results.Text("Use ?from=YYYY-MM-DD, for example ?from=2026-11-15.", text, statusCode: StatusCodes.Status400BadRequest);
            // Always fresh, and kept out of search engines: the link is private.
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            return Results.Text(await Text(db, s.Id, semester, clock.GetUtcNow(), start, Math.Clamp(days ?? 14, 1, MaxTextDays), ct), text);
        }).RequireRateLimiting(PublicTimetableEndpoints.Limit);
    }

    const int MaxTextDays = 62;

    /// <summary>One class or own slot: on its date, or weekly on its weekday.</summary>
    sealed record Item(DateOnly? Date, int Day, TimeOnly Start, TimeOnly End, string Line, bool IsClass);

    /// <summary>
    /// The timetable written for an AI assistant to read: the time now, the next class, then each day from
    /// <paramref name="from"/> (default today) with its classes and own slots, all in Oman time.
    /// </summary>
    public static async Task<string> Text(AppDb db, int studentId, Semester? semester, DateTimeOffset now, DateOnly? from, int days, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var local = now.ToOffset(Oman);
        var today = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);
        var t = new StringBuilder();
        t.AppendLine("Classy timetable. This link is private: don't share it or show it to anyone.");
        t.AppendLine($"Now: {local.ToString("dddd d MMMM yyyy, HH:mm", inv)}, Oman time (UTC+4). Times below are 24-hour Oman time.");
        if (semester is null)
        {
            t.AppendLine("No timetable is published in Classy yet.");
            return t.ToString();
        }

        var first = SemesterWeeks.Sunday(semester.Week1Start ?? DateOnly.FromDateTime((semester.PublishedAt ?? now).ToOffset(Oman).DateTime));
        var last = first.AddDays((semester.Week1Start is not null && semester.LastWeek is int weeks ? weeks : DefaultWeeks) * 7 - 1);
        string? Week(DateOnly d) => semester.Week1Start is null || d < first || d > last ? null : $"week {(d.DayNumber - first.DayNumber) / 7 + 1}";

        var items = new List<Item>();
        var picks = await db.Picks.Where(p => p.StudentId == studentId && p.Course.SemesterId == semester.Id && p.SectionId != null)
            .Include(p => p.Course).Include(p => p.Section).ThenInclude(x => x!.Meetings)
            .AsSplitQuery().ToListAsync(ct);
        foreach (var p in picks)
        foreach (var m in p.Section!.Meetings)
        {
            var parts = new[] { $"{p.Course.Code} {p.Course.Name}", Kind(m.Room), string.IsNullOrWhiteSpace(m.Room) ? null : $"Room {m.Room}", $"Section {p.Section.Number}", p.Section.Instructor };
            items.Add(new(m.Date, m.Day, m.Start, m.End, string.Join(" · ", parts.Where(x => !string.IsNullOrWhiteSpace(x))), true));
        }
        var slots = await db.CustomSlots.Where(x => x.StudentId == studentId && x.SemesterId == semester.Id).ToListAsync(ct);
        foreach (var slot in slots)
        {
            var parts = new[] { slot.Title, "own slot, not a class", slot.Place, slot.Note };
            items.Add(new(slot.Date, slot.Day, slot.Start, slot.End, string.Join(" · ", parts.Where(x => !string.IsNullOrWhiteSpace(x))), false));
        }

        // Weekly ones run from the semester's first week to its last; dated ones on their date only.
        List<Item> On(DateOnly d) => items
            .Where(i => i.Date == d || (i.Date is null && i.Day == (int)d.DayOfWeek && d >= first && d <= last))
            .OrderBy(i => i.Start).ThenBy(i => i.End).ToList();
        string When(Item i) => $"{Timeslots.Format(i.Start)}–{Timeslots.Format(i.End)}";

        t.AppendLine($"Semester: {semester.Name}{(Week(today) is string w ? $", teaching {w}{(semester.LastWeek is int lw ? $" of {lw}" : "")}" : "")}.");
        if (picks.Count == 0) t.AppendLine("The student hasn't picked their classes in Classy yet.");

        var current = On(today).FirstOrDefault(i => i.IsClass && i.Start <= time && time < i.End);
        if (current is not null) t.AppendLine($"In class right now, until {Timeslots.Format(current.End)}: {current.Line}");
        for (var d = today; d <= today.AddDays(MaxTextDays); d = d.AddDays(1))
        {
            var next = On(d).FirstOrDefault(i => i.IsClass && (d > today || i.Start > time));
            if (next is null) continue;
            var day = d == today ? "today" : d == today.AddDays(1) ? "tomorrow" : d.ToString("dddd d MMMM", inv);
            t.AppendLine($"Next class: {day}, {When(next)}: {next.Line}");
            break;
        }
        t.AppendLine($"Other dates: add ?from=YYYY-MM-DD&days=N (N up to {MaxTextDays}) to this link.");

        var start = from ?? today;
        for (var d = start; d < start.AddDays(days); d = d.AddDays(1))
        {
            t.AppendLine();
            t.AppendLine($"{d.ToString("ddd d MMM yyyy", inv)}{(Week(d) is string wk ? $" · {wk}" : "")}{(d == today ? " · today" : "")}");
            var list = On(d);
            if (list.Count == 0) t.AppendLine("- No classes");
            foreach (var i in list) t.AppendLine($"- {When(i)}  {i.Line}");
        }
        return t.ToString();
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
