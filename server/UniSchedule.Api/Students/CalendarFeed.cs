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
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);

    static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    static string Link(HttpRequest req, string token, string ext = "ics") => $"{req.Scheme}://{req.Host}/api/calendar/{token}.{ext}";

    /// <summary>The feed link, and the same link as plain text for AI assistants such as ChatGPT.</summary>
    static object Links(HttpRequest req, string token) => new { url = Link(req, token), textUrl = Link(req, token, "txt") };

    /// <summary>The class type, read from the room the way the app does: "Lab L2" is a lab, "Tut 3" a tutorial.</summary>
    static string Kind(string room) => SessionKinds.Label(SessionKinds.Of(room));

    public static void MapCalendar(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me/calendar").RequireAuthorization(Session.StudentPolicy);

        // The student's feed link, made the first time they ask for it.
        me.MapGet("", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var token = await db.Students.Where(x => x.Id == id).Select(x => x.CalendarToken).FirstAsync(ct);
            if (token is null)
            {
                // Only set while still unset, then read back: two first requests at once get the same link, not one dead one.
                await db.Students.Where(x => x.Id == id && x.CalendarToken == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.CalendarToken, NewToken()), ct);
                token = await db.Students.Where(x => x.Id == id).Select(x => x.CalendarToken).FirstAsync(ct);
            }
            return Results.Ok(Links(http.Request, token!));
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
        api.MapGet("/calendar/{token}.ics", async (string token, AppDb db, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            var s = token.Length >= 40 ? await db.Students.FirstOrDefaultAsync(x => x.CalendarToken == token, ct) : null;
            if (s is null) return Errors.NotFound("This calendar link was reset or doesn't exist. Add the new one from Classy.");
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            var planner = await Planner.PlannerEngine.Enabled(db, config, ct);
            return Results.Text(await Build(db, s.Id, semester, clock.GetUtcNow(), planner, ct), "text/calendar; charset=utf-8");
        }).RequireRateLimiting(PublicTimetableEndpoints.Limit);

        // The same timetable as plain text, for an AI assistant the student told to open this link when they ask about
        // their classes. Two weeks from today by default; ?from=YYYY-MM-DD&days=N for other dates.
        api.MapGet("/calendar/{token}.txt", async (string token, string? from, int? days, HttpContext http, AppDb db, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
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
            var planner = await Planner.PlannerEngine.Enabled(db, config, ct);
            return Results.Text(await Text(db, s.Id, semester, clock.GetUtcNow(), start, Math.Clamp(days ?? 14, 1, MaxTextDays), planner, ct), text);
        }).RequireRateLimiting(PublicTimetableEndpoints.Limit);
    }

    const int MaxTextDays = 62;

    static string Join(params string?[] parts) => string.Join(" · ", parts.Where(x => !string.IsNullOrWhiteSpace(x)));
    static string? RoomText(string room) => string.IsNullOrWhiteSpace(room) ? null : $"Room {room}";
    /// <summary>Exam, or the class type: as stored on a teacher's change when one touched it, else read from the room.</summary>
    static string KindOf(StudentSchedule.Item i) => i.EventKind == "exam" ? "Exam" : i.Kind is not null ? SessionKinds.Label(i.Kind) : Kind(i.Room);

    /// <summary>What a teacher's change did to a class, for the text feed: "moved from Tue 10:00", "room changed", "extra class".</summary>
    static string? ChangeWords(StudentSchedule.Item i) => i.ChangeKind switch
    {
        ClassChanges.Move => i.ChangeFrom is DateTime from ? $"moved from {from.ToString("ddd HH:mm", CultureInfo.InvariantCulture)}" : "moved",
        ClassChanges.Room => "room changed",
        ClassChanges.Extra => "extra class",
        _ => null,
    };

    /// <summary>The same for a calendar entry's description, with who did it: "Moved from Tue 2 Dec 10:00 by Raed Bolbol".</summary>
    static string? ChangeSentence(StudentSchedule.Item i) => i.ChangeKind switch
    {
        ClassChanges.Move => $"Moved{(i.ChangeFrom is DateTime from ? $" from {from.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}" : "")} by {i.ChangeBy}",
        ClassChanges.Room => $"Room changed by {i.ChangeBy}",
        ClassChanges.Extra => $"Extra class added by {i.ChangeBy}",
        _ => null,
    };

    /// <summary>How the text feed describes a class, an own slot or a batch rep's event.</summary>
    static string Line(StudentSchedule.Item i) => i.From switch
    {
        StudentSchedule.Source.Class when i.ChangeId is not null =>
            Join($"{i.Code} {i.Name}", KindOf(i), RoomText(i.Room), $"Section {i.Section}", i.Instructor, ChangeWords(i), i.Note),
        StudentSchedule.Source.Class => Join($"{i.Code} {i.Name}", KindOf(i), RoomText(i.Room), $"Section {i.Section}", i.Instructor),
        StudentSchedule.Source.Planner => Join($"{i.Code} {i.Name}", KindOf(i), RoomText(i.Room), "set by the batch rep", i.Note),
        _ => Join(i.Name, "own slot, not a class", i.Room, i.Note),
    };

    /// <summary>
    /// The timetable written for an AI assistant to read: the time now, the next class, then each day from
    /// <paramref name="from"/> (default today) with its classes and own slots, all in Oman time.
    /// </summary>
    public static async Task<string> Text(AppDb db, int studentId, Semester? semester, DateTimeOffset now, DateOnly? from, int days, bool planner, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var local = now.ToOffset(Oman);
        var t = new StringBuilder();
        t.AppendLine("Classy timetable. This link is private: don't share it or show it to anyone.");
        t.AppendLine($"Now: {local.ToString("dddd d MMMM yyyy, HH:mm", inv)}, Oman time (UTC+4). Times below are 24-hour Oman time.");
        if (semester is null)
        {
            t.AppendLine("No timetable is published in Classy yet.");
            return t.ToString();
        }

        var span = await SemesterSpan.ForAsync(db, semester, ct);
        var off = new List<StudentSchedule.Item>();
        var items = await StudentSchedule.ForAsync(db, semester.Id, [studentId], slots: true, planner, ct, off);
        TextFrom(t, items, off, span, semester.Name, now, from, days, "The student hasn't picked their classes in Classy yet.");
        return t.ToString();
    }

    /// <summary>
    /// The body of the text feed, from a list of items: a student's, or a teacher's. <paramref name="off"/>: sessions
    /// that aren't on; the cancelled ones are listed in place, marked CANCELLED.
    /// </summary>
    /// <param name="noClasses">What to say when the list has no class at all.</param>
    internal static void TextFrom(StringBuilder t, IReadOnlyList<StudentSchedule.Item> items, IReadOnlyList<StudentSchedule.Item> off, SemesterSpan span,
        string semesterName, DateTimeOffset now, DateOnly? from, int days, string noClasses)
    {
        var inv = CultureInfo.InvariantCulture;
        var local = now.ToOffset(Oman);
        var today = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);
        List<StudentSchedule.Item> On(DateOnly d) => items.Where(i => span.Happens(i, d)).OrderBy(i => i.Start).ThenBy(i => i.End).ToList();
        string When(StudentSchedule.Item i) => $"{Timeslots.Format(i.Start)}–{Timeslots.Format(i.End)}";

        t.AppendLine($"Semester: {semesterName}{(span.Week(today) is string w ? $", teaching {w}{(span.LastWeek is int lw ? $" of {lw}" : "")}" : "")}.");
        if (!items.Any(i => i.From == StudentSchedule.Source.Class)) t.AppendLine(noClasses);

        var current = On(today).FirstOrDefault(i => i.IsClass && i.Start <= time && time < i.End);
        if (current is not null) t.AppendLine($"In class right now, until {Timeslots.Format(current.End)}: {Line(current)}");
        for (var d = today; d <= today.AddDays(MaxTextDays); d = d.AddDays(1))
        {
            var next = On(d).FirstOrDefault(i => i.IsClass && (d > today || i.Start > time));
            if (next is null) continue;
            var day = d == today ? "today" : d == today.AddDays(1) ? "tomorrow" : d.ToString("dddd d MMMM", inv);
            t.AppendLine($"Next class: {day}, {When(next)}: {Line(next)}");
            break;
        }
        t.AppendLine($"Other dates: add ?from=YYYY-MM-DD&days=N (N up to {MaxTextDays}) to this link.");

        var start = from ?? today;
        for (var d = start; d < start.AddDays(days); d = d.AddDays(1))
        {
            t.AppendLine();
            t.AppendLine($"{d.ToString("ddd d MMM yyyy", inv)}{(span.Week(d) is string wk ? $" · {wk}" : "")}{(d == today ? " · today" : "")}{(span.DaysOff.TryGetValue(d, out var dayOff) ? $" · day off: {dayOff}" : "")}");
            // Cancelled classes stay in their place, said to be cancelled; "next class" above never counts them.
            var list = On(d).Select(i => (Item: i, Cancelled: false))
                .Concat(off.Where(i => i.ChangeKind == ClassChanges.Cancel && i.Date == d).Select(i => (Item: i, Cancelled: true)))
                .OrderBy(x => x.Item.Start).ThenBy(x => x.Item.End).ToList();
            if (list.Count == 0) t.AppendLine("- No classes");
            foreach (var (i, cancelled) in list)
                t.AppendLine(cancelled
                    ? $"- {When(i)}  CANCELLED by {i.ChangeBy}: {Join($"{i.Code} {i.Name}", i.Note)}"
                    : $"- {When(i)}  {Line(i)}");
        }
    }

    /// <summary>The iCalendar text: the student's classes, own slots and batch reps' events in the current semester, in Oman time.</summary>
    public static async Task<string> Build(AppDb db, int studentId, Semester? semester, DateTimeOffset now, bool planner, CancellationToken ct)
    {
        if (semester is null) return BuildFrom([], [], null, now, "Classy Schedule");
        var off = new List<StudentSchedule.Item>();
        var items = await StudentSchedule.ForAsync(db, semester.Id, [studentId], slots: true, planner, ct, off);
        return BuildFrom(items, off, await SemesterSpan.ForAsync(db, semester, ct), now, "Classy Schedule");
    }

    /// <summary>
    /// The iCalendar text from a list of items: a student's, or a teacher's. <paramref name="off"/>: sessions that
    /// aren't on. A cancelled one is still written, as "Cancelled: …"; the old slot of a moved class is not (the class
    /// is written at its new time under its old id, so calendar apps move the entry in place).
    /// </summary>
    /// <param name="span">Null while no semester is published: an empty calendar.</param>
    internal static string BuildFrom(IReadOnlyList<StudentSchedule.Item> items, IReadOnlyList<StudentSchedule.Item> off, SemesterSpan? span, DateTimeOffset now, string calendarName)
    {
        var ics = new Ics();
        ics.Line("BEGIN:VCALENDAR");
        ics.Line("VERSION:2.0");
        ics.Line("PRODID:-//Classy//Timetable//EN");
        ics.Line("CALSCALE:GREGORIAN");
        ics.Line("METHOD:PUBLISH");
        ics.Line($"X-WR-CALNAME:{calendarName}");
        ics.Line("X-WR-TIMEZONE:Asia/Muscat");
        // A hint to refresh hourly; each calendar app decides for itself in the end.
        ics.Line("REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        ics.Line("X-PUBLISHED-TTL:PT1H");
        // Oman has no daylight saving: one fixed offset.
        foreach (var l in new[] { "BEGIN:VTIMEZONE", "TZID:Asia/Muscat", "BEGIN:STANDARD", "DTSTART:19700101T000000", "TZOFFSETFROM:+0400", "TZOFFSETTO:+0400", "TZNAME:+04", "END:STANDARD", "END:VTIMEZONE" })
            ics.Line(l);

        if (span is not null)
        {
            var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
            foreach (var i in items.OrderBy(i => i.From).ThenBy(i => i.Code).ThenBy(i => i.Date).ThenBy(i => i.Day).ThenBy(i => i.Start))
            {
                var (uid, summary, details) = i.From switch
                {
                    // A class a teacher's change touched keeps the id of the class it was, and says what happened.
                    StudentSchedule.Source.Class => (i.Uid ?? $"class-{i.SectionId}-{i.MeetingId}@classy", $"{i.Name} · {KindOf(i)}",
                        Join(i.Code, $"Section {i.Section}", i.Instructor, ChangeSentence(i), i.ChangeId is null ? null : i.Note)),
                    StudentSchedule.Source.Planner => ($"planner-{i.EventId}-{i.Date:yyyyMMdd}@classy", $"{i.Name} · {KindOf(i)}", Join(i.Code, "Set by the batch rep", i.Note)),
                    _ => ($"slot-{i.SlotId}@classy", i.Name, i.Note ?? ""),
                };
                Event(ics, uid, stamp, summary, i.Room, details, i, span);
            }
            // Cancelled classes stay in the calendar with "Cancelled:" in front. No STATUS:CANCELLED line: calendar apps
            // hide those events. Marked as free time instead.
            foreach (var i in off.Where(i => i.ChangeKind == ClassChanges.Cancel && i.Date is not null && i.Uid is not null).OrderBy(i => i.Code).ThenBy(i => i.Date).ThenBy(i => i.Start))
                One(ics, i.Uid!, stamp, $"Cancelled: {i.Name} · {KindOf(i)}", i.Room,
                    $"Cancelled by {i.ChangeBy}.{(string.IsNullOrWhiteSpace(i.Note) ? "" : $" {i.Note}")}", i, i.Date!.Value, free: true);
        }

        ics.Line("END:VCALENDAR");
        return ics.ToString();
    }

    /// <summary>
    /// One event on its date, or one event per week it meets. Weekly classes are written out date by date rather than as a
    /// repeating rule: Samsung Calendar (through Google) dropped repeating events, and this way days off just don't appear.
    /// </summary>
    static void Event(Ics ics, string uid, string stamp, string summary, string location, string description, StudentSchedule.Item i, SemesterSpan span)
    {
        if (i.Date is DateOnly date)
        {
            // A dated class the batch rep cancelled or moved doesn't happen.
            if (!i.Skip.Contains(date)) One(ics, uid, stamp, summary, location, description, i, date);
            return;
        }
        var at = uid.IndexOf('@');
        for (var d = span.First; d <= span.Last; d = d.AddDays(1))
            if (span.Happens(i, d))
                One(ics, $"{uid[..at]}-{d:yyyyMMdd}{uid[at..]}", stamp, summary, location, description, i, d);
    }

    static void One(Ics ics, string uid, string stamp, string summary, string location, string description, StudentSchedule.Item i, DateOnly on, bool free = false)
    {
        ics.Line("BEGIN:VEVENT");
        ics.Line($"UID:{uid}");
        ics.Line($"DTSTAMP:{stamp}");
        ics.Line($"DTSTART;TZID=Asia/Muscat:{on:yyyyMMdd}T{i.Start:HHmmss}");
        ics.Line($"DTEND;TZID=Asia/Muscat:{on:yyyyMMdd}T{i.End:HHmmss}");
        ics.Line($"SUMMARY:{Ics.Escape(summary)}");
        if (!string.IsNullOrWhiteSpace(location)) ics.Line($"LOCATION:{Ics.Escape(location)}");
        if (!string.IsNullOrWhiteSpace(description)) ics.Line($"DESCRIPTION:{Ics.Escape(description)}");
        if (free) ics.Line("TRANSP:TRANSPARENT");
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
