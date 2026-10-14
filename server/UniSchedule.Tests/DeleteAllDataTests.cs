using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

        var wiped = await owner.PostAsJsonAsync("/api/admin/delete-all-data", new { confirm = "DELETE ALL DATA" });
        Assert.Equal(HttpStatusCode.OK, wiped.StatusCode);
        Assert.Equal(0, (await Json(await owner.GetAsync("/api/admin/semesters"))).GetArrayLength());
        Assert.Equal(0, (await Json(await owner.GetAsync("/api/admin/majors"))).GetArrayLength());
        // Accounts survive: both can still use the dashboard.
        Assert.Equal(HttpStatusCode.OK, (await rsa.GetAsync("/api/admin/semesters")).StatusCode);
    }
}
