using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Data;
using UniSchedule.Api.Students;

namespace UniSchedule.Tests;

/// <summary>Days off (holidays), the absence counter, and notification settings, reminders and change alerts.</summary>
public class DaysOffAbsencesNotificationsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 7100;
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<(HttpClient Client, string Id)> Student()
    {
        var id = $"26-{Interlocked.Increment(ref next):D4}";
        var c = factory.Client();
        var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).StatusCode);
        return (c, id);
    }

    static int setups;

    /// <summary>
    /// A published semester (weeks from Sun 6 Sep 2026, 15 weeks) with HX101 every Sunday 08:00–09:30 in R1, and a
    /// student who picked it. Returns the owner, the student, the semester and the course.
    /// </summary>
    async Task<(HttpClient Owner, HttpClient Student, string StudentId, int Semester, int Course, int Section)> Setup()
    {
        var name = $"owner-dao-{Interlocked.Increment(ref setups)}";
        await factory.AddAdmin(name, "owner-password-1", AdminRole.Owner, mustChange: false);
        var owner = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/admin-login", new { username = name, password = "owner-password-1" })).StatusCode);
        var csv = "code,name,major,type,year,section,instructor,date,days,time,room\nHX101,Holiday Things,HXM,Core,1,1,Dr. H,,Sun,08:00-09:30,R1\n";
        var sem = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2095 + setups }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "c.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{sem}/weeks", new { week1Start = "2026-09-06", lastWeek = 15 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/publish", null)).StatusCode);
        var major = (await Json(await owner.GetAsync("/api/admin/majors"))).EnumerateArray().First(m => m.GetProperty("code").GetString() == "HXM").GetProperty("id").GetInt32();
        var course = (await Json(await owner.GetAsync($"/api/admin/semesters/{sem}/courses"))).EnumerateArray().First().GetProperty("id").GetInt32();
        var section = (await Json(await owner.GetAsync($"/api/admin/courses/{course}"))).GetProperty("course").GetProperty("sections")[0].GetProperty("id").GetInt32();

        var (s, id) = await Student();
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/profile", new { majorId = major, year = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.PutAsJsonAsync("/api/me/picks", new { picks = new[] { new { courseId = course, sectionId = (int?)section } }, step = "core" })).StatusCode);
        return (owner, s, id, sem, course, section);
    }

    [Fact]
    public async Task A_day_off_skips_weekly_classes_everywhere()
    {
        var (owner, s, _, sem, _, _) = await Setup();

        // Students can't set days off; staff can, one day or a run of them.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.PostAsJsonAsync($"/api/admin/semesters/{sem}/days-off", new { date = "2026-10-18", name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/admin/semesters/{sem}/days-off", new { date = "2026-10-18", name = "" })).StatusCode);
        var days = await Json(await owner.PostAsJsonAsync($"/api/admin/semesters/{sem}/days-off", new { date = "2026-10-18", name = "Test Day" }));
        Assert.Equal("2026-10-18", days[0].GetProperty("date").GetString());
        days = await Json(await owner.PostAsJsonAsync($"/api/admin/semesters/{sem}/days-off", new { date = "2026-11-18", to = "2026-11-19", name = "National Day" }));
        Assert.Equal(3, days.GetArrayLength());

        // The timetable lists them, and the weekly class skips the Sunday one.
        var tt = await Json(await s.GetAsync("/api/me/timetable"));
        Assert.Equal(["2026-10-18", "2026-11-18", "2026-11-19"], tt.GetProperty("daysOff").EnumerateArray().Select(d => d.GetProperty("date").GetString()!).ToArray());
        var meeting = tt.GetProperty("classes")[0].GetProperty("section").GetProperty("meetings")[0];
        Assert.Equal(["2026-10-18"], meeting.GetProperty("excludedDates").EnumerateArray().Select(d => d.GetString()!).ToArray());

        // The calendar feed leaves that Sunday out; free rooms says it's a day off and R1 is free.
        var url = (await Json(await s.GetAsync("/api/me/calendar"))).GetProperty("url").GetString()!;
        var ics = await (await factory.Client().GetAsync(new Uri(url).PathAndQuery)).Content.ReadAsStringAsync();
        Assert.Contains("EXDATE;TZID=Asia/Muscat:20261018T080000\r\n", ics);
        var rooms = await Json(await s.GetAsync("/api/rooms/free?date=2026-10-18&at=08:30"));
        Assert.Equal("Test Day", rooms.GetProperty("dayOff").GetString());
        Assert.Contains(rooms.GetProperty("rooms").EnumerateArray(), r => r.GetProperty("room").GetString() == "R1");
        // The Sunday after, it's an ordinary day: R1 has the class.
        Assert.DoesNotContain((await Json(await s.GetAsync("/api/rooms/free?date=2026-10-25&at=08:30"))).GetProperty("rooms").EnumerateArray(), r => r.GetProperty("room").GetString() == "R1");

        // Removing it brings the class back.
        var id = days[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/admin/semesters/{sem}/days-off/{id}")).StatusCode);
        meeting = (await Json(await s.GetAsync("/api/me/timetable"))).GetProperty("classes")[0].GetProperty("section").GetProperty("meetings")[0];
        Assert.True(meeting.GetProperty("excludedDates").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Students_count_their_own_absences_against_the_limit()
    {
        var (owner, s, _, _, course, _) = await Setup();

        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/absences", new { courseId = course, date = "2026-10-11", start = "08:00", missed = true })).StatusCode);
        // Marking twice keeps one.
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/absences", new { courseId = course, date = "2026-10-11", start = "08:00", missed = true })).StatusCode);
        var list = await Json(await s.GetAsync("/api/me/absences"));
        Assert.Equal(1, list.GetProperty("absences").GetArrayLength());
        Assert.Equal("08:00", list.GetProperty("absences")[0].GetProperty("start").GetString());
        Assert.True(list.GetProperty("limit").ValueKind == JsonValueKind.Null);

        // Not for days to come, not for someone else's class, and another student sees none of it.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PutAsJsonAsync("/api/me/absences", new { courseId = course, date = "2099-01-04", start = "08:00", missed = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PutAsJsonAsync("/api/me/absences", new { courseId = 999_999, date = "2026-10-11", start = "08:00", missed = true })).StatusCode);
        var (other, _) = await Student();
        Assert.Equal(0, (await Json(await other.GetAsync("/api/me/absences"))).GetProperty("absences").GetArrayLength());

        // Owners set the limit; students see it.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.PutAsJsonAsync("/api/admin/attendance-limit", new { percent = 25 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/admin/attendance-limit", new { percent = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/admin/attendance-limit", new { percent = 25 })).StatusCode);
        Assert.Equal(25, (await Json(await s.GetAsync("/api/me/absences"))).GetProperty("limit").GetInt32());

        // Unmarking removes it.
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/absences", new { courseId = course, date = "2026-10-11", start = "08:00", missed = false })).StatusCode);
        Assert.Equal(0, (await Json(await s.GetAsync("/api/me/absences"))).GetProperty("absences").GetArrayLength());
    }

    [Fact]
    public async Task Reminders_come_due_before_class_and_changes_raise_alerts()
    {
        var (owner, s, studentId, sem, course, section) = await Setup();

        // The signing key is made once and stays the same.
        var key = (await Json(await factory.Client().GetAsync("/api/push/key"))).GetProperty("publicKey").GetString();
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(key, (await Json(await factory.Client().GetAsync("/api/push/key"))).GetProperty("publicKey").GetString());

        // A device, then settings.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostAsJsonAsync("/api/me/push", new { endpoint = "http://not-https.example/x", keys = new { p256dh = "k", auth = "a" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.PostAsJsonAsync("/api/me/push", new { endpoint = "https://push.example.com/device-1", keys = new { p256dh = "BKey", auth = "Auth" } })).StatusCode);
        var settings = await Json(await s.GetAsync("/api/me/notifications"));
        Assert.Equal(1, settings.GetProperty("devices").GetInt32());
        Assert.True(settings.GetProperty("remindMinutes").ValueKind == JsonValueKind.Null);
        Assert.True(settings.GetProperty("changes").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PutAsJsonAsync("/api/me/notifications", new { remindMinutes = 7, changes = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/notifications", new { remindMinutes = 15, changes = true })).StatusCode);
        Assert.Equal(15, (await Json(await s.GetAsync("/api/me/notifications"))).GetProperty("remindMinutes").GetInt32());

        // Sunday 11 Oct 2026, class at 08:00: its reminder is due in the half minute around 07:45 Oman time, not before.
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/admin/semesters/{sem}/days-off", new { date = "2026-10-18", name = "Test Day" })).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var me = await db.Students.Where(x => x.UniversityId == studentId).Select(x => x.Id).FirstAsync();
        DateTimeOffset At(int day, int h, int m, int sec = 0) => new(2026, 10, day, h, m, sec, Oman);
        var due = await Notifications.DueReminders(db, At(11, 7, 44, 50), At(11, 7, 45, 20), default);
        var note = Assert.Single(due, d => d.StudentId == me).Note;
        Assert.Equal("HX101 in 15 min", note.Title);
        Assert.Equal("Holiday Things · 08:00 · R1", note.Body);
        Assert.Equal($"/?session={course}.2026-10-11.08:00", note.Url);
        Assert.DoesNotContain(await Notifications.DueReminders(db, At(11, 7, 40), At(11, 7, 44), default), d => d.StudentId == me);
        // On the day off there's no class, so no reminder.
        Assert.DoesNotContain(await Notifications.DueReminders(db, At(18, 7, 44, 50), At(18, 7, 45, 20), default), d => d.StudentId == me);

        // A change to the section after the last check raises an alert for the student, until they mute changes.
        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.Sections.Where(x => x.Id == section).ExecuteUpdateAsync(u => u.SetProperty(x => x.ChangedAt, DateTimeOffset.UtcNow).SetProperty(x => x.ChangeSummary, "Moved to R2."));
        var alert = Assert.Single(await Notifications.ChangeAlerts(db, before, DateTimeOffset.UtcNow.AddMinutes(1), default), a => a.StudentId == me).Note;
        Assert.Equal("HX101 changed", alert.Title);
        Assert.Equal("Holiday Things: Moved to R2.", alert.Body);
        Assert.Empty(await Notifications.ChangeAlerts(db, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddMinutes(2), default));
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/notifications", new { remindMinutes = 15, changes = false })).StatusCode);
        Assert.DoesNotContain(await Notifications.ChangeAlerts(db, before, DateTimeOffset.UtcNow.AddMinutes(1), default), a => a.StudentId == me);

        // Removing the device stops it all.
        Assert.Equal(HttpStatusCode.NoContent, (await s.PostAsJsonAsync("/api/me/push/remove", new { endpoint = "https://push.example.com/device-1" })).StatusCode);
        Assert.Equal(0, (await Json(await s.GetAsync("/api/me/notifications"))).GetProperty("devices").GetInt32());
    }
}
