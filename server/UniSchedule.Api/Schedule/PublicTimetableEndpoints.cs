using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Schedule;

/// <summary>
/// The published semester's classes for anyone, with no sign-in, while an owner has it switched on.
/// Only timetable data: courses, sections, teachers, dates, times and rooms. Nothing about students.
/// </summary>
public static class PublicTimetableEndpoints
{
    public const string Setting = "public-timetable";
    public const string Limit = "public-timetable";

    public record ToggleBody(bool Enabled);

    static Task<bool> IsOn(AppDb db, CancellationToken ct) => db.AppSettings.AnyAsync(s => s.Key == Setting && s.Value == "on", ct);

    public static void MapPublicTimetable(this RouteGroupBuilder api)
    {
        var pub = api.MapGroup("/public").RequireRateLimiting(Limit);

        pub.MapGet("/timetable", async (AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await IsOn(db, ct)) return Errors.NotFound("The public timetable is switched off.");
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            if (semester is null) return Errors.NotFound("No semester has been published yet.");
            var rows = await TimetableExport.RowsAsync(db, semester.Id, ct);
            return Results.Ok(new
            {
                semester = semester.Name,
                generatedAt = clock.GetUtcNow(),
                classes = rows.Select(r => new
                {
                    r.Code, r.Name, r.Majors, r.Type, r.Year, r.Credits, r.Section, r.Instructor,
                    r.Date, day = Timeslots.DayNames[r.Day], r.Start, r.End, r.Room,
                }),
            });
        });

        pub.MapGet("/timetable.csv", async (AppDb db, CancellationToken ct) =>
        {
            if (!await IsOn(db, ct)) return Errors.NotFound("The public timetable is switched off.");
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            if (semester is null) return Errors.NotFound("No semester has been published yet.");
            return Results.File(TimetableExport.Csv(await TimetableExport.RowsAsync(db, semester.Id, ct)), "text/csv; charset=utf-8", TimetableExport.FileName(semester));
        });

        // ---- Owners switch it on and off ----
        var owner = api.MapGroup("/admin/public-timetable").RequireAuthorization(Session.OwnerPolicy);

        owner.MapGet("", async (AppDb db, CancellationToken ct) =>
        {
            var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == Setting, ct);
            return Results.Ok(new { enabled = s?.Value == "on", updatedBy = s?.UpdatedBy, updatedAt = s?.UpdatedAt });
        });

        owner.MapPut("", async (ToggleBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == Setting, ct);
            if (s is null) db.AppSettings.Add(s = new AppSetting { Key = Setting });
            s.Value = body.Enabled ? "on" : "off";
            s.UpdatedBy = await OwnerEndpoints.ActorName(http, db, ct);
            s.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { enabled = body.Enabled, s.UpdatedBy, s.UpdatedAt });
        });
    }
}
