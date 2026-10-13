using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api;

/// <summary>Development only: loads sample-data/fall-2026.csv as the published semester and adds a dashboard login.</summary>
public static class DevSeed
{
    public const string AdminUsername = "dev-owner";
    public const string AdminPassword = "dev-password";

    public static async Task RunAsync(AppDb db, IHostEnvironment env, ILogger log)
    {
        if (!await db.AdminUsers.AnyAsync())
        {
            var user = new AdminUser { Username = AdminUsername, Role = AdminRole.Owner, MustChangePassword = false, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "seed" };
            user.PasswordHash = AuthEndpoints.HashPassword(user, AdminPassword);
            db.AdminUsers.Add(user);
            await db.SaveChangesAsync();
            log.LogWarning("Dev dashboard login: {User} / {Password}", AdminUsername, AdminPassword);
        }
        if (await db.Semesters.AnyAsync()) return;

        var file = FindSample(env.ContentRootPath);
        if (file is null) { log.LogWarning("sample-data/fall-2026.csv not found; starting with no semester."); return; }
        var read = ScheduleFile.ReadText(await File.ReadAllTextAsync(file));
        var parsed = ScheduleImport.Parse(read.Rows);
        if (parsed.Errors.Count > 0)
        {
            foreach (var e in parsed.Errors) log.LogError("Sample row {Line}: {Message}", e.Line, e.Message);
            return;
        }
        if (!await db.Majors.AnyAsync())
        {
            db.Majors.AddRange(
                new Major { Code = "CS", Name = "Computer Science" },
                new Major { Code = "PE", Name = "Process Engineering" },
                new Major { Code = "LOG", Name = "Logistics" },
                new Major { Code = "UPAD", Name = "Urban Planning and Architectural Design" });
            await db.SaveChangesAsync();
        }
        var now = DateTimeOffset.UtcNow;
        var semester = new Semester { Name = "Fall 2026", Term = "Fall", Year = 2026, CreatedAt = now };
        db.Semesters.Add(semester);
        await db.SaveChangesAsync();
        await ScheduleImport.ApplyAsync(db, semester, parsed, now, CancellationToken.None);
        semester.IsCurrent = true;
        semester.PublishedAt = now;
        await db.SaveChangesAsync();
        log.LogInformation("Seeded Fall 2026 with {Courses} courses from {File}", parsed.Courses.Count, file);
    }

    static string? FindSample(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "sample-data", "fall-2026.csv");
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
