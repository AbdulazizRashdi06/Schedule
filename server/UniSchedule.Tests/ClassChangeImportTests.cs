using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.ChangeKit;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// When the university timetable changes under a teacher's change. Through <c>ScheduleImport.ApplyAsync</c> against a
/// real (in-memory) SQLite database: a room-only change keeps the teacher's change, a moved or removed class drops it
/// and tells the teacher, a timetable that now shows what the teacher did merges quietly, and the preview lists
/// exactly what the apply then does. The last tests go through the dashboard: a sync, a hand edit, a day off and a
/// teaching-weeks edit.
/// </summary>
public sealed class ClassChangeImportTests : IDisposable
{
    readonly SqliteConnection conn = new("Data Source=:memory:");
    readonly AppDb db;
    readonly Semester semester;
    /// <summary>Wednesday 25 November 2026, noon in Oman.</summary>
    static readonly DateTimeOffset Now = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    static readonly DateOnly Sun = new(2026, 11, 29), Mon = new(2026, 11, 30), Tue = new(2026, 12, 1), NextSun = new(2026, 12, 6), NextMon = new(2026, 12, 7);
    const string Salim = "Salim Al Harthy", Huda = "Huda Al Kindi";

    Teacher salim = null!, huda = null!;

    /// <summary>One class of the upload: a section on one date.</summary>
    sealed record Row(string Code, string Section, string Instructor, DateOnly Date, string Time, string Room);

    /// <summary>
    /// One real class under three codes (Salim, Sundays), and three classes of Huda's on Mondays.
    /// </summary>
    static List<Row> Timetable()
    {
        var rows = new List<Row>();
        foreach (var sunday in new[] { Sun, NextSun })
        foreach (var code in new[] { "BE1101", "IBSM1101", "LOG1101" })
            rows.Add(new(code, "1", Salim, sunday, "08:00-09:30", "A101"));
        foreach (var monday in new[] { Mon, NextMon })
        {
            rows.Add(new("CS2201", "1", Huda, monday, "08:00-09:30", "B105"));
            rows.Add(new("CS2202", "1", Huda, monday, "09:45-11:15", "B105"));
            rows.Add(new("CS2203", "1", Huda, monday, "11:30-13:00", "B105"));
        }
        return rows;
    }

    static string Csv(IEnumerable<Row> rows) => "code,name,major,type,year,section,instructor,date,time,room\n" + string.Concat(rows.Select(r =>
        $"{r.Code},Course {r.Code},{r.Code[..2]},Core,1,{r.Section},{r.Instructor},{r.Date:dd/MM/yyyy},{r.Time},{r.Room}\n"));

    public ClassChangeImportTests()
    {
        conn.Open();
        db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite(conn).Options);
        db.Database.Migrate();
        semester = new Semester { Name = "Fall 2026", Term = "Fall", Year = 2026, CreatedAt = Now, IsCurrent = true, PublishedAt = Now, Week1Start = new(2026, 9, 20), LastWeek = 14 };
        db.Semesters.Add(semester);
        db.SaveChanges();
    }

    public void Dispose()
    {
        db.Dispose();
        conn.Dispose();
    }

    sealed class Fixed(DateTimeOffset at) : TimeProvider { public override DateTimeOffset GetUtcNow() => at; }

    static ParsedSchedule Parse(IEnumerable<Row> rows) => ScheduleImport.Parse(ScheduleFile.ReadText(Csv(rows)).Rows);

    async Task Apply(IEnumerable<Row> rows)
    {
        await ScheduleImport.ApplyAsync(db, semester, Parse(rows), new Fixed(Now), CancellationToken.None);
        db.ChangeTracker.Clear();
    }

    Task<ImportPreview> Preview(IEnumerable<Row> rows) => ScheduleImport.PreviewAsync(db, semester.Id, Parse(rows), CancellationToken.None, Now);

    /// <summary>The timetable, and the two teachers linked to their names.</summary>
    async Task Seed()
    {
        await Apply(Timetable());
        salim = await AddTeacher("salim.alharthy@gutech.edu.om", Salim);
        huda = await AddTeacher("huda.alkindi@gutech.edu.om", Huda);
        // The first upload is no news to anyone.
        await db.Sections.ExecuteUpdateAsync(u => u.SetProperty(s => s.ChangedAt, (DateTimeOffset?)null).SetProperty(s => s.ChangeSummary, (string?)null));
    }

    async Task<Teacher> AddTeacher(string email, params string[] names)
    {
        var t = new Teacher { Email = email, CreatedAt = Now };
        db.Teachers.Add(t);
        await db.SaveChangesAsync();
        db.TeacherNames.AddRange(names.Select(n => new TeacherName
        {
            TeacherId = t.Id, Name = n, NameKey = TeacherNames.NameKey(n), State = TeacherName.Approved, How = TeacherName.ByStaff, CreatedAt = Now,
        }));
        await db.SaveChangesAsync();
        return t;
    }

    Task<Section> Section(string code, string number = "1") =>
        db.Sections.AsNoTracking().Include(s => s.Meetings).Include(s => s.Course).SingleAsync(s => s.Course.SemesterId == semester.Id && s.Course.Code == code && s.Number == number);

    /// <summary>
    /// A teacher's change as the teacher endpoints store it: one row per code, sharing a group. <paramref name="to"/>
    /// is the new time of a move or an extra class.
    /// </summary>
    async Task<List<ClassChange>> Change(Teacher by, string kind, string[] codes, DateOnly? date, string? start,
        (DateOnly Date, string Start, string End)? to = null, string? room = null)
    {
        var group = Guid.NewGuid().ToString("N");
        var rows = new List<ClassChange>();
        foreach (var code in codes)
        {
            var s = await Section(code);
            var session = start is null ? null : s.Meetings.Single(m => m.Date == date && m.Start == TimeOnly.Parse(start));
            var newDate = kind == "room" ? date : to?.Date;
            var newStart = kind == "room" ? session!.Start : to is null ? (TimeOnly?)null : TimeOnly.Parse(to.Value.Start);
            rows.Add(new ClassChange
            {
                SemesterId = semester.Id, SectionId = s.Id, CourseCode = s.Course.Code, CourseName = s.Course.Name, SectionNumber = s.Number, GroupId = group,
                Kind = kind, SessionType = "lecture", OrigDate = session is null ? null : date, OrigStart = session?.Start, OrigEnd = session?.End, OrigRoom = session?.Room,
                NewDate = newDate, NewStart = newStart, NewEnd = kind == "room" ? session!.End : to is null ? null : TimeOnly.Parse(to.Value.End),
                NewRoom = kind == "cancel" ? null : room ?? "",
                Tag = session is null ? TeachNotices.Tag(s.Id, newDate!.Value, newStart!.Value, true) : TeachNotices.Tag(s.Id, date!.Value, session.Start, false),
                TeacherId = by.Id, ByKind = "teacher", ById = by.Id, ByLabel = by.Email!, ByName = by.Id == salim.Id ? Salim : Huda,
                NamedTeacher = s.Instructor, CreatedAt = Now,
            });
        }
        db.ClassChanges.AddRange(rows);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return rows;
    }

    Task<List<ClassChange>> Rows() => db.ClassChanges.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
    async Task<List<ClassChange>> Live() => (await Rows()).Where(c => c.EndedAt is null).ToList();
    Task<List<TeachNotice>> Notices(string audience = TeachNotice.ToTeacher) => db.TeachNotices.AsNoTracking().Where(n => n.Audience == audience).OrderBy(n => n.Id).ToListAsync();

    async Task AssertNoOrphans()
    {
        await using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check";
        await using (var broken = await check.ExecuteReaderAsync())
            Assert.False(await broken.ReadAsync(), "A foreign key points at a missing row.");
        Assert.DoesNotContain(await Live(), c => c.SectionId is null);
    }

    /// <summary>The live changes of the semester as every reader sees them.</summary>
    async Task<(ChangeSet Changes, List<Section> Sections)> Shown()
    {
        var sections = await db.Sections.AsNoTracking().Where(s => s.Course.SemesterId == semester.Id).Include(s => s.Course).Include(s => s.Meetings).ToListAsync();
        return (await ClassChanges.LoadAsync(db, semester.Id, sections, CancellationToken.None), sections);
    }

    static IEnumerable<Row> With(IEnumerable<Row> rows, Func<Row, bool> which, Func<Row, Row> change) => rows.Select(r => which(r) ? change(r) : r);

    // ---- Kept ----

    [Fact]
    public async Task A_room_only_and_an_instructor_only_reimport_keep_a_cancel_though_the_meeting_ids_change()
    {
        await Seed();
        await Change(huda, "cancel", ["CS2201"], Mon, "08:00");
        var before = (await Section("CS2201")).Meetings.Select(m => m.Id).ToList();

        // The university puts that very session in another room.
        await Apply(With(Timetable(), r => r is { Code: "CS2201" } && r.Date == Mon, r => r with { Room = "B110" }));
        var section = await Section("CS2201");
        Assert.Empty(section.Meetings.Select(m => m.Id).Intersect(before));
        var change = Assert.Single(await Live());
        // The snapshot follows the university; the key never held the room.
        Assert.Equal("B110", change.OrigRoom);
        var (changes, sections) = await Shown();
        var listed = sections.Single(s => s.Id == section.Id);
        Assert.Contains(Mon, changes.Skip(listed, listed.Meetings.Single(m => m.Date == Mon)));
        // The class isn't on either way, so nobody is told the section changed, and the teacher gets nothing.
        Assert.Null(section.ChangedAt);
        Assert.Empty(await Notices());

        // Another instructor, same times: still kept.
        await Apply(With(Timetable(), r => r.Code == "CS2201", r => r with { Instructor = "Maryam Nasser", Room = r.Date == Mon ? "B110" : r.Room }));
        Assert.Single(await Live());
        Assert.NotNull((await Section("CS2201")).ChangedAt);

        // A change that is over is history: nothing decides it again, even when its session goes.
        db.ChangeTracker.Clear();
        await db.ClassChanges.ExecuteUpdateAsync(u => u.SetProperty(c => c.OrigDate, new DateOnly(2026, 11, 23)));
        await Apply(Timetable().Where(r => !(r.Code == "CS2201" && r.Date == Mon)));
        Assert.Single(await Live());
        Assert.DoesNotContain(await Notices(), n => n.Kind == "dropped");
    }

    // ---- Dropped ----

    [Fact]
    public async Task A_moved_session_drops_the_change_and_tells_the_teacher_once()
    {
        await Seed();
        await Change(huda, "cancel", ["CS2201"], Mon, "08:00");
        // Students were told about the cancel.
        db.TeachNotices.Add(new TeachNotice { Audience = TeachNotice.Students, SectionId = (await Section("CS2201")).Id, Kind = "change", Tag = (await Live())[0].Tag, Title = "x", ClaimedAt = Now, SentAt = Now, CreatedAt = Now });
        await db.SaveChangesAsync();

        var moved = With(Timetable(), r => r.Code == "CS2201" && r.Date == Mon, r => r with { Time = "13:15-14:45" }).ToList();
        await Apply(moved);
        var row = Assert.Single(await Rows());
        Assert.Equal(("dropped-moved", "system"), (row.EndReason, row.EndedBy));
        Assert.Equal(Now, row.EndedAt);
        // One notice to the teacher, about that change, and no "your class changed" on top of it.
        var notice = Assert.Single(await Notices());
        Assert.Equal(("dropped", huda.Id, row.GroupId), (notice.Kind, notice.TeacherId, notice.GroupId));
        Assert.Equal("Your change to CS2201 was dropped", notice.Title);
        Assert.Equal("The university timetable changed Mon 30 Nov 08:00. Your cancellation no longer applies; check the new time.", notice.Body);
        // Students get no "back on" (it would be false): the "changed" stamp tells them about the university's move.
        Assert.DoesNotContain(await Notices(TeachNotice.Students), n => n.Kind != "change");
        Assert.NotNull((await Section("CS2201")).ChangedAt);

        // The same file again changes nothing and tells nobody.
        await Apply(moved);
        Assert.Single(await Notices());

        // The timetable reverts: the dropped change does not come back.
        await Apply(Timetable());
        Assert.Empty(await Live());
        var (changes, sections) = await Shown();
        Assert.True(changes.Empty);
        Assert.Equal(2, sections.Single(s => s.Course.Code == "CS2201").Meetings.Count);
        await AssertNoOrphans();
    }

    [Fact]
    public async Task One_apply_that_drops_a_changed_three_code_class_gives_its_teacher_one_dropped_and_no_uni_changed()
    {
        await Seed();
        var rows = await Change(salim, "room", ["BE1101", "IBSM1101", "LOG1101"], Sun, "08:00", room: "C201");

        await Apply(With(Timetable(), r => r.Instructor == Salim && r.Date == Sun, r => r with { Time = "10:00-11:30" }));
        Assert.All(await Rows(), c => Assert.Equal("dropped-moved", c.EndReason));
        var notice = Assert.Single(await Notices());
        Assert.Equal(("dropped", salim.Id, rows[0].GroupId), (notice.Kind, notice.TeacherId, notice.GroupId));
        Assert.Equal("Your change to BE1101 · IBSM1101 · LOG1101 was dropped", notice.Title);
        Assert.Contains("Your room change no longer applies", notice.Body);
    }

    [Fact]
    public async Task A_removed_section_and_a_removed_course_end_their_changes_before_the_delete_and_the_log_stays_readable()
    {
        await Seed();
        // A student picked CS2202, so the course stays (with no sections); nobody picked CS2203, so it goes.
        var student = new Student { UniversityId = "26-7001", Email = "x", CreatedAt = Now };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        var picked = await Section("CS2202");
        db.Picks.Add(new Pick { StudentId = student.Id, CourseId = picked.CourseId, SectionId = picked.Id });
        await db.SaveChangesAsync();
        await Change(huda, "cancel", ["CS2202"], Mon, "09:45");
        await Change(huda, "move", ["CS2203"], Mon, "11:30", (Tue, "11:30", "13:00"), "B105");
        await Change(huda, "extra", ["CS2203"], null, null, (Tue, "15:00", "16:00"), "B105");
        // Salim added CS2203 to his classes.
        var added = await Section("CS2203");
        db.TeacherSections.Add(new TeacherSection { TeacherId = salim.Id, SemesterId = semester.Id, SectionId = added.Id, CourseCode = "CS2203", SectionNumber = "1", Mode = TeacherSection.AddedMode, CreatedAt = Now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Apply(Timetable().Where(r => r.Code is not ("CS2202" or "CS2203")));
        Assert.Empty(await db.Sections.Where(s => s.Course.Code == "CS2202" || s.Course.Code == "CS2203").ToListAsync());
        var rows = await Rows();
        Assert.All(rows, c =>
        {
            Assert.Equal(("dropped-removed", "system"), (c.EndReason, c.EndedBy));
            Assert.Null(c.SectionId);
            // The text copies keep the log readable.
            Assert.StartsWith("CS220", c.CourseCode);
            Assert.Equal("1", c.SectionNumber);
            Assert.Equal(Huda, c.ByName);
        });
        Assert.Equal(3, rows.Count);
        await AssertNoOrphans();

        // Huda is told about each change; Salim, who added CS2203, once (the move and the extra class are two changes).
        var notices = await Notices();
        Assert.Equal(3, notices.Count(n => n.Kind == "dropped" && n.TeacherId == huda.Id));
        Assert.All(notices.Where(n => n.Kind == "dropped"), n => Assert.EndsWith("is no longer on the timetable.", n.Body));
        Assert.Equal(2, notices.Count(n => n.Kind == "dropped" && n.TeacherId == salim.Id));
        Assert.DoesNotContain(notices, n => n.Kind == "uni-changed");
        // The added row stays, pointing nowhere, for the "removed" list.
        Assert.Null((await db.TeacherSections.AsNoTracking().SingleAsync()).SectionId);

        // It comes back: the added section is his again, by its text.
        await Apply(Timetable());
        Assert.Equal((await Section("CS2203")).Id, (await db.TeacherSections.AsNoTracking().SingleAsync()).SectionId);
        // The dropped changes stay dropped.
        Assert.Empty(await Live());
    }

    [Fact]
    public async Task An_added_section_that_goes_tells_its_adder_and_reattaches_when_it_returns()
    {
        await Seed();
        var added = await Section("CS2203");
        db.TeacherSections.Add(new TeacherSection { TeacherId = salim.Id, SemesterId = semester.Id, SectionId = added.Id, CourseCode = "CS2203", SectionNumber = "1", Mode = TeacherSection.AddedMode, CreatedAt = Now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Apply(Timetable().Where(r => r.Code != "CS2203"));
        var notice = Assert.Single(await Notices());
        Assert.Equal(("section-removed", salim.Id, "CS2203 section 1 is no longer on the timetable."), (notice.Kind, notice.TeacherId, notice.Title));
        Assert.Null((await db.TeacherSections.AsNoTracking().SingleAsync()).SectionId);
        await Apply(Timetable());
        Assert.Equal((await Section("CS2203")).Id, (await db.TeacherSections.AsNoTracking().SingleAsync()).SectionId);
        await AssertNoOrphans();
    }

    // ---- Merged ----

    [Fact]
    public async Task A_removed_session_merges_a_cancel_quietly()
    {
        await Seed();
        await Change(huda, "cancel", ["CS2201"], Mon, "08:00");
        await Apply(Timetable().Where(r => !(r.Code == "CS2201" && r.Date == Mon)));
        var row = Assert.Single(await Rows());
        Assert.Equal(("merged", "system"), (row.EndReason, row.EndedBy));
        // Quiet: no teacher notice, no student notice, and no "changed" stamp (students see what they saw).
        Assert.Empty(await db.TeachNotices.ToListAsync());
        Assert.Null((await Section("CS2201")).ChangedAt);
    }

    [Fact]
    public async Task The_university_adopting_a_move_merges_it_with_no_changed_stamp()
    {
        await Seed();
        await Change(huda, "move", ["CS2201"], Mon, "08:00", (Tue, "10:00", "11:30"), "B202");
        await Apply(With(Timetable(), r => r.Code == "CS2201" && r.Date == Mon, r => r with { Date = Tue, Time = "10:00-11:30", Room = "B202" }));
        Assert.Equal("merged", Assert.Single(await Rows()).EndReason);
        Assert.Empty(await db.TeachNotices.ToListAsync());
        var section = await Section("CS2201");
        Assert.Null(section.ChangedAt);
        Assert.Contains(section.Meetings, m => m.Date == Tue && m.Room == "B202");
    }

    [Fact]
    public async Task The_university_moving_it_to_the_teachers_time_in_another_room_ends_the_move_and_tells_the_teacher()
    {
        await Seed();
        var rows = await Change(huda, "move", ["CS2201"], Mon, "08:00", (Tue, "10:00", "11:30"), "B202");
        await Apply(With(Timetable(), r => r.Code == "CS2201" && r.Date == Mon, r => r with { Date = Tue, Time = "10:00-11:30", Room = "B105" }));
        Assert.Equal("merged", Assert.Single(await Rows()).EndReason);
        var notice = Assert.Single(await Notices());
        Assert.Equal(("merged", huda.Id, rows[0].GroupId), (notice.Kind, notice.TeacherId, notice.GroupId));
        Assert.Equal("CS2201: the university moved it too", notice.Title);
        Assert.Equal("It now lists Tue 1 Dec 10:00 in B105. Students see that.", notice.Body);
        // Students see another room than before, so the section is stamped.
        Assert.NotNull((await Section("CS2201")).ChangedAt);
    }

    // ---- Another room ----

    [Fact]
    public async Task When_the_university_lists_another_room_the_teachers_room_stays_and_they_are_told_once()
    {
        await Seed();
        var rows = await Change(huda, "room", ["CS2201"], Mon, "08:00", room: "C201");
        var other = With(Timetable(), r => r.Code == "CS2201" && r.Date == Mon, r => r with { Room = "B110" }).ToList();
        await Apply(other);

        var change = Assert.Single(await Live());
        Assert.Equal(("B110", "C201"), (change.OrigRoom, change.NewRoom));
        var (changes, sections) = await Shown();
        Assert.Equal("C201", Assert.Single(changes.AddedTo(sections.Single(s => s.Course.Code == "CS2201"))).Room);
        var notice = Assert.Single(await Notices());
        Assert.Equal(("room-differs", huda.Id, rows[0].GroupId), (notice.Kind, notice.TeacherId, notice.GroupId));
        Assert.Equal("CS2201: the university lists another room", notice.Title);
        Assert.Equal("It now says B110 for Mon 30 Nov 08:00. Students still see your room, C201. Undo your change to use the university's room.", notice.Body);
        // Students still see C201: nothing changed for them.
        Assert.Null((await Section("CS2201")).ChangedAt);

        // A second identical import tells nobody.
        await Apply(other);
        Assert.Single(await Notices());
        // The university then takes the teacher's room: merged, quietly.
        await Apply(With(Timetable(), r => r.Code == "CS2201" && r.Date == Mon, r => r with { Room = "C201" }));
        Assert.Equal("merged", Assert.Single(await Rows()).EndReason);
        Assert.Single(await Notices());
        Assert.Null((await Section("CS2201")).ChangedAt);
    }

    [Fact]
    public async Task A_kept_move_whose_new_time_the_university_newly_lists_in_another_room_tells_the_teacher_once()
    {
        await Seed();
        await Change(huda, "move", ["CS2201"], Mon, "08:00", (Tue, "10:00", "11:30"), "B202");
        var listed = Timetable().Append(new("CS2201", "1", Huda, Tue, "10:00-11:30", "B105")).ToList();
        await Apply(listed);
        Assert.Single(await Live());
        var notice = Assert.Single(await Notices());
        Assert.Equal(("room-differs", "CS2201: the university lists it at your new time", "It now says Tue 1 Dec 10:00 in B105. Students see that room."),
            (notice.Kind, notice.Title, notice.Body));
        // One class at the new time, the university's: no doubles.
        var (changes, sections) = await Shown();
        Assert.Empty(changes.AddedTo(sections.Single(s => s.Course.Code == "CS2201")));
        // Another change to the section later says nothing about the room again.
        await Apply(With(listed, r => r.Code == "CS2201" && r.Date == NextMon, r => r with { Room = "B300" }));
        Assert.Single(await Notices(), n => n.Kind == "room-differs");
    }

    // ---- Reassignment and "your class changed" ----

    [Fact]
    public async Task Reassignment_keeps_the_changes_and_tells_both_teachers()
    {
        await Seed();
        await Change(huda, "cancel", ["CS2201"], Mon, "08:00");
        await Change(huda, "cancel", ["CS2201"], NextMon, "08:00");
        await Apply(With(Timetable(), r => r.Code == "CS2201", r => r with { Instructor = Salim }));

        Assert.Equal(2, (await Live()).Count);
        var notices = await Notices();
        var was = Assert.Single(notices, n => n.TeacherId == huda.Id);
        Assert.Equal(("reassigned", "CS2201 section 1 is now listed under Salim Al Harthy.", "Your 2 upcoming changes stay until someone undoes them."), (was.Kind, was.Title, was.Body));
        var takes = Assert.Single(notices, n => n.TeacherId == salim.Id);
        Assert.Equal(("reassigned", "CS2201 section 1 is now under your name.", "It has 2 upcoming changes made by Huda Al Kindi. You can undo them from the class."), (takes.Kind, takes.Title, takes.Body));
        // The section is the new teacher's to change, so they can undo.
        var salims = await TeacherSchedule.SectionsAsync(db, semester.Id, salim, [TeacherNames.NameKey(Salim)], CancellationToken.None);
        Assert.Contains(salims, t => t.Course.Code == "CS2201");

        // A re-spelling that leaves the name's key equal is no reassignment.
        await Apply(With(Timetable(), r => r.Code == "CS2201", r => r with { Instructor = "SALIM  AL-HARTHY" }));
        Assert.Equal(2, (await Notices()).Count(n => n.Kind == "reassigned"));

        // Several sections to one teacher in one apply: one notice each, listing them.
        await db.TeachNotices.ExecuteDeleteAsync();
        await Apply(With(Timetable(), r => r.Code is "CS2202" or "CS2203", r => r with { Instructor = Salim }).Select(r => r.Code == "CS2201" ? r with { Instructor = "SALIM  AL-HARTHY" } : r));
        notices = await Notices();
        Assert.Equal("CS2202 1 and CS2203 1 are now listed under Salim Al Harthy.", Assert.Single(notices, n => n.TeacherId == huda.Id).Title);
        Assert.Equal("CS2202 1 and CS2203 1 are now under your name.", Assert.Single(notices, n => n.TeacherId == salim.Id).Title);
    }

    [Fact]
    public async Task Teachers_are_told_when_the_university_changes_their_classes_once_per_apply()
    {
        await Seed();
        // One class: its code and what students are told.
        await Apply(With(Timetable(), r => r.Code == "CS2201" && r.Date == Mon, r => r with { Room = "B110" }));
        var one = Assert.Single(await Notices());
        Assert.Equal(("uni-changed", huda.Id, "CS2201 changed", "/teach"), (one.Kind, one.TeacherId, one.Title, one.Url));
        Assert.Equal((await Section("CS2201")).ChangeSummary, one.Body);
        Assert.Equal($"tuni-{huda.Id}", one.Tag);

        // Three of her classes in one apply: one notice. Salim's merged class changes too: one for him.
        await db.TeachNotices.ExecuteDeleteAsync();
        var second = With(Timetable(), r => r.Date == NextMon || r.Date == NextSun, r => r with { Room = "B300" }).ToList();
        await Apply(second);
        var notices = await Notices();
        Assert.Equal(2, notices.Count);
        var hers = Assert.Single(notices, n => n.TeacherId == huda.Id);
        Assert.Equal(("3 of your classes changed", "CS2201 1, CS2202 1, CS2203 1"), (hers.Title, hers.Body));
        Assert.Equal("3 of your classes changed", Assert.Single(notices, n => n.TeacherId == salim.Id).Title);

        // Someone who added a class is told; someone who hid it, or muted university changes, is not.
        await db.TeachNotices.ExecuteDeleteAsync();
        var cs = await Section("CS2201");
        var be = await Section("BE1101");
        db.TeacherSections.Add(new TeacherSection { TeacherId = salim.Id, SemesterId = semester.Id, SectionId = cs.Id, CourseCode = "CS2201", SectionNumber = "1", Mode = TeacherSection.AddedMode, CreatedAt = Now });
        db.TeacherSections.Add(new TeacherSection { TeacherId = salim.Id, SemesterId = semester.Id, SectionId = be.Id, CourseCode = "BE1101", SectionNumber = "1", Mode = TeacherSection.HiddenMode, CreatedAt = Now });
        await db.SaveChangesAsync();
        await db.Teachers.Where(t => t.Id == huda.Id).ExecuteUpdateAsync(u => u.SetProperty(t => t.MuteChanges, true));
        db.ChangeTracker.Clear();
        await Apply(With(second, r => r.Code is "CS2201" or "BE1101", r => r with { Room = "B400" }));
        var told = Assert.Single(await Notices());
        Assert.Equal((salim.Id, "CS2201 changed"), (told.TeacherId, told.Title));

        // A semester nobody is using yet tells nobody.
        await db.TeachNotices.ExecuteDeleteAsync();
        await db.Semesters.ExecuteUpdateAsync(u => u.SetProperty(s => s.IsCurrent, false));
        semester.IsCurrent = false;
        await Apply(Timetable());
        Assert.Empty(await Notices());
    }

    // ---- Re-spelling ----

    [Fact]
    public async Task A_respelled_name_gets_a_hint_and_never_links_by_itself()
    {
        await Seed();
        await Apply(With(Timetable(), r => r.Instructor == Huda, r => r with { Instructor = "Huda Al Kindy" }));
        var link = await db.TeacherNames.AsNoTracking().SingleAsync(n => n.TeacherId == huda.Id);
        // The link is untouched, and gives her nothing under the new spelling until staff say it is the same person.
        Assert.Equal((Huda, TeacherName.Approved, "Huda Al Kindy"), (link.Name, link.State, link.SuggestedName));
        Assert.Empty(await TeacherSchedule.SectionsAsync(db, semester.Id, huda, [link.NameKey], CancellationToken.None));
        Assert.Null((await db.TeacherNames.AsNoTracking().SingleAsync(n => n.TeacherId == salim.Id)).SuggestedName);

        // Back on the timetable: the hint goes.
        await Apply(Timetable());
        Assert.Null((await db.TeacherNames.AsNoTracking().SingleAsync(n => n.TeacherId == huda.Id)).SuggestedName);

        // Her sections go to two different names, to a placeholder, or to a name someone holds: no hint.
        await Apply(With(Timetable(), r => r.Instructor == Huda, r => r with { Instructor = r.Code == "CS2201" ? "Huda Al Kindy" : "Maryam Nasser" }));
        Assert.Null((await db.TeacherNames.AsNoTracking().SingleAsync(n => n.TeacherId == huda.Id)).SuggestedName);
        await Apply(Timetable());
        await Apply(With(Timetable(), r => r.Instructor == Huda, r => r with { Instructor = "CS2 CS2" }));
        Assert.Null((await db.TeacherNames.AsNoTracking().SingleAsync(n => n.TeacherId == huda.Id)).SuggestedName);
        await Apply(Timetable());
        await Apply(With(Timetable(), r => r.Instructor == Huda, r => r with { Instructor = Salim }));
        Assert.Null((await db.TeacherNames.AsNoTracking().SingleAsync(n => n.TeacherId == huda.Id)).SuggestedName);
    }

    // ---- The preview ----

    [Fact]
    public async Task The_preview_lists_exactly_what_the_apply_then_does_and_saves_nothing()
    {
        await Seed();
        await Change(salim, "cancel", ["BE1101", "IBSM1101", "LOG1101"], Sun, "08:00");           // its session moves: dropped (three rows)
        await Change(huda, "cancel", ["CS2201"], Mon, "08:00");                                    // its session goes: merged
        await Change(huda, "room", ["CS2202"], Mon, "09:45", room: "C201");                        // another room: stays, teacher told
        await Change(huda, "move", ["CS2203"], Mon, "11:30", (Tue, "11:30", "13:00"), "B105");     // its section goes: dropped
        await Change(huda, "cancel", ["CS2202"], NextMon, "09:45");                                // untouched: kept, not listed
        await Change(huda, "room", ["CS2201"], NextMon, "08:00", room: "C300");                    // only the end changes: kept, not listed
        var upload = With(Timetable(), r => r.Instructor == Salim && r.Date == Sun, r => r with { Time = "10:00-11:30" })
            .Where(r => !(r.Code == "CS2201" && r.Date == Mon)).Where(r => r.Code != "CS2203")
            .Select(r => r.Code == "CS2202" && r.Date == Mon ? r with { Room = "B110" } : r)
            .Select(r => r.Code == "CS2201" && r.Date == NextMon ? r with { Time = "08:00-10:00" } : r).ToList();

        var preview = await Preview(upload);
        var effects = preview.TeacherChanges!;
        Assert.Equal(
        [
            ("dropped", "BE1101", "1", "Cancelled Sun 29 Nov 08:00", Salim),
            ("merged", "CS2201", "1", "Cancelled Mon 30 Nov 08:00", Huda),
            ("room-differs", "CS2202", "1", "Room C201 for Mon 30 Nov 09:45", Huda),
            ("dropped", "CS2203", "1", "Moved Mon 30 Nov 11:30 → Tue 1 Dec 11:30", Huda),
            ("dropped", "IBSM1101", "1", "Cancelled Sun 29 Nov 08:00", Salim),
            ("dropped", "LOG1101", "1", "Cancelled Sun 29 Nov 08:00", Salim),
        ], effects.Select(e => (e.Effect, e.Code, e.Section, e.What, e.By)));
        // Nothing was saved.
        Assert.Equal(8, (await Live()).Count);
        Assert.Empty(await db.TeachNotices.ToListAsync());

        await Apply(upload);
        var rows = await Rows();
        string[] Ended(params string[] reasons) => rows.Where(c => reasons.Contains(c.EndReason)).Select(c => c.CourseCode).Order().ToArray();
        Assert.Equal(effects.Where(e => e.Effect == "dropped").Select(e => e.Code).Order(), Ended("dropped-moved", "dropped-removed"));
        Assert.Equal(effects.Where(e => e.Effect == "merged").Select(e => e.Code).Order(), Ended("merged"));
        var live = rows.Where(c => c.EndedAt is null).ToList();
        Assert.Equal(["CS2201", "CS2202", "CS2202"], live.Select(c => c.CourseCode).Order());
        // The kept room change follows the university's new end time.
        Assert.Equal(new TimeOnly(10, 0), live.Single(c => c.CourseCode == "CS2201").NewEnd);
        Assert.Equal(4, await ClassChanges.DroppedSinceAsync(db, semester.Id, Now, CancellationToken.None));
        // Every "room-differs" row of the preview is one notice to that teacher.
        var notices = await Notices();
        Assert.Equal(effects.Count(e => e.Effect == "room-differs"), notices.Count(n => n.Kind == "room-differs"));
        // Salim: one "dropped" for his three-code class. Huda: one "dropped", one "room-differs", and "changed" for
        // the one class the others don't cover (CS2201: its cancel merged quietly, but another date changed).
        Assert.Equal(["dropped"], notices.Where(n => n.TeacherId == salim.Id).Select(n => n.Kind));
        Assert.Equal(["dropped", "room-differs", "uni-changed"], notices.Where(n => n.TeacherId == huda.Id).Select(n => n.Kind).Order());
        Assert.Equal("CS2201 changed", notices.Single(n => n.Kind == "uni-changed").Title);

        // With nothing left to decide, the preview lists nothing.
        Assert.Empty((await Preview(upload)).TeacherChanges ?? []);
        await AssertNoOrphans();
    }

    [Fact]
    public async Task A_semester_no_teacher_touched_previews_no_teacher_changes()
    {
        await Apply(Timetable());
        var preview = await Preview(Timetable().Where(r => r.Code != "CS2203"));
        Assert.Null(preview.TeacherChanges);
        Assert.Single(preview.Removed);
    }

    // ---- Through the dashboard ----

    static MultipartFormDataContent File(string csv) => new() { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "t.csv" } };

    static string Day(DateOnly d) => d.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task An_upload_and_a_sync_preview_the_dropped_changes_and_count_them_when_applied()
    {
        using var w = await TeachWorld.Create();
        var cs = await w.Section("CS2201", "1");
        var (first, second) = (w.Date(0, 1), w.Date(1, 1));
        await w.Teacher2.Made(new { kind = "cancel", sectionId = cs, date = Iso(first), start = "08:00" });
        await w.Teacher2.Made(new { kind = "room", sectionId = cs, date = Iso(second), start = "08:00", room = "C201" });

        // An upload that moves the first session: the preview names the cancel, by the teacher's shown name.
        var export = await (await w.Owner.GetAsync($"/api/admin/semesters/{w.Semester}/export.csv")).Content.ReadAsStringAsync();
        var line = export.Split('\n').Single(l => l.StartsWith("CS2201,") && l.Contains(Iso(first)));
        var moved = export.Replace(line, line.Replace("08:00-09:30", "10:00-11:30"));
        Assert.NotEqual(export, moved);
        using (var file = File(moved))
        {
            var effect = Assert.Single((await Json(await w.Rsa.PostAsync($"/api/admin/semesters/{w.Semester}/upload/preview", file))).GetProperty("teacherChanges").EnumerateArray());
            Assert.Equal(("dropped", "CS2201", "1", TeachWorld.Teacher2Name), (effect.GetProperty("effect").GetString(), effect.GetProperty("code").GetString(),
                effect.GetProperty("section").GetString(), effect.GetProperty("by").GetString()));
            Assert.StartsWith("Cancelled ", effect.GetProperty("what").GetString());
        }
        Assert.Equal(2, (await w.Live()).Count);
        using (var file = File(moved))
            Assert.Equal(1, (await Json(await w.Rsa.PostAsync($"/api/admin/semesters/{w.Semester}/upload/apply", file))).GetProperty("teacherChangesDropped").GetInt32());
        Assert.Equal("room", Assert.Single(await w.Live()).Kind);
        // An apply that drops nothing says so.
        w.Time.Advance(TimeSpan.FromMinutes(1));
        using (var file = File(moved))
            Assert.Equal(0, (await Json(await w.Rsa.PostAsync($"/api/admin/semesters/{w.Semester}/upload/apply", file))).GetProperty("teacherChangesDropped").GetInt32());

        // A sync that removes the second session: the same list in its preview, the same count when applied.
        var token = (await Json(await w.Owner.PostAsJsonAsync("/api/admin/sync-tokens", new { name = "Laptop" }))).GetProperty("token").GetString()!;
        var ext = w.Browser();
        ext.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // The whole timetable as EduWave would send it, with that one session at another time.
        var classes = await w.Db(db => db.Sections.AsNoTracking().Where(x => x.Course.SemesterId == w.Semester).Include(x => x.Course).Include(x => x.Meetings).ToListAsync());
        var sent = await ext.PostAsJsonAsync("/api/sync/timetable", new
        {
            semesterId = w.Semester,
            rows = classes.SelectMany(x => x.Meetings.Select(m => new
            {
                code = x.Course.Code, name = "", section = x.Number, instructor = x.Instructor, date = Iso(m.Date!.Value),
                start = x.Id == cs && m.Date == second ? "13:15" : m.Start.ToString("HH:mm"), end = x.Id == cs && m.Date == second ? "14:45" : m.End.ToString("HH:mm"), room = m.Room,
            })).ToList(),
            failed = Array.Empty<string>(),
        });
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        var pending = (await Json(await w.Rsa.GetAsync($"/api/admin/semesters/{w.Semester}/sync"))).GetProperty("pending");
        var listed = Assert.Single(pending.GetProperty("preview").GetProperty("teacherChanges").EnumerateArray());
        Assert.Equal("dropped", listed.GetProperty("effect").GetString());
        Assert.StartsWith("Room C201 for ", listed.GetProperty("what").GetString());
        w.Time.Advance(TimeSpan.FromMinutes(1));
        var applied = await Json(await w.Rsa.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/sync/apply", new { id = pending.GetProperty("id").GetInt32() }));
        Assert.Equal(1, applied.GetProperty("teacherChangesDropped").GetInt32());
        Assert.Empty(await w.Live());
        Assert.Equal(2, (await w.Notices(TeachNotice.ToTeacher)).Count(n => n.Kind == "dropped" && n.TeacherId == w.Teacher2Id));
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task A_hand_edit_of_a_section_follows_the_same_rules_as_a_sync()
    {
        using var w = await TeachWorld.Create();
        var cs = await w.Section("CS2201", "1");
        var (first, second) = (w.Date(0, 1), w.Date(1, 1));
        await w.Teacher2.Made(new { kind = "cancel", sectionId = cs, date = Iso(first), start = "08:00" });
        await w.Teacher2.Made(new { kind = "room", sectionId = cs, date = Iso(second), start = "08:00", room = "C201" });
        object Meetings(string firstStart, string firstEnd, string room) => new[]
        {
            new { day = 1, start = firstStart, end = firstEnd, room, date = Iso(first) },
            new { day = 1, start = "08:00", end = "09:30", room, date = Iso(second) },
        };

        // The course editor warns before saving: it lists the live changes of the course's sections.
        var course = await Json(await w.Rsa.GetAsync($"/api/admin/courses/{await w.CourseOf(cs)}"));
        Assert.Equal([("cancel", Iso(first), "08:00", TeachWorld.Teacher2Name), ("room", Iso(second), "08:00", TeachWorld.Teacher2Name)],
            course.GetProperty("teacherChanges").EnumerateArray().Select(c => (c.GetProperty("kind").GetString(), c.GetProperty("date").GetString(),
                c.GetProperty("start").GetString(), c.GetProperty("by").GetString())));
        Assert.All(course.GetProperty("teacherChanges").EnumerateArray(), c => Assert.Equal(cs, c.GetProperty("sectionId").GetInt32()));

        // RSA puts both sessions in another room: both changes stay; the teacher is told about her room change only,
        // and nobody is told the section changed (students see what they saw).
        await Json(await w.Rsa.PutAsJsonAsync($"/api/admin/sections/{cs}", new { instructor = TeachWorld.Teacher2Name, meetings = Meetings("08:00", "09:30", "B110") }));
        Assert.Equal(2, (await w.Live()).Count);
        Assert.Equal("room-differs", Assert.Single(await w.Notices(TeachNotice.ToTeacher), n => n.TeacherId == w.Teacher2Id).Kind);
        Assert.Null(await w.Db(db => db.Sections.Where(s => s.Id == cs).Select(s => s.ChangedAt).SingleAsync()));
        // What students see: the first session cancelled, the second in the teacher's room.
        var seen = Sessions(await Json(await w.Students[0].GetAsync("/api/me/timetable")), first, second).Where(s => s.Code == "CS2201").ToList();
        Assert.Equal([(second, "C201")], seen.Select(s => (s.Date, s.Room)));

        // RSA moves the first session to another time: the cancel is dropped and the teacher is told.
        await Json(await w.Rsa.PutAsJsonAsync($"/api/admin/sections/{cs}", new { instructor = TeachWorld.Teacher2Name, meetings = Meetings("10:00", "11:30", "B110") }));
        var rows = await w.Rows();
        Assert.Equal(("dropped-moved", "system"), (rows[0].EndReason, rows[0].EndedBy));
        Assert.Null(rows[1].EndedAt);
        Assert.Single(await w.Notices(TeachNotice.ToTeacher), n => n.Kind == "dropped" && n.TeacherId == w.Teacher2Id);
        Assert.NotNull(await w.Db(db => db.Sections.Where(s => s.Id == cs).Select(s => s.ChangedAt).SingleAsync()));
        Assert.Single((await Json(await w.Rsa.GetAsync($"/api/admin/courses/{await w.CourseOf(cs)}"))).GetProperty("teacherChanges").EnumerateArray());

        // RSA gives the section to the other teacher: the room change stays, and both are told.
        await Json(await w.Rsa.PutAsJsonAsync($"/api/admin/sections/{cs}", new { instructor = TeachWorld.Teacher1Name, meetings = Meetings("10:00", "11:30", "B110") }));
        Assert.Single(await w.Live());
        var reassigned = (await w.Notices(TeachNotice.ToTeacher)).Where(n => n.Kind == "reassigned").ToList();
        Assert.Equal([w.Teacher1Id, w.Teacher2Id], reassigned.Select(n => n.TeacherId!.Value).Order());
        // The new teacher can undo it from the class.
        var fresh = await w.Fresh(TeachWorld.Teacher1Email);
        Assert.Equal(HttpStatusCode.OK, (await fresh.Undo(rows[1].GroupId)).StatusCode);
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task A_day_off_and_a_weeks_edit_end_the_changes_left_without_a_session()
    {
        using var w = await TeachWorld.Create();
        var cs = await w.Section("CS2201", "1");
        // A weekly Thursday tutorial, 13:15 in B105: the weeks after the dated classes have only this.
        await w.AddMeeting(cs, null, 4, "13:15", "14:45", "B105");
        var (third, fourth, fifth) = (w.Date(2, 4), w.Date(3, 4), w.Date(4, 4));
        // An announced move, a cancel, and a room change on three Thursdays; and a move students were never told about.
        var moved = await w.Teacher2.Made(new { kind = "move", sectionId = cs, date = Iso(third), start = "13:15", newDate = Iso(third.AddDays(-1)), newStart = "10:00", room = "B202" });
        await w.MarkSent();
        var cancelled = await w.Teacher2.Made(new { kind = "cancel", sectionId = cs, date = Iso(fourth), start = "13:15" });
        var roomed = await w.Teacher2.Made(new { kind = "room", sectionId = cs, date = Iso(fifth), start = "13:15", room = "C201" });
        // A dated class keeps its date whatever is off, so a change to one is never touched.
        var dated = await w.Teacher2.Made(new { kind = "cancel", sectionId = cs, date = Iso(w.Date(1, 1)), start = "08:00" });
        await w.Db(db => db.TeachNotices.Where(n => n.Audience == TeachNotice.ToTeacher).ExecuteDeleteAsync());

        // The third Thursday becomes a day off (RSA): the move ends, the teacher is told, and so are the students who
        // were told the class had moved.
        await Json(await w.Rsa.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/days-off", new { date = Iso(third), name = "National Day" }));
        var rows = await w.Rows();
        var dropped = rows.Single(c => c.GroupId == moved);
        Assert.Equal(("dropped-day-off", "system"), (dropped.EndReason, dropped.EndedBy));
        Assert.Equal(3, (await w.Live()).Count);
        var teacher = Assert.Single(await w.Notices(TeachNotice.ToTeacher));
        Assert.Equal(("dropped", w.Teacher2Id, "Your change to CS2201 was dropped", $"{Day(third)} is now a day off."), (teacher.Kind, teacher.TeacherId, teacher.Title, teacher.Body));
        var students = (await w.Notices(TeachNotice.Students)).Last();
        Assert.Equal(("dropped", cs, dropped.Tag), (students.Kind, students.SectionId!.Value, students.Tag));
        Assert.Equal($"CS2201: the class moved to {Day(third.AddDays(-1))} 10:00 is off", students.Title);
        Assert.Equal($"{Day(third)} is now a day off.", students.Body);
        Assert.Equal(OmanTime(third.AddDays(-1), 10).ToUniversalTime(), students.ExpiresAt);
        // Nothing of the moved class is left on a student's timetable that week.
        var timetable = await Json(await w.Students[0].GetAsync("/api/me/timetable"));
        Assert.DoesNotContain(Sessions(timetable, third.AddDays(-4), third.AddDays(2)), s => s.Code == "CS2201");

        // Adding the same day again, or another day nothing is on, ends nothing more.
        await Json(await w.Rsa.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/days-off", new { date = Iso(third), name = "National Day" }));
        await Json(await w.Rsa.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/days-off", new { date = Iso(fourth.AddDays(1)), name = "Friday" }));
        Assert.Equal(3, (await w.Live()).Count);
        Assert.Single(await w.Notices(TeachNotice.ToTeacher));

        // The teaching weeks now end before the fourth Thursday: the cancel and the room change end; the teacher is
        // told about each, and students about neither (the class doesn't meet then either way).
        await w.MarkSent();
        var before = (await w.Notices(TeachNotice.Students)).Count;
        await Json(await w.Owner.PutAsJsonAsync($"/api/admin/semesters/{w.Semester}/weeks", new { week1Start = Iso(w.FirstSunday.AddDays(-28)), lastWeek = 7 }));
        rows = await w.Rows();
        Assert.All(rows.Where(c => c.GroupId == cancelled || c.GroupId == roomed), c => Assert.Equal(("dropped-moved", "system"), (c.EndReason, c.EndedBy)));
        Assert.Equal(dated, Assert.Single(await w.Live()).GroupId);
        var told = (await w.Notices(TeachNotice.ToTeacher)).Skip(1).ToList();
        Assert.Equal([$"{Day(fourth)} is no longer in the teaching weeks.", $"{Day(fifth)} is no longer in the teaching weeks."], told.Select(n => n.Body));
        Assert.All(told, n => Assert.Equal(("dropped", w.Teacher2Id), (n.Kind, n.TeacherId)));
        Assert.Equal(before, (await w.Notices(TeachNotice.Students)).Count);

        // Widening the weeks again ends nothing, and brings nothing back.
        await Json(await w.Owner.PutAsJsonAsync($"/api/admin/semesters/{w.Semester}/weeks", new { week1Start = Iso(w.FirstSunday.AddDays(-28)), lastWeek = 12 }));
        Assert.Single(await w.Live());
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task A_move_students_were_never_told_about_is_dropped_by_a_day_off_without_telling_them()
    {
        using var w = await TeachWorld.Create();
        var cs = await w.Section("CS2201", "1");
        await w.AddMeeting(cs, null, 4, "13:15", "14:45", "B105");
        var third = w.Date(2, 4);
        await w.Teacher2.Made(new { kind = "move", sectionId = cs, date = Iso(third), start = "13:15", newDate = Iso(third.AddDays(-1)), newStart = "10:00", room = "B202" });
        await Json(await w.Owner.PostAsJsonAsync($"/api/admin/semesters/{w.Semester}/days-off", new { date = Iso(third), name = "Holiday" }));
        Assert.Empty(await w.Live());
        // The unsent "moved" notice went with the change, and nothing takes its place.
        Assert.Empty(await w.Notices(TeachNotice.Students));
        Assert.Single(await w.Notices(TeachNotice.ToTeacher), n => n.Kind == "dropped");
    }
}
