using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;

namespace UniSchedule.Tests;

public class PlannerTests
{
    static async Task<JsonElement> Json(HttpResponseMessage response) {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }
    sealed class World : IDisposable
    {
        public ApiFactory Factory { get; } = new();
        public HttpClient Owner = null!, Rep = null!, OtherRep = null!, Student = null!, OtherSection = null!;
        public int Semester, Major, OtherMajor, Course, Section, Section2, StudentId, RepId, Workspace;
        public string Base => $"/api/rep/workspaces/{Workspace}";
        public void Dispose() => Factory.Dispose();
        public async Task Init() {
            await Factory.AddAdmin("planner-owner", "planner-password-1", AdminRole.Owner, false);
            Owner = Factory.Client(); await Owner.PostAsJsonAsync("/api/auth/admin-login", new { username = "planner-owner", password = "planner-password-1" });
            Rep = await Login("24-9201"); OtherRep = await Login("24-9202"); Student = await Login("24-9203"); OtherSection = await Login("24-9204");
            using (var scope = Factory.Services.CreateScope()) {
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var semester = new Semester { Name = "Fall 2026", Term = "Fall", Year = 2026, IsCurrent = true, PublishedAt = DateTimeOffset.UtcNow, Week1Start = new(2026, 9, 20), LastWeek = 18 };
                var major = new Major { Code = "CS", Name = "Computer Science", Family = "COMP" };
                var otherMajor = new Major { Code = "LOG", Name = "Logistics", Family = "BUS" };
                db.AddRange(semester, major, otherMajor); await db.SaveChangesAsync();
                Semester = semester.Id; Major = major.Id; OtherMajor = otherMajor.Id;
                var course = new Course { SemesterId = Semester, Code = "LOG201", Name = "Shared scheduling course", Type = CourseType.Core, Year = 2 };
                course.Majors.Add(new() { MajorId = otherMajor.Id });
                course.Sections.Add(new() { Number = "1", Meetings = [new() { Day = 0, Start = new(8, 0), End = new(9, 30), Room = "B105" }] });
                course.Sections.Add(new() { Number = "2", Meetings = [new() { Day = 1, Start = new(8, 0), End = new(9, 30), Room = "B106" }] });
                db.Courses.Add(course); await db.SaveChangesAsync(); Course = course.Id; Section = course.Sections[0].Id; Section2 = course.Sections[1].Id;
                foreach (var s in await db.Students.ToListAsync()) {
                    s.MajorId = s.UniversityId == "24-9203" ? OtherMajor : Major; s.Year = s.UniversityId == "24-9203" ? 4 : 2;
                    s.SetupSemesterId = Semester; s.SetupStep = "done"; s.SetupCompletedAt = DateTimeOffset.UtcNow;
                    db.Picks.Add(new() { StudentId = s.Id, CourseId = Course, SectionId = s.UniversityId == "24-9204" ? Section2 : Section });
                    if (s.UniversityId == "24-9201") RepId = s.Id;
                    if (s.UniversityId == "24-9203") StudentId = s.Id;
                }
                await db.SaveChangesAsync();
            }
            var list = (await Json(await Owner.GetAsync("/api/admin/students"))).GetProperty("students");
            foreach (var s in list.EnumerateArray().Where(s => s.GetProperty("universityId").GetString() is "24-9201" or "24-9202"))
                Assert.Equal(HttpStatusCode.OK, (await Owner.PutAsJsonAsync($"/api/admin/students/{s.GetProperty("id").GetInt32()}/batch-rep", new { homeMajorId = Major })).StatusCode);
            await Owner.PutAsJsonAsync("/api/admin/planner/settings", new { enabled = true });
            Workspace = (await Json(await Rep.PostAsJsonAsync("/api/rep/workspaces/open", new { semesterId = Semester }))).GetProperty("id").GetInt32();
        }
        async Task<HttpClient> Login(string id) {
            var c = Factory.Client(); var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
            await Json(await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })); return c;
        }
        public EventSpec Exam(string date = "2026-11-08", string start = "10:00", string end = "12:00", bool sectionOnly = false) => new("exam", "add", "Midterm", date, start, end, "Exam hall", [new(Course, sectionOnly ? [Section] : [])]);
        public async Task<JsonElement> Draft(EventSpec spec, int? eventId = null, int baseEventVersion = 0) => await Json(await Rep.PostAsJsonAsync($"{Base}/proposals", new { spec, eventId, baseEventVersion }));
        public async Task<JsonElement> Analysis(JsonElement p) => await Json(await Rep.PostAsync($"{Base}/proposals/{p.GetProperty("id").GetInt32()}/analyze", null));
        public async Task<JsonElement> Publish(JsonElement p, string explanation = "Lecturer confirmed this date.") {
            var analysis = await Analysis(p);
            return await Json(await Rep.PostAsJsonAsync($"{Base}/proposals/{p.GetProperty("id").GetInt32()}/publish", new { version = p.GetProperty("version").GetInt32(), analysisToken = analysis.GetProperty("token").GetString(), explanation }));
        }
    }

    [Fact]
    public async Task Roles_are_owner_managed_and_family_workspaces_share_any_major() {
        using var w = new World(); await w.Init();
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Student.GetAsync("/api/rep/bootstrap")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rep.PutAsJsonAsync($"/api/admin/students/{w.StudentId}/batch-rep", new { homeMajorId = w.Major })).StatusCode);
        var other = await Json(await w.OtherRep.PostAsJsonAsync("/api/rep/workspaces/open", new { semesterId = w.Semester, family = "family:BUS" }));
        Assert.Equal(w.Workspace, other.GetProperty("id").GetInt32());
        var batch = await Json(await w.Rep.PostAsJsonAsync($"{w.Base}/batches", new { name = "Logistics in COMP workspace", majorId = w.OtherMajor, year = 2, targets = new[] { new PlannerTarget(w.Course, []) } }));
        Assert.Equal(1, (await Json(await w.OtherRep.GetAsync(w.Base))).GetProperty("batches").GetArrayLength());
        Assert.Equal(w.Course, batch.GetProperty("targets")[0].GetProperty("courseId").GetInt32());
        await w.Owner.DeleteAsync($"/api/admin/students/{w.RepId}/batch-rep");
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rep.GetAsync(w.Base)).StatusCode);
    }

    [Fact]
    public async Task Feature_flag_hides_published_events_and_drafts_never_reach_students() {
        using var w = new World(); await w.Init();
        var p = await w.Draft(w.Exam(sectionOnly: true));
        Assert.Equal(0, (await Json(await w.Student.GetAsync("/api/me/timetable"))).GetProperty("plannerEvents").GetArrayLength());
        await w.Publish(p);
        var visible = (await Json(await w.Student.GetAsync("/api/me/timetable"))).GetProperty("plannerEvents");
        Assert.Equal(1, visible.GetArrayLength()); // Different major and year still receives its enrolled course's exam.
        Assert.Equal(0, (await Json(await w.OtherSection.GetAsync("/api/me/timetable"))).GetProperty("plannerEvents").GetArrayLength());
        await w.Owner.PutAsJsonAsync("/api/admin/planner/settings", new { enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rep.GetAsync(w.Base)).StatusCode);
        Assert.Equal(0, (await Json(await w.Student.GetAsync("/api/me/timetable"))).GetProperty("plannerEvents").GetArrayLength());
    }

    [Fact]
    public async Task Published_edits_are_separate_revisions_and_stale_changes_are_rejected() {
        using var w = new World(); await w.Init();
        var draft = await w.Draft(w.Exam()); var published = await w.Publish(draft);
        var eid = published.GetProperty("id").GetInt32();
        var a = await w.Draft(w.Exam("2026-11-09"), eid, 1); var b = await w.Draft(w.Exam("2026-11-10"), eid, 1);
        Assert.Equal("2026-11-08", (await Json(await w.Student.GetAsync("/api/me/timetable"))).GetProperty("plannerEvents")[0].GetProperty("date").GetString());
        var pId = a.GetProperty("id").GetInt32();
        var updated = await Json(await w.OtherRep.PutAsJsonAsync($"{w.Base}/proposals/{pId}", new { spec = w.Exam("2026-11-11"), version = 1 }));
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rep.PutAsJsonAsync($"{w.Base}/proposals/{pId}", new { spec = w.Exam(), version = 1 })).StatusCode);
        await w.Publish(updated);
        var analysis = await w.Analysis(b);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rep.PostAsJsonAsync($"{w.Base}/proposals/{b.GetProperty("id").GetInt32()}/publish", new { version = 1, analysisToken = analysis.GetProperty("token").GetString(), explanation = "Reviewed" })).StatusCode);
        Assert.Equal("2026-11-11", (await Json(await w.Student.GetAsync("/api/me/timetable"))).GetProperty("plannerEvents")[0].GetProperty("date").GetString());
    }

    [Fact]
    public async Task Conflict_acknowledgment_and_current_analysis_are_required() {
        using var w = new World(); await w.Init();
        var draft = await w.Draft(w.Exam(start: "08:15", end: "09:00"));
        var analysis = await w.Analysis(draft);
        Assert.NotEmpty(analysis.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.PostAsJsonAsync($"{w.Base}/proposals/{draft.GetProperty("id").GetInt32()}/publish", new { version = 1, analysisToken = analysis.GetProperty("token").GetString() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rep.PostAsJsonAsync($"{w.Base}/proposals/{draft.GetProperty("id").GetInt32()}/publish", new { version = 1, analysisToken = "old", explanation = "Reviewed" })).StatusCode);
        await w.Publish(draft);
        var second = await w.Draft(w.Exam(start: "14:00", end: "16:00"));
        Assert.Contains((await w.Analysis(second)).GetProperty("conflicts").EnumerateArray(), c => c.GetProperty("kind").GetString() == "daily-load");
    }

    [Fact]
    public async Task Class_changes_preserve_imports_withdraw_cleanly_and_become_stale_after_import_edits() {
        using var w = new World(); await w.Init();
        var calendar = await Json(await w.Rep.GetAsync($"{w.Base}/calendar?from=2026-11-08&to=2026-11-14"));
        var imported = calendar.EnumerateArray().First(e => e.GetProperty("source").GetProperty("sectionId").GetInt32() == w.Section);
        var source = imported.GetProperty("source").Deserialize<PlannerSource>(PlannerJson.Options)!;
        var spec = new EventSpec("class", "replace", "Replacement lecture", "2026-11-09", "13:00", "14:30", "B200", [new(w.Course, [w.Section])], "2026-11-23", source);
        var p = await w.Draft(spec); var e = await w.Publish(p);
        var student = await Json(await w.Student.GetAsync("/api/me/timetable"));
        Assert.Equal(3, student.GetProperty("plannerEvents").GetArrayLength());
        Assert.Equal("2026-11-08", student.GetProperty("classes")[0].GetProperty("section").GetProperty("meetings")[0].GetProperty("excludedDates")[0].GetString());
        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>(); var meeting = await db.Meetings.FirstAsync(m => m.SectionId == w.Section); Assert.Equal(new TimeOnly(8, 0), meeting.Start); meeting.Room = "Updated by university"; await db.SaveChangesAsync();
        }
        student = await Json(await w.Student.GetAsync("/api/me/timetable"));
        Assert.Equal(0, student.GetProperty("plannerEvents").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, student.GetProperty("classes")[0].GetProperty("section").GetProperty("meetings")[0].GetProperty("excludedDates").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.PostAsJsonAsync($"{w.Base}/proposals", new { spec })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rep.PostAsJsonAsync($"{w.Base}/events/{e.GetProperty("id").GetInt32()}/withdraw", new { version = 1, explanation = "University timetable changed" })).StatusCode);
    }

    [Fact]
    public async Task A_moved_class_moves_in_the_calendar_feed_reminders_and_group_free_time() {
        using var w = new World(); await w.Init();
        var calendar = await Json(await w.Rep.GetAsync($"{w.Base}/calendar?from=2026-11-08&to=2026-11-14"));
        var source = calendar.EnumerateArray().First(e => e.GetProperty("source").GetProperty("sectionId").GetInt32() == w.Section)
            .GetProperty("source").Deserialize<PlannerSource>(PlannerJson.Options)!;
        // Sunday 8 Nov 08:00 moves to Monday 9 Nov 13:00.
        var e = await w.Publish(await w.Draft(new EventSpec("class", "replace", "Replacement lecture", "2026-11-09", "13:00", "14:30", "B200", [new(w.Course, [w.Section])], null, source)));
        var eventId = e.GetProperty("id").GetInt32();

        // Calendar feed: that Sunday is left out and the Monday is added.
        var links = await Json(await w.Student.GetAsync("/api/me/calendar"));
        var ics = await (await w.Factory.Client().GetAsync(new Uri(links.GetProperty("url").GetString()!).PathAndQuery)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("DTSTART;TZID=Asia/Muscat:20261108T080000\r\n", ics);
        Assert.Contains("DTSTART;TZID=Asia/Muscat:20261115T080000\r\n", ics);
        Assert.Contains($"UID:planner-{eventId}-20261109@classy", ics);
        var text = await (await w.Factory.Client().GetAsync(new Uri(links.GetProperty("textUrl").GetString()!).PathAndQuery + "?from=2026-11-08&days=2")).Content.ReadAsStringAsync();
        Assert.Contains("Sun 8 Nov 2026 · week 8\n- No classes", text.Replace("\r\n", "\n"));
        Assert.Contains("13:00–14:30  LOG201 Replacement lecture", text);

        // Reminders: none for the moved Sunday class, one before the Monday one.
        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.Students.Where(s => s.Id == w.StudentId).ExecuteUpdateAsync(u => u.SetProperty(s => s.RemindMinutes, 15));
            db.PushSubscriptions.Add(new PushSubscription { StudentId = w.StudentId, Endpoint = "https://fcm.googleapis.com/fcm/send/moved", P256dh = "k", Auth = "a", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            DateTimeOffset At(int day, int h, int m, int sec = 0) => new(2026, 11, day, h, m, sec, TimeSpan.FromHours(4));
            Assert.DoesNotContain(await UniSchedule.Api.Students.Notifications.DueReminders(db, At(8, 7, 44, 50), At(8, 7, 45, 20), default, planner: true), d => d.StudentId == w.StudentId);
            var due = Assert.Single(await UniSchedule.Api.Students.Notifications.DueReminders(db, At(9, 12, 44, 50), At(9, 12, 45, 20), default, planner: true), d => d.StudentId == w.StudentId);
            Assert.Equal("LOG201 in 15 min", due.Note.Title);
            // Opens the planner event itself, which the website finds under p{event id}.
            Assert.Equal($"/?session=p{eventId}.2026-11-09.13:00", due.Note.Url);
        }

        // Group free time: free that Sunday morning, busy on the Monday afternoon.
        var group = (await Json(await w.Student.PostAsJsonAsync("/api/me/groups", new { name = "Study", kind = "friends", displayName = "Me" }))).GetProperty("id").GetInt32();
        var week = (await Json(await w.Student.GetAsync($"/api/me/groups/{group}/week?start=2026-11-08"))).GetProperty("days");
        var sunday = week.EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-11-08");
        Assert.Equal("08:00", sunday.GetProperty("free")[0].GetProperty("start").GetString());
        var monday = week.EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-11-09");
        Assert.DoesNotContain(monday.GetProperty("free").EnumerateArray(), f => string.CompareOrdinal(f.GetProperty("start").GetString(), "13:00") <= 0 && string.CompareOrdinal(f.GetProperty("end").GetString(), "14:30") >= 0);
    }

    [Fact]
    public async Task A_whole_course_exam_reaches_the_feeds_reminders_and_groups_of_a_pick_without_a_section() {
        using var w = new World(); await w.Init();
        var e = await w.Publish(await w.Draft(w.Exam("2026-11-10", "10:00", "12:00")));
        var eventId = e.GetProperty("id").GetInt32();
        // An upload removed the student's section: they still take the course, and its exam is still theirs.
        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.Picks.Where(p => p.StudentId == w.StudentId).ExecuteUpdateAsync(u => u.SetProperty(p => p.SectionId, (int?)null).SetProperty(p => p.SectionRemoved, true));
        }
        var timetable = await Json(await w.Student.GetAsync("/api/me/timetable"));
        Assert.Equal(1, timetable.GetProperty("needsSection").GetArrayLength());
        Assert.Equal(1, timetable.GetProperty("plannerEvents").GetArrayLength());

        var links = await Json(await w.Student.GetAsync("/api/me/calendar"));
        var ics = await (await w.Factory.Client().GetAsync(new Uri(links.GetProperty("url").GetString()!).PathAndQuery)).Content.ReadAsStringAsync();
        Assert.Contains($"UID:planner-{eventId}-20261110@classy", ics);
        var text = await (await w.Factory.Client().GetAsync(new Uri(links.GetProperty("textUrl").GetString()!).PathAndQuery + "?from=2026-11-10&days=1")).Content.ReadAsStringAsync();
        Assert.Contains("10:00–12:00  LOG201 Midterm", text);

        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.Students.Where(s => s.Id == w.StudentId).ExecuteUpdateAsync(u => u.SetProperty(s => s.RemindMinutes, 15));
            db.PushSubscriptions.Add(new PushSubscription { StudentId = w.StudentId, Endpoint = "https://fcm.googleapis.com/fcm/send/sectionless", P256dh = "k", Auth = "a", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            DateTimeOffset At(int h, int m, int sec = 0) => new(2026, 11, 10, h, m, sec, TimeSpan.FromHours(4));
            Assert.Single(await UniSchedule.Api.Students.Notifications.DueReminders(db, At(9, 44, 50), At(9, 45, 20), default, planner: true), d => d.StudentId == w.StudentId);
        }

        var group = (await Json(await w.Student.PostAsJsonAsync("/api/me/groups", new { name = "Study", kind = "friends", displayName = "Me" }))).GetProperty("id").GetInt32();
        var tuesday = (await Json(await w.Student.GetAsync($"/api/me/groups/{group}/week?start=2026-11-08"))).GetProperty("days")
            .EnumerateArray().Single(d => d.GetProperty("date").GetString() == "2026-11-10");
        Assert.DoesNotContain(tuesday.GetProperty("free").EnumerateArray(), f => string.CompareOrdinal(f.GetProperty("start").GetString(), "10:00") <= 0 && string.CompareOrdinal(f.GetProperty("end").GetString(), "12:00") >= 0);
    }

    [Fact]
    public async Task Free_rooms_follow_the_teaching_weeks_and_published_changes() {
        using var w = new World(); await w.Init();
        async Task<Dictionary<string, string?>> Free(string date, string at) =>
            (await Json(await w.Student.GetAsync($"/api/rooms/free?date={date}&at={at}"))).GetProperty("rooms").EnumerateArray()
                .ToDictionary(x => x.GetProperty("room").GetString()!, x => x.GetProperty("freeUntil").GetString());
        // Section 1 meets Sundays 08:00 in B105 from 20 Sep: in class in term, free before week 1.
        Assert.False((await Free("2026-11-01", "08:30")).ContainsKey("B105"));
        Assert.True((await Free("2026-09-13", "08:30")).ContainsKey("B105"));

        var calendar = await Json(await w.Rep.GetAsync($"{w.Base}/calendar?from=2026-11-08&to=2026-11-15"));
        PlannerSource Source(string date) => calendar.EnumerateArray().First(e => e.GetProperty("date").GetString() == date && e.GetProperty("source").GetProperty("sectionId").GetInt32() == w.Section)
            .GetProperty("source").Deserialize<PlannerSource>(PlannerJson.Options)!;
        // Cancelled on 8 Nov: B105 is free.
        await w.Publish(await w.Draft(new EventSpec("class", "cancel", "No lecture", "2026-11-08", "08:00", "09:30", "B105", [new(w.Course, [w.Section])], null, Source("2026-11-08"))));
        Assert.True((await Free("2026-11-08", "08:30")).ContainsKey("B105"));
        // Moved from 15 Nov to Mon 16 Nov 13:00 in B106: B105 is free that Sunday, B106 busy then and free until 13:00 before.
        await w.Publish(await w.Draft(new EventSpec("class", "replace", "Moved lecture", "2026-11-16", "13:00", "14:30", "B106", [new(w.Course, [w.Section])], null, Source("2026-11-15"))));
        Assert.True((await Free("2026-11-15", "08:30")).ContainsKey("B105"));
        Assert.False((await Free("2026-11-16", "13:30")).ContainsKey("B106"));
        Assert.Equal("13:00", (await Free("2026-11-16", "10:00"))["B106"]);
        // An exam in B105 on Tue 17 Nov blocks it.
        await w.Publish(await w.Draft(new EventSpec("exam", "add", "Midterm", "2026-11-17", "10:00", "12:00", "B105", [new(w.Course, [])])));
        Assert.False((await Free("2026-11-17", "10:30")).ContainsKey("B105"));
        Assert.Equal("10:00", (await Free("2026-11-17", "09:00"))["B105"]);

        // With the planner off, students' timetables ignore its changes, and so do free rooms.
        await w.Owner.PutAsJsonAsync("/api/admin/planner/settings", new { enabled = false });
        Assert.False((await Free("2026-11-08", "08:30")).ContainsKey("B105"));
        Assert.Null((await Free("2026-11-17", "10:30"))["B105"]);
    }

    [Fact]
    public async Task Absences_and_session_notes_follow_published_changes() {
        using var w = new World(); await w.Init();
        var calendar = await Json(await w.Rep.GetAsync($"{w.Base}/calendar?from=2026-11-08&to=2026-11-15"));
        PlannerSource Source(string date) => calendar.EnumerateArray().First(e => e.GetProperty("date").GetString() == date && e.GetProperty("source").GetProperty("sectionId").GetInt32() == w.Section)
            .GetProperty("source").Deserialize<PlannerSource>(PlannerJson.Options)!;
        // Section 1 meets Sundays 08:00. Cancelled on 8 Nov, moved from 15 Nov to Mon 16 Nov 13:00, a midterm on Tue 17 Nov.
        await w.Publish(await w.Draft(new EventSpec("class", "cancel", "No lecture", "2026-11-08", "08:00", "09:30", "B105", [new(w.Course, [w.Section])], null, Source("2026-11-08"))));
        await w.Publish(await w.Draft(new EventSpec("class", "replace", "Moved lecture", "2026-11-16", "13:00", "14:30", "B106", [new(w.Course, [w.Section])], null, Source("2026-11-15"))));
        await w.Publish(await w.Draft(new EventSpec("exam", "add", "Midterm", "2026-11-17", "10:00", "12:00", "B105", [new(w.Course, [])])));

        async Task<HttpStatusCode> Missed(string date, string start) =>
            (await w.Student.PutAsJsonAsync("/api/me/absences", new { courseId = w.Course, date, start, missed = true })).StatusCode;
        Assert.Equal(HttpStatusCode.NoContent, await Missed("2026-11-01", "08:00"));
        Assert.Equal(HttpStatusCode.NoContent, await Missed("2026-11-16", "13:00"));
        // Not the cancelled class, not the one that moved away, and not the exam: exams don't count.
        Assert.Equal(HttpStatusCode.BadRequest, await Missed("2026-11-08", "08:00"));
        Assert.Equal(HttpStatusCode.BadRequest, await Missed("2026-11-15", "08:00"));
        Assert.Equal(HttpStatusCode.BadRequest, await Missed("2026-11-17", "10:00"));

        // "This session" notes work on the moved class and the exam, on days the section doesn't otherwise meet.
        async Task<HttpStatusCode> Note(string date) =>
            (await w.Student.PostAsJsonAsync("/api/me/notes", new { courseId = w.Course, date, text = "Bring a calculator" })).StatusCode;
        Assert.Equal(HttpStatusCode.OK, await Note("2026-11-16"));
        Assert.Equal(HttpStatusCode.OK, await Note("2026-11-17"));
        Assert.Equal(HttpStatusCode.BadRequest, await Note("2026-11-18"));

        // With the planner off, those sessions are gone again (unmarking still works).
        await w.Owner.PutAsJsonAsync("/api/admin/planner/settings", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, (await w.Student.PutAsJsonAsync("/api/me/absences", new { courseId = w.Course, date = "2026-11-16", start = "13:00", missed = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, await Missed("2026-11-16", "13:00"));
        Assert.Equal(HttpStatusCode.BadRequest, await Note("2026-11-17"));
        Assert.Equal(HttpStatusCode.NoContent, await Missed("2026-11-08", "08:00"));
    }

    [Fact]
    public async Task Slot_finder_checks_every_recurring_occurrence_and_reports_missing_sections() {
        using var w = new World(); await w.Init();
        await w.Publish(await w.Draft(w.Exam("2026-11-15", "10:00", "11:30")));
        var search = new { targets = new[] { new PlannerTarget(w.Course, [w.Section]) }, kind = "class", from = "2026-11-08", to = "2026-11-08", duration = 90, weekdays = new[] { 0 }, start = "08:00", end = "12:00", repeatUntil = "2026-11-22", room = "" };
        var candidates = (await Json(await w.Rep.PostAsJsonAsync($"{w.Base}/slots", search))).GetProperty("candidates");
        Assert.NotEmpty(candidates.EnumerateArray());
        Assert.All(candidates.EnumerateArray(), c => Assert.NotEmpty(c.GetProperty("analysis").GetProperty("conflicts").EnumerateArray()));
        using (var scope = w.Factory.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<AppDb>(); await db.Meetings.Where(m => m.SectionId == w.Section).ExecuteDeleteAsync(); }
        var analysis = await w.Analysis(await w.Draft(w.Exam(sectionOnly: true)));
        Assert.NotEmpty(analysis.GetProperty("coverageWarnings").EnumerateArray());
    }

    [Fact]
    public async Task Another_familys_rep_cannot_edit_or_withdraw_a_published_event() {
        using var w = new World(); await w.Init();
        var e = await w.Publish(await w.Draft(w.Exam()));
        var eventId = e.GetProperty("id").GetInt32();
        // A Business-family rep, in their own workspace.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsJsonAsync($"/api/admin/students/{w.StudentId}/batch-rep", new { homeMajorId = w.OtherMajor })).StatusCode);
        var bus = (await Json(await w.Student.PostAsJsonAsync("/api/rep/workspaces/open", new { semesterId = w.Semester }))).GetProperty("id").GetInt32();
        Assert.NotEqual(w.Workspace, bus);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Student.PostAsJsonAsync($"/api/rep/workspaces/{bus}/events/{eventId}/withdraw", new { version = 1, explanation = "Not ours" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Student.PostAsJsonAsync($"/api/rep/workspaces/{bus}/proposals", new { spec = w.Exam("2026-11-09"), eventId, baseEventVersion = 1 })).StatusCode);
        // The family that published it still can.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rep.PostAsJsonAsync($"{w.Base}/events/{eventId}/withdraw", new { version = 1, explanation = "Moved by lecturer" })).StatusCode);
    }

    [Fact]
    public async Task Cross_family_workspaces_share_published_event_references_not_drafts() {
        using var w = new World(); await w.Init();
        var other = await Json(await w.Owner.PostAsJsonAsync("/api/rep/workspaces/open", new { semesterId = w.Semester, family = "family:BUS" }));
        var otherId = other.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rep.GetAsync($"/api/rep/workspaces/{otherId}")).StatusCode);
        var p = await w.Draft(w.Exam());
        Assert.Equal(0, (await Json(await w.Owner.GetAsync($"/api/rep/workspaces/{otherId}/calendar?from=2026-11-08&to=2026-11-08"))).EnumerateArray().Count(e => e.GetProperty("kind").GetString() == "exam"));
        var published = await w.Publish(p);
        var shared = (await Json(await w.Owner.GetAsync($"/api/rep/workspaces/{otherId}/calendar?from=2026-11-08&to=2026-11-08"))).EnumerateArray().Single(e => e.GetProperty("kind").GetString() == "exam");
        Assert.Equal(published.GetProperty("id").GetInt32(), shared.GetProperty("eventId").GetInt32());
    }

    [Fact]
    public async Task Invalid_targets_and_recurrences_are_rejected() {
        using var w = new World(); await w.Init();
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.PostAsJsonAsync($"{w.Base}/proposals", new { spec = w.Exam() with { Targets = [new(w.Course, [-100])] } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.PostAsJsonAsync($"{w.Base}/proposals", new { spec = w.Exam() with { End = "08:00" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.PostAsJsonAsync($"{w.Base}/proposals", new { spec = w.Exam() with { RepeatUntil = "2027-01-01" } })).StatusCode);
    }

    [Fact]
    public async Task AI_jobs_are_author_private_and_cancellation_erases_attachments() {
        using var w = new World(); await w.Init();
        string jobId;
        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var job = new PlannerAiJob { WorkspaceId = w.Workspace, StudentId = w.RepId, Status = "running", CreatedAt = DateTimeOffset.UtcNow,
                InputJson = PlannerJson.Write(new PlannerAiInput("Private lecturer screenshot", ["data:image/png;base64,aGVsbG8="])) };
            db.PlannerAiJobs.Add(job); await db.SaveChangesAsync(); jobId = job.Id;
        }
        Assert.Equal(HttpStatusCode.OK, (await w.Rep.GetAsync($"{w.Base}/ai-jobs/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.OtherRep.GetAsync($"{w.Base}/ai-jobs/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rep.DeleteAsync($"{w.Base}/ai-jobs/{jobId}")).StatusCode);
        using (var scope = w.Factory.Services.CreateScope()) {
            var job = await scope.ServiceProvider.GetRequiredService<AppDb>().PlannerAiJobs.FirstAsync(j => j.Id == jobId);
            Assert.Equal("cancelled", job.Status); Assert.Equal("{}", job.InputJson);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.PostAsJsonAsync($"{w.Base}/ai-jobs", new { text = "Read this", images = new string?[] { null } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.PostAsJsonAsync($"{w.Base}/ai-jobs", new { text = "Read this", images = new[] { "https://private.example/screenshot" } })).StatusCode);
    }

    [Fact]
    public async Task Workload_counts_one_canonical_exam_when_its_change_is_also_drafted() {
        using var w = new World(); await w.Init();
        await Json(await w.Rep.PutAsJsonAsync($"{w.Base}/settings", new { version = 1, dailyExamWarning = 3, weeklyExamWarning = 10 }));
        var published = await w.Publish(await w.Draft(w.Exam()));
        await w.Draft(w.Exam(start: "12:00", end: "13:00"), published.GetProperty("id").GetInt32(), 1);
        var analysis = await w.Analysis(await w.Draft(w.Exam(start: "14:00", end: "15:00")));
        Assert.DoesNotContain(analysis.GetProperty("conflicts").EnumerateArray(), c => c.GetProperty("kind").GetString() == "daily-load");
    }

    [Fact]
    public async Task Renaming_a_family_keeps_its_workspace_and_reps() {
        using var w = new World(); await w.Init();
        var e = await w.Publish(await w.Draft(w.Exam()));
        var eventId = e.GetProperty("id").GetInt32();
        await Json(await w.Rep.PostAsJsonAsync($"{w.Base}/batches", new { name = "Year 2", majorId = w.Major, year = 2, targets = new[] { new PlannerTarget(w.Course, []) } }));
        // CS is COMP's only major, so COMP is renamed.
        await Json(await w.Owner.PutAsJsonAsync($"/api/admin/majors/{w.Major}", new { code = "CS", name = "Computer Science", family = "COMPUTING" }));
        var opened = await Json(await w.Rep.PostAsJsonAsync("/api/rep/workspaces/open", new { semesterId = w.Semester }));
        Assert.Equal(w.Workspace, opened.GetProperty("id").GetInt32());
        Assert.Equal("family:COMPUTING", opened.GetProperty("family").GetString());
        Assert.Equal(1, (await Json(await w.Rep.GetAsync(w.Base))).GetProperty("batches").GetArrayLength());
        Assert.Equal("family:COMPUTING", (await Json(await w.Owner.GetAsync($"/api/admin/students/{w.RepId}/batch-rep"))).GetProperty("assignment").GetProperty("homeFamily").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rep.PostAsJsonAsync($"{w.Base}/events/{eventId}/withdraw", new { version = 1, explanation = "Moved by lecturer" })).StatusCode);
    }

    [Fact]
    public async Task Moving_a_familys_last_major_into_another_family_merges_the_workspaces() {
        using var w = new World(); await w.Init();
        var e = await w.Publish(await w.Draft(w.Exam()));
        var eventId = e.GetProperty("id").GetInt32();
        await Json(await w.Rep.PostAsJsonAsync($"{w.Base}/batches", new { name = "Year 2", majorId = w.Major, year = 2, targets = new[] { new PlannerTarget(w.Course, []) } }));
        var bus = (await Json(await w.Owner.PostAsJsonAsync("/api/rep/workspaces/open", new { semesterId = w.Semester, family = "family:BUS" }))).GetProperty("id").GetInt32();
        // CS leaves COMP (its only major) for BUS, where a workspace already exists.
        await Json(await w.Owner.PutAsJsonAsync($"/api/admin/majors/{w.Major}", new { code = "CS", name = "Computer Science", family = "BUS" }));
        var opened = await Json(await w.Rep.PostAsJsonAsync("/api/rep/workspaces/open", new { semesterId = w.Semester }));
        Assert.Equal(bus, opened.GetProperty("id").GetInt32());
        var ws = await Json(await w.Rep.GetAsync($"/api/rep/workspaces/{bus}"));
        Assert.Equal(1, ws.GetProperty("batches").GetArrayLength());
        Assert.Contains(ws.GetProperty("history").EnumerateArray(), a => a.GetProperty("action").GetString() == "published");
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.GetAsync(w.Base)).StatusCode);
        // The event it published from the old workspace is still the rep's to change.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rep.PostAsJsonAsync($"/api/rep/workspaces/{bus}/events/{eventId}/withdraw", new { version = 1, explanation = "Moved by lecturer" })).StatusCode);
    }
    async Task DayOff(World w, DateOnly date, string name = "National Day") {
        using var scope = w.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.DaysOff.Add(new() { SemesterId = w.Semester, Date = date, Name = name, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    static string[] Coverage(JsonElement analysis) => analysis.GetProperty("coverageWarnings").EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task A_published_cancellation_whose_class_changed_shows_in_the_rep_calendar_for_review() {
        using var w = new World(); await w.Init();
        async Task<JsonElement[]> Sunday() => (await Json(await w.Rep.GetAsync($"{w.Base}/calendar?from=2026-11-08&to=2026-11-08"))).EnumerateArray()
            .Where(e => e.GetProperty("targets")[0].GetProperty("sectionIds").EnumerateArray().Any(id => id.GetInt32() == w.Section)).ToArray();
        var source = (await Sunday()).Single().GetProperty("source").Deserialize<PlannerSource>(PlannerJson.Options)!;
        var e = await w.Publish(await w.Draft(new EventSpec("class", "cancel", "No lecture", "2026-11-08", "08:00", "09:30", "B105", [new(w.Course, [w.Section])], null, source)));
        Assert.Empty(await Sunday());
        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.Meetings.Where(m => m.SectionId == w.Section).ExecuteUpdateAsync(u => u.SetProperty(m => m.Room, "B999"));
        }
        // The class is back for students, and the rep is shown the lapsed cancellation to withdraw or redo.
        var lapsed = Assert.Single(await Sunday(), x => x.GetProperty("eventId").ValueKind == JsonValueKind.Number);
        Assert.Equal(e.GetProperty("id").GetInt32(), lapsed.GetProperty("eventId").GetInt32());
        Assert.True(lapsed.GetProperty("stale").GetBoolean());
        Assert.Equal("cancelled", lapsed.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Weekly_planner_events_skip_days_off_and_dates_after_the_teaching_weeks() {
        using var w = new World(); await w.Init();
        await DayOff(w, new(2026, 11, 16));
        // The semester's last teaching day is Sat 23 Jan 2027.
        var weekly = await w.Draft(new EventSpec("class", "add", "Extra class", "2026-11-09", "13:00", "14:30", "B200", [new(w.Course, [w.Section])], "2026-11-23"));
        Assert.Contains(Coverage(await w.Analysis(weekly)), x => x.Contains("2026-11-16"));
        await w.Publish(weekly);
        await w.Publish(await w.Draft(new EventSpec("class", "add", "Late class", "2027-01-18", "13:00", "14:30", "B200", [new(w.Course, [w.Section])], "2027-02-15")));
        // A make-up class put on the day off itself still happens, with a warning.
        var makeUp = await w.Draft(new EventSpec("class", "add", "Make-up", "2026-11-16", "16:00", "17:00", "B200", [new(w.Course, [w.Section])]));
        Assert.Contains("2026-11-16 is National Day, a day off.", Coverage(await w.Analysis(makeUp)));
        await w.Publish(makeUp);
        var dates = (await Json(await w.Student.GetAsync("/api/me/timetable"))).GetProperty("plannerEvents").EnumerateArray()
            .Select(e => $"{e.GetProperty("title").GetString()} {e.GetProperty("date").GetString()}").Order().ToArray();
        Assert.Equal(["Extra class 2026-11-09", "Extra class 2026-11-23", "Late class 2027-01-18", "Make-up 2026-11-16"], dates);
        var calendar = (await Json(await w.Rep.GetAsync($"{w.Base}/calendar?from=2026-11-15&to=2026-11-17"))).EnumerateArray();
        Assert.DoesNotContain(calendar, e => e.GetProperty("title").GetString() == "Extra class");
    }

    [Fact]
    public async Task A_weekly_move_follows_its_original_class_on_days_off() {
        using var w = new World(); await w.Init();
        // The Sunday class on 15 Nov is off, and so is the Tuesday it would move to on 24 Nov.
        await DayOff(w, new(2026, 11, 15)); await DayOff(w, new(2026, 11, 24), "Break");
        var source = (await Json(await w.Rep.GetAsync($"{w.Base}/calendar?from=2026-11-08&to=2026-11-08"))).EnumerateArray()
            .First(e => e.GetProperty("source").GetProperty("sectionId").GetInt32() == w.Section).GetProperty("source").Deserialize<PlannerSource>(PlannerJson.Options)!;
        var move = await w.Draft(new EventSpec("class", "replace", "Moved lecture", "2026-11-10", "13:00", "14:30", "B200", [new(w.Course, [w.Section])], "2026-11-24", source));
        var coverage = Coverage(await w.Analysis(move));
        Assert.Contains(coverage, x => x.StartsWith("The weekly repeat skips") && x.Contains("2026-11-17"));
        Assert.Contains("The moved class would fall on a day off or outside the teaching weeks on 2026-11-24, so the class doesn't happen those weeks.", coverage);
        await w.Publish(move);
        // No moved class the week its original is a day off, and none on the Tuesday off; that week's Sunday class stays moved away.
        var student = await Json(await w.Student.GetAsync("/api/me/timetable"));
        Assert.Equal(["2026-11-10"], student.GetProperty("plannerEvents").EnumerateArray().Select(e => e.GetProperty("date").GetString()!).ToArray());
        var excluded = student.GetProperty("classes")[0].GetProperty("section").GetProperty("meetings")[0].GetProperty("excludedDates").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("2026-11-22", excluded);
        var calendar = (await Json(await w.Rep.GetAsync($"{w.Base}/calendar?from=2026-11-15&to=2026-11-28"))).EnumerateArray()
            .Where(e => e.GetProperty("targets")[0].GetProperty("sectionIds").EnumerateArray().Any(id => id.GetInt32() == w.Section)).ToArray();
        Assert.Empty(calendar);
    }

    [Fact]
    public async Task Slot_finder_and_conflict_check_treat_days_off_as_days_off() {
        using var w = new World(); await w.Init();
        await DayOff(w, new(2026, 11, 22));
        var search = new { targets = new[] { new PlannerTarget(w.Course, [w.Section]) }, kind = "exam", from = "2026-11-15", to = "2026-11-22", duration = 90, weekdays = new[] { 0 }, start = "08:00", end = "09:30", room = "" };
        var candidates = (await Json(await w.Rep.PostAsJsonAsync($"{w.Base}/slots", search))).GetProperty("candidates").EnumerateArray();
        Assert.DoesNotContain(candidates, c => c.GetProperty("spec").GetProperty("date").GetString() == "2026-11-22");
        Assert.Contains("2026-11-22 is National Day, a day off.", Coverage(await w.Analysis(await w.Draft(w.Exam("2026-11-22")))));
        // Exams after the last teaching week are the exam period, not a warning.
        Assert.Empty(Coverage(await w.Analysis(await w.Draft(w.Exam("2027-02-07")))));
        Assert.Contains("2027-02-07 is outside the semester's teaching weeks.", Coverage(await w.Analysis(await w.Draft(new EventSpec("class", "add", "Extra class", "2027-02-07", "13:00", "14:30", "B200", [new(w.Course, [])])))));
    }

    [Fact]
    public void Fingerprints_and_dates_do_not_depend_on_the_server_culture() {
        var section = new Section { Id = 7, CourseId = 3 };
        var weekly = new Meeting { Day = 0, Start = new(8, 0), End = new(9, 30), Room = "B105" };
        var dated = new Meeting { Day = 0, Start = new(8, 0), End = new(9, 30), Room = "B105", Date = new(2026, 11, 8) };
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
            var expected = (PlannerEngine.Fingerprint(section, weekly), PlannerEngine.Fingerprint(section, dated));
            foreach (var name in new[] { "en-US", "en-GB", "ar-SA" }) {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);
                Assert.Equal(expected, (PlannerEngine.Fingerprint(section, weekly), PlannerEngine.Fingerprint(section, dated)));
                Assert.Equal("2026-11-08", PlannerEngine.Iso(new(2026, 11, 8)));
            }
        } finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public async Task A_draft_at_the_end_of_the_calendar_is_refused_and_one_stored_earlier_breaks_nothing() {
        using var w = new World(); await w.Init();
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.PostAsJsonAsync($"{w.Base}/proposals", new { spec = w.Exam("9999-12-27") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.GetAsync($"{w.Base}/calendar?from=9999-12-25&to=9999-12-31")).StatusCode);
        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.PlannerProposals.Add(new() { WorkspaceId = w.Workspace, PayloadJson = PlannerJson.Write(w.Exam("9999-12-27")), UpdatedBy = "Rep", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await Json(await w.Rep.GetAsync($"{w.Base}/calendar?from=2026-11-08&to=2026-11-14"));
        var search = new { targets = new[] { new PlannerTarget(w.Course, []) }, kind = "exam", from = "2026-11-08", to = "2026-11-10", duration = 90, weekdays = new[] { 0, 1, 2 }, start = "08:00", end = "12:00", room = "" };
        await Json(await w.Rep.PostAsJsonAsync($"{w.Base}/slots", search));
        await w.Publish(await w.Draft(w.Exam()));
        var stored = (await Json(await w.Rep.GetAsync(w.Base))).GetProperty("proposals").EnumerateArray().Single(p => p.GetProperty("spec").GetProperty("date").GetString() == "9999-12-27");
        Assert.Contains("Choose a valid date.", Coverage(await w.Analysis(stored)));
    }

    [Fact]
    public async Task Exam_load_counts_rival_drafts_of_the_same_exam_once() {
        using var w = new World(); await w.Init();
        var eventId = (await w.Publish(await w.Draft(w.Exam()))).GetProperty("id").GetInt32();
        var a = await w.Draft(w.Exam(start: "13:00", end: "14:00"), eventId, 1);
        var b = await Json(await w.OtherRep.PostAsJsonAsync($"{w.Base}/proposals", new { spec = w.Exam(start: "15:00", end: "16:00"), eventId, baseEventVersion = 1 }));
        foreach (var p in new[] { a, b })
            Assert.DoesNotContain((await w.Analysis(p)).GetProperty("conflicts").EnumerateArray(), c => c.GetProperty("kind").GetString() is "daily-load" or "weekly-load");
    }

    [Fact]
    public async Task A_draft_of_an_event_that_changed_since_can_be_rebased_on_its_current_version() {
        using var w = new World(); await w.Init();
        var eventId = (await w.Publish(await w.Draft(w.Exam()))).GetProperty("id").GetInt32();
        var a = await w.Draft(w.Exam("2026-11-09"), eventId, 1); var b = await w.Draft(w.Exam("2026-11-10"), eventId, 1);
        await w.Publish(a);
        var bId = b.GetProperty("id").GetInt32();
        var token = (await w.Analysis(b)).GetProperty("token").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rep.PostAsJsonAsync($"{w.Base}/proposals/{bId}/publish", new { version = 1, analysisToken = token, explanation = "Reviewed" })).StatusCode);
        // Only onto the version that's current now.
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rep.PutAsJsonAsync($"{w.Base}/proposals/{bId}", new { spec = w.Exam("2026-11-10"), eventId, baseEventVersion = 5, version = 1 })).StatusCode);
        var rebased = await Json(await w.Rep.PutAsJsonAsync($"{w.Base}/proposals/{bId}", new { spec = w.Exam("2026-11-10"), eventId, baseEventVersion = 2, version = 1 }));
        Assert.Equal(2, rebased.GetProperty("baseEventVersion").GetInt32());
        Assert.Equal(3, (await w.Publish(rebased)).GetProperty("version").GetInt32());
        Assert.Equal("2026-11-10", (await Json(await w.Student.GetAsync("/api/me/timetable"))).GetProperty("plannerEvents")[0].GetProperty("date").GetString());
    }

    [Fact]
    public async Task Reps_only_plan_in_published_semesters() {
        using var w = new World(); await w.Init();
        int draft;
        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var s = new Semester { Name = "Spring 2027", Term = "Spring", Year = 2027, Week1Start = new(2027, 1, 24), LastWeek = 16 };
            db.Semesters.Add(s); await db.SaveChangesAsync(); draft = s.Id;
            var course = new Course { SemesterId = draft, Code = "NEW999", Name = "Secret", Type = CourseType.Core, Year = 2 };
            course.Sections.Add(new() { Number = "1", Instructor = "Dr Not Yet Announced", Meetings = [new() { Day = 2, Start = new(8, 0), End = new(9, 30), Room = "Z1" }] });
            db.Courses.Add(course); await db.SaveChangesAsync();
        }
        var boot = await Json(await w.Rep.GetAsync($"/api/rep/bootstrap?semesterId={draft}"));
        Assert.DoesNotContain(boot.GetProperty("semesters").EnumerateArray(), s => s.GetProperty("id").GetInt32() == draft);
        Assert.Equal(w.Semester, boot.GetProperty("semesterId").GetInt32());
        Assert.DoesNotContain(boot.GetProperty("courses").EnumerateArray(), c => c.GetProperty("code").GetString() == "NEW999");
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rep.PostAsJsonAsync("/api/rep/workspaces/open", new { semesterId = draft })).StatusCode);
        // Owners can still prepare it; a rep can't reach that workspace until it's published.
        Assert.Equal(draft, (await Json(await w.Owner.GetAsync($"/api/rep/bootstrap?semesterId={draft}"))).GetProperty("semesterId").GetInt32());
        var ws = (await Json(await w.Owner.PostAsJsonAsync("/api/rep/workspaces/open", new { semesterId = draft, family = "family:COMP" }))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rep.GetAsync($"/api/rep/workspaces/{ws}/calendar?from=2027-01-24&to=2027-01-30")).StatusCode);
        using (var scope = w.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDb>().Semesters.Where(s => s.Id == draft).ExecuteUpdateAsync(u => u.SetProperty(s => s.PublishedAt, DateTimeOffset.UtcNow));
        await Json(await w.Rep.GetAsync($"/api/rep/workspaces/{ws}/calendar?from=2027-01-24&to=2027-01-30"));
    }

    [Fact]
    public async Task A_withdrawn_event_cannot_be_withdrawn_again() {
        using var w = new World(); await w.Init();
        var eventId = (await w.Publish(await w.Draft(w.Exam()))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rep.PostAsJsonAsync($"{w.Base}/events/{eventId}/withdraw", new { version = 1, explanation = "Moved" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rep.PostAsJsonAsync($"{w.Base}/events/{eventId}/withdraw", new { version = 2, explanation = "Again" })).StatusCode);
    }

    [Fact]
    public async Task A_batch_keeps_saving_after_an_upload_removed_one_of_its_courses() {
        using var w = new World(); await w.Init();
        int gone, keep, keepSection, droppedSection;
        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var a = new Course { SemesterId = w.Semester, Code = "TMP100", Name = "Temporary", Type = CourseType.Core, Year = 2 };
            a.Sections.Add(new() { Number = "1", Meetings = [new() { Day = 3, Start = new(8, 0), End = new(9, 30), Room = "C1" }] });
            var b = new Course { SemesterId = w.Semester, Code = "TMP200", Name = "Kept", Type = CourseType.Core, Year = 2 };
            b.Sections.Add(new() { Number = "1", Meetings = [new() { Day = 3, Start = new(10, 0), End = new(11, 30), Room = "C1" }] });
            b.Sections.Add(new() { Number = "2", Meetings = [new() { Day = 4, Start = new(10, 0), End = new(11, 30), Room = "C1" }] });
            db.Courses.AddRange(a, b); await db.SaveChangesAsync();
            gone = a.Id; keep = b.Id; keepSection = b.Sections[0].Id; droppedSection = b.Sections[1].Id;
        }
        var batch = await Json(await w.Rep.PostAsJsonAsync($"{w.Base}/batches", new { name = "Year 2", majorId = w.Major, year = 2, targets = new[] { new PlannerTarget(w.Course, []), new PlannerTarget(gone, []), new PlannerTarget(keep, [keepSection, droppedSection]) } }));
        // A later upload deletes the course nobody picked, and one section of another.
        using (var scope = w.Factory.Services.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.Sections.Where(s => s.Id == droppedSection).ExecuteDeleteAsync();
            db.Courses.Remove(await db.Courses.FirstAsync(c => c.Id == gone));
            await db.SaveChangesAsync();
        }
        var targets = batch.GetProperty("targets").Deserialize<PlannerTarget[]>(PlannerJson.Options)!;
        var saved = await Json(await w.Rep.PutAsJsonAsync($"{w.Base}/batches/{batch.GetProperty("id").GetInt32()}", new { name = "Year 2 renamed", majorId = w.Major, year = 2, targets, version = 1 }));
        var kept = saved.GetProperty("targets").Deserialize<PlannerTarget[]>(PlannerJson.Options)!;
        Assert.Equal([w.Course, keep], kept.Select(t => t.CourseId));
        Assert.Equal([keepSection], kept[1].SectionIds);
    }
}
