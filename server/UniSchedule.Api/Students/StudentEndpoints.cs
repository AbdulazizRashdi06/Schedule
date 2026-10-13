using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Students;

public static class StudentEndpoints
{
    public static readonly string[] Steps = ["major", "year", "core", "ls", "electives", "done"];

    public record ProfileBody(int? MajorId, int? Year);
    public record StepBody(string? Step);
    public record PickItem(int CourseId, int? SectionId);
    public record PicksBody(List<PickItem>? Picks, string? Step);
    public record SeenBody(int SectionId);

    public static Task<Semester?> CurrentSemester(AppDb db, CancellationToken ct) =>
        db.Semesters.Where(s => s.IsCurrent).FirstOrDefaultAsync(ct);

    /// <summary>Academic year for the year prediction when nothing is published yet: from August a new one starts.</summary>
    public static int AcademicStartYear(Semester? current, DateTimeOffset now) =>
        current?.AcademicStartYear ?? (now.Month >= 8 ? now.Year : now.Year - 1);

    /// <summary>Where the student is in setup for the published semester (see the plan, Parts A and E).</summary>
    public static (string Step, bool Done) Progress(Student s, Semester? current)
    {
        if (current is null)
            return (s.MajorId is null ? "major" : s.Year is null ? "year" : "waiting", false);
        if (s.SetupSemesterId == current.Id)
        {
            var step = s.SetupStep ?? "major";
            return (step, step == "done" && s.SetupCompletedAt is not null);
        }
        // Picked major and year while waiting for the first semester: carry on from core courses.
        if (s.SetupSemesterId is null && s.SetupStep == "waiting" && s.MajorId is not null && s.Year is not null)
            return ("core", false);
        // A new semester was published: full setup again (major and year stay filled in as suggestions).
        return ("major", false);
    }

    static IQueryable<Course> SemesterCourses(AppDb db, int semesterId) =>
        db.Courses.Where(c => c.SemesterId == semesterId)
            .Include(c => c.Majors).ThenInclude(m => m.Major)
            .Include(c => c.Sections).ThenInclude(s => s.Meetings)
            .AsSplitQuery();

    /// <summary>The major and every other major in its family: they share their courses.</summary>
    public static async Task<List<int>> FamilyMajorIds(AppDb db, int majorId, CancellationToken ct)
    {
        var family = await db.Majors.Where(m => m.Id == majorId).Select(m => m.Family).FirstOrDefaultAsync(ct);
        return family is null ? [majorId] : await db.Majors.Where(m => m.Family == family).Select(m => m.Id).ToListAsync(ct);
    }

    /// <summary>Courses this student may pick: core for their major's family (every year), all LS, electives the family allows.</summary>
    public static async Task<List<Course>> Options(AppDb db, int semesterId, int majorId, CancellationToken ct)
    {
        var all = await SemesterCourses(db, semesterId).ToListAsync(ct);
        var majors = await FamilyMajorIds(db, majorId, ct);
        var extra = (await db.MajorElectives.Where(e => majors.Contains(e.MajorId)).Select(e => e.CourseCode).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.Where(c => c.Type switch
        {
            CourseType.Core => c.Majors.Any(m => majors.Contains(m.MajorId)),
            CourseType.LS => true,
            // No majors listed means any major may take it; admins can allow more under Majors.
            CourseType.Elective => c.Majors.Count == 0 || c.Majors.Any(m => majors.Contains(m.MajorId)) || extra.Contains(c.Code),
            _ => false,
        }).OrderBy(c => c.Code).ToList();
    }

    /// <summary>
    /// Courses ticked when setup starts: the major's own defaults for that year, else its family's,
    /// else the upload's core courses listed for that major and year.
    /// </summary>
    public static async Task<List<int>> DefaultCourseIds(AppDb db, int semesterId, int majorId, int year, CancellationToken ct)
    {
        var defaults = db.CourseDefaults.Where(d => d.SemesterId == semesterId && d.Year == year);
        var own = await defaults.Where(d => d.MajorId == majorId).Select(d => d.CourseId).ToListAsync(ct);
        if (own.Count > 0) return own;
        var family = await db.Majors.Where(m => m.Id == majorId).Select(m => m.Family).FirstOrDefaultAsync(ct);
        if (family is not null)
        {
            var shared = await defaults.Where(d => d.Family == family).Select(d => d.CourseId).ToListAsync(ct);
            if (shared.Count > 0) return shared;
        }
        return await db.Courses.Where(c => c.SemesterId == semesterId && c.Type == CourseType.Core && c.Year == year && c.Majors.Any(m => m.MajorId == majorId))
            .Select(c => c.Id).ToListAsync(ct);
    }

    /// <summary>
    /// Ticks the default courses of the student's major and year when they have no core course yet this semester.
    /// A course with a single section gets that section picked too.
    /// </summary>
    public static async Task EnsureCoreTicked(AppDb db, Student s, Semester current, CancellationToken ct)
    {
        if (s.MajorId is null || s.Year is null) return;
        if (await db.Picks.AnyAsync(p => p.StudentId == s.Id && p.Course.SemesterId == current.Id && p.Course.Type == CourseType.Core, ct)) return;
        var ids = await DefaultCourseIds(db, current.Id, s.MajorId.Value, s.Year.Value, ct);
        var cores = await db.Courses.Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, Sections = c.Sections.Select(x => x.Id).ToList() }).ToListAsync(ct);
        var taken = await db.Picks.Where(p => p.StudentId == s.Id && p.Course.SemesterId == current.Id).Select(p => p.CourseId).ToListAsync(ct);
        foreach (var c in cores.Where(c => !taken.Contains(c.Id)))
            db.Picks.Add(new Pick { StudentId = s.Id, CourseId = c.Id, SectionId = c.Sections.Count == 1 ? c.Sections[0] : null });
        await db.SaveChangesAsync(ct);
    }

    public static void MapStudent(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me").RequireAuthorization(Session.StudentPolicy);

        me.MapGet("/", async (HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var s = await db.Students.Include(x => x.Major).FirstAsync(x => x.Id == http.User.Id(), ct);
            var current = await CurrentSemester(db, ct);
            var (step, done) = Progress(s, current);
            if (current is not null && s.SetupSemesterId is null && step == "core")
            {
                // The first semester was published while they waited: carry on from core courses.
                s.SetupSemesterId = current.Id;
                s.SetupStep = "core";
                await db.SaveChangesAsync(ct);
                await EnsureCoreTicked(db, s, current, ct);
            }
            return Results.Ok(new
            {
                s.UniversityId,
                s.Email,
                s.IsOwner,
                major = s.Major is null ? null : new { s.Major.Id, s.Major.Code, s.Major.Name },
                s.Year,
                predictedYear = StudentIds.PredictYear(s.UniversityId, AcademicStartYear(current, clock.GetUtcNow())),
                semester = current is null ? null : new { current.Id, current.Name },
                step,
                setupDone = done,
            });
        });

        api.MapGet("/majors", async (AppDb db, CancellationToken ct) =>
            await db.Majors.OrderBy(m => m.Name).Select(m => new { m.Id, m.Code, m.Name }).ToListAsync(ct))
            .RequireAuthorization();

        me.MapPut("/profile", async (ProfileBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            var current = await CurrentSemester(db, ct);
            if (body.MajorId is int majorId)
            {
                if (!await db.Majors.AnyAsync(m => m.Id == majorId, ct)) return Errors.BadRequest("Pick a major from the list.");
                if (s.MajorId != majorId && current is not null)
                {
                    // Core courses and electives of the old major no longer fit.
                    var stale = await db.Picks.Where(p => p.StudentId == s.Id && p.Course.SemesterId == current.Id && p.Course.Type != CourseType.LS).ToListAsync(ct);
                    db.Picks.RemoveRange(stale);
                }
                s.MajorId = majorId;
            }
            if (body.Year is int year)
            {
                if (year is < 0 or > 5) return Errors.BadRequest("Pick Foundation or a year from 1 to 5.");
                if (s.Year != year && current is not null && s.SetupSemesterId == current.Id && s.SetupStep is "major" or "year" or "core")
                {
                    // Different year before setup is done: re-tick the right core courses.
                    var cores = await db.Picks.Where(p => p.StudentId == s.Id && p.Course.SemesterId == current.Id && p.Course.Type == CourseType.Core).ToListAsync(ct);
                    db.Picks.RemoveRange(cores);
                }
                s.Year = year;
            }
            if (current is null)
            {
                s.SetupStep = s.MajorId is not null && s.Year is not null ? "waiting" : s.MajorId is null ? "major" : "year";
                s.SetupSemesterId = null;
            }
            else
            {
                if (s.SetupSemesterId != current.Id)
                {
                    s.SetupSemesterId = current.Id;
                    s.SetupCompletedAt = null;
                    s.SetupStep = "major";
                }
                var target = body.Year is not null ? "core" : "year";
                if (Array.IndexOf(Steps, target) > Array.IndexOf(Steps, s.SetupStep ?? "major") && s.SetupStep != "done") s.SetupStep = target;
            }
            await db.SaveChangesAsync(ct);
            if (current is not null && body.Year is not null) await EnsureCoreTicked(db, s, current, ct);
            return Results.NoContent();
        });

        me.MapPut("/step", async (StepBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            if (body.Step is null || Array.IndexOf(Steps, body.Step) < 0 || body.Step == "done") return Errors.BadRequest("Unknown step.");
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            var current = await CurrentSemester(db, ct);
            if (current is null || s.SetupSemesterId != current.Id) return Errors.Conflict("Start from your major.");
            if (s.SetupStep != "done") s.SetupStep = body.Step;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        me.MapGet("/options", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            var current = await CurrentSemester(db, ct);
            if (current is null) return Errors.Conflict("No semester has been published yet.");
            if (s.MajorId is null) return Errors.Conflict("Pick your major first.");
            var courses = await Options(db, current.Id, s.MajorId.Value, ct);
            // Last semester's picks, shown as suggestions after a new semester is published.
            var lastSemester = await db.Picks.Where(p => p.StudentId == s.Id && p.Course.SemesterId != current.Id)
                .OrderByDescending(p => p.Course.SemesterId).Select(p => (int?)p.Course.SemesterId).FirstOrDefaultAsync(ct);
            var previous = lastSemester is null ? [] : await db.Picks.Where(p => p.StudentId == s.Id && p.Course.SemesterId == lastSemester)
                .Select(p => p.Course.Code).ToListAsync(ct);
            return Results.Ok(new { semester = new { current.Id, current.Name }, year = s.Year, courses = courses.Select(Dto.Of), previous });
        });

        me.MapGet("/picks", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var current = await CurrentSemester(db, ct);
            if (current is null) return Results.Ok(Array.Empty<object>());
            var id = http.User.Id();
            var picks = await db.Picks.Where(p => p.StudentId == id && p.Course.SemesterId == current.Id)
                .Select(p => new { p.CourseId, p.SectionId, p.SectionRemoved }).ToListAsync(ct);
            return Results.Ok(picks);
        });

        me.MapPut("/picks", async (PicksBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            var current = await CurrentSemester(db, ct);
            if (current is null) return Errors.Conflict("No semester has been published yet.");
            if (s.MajorId is null) return Errors.Conflict("Pick your major first.");
            var wanted = body.Picks ?? [];
            if (wanted.Select(p => p.CourseId).Distinct().Count() != wanted.Count) return Errors.BadRequest("Each course can only be picked once.");

            var allowed = (await Options(db, current.Id, s.MajorId.Value, ct)).ToDictionary(c => c.Id);
            foreach (var p in wanted)
            {
                if (!allowed.TryGetValue(p.CourseId, out var course)) return Errors.BadRequest("One of the courses isn't available for your major.");
                if (p.SectionId is int sid && course.Sections.All(x => x.Id != sid)) return Errors.BadRequest($"That section isn't part of {course.Code}.");
            }

            var existing = await db.Picks.Where(p => p.StudentId == s.Id && p.Course.SemesterId == current.Id).ToListAsync(ct);
            db.Picks.RemoveRange(existing.Where(e => wanted.All(w => w.CourseId != e.CourseId)));
            foreach (var w in wanted)
            {
                var pick = existing.FirstOrDefault(e => e.CourseId == w.CourseId);
                if (pick is null) db.Picks.Add(pick = new Pick { StudentId = s.Id, CourseId = w.CourseId });
                if (pick.SectionId != w.SectionId)
                {
                    pick.SectionId = w.SectionId;
                    pick.SeenAt = null;
                    if (w.SectionId is not null) pick.SectionRemoved = false;
                }
            }
            if (body.Step is not null && Array.IndexOf(Steps, body.Step) >= 0 && body.Step != "done" && s.SetupSemesterId == current.Id && s.SetupStep != "done")
                s.SetupStep = body.Step;
            await db.SaveChangesAsync(ct);

            var sections = wanted.Where(w => w.SectionId is not null)
                .Select(w => allowed[w.CourseId].Sections.First(x => x.Id == w.SectionId)).ToList();
            return Results.Ok(new { clashes = Clashes.Find(sections) });
        });

        me.MapPost("/finish", async (HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            var current = await CurrentSemester(db, ct);
            if (current is null) return Errors.Conflict("No semester has been published yet.");
            var picks = await db.Picks.Include(p => p.Course).Where(p => p.StudentId == s.Id && p.Course.SemesterId == current.Id).ToListAsync(ct);
            if (picks.Count == 0) return Errors.BadRequest("Pick at least one course.");
            var missing = picks.Where(p => p.SectionId is null).Select(p => p.Course.Code).ToList();
            if (missing.Count > 0) return Errors.BadRequest($"Pick a section for {string.Join(", ", missing)}.");
            s.SetupSemesterId = current.Id;
            s.SetupStep = "done";
            s.SetupCompletedAt ??= clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        me.MapGet("/timetable", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var current = await CurrentSemester(db, ct);
            if (current is null) return Results.Ok(new { semester = (object?)null, classes = Array.Empty<object>(), needsSection = Array.Empty<object>(), clashes = Array.Empty<int[]>() });
            var picks = await db.Picks.Where(p => p.StudentId == id && p.Course.SemesterId == current.Id)
                .Include(p => p.Course)
                .Include(p => p.Section).ThenInclude(s => s!.Meetings)
                .AsSplitQuery().ToListAsync(ct);
            var withSection = picks.Where(p => p.Section is not null).OrderBy(p => p.Course.Code).ToList();
            return Results.Ok(new
            {
                semester = new { current.Id, current.Name },
                classes = withSection.Select(p => new
                {
                    courseId = p.CourseId,
                    p.Course.Code,
                    p.Course.Name,
                    type = p.Course.Type.ToString(),
                    p.Course.Credits,
                    section = Dto.Of(p.Section!),
                    changed = p.Section!.ChangedAt is not null && (p.SeenAt is null || p.SeenAt < p.Section.ChangedAt),
                }),
                needsSection = picks.Where(p => p.Section is null).Select(p => new { courseId = p.CourseId, p.Course.Code, p.Course.Name, removed = p.SectionRemoved }),
                clashes = Clashes.Find(withSection.Select(p => p.Section!).ToList()),
            });
        });

        me.MapPost("/seen", async (SeenBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var id = http.User.Id();
            await db.Picks.Where(p => p.StudentId == id && p.SectionId == body.SectionId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.SeenAt, clock.GetUtcNow()), ct);
            return Results.NoContent();
        });
    }
}
