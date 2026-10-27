using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Admin;

/// <summary>A problem reaching or reading an outside API, worded for the dashboard.</summary>
public sealed class ConnectionException(string message) : Exception(message);

/// <summary>Fetches an outside API's reply. Tests swap it for canned replies.</summary>
public interface IConnectionFetcher
{
    Task<string> GetAsync(Uri url, string headerName, string headerValue, CancellationToken ct);
}

public sealed class HttpConnectionFetcher(HttpClient http) : IConnectionFetcher
{
    public const int MaxBytes = 20 * 1024 * 1024;

    public async Task<string> GetAsync(Uri url, string headerName, string headerValue, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        if (headerName.Length > 0 && !req.Headers.TryAddWithoutValidation(headerName, headerValue))
            throw new ConnectionException($"The header {headerName} can't be sent.");
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) { throw new ConnectionException($"Couldn't reach the API ({e.Message})."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ConnectionException("The API took longer than 30 seconds to answer."); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw new ConnectionException($"The API answered {(int)res.StatusCode} {res.ReasonPhrase}.");
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > MaxBytes) throw new ConnectionException("The API's reply is bigger than 20 MB.");
                buffer.Write(chunk, 0, n);
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}

/// <summary>
/// Reads any JSON API into Classy's rows. The list is found by a path ("data.items"; "*" steps into every element of a
/// list, so "courses.*.sections" reaches nested lists). Each field is a path inside an item; a field the item doesn't
/// have is looked up in the items around it (a class's code from its course), nearest first.
/// </summary>
public static class Connections
{
    public const string Timetable = "timetable";
    public const string StudentCourses = "student-courses";
    public static readonly string[] Kinds = [Timetable, StudentCourses];

    public static readonly string[] TimetableFields = ["code", "name", "section", "instructor", "date", "days", "start", "end", "time", "room"];
    public static readonly string[] StudentFields = ["code", "section"];
    public static string[] FieldsOf(string kind) => kind == Timetable ? TimetableFields : StudentFields;

    static IDataProtector Protector(IDataProtectionProvider p) => p.CreateProtector("Classy.Connections.v1");
    public static string Protect(IDataProtectionProvider p, string value) => value.Length == 0 ? "" : Protector(p).Protect(value);
    public static string Unprotect(IDataProtectionProvider p, string value) => value.Length == 0 ? "" : Protector(p).Unprotect(value);

    public static Dictionary<string, string> Fields(Connection c) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(c.FieldsJson) ?? [];

    /// <summary>The URL with {studentId} and {semester} filled in; https only (http just for localhost, when testing).</summary>
    public static Uri? Address(string template, string studentId = "24-0161", string semester = "Fall 2026")
    {
        var url = template.Replace("{studentId}", Uri.EscapeDataString(studentId), StringComparison.OrdinalIgnoreCase)
            .Replace("{semester}", Uri.EscapeDataString(semester), StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
    }

    static JsonElement? Step(JsonElement e, string part)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
                if (string.Equals(p.Name, part, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return null;
        }
        if (e.ValueKind == JsonValueKind.Array && int.TryParse(part, out var i) && i >= 0 && i < e.GetArrayLength()) return e[i];
        return null;
    }

    static JsonElement? At(JsonElement e, string path)
    {
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Step(e, part) is not { } next) return null;
            e = next;
        }
        return e;
    }

    static string Text(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        // ["Sun", "Tue"] → "Sun/Tue"
        JsonValueKind.Array => string.Join("/", e.EnumerateArray().Select(Text).Where(s => s.Length > 0)),
        _ => "",
    };

    static void Walk(JsonElement e, string[] parts, int i, List<JsonElement> up, List<(JsonElement, List<JsonElement>)> output, int max)
    {
        if (output.Count > max) return;
        if (i == parts.Length)
        {
            if (e.ValueKind == JsonValueKind.Array)
                foreach (var item in e.EnumerateArray()) output.Add((item, up));
            else output.Add((e, up));
            return;
        }
        if (parts[i] == "*")
        {
            if (e.ValueKind != JsonValueKind.Array) return;
            foreach (var item in e.EnumerateArray()) Walk(item, parts, i + 1, [item, .. up], output, max);
        }
        else if (Step(e, parts[i]) is { } next) Walk(next, parts, i + 1, up, output, max);
    }

    /// <summary>One dictionary per item: Classy field → text. Throws when the reply isn't JSON or the list isn't there.</summary>
    public static List<Dictionary<string, string>> Extract(string json, string listPath, Dictionary<string, string> fields, int max = SyncEndpoints.MaxRows)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw new ConnectionException("The API's reply isn't JSON."); }
        using (doc)
        {
            var parts = listPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var items = new List<(JsonElement Item, List<JsonElement> Up)>();
            Walk(doc.RootElement, parts, 0, [], items, max);
            if (items.Count == 0) throw new ConnectionException(listPath.Length == 0 ? "The reply has no list of items." : $"Nothing found at \"{listPath}\" in the reply.");
            if (items.Count > max) throw new ConnectionException($"The reply has more than {max:N0} items.");
            return items.Select(x =>
            {
                var row = new Dictionary<string, string>();
                foreach (var (field, path) in fields)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    JsonElement? value = At(x.Item, path);
                    foreach (var parent in x.Up)
                    {
                        if (value is not null) break;
                        value = At(parent, path);
                    }
                    row[field] = value is { } v ? Text(v).Trim() : "";
                }
                return row;
            }).ToList();
        }
    }

    static string F(Dictionary<string, string> r, string key) => r.TryGetValue(key, out var v) ? v : "";

    public static SyncEndpoints.SyncRow ToSyncRow(Dictionary<string, string> r) =>
        new(F(r, "code"), F(r, "name"), F(r, "section"), F(r, "instructor"), F(r, "date"), F(r, "start"), F(r, "end"), F(r, "room"), F(r, "days"), F(r, "time"));

    static async Task<string> Fetch(Connection c, Uri url, IConnectionFetcher fetcher, IDataProtectionProvider protection, CancellationToken ct)
    {
        string secret;
        try { secret = Unprotect(protection, c.HeaderSecret); }
        catch (System.Security.Cryptography.CryptographicException) { throw new ConnectionException("The saved header value can't be read any more. Enter it again."); }
        return await fetcher.GetAsync(url, c.HeaderName, secret, ct);
    }

    public static async Task<List<SyncEndpoints.SyncRow>> FetchTimetable(Connection c, IConnectionFetcher fetcher, IDataProtectionProvider protection, string semester, CancellationToken ct)
    {
        var url = Address(c.Url, semester: semester) ?? throw new ConnectionException("Set an https address first.");
        return Extract(await Fetch(c, url, fetcher, protection, ct), c.ListPath, Fields(c)).Select(ToSyncRow).ToList();
    }

    public static async Task<List<(string Code, string Section)>> FetchStudent(Connection c, IConnectionFetcher fetcher, IDataProtectionProvider protection,
        string universityId, string semester, CancellationToken ct)
    {
        var url = Address(c.Url, universityId, semester) ?? throw new ConnectionException("Set an https address with {studentId} first.");
        return Extract(await Fetch(c, url, fetcher, protection, ct), c.ListPath, Fields(c), 200)
            .Select(r => (F(r, "code"), F(r, "section"))).Where(x => x.Item1.Length > 0).ToList();
    }

    /// <summary>The courses GUtech says this student takes: from the student-courses API when it's on, else from pushed enrollments.</summary>
    public static async Task<List<(string Code, string Section)>> StudentCoursesFor(AppDb db, IConnectionFetcher fetcher, IDataProtectionProvider protection,
        Semester semester, string universityId, CancellationToken ct)
    {
        var c = await db.Connections.FirstOrDefaultAsync(x => x.Kind == StudentCourses && x.Enabled, ct);
        if (c is not null) return await FetchStudent(c, fetcher, protection, universityId, semester.Name, ct);
        return (await db.Enrollments.Where(e => e.SemesterId == semester.Id && e.UniversityId == universityId).ToListAsync(ct))
            .Select(e => (e.CourseCode, e.Section)).ToList();
    }

    public static async Task<bool> CanImport(AppDb db, Semester? semester, string universityId, CancellationToken ct) =>
        semester is not null && (await db.Connections.AnyAsync(x => x.Kind == StudentCourses && x.Enabled, ct)
            || await db.Enrollments.AnyAsync(e => e.SemesterId == semester.Id && e.UniversityId == universityId, ct));

    /// <summary>Fetches the timetable API and keeps it as a pending sync; records the outcome on the connection.</summary>
    public static async Task<(SyncEndpoints.Kept? Kept, string? Problem)> RunTimetable(AppDb db, IConnectionFetcher fetcher, IDataProtectionProvider protection,
        Semester semester, DateTimeOffset now, CancellationToken ct)
    {
        var c = await db.Connections.FirstOrDefaultAsync(x => x.Kind == Timetable, ct);
        if (c is null || c.Url.Length == 0) return (null, "Set up the timetable API first.");
        string? problem = null;
        SyncEndpoints.Kept? kept = null;
        try
        {
            var rows = await FetchTimetable(c, fetcher, protection, semester.Name, ct);
            (kept, problem) = await SyncEndpoints.KeepAsync(db, semester, rows, [], "Timetable API", now, "Check the API and its field mapping.", ct);
        }
        catch (ConnectionException e) { problem = e.Message; }
        c.LastRunAt = now;
        c.LastResult = problem ?? $"{kept!.Courses} courses, {kept.Classes:N0} classes: {kept.Added} added, {kept.Updated} changed, {kept.Removed} removed. Waiting to be applied.";
        await db.SaveChangesAsync(ct);
        return (kept, problem);
    }

    /// <summary>
    /// Replaces this semester's picks with the courses GUtech lists. Codes match whatever the spacing; sections match
    /// by number, and a course whose section isn't found is added without one so the student picks it.
    /// </summary>
    public static async Task<(int Imported, List<string> NotFound, List<string> NoSection)> ImportPicks(AppDb db, Student s, Semester semester,
        List<(string Code, string Section)> courses, CancellationToken ct)
    {
        var all = await db.Courses.Where(c => c.SemesterId == semester.Id).Include(c => c.Sections).ToListAsync(ct);
        var byKey = all.GroupBy(c => SyncEndpoints.CodeKey(c.Code)).ToDictionary(g => g.Key, g => g.First());
        var notFound = new List<string>();
        var noSection = new List<string>();
        var picks = new Dictionary<int, int?>();
        foreach (var (code, section) in courses)
        {
            if (!byKey.TryGetValue(SyncEndpoints.CodeKey(code), out var course)) { notFound.Add(code); continue; }
            var wanted = ScheduleImport.NormalizeSection(section);
            var match = course.Sections.FirstOrDefault(x => x.Number == wanted) ?? (course.Sections.Count == 1 ? course.Sections[0] : null);
            if (match is null) noSection.Add(course.Code);
            picks[course.Id] = match?.Id;
        }
        if (picks.Count == 0) return (0, notFound, noSection);
        var old = await db.Picks.Where(p => p.StudentId == s.Id && p.Course.SemesterId == semester.Id).ToListAsync(ct);
        db.Picks.RemoveRange(old);
        foreach (var (courseId, sectionId) in picks) db.Picks.Add(new Pick { StudentId = s.Id, CourseId = courseId, SectionId = sectionId });
        await db.SaveChangesAsync(ct);
        return (picks.Count, notFound, noSection);
    }
}

/// <summary>Fetches the timetable API every morning (from 06:00 Oman time) when an owner has turned that on.</summary>
public sealed class ConnectionScheduler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ConnectionScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var c = await db.Connections.FirstOrDefaultAsync(x => x.Kind == Connections.Timetable && x.Enabled && x.AutoDaily, stop);
                var now = clock.GetUtcNow();
                var oman = now.ToOffset(TimeSpan.FromHours(4));
                if (c is not null && oman.Hour >= 6 && (c.LastRunAt is null || c.LastRunAt.Value.ToOffset(TimeSpan.FromHours(4)).Date < oman.Date)
                    && await StudentEndpoints.CurrentSemester(db, stop) is { } semester)
                {
                    var (_, problem) = await Connections.RunTimetable(db, scope.ServiceProvider.GetRequiredService<IConnectionFetcher>(),
                        scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>(), semester, now, stop);
                    log.LogInformation("Morning timetable fetch: {Result}", problem ?? "kept as a pending sync");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Morning timetable fetch failed"); }
            await Task.Delay(TimeSpan.FromMinutes(15), clock, stop);
        }
    }
}
