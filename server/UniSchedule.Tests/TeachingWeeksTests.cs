using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniSchedule.Api.Data;

namespace UniSchedule.Tests;

/// <summary>
/// The week labels students see (/api/me's semester.weeks) and the weeks classes meet in (the timetable's span, which the
/// calendar feed, reminders and groups use too) come from one place, so they agree whatever staff have set.
/// </summary>
public class TeachingWeeksTests
{
    static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    sealed class World : IDisposable
    {
        public ApiFactory Factory { get; } = new();
        public HttpClient Owner = null!, Student = null!;
        public int Semester, Course, Section, Section2;
        public void Dispose() => Factory.Dispose();

        /// <summary>Fall 2026, published, one course with the given meetings in section 1 (and an empty section 2), picked by one student.</summary>
        public async Task Init(params Meeting[] meetings)
        {
            await Factory.AddAdmin("weeks-owner", "weeks-password-1", AdminRole.Owner, false);
            Owner = Factory.Client();
            Assert.Equal(HttpStatusCode.OK, (await Owner.PostAsJsonAsync("/api/auth/admin-login", new { username = "weeks-owner", password = "weeks-password-1" })).StatusCode);
            Student = Factory.Client();
            var code = (await Json(await Student.PostAsJsonAsync("/api/auth/request-code", new { universityId = "24-9301" }))).GetProperty("testCode").GetString();
            await Json(await Student.PostAsJsonAsync("/api/auth/verify", new { universityId = "24-9301", code }));
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var semester = new Semester { Name = "Fall 2026", Term = "Fall", Year = 2026, IsCurrent = true, PublishedAt = DateTimeOffset.UtcNow };
            var major = new Major { Code = "WK", Name = "Weeks", Family = "WK" };
            db.AddRange(semester, major); await db.SaveChangesAsync();
            Semester = semester.Id;
            var course = new Course { SemesterId = Semester, Code = "WK101", Name = "Weeks course", Type = CourseType.Core, Year = 1 };
            course.Majors.Add(new() { MajorId = major.Id });
            course.Sections.Add(new() { Number = "1", Meetings = [.. meetings] });
            course.Sections.Add(new() { Number = "2", Meetings = [new() { Day = 2, Start = new(10, 0), End = new(11, 0), Room = "R2", Date = meetings.Any(m => m.Date is null) ? null : new(2026, 9, 22) }] });
            db.Courses.Add(course); await db.SaveChangesAsync();
            Course = course.Id; Section = course.Sections[0].Id; Section2 = course.Sections[1].Id;
            var s = await db.Students.SingleAsync(x => x.UniversityId == "24-9301");
            s.MajorId = major.Id; s.Year = 1; s.SetupSemesterId = Semester; s.SetupStep = "done"; s.SetupCompletedAt = DateTimeOffset.UtcNow;
            db.Picks.Add(new() { StudentId = s.Id, CourseId = Course, SectionId = Section });
            await db.SaveChangesAsync();
        }

        public Task<HttpResponseMessage> SetWeeks(string? week1Start, int? lastWeek) =>
            Owner.PutAsJsonAsync($"/api/admin/semesters/{Semester}/weeks", new { week1Start, lastWeek });

        /// <summary>(week 1, last week) as the labels have them, and (first, last) of the span classes meet in.</summary>
        public async Task<(string? Week1, int? LastWeek, string First, string Last)> Read()
        {
            var weeks = (await Json(await Student.GetAsync("/api/me"))).GetProperty("semester").GetProperty("weeks");
            var span = (await Json(await Student.GetAsync("/api/me/timetable"))).GetProperty("span");
            var lastWeek = weeks.GetProperty("lastWeek");
            return (weeks.GetProperty("week1Start").GetString(), lastWeek.ValueKind == JsonValueKind.Null ? null : lastWeek.GetInt32(),
                span.GetProperty("first").GetString()!, span.GetProperty("last").GetString()!);
        }
    }

    /// <summary>When the labels number the weeks, the span starts on week 1's Sunday and ends on the last week's Saturday.</summary>
    static void Agree((string? Week1, int? LastWeek, string First, string Last) r)
    {
        Assert.NotNull(r.Week1);
        Assert.Equal(r.Week1, r.First);
        Assert.Equal(DateOnly.Parse(r.Week1!).AddDays(r.LastWeek!.Value * 7 - 1).ToString("yyyy-MM-dd"), r.Last);
    }

    [Fact]
    public async Task A_fully_dated_timetable_gives_the_weeks_and_labels_and_span_agree_in_every_state()
    {
        using var w = new World();
        // Wed 23 Sep and Mon 7 Dec 2026, so the dates make weeks 1 to 12 from Sunday 20 Sep.
        await w.Init(new Meeting { Day = 3, Start = new(8, 0), End = new(9, 30), Room = "R1", Date = new(2026, 9, 23) },
            new Meeting { Day = 1, Start = new(8, 0), End = new(9, 30), Room = "R1", Date = new(2026, 12, 7) });

        // Nothing set: both from the class dates.
        var none = await w.Read();
        Assert.Equal(("2026-09-20", (int?)12), (none.Week1, none.LastWeek));
        Agree(none);

        // Only week 1 set: the last week still comes from the last class.
        Assert.Equal(HttpStatusCode.OK, (await w.SetWeeks("2026-09-06", null)).StatusCode);
        var start = await w.Read();
        Assert.Equal(("2026-09-06", (int?)14), (start.Week1, start.LastWeek));
        Agree(start);

        // Only a last week: refused, it counts from week 1.
        var refused = await w.SetWeeks(null, 10);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("week 1", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await w.SetWeeks("", 10)).StatusCode);

        // One already stored that way is ignored by both, not honoured by the labels alone.
        using (var scope = w.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await db.Semesters.Where(s => s.Id == w.Semester).ExecuteUpdateAsync(u => u.SetProperty(s => s.Week1Start, (DateOnly?)null).SetProperty(s => s.LastWeek, 10));
        }
        var half = await w.Read();
        Assert.Equal(("2026-09-20", (int?)12), (half.Week1, half.LastWeek));
        Agree(half);
        Assert.False((await Json(await w.Owner.GetAsync($"/api/admin/semesters/{w.Semester}/weeks"))).GetProperty("set").GetBoolean());

        // Both set.
        Assert.Equal(HttpStatusCode.OK, (await w.SetWeeks("2026-09-20", 10)).StatusCode);
        var both = await w.Read();
        Assert.Equal(("2026-09-20", (int?)10, "2026-11-28"), (both.Week1, both.LastWeek, both.Last));
        Agree(both);
    }

    [Fact]
    public async Task One_weekly_class_added_to_a_dated_timetable_keeps_the_weeks_from_the_class_dates()
    {
        using var w = new World();
        await w.Init(new Meeting { Day = 3, Start = new(8, 0), End = new(9, 30), Room = "R1", Date = new(2026, 9, 23) },
            new Meeting { Day = 1, Start = new(8, 0), End = new(9, 30), Room = "R1", Date = new(2026, 12, 7) });
        using (var scope = w.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var other = new Course { SemesterId = w.Semester, Code = "WK102", Name = "Another dated course", Type = CourseType.Core, Year = 1 };
            other.Sections.Add(new() { Number = "1", Meetings = [new() { Day = 4, Start = new(12, 0), End = new(13, 0), Room = "R3", Date = new(2026, 10, 1) }] });
            db.Courses.Add(other); await db.SaveChangesAsync();
        }
        var before = await w.Read();
        Assert.Equal(("2026-09-20", (int?)12), (before.Week1, before.LastWeek));

        // Staff add a weekly tutorial to one section in the section editor.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsJsonAsync($"/api/admin/sections/{w.Section2}", new
        {
            instructor = "",
            meetings = new object[]
            {
                new { day = 2, start = "10:00", end = "11:00", room = "R2", date = "2026-09-22" },
                new { day = 0, start = "14:00", end = "15:00", room = "R2", date = (string?)null },
            },
        })).StatusCode);

        // Still numbered from the class dates, and the tutorial meets in those weeks.
        var after = await w.Read();
        Assert.Equal(before, after);
        Agree(after);
    }

    [Fact]
    public async Task With_weekly_classes_a_dated_class_does_not_set_the_weeks()
    {
        using var w = new World();
        await w.Init(new Meeting { Day = 0, Start = new(8, 0), End = new(9, 30), Room = "R1" },
            new Meeting { Day = 3, Start = new(10, 0), End = new(11, 30), Room = "Lab", Date = new(2026, 9, 23) });

        // Nothing set: the term's usual 16 weeks from the Sunday before 1 September, not numbered.
        var none = await w.Read();
        Assert.Equal(((string?)null, (int?)null, "2026-08-30", "2026-12-19"), none);

        // Only week 1 set: 16 weeks on, and the labels end where the classes do.
        Assert.Equal(HttpStatusCode.OK, (await w.SetWeeks("2026-09-20", null)).StatusCode);
        var start = await w.Read();
        Assert.Equal(("2026-09-20", (int?)16), (start.Week1, start.LastWeek));
        Agree(start);

        // Both set.
        Assert.Equal(HttpStatusCode.OK, (await w.SetWeeks("2026-09-20", 10)).StatusCode);
        Agree(await w.Read());

        // Cleared again: back to the unnumbered default.
        Assert.Equal(HttpStatusCode.OK, (await w.SetWeeks(null, null)).StatusCode);
        Assert.Equal(none, await w.Read());
    }

    [Fact]
    public async Task A_section_picked_after_it_was_edited_is_not_marked_changed()
    {
        using var w = new World();
        await w.Init(new Meeting { Day = 0, Start = new(8, 0), End = new(9, 30), Room = "R1" });
        // Staff move section 2 to another room after publishing: it is marked changed.
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsJsonAsync($"/api/admin/sections/{w.Section2}",
            new { instructor = "", meetings = new[] { new { day = 2, start = "10:00", end = "11:00", room = "R9" } } })).StatusCode);

        async Task<bool> Changed()
        {
            var classes = (await Json(await w.Student.GetAsync("/api/me/timetable"))).GetProperty("classes");
            return Assert.Single(classes.EnumerateArray()).GetProperty("changed").GetBoolean();
        }
        // The student switches into it afterwards: they chose it as it is now.
        Assert.Equal(HttpStatusCode.OK, (await w.Student.PutAsJsonAsync("/api/me/picks", new { picks = new[] { new { courseId = w.Course, sectionId = (int?)w.Section2 } } })).StatusCode);
        Assert.False(await Changed());

        // A later edit still marks it.
        await Task.Delay(20);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PutAsJsonAsync($"/api/admin/sections/{w.Section2}",
            new { instructor = "", meetings = new[] { new { day = 2, start = "10:00", end = "11:00", room = "R10" } } })).StatusCode);
        Assert.True(await Changed());
    }
}
