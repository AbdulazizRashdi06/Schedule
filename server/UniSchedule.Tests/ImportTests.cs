using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Admin;
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

    static Meeting M(int day, string room) => new() { Day = day, Start = new(8, 0), End = new(9, 30), Room = room };

    [Fact]
    public void A_room_change_on_one_day_is_a_change_and_reordered_rows_are_not()
    {
        // Sun A101, Tue A101, Thu Lab L2 → Tue moves to Lab L2: the distinct rooms are the same, but it's still a change.
        Meeting[] old = [M(0, "A101"), M(2, "A101"), M(4, "Lab L2")];
        var moved = new[] { M(0, "A101"), M(2, "Lab L2"), M(4, "Lab L2") }.Select(ParsedMeeting.Of);
        Assert.Equal("Room: Tue A101 → Lab L2", ScheduleImport.DescribeChange("Dr. A", old, "Dr. A", moved));

        // The same meetings listed in another order are no change.
        var reordered = new[] { M(4, "Lab L2"), M(0, "A101"), M(2, "A101") }.Select(ParsedMeeting.Of);
        Assert.Null(ScheduleImport.DescribeChange("Dr. A", old, "Dr. A", reordered));
        Meeting[] twoRooms = [M(0, "B2"), M(2, "A1")];
        Assert.Null(ScheduleImport.DescribeChange("Dr. A", twoRooms, "Dr. A", new[] { M(2, "A1"), M(0, "B2") }.Select(ParsedMeeting.Of)));

        // Every meeting moving to one new room keeps the short wording.
        Assert.Equal("Room: A101 → B2", ScheduleImport.DescribeChange("Dr. A", [M(0, "A101"), M(2, "A101")], "Dr. A", new[] { M(0, "B2"), M(2, "B2") }.Select(ParsedMeeting.Of)));
    }

    [Fact]
    public async Task A_one_day_room_change_is_saved_on_upload()
    {
        var csv = Header + "CS301,Networks,CS,Core,3,1,Dr. N,Sun/Tue,08:00-09:30,A101\nCS301,Networks,CS,Core,3,1,Dr. N,Thu,08:00-09:30,Lab L2\n";
        await Apply(csv);
        semester.IsCurrent = true;
        await db.SaveChangesAsync();
        await Apply(Header + "CS301,Networks,CS,Core,3,1,Dr. N,Sun,08:00-09:30,A101\nCS301,Networks,CS,Core,3,1,Dr. N,Tue/Thu,08:00-09:30,Lab L2\n");
        db.ChangeTracker.Clear();
        var sec = await db.Sections.Include(s => s.Meetings).SingleAsync(s => s.Course.Code == "CS301");
        Assert.Equal("Lab L2", sec.Meetings.Single(m => m.Day == 2).Room);
        Assert.Equal("Room: Tue A101 → Lab L2", sec.ChangeSummary);
    }

    [Fact]
    public async Task Changes_to_an_unpublished_semester_get_no_badge()
    {
        await Apply(Base);
        await Apply(Base.Replace("B105", "C001"));
        Assert.All(await db.Sections.ToListAsync(), s => Assert.Null(s.ChangedAt));
    }

    [Fact]
    public async Task Removed_section_or_course_flags_the_picks_and_keeps_the_notes()
    {
        await Apply(Base);
        var student = await StudentWith(("CS201", "2"), ("CS210", "1"));
        // A session note on the section that's about to go.
        var gone = await db.Sections.Include(s => s.Course).SingleAsync(s => s.Course.Code == "CS201" && s.Number == "2");
        db.ClassNotes.Add(new ClassNote { StudentId = student.Id, CourseId = gone.CourseId, SectionId = gone.Id, Date = new DateOnly(2026, 10, 18), Text = "Bring the lab sheet", CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync();

        var next = string.Join("\n", Base.Split('\n').Where(l => !l.Contains("Dr. B") && !l.StartsWith("CS210")));
        var p = await ScheduleImport.PreviewAsync(db, semester.Id, Parse(next), CancellationToken.None);
        Assert.Equal(2, p.Removed.Count);
        Assert.Contains(p.Removed, r => r.Kind == "section" && r.Code == "CS201" && r.Section == "2");
        Assert.Contains(p.Removed, r => r.Kind == "course" && r.Code == "CS210");
        Assert.Equal(1, p.StudentsAffected);

        await Apply(next);
        db.ChangeTracker.Clear();
        // Both picks stay, flagged, so the student is told rather than the class silently vanishing.
        var picks = await db.Picks.Include(x => x.Course).Where(x => x.StudentId == student.Id).OrderBy(x => x.Course.Code).ToListAsync();
        Assert.Equal(["CS201", "CS210"], picks.Select(x => x.Course.Code));
        Assert.All(picks, x => Assert.Null(x.SectionId));
        Assert.All(picks, x => Assert.True(x.SectionRemoved));
        // The dropped course is kept with no sections, and nobody else can pick it.
        Assert.Empty(await db.Sections.Where(s => s.Course.Code == "CS210").ToListAsync());
        // The note on the removed section survives, as a note on the course's date.
        var note = await db.ClassNotes.SingleAsync(n => n.StudentId == student.Id);
        Assert.Null(note.SectionId);
        Assert.Equal(new DateOnly(2026, 10, 18), note.Date);
    }

    [Fact]
    public async Task A_dropped_course_nobody_picked_is_deleted()
    {
        await Apply(Base);
        await Apply(string.Join("\n", Base.Split('\n').Where(l => !l.StartsWith("CS210"))));
        Assert.False(await db.Courses.AnyAsync(c => c.Code == "CS210"));
    }

    [Fact]
    public async Task Rows_with_errors_cannot_be_applied()
    {
        var p = await ScheduleImport.PreviewAsync(db, semester.Id, Parse(Base + "CS999,Bad,CS,Core,2,1,X,Funday,08:00-09:30,A1\n"), CancellationToken.None);
        Assert.False(p.CanApply);
        Assert.Equal(6, Assert.Single(p.Errors).Line);
    }

    [Fact]
    public async Task A_dropped_course_with_default_settings_is_kept_until_it_comes_back()
    {
        await Apply(Base);
        var cs210 = await db.Courses.SingleAsync(c => c.Code == "CS210");
        var cs = await db.Majors.SingleAsync(m => m.Code == "CS");
        db.CourseDefaults.Add(new CourseDefault { SemesterId = semester.Id, CourseId = cs210.Id, MajorId = cs.Id, Year = 2 });
        await db.SaveChangesAsync();
        var without = string.Join("\n", Base.Split('\n').Where(l => !l.StartsWith("CS210")));

        var p = await ScheduleImport.PreviewAsync(db, semester.Id, Parse(without), CancellationToken.None);
        Assert.Contains("default-course settings are kept", Assert.Single(p.Removed).Detail);
        await Apply(without);
        db.ChangeTracker.Clear();
        // Kept with no sections, and the setting with it.
        Assert.Empty(await db.Sections.Where(s => s.CourseId == cs210.Id).ToListAsync());
        Assert.True(await db.CourseDefaults.AnyAsync(d => d.CourseId == cs210.Id));

        // Back in the next upload: the same course again, with its sections and its setting.
        await Apply(Base);
        db.ChangeTracker.Clear();
        Assert.Equal(cs210.Id, (await db.Courses.SingleAsync(c => c.Code == "CS210")).Id);
        Assert.Single(await db.Sections.Where(s => s.CourseId == cs210.Id).ToListAsync());
        Assert.Equal(2, (await db.CourseDefaults.SingleAsync(d => d.CourseId == cs210.Id)).Year);
    }

    [Fact]
    public async Task A_code_with_a_non_breaking_space_is_the_same_course()
    {
        await Apply(Base);
        var student = await StudentWith(("CS201", "1"));
        var nbsp = Base.Replace("CS201,", "CS\u00A0201,");
        var p = await ScheduleImport.PreviewAsync(db, semester.Id, Parse(nbsp), CancellationToken.None);
        Assert.Empty(p.Added);
        Assert.Empty(p.Removed);
        await Apply(nbsp);
        db.ChangeTracker.Clear();
        var pick = await db.Picks.SingleAsync(x => x.StudentId == student.Id);
        Assert.NotNull(pick.SectionId);
        Assert.False(pick.SectionRemoved);
        Assert.Equal(1, await db.Courses.CountAsync(c => c.Code.Contains("201")));
    }

    [Fact]
    public async Task A_family_in_the_major_column_is_an_error_and_never_becomes_a_major()
    {
        db.Majors.AddRange(new Major { Code = "CS", Name = "Computer Science", Family = "COMP" }, new Major { Code = "AI", Name = "AI", Family = "COMP" });
        await db.SaveChangesAsync();
        var csv = Header + "CS301,Shared,COMP,Core,3,1,Dr. A,Sun,08:00-09:30,B105\n";
        var families = await ScheduleImport.FamilyCodesAsync(db, CancellationToken.None);
        var parsed = ScheduleImport.Parse(ScheduleFile.ReadText(csv).Rows, families);
        var error = Assert.Single(parsed.Errors);
        Assert.Equal(2, error.Line);
        Assert.Contains("\"COMP\" is a family of majors", error.Message);
        Assert.False((await ScheduleImport.PreviewAsync(db, semester.Id, parsed, CancellationToken.None)).CanApply);

        // Even when applied without the check, no major named after the family is made.
        await Apply(Header + "CS301,Shared,COMP;CS,Core,3,1,Dr. A,Sun,08:00-09:30,B105\n");
        Assert.False(await db.Majors.AnyAsync(m => m.Code == "COMP"));
        Assert.Equal(["CS"], await db.CourseMajors.Where(cm => cm.Course.Code == "CS301").Select(cm => cm.Major.Code).ToListAsync());
    }

    [Fact]
    public async Task A_section_imported_with_overlapping_meetings_can_still_be_edited()
    {
        await Apply(Header + "CS201,Data Structures,CS,Core,2,1,Dr. A,Sun,08:00-09:30,R1\nCS201,Data Structures,CS,Core,2,1,Dr. A,Sun,08:00-09:30,R2\n");
        var stored = (await db.Sections.Include(s => s.Meetings).SingleAsync(s => s.Course.Code == "CS201")).Meetings.ToList();
        Assert.Equal(2, stored.Count);
        var same = stored.Select(m => new AdminEndpoints.MeetingBody(m.Day, Timeslots.Format(m.Start), Timeslots.Format(m.End), m.Room)).ToList();
        // Sending them back (say, with only the instructor changed) saves.
        var (meetings, problem) = AdminEndpoints.ParseMeetings(same, stored);
        Assert.Null(problem);
        Assert.Equal(2, meetings!.Count);
        // A new overlap is still refused.
        (_, problem) = AdminEndpoints.ParseMeetings([.. same, new AdminEndpoints.MeetingBody(0, "09:00", "10:00", "R3")], stored);
        Assert.Equal("Two meetings overlap on Sun.", problem);
    }
}
