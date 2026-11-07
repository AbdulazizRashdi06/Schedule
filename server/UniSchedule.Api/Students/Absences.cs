using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Students;

/// <summary>
/// A student's own count of classes they missed, per course, so they can see how close they are to the attendance
/// limit. Private: nobody else sees it, and it isn't the university's attendance record.
/// </summary>
public static class Absences
{
    /// <summary>The attendance limit owners set, as a whole percentage of classes ("25"). Unset: no limit shown.</summary>
    public const string LimitSetting = "attendance.limit";
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);

    public record AbsenceBody(int CourseId, string? Date, string? Start, bool Missed);
    public record LimitBody(int? Percent);

    static async Task<int?> Limit(AppDb db, CancellationToken ct) =>
        await db.AppSettings.Where(x => x.Key == LimitSetting).Select(x => x.Value).FirstOrDefaultAsync(ct) is { } v && int.TryParse(v, out var n) ? n : null;

    public static void MapAbsences(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me/absences").RequireAuthorization(Session.StudentPolicy);

        me.MapGet("", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            var id = http.User.Id();
            var list = current is null ? [] : await db.Absences
                .Where(a => a.StudentId == id && db.Courses.Any(c => c.Id == a.CourseId && c.SemesterId == current.Id))
                .OrderBy(a => a.Date).ThenBy(a => a.Start).ToListAsync(ct);
            return Results.Ok(new
            {
                limit = await Limit(db, ct),
                absences = list.Select(a => new { a.CourseId, date = a.Date.ToString("yyyy-MM-dd"), start = Timeslots.Format(a.Start) }),
            });
        });

        // Marks one session missed, or not. Only for the student's own classes, and not for days still to come.
        me.MapPut("", async (AbsenceBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is null || !await db.Picks.AnyAsync(p => p.StudentId == id && p.CourseId == body.CourseId && p.Course.SemesterId == current.Id, ct))
                return Errors.NotFound("That class isn't in your timetable.");
            var date = body.Date is null ? null : ScheduleImport.ParseDate(body.Date);
            if (date is null || !TimeOnly.TryParseExact(body.Start, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                return Errors.BadRequest("Which session? Send its date and start time.");
            if (date > DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(Oman).DateTime))
                return Errors.BadRequest("You can only mark a class missed once it's on.");

            var existing = await db.Absences.FirstOrDefaultAsync(a => a.StudentId == id && a.CourseId == body.CourseId && a.Date == date && a.Start == start, ct);
            if (body.Missed && existing is null)
                db.Absences.Add(new Absence { StudentId = id, CourseId = body.CourseId, Date = date.Value, Start = start, CreatedAt = clock.GetUtcNow() });
            else if (!body.Missed && existing is not null)
                db.Absences.Remove(existing);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // The limit, for the dashboard; owners change it.
        api.MapGet("/admin/attendance-limit", async (AppDb db, CancellationToken ct) => Results.Ok(new { percent = await Limit(db, ct) }))
            .RequireAuthorization(Session.StaffPolicy);
        api.MapPut("/admin/attendance-limit", async (LimitBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (body.Percent is not null and not (>= 1 and <= 100)) return Errors.BadRequest("The limit is a percentage from 1 to 100.");
            var setting = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == LimitSetting, ct);
            if (body.Percent is null)
            {
                if (setting is not null) db.AppSettings.Remove(setting);
            }
            else
            {
                if (setting is null) db.AppSettings.Add(setting = new AppSetting { Key = LimitSetting });
                setting.Value = body.Percent.Value.ToString(CultureInfo.InvariantCulture);
                setting.UpdatedBy = await OwnerEndpoints.ActorName(http, db, ct);
                setting.UpdatedAt = clock.GetUtcNow();
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { percent = body.Percent });
        }).RequireAuthorization(Session.OwnerPolicy);
    }
}
