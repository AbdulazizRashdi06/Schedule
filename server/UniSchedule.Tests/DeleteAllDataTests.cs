using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

public class DeleteAllDataTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<HttpClient> Staff(string username, AdminRole role)
    {
        await factory.AddAdmin(username, "staff-password-1", role, mustChange: false);
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "staff-password-1" })).StatusCode);
        return c;
    }

    [Fact]
    public async Task Teaching_weeks_come_from_dated_classes_until_an_admin_sets_them()
    {
        var rsa = await Staff("rsa-weeks", AdminRole.Rsa);
        var id = (await Json(await rsa.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2051 }))).GetProperty("id").GetInt32();
        // Wed 23 Sep 2026 to Mon 7 Dec 2026: weeks of Sunday 20 Sep to Sunday 6 Dec, so 12 weeks.
        var csv = "code,name,major,type,year,section,instructor,date,time,room\n" +
            "WK101,Weeks,WK,Core,1,1,A,23/09/2026,08:00-09:30,R1\n" +
            "WK101,Weeks,WK,Core,1,1,A,07/12/2026,08:00-09:30,R1\n";
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "w.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await rsa.PostAsync($"/api/admin/semesters/{id}/upload/apply", file)).StatusCode);

        var auto = await Json(await rsa.GetAsync($"/api/admin/semesters/{id}/weeks"));
        Assert.Equal("2026-09-20", auto.GetProperty("week1Start").GetString());
        Assert.Equal(12, auto.GetProperty("lastWeek").GetInt32());
        Assert.False(auto.GetProperty("set").GetBoolean());

        // A date mid-week snaps to its Sunday; the last week is whatever the admin says.
        var set = await Json(await rsa.PutAsJsonAsync($"/api/admin/semesters/{id}/weeks", new { week1Start = "2026-09-16", lastWeek = 14 }));
        Assert.Equal("2026-09-13", set.GetProperty("week1Start").GetString());
        Assert.Equal(14, set.GetProperty("lastWeek").GetInt32());
        Assert.True(set.GetProperty("set").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await rsa.PutAsJsonAsync($"/api/admin/semesters/{id}/weeks", new { week1Start = "soon", lastWeek = 14 })).StatusCode);
    }

    [Fact]
    public async Task Only_an_owner_can_delete_all_data_and_it_keeps_accounts()
    {
        var owner = await Staff("owner-wipe", AdminRole.Owner);
        var rsa = await Staff("rsa-wipe", AdminRole.Rsa);
        var id = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2050 }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent("code,name,major,type,year,section,instructor,days,time,room\nW101,Wipe,WP,Core,1,1,A,Sun,08:00-09:30,R1\n"u8.ToArray()), "file", "w.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{id}/upload/apply", file)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await rsa.PostAsJsonAsync("/api/admin/delete-all-data", new { confirm = "DELETE ALL DATA" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/admin/delete-all-data", new { confirm = "yes" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await factory.Client().PostAsJsonAsync("/api/auth/request-code", new { universityId = "24-9901" })).StatusCode);

        var wiped = await owner.PostAsJsonAsync("/api/admin/delete-all-data", new { confirm = "DELETE ALL DATA" });
        Assert.Equal(HttpStatusCode.OK, wiped.StatusCode);
        // Sign-in codes name the students who asked for them, so they go too.
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDb>().LoginCodes.CountAsync());
        Assert.Equal(0, (await Json(await owner.GetAsync("/api/admin/semesters"))).GetArrayLength());
        Assert.Equal(0, (await Json(await owner.GetAsync("/api/admin/majors"))).GetArrayLength());
        // Accounts survive: both can still use the dashboard.
        Assert.Equal(HttpStatusCode.OK, (await rsa.GetAsync("/api/admin/semesters")).StatusCode);
    }

    [Fact]
    public async Task Delete_all_data_removes_teacher_accounts_and_their_changes_and_keeps_blocked_emails()
    {
        var owner = await Staff("owner-wipe-teachers", AdminRole.Owner);
        var id = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2052 }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent("code,name,major,type,year,section,instructor,days,time,room\nT101,Teach,TP,Core,1,1,Salim Al Harthy,Sun,08:00-09:30,R1\n"u8.ToArray()), "file", "t.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{id}/upload/apply", file)).StatusCode);

        // A staff account with everything a teacher can have, an owner (a student) with teaching rights, and a blocked email.
        var studentOwner = await TeacherTestKit.SignInStudent(factory, ApiFactory.OwnerId);
        var teacher = await factory.Services.AddTeacher("wipe.me@gutech.edu.om", "Salim Al Harthy");
        await factory.Services.Db(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            var section = await db.Sections.SingleAsync(s => s.Course.SemesterId == id);
            var ownerRow = await db.Students.SingleAsync(s => s.UniversityId == ApiFactory.OwnerId);
            db.Teachers.Add(new UniSchedule.Api.Teachers.Teacher { StudentId = ownerRow.Id, DisplayName = "Owner TA", CreatedAt = now });
            db.TeacherSections.Add(new() { TeacherId = teacher.Id, SemesterId = id, SectionId = section.Id, CourseCode = "T101", SectionNumber = "1", Mode = "hidden", CreatedAt = now });
            db.TeacherSeens.Add(new() { TeacherId = teacher.Id, SectionId = section.Id, SeenAt = now });
            db.TeacherSlots.Add(new() { TeacherId = teacher.Id, SemesterId = id, Title = "Meeting", Day = 1, Start = new(10, 0), End = new(11, 0), CreatedAt = now });
            db.TeacherPushSubscriptions.Add(new() { TeacherId = teacher.Id, Endpoint = "https://push.example/wipe", P256dh = "k", Auth = "a", CreatedAt = now });
            db.OfficeHours.Add(new() { TeacherId = teacher.Id, Day = 2, Start = new(12, 0), End = new(13, 0), Room = "C1" });
            db.ClassChanges.Add(new()
            {
                SemesterId = id, SectionId = section.Id, CourseCode = "T101", CourseName = "Teach", SectionNumber = "1", GroupId = Guid.NewGuid().ToString("N"), Kind = "extra",
                SessionType = "lecture", NewDate = new(2052, 10, 1), NewStart = new(10, 0), NewEnd = new(11, 0), NewRoom = "R1", Tag = "t", TeacherId = teacher.Id,
                ByKind = "teacher", ById = teacher.Id, ByLabel = teacher.Email!, ByName = "Salim Al Harthy", CreatedAt = now,
            });
            db.TeachNotices.Add(new() { Audience = "students", SectionId = section.Id, Kind = "change", Title = "x", Tag = "t", CreatedAt = now });
            db.TeachNotices.Add(new() { Audience = "teacher", TeacherId = teacher.Id, Kind = "undone", Title = "x", Tag = "t", CreatedAt = now });
            db.TeachNotices.Add(new() { Audience = "staff", Kind = "many-changes", Title = "x", Tag = "t", CreatedAt = now });
            db.BlockedEmails.Add(new() { Email = "blocked.before@gutech.edu.om", BlockedAt = now, BlockedBy = "owner-wipe-teachers" });
            db.AppSettings.Add(new AppSetting { Key = UniSchedule.Api.Teachers.TeachReminders.Since, Value = now.ToString("O"), UpdatedBy = "system" });
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.OK, (await studentOwner.GetAsync("/api/teach/me")).StatusCode);

        var wiped = await Json(await owner.PostAsJsonAsync("/api/admin/delete-all-data", new { confirm = "DELETE ALL DATA" }));
        Assert.True(wiped.GetProperty("teachers").GetInt32() >= 2);
        await factory.Services.Db(async db =>
        {
            Assert.Equal(0, await db.Teachers.CountAsync());
            Assert.Equal(0, await db.TeacherNames.CountAsync());
            Assert.Equal(0, await db.TeacherSections.CountAsync());
            Assert.Equal(0, await db.TeacherSeens.CountAsync());
            Assert.Equal(0, await db.TeacherSlots.CountAsync());
            Assert.Equal(0, await db.TeacherPushSubscriptions.CountAsync());
            Assert.Equal(0, await db.OfficeHours.CountAsync());
            Assert.Equal(0, await db.ClassChanges.CountAsync());
            Assert.Equal(0, await db.TeachNotices.CountAsync());
            Assert.False(await db.AppSettings.AnyAsync(x => x.Key == UniSchedule.Api.Teachers.TeachReminders.Since));
            // Blocked emails stay blocked.
            Assert.True(await db.BlockedEmails.AnyAsync(b => b.Email == "blocked.before@gutech.edu.om"));
            // The owner is still a student account, without the teaching profile.
            Assert.True(await db.Students.AnyAsync(s => s.UniversityId == ApiFactory.OwnerId));
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await studentOwner.GetAsync("/api/teach/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await studentOwner.GetAsync("/api/auth/session")).StatusCode);
    }
}
