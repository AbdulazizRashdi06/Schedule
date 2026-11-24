using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>The teachers migration only adds tables: a live database keeps every student, pick and device through it.</summary>
public sealed class MigrationTests : IDisposable
{
    static readonly string[] NewTables =
    [
        "BlockedEmails", "ClassChanges", "OfficeHours", "TeachNotices", "TeacherNames", "TeacherPushSubscriptions",
        "TeacherSections", "TeacherSeens", "TeacherSlots", "Teachers",
    ];

    readonly SqliteConnection conn = new("Data Source=:memory:");
    readonly AppDb db;

    public MigrationTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
    }

    public void Dispose()
    {
        db.Dispose();
        conn.Dispose();
    }

    /// <summary>Every table and index with the SQL that made it.</summary>
    Dictionary<string, string> Schema()
    {
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT type || ' ' || name, COALESCE(sql, '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
        using var reader = command.ExecuteReader();
        var schema = new Dictionary<string, string>();
        while (reader.Read()) schema[reader.GetString(0)] = reader.GetString(1);
        return schema;
    }

    [Fact]
    public void A_database_from_before_teachers_keeps_its_students_picks_and_devices()
    {
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate("S1AuthBinding");
        Assert.DoesNotContain("table Teachers", Schema().Keys);

        var semester = new Semester { Name = "Fall 2026", Term = "Fall", Year = 2026, IsCurrent = true, CreatedAt = DateTimeOffset.UtcNow };
        var student = new Student { UniversityId = "24-0161", Email = "24-0161@student.gutech.edu.om", CreatedAt = DateTimeOffset.UtcNow, RemindMinutes = 15 };
        db.AddRange(semester, student);
        db.SaveChanges();
        var course = new Course { SemesterId = semester.Id, Code = "CS201", Name = "Data Structures", Type = CourseType.Core, Year = 2 };
        course.Sections.Add(new Section { Number = "1", Instructor = "Karim Hadad", Meetings = [new() { Day = 0, Start = new(8, 0), End = new(9, 30), Room = "B105" }] });
        db.Courses.Add(course);
        db.SaveChanges();
        db.Picks.Add(new Pick { StudentId = student.Id, CourseId = course.Id, SectionId = course.Sections[0].Id });
        db.PushSubscriptions.Add(new PushSubscription { StudentId = student.Id, Endpoint = "https://push.example/device", P256dh = "p", Auth = "a", CreatedAt = DateTimeOffset.UtcNow });
        db.LoginCodes.Add(new LoginCode { UniversityId = "24-0161", CodeHash = "h", ClientHash = "c", CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        var before = Schema();

        migrator.Migrate();

        // Nothing that was there changed: not a column, not an index, not a rebuilt table.
        var after = Schema();
        foreach (var (name, sql) in before) Assert.Equal(sql, after[name]);
        // What is new is the ten tables and their indexes.
        var added = after.Keys.Except(before.Keys).ToList();
        Assert.Equal(NewTables.Select(t => "table " + t).Order(), added.Where(k => k.StartsWith("table ")).Order());
        Assert.All(added.Where(k => !k.StartsWith("table ")), k => Assert.Contains(NewTables, t => after[k].Contains($"ON \"{t}\"")));

        var kept = db.Students.Single();
        Assert.Equal(("24-0161", "24-0161@student.gutech.edu.om", (int?)15), (kept.UniversityId, kept.Email, kept.RemindMinutes));
        var pick = db.Picks.Single();
        Assert.Equal((student.Id, course.Id, course.Sections[0].Id), (pick.StudentId, pick.CourseId, pick.SectionId));
        Assert.Equal("https://push.example/device", db.PushSubscriptions.Single().Endpoint);
        Assert.Equal("Karim Hadad", db.Sections.Single().Instructor);
        Assert.Single(db.Meetings);
        Assert.Single(db.LoginCodes);
        Assert.Empty(db.Teachers);
        Assert.Empty(db.ClassChanges);
    }

    [Fact]
    public void The_teachers_migration_is_the_newest_and_only_creates_tables_and_indexes()
    {
        var all = db.GetService<IMigrationsAssembly>();
        var ids = all.Migrations.Keys.Order().ToList();
        Assert.EndsWith("_Teachers", ids[^1]);
        Assert.EndsWith("_S1AuthBinding", ids[^2]);
        Assert.Single(ids, id => id.EndsWith("_Teachers"));

        var up = all.CreateMigration(all.Migrations[ids[^1]], db.Database.ProviderName!).UpOperations;
        Assert.All(up, o => Assert.True(o is CreateTableOperation or CreateIndexOperation, $"{o.GetType().Name} is not additive."));
        Assert.Equal(NewTables.Order(), up.OfType<CreateTableOperation>().Select(o => o.Name).Order());
        Assert.All(up.OfType<CreateIndexOperation>(), o => Assert.Contains(o.Table, NewTables));
        // The model and the migrations agree: there is nothing left to migrate.
        db.Database.Migrate();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void The_new_tables_keep_one_live_holder_per_name_and_one_live_change_per_session()
    {
        db.Database.Migrate();
        var semester = new Semester { Name = "Fall 2026", Term = "Fall", Year = 2026, CreatedAt = DateTimeOffset.UtcNow };
        db.Add(semester);
        db.SaveChanges();
        var course = new Course { SemesterId = semester.Id, Code = "CS201", Name = "Data Structures", Type = CourseType.Core, Year = 2 };
        course.Sections.Add(new Section { Number = "1", Instructor = "Karim Hadad" });
        db.Courses.Add(course);
        UniSchedule.Api.Teachers.Teacher a = new() { Email = "a.b@gutech.edu.om" }, b = new() { Email = "c.d@gutech.edu.om" }, assistant = new(), second = new();
        db.AddRange(a, b, assistant, second);
        // Many accounts without an email (student profiles) and many without a student (staff) sit side by side.
        db.SaveChanges();
        var section = course.Sections[0].Id;

        UniSchedule.Api.Teachers.ClassChange Change(string kind, DateTimeOffset? ended = null) => new()
        {
            SemesterId = semester.Id, SectionId = section, GroupId = Guid.NewGuid().ToString("N"), Kind = kind,
            OrigDate = kind == "extra" ? null : new DateOnly(2026, 11, 29), OrigStart = kind == "extra" ? null : new TimeOnly(8, 0), EndedAt = ended,
        };
        // History and extra classes don't count; a second live change on the session does.
        db.AddRange(Change("cancel", DateTimeOffset.UtcNow), Change("cancel", DateTimeOffset.UtcNow), Change("extra"), Change("extra"), Change("cancel"));
        db.SaveChanges();
        db.Add(Change("move"));
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();

        // A section going takes its live rows' link with it, never the log.
        db.Sections.Where(s => s.Id == section).ExecuteDelete();
        Assert.Equal(5, db.ClassChanges.Count(c => c.SectionId == null));
        // A teacher going takes their names, and leaves the log.
        db.Teachers.Where(t => t.Id == a.Id).ExecuteDelete();
        Assert.Equal(3, db.Teachers.Count());
    }
}
