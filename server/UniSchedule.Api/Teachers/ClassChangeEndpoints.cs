using System.Globalization;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Teachers;

/// <summary>
/// A teacher's changes to single sessions (/api/teach/changes): cancel, move, another room, an extra class. Live at
/// once, with no approval. The rules are in <see cref="ChangeRules"/>; this file is only the doors.
/// </summary>
public static class ClassChangeEndpoints
{
    /// <summary>A group id is 32 hex characters; anything else can't name a change.</summary>
    static bool IsGroup(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// One write, in one transaction that is opened before anything is read: it takes the database's write lock, so
    /// two changes to one session, or a change and a timetable upload, never interleave. The time is read after the
    /// lock is held. A refusal has written nothing; one that came after a first save rolled the transaction back itself.
    /// </summary>
    static async Task<IResult> WriteAsync(AppDb db, TimeProvider clock, Func<DateTimeOffset, Task<IResult>> write, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await write(clock.GetUtcNow());
        if (db.Database.CurrentTransaction is not null) await tx.CommitAsync(ct);
        return result;
    }

    public static void MapClassChanges(this RouteGroupBuilder teach)
    {
        // Numbers and warnings for a change. Saves nothing, and needs no fresh code.
        teach.MapPost("/changes/preview", async (ChangeRules.Body body, HttpContext http, AppDb db, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow();
            return await ChangeRules.PreviewAsync(db, http.Teach(), body, now, TeachActor.CodeFresh(http, now), config, ct);
        });

        teach.MapPost("/changes", async (ChangeRules.Body body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (TeachActor.CodeNeeded(http, clock) is { } needsCode) return needsCode;
            return await WriteAsync(db, clock, now => ChangeRules.CreateAsync(db, http.Teach(), body, now, ct), ct);
        });

        // Edits a change without undoing it first.
        teach.MapPut("/changes/{groupId}", async (string groupId, ChangeRules.Body body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (TeachActor.CodeNeeded(http, clock) is { } needsCode) return needsCode;
            if (!IsGroup(groupId)) return Errors.Conflict(ChangeRules.Gone);
            return await WriteAsync(db, clock, now => ChangeRules.ReplaceAsync(db, http.Teach(), groupId, body, now, ct), ct);
        });

        // Undo also notifies a whole class, so it asks for a fresh code too.
        teach.MapPost("/changes/{groupId}/undo", async (string groupId, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (TeachActor.CodeNeeded(http, clock) is { } needsCode) return needsCode;
            if (!IsGroup(groupId)) return Errors.Conflict(ChangeRules.Gone);
            return await WriteAsync(db, clock, now => ChangeRules.UndoAsync(db, http.Teach(), groupId, now, ct), ct);
        });

        teach.MapGet("/changes", async (HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
            await ChangeRules.ListAsync(db, http.Teach(), clock.GetUtcNow(), ct));

        // Rooms free for the whole of a class, for the room picker: the answer of /rooms/free. Naming the session being
        // changed (or the change being edited) leaves out the room that class takes itself.
        teach.MapGet("/rooms", async (string? date, string? start, string? end, int? sectionId, string? origDate, string? origStart, string? groupId,
            HttpContext http, AppDb db, IConfiguration config, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!PlannerEngine.Date(date, out var day) || !PlannerEngine.Sane(day)) return Errors.BadRequest("Pick a date like 2026-11-04.");
            if (!TimeOnly.TryParseExact(start, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
                || !TimeOnly.TryParseExact(end, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || to <= from)
                return Errors.BadRequest("Pick a start and an end time like 13:15.");
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            Func<FreeRooms.Occupied, bool> ignore = _ => false;
            if (semester is not null && sectionId is int id)
            {
                var taught = await TeacherSchedule.SectionsAsync(db, semester.Id, actor, ct);
                if (!taught.Any(t => t.Section.Id == id)) return Errors.Forbidden(ChangeRules.NotYours);
                if (PlannerEngine.Date(origDate, out var on) && TimeOnly.TryParseExact(origStart, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                {
                    var changes = await ClassChanges.LoadAsync(db, semester.Id, taught.Select(t => t.Section).ToList(), ct);
                    var sections = TeacherSchedule.Siblings(taught, await SemesterSpan.ForAsync(db, semester, ct), changes, id, on, at).Select(t => t.Section.Id).Append(id).ToHashSet();
                    // The class as the university lists it, and whatever a change to that very session put on.
                    ignore = o => o.SectionId is int section && sections.Contains(section)
                        && (o.Change is { } c ? c.OrigDate == on && c.OrigStart == at : day == on && o.Start == at);
                }
            }
            else if (semester is not null && IsGroup(groupId))
                // An extra class being edited has no university session: it is named by its change.
                ignore = o => o.Change?.GroupId == groupId;
            return Results.Ok(await FreeRooms.FreeForAsync(db, semester, day, from, to, ignore, config, ct));
        });
    }
}
