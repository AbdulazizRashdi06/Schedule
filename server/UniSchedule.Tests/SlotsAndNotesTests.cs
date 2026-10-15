using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>Custom slots and class notes: who can see, change and report what.</summary>
public class SlotsAndNotesTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    static int next = 7000;
    static string NewId() => $"25-{Interlocked.Increment(ref next):D4}";
    static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    async Task<HttpClient> Staff(string username, AdminRole role)
    {
        await factory.AddAdmin(username, "staff-password-1", role, mustChange: false);
        var c = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/admin-login", new { username, password = "staff-password-1" })).StatusCode);
        return c;
    }

    async Task<HttpClient> Student()
    {
        var id = NewId();
        var c = factory.Client();
        var code = (await Json(await c.PostAsJsonAsync("/api/auth/request-code", new { universityId = id }))).GetProperty("testCode").GetString();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/verify", new { universityId = id, code })).StatusCode);
        return c;
    }

    record World(HttpClient Owner, int Course, int Section1, int Section2, int Major, int OtherMajor);

    /// <summary>One course with section 1 (dated: Mon 12 Oct, Mon 19 Oct 2026) and section 2 (every Tuesday), published.</summary>
    async Task<World> Setup(string tag, int year)
    {
        var owner = await Staff($"owner-notes-{tag.ToLowerInvariant()}", AdminRole.Owner);
        var csv = "code,name,major,type,year,section,instructor,date,days,time,room\n" +
            $"{tag}101,Notes Course,{tag}M,Core,1,1,A,2026-10-12,,08:00-09:30,R1\n" +
            $"{tag}101,Notes Course,{tag}M,Core,1,1,A,2026-10-19,,08:00-09:30,R1\n" +
            $"{tag}101,Notes Course,{tag}M,Core,1,2,B,,Tue,09:45-11:15,R2\n" +
            $"{tag}201,Other Course,{tag}X,Core,1,1,C,,Wed,08:00-09:30,R3\n";
        var sem = (await Json(await owner.PostAsJsonAsync("/api/admin/semesters", new { term = "Fall", year }))).GetProperty("id").GetInt32();
        using var file = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)), "file", "n.csv" } };
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/upload/apply", file)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync($"/api/admin/semesters/{sem}/publish", null)).StatusCode);
        var course = (await Json(await owner.GetAsync($"/api/admin/semesters/{sem}/courses"))).EnumerateArray().First(c => c.GetProperty("code").GetString() == $"{tag}101").GetProperty("id").GetInt32();
        var sections = (await Json(await owner.GetAsync($"/api/admin/courses/{course}"))).GetProperty("course").GetProperty("sections").EnumerateArray()
            .ToDictionary(s => s.GetProperty("number").GetString()!, s => s.GetProperty("id").GetInt32());
        var majors = (await Json(await owner.GetAsync("/api/admin/majors"))).EnumerateArray().ToDictionary(m => m.GetProperty("code").GetString()!, m => m.GetProperty("id").GetInt32());
        return new(owner, course, sections["1"], sections["2"], majors[$"{tag}M"], majors[$"{tag}X"]);
    }

    /// <summary>A student taking the course in <paramref name="section"/> (or not taking it at all when null).</summary>
    async Task<HttpClient> Classmate(World w, int? section)
    {
        var s = await Student();
        Assert.Equal(HttpStatusCode.NoContent, (await s.PutAsJsonAsync("/api/me/profile", new { majorId = section is null ? w.OtherMajor : w.Major, year = 1 })).StatusCode);
        if (section is not null)
            Assert.Equal(HttpStatusCode.OK, (await s.PutAsJsonAsync("/api/me/picks", new { picks = new[] { new { courseId = w.Course, sectionId = section } }, step = "core" })).StatusCode);
        return s;
    }

    static async Task<List<JsonElement>> Notes(HttpClient c, int course) =>
        (await Json(await c.GetAsync($"/api/me/notes?courseId={course}"))).EnumerateArray().ToList();

    [Fact]
    public async Task Slots_belong_to_their_student()
    {
        var w = await Setup("SA", 2061);
        var a = await Classmate(w, w.Section1);
        var b = await Classmate(w, w.Section2);

        var weekly = await Json(await a.PostAsJsonAsync("/api/me/slots", new { title = "Gym", day = 2, start = "17:00", end = "18:00", place = "Gym hall" }));
        var dated = await a.PostAsJsonAsync("/api/me/slots", new { title = "Dentist", date = "2026-10-21", start = "10:00", end = "11:00", note = "Bring the card" });
        Assert.Equal(HttpStatusCode.OK, dated.StatusCode);
        // 21 Oct 2026 is a Wednesday.
        Assert.Equal(3, (await Json(dated)).GetProperty("day").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/me/slots", new { title = "Backwards", day = 1, start = "12:00", end = "11:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/me/slots", new { title = "", day = 1, start = "10:00", end = "11:00" })).StatusCode);

        Assert.Equal(2, (await Json(await a.GetAsync("/api/me/slots"))).GetArrayLength());
        Assert.Equal(2, (await Json(await a.GetAsync("/api/me/timetable"))).GetProperty("slots").GetArrayLength());
        Assert.Equal(0, (await Json(await b.GetAsync("/api/me/slots"))).GetArrayLength());

        var slotId = weekly.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/me/slots/{slotId}", new { title = "Mine now", day = 2, start = "17:00", end = "18:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/me/slots/{slotId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsJsonAsync($"/api/me/slots/{slotId}", new { title = "Gym", day = 4, start = "17:00", end = "18:30" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/me/slots/{slotId}")).StatusCode);
        Assert.Equal(1, (await Json(await a.GetAsync("/api/me/slots"))).GetArrayLength());
    }

    [Fact]
    public async Task Notes_are_seen_only_by_the_right_people()
    {
        var w = await Setup("NA", 2062);
        var author = await Classmate(w, w.Section1);
        var sameSection = await Classmate(w, w.Section1);
        var otherSection = await Classmate(w, w.Section2);
        var notTaking = await Classmate(w, null);

        async Task<int> Post(object body, HttpStatusCode expect = HttpStatusCode.OK)
        {
            var r = await author.PostAsJsonAsync("/api/me/notes", body);
            Assert.Equal(expect, r.StatusCode);
            return r.StatusCode == HttpStatusCode.OK ? (await Json(r)).GetProperty("id").GetInt32() : 0;
        }
        var privateNote = await Post(new { courseId = w.Course, text = "my own reminder", isPublic = false });
        var publicCourse = await Post(new { courseId = w.Course, text = "Lecture slides are on Moodle", isPublic = true });
        var publicSession = await Post(new { courseId = w.Course, date = "2026-10-19", text = "Quiz this day", isPublic = true });
        // Section 1 has no class on Tuesday 20 Oct.
        await Post(new { courseId = w.Course, date = "2026-10-20", text = "Wrong day", isPublic = true }, HttpStatusCode.BadRequest);

        var mine = await Notes(author, w.Course);
        Assert.Equal(3, mine.Count);
        Assert.All(mine, n => Assert.True(n.GetProperty("mine").GetBoolean()));

        var classmate = await Notes(sameSection, w.Course);
        Assert.Equal([publicSession, publicCourse], classmate.Select(n => n.GetProperty("id").GetInt32()));
        Assert.All(classmate, n => Assert.StartsWith("25-", n.GetProperty("author").GetString()));

        // Other section: the whole-course note, not section 1's session note.
        Assert.Equal([publicCourse], (await Notes(otherSection, w.Course)).Select(n => n.GetProperty("id").GetInt32()));
        Assert.Empty(await Notes(notTaking, w.Course));
        Assert.Equal(HttpStatusCode.Forbidden, (await notTaking.PostAsJsonAsync("/api/me/notes", new { courseId = w.Course, text = "hi", isPublic = true })).StatusCode);

        // Only the author changes or deletes a note.
        Assert.Equal(HttpStatusCode.NotFound, (await sameSection.PutAsJsonAsync($"/api/me/notes/{publicCourse}", new { text = "changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sameSection.DeleteAsync($"/api/me/notes/{publicCourse}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await author.PutAsJsonAsync($"/api/me/notes/{privateNote}", new { text = "edited", isPublic = false })).StatusCode);

        // Note dots on the timetable.
        var counts = (await Json(await sameSection.GetAsync("/api/me/timetable"))).GetProperty("classes")[0].GetProperty("notes");
        Assert.Equal(1, counts.GetProperty("classWide").GetInt32());
        Assert.Equal(1, counts.GetProperty("dates").GetProperty("2026-10-19").GetInt32());
    }

    [Fact]
    public async Task Reports_count_once_and_staff_can_hide_a_note()
    {
        var w = await Setup("NB", 2063);
        var author = await Classmate(w, w.Section1);
        var reader = await Classmate(w, w.Section1);
        var note = (await Json(await author.PostAsJsonAsync("/api/me/notes", new { courseId = w.Course, text = "something rude", isPublic = true }))).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync($"/api/me/notes/{note}/report", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync($"/api/me/notes/{note}/report", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await author.PostAsync($"/api/me/notes/{note}/report", null)).StatusCode);

        var rsa = await Staff("rsa-notes-nb", AdminRole.Rsa);
        var listed = (await Json(await rsa.GetAsync($"/api/admin/courses/{w.Course}/notes")))[0];
        Assert.Equal(1, listed.GetProperty("reports").GetInt32());
        Assert.StartsWith("25-", listed.GetProperty("author").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await rsa.PutAsJsonAsync($"/api/admin/notes/{note}/hidden", new { hidden = true })).StatusCode);

        Assert.Empty(await Notes(reader, w.Course));
        Assert.True((await Notes(author, w.Course))[0].GetProperty("hidden").GetBoolean());
        // Students can't reach the moderation list.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/admin/courses/{w.Course}/notes")).StatusCode);
    }

    [Fact]
    public async Task Notes_are_rate_limited()
    {
        var w = await Setup("NC", 2064);
        var s = await Classmate(w, w.Section2);
        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.OK, (await s.PostAsJsonAsync("/api/me/notes", new { courseId = w.Course, text = $"note {i}" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await s.PostAsJsonAsync("/api/me/notes", new { courseId = w.Course, text = "one too many" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.PostAsJsonAsync("/api/me/notes", new { courseId = w.Course, text = new string('x', 501) })).StatusCode);
    }
}
