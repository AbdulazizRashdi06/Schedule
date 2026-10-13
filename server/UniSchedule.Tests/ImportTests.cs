using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Tests;

/// <summary>Preview and apply against a real (in-memory) SQLite database.</summary>
public sealed class ImportTests : IDisposable
{
    readonly SqliteConnection conn = new("Data Source=:memory:");
    readonly AppDb db;
    readonly Semester semester;
    static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    const string Header = "code,name,major,type,year,section,instructor,days,time,room\n";
    const string Base = Header +
        "CS201,Data Structures,CS,Core,2,1,Dr. A,Sun/Tue,08:00-09:30,B105\n" +
        "CS201,Data Structures,CS,Core,2,2,Dr. B,Mon/Wed,08:00-09:30,B110\n" +
        "CS210,Databases,CS,Core,2,1,Dr. C,Sun/Tue,11:30-13:00,A101\n" +
        "LS101,Academic English,,LS,,1,Ms. D,Thu,08:00-09:30,A203\n";

    public ImportTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.Migrate();
        semester = new Semester { Name = "Fall 2026", Term = "Fall", Year = 2026, CreatedAt = Now };
        db.Semesters.Add(semester);
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        conn.Dispose();
    }

    static ParsedSchedule Parse(string csv) => ScheduleImport.Parse(ScheduleFile.ReadText(csv).Rows);

    async Task Apply(string csv) => await ScheduleImport.ApplyAsync(db, semester, Parse(csv), Now, CancellationToken.None);

    async Task<Student> StudentWith(params (string Code, string Section)[] picks)
    {
        var s = new Student { UniversityId = $"24-{Random.Shared.Next(1000, 9999)}", Email = "x", CreatedAt = Now };
        db.Students.Add(s);
        await db.SaveChangesAsync();
        foreach (var (code, number) in picks)
        {
            var sec = await db.Sections.Include(x => x.Course).FirstAsync(x => x.Course.Code == code && x.Number == number);
            db.Picks.Add(new Pick { StudentId = s.Id, CourseId = sec.CourseId, SectionId = sec.Id });
        }
        await db.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task First_upload_adds_everything_and_creates_majors()
    {
        var p = await ScheduleImport.PreviewAsync(db, semester.Id, Parse(Base), CancellationToken.None);
        Assert.True(p.CanApply);
        Assert.Equal(["CS"], p.NewMajors);
        Assert.Equal(3, p.Added.Count);
        Assert.Empty(p.Updated);
        Assert.Empty(p.Removed);

        await Apply(Base);
        Assert.Equal(3, await db.Courses.CountAsync());
        Assert.Equal(4, await db.Sections.CountAsync());
        Assert.True(await db.Majors.AnyAsync(m => m.Code == "CS"));
        // LS courses belong to no major.
        Assert.Empty(await db.CourseMajors.Where(cm => cm.Course.Code == "LS101").ToListAsync());
    }

    [Fact]
    public async Task Reuploading_the_same_file_changes_nothing()
    {
        await Apply(Base);
        var ids = await db.Sections.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync();
        var p = await ScheduleImport.PreviewAsync(db, semester.Id, Parse(Base), CancellationToken.None);
        Assert.Empty(p.Added);
        Assert.Empty(p.Updated);
        Assert.Empty(p.Removed);
        await Apply(Base);
        Assert.Equal(ids, await db.Sections.OrderBy(s => s.Id).Select(s => s.Id).ToListAsync());
    }

    [Fact]
    public async Task Changed_rows_are_previewed_and_only_they_are_updated()
    {
        await Apply(Base);
        semester.IsCurrent = true;
        await db.SaveChangesAsync();
        var student = await StudentWith(("CS201", "1"), ("CS210", "1"));

        var next = Base.Replace("Sun/Tue,08:00-09:30,B105", "Sun/Tue,08:00-09:30,C001") + "CS220,Architecture,CS,Core,2,1,Dr. E,Mon/Wed,09:45-11:15,B204\n";
        var p = await ScheduleImport.PreviewAsync(db, semester.Id, Parse(next), CancellationToken.None);
        var updated = Assert.Single(p.Updated);
        Assert.Equal(("CS201", "1"), (updated.Code, updated.Section));
        Assert.Contains("Room: B105 → C001", updated.Detail);
        Assert.Equal("CS220", Assert.Single(p.Added).Code);
        Assert.Equal(1, p.StudentsAffected);

        await Apply(next);
        var changed = await db.Sections.Include(s => s.Course).Include(s => s.Meetings).SingleAsync(s => s.Course.Code == "CS201" && s.Number == "1");
        Assert.Equal(Now, changed.ChangedAt);
        Assert.All(changed.Meetings, m => Assert.Equal("C001", m.Room));
        // The untouched section has no badge, and the student's picks survive.
        Assert.Null((await db.Sections.SingleAsync(s => s.Course.Code == "CS210")).ChangedAt);
        Assert.Equal(2, await db.Picks.CountAsync(x => x.StudentId == student.Id && x.SectionId != null));
    }

    [Fact]
    public async Task Changes_to_an_unpublished_semester_get_no_badge()
    {
        await Apply(Base);
        await Apply(Base.Replace("B105", "C001"));
        Assert.All(await db.Sections.ToListAsync(), s => Assert.Null(s.ChangedAt));
    }

    [Fact]
    public async Task Removed_section_flags_the_pick_and_removed_course_drops_it()
    {
        await Apply(Base);
        var student = await StudentWith(("CS201", "2"), ("CS210", "1"));

        var next = string.Join("\n", Base.Split('\n').Where(l => !l.Contains("Dr. B") && !l.StartsWith("CS210")));
        var p = await ScheduleImport.PreviewAsync(db, semester.Id, Parse(next), CancellationToken.None);
        Assert.Equal(2, p.Removed.Count);
        Assert.Contains(p.Removed, r => r.Kind == "section" && r.Code == "CS201" && r.Section == "2");
        Assert.Contains(p.Removed, r => r.Kind == "course" && r.Code == "CS210");
        Assert.Equal(1, p.StudentsAffected);

        await Apply(next);
        var picks = await db.Picks.Include(x => x.Course).Where(x => x.StudentId == student.Id).ToListAsync();
        var pick = Assert.Single(picks);
        Assert.Equal("CS201", pick.Course.Code);
        Assert.Null(pick.SectionId);
        Assert.True(pick.SectionRemoved);
    }

    [Fact]
    public async Task Rows_with_errors_cannot_be_applied()
    {
        var p = await ScheduleImport.PreviewAsync(db, semester.Id, Parse(Base + "CS999,Bad,CS,Core,2,1,X,Funday,08:00-09:30,A1\n"), CancellationToken.None);
        Assert.False(p.CanApply);
        Assert.Equal(6, Assert.Single(p.Errors).Line);
    }
}
