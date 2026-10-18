using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>The data behind the v3 dashboard: needs attention, recent uploads, course status, student course counts, password rules.</summary>
public class DashboardTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    const string Csv = "code,name,major,type,year,section,instructor,days,time,room\n" +
        "CS201,Data Structures,CS,Core,2,1,Dr. Laura Becker,Sun/Tue,08:00-09:30,B105\n" +
        "CS201,Data Structures,CS,Core,2,2,Dr. B,Mon/Wed,08:00-09:30,\n" +
        "LS101,Academic English,,LS,,1,Ms. D,Thu,08:00-09:30,A203\n";

    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<HttpClient> Owner(string username)
    {
        await factory.AddAdmin(username, "owner-password-1", AdminRole.Owner, mustChange: false);
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "owner-password-1" })).StatusCode);
        return c;
    }

    static async Task<int> UploadAndPublish(HttpClient c, int year)
    {
        var semester = await Json(await c.PostAsJsonAsync("/api/admin/semesters", new { term = "Spring", year }));
        var id = semester.GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(Csv)), "file", "spring.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/admin/semesters/{id}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/admin/semesters/{id}/publish", null)).StatusCode);
        return id;
    }

    [Fact]
    public async Task Applying_an_upload_shows_under_recent_uploads()
    {
        var c = await Owner("owner-uploads");
        var id = await UploadAndPublish(c, 2031);
        var stats = await Json(await c.GetAsync($"/api/admin/stats?semesterId={id}"));
        var upload = stats.GetProperty("recentUploads")[0];
        Assert.Equal("spring.csv", upload.GetProperty("fileName").GetString());
        Assert.Equal("owner-uploads", upload.GetProperty("by").GetString());
        Assert.True(upload.GetProperty("added").GetInt32() > 0);
        Assert.Equal(0, upload.GetProperty("removed").GetInt32());
    }

    [Fact]
    public async Task Needs_attention_lists_sections_without_a_room_and_students_stuck_in_setup()
    {
        var c = await Owner("owner-attention");
        var id = await UploadAndPublish(c, 2032);
        var stats = await Json(await c.GetAsync($"/api/admin/stats?semesterId={id}"));
        var attention = stats.GetProperty("attention");
        var noRoom = attention.GetProperty("noRoom");
        Assert.Equal(1, noRoom.GetArrayLength());
        Assert.Equal("CS201", noRoom[0].GetProperty("code").GetString());
        Assert.Equal("2", noRoom[0].GetProperty("number").GetString());
        Assert.Equal(0, attention.GetProperty("removed").GetArrayLength());
        Assert.True(attention.GetProperty("stuckInSetup").GetInt32() >= 0);
        Assert.Equal(0, stats.GetProperty("sectionEditsThisWeek").GetInt32());
    }

    [Fact]
    public async Task Courses_list_has_a_status_and_finds_instructors()
    {
        var c = await Owner("owner-courses");
        var id = await UploadAndPublish(c, 2033);
        var byInstructor = await Json(await c.GetAsync($"/api/admin/semesters/{id}/courses?q=becker"));
        Assert.Equal(1, byInstructor.GetArrayLength());
        var cs201 = byInstructor[0];
        Assert.Equal("CS201", cs201.GetProperty("code").GetString());
        Assert.Equal(1, cs201.GetProperty("noRoom").GetInt32());
        Assert.False(cs201.GetProperty("changed").GetBoolean());
        Assert.Equal(0, cs201.GetProperty("removedPicks").GetInt32());
    }

    [Fact]
    public async Task Student_list_counts_courses()
    {
        var c = await Owner("owner-students");
        var list = await Json(await c.GetAsync("/api/admin/students"));
        foreach (var s in list.GetProperty("students").EnumerateArray())
            Assert.True(s.TryGetProperty("courses", out _));
    }

    [Theory]
    [InlineData("rsa-rule-a", "short-1", "Use at least 12 characters.")]
    [InlineData("rsa-rule-b", "no-numbers-in-here", "Add at least one number.")]
    [InlineData("rsa-rule-c", "rsa-rule-c-password-1", "Don't put your username in your password.")]
    public async Task New_passwords_follow_the_rules(string username, string password, string error)
    {
        await factory.AddAdmin(username, "temp-pass-1", AdminRole.Rsa, mustChange: true);
        var c = factory.Client();
        await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "temp-pass-1" });
        var r = await c.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = "temp-pass-1", newPassword = password });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(error, (await Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public void Password_rules_accept_a_good_password()
    {
        Assert.Null(AuthEndpoints.PasswordProblem("timetable-2026", "rsa-office"));
    }

    [Fact]
    public async Task Using_the_app_counts_as_active_without_signing_in_again()
    {
        var owner = await Owner("owner-active");
        var id = "25-9301";
        var student = factory.Client();
        var code = (await Json(await student.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await student.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).StatusCode);

        // Pretend the sign-in was days ago: the cookie still works, and using the app moves "last seen" up.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var s = db.Students.Single(x => x.UniversityId == id);
            s.LastSeenAt = DateTimeOffset.UtcNow.AddDays(-3);
            db.SaveChanges();
        }
        var before = await Json(await owner.GetAsync("/api/admin/stats"));
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync("/api/me")).StatusCode);
        var after = await Json(await owner.GetAsync("/api/admin/stats"));
        Assert.Equal(before.GetProperty("activeNow").GetInt32() + 1, after.GetProperty("activeNow").GetInt32());
        Assert.True(after.GetProperty("activeToday").GetInt32() >= after.GetProperty("activeNow").GetInt32());
        using (var scope = factory.Services.CreateScope())
        {
            var seen = scope.ServiceProvider.GetRequiredService<AppDb>().Students.Single(x => x.UniversityId == id).LastSeenAt;
            Assert.True(seen > DateTimeOffset.UtcNow.AddMinutes(-1));
        }
    }
}
