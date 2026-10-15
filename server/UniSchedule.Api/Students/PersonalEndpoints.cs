using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Students;

/// <summary>A student's own slots and their notes on classes (private, or public to classmates).</summary>
public static class PersonalEndpoints
{
    public const int MaxSlots = 60;
    public const int MaxNoteLength = 500;
    public const int NotesPerHour = 30;

    public record SlotBody(string? Title, int? Day, string? Start, string? End, string? Date, string? Place, string? Note);
    public record NoteBody(int? CourseId, string? Date, string? Text, bool? IsPublic);

    public static object Of(CustomSlot s) => new
    {
        s.Id, s.Title, s.Day, start = Timeslots.Format(s.Start), end = Timeslots.Format(s.End),
        date = s.Date?.ToString("yyyy-MM-dd"), s.Place, s.Note,
    };

    static bool TryTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    /// <summary>Checks a slot from the app; copies it onto <paramref name="slot"/> when it's fine.</summary>
    static string? Fill(CustomSlot slot, SlotBody b)
    {
        var title = b.Title?.Trim() ?? "";
        if (title.Length is 0 or > 60) return "Give the slot a name (up to 60 characters).";
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(b.Date))
        {
            date = ScheduleImport.ParseDate(b.Date);
            if (date is null) return "Pick a date.";
        }
        var day = date is null ? b.Day ?? -1 : (int)date.Value.DayOfWeek;
        if (day is < 0 or > 6) return "Pick a day.";
        if (!TryTime(b.Start, out var start) || !TryTime(b.End, out var end)) return "Enter times like 08:00.";
        if (end <= start) return "The slot ends before it starts.";
        var place = b.Place?.Trim() ?? "";
        if (place.Length > 60) return "The place is too long.";
        var note = string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim();
        if (note?.Length > MaxNoteLength) return $"Keep the note under {MaxNoteLength} characters.";
        (slot.Title, slot.Day, slot.Start, slot.End, slot.Date, slot.Place, slot.Note) = (title, day, start, end, date, place, note);
        return null;
    }

    /// <summary>
    /// Notes this student may see on their courses: their own, and public ones that aren't hidden —
    /// whole-course notes for anyone taking the course, session notes for the same section.
    /// </summary>
    public static async Task<List<ClassNote>> VisibleNotes(AppDb db, int studentId, IReadOnlyCollection<int> courseIds, CancellationToken ct)
    {
        var sections = await db.Picks.Where(p => p.StudentId == studentId && courseIds.Contains(p.CourseId))
            .Select(p => new { p.CourseId, p.SectionId }).ToListAsync(ct);
        var taken = sections.ToDictionary(p => p.CourseId, p => p.SectionId);
        var notes = await db.ClassNotes.Include(n => n.Student)
            .Where(n => courseIds.Contains(n.CourseId) && (n.StudentId == studentId || (n.IsPublic && n.HiddenAt == null)))
            .ToListAsync(ct);
        return notes.Where(n => n.StudentId == studentId
                || (taken.TryGetValue(n.CourseId, out var section) && (n.SectionId is null || n.SectionId == section)))
            .ToList();
    }

    public static void MapPersonal(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me").RequireAuthorization(Session.StudentPolicy);

        // ---- Custom slots (current semester) ----

        me.MapGet("/slots", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is null) return Results.Ok(Array.Empty<object>());
            var id = http.User.Id();
            var slots = await db.CustomSlots.Where(s => s.StudentId == id && s.SemesterId == current.Id).ToListAsync(ct);
            return Results.Ok(slots.OrderBy(s => s.Date).ThenBy(s => s.Day).ThenBy(s => s.Start).Select(Of));
        });

        me.MapPost("/slots", async (SlotBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is null) return Errors.Conflict("No semester has been published yet.");
            var id = http.User.Id();
            if (await db.CustomSlots.CountAsync(s => s.StudentId == id && s.SemesterId == current.Id, ct) >= MaxSlots)
                return Errors.BadRequest($"You can have up to {MaxSlots} slots.");
            var slot = new CustomSlot { StudentId = id, SemesterId = current.Id, CreatedAt = clock.GetUtcNow() };
            if (Fill(slot, body) is string problem) return Errors.BadRequest(problem);
            db.CustomSlots.Add(slot);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Of(slot));
        });

        me.MapPut("/slots/{slotId:int}", async (int slotId, SlotBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var slot = await db.CustomSlots.FirstOrDefaultAsync(s => s.Id == slotId && s.StudentId == http.User.Id(), ct);
            if (slot is null) return Errors.NotFound("That slot doesn't exist.");
            if (Fill(slot, body) is string problem) return Errors.BadRequest(problem);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Of(slot));
        });

        me.MapDelete("/slots/{slotId:int}", async (int slotId, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var removed = await db.CustomSlots.Where(s => s.Id == slotId && s.StudentId == http.User.Id()).ExecuteDeleteAsync(ct);
            return removed == 0 ? Errors.NotFound("That slot doesn't exist.") : Results.NoContent();
        });

        // ---- Notes on classes ----

        object NoteDto(ClassNote n, int me, HashSet<int> reported) => new
        {
            n.Id, author = n.Student.UniversityId, mine = n.StudentId == me, n.IsPublic,
            date = n.Date?.ToString("yyyy-MM-dd"), n.Text, n.CreatedAt, edited = n.UpdatedAt > n.CreatedAt,
            hidden = n.HiddenAt is not null, reported = reported.Contains(n.Id),
        };

        me.MapGet("/notes", async (int courseId, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var notes = await VisibleNotes(db, id, [courseId], ct);
            var reported = (await db.NoteReports.Where(r => r.StudentId == id).Select(r => r.NoteId).ToListAsync(ct)).ToHashSet();
            return Results.Ok(notes.OrderByDescending(n => n.CreatedAt).Select(n => NoteDto(n, id, reported)));
        });

        me.MapPost("/notes", async (NoteBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var text = body.Text?.Trim() ?? "";
            if (text.Length == 0) return Errors.BadRequest("Write something first.");
            if (text.Length > MaxNoteLength) return Errors.BadRequest($"Keep notes under {MaxNoteLength} characters.");
            var now = clock.GetUtcNow();
            if (await db.ClassNotes.CountAsync(n => n.StudentId == id && n.CreatedAt > now.AddHours(-1), ct) >= NotesPerHour)
                return Errors.TooMany("That's a lot of notes in one hour. Try again a bit later.");
            var pick = await db.Picks.Include(p => p.Section).ThenInclude(s => s!.Meetings)
                .FirstOrDefaultAsync(p => p.StudentId == id && p.CourseId == body.CourseId, ct);
            if (pick is null) return Errors.Forbidden("You can only write notes on your own classes.");
            DateOnly? date = null;
            if (!string.IsNullOrWhiteSpace(body.Date))
            {
                date = ScheduleImport.ParseDate(body.Date);
                if (date is null) return Errors.BadRequest("Pick a date.");
                if (pick.Section is null) return Errors.BadRequest("Pick a section for this class first.");
                var day = (int)date.Value.DayOfWeek;
                if (!pick.Section.Meetings.Any(m => m.Date == date || (m.Date is null && m.Day == day)))
                    return Errors.BadRequest("Your section has no class on that date.");
            }
            var note = new ClassNote
            {
                StudentId = id, CourseId = pick.CourseId, SectionId = date is null ? null : pick.SectionId, Date = date,
                Text = text, IsPublic = body.IsPublic == true, CreatedAt = now, UpdatedAt = now,
            };
            db.ClassNotes.Add(note);
            await db.SaveChangesAsync(ct);
            await db.Entry(note).Reference(n => n.Student).LoadAsync(ct);
            return Results.Ok(NoteDto(note, id, []));
        });

        me.MapPut("/notes/{noteId:int}", async (int noteId, NoteBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var note = await db.ClassNotes.Include(n => n.Student).FirstOrDefaultAsync(n => n.Id == noteId && n.StudentId == id, ct);
            if (note is null) return Errors.NotFound("That note doesn't exist.");
            var text = body.Text?.Trim() ?? "";
            if (text.Length == 0) return Errors.BadRequest("Write something first.");
            if (text.Length > MaxNoteLength) return Errors.BadRequest($"Keep notes under {MaxNoteLength} characters.");
            note.Text = text;
            if (body.IsPublic is bool isPublic) note.IsPublic = isPublic;
            note.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(NoteDto(note, id, []));
        });

        me.MapDelete("/notes/{noteId:int}", async (int noteId, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var removed = await db.ClassNotes.Where(n => n.Id == noteId && n.StudentId == http.User.Id()).ExecuteDeleteAsync(ct);
            return removed == 0 ? Errors.NotFound("That note doesn't exist.") : Results.NoContent();
        });

        // Report a classmate's public note to staff. Each student counts once.
        me.MapPost("/notes/{noteId:int}/report", async (int noteId, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var note = await db.ClassNotes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
            if (note is null || note.StudentId == id) return Errors.NotFound("That note doesn't exist.");
            if (!(await VisibleNotes(db, id, [note.CourseId], ct)).Any(n => n.Id == noteId)) return Errors.NotFound("That note doesn't exist.");
            if (await db.NoteReports.AnyAsync(r => r.NoteId == noteId && r.StudentId == id, ct)) return Results.NoContent();
            db.NoteReports.Add(new NoteReport { NoteId = noteId, StudentId = id });
            note.ReportCount++;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
