using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UniSchedule.Api.Data;
using UniSchedule.Api.Students;

namespace UniSchedule.Tests;

/// <summary>Free rooms: which rooms have no class at a time, and until when, from the published timetable.</summary>
public class FreeRoomsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// Sundays: A101 08:00–09:30 and 13:15–14:45, A102 09:45–11:15. Sun 18 Oct 2026 only: Lab L2 08:00–09:30.
    /// Mondays: a class with no room yet (TBA).
    /// </summary>
    async Task<HttpClient> Published()
    {
        await factory.AddAdmin("owner-rooms", "owner-password-1", AdminRole.Owner, mustChange: false);
        var owner = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/admin-login", new { username = "owner-rooms", password = "owner-password-1" })).StatusCode);
        var csv = "code,name,major,type,year,section,instructor,date,days,time,room\n" +
            "FR101,Morning,FRM,Core,1,1,A,,Sun,08:00-09:30,A101\n" +
            "FR102,Midday,FRM,Core,1,1,B,,Sun,09:45-11:15,A102\n" +
            "FR103,Afternoon,FRM,Core,1,1,C,,Sun,13:15-14:45,a101\n" +
            "FR104,Once,FRM,Core,1,1,D,2026-10-18,,08:00-09:30,Lab L2\n" +
            "FR105,Unplaced,FRM,Core,1,1,E,,Mon,08:00-09:30,TBA\n";
        var sem = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2093 }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "r.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/publish", null)).StatusCode);
        return owner;
    }

    static Dictionary<string, string?> Rooms(JsonElement r) =>
        r.GetProperty("rooms").EnumerateArray().ToDictionary(x => x.GetProperty("room").GetString()!, x => x.GetProperty("freeUntil").GetString());

    [Fact]
    public async Task Rooms_are_free_until_their_next_class()
    {
        var c = await Published();
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/rooms/free")).StatusCode);

        // Sunday 08:30: A101 and the one-off Lab L2 are in use; A102 is free until its 09:45 class.
        var early = await Json(await c.GetAsync("/api/rooms/free?date=2026-10-18&at=08:30"));
        Assert.True(early.GetProperty("classesThatDay").GetBoolean());
        Assert.Equal(2, early.GetProperty("busy").GetInt32());
        Assert.Equal(new Dictionary<string, string?> { ["A102"] = "09:45" }, Rooms(early));

        // 10:00: A101 is free until its afternoon class (written "a101" in the file: the same room); Lab L2 for the rest of the day.
        var later = Rooms(await Json(await c.GetAsync("/api/rooms/free?date=2026-10-18&at=10:00")));
        Assert.Equal("13:15", later["A101"]);
        Assert.Null(later["Lab L2"]);
        Assert.False(later.ContainsKey("A102"));
        Assert.False(later.ContainsKey("TBA"));

        // The one-off class only blocks its own date.
        var nextSunday = Rooms(await Json(await c.GetAsync("/api/rooms/free?date=2026-10-25&at=08:30")));
        Assert.True(nextSunday.ContainsKey("Lab L2"));
        Assert.False(nextSunday.ContainsKey("A101"));

        // A Friday has no classes: every room is free, and the page is told so.
        var friday = await Json(await c.GetAsync("/api/rooms/free?date=2026-10-23&at=10:00"));
        Assert.False(friday.GetProperty("classesThatDay").GetBoolean());
        Assert.Equal(3, friday.GetProperty("rooms").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/rooms/free?at=noon")).StatusCode);
    }

    [Theory]
    [InlineData("A101", "A")]
    [InlineData("Lab L2", "Lab")]
    [InlineData("b-105", "B")]
    [InlineData("101", "Other")]
    public void Rooms_are_grouped_by_building(string room, string building) => Assert.Equal(building, FreeRooms.Building(room));
}
