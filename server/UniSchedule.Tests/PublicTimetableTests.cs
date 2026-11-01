using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>The public timetable API: off until an owner switches it on, then the published semester for anyone.</summary>
public class PublicTimetableTests(ApiFactory factory) : IClassFixture<ApiFactory>
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
    public async Task Anyone_can_read_the_published_timetable_only_while_it_is_switched_on()
    {
        var owner = await Staff("owner-public", AdminRole.Owner);
        var rsa = await Staff("rsa-public", AdminRole.Rsa);
        var anyone = factory.Client();
        var csv = "code,name,major,type,year,section,instructor,date,days,time,room\n" +
            "PB101,Public Course,PBM,Core,1,1,Dr. A,2026-10-18,,09:45-11:15,GU1 240\n" +
            "PB101,Public Course,PBM,Core,1,2,Dr. B,,Mon,08:00-09:30,GU1 101\n";
        var sem = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2098 }))).GetProperty("id").GetInt32();
        using (var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "p.csv" } })
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/publish", null)).StatusCode);

        // Off by default.
        Assert.False((await Json(await owner.GetAsync("/api/admin/public-timetable"))).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync("/api/public/timetable")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync("/api/public/timetable.csv")).StatusCode);

        // Only owners switch it.
        Assert.Equal(HttpStatusCode.Forbidden, (await rsa.PutAsJsonAsync("/api/admin/public-timetable", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anyone.PutAsJsonAsync("/api/admin/public-timetable", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/admin/public-timetable", new { enabled = true })).StatusCode);

        var json = await Json(await anyone.GetAsync("/api/public/timetable"));
        Assert.Equal("Fall 2098", json.GetProperty("semester").GetString());
        var classes = json.GetProperty("classes").EnumerateArray().ToList();
        Assert.Equal(2, classes.Count);
        var dated = classes.First(c => c.GetProperty("section").GetString() == "1");
        Assert.Equal("2026-10-18", dated.GetProperty("date").GetString());
        Assert.Equal("Sun", dated.GetProperty("day").GetString());
        Assert.Equal("GU1 240", dated.GetProperty("room").GetString());
        Assert.Equal(JsonValueKind.Null, classes.First(c => c.GetProperty("section").GetString() == "2").GetProperty("date").ValueKind);

        var file2 = await anyone.GetAsync("/api/public/timetable.csv");
        Assert.Equal(HttpStatusCode.OK, file2.StatusCode);
        var text = (await file2.Content.ReadAsStringAsync()).TrimStart('﻿');
        Assert.StartsWith("Course Code,Course Name,Major,Type,Year,Credits,Section,Instructor,Date,Days,Time,Room", text);
        Assert.Contains("PB101,Public Course,PBM,Core,1,,2,Dr. B,,Mon,08:00-09:30,GU1 101", text);

        // Switched off again.
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/admin/public-timetable", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anyone.GetAsync("/api/public/timetable")).StatusCode);
    }
}
