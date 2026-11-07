using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Schedule;

/// <summary>
/// Days with no classes (public holidays, breaks), set by staff per semester. Weekly classes skip them everywhere:
/// home, the week, free rooms, calendar feeds and reminders. A class put on that exact date still happens.
/// </summary>
public static class DaysOff
{
    public record DayOffBody(string? Date, string? To, string? Name);

    public static void MapDaysOff(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin/semesters/{id:int}/days-off").RequireAuthorization(Session.StaffPolicy);

        admin.MapGet("", async (int id, AppDb db, CancellationToken ct) =>
            Results.Ok(await db.DaysOff.Where(d => d.SemesterId == id).OrderBy(d => d.Date)
                .Select(d => new { d.Id, date = d.Date.ToString("yyyy-MM-dd"), d.Name }).ToListAsync(ct)));

        // One day off, or a run of them: with "to", every day from date to to (inclusive) is off.
        admin.MapPost("", async (int id, DayOffBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await db.Semesters.AnyAsync(s => s.Id == id, ct)) return Errors.NotFound("That semester doesn't exist.");
            var from = body.Date is null ? null : ScheduleImport.ParseDate(body.Date);
            if (from is null) return Errors.BadRequest("Pick the day off's date.");
            var until = string.IsNullOrWhiteSpace(body.To) ? from : ScheduleImport.ParseDate(body.To);
            if (until is null || until < from) return Errors.BadRequest("The last day can't be before the first.");
            if (until.Value.DayNumber - from.Value.DayNumber > 31) return Errors.BadRequest("A break can be at most a month at a time.");
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > 60) return Errors.BadRequest("Give it a short name, like National Day.");
            var by = await OwnerEndpoints.ActorName(http, db, ct);
            var existing = await db.DaysOff.Where(d => d.SemesterId == id && d.Date >= from && d.Date <= until).ToListAsync(ct);
            for (var d = from.Value; d <= until.Value; d = d.AddDays(1))
            {
                // Adding a day that's already off renames it.
                if (existing.FirstOrDefault(x => x.Date == d) is { } same) same.Name = name;
                else db.DaysOff.Add(new DayOff { SemesterId = id, Date = d, Name = name, CreatedBy = by, CreatedAt = clock.GetUtcNow() });
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(await db.DaysOff.Where(d => d.SemesterId == id).OrderBy(d => d.Date)
                .Select(d => new { d.Id, date = d.Date.ToString("yyyy-MM-dd"), d.Name }).ToListAsync(ct));
        });

        admin.MapDelete("/{dayId:int}", async (int id, int dayId, AppDb db, CancellationToken ct) =>
            await db.DaysOff.Where(d => d.Id == dayId && d.SemesterId == id).ExecuteDeleteAsync(ct) == 0
                ? Errors.NotFound("That day off is already gone.")
                : Results.NoContent());
    }

    /// <summary>The semester's days off by date.</summary>
    public static async Task<Dictionary<DateOnly, string>> ForAsync(AppDb db, int semesterId, CancellationToken ct) =>
        await db.DaysOff.Where(d => d.SemesterId == semesterId).ToDictionaryAsync(d => d.Date, d => d.Name, ct);

    /// <summary>The days off a weekly meeting on <paramref name="weekday"/> skips, as "2026-11-18".</summary>
    public static IEnumerable<string> Skipped(Dictionary<DateOnly, string> daysOff, int weekday) =>
        daysOff.Keys.Where(d => (int)d.DayOfWeek == weekday).Order().Select(d => d.ToString("yyyy-MM-dd"));
}
