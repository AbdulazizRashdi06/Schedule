using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Admin;

/// <summary>
/// Outside APIs: owners point Classy at a timetable API and a student-courses API and map their fields;
/// GUtech can also push enrollments with a sync token; students import their courses with one press.
/// </summary>
public static partial class ConnectionEndpoints
{
    public const int MaxEnrollments = 50_000;

    /// <summary><paramref name="HeaderValue"/>: null keeps the saved one; <paramref name="ClearSecret"/> removes it.</summary>
    public record ConnectionBody(string? Url, string? HeaderName, string? HeaderValue, bool? ClearSecret, string? ListPath,
        Dictionary<string, string>? Fields, bool Enabled, bool AutoDaily);
    public record RunBody(int? SemesterId);
    public record TestStudentBody(string? UniversityId);
    public record EnrollCourse(string? Code, string? Section);
    public record EnrollStudent(string? UniversityId, List<EnrollCourse>? Courses);
    public record EnrollmentsBody(int? SemesterId, List<EnrollStudent>? Students);

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,100}$")]
    private static partial Regex HeaderNamePattern();

    static object Of(string kind, Connection? c) => new
    {
        kind,
        url = c?.Url ?? "",
        headerName = c?.HeaderName ?? "",
        hasSecret = (c?.HeaderSecret ?? "").Length > 0,
        listPath = c?.ListPath ?? "",
        fields = c is null ? [] : Connections.Fields(c),
        enabled = c?.Enabled ?? false,
        autoDaily = c?.AutoDaily ?? false,
        lastRunAt = c?.LastRunAt,
        lastResult = c?.LastResult,
        updatedBy = c?.UpdatedBy,
        updatedAt = c?.UpdatedAt,
    };

    static string? Check(string kind, ConnectionBody b)
    {
        var url = b.Url?.Trim() ?? "";
        if (url.Length > 500) return "The address is too long.";
        if (url.Length > 0 && Connections.Address(url) is null) return "Enter a full https address, like https://api.example.com/timetable.";
        if (kind == Connections.StudentCourses && url.Length > 0 && !url.Contains("{studentId}", StringComparison.OrdinalIgnoreCase))
            return "Put {studentId} in the address where the student's ID goes.";
        var header = b.HeaderName?.Trim() ?? "";
        if (header.Length > 0 && !HeaderNamePattern().IsMatch(header)) return "A header name is letters, digits and dashes, like X-Api-Key.";
        if (b.HeaderValue?.Length > 4000) return "The header value is too long.";
        if (b.ListPath?.Length > 200) return "The list path is too long.";
        var fields = b.Fields ?? [];
        var allowed = Connections.FieldsOf(kind);
        if (fields.Keys.FirstOrDefault(k => !allowed.Contains(k)) is { } odd) return $"Classy has no field called {odd}.";
        if (fields.Values.Any(v => v?.Length > 200)) return "A field path is too long.";
        if (!b.Enabled) return null;
        bool Has(string f) => fields.TryGetValue(f, out var v) && !string.IsNullOrWhiteSpace(v);
        if (url.Length == 0) return "Enter the API's address before turning it on.";
        if (!Has("code")) return "Map the course code before turning it on.";
        if (kind == Connections.Timetable)
        {
            if (!Has("section")) return "Map the section before turning it on.";
            if (!Has("date") && !Has("days")) return "Map the class date, or the weekdays, before turning it on.";
            if (!Has("time") && !(Has("start") && Has("end"))) return "Map the start and end times, or one time range, before turning it on.";
        }
        return null;
    }

    public static void MapConnections(this RouteGroupBuilder api)
    {
        // ---- Owners: set up, test and run ----
        var owner = api.MapGroup("/admin/connections").RequireAuthorization(Session.OwnerPolicy);

        owner.MapGet("", async (AppDb db, CancellationToken ct) =>
        {
            var saved = await db.Connections.ToListAsync(ct);
            return Results.Ok(Connections.Kinds.Select(k => Of(k, saved.FirstOrDefault(c => c.Kind == k))));
        });

        owner.MapPut("/{kind}", async (string kind, ConnectionBody body, HttpContext http, AppDb db, IDataProtectionProvider protection,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (!Connections.Kinds.Contains(kind)) return Errors.NotFound("Classy has no connection called that.");
            if (Check(kind, body) is { } problem) return Errors.BadRequest(problem);
            var c = await db.Connections.FirstOrDefaultAsync(x => x.Kind == kind, ct);
            if (c is null) db.Connections.Add(c = new Connection { Kind = kind });
            c.Url = body.Url?.Trim() ?? "";
            c.HeaderName = body.HeaderName?.Trim() ?? "";
            if (body.ClearSecret == true || c.HeaderName.Length == 0) c.HeaderSecret = "";
            else if (body.HeaderValue is not null) c.HeaderSecret = Connections.Protect(protection, body.HeaderValue);
            c.ListPath = body.ListPath?.Trim() ?? "";
            c.FieldsJson = JsonSerializer.Serialize((body.Fields ?? [])
                .Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToDictionary(f => f.Key, f => f.Value.Trim()));
            c.Enabled = body.Enabled;
            c.AutoDaily = kind == Connections.Timetable && body.AutoDaily;
            c.UpdatedBy = await OwnerEndpoints.ActorName(http, db, ct);
            c.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.Ok(Of(kind, c));
        });

        // Fetch without keeping anything: how many items, and the first few as Classy reads them.
        owner.MapPost("/timetable/test", async (RunBody body, AppDb db, IConnectionFetcher fetcher, IDataProtectionProvider protection, CancellationToken ct) =>
        {
            var c = await db.Connections.FirstOrDefaultAsync(x => x.Kind == Connections.Timetable, ct);
            if (c is null || c.Url.Length == 0) return Errors.BadRequest("Save the timetable API's address first.");
            var semester = await SyncEndpoints.TargetSemester(db, body.SemesterId, ct);
            try
            {
                var rows = await Connections.FetchTimetable(c, fetcher, protection, semester?.Name ?? "", ct);
                return Results.Ok(new
                {
                    count = rows.Count,
                    courses = rows.Select(r => SyncEndpoints.CodeKey(r.Code ?? "")).Where(k => k.Length > 0).Distinct().Count(),
                    noCode = rows.Count(r => string.IsNullOrWhiteSpace(r.Code)),
                    noSection = rows.Count(r => string.IsNullOrWhiteSpace(r.Section)),
                    noWhen = rows.Count(r => string.IsNullOrWhiteSpace(r.Date) && string.IsNullOrWhiteSpace(r.Days)),
                    sample = rows.Take(5),
                });
            }
            catch (ConnectionException e) { return Errors.BadRequest(e.Message); }
        });

        // Fetch now and keep it as a pending sync, applied from Upload like the others.
        owner.MapPost("/timetable/run", async (RunBody body, AppDb db, IConnectionFetcher fetcher, IDataProtectionProvider protection,
            TimeProvider clock, CancellationToken ct) =>
        {
            var semester = await SyncEndpoints.TargetSemester(db, body.SemesterId, ct);
            if (semester is null) return Errors.NotFound("There's no semester to fill.");
            var (kept, problem) = await Connections.RunTimetable(db, fetcher, protection, semester, clock.GetUtcNow(), ct);
            return problem is null ? Results.Ok(kept) : Errors.BadRequest(problem);
        });

        owner.MapPost("/student-courses/test", async (TestStudentBody body, AppDb db, IConnectionFetcher fetcher, IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var id = StudentIds.Normalize(body.UniversityId);
            if (id is null) return Errors.BadRequest("Enter a university ID like 24-0161.");
            var c = await db.Connections.FirstOrDefaultAsync(x => x.Kind == Connections.StudentCourses, ct);
            if (c is null || c.Url.Length == 0) return Errors.BadRequest("Save the student-courses API's address first.");
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            try
            {
                var courses = await Connections.FetchStudent(c, fetcher, protection, id, semester?.Name ?? "", ct);
                var known = semester is null ? [] : (await db.Courses.Where(x => x.SemesterId == semester.Id).Select(x => x.Code).ToListAsync(ct))
                    .Select(SyncEndpoints.CodeKey).ToHashSet();
                return Results.Ok(courses.Select(x => new { code = x.Code, section = x.Section, inClassy = known.Contains(SyncEndpoints.CodeKey(x.Code)) }));
            }
            catch (ConnectionException e) { return Errors.BadRequest(e.Message); }
        });

        // ---- GUtech pushing enrollments, with a sync token ----
        api.MapPost("/sync/enrollments", async (EnrollmentsBody body, HttpRequest req, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var token = await SyncEndpoints.Token(req, db, ct);
            if (token is null) return Errors.Unauthorized("Send a valid sync token.");
            var semester = await SyncEndpoints.TargetSemester(db, body.SemesterId, ct);
            if (semester is null) return Errors.NotFound("That semester doesn't exist.");
            var students = body.Students ?? [];
            if (students.Sum(s => s.Courses?.Count ?? 0) > MaxEnrollments) return Errors.BadRequest("Too many enrollments in one request.");
            var now = clock.GetUtcNow();
            var byId = new Dictionary<string, List<EnrollCourse>>();
            foreach (var s in students)
            {
                var id = StudentIds.Normalize(s.UniversityId);
                if (id is null) return Errors.BadRequest($"\"{s.UniversityId}\" isn't a university ID like 24-0161.");
                byId[id] = (s.Courses ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Code)).ToList();
            }
            var ids = byId.Keys.ToList();
            await db.Enrollments.Where(e => e.SemesterId == semester.Id && ids.Contains(e.UniversityId)).ExecuteDeleteAsync(ct);
            foreach (var (id, courses) in byId)
            foreach (var c in courses)
                db.Enrollments.Add(new Enrollment
                {
                    SemesterId = semester.Id, UniversityId = id, CourseCode = c.Code!.Trim()[..Math.Min(c.Code.Trim().Length, 40)],
                    Section = (c.Section ?? "").Trim()[..Math.Min((c.Section ?? "").Trim().Length, 20)], ReceivedAt = now,
                });
            token.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { students = byId.Count, courses = byId.Values.Sum(v => v.Count) });
        });

        // ---- Students: "Get my courses from GUtech" ----
        api.MapPost("/me/import-courses", async (HttpContext http, AppDb db, IConnectionFetcher fetcher, IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var s = await db.Students.FirstAsync(x => x.Id == http.User.Id(), ct);
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            if (semester is null) return Errors.Conflict("No semester has been published yet.");
            List<(string Code, string Section)> courses;
            try { courses = await Connections.StudentCoursesFor(db, fetcher, protection, semester, s.UniversityId, ct); }
            catch (ConnectionException) { return Errors.Unavailable("GUtech's system didn't answer. Try again later, or pick your courses yourself."); }
            if (courses.Count == 0) return Errors.NotFound("GUtech has no courses listed for you yet. Pick them yourself for now.");
            var (imported, notFound, noSection) = await Connections.ImportPicks(db, s, semester, courses, ct);
            if (imported == 0) return Errors.NotFound("None of your GUtech courses are in Classy yet. Pick them yourself for now.");
            return Results.Ok(new { imported, notFound, noSection });
        }).RequireAuthorization(Session.StudentPolicy);
    }
}
