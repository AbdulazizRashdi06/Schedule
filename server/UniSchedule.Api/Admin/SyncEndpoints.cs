using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Admin;

/// <summary>
/// The Classy Sync browser extension fetches the timetable from EduWave in the owner's signed-in browser and sends it here
/// with a sync token. It's kept as a pending sync that staff preview and apply, the same as an upload.
/// </summary>
public static partial class SyncEndpoints
{
    public const int MaxRows = 20_000;
    public const string TokenPrefix = "csync_";

    public record TokenBody(string? Name);
    public record SyncRow(string? Code, string? Name, string? Section, string? Instructor, string? Date, string? Start, string? End, string? Room);
    public record SyncBody(int? SemesterId, List<SyncRow>? Rows, List<string>? Failed);
    public record ApplyBody(int Id);

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    static string Key(string code) => string.Concat(code.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    [GeneratedRegex(@"^([A-Z]+(?:-[A-Z]+)?)(\d+)(-[A-Z]+)?$")]
    private static partial Regex CodeParts();

    /// <summary>"CS1107" → "CS 1 107", "IBSM-FE340" → "IBSM-FE 340", "CS1102-L" → "CS 1 102-L": how EduWave writes codes.</summary>
    public static string EduWaveCode(string code)
    {
        var m = CodeParts().Match(Key(code));
        if (!m.Success) return code;
        var (prefix, digits, suffix) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        return (digits.Length == 4 ? $"{prefix} {digits[0]} {digits[1..]}" : $"{prefix} {digits}") + suffix;
    }

    /// <summary>
    /// Codes to fetch even when the semester doesn't have them yet (Sync:ExtraCodes, comma-separated), such as lab
    /// courses EduWave lists separately. After the first sync they're ordinary courses of the semester.
    /// </summary>
    public static List<string> ExtraCodes(IConfiguration config) =>
        (config["Sync:ExtraCodes"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    static async Task<SyncToken?> TokenOf(HttpRequest req, AppDb db, CancellationToken ct)
    {
        var header = req.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = header["Bearer ".Length..].Trim();
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(token);
        return await db.SyncTokens.FirstOrDefaultAsync(t => t.Hash == hash, ct);
    }

    static async Task<Semester?> SemesterOf(AppDb db, int? id, CancellationToken ct) =>
        id is not null
            ? await db.Semesters.FirstOrDefaultAsync(s => s.Id == id, ct)
            : await db.Semesters.Where(s => s.IsCurrent).FirstOrDefaultAsync(ct)
              ?? await db.Semesters.OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Turns what EduWave sent into upload rows. Names, majors, types, years and credits come from the courses
    /// already in the semester; new codes get the usual rules. Courses that failed keep their current classes.
    /// </summary>
    public static async Task<List<ScheduleRow>> BuildRows(AppDb db, int semesterId, List<SyncRow> rows, IEnumerable<string> failed, CancellationToken ct)
    {
        var existing = (await db.Courses.Where(c => c.SemesterId == semesterId)
                .Include(c => c.Majors).ThenInclude(m => m.Major)
                .Include(c => c.Sections).ThenInclude(s => s.Meetings)
                .AsSplitQuery().ToListAsync(ct))
            .GroupBy(c => Key(c.Code)).ToDictionary(g => g.Key, g => g.First());

        var output = new List<ScheduleRow>();
        string Year(int? y) => y?.ToString() ?? "";
        foreach (var r in rows)
        {
            var key = Key(r.Code ?? "");
            string code, name, majors, type, year, credits;
            if (existing.TryGetValue(key, out var c))
                (code, name, majors, type, year, credits) = (c.Code, c.Name, string.Join(", ", c.Majors.Select(m => m.Major.Code)), c.Type.ToString(), Year(c.Year), c.Credits?.ToString() ?? "");
            else if (Labs.ParentKey(key) is { } pk && existing.TryGetValue(pk, out var parent))
                // A new lab takes its course's details.
                (code, name, majors, type, year, credits) = (key, $"{parent.Name} Lab", string.Join(", ", parent.Majors.Select(m => m.Major.Code)), parent.Type.ToString(), Year(parent.Year), "");
            else
            {
                var m = CodeParts().Match(key);
                var prefix = m.Success ? m.Groups[1].Value : key;
                var kind = prefix == "LS" ? "LS" : prefix.Contains('-') ? "Elective" : "Core";
                code = key;
                name = r.Name?.Trim() is { Length: > 0 } n ? n : key;
                majors = kind == "Core" ? prefix : "";
                type = kind;
                year = kind == "Core" && m.Success ? m.Groups[2].Value[..1] : "";
                credits = "";
            }
            output.Add(new ScheduleRow(output.Count + 2, code, name, majors, type, year, credits,
                (r.Section ?? "").Trim(), Clip(r.Instructor, 120), "", $"{r.Start?.Trim()}-{r.End?.Trim()}", Clip(r.Room, 60), (r.Date ?? "").Trim()));
        }
        foreach (var key in failed.Select(Key).Distinct())
        {
            if (!existing.TryGetValue(key, out var c)) continue;
            var majors = string.Join(", ", c.Majors.Select(m => m.Major.Code));
            foreach (var s in c.Sections)
            foreach (var m in s.Meetings)
                output.Add(new ScheduleRow(output.Count + 2, c.Code, c.Name, majors, c.Type.ToString(), Year(c.Year), c.Credits?.ToString() ?? "",
                    s.Number, s.Instructor, m.Date is null ? Timeslots.DayNames[m.Day] : "", $"{Timeslots.Format(m.Start)}-{Timeslots.Format(m.End)}", m.Room,
                    m.Date?.ToString("yyyy-MM-dd") ?? ""));
        }
        return output;
    }

    static string Clip(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length > max ? t[..max] : t;
    }

    public static void MapSync(this RouteGroupBuilder api)
    {
        // ---- Tokens: owners make one for the extension (it can do nothing but timetable syncs) ----
        var tokens = api.MapGroup("/admin/sync-tokens").RequireAuthorization(Session.OwnerPolicy);

        tokens.MapGet("", async (AppDb db, CancellationToken ct) =>
            await db.SyncTokens.OrderByDescending(t => t.Id).Select(t => new { t.Id, t.Name, t.CreatedBy, t.CreatedAt, t.LastUsedAt }).ToListAsync(ct));

        tokens.MapPost("", async (TokenBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var name = Clip(body.Name, 60);
            if (name.Length == 0) name = "Classy Sync";
            var token = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            db.SyncTokens.Add(new SyncToken { Name = name, Hash = Hash(token), CreatedBy = await OwnerEndpoints.ActorName(http, db, ct), CreatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { token });
        });

        tokens.MapDelete("/{id:int}", async (int id, AppDb db, CancellationToken ct) =>
            await db.SyncTokens.Where(t => t.Id == id).ExecuteDeleteAsync(ct) == 0 ? Errors.NotFound("That token doesn't exist.") : Results.NoContent());

        // ---- The extension, with its token ----
        var sync = api.MapGroup("/sync");

        // Which courses to fetch: every course in the semester, with EduWave's spelling of the code.
        sync.MapGet("/courses", async (int? semesterId, HttpRequest req, AppDb db, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            var token = await TokenOf(req, db, ct);
            if (token is null) return Errors.Unauthorized("Connect Classy Sync to Classy again.");
            var semester = await SemesterOf(db, semesterId, ct);
            if (semester is null) return Errors.NotFound("There's no semester to sync.");
            token.LastUsedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            var courses = await db.Courses.Where(c => c.SemesterId == semester.Id).OrderBy(c => c.Code).Select(c => new { c.Code, c.Name }).ToListAsync(ct);
            var known = courses.Select(c => Key(c.Code)).ToHashSet();
            var extra = ExtraCodes(config).Where(c => known.Add(Key(c))).Select(c => new { Code = Key(c), Name = "" });
            return Results.Ok(new
            {
                semester = new { semester.Id, semester.Name },
                courses = courses.Concat(extra).Select(c => new { c.Code, eduwave = EduWaveCode(c.Code), c.Name }),
            });
        });

        sync.MapPost("/timetable", async (SyncBody body, HttpRequest req, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var token = await TokenOf(req, db, ct);
            if (token is null) return Errors.Unauthorized("Connect Classy Sync to Classy again.");
            var semester = await SemesterOf(db, body.SemesterId, ct);
            if (semester is null) return Errors.NotFound("That semester doesn't exist.");
            var rows = body.Rows ?? [];
            var failed = body.Failed ?? [];
            if (rows.Count == 0) return Errors.BadRequest("Nothing came back from EduWave.");
            if (rows.Count > MaxRows) return Errors.BadRequest("Too many classes in one sync.");
            var known = await db.Courses.CountAsync(c => c.SemesterId == semester.Id, ct);
            if (failed.Count > Math.Max(3, known / 4)) return Errors.BadRequest($"{failed.Count} courses failed, so this sync wasn't kept. Sign in to EduWave and sync again.");

            var built = await BuildRows(db, semester.Id, rows, failed, ct);
            var parsed = ScheduleImport.Parse(built);
            var preview = await ScheduleImport.PreviewAsync(db, semester.Id, parsed, ct);
            // EduWave answering with error pages looks like courses with no sections: don't keep a sync that empties the semester.
            var gone = preview.Removed.Count(c => c.Kind == "course");
            if (gone > Math.Max(5, known / 4)) return Errors.BadRequest($"This sync would remove {gone} courses, so it wasn't kept. Open Advisor Plan in EduWave and sync again.");
            var now = clock.GetUtcNow();
            // Only the newest unapplied sync is kept.
            await db.PendingSyncs.Where(p => p.SemesterId == semester.Id && p.AppliedAt == null).ExecuteDeleteAsync(ct);
            var pending = new PendingSync
            {
                SemesterId = semester.Id, RowsJson = JsonSerializer.Serialize(built), Failed = string.Join(", ", failed.Take(300)),
                Courses = parsed.Courses.Count, Classes = built.Count, Source = token.Name, ReceivedAt = now,
            };
            db.PendingSyncs.Add(pending);
            token.LastUsedAt = now;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                pending.Id, semester = semester.Name, courses = parsed.Courses.Count, classes = built.Count,
                added = preview.Added.Count, updated = preview.Updated.Count, removed = preview.Removed.Count, errors = preview.Errors.Count,
            });
        });

        // ---- Staff: check the latest sync and apply it ----
        var admin = api.MapGroup("/admin/semesters/{id:int}/sync").RequireAuthorization(Session.StaffPolicy);

        admin.MapGet("", async (int id, AppDb db, CancellationToken ct) =>
        {
            var last = await db.PendingSyncs.Where(p => p.SemesterId == id).OrderByDescending(p => p.Id).FirstOrDefaultAsync(ct);
            if (last is null) return Results.Ok(new { pending = (object?)null, lastApplied = (DateTimeOffset?)null });
            var lastApplied = await db.PendingSyncs.Where(p => p.SemesterId == id && p.AppliedAt != null).MaxAsync(p => p.AppliedAt, ct);
            if (last.AppliedAt is not null) return Results.Ok(new { pending = (object?)null, lastApplied });
            var parsed = ScheduleImport.Parse(JsonSerializer.Deserialize<List<ScheduleRow>>(last.RowsJson) ?? []);
            var preview = await ScheduleImport.PreviewAsync(db, id, parsed, ct);
            return Results.Ok(new
            {
                pending = new { last.Id, last.ReceivedAt, last.Source, last.Courses, last.Classes, failed = last.Failed, preview },
                lastApplied,
            });
        });

        admin.MapPost("/apply", async (int id, ApplyBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var semester = await db.Semesters.FindAsync([id], ct);
            if (semester is null) return Errors.NotFound("That semester doesn't exist.");
            var pending = await db.PendingSyncs.FirstOrDefaultAsync(p => p.Id == body.Id && p.SemesterId == id, ct);
            if (pending is null) return Errors.NotFound("That sync has been replaced by a newer one. Reload.");
            if (pending.AppliedAt is not null) return Errors.Conflict("That sync is already applied.");
            var parsed = ScheduleImport.Parse(JsonSerializer.Deserialize<List<ScheduleRow>>(pending.RowsJson) ?? []);
            if (parsed.Errors.Count > 0) return Errors.BadRequest($"{parsed.Errors.Count} rows have problems, so it can't be applied.");
            if (parsed.Courses.Count == 0) return Errors.BadRequest("The sync has no courses.");
            var preview = await ScheduleImport.PreviewAsync(db, id, parsed, ct);
            var now = clock.GetUtcNow();
            await ScheduleImport.ApplyAsync(db, semester, parsed, now, ct);
            await Labs.AlignAsync(db, semester.Id, ct);
            var by = await OwnerEndpoints.ActorName(http, db, ct);
            pending.AppliedAt = now;
            pending.AppliedBy = by;
            db.UploadLogs.Add(new UploadLog
            {
                SemesterId = id, FileName = "EduWave sync", By = by, At = now,
                Added = preview.Added.Count, Updated = preview.Updated.Count, Removed = preview.Removed.Count,
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added = preview.Added.Count, updated = preview.Updated.Count, removed = preview.Removed.Count, preview.StudentsAffected });
        });
    }
}
