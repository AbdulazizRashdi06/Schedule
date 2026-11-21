using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Admin;

/// <summary>Dashboard endpoints for owners and RSA: semesters, uploads, courses and sections, majors, stats. No student names or IDs here.</summary>
public static class AdminEndpoints
{
    public static readonly string[] Terms = ["Fall", "Spring", "Summer"];
    public const long MaxUploadBytes = 5 * 1024 * 1024;

    public record SemesterBody(string? Term, int? Year);
    public record HiddenBody(bool Hidden);
    /// <summary>Both null = work them out from the dated classes again.</summary>
    public record WeeksBody(string? Week1Start, int? LastWeek);
    /// <summary><paramref name="Date"/> ("2026-09-21") keeps a dated class on its date; its weekday wins over Day.</summary>
    public record MeetingBody(int Day, string? Start, string? End, string? Room, string? Date = null);
    public record SectionBody(string? Instructor, List<MeetingBody>? Meetings);
    /// <summary><paramref name="Family"/>: null keeps it, "" takes the major out of its family.</summary>
    public record MajorBody(string? Code, string? Name, string? Family = null);
    /// <summary>One default: course code, major or family code, study year.</summary>
    public record DefaultItem(string? Id, string? Major, int? Year);
    public record ElectivesBody(List<string>? Codes);

    public static void MapAdmin(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").RequireAuthorization(Session.StaffPolicy);

        // ---- Semesters ----

        admin.MapGet("/semesters", async (AppDb db, CancellationToken ct) =>
            await db.Semesters.OrderByDescending(s => s.Year).ThenByDescending(s => s.Term == "Fall" ? 3 : s.Term == "Summer" ? 2 : 1)
                .Select(s => new
                {
                    s.Id, s.Name, s.Term, s.Year, s.IsCurrent, s.PublishedAt, s.Week1Start, s.LastWeek,
                    courses = s.Courses.Count,
                    sections = s.Courses.SelectMany(c => c.Sections).Count(),
                }).ToListAsync(ct));

        admin.MapPost("/semesters", async (SemesterBody body, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var term = Terms.FirstOrDefault(t => t.Equals(body.Term?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (term is null) return Errors.BadRequest("Pick Fall, Spring or Summer.");
            if (body.Year is not (>= 2020 and <= 2100)) return Errors.BadRequest("Enter a year like 2026.");
            var name = $"{term} {body.Year}";
            if (await db.Semesters.AnyAsync(s => s.Name == name, ct)) return Errors.Conflict($"{name} already exists.");
            var semester = new Semester { Name = name, Term = term, Year = body.Year.Value, CreatedAt = clock.GetUtcNow() };
            db.Semesters.Add(semester);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Dto.Of(semester));
        });

        // Publishing a different semester makes every student redo setup (StudentEndpoints.Progress compares semester ids).
        admin.MapPost("/semesters/{id:int}/publish", async (int id, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var semester = await db.Semesters.FindAsync([id], ct);
            if (semester is null) return Errors.NotFound("That semester doesn't exist.");
            if (semester.IsCurrent) return Results.Ok(Dto.Of(semester));
            if (!await db.Courses.AnyAsync(c => c.SemesterId == id, ct)) return Errors.BadRequest("Upload the schedule before publishing.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Semesters.Where(s => s.IsCurrent).ExecuteUpdateAsync(u => u.SetProperty(s => s.IsCurrent, false), ct);
            // Edits made before publishing aren't news to anyone.
            await db.Sections.Where(s => s.Course.SemesterId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.ChangedAt, (DateTimeOffset?)null).SetProperty(s => s.ChangeSummary, (string?)null), ct);
            semester.IsCurrent = true;
            semester.PublishedAt = clock.GetUtcNow();
            // The notification worker tells students with notifications on that it's out (Notifications.PublishAnnouncement).
            var pending = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == Students.Notifications.PublishPending, ct);
            if (pending is null) db.AppSettings.Add(pending = new AppSetting { Key = Students.Notifications.PublishPending, UpdatedBy = "system" });
            pending.Value = semester.Name;
            pending.UpdatedAt = semester.PublishedAt.Value;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(Dto.Of(semester));
        });

        // Teaching weeks: week 1's start (snapped to its Sunday) and the last week's number. Later weeks follow on.
        admin.MapGet("/semesters/{id:int}/weeks", async (int id, AppDb db, CancellationToken ct) =>
        {
            var semester = await db.Semesters.FindAsync([id], ct);
            return semester is null ? Errors.NotFound("That semester doesn't exist.") : Results.Ok(await SemesterWeeks.ForAsync(db, semester, ct));
        });

        admin.MapPut("/semesters/{id:int}/weeks", async (int id, WeeksBody body, AppDb db, CancellationToken ct) =>
        {
            var semester = await db.Semesters.FindAsync([id], ct);
            if (semester is null) return Errors.NotFound("That semester doesn't exist.");
            DateOnly? start = null;
            if (!string.IsNullOrWhiteSpace(body.Week1Start))
            {
                start = ScheduleImport.ParseDate(body.Week1Start);
                if (start is null) return Errors.BadRequest("Enter week 1's start date like 2026-09-20.");
                start = SemesterWeeks.Sunday(start.Value);
            }
            if (body.LastWeek is not null and not (>= 1 and <= 40)) return Errors.BadRequest("The last week should be between 1 and 40.");
            // The last week counts on from week 1, so it means nothing without it.
            if (body.LastWeek is not null && start is null) return Errors.BadRequest("Enter week 1's start date as well as the last week.");
            semester.Week1Start = start;
            semester.LastWeek = body.LastWeek;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await SemesterWeeks.ForAsync(db, semester, ct));
        });

        admin.MapDelete("/semesters/{id:int}", async (int id, AppDb db, CancellationToken ct) =>
        {
            var semester = await db.Semesters.FindAsync([id], ct);
            if (semester is null) return Errors.NotFound("That semester doesn't exist.");
            if (semester.IsCurrent) return Errors.Conflict("Students are using this semester. Publish another one first.");
            db.Semesters.Remove(semester);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Public notes on a course: staff can hide or delete them (private notes stay private) ----

        // The author's university ID is student data: owners see it, RSA gets null.
        admin.MapGet("/courses/{id:int}/notes", async (int id, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var isOwner = http.User.IsOwner();
            return Results.Ok(await db.ClassNotes.Where(n => n.CourseId == id && n.IsPublic)
                .OrderByDescending(n => n.ReportCount).ThenByDescending(n => n.CreatedAt)
                .Select(n => new
                {
                    n.Id, author = isOwner ? n.Student.UniversityId : null, n.Text, date = n.Date, section = n.Section == null ? null : n.Section.Number,
                    reports = n.ReportCount, hidden = n.HiddenAt != null, n.CreatedAt,
                }).ToListAsync(ct));
        });

        // Every public note in a semester (the current one by default), newest first.
        admin.MapGet("/notes", async (int? semesterId, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var isOwner = http.User.IsOwner();
            var sid = semesterId ?? await db.Semesters.Where(s => s.IsCurrent).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
            return Results.Ok(await db.ClassNotes.Where(n => n.IsPublic && n.Course.SemesterId == sid)
                .OrderByDescending(n => n.CreatedAt).Take(1000)
                .Select(n => new
                {
                    n.Id, courseId = n.CourseId, code = n.Course.Code, course = n.Course.Name, author = isOwner ? n.Student.UniversityId : null, n.Text,
                    date = n.Date, section = n.Section == null ? null : n.Section.Number,
                    reports = n.ReportCount, hidden = n.HiddenAt != null, n.CreatedAt,
                }).ToListAsync(ct));
        });

        admin.MapPut("/notes/{id:int}/hidden", async (int id, HiddenBody body, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var note = await db.ClassNotes.FirstOrDefaultAsync(n => n.Id == id && n.IsPublic, ct);
            if (note is null) return Errors.NotFound("That note doesn't exist.");
            note.HiddenAt = body.Hidden ? clock.GetUtcNow() : null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        admin.MapDelete("/notes/{id:int}", async (int id, AppDb db, CancellationToken ct) =>
        {
            var removed = await db.ClassNotes.Where(n => n.Id == id && n.IsPublic).ExecuteDeleteAsync(ct);
            return removed == 0 ? Errors.NotFound("That note doesn't exist.") : Results.NoContent();
        });

        // ---- Default courses: which courses are ticked for a major or family and study year ----

        admin.MapGet("/semesters/{id:int}/defaults", async (int id, AppDb db, CancellationToken ct) =>
        {
            if (!await db.Semesters.AnyAsync(s => s.Id == id, ct)) return Errors.NotFound("That semester doesn't exist.");
            var majors = await db.Majors.ToDictionaryAsync(m => m.Id, m => m.Code, ct);
            var rows = await db.CourseDefaults.Where(d => d.SemesterId == id)
                .Select(d => new { d.Course.Code, d.MajorId, d.Family, d.Year }).ToListAsync(ct);
            return Results.Ok(rows
                .Select(d => new { id = d.Code, major = d.MajorId is int m ? majors[m] : d.Family!, year = d.Year })
                .OrderBy(d => d.major).ThenBy(d => d.year).ThenBy(d => d.id));
        });

        // Replaces every default of the semester with the list. Nothing is saved while any row has a problem.
        admin.MapPut("/semesters/{id:int}/defaults", async (int id, List<DefaultItem>? body, AppDb db, CancellationToken ct) =>
        {
            if (!await db.Semesters.AnyAsync(s => s.Id == id, ct)) return Errors.NotFound("That semester doesn't exist.");
            if (body is null) return Errors.BadRequest("Send a list of { id, major, year }.");
            if (body.Count > 5000) return Errors.BadRequest("Too many rows.");
            // Grouped: two stored codes that differ only by spacing ("NB1101" and "NB 1 101" with a non-breaking space) mustn't crash.
            // Of those, the one with sections: the other was left by an upload and can't be taken.
            var courses = (await db.Courses.Where(c => c.SemesterId == id).OrderByDescending(c => c.Sections.Any()).ThenBy(c => c.Id).Select(c => new { c.Id, c.Code }).ToListAsync(ct))
                .GroupBy(c => CodeKey(c.Code)).ToDictionary(g => g.Key, g => g.First().Id);
            var majors = await db.Majors.ToListAsync(ct);
            var families = majors.Where(m => m.Family is not null).Select(m => m.Family!).ToHashSet();
            var problems = new List<string>();
            var rows = new List<CourseDefault>();
            for (var i = 0; i < body.Count; i++)
            {
                var item = body[i];
                var target = (item.Major ?? "").Trim().ToUpperInvariant();
                var row = $"Row {i + 1}";
                if (!courses.TryGetValue(CodeKey(item.Id ?? ""), out var courseId)) { problems.Add($"{row}: no course \"{item.Id}\" in this semester."); continue; }
                var major = majors.FirstOrDefault(m => m.Code == target);
                if (major is null && !families.Contains(target)) { problems.Add($"{row}: \"{item.Major}\" is not a major or a family."); continue; }
                // Don't quietly turn the family's defaults into the major's: say so, and the major can be renamed.
                if (major is not null && families.Contains(target)) { problems.Add($"{row}: \"{target}\" is both a major and a family. Rename the major under Majors first."); continue; }
                if (item.Year is not (>= 0 and <= 5)) { problems.Add($"{row}: year should be 0 (Foundation) to 5."); continue; }
                var family = major is null ? target : null;
                if (rows.Any(r => r.CourseId == courseId && r.MajorId == major?.Id && r.Family == family && r.Year == item.Year)) continue;
                rows.Add(new CourseDefault { SemesterId = id, CourseId = courseId, MajorId = major?.Id, Family = family, Year = item.Year.Value });
            }
            if (problems.Count > 0)
                return Results.Json(new { error = $"{problems.Count} {(problems.Count == 1 ? "row has a problem" : "rows have problems")}. Nothing was saved.", problems }, statusCode: StatusCodes.Status400BadRequest);
            // A lab follows its course: its own rows are dropped and the course's rows are copied onto it.
            var labParents = await Labs.ParentsAsync(db, id, ct);
            rows.RemoveAll(r => labParents.ContainsKey(r.CourseId));
            foreach (var (lab, parent) in labParents)
                rows.AddRange(rows.Where(r => r.CourseId == parent).ToList()
                    .Select(r => new CourseDefault { SemesterId = id, CourseId = lab, MajorId = r.MajorId, Family = r.Family, Year = r.Year }));
            // One transaction: a save cut off part-way keeps the old defaults instead of leaving none.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.CourseDefaults.Where(d => d.SemesterId == id).ExecuteDeleteAsync(ct);
            db.CourseDefaults.AddRange(rows);
            // A course placed in another year takes that year (the one it's used in most; the lower on a tie).
            var years = rows.GroupBy(r => r.CourseId).ToDictionary(g => g.Key, g => g.GroupBy(r => r.Year).OrderByDescending(y => y.Count()).ThenBy(y => y.Key).First().Key);
            foreach (var course in await db.Courses.Where(c => years.Keys.Contains(c.Id)).ToListAsync(ct))
                course.Year = years[course.Id];
            await db.SaveChangesAsync(ct);
            await Labs.AlignAsync(db, id, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { saved = rows.Count });
        });

        // ---- Export: every class of the semester as CSV, in the upload's own columns, so it can be edited and uploaded back ----

        admin.MapGet("/semesters/{id:int}/export.csv", async (int id, AppDb db, CancellationToken ct) =>
        {
            var semester = await db.Semesters.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (semester is null) return Errors.NotFound("That semester doesn't exist.");
            return Results.File(TimetableExport.Csv(await TimetableExport.RowsAsync(db, id, ct)), "text/csv; charset=utf-8", TimetableExport.FileName(semester));
        });

        // ---- Upload: the same file is sent to preview, then again to apply ----

        admin.MapPost("/semesters/{id:int}/upload/preview", async (int id, HttpRequest req, AppDb db, CancellationToken ct) =>
        {
            if (!await db.Semesters.AnyAsync(s => s.Id == id, ct)) return Errors.NotFound("That semester doesn't exist.");
            var (parsed, error) = await ReadUpload(req, db, ct);
            if (error is not null) return error;
            return Results.Ok(await ScheduleImport.PreviewAsync(db, id, parsed!, ct));
        });

        admin.MapPost("/semesters/{id:int}/upload/apply", async (int id, HttpRequest req, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var semester = await db.Semesters.FindAsync([id], ct);
            if (semester is null) return Errors.NotFound("That semester doesn't exist.");
            var (parsed, error) = await ReadUpload(req, db, ct);
            if (error is not null) return error;
            if (parsed!.Errors.Count > 0) return Errors.BadRequest($"Fix the {parsed.Errors.Count} problem row{(parsed.Errors.Count == 1 ? "" : "s")} first.");
            if (parsed.Courses.Count == 0) return Errors.BadRequest("The file has no courses.");
            var preview = await ScheduleImport.PreviewAsync(db, id, parsed, ct);
            var now = clock.GetUtcNow();
            await ScheduleImport.ApplyAsync(db, semester, parsed, clock, ct);
            await Labs.AlignAsync(db, semester.Id, ct);
            var fileName = Path.GetFileName(req.Form.Files.GetFile("file")?.FileName ?? "");
            db.UploadLogs.Add(new UploadLog
            {
                SemesterId = id,
                FileName = fileName == "" ? "Pasted rows" : fileName[..Math.Min(fileName.Length, 200)],
                By = await OwnerEndpoints.ActorName(http, db, ct),
                At = now,
                Added = preview.Added.Count,
                Updated = preview.Updated.Count,
                Removed = preview.Removed.Count,
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added = preview.Added.Count, updated = preview.Updated.Count, removed = preview.Removed.Count, preview.StudentsAffected });
        });

        // ---- Courses and sections (enrollment counts only) ----

        admin.MapGet("/semesters/{id:int}/courses", async (int id, string? q, AppDb db, CancellationToken ct) =>
        {
            var query = db.Courses.Where(c => c.SemesterId == id);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                var compact = term.Replace(" ", "");
                query = query.Where(c => c.Code.ToLower().Contains(compact) || c.Name.ToLower().Contains(term)
                    || c.Sections.Any(s => s.Instructor.ToLower().Contains(term)));
            }
            var courses = await query.OrderBy(c => c.Code).Select(c => new
            {
                c.Id, c.Code, c.Name, type = c.Type.ToString(), c.Year, c.Credits,
                majors = c.Majors.Select(m => m.Major.Code).ToList(),
                sections = c.Sections.Count,
                students = db.Picks.Count(p => p.CourseId == c.Id),
                // For the Courses table's status column.
                noRoom = c.Sections.Count(s => s.Meetings.Any(m => m.Room == "")),
                removedPicks = db.Picks.Count(p => p.CourseId == c.Id && p.SectionRemoved && p.SectionId == null),
                changed = c.Sections.Any(s => s.ChangedAt != null),
            }).ToListAsync(ct);
            return Results.Ok(courses);
        });

        admin.MapGet("/courses/{id:int}", async (int id, AppDb db, CancellationToken ct) =>
        {
            var course = await db.Courses.Where(c => c.Id == id)
                .Include(c => c.Semester)
                .Include(c => c.Majors).ThenInclude(m => m.Major)
                .Include(c => c.Sections).ThenInclude(s => s.Meetings)
                .AsSplitQuery().FirstOrDefaultAsync(ct);
            if (course is null) return Errors.NotFound("That course doesn't exist.");
            var counts = await db.Picks.Where(p => p.CourseId == id && p.SectionId != null)
                .GroupBy(p => p.SectionId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
            var noSection = await db.Picks.CountAsync(p => p.CourseId == id && p.SectionId == null, ct);
            var dto = Dto.Of(course);
            return Results.Ok(new
            {
                course = dto,
                semester = Dto.Of(course.Semester),
                weeks = await SemesterWeeks.ForAsync(db, course.Semester, ct),
                enrollment = dto.Sections.ToDictionary(s => s.Id.ToString(CultureInfo.InvariantCulture), s => counts.GetValueOrDefault(s.Id)),
                noSection,
            });
        });

        admin.MapPut("/sections/{id:int}", async (int id, SectionBody body, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var section = await db.Sections.Include(s => s.Meetings).Include(s => s.Course).ThenInclude(c => c.Semester)
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (section is null) return Errors.NotFound("That section doesn't exist.");
            var (meetings, problem) = ParseMeetings(body.Meetings, section.Meetings);
            if (problem is not null) return Errors.BadRequest(problem);
            var instructor = body.Instructor?.Trim() ?? "";
            var change = ScheduleImport.DescribeChange(section.Instructor, section.Meetings, instructor, meetings!.Select(ParsedMeeting.Of));
            if (change is null) return Results.Ok(Dto.Of(section));
            section.Instructor = instructor;
            db.Meetings.RemoveRange(section.Meetings);
            section.Meetings = meetings!;
            // Students only get the "Changed" badge for a published semester. Stamped once the write lock is held, so
            // change times follow commit order, as the notification worker needs (ScheduleImport.ApplyAsync).
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (section.Course.Semester.IsCurrent) { section.ChangedAt = clock.GetUtcNow(); section.ChangeSummary = change; }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(Dto.Of(section));
        });

        // ---- Majors and allowed electives ----

        admin.MapGet("/majors", async (AppDb db, CancellationToken ct) =>
        {
            var current = await db.Semesters.Where(s => s.IsCurrent).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
            return Results.Ok(await db.Majors.OrderBy(m => m.Name).Select(m => new
            {
                m.Id, m.Code, m.Name, m.Family,
                coreCourses = db.CourseMajors.Count(cm => cm.MajorId == m.Id && cm.Course.SemesterId == current && cm.Course.Type == CourseType.Core),
                extraElectives = m.Electives.Count,
                students = db.Students.Count(s => s.MajorId == m.Id),
            }).ToListAsync(ct));
        });

        admin.MapPost("/majors", async (MajorBody body, AppDb db, CancellationToken ct) =>
        {
            var (code, name, problem) = CleanMajor(body);
            if (problem is not null) return Errors.BadRequest(problem);
            if (await db.Majors.AnyAsync(m => m.Code == code, ct)) return Errors.Conflict($"A major with code {code} already exists.");
            if (await db.Majors.AnyAsync(m => m.Family == code, ct)) return Errors.Conflict($"{code} is already a family name.");
            var (family, familyProblem) = await CleanFamily(db, body.Family, code!, null, ct);
            if (familyProblem is not null) return Errors.BadRequest(familyProblem);
            var major = new Major { Code = code!, Name = name!, Family = family };
            db.Majors.Add(major);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { major.Id, major.Code, major.Name, major.Family });
        });

        admin.MapPut("/majors/{id:int}", async (int id, MajorBody body, AppDb db, CancellationToken ct) =>
        {
            var major = await db.Majors.FindAsync([id], ct);
            if (major is null) return Errors.NotFound("That major doesn't exist.");
            var (code, name, problem) = CleanMajor(body);
            if (problem is not null) return Errors.BadRequest(problem);
            if (await db.Majors.AnyAsync(m => m.Code == code && m.Id != id, ct)) return Errors.Conflict($"A major with code {code} already exists.");
            // Its own family doesn't count: a major stuck with its code as its family can still be edited.
            if (await db.Majors.AnyAsync(m => m.Family == code && m.Id != id, ct)) return Errors.Conflict($"{code} is already a family name.");
            var oldFamily = major.Family;
            var oldKey = PlannerJson.Family(major);
            if (body.Family is not null)
            {
                var (family, familyProblem) = await CleanFamily(db, body.Family, code!, id, ct);
                if (familyProblem is not null) return Errors.BadRequest(familyProblem);
                major.Family = family;
            }
            major.Code = code!;
            major.Name = name!;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await MoveFamily(db, major, oldFamily, oldKey, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { major.Id, major.Code, major.Name, major.Family });
        });

        admin.MapDelete("/majors/{id:int}", async (int id, AppDb db, CancellationToken ct) =>
        {
            var major = await db.Majors.FindAsync([id], ct);
            if (major is null) return Errors.NotFound("That major doesn't exist.");
            if (await db.CourseMajors.AnyAsync(cm => cm.MajorId == id, ct))
                return Errors.Conflict("Courses in an uploaded schedule use this major. Remove it from the schedule first.");
            if (await db.Students.AnyAsync(s => s.MajorId == id, ct))
                return Errors.Conflict("Students have picked this major.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // The family's last major: its defaults name nobody now, and would block saving the defaults grid.
            if (major.Family is { } family && !await db.Majors.AnyAsync(m => m.Family == family && m.Id != id, ct))
                await db.CourseDefaults.Where(d => d.Family == family).ExecuteDeleteAsync(ct);
            db.Majors.Remove(major);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // Electives of a semester and whether this major may take each one.
        admin.MapGet("/majors/{id:int}/electives", async (int id, int? semesterId, AppDb db, CancellationToken ct) =>
        {
            var major = await db.Majors.Include(m => m.Electives).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (major is null) return Errors.NotFound("That major doesn't exist.");
            var sid = semesterId ?? await db.Semesters.Where(s => s.IsCurrent).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct)
                ?? await db.Semesters.OrderByDescending(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
            var electives = sid is null ? [] : await db.Courses.Where(c => c.SemesterId == sid && c.Type == CourseType.Elective)
                .OrderBy(c => c.Code)
                .Select(c => new { c.Code, c.Name, majors = c.Majors.Select(m => m.MajorId).ToList() }).ToListAsync(ct);
            var extra = major.Electives.Select(e => e.CourseCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Students get what their whole family may take, so the other majors of the family count too.
            var family = await StudentEndpoints.FamilyMajorIds(db, id, ct);
            var viaFamily = (await db.MajorElectives.Where(e => family.Contains(e.MajorId) && e.MajorId != id).Select(e => e.CourseCode).ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                major = new { major.Id, major.Code, major.Name },
                semesterId = sid,
                electives = electives.Select(c => new
                {
                    c.Code, c.Name,
                    // "upload": the schedule lists this major (or another major of its family); "open": no majors listed,
                    // so anyone may take it; "family": another major of the family is allowed it, so this one's students are too.
                    source = c.majors.Any(family.Contains) ? "upload" : c.majors.Count == 0 ? "open" : extra.Contains(c.Code) ? "allowed"
                        : viaFamily.Contains(c.Code) ? "family" : "no",
                }),
                // Allowed codes not offered this semester stay allowed for later semesters.
                otherAllowed = extra.Where(code => electives.All(c => !c.Code.Equals(code, StringComparison.OrdinalIgnoreCase))).Order().ToList(),
            });
        });

        admin.MapPut("/majors/{id:int}/electives", async (int id, ElectivesBody body, AppDb db, CancellationToken ct) =>
        {
            var major = await db.Majors.Include(m => m.Electives).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (major is null) return Errors.NotFound("That major doesn't exist.");
            var codes = (body.Codes ?? []).Select(CodeKey).Where(c => c.Length > 0)
                .Distinct().ToList();
            if (codes.Count > 500) return Errors.BadRequest("Too many courses.");
            major.Electives.RemoveAll(e => !codes.Contains(e.CourseCode, StringComparer.OrdinalIgnoreCase));
            foreach (var code in codes.Where(c => !major.Electives.Any(e => e.CourseCode.Equals(c, StringComparison.OrdinalIgnoreCase))))
                major.Electives.Add(new MajorElective { MajorId = id, CourseCode = code });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Stats (counts only) ----

        admin.MapGet("/stats", async (int? semesterId, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var isOwner = http.User.IsOwner();
            var current = await db.Semesters.Where(s => s.IsCurrent).FirstOrDefaultAsync(ct);
            var sid = semesterId ?? current?.Id;
            var currentId = current?.Id;
            var now = clock.GetUtcNow();
            var weekAgo = now.AddDays(-7);
            // "Today" is Oman time (UTC+4), from midnight.
            var oman = now.ToOffset(TimeSpan.FromHours(4));
            var todayStart = new DateTimeOffset(oman.Date, oman.Offset).ToUniversalTime();
            var activeSince = now.AddMinutes(-5);
            // "Needs attention" on the dashboard home: removed sections students must re-pick, sections without a room, students stuck in setup.
            var removed = sid is null ? [] : await db.Picks.Where(p => p.Course.SemesterId == sid && p.SectionRemoved && p.SectionId == null)
                .GroupBy(p => new { p.CourseId, p.Course.Code })
                .Select(g => new { courseId = g.Key.CourseId, code = g.Key.Code, students = g.Count() })
                .OrderByDescending(x => x.students).ToListAsync(ct);
            var noRoom = sid is null ? [] : await db.Sections.Where(s => s.Course.SemesterId == sid && s.Meetings.Any(m => m.Room == ""))
                .OrderBy(s => s.Course.Code).ThenBy(s => s.Number)
                .Select(s => new { courseId = s.CourseId, code = s.Course.Code, number = s.Number }).ToListAsync(ct);
            var uploads = sid is null ? [] : await db.UploadLogs.Where(u => u.SemesterId == sid).OrderByDescending(u => u.At).Take(5).ToListAsync(ct);
            var topSections = sid is null ? [] : await db.Sections.Where(s => s.Course.SemesterId == sid)
                .Select(s => new { s.Id, courseId = s.CourseId, s.Course.Code, s.Number, students = db.Picks.Count(p => p.SectionId == s.Id) })
                .OrderByDescending(x => x.students).ThenBy(x => x.Code).Take(8).ToListAsync(ct);
            return Results.Ok(new
            {
                semester = current is null ? null : Dto.Of(current),
                studentsRegistered = await db.Students.CountAsync(ct),
                // Students who used the app in the last 5 minutes, and since midnight.
                activeNow = await db.Students.CountAsync(s => s.LastSeenAt != null && s.LastSeenAt >= activeSince, ct),
                activeToday = await db.Students.CountAsync(s => s.LastSeenAt != null && s.LastSeenAt >= todayStart, ct),
                studentsFinished = current is null ? 0 : await db.Students.CountAsync(s => s.SetupSemesterId == current.Id && s.SetupCompletedAt != null, ct),
                courses = sid is null ? 0 : await db.Courses.CountAsync(c => c.SemesterId == sid, ct),
                sections = sid is null ? 0 : await db.Sections.CountAsync(s => s.Course.SemesterId == sid, ct),
                needRepick = sid is null ? 0 : await db.Picks.Where(p => p.Course.SemesterId == sid && p.SectionRemoved && p.SectionId == null).Select(p => p.StudentId).Distinct().CountAsync(ct),
                topSections = topSections.Where(x => x.students > 0),
                sectionEditsThisWeek = sid is null ? 0 : await db.Sections.CountAsync(s => s.Course.SemesterId == sid && s.ChangedAt != null && s.ChangedAt > weekAgo, ct),
                attention = new
                {
                    removed,
                    noRoom,
                    // Signed in but haven't finished setup for the live semester (nobody is stuck while nothing is published).
                    stuckInSetup = current is null ? 0 : await db.Students.CountAsync(s => !(s.SetupSemesterId == currentId && s.SetupCompletedAt != null), ct),
                    // Public notes classmates reported that staff haven't hidden yet.
                    reportedNotes = sid is null ? [] : await db.ClassNotes.Where(n => n.IsPublic && n.ReportCount > 0 && n.HiddenAt == null && n.Course.SemesterId == sid)
                        .GroupBy(n => new { n.CourseId, n.Course.Code })
                        .Select(g => new { courseId = g.Key.CourseId, code = g.Key.Code, notes = g.Count() }).ToListAsync(ct),
                },
                recentUploads = uploads.Select(u => new { at = u.At, u.FileName, by = OwnerEndpoints.ShownActor(u.By, isOwner), u.Added, u.Updated, u.Removed }),
            });
        });
    }

    static (string? Code, string? Name, string? Problem) CleanMajor(MajorBody body)
    {
        var code = body.Code is null ? null : CodeKey(body.Code);
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 16) return (null, null, "Enter a short code like CS (up to 16 characters).");
        if (string.IsNullOrEmpty(name) || name.Length > 100) return (null, null, "Enter the major's name.");
        return (code, name, null);
    }

    /// <summary>"cs 1 101", "CS1101" and "CS  1 101" all match "CS 1 101".</summary>
    static string CodeKey(string code) => string.Concat(code.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>
    /// Blank takes the major out of its family. A family can't use a major's code, since defaults name either:
    /// not another major's, and not the <paramref name="code"/> this major will have (<paramref name="id"/>: the major being edited).
    /// </summary>
    static async Task<(string? Family, string? Problem)> CleanFamily(AppDb db, string? raw, string code, int? id, CancellationToken ct)
    {
        var family = raw is null ? null : CodeKey(raw);
        if (string.IsNullOrEmpty(family)) return (null, null);
        if (family.Length > 16) return (null, "Keep the family code short, like COMP (up to 16 characters).");
        if (family == code) return (null, "A family can't use the major's own code. Pick another family name.");
        if (await db.Majors.AnyAsync(m => m.Code == family && m.Id != id, ct)) return (null, $"{family} is already a major's code. Pick another family name.");
        return (family, null);
    }

    /// <summary>
    /// After a major's family changed: when no other major is left in the old family, what's stored under the old name
    /// moves with this major, so it keeps working. Its default courses go to the new family (or become the major's own),
    /// unless the new place already has defaults for that semester and year. Its planner workspaces and batch reps go to
    /// the new planner family; a workspace that already exists there takes in the old one's batches, drafts and history.
    /// </summary>
    static async Task MoveFamily(AppDb db, Major major, string? oldFamily, string oldKey, CancellationToken ct)
    {
        var others = db.Majors.Where(m => m.Id != major.Id);
        if (oldFamily is not null && oldFamily != major.Family && !await others.AnyAsync(m => m.Family == oldFamily, ct))
        {
            var moving = await db.CourseDefaults.Where(d => d.Family == oldFamily).ToListAsync(ct);
            var there = major.Family is null ? db.CourseDefaults.Where(d => d.MajorId == major.Id) : db.CourseDefaults.Where(d => d.Family == major.Family);
            var taken = (await there.Select(d => new { d.SemesterId, d.Year }).ToListAsync(ct)).ToHashSet();
            foreach (var d in moving)
            {
                if (taken.Contains(new { d.SemesterId, d.Year })) { db.CourseDefaults.Remove(d); continue; }
                if (major.Family is null) d.MajorId = major.Id;
                d.Family = major.Family;
            }
        }

        var newKey = PlannerJson.Family(major);
        if (newKey == oldKey || (await others.ToListAsync(ct)).Any(m => PlannerJson.Family(m) == oldKey)) return;
        foreach (var rep in await db.BatchRepAssignments.Where(r => r.HomeFamily == oldKey).ToListAsync(ct)) rep.HomeFamily = newKey;
        foreach (var old in await db.PlannerWorkspaces.Where(w => w.Family == oldKey).ToListAsync(ct))
        {
            var into = await db.PlannerWorkspaces.FirstOrDefaultAsync(w => w.SemesterId == old.SemesterId && w.Family == newKey, ct);
            if (into is null) { old.Family = newKey; continue; }
            await db.PlannerBatches.Where(x => x.WorkspaceId == old.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.WorkspaceId, into.Id), ct);
            await db.PlannerProposals.Where(x => x.WorkspaceId == old.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.WorkspaceId, into.Id), ct);
            // Activities too: the first "published" one says which workspace may change an event.
            await db.PlannerActivities.Where(x => x.WorkspaceId == old.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.WorkspaceId, into.Id), ct);
            await db.PlannerAiJobs.Where(x => x.WorkspaceId == old.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.WorkspaceId, into.Id), ct);
            db.PlannerWorkspaces.Remove(old);
        }
    }

    /// <summary>
    /// <paramref name="stored"/>: the section's meetings now. Two of them that overlap may stay (an upload can list a
    /// class in two rooms), so editing anything else still saves; only a new overlap is refused.
    /// Each stored meeting covers one sent meeting, so a copy of one is still a new overlap.
    /// </summary>
    public static (List<Meeting>? Meetings, string? Problem) ParseMeetings(List<MeetingBody>? input, IEnumerable<Meeting>? stored = null)
    {
        var unmatched = (stored ?? []).Select(ParsedMeeting.Of).ToList();
        var kept = new List<bool>();
        if (input is null || input.Count == 0) return (null, "Add at least one meeting time.");
        if (input.Count > 400) return (null, "Too many meetings.");
        var list = new List<Meeting>();
        foreach (var m in input)
        {
            DateOnly? date = null;
            if (!string.IsNullOrWhiteSpace(m.Date))
            {
                date = ScheduleImport.ParseDate(m.Date);
                if (date is null) return (null, $"Can't read the date \"{m.Date}\".");
            }
            var day = date is null ? m.Day : (int)date.Value.DayOfWeek;
            if (day is < 0 or > 6) return (null, "Pick a day for every meeting.");
            if (!TryTime(m.Start, out var start) || !TryTime(m.End, out var end)) return (null, "Enter times like 08:00.");
            if (end <= start) return (null, $"On {Timeslots.DayNames[day]} the class ends before it starts.");
            var room = m.Room?.Trim() ?? "";
            if (room.Length > 60) return (null, "The room name is too long.");
            var meeting = new Meeting { Day = day, Start = start, End = end, Room = room, Date = date };
            var isKept = unmatched.Remove(ParsedMeeting.Of(meeting));
            if (list.Where((x, i) => Clashes.Overlap(x, meeting) && !(kept[i] && isKept)).Any()) return (null, $"Two meetings overlap on {(date is null ? Timeslots.DayNames[day] : date.Value.ToString("d MMM"))}.");
            list.Add(meeting);
            kept.Add(isKept);
        }
        return (list, null);
    }

    static bool TryTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    /// <summary>Reads a multipart "file" (.xlsx, .csv, .tsv) or a "text" field pasted from Excel.</summary>
    static async Task<(ParsedSchedule? Parsed, IResult? Error)> ReadUpload(HttpRequest req, AppDb db, CancellationToken ct)
    {
        if (!req.HasFormContentType) return (null, Errors.BadRequest("Choose a file to upload."));
        if (req.ContentLength > MaxUploadBytes) return (null, Errors.BadRequest("The file is too big (5 MB at most)."));
        var form = await req.ReadFormAsync(ct);
        ScheduleFile.ReadResult read;
        var file = form.Files.GetFile("file");
        if (file is not null)
        {
            if (file.Length > MaxUploadBytes) return (null, Errors.BadRequest("The file is too big (5 MB at most)."));
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not (".xlsx" or ".csv" or ".tsv" or ".txt")) return (null, Errors.BadRequest("Upload an .xlsx or .csv file."));
            await using var stream = file.OpenReadStream();
            try { read = ScheduleFile.Read(stream, file.FileName); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return (null, Errors.BadRequest("We couldn't read that file. Save it as .xlsx or .csv and try again."));
            }
        }
        else if (form.TryGetValue("text", out var text) && !string.IsNullOrWhiteSpace(text))
            read = ScheduleFile.ReadText(text.ToString());
        else return (null, Errors.BadRequest("Choose a file to upload."));

        if (read.Problems.Count > 0) return (null, Errors.BadRequest(string.Join(" ", read.Problems)));
        if (read.Rows.Count > 20_000) return (null, Errors.BadRequest("That file has too many rows."));
        var families = await ScheduleImport.FamilyCodesAsync(db, ct);
        // The parsers turn what they can't read into row errors; this is only a net, so a slip is a 400 and not a 500.
        try { return (ScheduleImport.Parse(read.Rows, families), null); }
        catch (FormatException) { return (null, Errors.BadRequest("Some dates or times in the file couldn't be read. Type them like 2026-09-21 and 08:00.")); }
    }
}
