using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
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
    /// <summary>Most sessions one student can mark missed in one course: well above any real semester's count.</summary>
    public const int MaxPerCourse = 200;

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
            // A session a teacher cancelled or moved away isn't one that can be missed, unless another change put a
            // class in that very slot (two sessions swapped with each other).
            if (list.Count > 0)
            {
                var sections = await db.Picks.Where(p => p.StudentId == id && p.Course.SemesterId == current!.Id && p.Section != null)
                    .Select(p => p.Section!).Include(s => s.Meetings).ToListAsync(ct);
                var changes = await ClassChanges.LoadAsync(db, current!.Id, sections, ct);
                if (!changes.Empty)
                    list = list.Where(a => !sections.Any(s => s.CourseId == a.CourseId
                        && changes.OffIn(s).Any(o => o.Date == a.Date && o.Start == a.Start)
                        && !changes.AddedTo(s).Any(x => x.Date == a.Date && x.Start == a.Start))).ToList();
            }
            return Results.Ok(new
            {
                limit = await Limit(db, ct),
                absences = list.Select(a => new { a.CourseId, date = a.Date.ToString("yyyy-MM-dd"), start = Timeslots.Format(a.Start) }),
            });
        });

        // Marks one session missed, or not. Only for sessions of the student's own classes, and not for days still to come.
        me.MapPut("", async (AbsenceBody body, HttpContext http, AppDb db, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
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

            var same = db.Absences.Where(a => a.StudentId == id && a.CourseId == body.CourseId && a.Date == date && a.Start == start);
            // Unmarking is always allowed, so a session that later moved or was cancelled can still be cleared. One
            // statement, so it doesn't depend on an earlier read when two taps cross.
            if (!body.Missed)
            {
                await same.ExecuteDeleteAsync(ct);
                return Results.NoContent();
            }
            if (await same.AnyAsync(ct)) return Results.NoContent();

            // Only a session the timetable really has then: a class or a batch rep's class (exams don't count, as in the
            // app), with days off and cancelled or moved classes taken out.
            var span = await SemesterSpan.ForAsync(db, current, ct);
            var items = await StudentSchedule.ForAsync(db, current.Id, [id], slots: false, await PlannerEngine.Enabled(db, config, ct), ct);
            if (!items.Any(i => i.CourseId == body.CourseId && i.Start == start && i.EventKind != "exam" && span.Happens(i, date.Value)))
                return Errors.BadRequest("This class has no session then.");
            if (await db.Absences.CountAsync(a => a.StudentId == id && a.CourseId == body.CourseId, ct) >= MaxPerCourse)
                return Errors.BadRequest("That's more sessions than this class has.");

            db.Absences.Add(new Absence { StudentId = id, CourseId = body.CourseId, Date = date.Value, Start = start, CreatedAt = clock.GetUtcNow() });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Another tap marked it at the same moment: it's marked, which is what was asked.
                db.ChangeTracker.Clear();
                if (!await same.AnyAsync(ct)) throw;
            }
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
