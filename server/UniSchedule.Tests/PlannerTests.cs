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
}
