using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>The calendar feed: a private link calendar apps subscribe to, with the student's classes and own slots.</summary>
public class CalendarTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 9800;
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<HttpClient> Student()
    {
        var id = $"25-{Interlocked.Increment(ref next):D4}";
        var c = factory.Client();
        var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).StatusCode);
        return c;
    }

    /// <summary>CF101 every Sunday 08:00–09:30 in R1, CF102 once on Mon 19 Oct 2026 in "Lab L2". Published, with weeks set.</summary>
    async Task<(int Major, Dictionary<string, (int Course, int Section)> Courses)> Setup()
    {
        await factory.AddAdmin("owner-calendar", "owner-password-1", AdminRole.Owner, mustChange: false);
        var owner = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/auth/admin-login", new { username = "owner-calendar", password = "owner-password-1" })).StatusCode);
        var csv = "code,name,major,type,year,section,instructor,date,days,time,room\n" +
            "CF101,Weekly Things,CFM,Core,1,1,Dr. A,,Sun,08:00-09:30,R1\n" +
            "CF102,Dated Thing,CFM,Core,1,1,Dr. B,2026-10-19,,11:30-13:00,Lab L2\n";
        var sem = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year = 2094 }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "c.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/admin/semesters/{sem}/weeks", new { week1Start = "2026-09-06", lastWeek = 15 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/publish", null)).StatusCode);
        var major = (await Json(await owner.GetAsync("/api/admin/majors"))).EnumerateArray().First(m => m.GetProperty("code").GetString() == "CFM").GetProperty("id").GetInt32();
        var courses = new Dictionary<string, (int, int)>();
        foreach (var c in (await Json(await owner.GetAsync($"/api/admin/semesters/{sem}/courses"))).EnumerateArray())
        {
            var id = c.GetProperty("id").GetInt32();
            var section = (await Json(await owner.GetAsync($"/api/admin/courses/{id}"))).GetProperty("course").GetProperty("sections")[0].GetProperty("id").GetInt32();
            courses[c.GetProperty("code").GetString()!] = (id, section);
        }
        return (major, courses);
    }

    [Fact]
    public async Task The_feed_has_classes_and_slots_and_the_link_can_be_reset()
    {
        var (major, courses) = await Setup();
        var s = await Student();
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/profile", new { majorId = major, year = 1 })).StatusCode);
        var picks = new[] { "CF101", "CF102" }.Select(c => new { courseId = courses[c].Course, sectionId = (int?)courses[c].Section }).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await s.PutAsJsonAsync("/api/me/picks", new { picks, step = "core" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.PostAsJsonAsync("/api/me/slots", new { title = "Gym, then lunch", day = 1, start = "17:00", end = "18:00", place = "Sports hall" })).StatusCode);

        // Signed out, the student's link isn't available; signed in, it is, and it stays the same.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().GetAsync("/api/me/calendar")).StatusCode);
        var url = (await Json(await s.GetAsync("/api/me/calendar"))).GetProperty("url").GetString()!;
        Assert.Equal(url, (await Json(await s.GetAsync("/api/me/calendar"))).GetProperty("url").GetString());
        Assert.EndsWith(".ics", url);

        // The calendar app fetches it without signing in.
        var app = factory.Client();
        var feed = await app.GetAsync(new Uri(url).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal("text/calendar", feed.Content.Headers.ContentType?.MediaType);
        var ics = await feed.Content.ReadAsStringAsync();
        Assert.StartsWith("BEGIN:VCALENDAR\r\n", ics);
        Assert.Contains("SUMMARY:Weekly Things · Lecture\r\n", ics);
        // Weekly from week 1's Sunday (6 Sep) to week 15's (13 Dec), each meeting its own event: no repeat rules,
        // which Samsung Calendar dropped.
        Assert.Contains("DTSTART;TZID=Asia/Muscat:20260906T080000\r\n", ics);
        Assert.Contains("DTSTART;TZID=Asia/Muscat:20261213T080000\r\n", ics);
        Assert.DoesNotContain("DTSTART;TZID=Asia/Muscat:20261220T080000\r\n", ics);
        Assert.DoesNotContain("RRULE", ics);
        Assert.Contains("SUMMARY:Dated Thing · Lab\r\n", ics);
        Assert.Contains("DTSTART;TZID=Asia/Muscat:20261019T113000\r\n", ics);
        Assert.Contains("LOCATION:Lab L2\r\n", ics);
        Assert.Contains("SUMMARY:Gym\\, then lunch\r\n", ics);
        Assert.Contains("DTSTART;TZID=Asia/Muscat:20260907T170000\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);

        // The same link as plain text, for ChatGPT and other assistants: the time now, then day by day.
        var textUrl = (await Json(await s.GetAsync("/api/me/calendar"))).GetProperty("textUrl").GetString()!;
        Assert.Equal(url[..^4] + ".txt", textUrl);
        var plain = await app.GetAsync(new Uri(textUrl).PathAndQuery + "?from=2026-10-18&days=2");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal("text/plain", plain.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", plain.Headers.CacheControl?.ToString());
        var text = (await plain.Content.ReadAsStringAsync()).ReplaceLineEndings("\n");
        Assert.Contains("Oman time (UTC+4)", text);
        Assert.Contains("Sun 18 Oct 2026 · week 7\n- 08:00–09:30  CF101 Weekly Things · Lecture · Room R1 · Section 1 · Dr. A\n", text);
        Assert.Contains("Mon 19 Oct 2026 · week 7\n" +
            "- 11:30–13:00  CF102 Dated Thing · Lab · Room Lab L2 · Section 1 · Dr. B\n" +
            "- 17:00–18:00  Gym, then lunch · own slot, not a class · Sports hall\n", text);
        Assert.DoesNotContain("Tue 20 Oct", text);
        // After the last teaching week (week 15 ends Sat 19 Dec), weekly classes stop.
        var after = await (await app.GetAsync(new Uri(textUrl).PathAndQuery + "?from=2026-12-20&days=1")).Content.ReadAsStringAsync();
        Assert.Contains("Sun 20 Dec 2026\n- No classes\n", after.ReplaceLineEndings("\n"));
        Assert.Equal(HttpStatusCode.BadRequest, (await app.GetAsync(new Uri(textUrl).PathAndQuery + "?from=soon")).StatusCode);

        // A reset link replaces the old one: the old one stops working.
        var fresh = (await Json(await s.PostAsync("/api/me/calendar/reset", null))).GetProperty("url").GetString()!;
        Assert.NotEqual(url, fresh);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync(new Uri(url).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync(new Uri(textUrl).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync(new Uri(fresh).PathAndQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.GetAsync("/api/calendar/guess.ics")).StatusCode);
    }
}
