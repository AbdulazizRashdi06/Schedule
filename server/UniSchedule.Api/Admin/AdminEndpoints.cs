using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Admin;

/// <summary>Dashboard endpoints for owners and RSA: semesters, uploads, courses and sections, majors, stats. No student names or IDs here.</summary>
public static class AdminEndpoints
{
    public static readonly string[] Terms = ["Fall", "Spring", "Summer"];
    public const long MaxUploadBytes = 5 * 1024 * 1024;

    public record SemesterBody(string? Term, int? Year);
    public record MeetingBody(int Day, string? Start, string? End, string? Room);
    public record SectionBody(string? Instructor, List<MeetingBody>? Meetings);
    public record MajorBody(string? Code, string? Name);
    public record ElectivesBody(List<string>? Codes);

    public static void MapAdmin(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").RequireAuthorization(Session.StaffPolicy);

        // ---- Semesters ----

        admin.MapGet("/semesters", async (AppDb db, CancellationToken ct) =>
            await db.Semesters.OrderByDescending(s => s.Year).ThenByDescending(s => s.Term == "Fall" ? 3 : s.Term == "Summer" ? 2 : 1)
                .Select(s => new
                {
                    s.Id, s.Name, s.Term, s.Year, s.IsCurrent, s.PublishedAt,
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
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(Dto.Of(semester));
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

        // ---- Upload: the same file is sent to preview, then again to apply ----

        admin.MapPost("/semesters/{id:int}/upload/preview", async (int id, HttpRequest req, AppDb db, CancellationToken ct) =>
        {
            if (!await db.Semesters.AnyAsync(s => s.Id == id, ct)) return Errors.NotFound("That semester doesn't exist.");
            var (parsed, error) = await ReadUpload(req, ct);
            if (error is not null) return error;
            return Results.Ok(await ScheduleImport.PreviewAsync(db, id, parsed!, ct));
        });

        admin.MapPost("/semesters/{id:int}/upload/apply", async (int id, HttpRequest req, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var semester = await db.Semesters.FindAsync([id], ct);
            if (semester is null) return Errors.NotFound("That semester doesn't exist.");
            var (parsed, error) = await ReadUpload(req, ct);
            if (error is not null) return error;
            if (parsed!.Errors.Count > 0) return Errors.BadRequest($"Fix the {parsed.Errors.Count} problem row{(parsed.Errors.Count == 1 ? "" : "s")} first.");
            if (parsed.Courses.Count == 0) return Errors.BadRequest("The file has no courses.");
            var preview = await ScheduleImport.PreviewAsync(db, id, parsed, ct);
            var now = clock.GetUtcNow();
            await ScheduleImport.ApplyAsync(db, semester, parsed, now, ct);
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
                enrollment = dto.Sections.ToDictionary(s => s.Id.ToString(CultureInfo.InvariantCulture), s => counts.GetValueOrDefault(s.Id)),
                noSection,
            });
        });

        admin.MapPut("/sections/{id:int}", async (int id, SectionBody body, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var section = await db.Sections.Include(s => s.Meetings).Include(s => s.Course).ThenInclude(c => c.Semester)
                .FirstOrDefaultAsync(s => s.Id == id, ct);
            if (section is null) return Errors.NotFound("That section doesn't exist.");
            var (meetings, problem) = ParseMeetings(body.Meetings);
            if (problem is not null) return Errors.BadRequest(problem);
            var instructor = body.Instructor?.Trim() ?? "";
            var change = ScheduleImport.DescribeChange(section.Instructor, section.Meetings, instructor, meetings!.Select(m => (m.Day, m.Start, m.End, m.Room)));
            if (change is null) return Results.Ok(Dto.Of(section));
            section.Instructor = instructor;
            db.Meetings.RemoveRange(section.Meetings);
            section.Meetings = meetings!;
            // Students only get the "Changed" badge for a published semester.
            if (section.Course.Semester.IsCurrent) { section.ChangedAt = clock.GetUtcNow(); section.ChangeSummary = change; }
            await db.SaveChangesAsync(ct);
            return Results.Ok(Dto.Of(section));
        });

        // ---- Majors and allowed electives ----

        admin.MapGet("/majors", async (AppDb db, CancellationToken ct) =>
        {
            var current = await db.Semesters.Where(s => s.IsCurrent).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
            return Results.Ok(await db.Majors.OrderBy(m => m.Name).Select(m => new
            {
                m.Id, m.Code, m.Name,
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
            var major = new Major { Code = code!, Name = name! };
            db.Majors.Add(major);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { major.Id, major.Code, major.Name });
        });

        admin.MapPut("/majors/{id:int}", async (int id, MajorBody body, AppDb db, CancellationToken ct) =>
        {
            var major = await db.Majors.FindAsync([id], ct);
            if (major is null) return Errors.NotFound("That major doesn't exist.");
            var (code, name, problem) = CleanMajor(body);
            if (problem is not null) return Errors.BadRequest(problem);
            if (await db.Majors.AnyAsync(m => m.Code == code && m.Id != id, ct)) return Errors.Conflict($"A major with code {code} already exists.");
            major.Code = code!;
            major.Name = name!;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { major.Id, major.Code, major.Name });
        });

        admin.MapDelete("/majors/{id:int}", async (int id, AppDb db, CancellationToken ct) =>
        {
            var major = await db.Majors.FindAsync([id], ct);
            if (major is null) return Errors.NotFound("That major doesn't exist.");
            if (await db.CourseMajors.AnyAsync(cm => cm.MajorId == id, ct))
                return Errors.Conflict("Courses in an uploaded schedule use this major. Remove it from the schedule first.");
            if (await db.Students.AnyAsync(s => s.MajorId == id, ct))
                return Errors.Conflict("Students have picked this major.");
            db.Majors.Remove(major);
            await db.SaveChangesAsync(ct);
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
            return Results.Ok(new
            {
                major = new { major.Id, major.Code, major.Name },
                semesterId = sid,
                electives = electives.Select(c => new
                {
                    c.Code, c.Name,
                    // "upload": the schedule lists this major; "open": no majors listed, so anyone may take it.
                    source = c.majors.Contains(id) ? "upload" : c.majors.Count == 0 ? "open" : extra.Contains(c.Code) ? "allowed" : "no",
                }),
                // Allowed codes not offered this semester stay allowed for later semesters.
                otherAllowed = extra.Where(code => electives.All(c => !c.Code.Equals(code, StringComparison.OrdinalIgnoreCase))).Order().ToList(),
            });
        });

        admin.MapPut("/majors/{id:int}/electives", async (int id, ElectivesBody body, AppDb db, CancellationToken ct) =>
        {
            var major = await db.Majors.Include(m => m.Electives).FirstOrDefaultAsync(m => m.Id == id, ct);
            if (major is null) return Errors.NotFound("That major doesn't exist.");
            var codes = (body.Codes ?? []).Select(c => c.Trim().ToUpperInvariant().Replace(" ", "")).Where(c => c.Length > 0)
                .Distinct().ToList();
            if (codes.Count > 500) return Errors.BadRequest("Too many courses.");
            major.Electives.RemoveAll(e => !codes.Contains(e.CourseCode, StringComparer.OrdinalIgnoreCase));
            foreach (var code in codes.Where(c => !major.Electives.Any(e => e.CourseCode.Equals(c, StringComparison.OrdinalIgnoreCase))))
                major.Electives.Add(new MajorElective { MajorId = id, CourseCode = code });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Stats (counts only) ----

        admin.MapGet("/stats", async (int? semesterId, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var current = await db.Semesters.Where(s => s.IsCurrent).FirstOrDefaultAsync(ct);
            var sid = semesterId ?? current?.Id;
            var currentId = current?.Id;
            var weekAgo = clock.GetUtcNow().AddDays(-7);
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
                },
                recentUploads = uploads.Select(u => new { at = u.At, u.FileName, u.By, u.Added, u.Updated, u.Removed }),
            });
        });
    }

    static (string? Code, string? Name, string? Problem) CleanMajor(MajorBody body)
    {
        var code = body.Code?.Trim().ToUpperInvariant().Replace(" ", "");
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 16) return (null, null, "Enter a short code like CS (up to 16 characters).");
        if (string.IsNullOrEmpty(name) || name.Length > 100) return (null, null, "Enter the major's name.");
        return (code, name, null);
    }

    public static (List<Meeting>? Meetings, string? Problem) ParseMeetings(List<MeetingBody>? input)
    {
        if (input is null || input.Count == 0) return (null, "Add at least one meeting time.");
        if (input.Count > 14) return (null, "Too many meetings.");
        var list = new List<Meeting>();
        foreach (var m in input)
        {
            if (m.Day is < 0 or > 6) return (null, "Pick a day for every meeting.");
            if (!TryTime(m.Start, out var start) || !TryTime(m.End, out var end)) return (null, "Enter times like 08:00.");
            if (end <= start) return (null, $"On {Timeslots.DayNames[m.Day]} the class ends before it starts.");
            var room = m.Room?.Trim() ?? "";
            if (room.Length > 60) return (null, "The room name is too long.");
            var meeting = new Meeting { Day = m.Day, Start = start, End = end, Room = room };
            if (list.Any(x => Clashes.Overlap(x, meeting))) return (null, $"Two meetings overlap on {Timeslots.DayNames[m.Day]}.");
            list.Add(meeting);
        }
        return (list, null);
    }

    static bool TryTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    /// <summary>Reads a multipart "file" (.xlsx, .csv, .tsv) or a "text" field pasted from Excel.</summary>
    static async Task<(ParsedSchedule? Parsed, IResult? Error)> ReadUpload(HttpRequest req, CancellationToken ct)
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
        return (ScheduleImport.Parse(read.Rows), null);
    }
}
