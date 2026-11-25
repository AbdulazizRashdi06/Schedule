using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.ChangeKit;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// Corner cases of class changes found in review: an extra class put on at a time an edited one started from, "also
/// the other sessions" (the same room, and what the preview lists), an absence on a session that was taken away
/// again, calendar ids of a merged weekly class, and sections the teacher hid.
/// </summary>
public class ClassChangeReviewTests
{
    static object Cancel(int section, DateOnly date, string start = "08:00", int[]? sectionIds = null, bool alsoOther = false) =>
        new { kind = "cancel", sectionId = section, date = Iso(date), start, sectionIds, alsoOther };

    static object Room(int section, DateOnly date, string start, string room, bool alsoOther = false) =>
        new { kind = "room", sectionId = section, date = Iso(date), start, room, alsoOther };

    static object Extra(int[] sectionIds, DateOnly to, string newStart, string newEnd) =>
        new { kind = "extra", sectionIds, newDate = Iso(to), newStart, newEnd, room = "B202", type = "lecture" };

    sealed record Ids(int Be, int Ibsm, int Log, int Cs1, int Cs2);

    static async Task<Ids> Sections(TeachWorld w) => new(await w.Section("BE1101", "1"), await w.Section("IBSM1101", "1"), await w.Section("LOG1101", "1"),
        await w.Section("CS2201", "1"), await w.Section("CS2201", "2"));

    static List<string> Others(JsonElement preview) =>
        preview.GetProperty("others").EnumerateArray().Select(o => $"{o.GetProperty("start").GetString()} {o.GetProperty("room").GetString()}").ToList();

    [Fact]
    public async Task A_new_extra_class_at_the_time_an_edited_one_started_from_is_a_class_of_its_own()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (thu, later) = (w.Date(0, 4), w.Date(1, 4));

        // An extra class on Thursday, then edited to the Thursday after: it keeps the tag of its first time.
        var first = await w.Teacher2.Made(Extra([s.Cs1], thu, "15:00", "16:30"));
        await Json(await w.Teacher2.Replace(first, Extra([s.Cs1], later, "15:00", "16:30")));
        var firstTag = Assert.Single(await w.Live()).Tag;
        Assert.Equal($"tchange-{s.Cs1}-x{thu:yyyyMMdd}-1500", firstTag);
        var waiting = Assert.Single(await w.Notices(TeachNotice.Students));

        // A new extra class at that first time: the first one's unsent notice is still there.
        var second = await w.Teacher2.Made(Extra([s.Cs1], thu, "15:00", "16:30"));
        var secondTag = (await w.Live()).Single(c => c.GroupId == second).Tag;
        Assert.NotEqual(firstTag, secondTag);
        Assert.True(secondTag.Length <= 48);
        Assert.Equal([waiting.Id, waiting.Id + 1], (await w.Notices(TeachNotice.Students)).Select(n => n.Id));

        // The first one's notice goes out. The second is undone before anyone was told of it: nobody hears
        // "extra class removed" about a class they never heard of.
        await w.Db(db => db.TeachNotices.Where(n => n.Id == waiting.Id).ExecuteUpdateAsync(u => u.SetProperty(n => n.ClaimedAt, w.Time.GetUtcNow()).SetProperty(n => n.SentAt, w.Time.GetUtcNow())));
        await Json(await w.Teacher2.Undo(second));
        Assert.Equal([(waiting.Id, "change")], (await w.Notices(TeachNotice.Students)).Select(n => (n.Id, n.Kind)));

        // The same after the first one is gone for good: a third at that time doesn't answer for it either.
        var third = await w.Teacher2.Made(Extra([s.Cs1], thu, "15:00", "16:30"));
        Assert.Equal(3, (await w.Rows()).Select(c => c.Tag).Distinct().Count());
        await Json(await w.Teacher2.Undo(third));
        Assert.Equal([(waiting.Id, "change")], (await w.Notices(TeachNotice.Students)).Select(n => (n.Id, n.Kind)));
    }

    [Fact]
    public async Task Also_the_other_session_leaves_one_that_is_already_in_that_room_alone()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var tue = w.Date(0, 2);
        // Section 2 meets twice that Tuesday: 08:00 in B105 and 13:15 in B202.
        await w.AddMeeting(s.Cs2, tue, 0, "13:15", "14:45", "B202");

        // Into B202, with the tick box: the afternoon session is in B202 already, so it isn't listed and isn't changed.
        Assert.Empty(Others(await Json(await w.Teacher2.Preview(Room(s.Cs2, tue, "08:00", "b 202", alsoOther: true)))));
        var made = await Json(await w.Teacher2.Change(Room(s.Cs2, tue, "08:00", "B202", alsoOther: true)));
        Assert.Single(Groups(made));
        var row = Assert.Single(await w.Live());
        Assert.Equal((new TimeOnly(8, 0), "B105", "B202"), (row.OrigStart!.Value, row.OrigRoom, row.NewRoom));
        Assert.DoesNotContain(await w.Notices(TeachNotice.Students), n => n.Body.Contains("(was B202)"));

        // Into another room, both go.
        var next = w.Date(1, 2);
        await w.AddMeeting(s.Cs2, next, 0, "13:15", "14:45", "B202");
        Assert.Equal(["13:15 B202"], Others(await Json(await w.Teacher2.Preview(Room(s.Cs2, next, "08:00", "C1", alsoOther: true)))));
        Assert.Equal(2, Groups(await Json(await w.Teacher2.Change(Room(s.Cs2, next, "08:00", "C1", alsoOther: true)))).Count);
    }

    [Fact]
    public async Task The_preview_lists_exactly_the_other_sessions_the_write_changes()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var sun = w.Date(0, 0);
        // One of the three codes of the Sunday class has a second session that day.
        await w.AddMeeting(s.Ibsm, sun, 0, "13:15", "14:45", "A200");

        // Opened from another code with every code ticked: that session would be cancelled too, and the preview says so.
        Assert.Equal(["13:15 A200"], Others(await Json(await w.Teacher1.Preview(Cancel(s.Be, sun, alsoOther: true)))));
        // With that code unticked, nothing else is touched.
        Assert.Empty(Others(await Json(await w.Teacher1.Preview(Cancel(s.Be, sun, sectionIds: [s.Be, s.Log], alsoOther: true)))));

        var made = await Json(await w.Teacher1.Change(Cancel(s.Be, sun, alsoOther: true)));
        Assert.Equal(2, Groups(made).Count);
        Assert.Equal(new[] { (s.Be, "08:00"), (s.Ibsm, "08:00"), (s.Ibsm, "13:15"), (s.Log, "08:00") }.Order(),
            (await w.Live()).Select(c => (c.SectionId!.Value, c.OrigStart!.Value.ToString("HH:mm"))).Order());
    }

    [Fact]
    public async Task An_absence_on_a_session_a_change_put_on_and_took_away_is_no_longer_counted()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var course = await w.CourseOf(s.Cs1);
        var student = w.Students[0];
        async Task<List<string>> Missed() => (await Json(await student.GetAsync("/api/me/absences"))).GetProperty("absences").EnumerateArray()
            .Select(a => $"{a.GetProperty("date").GetString()} {a.GetProperty("start").GetString()}").ToList();
        async Task Mark(DateOnly date, string start) =>
            Assert.True((await student.PutAsJsonAsync("/api/me/absences", new { courseId = course, date = Iso(date), start, missed = true })).IsSuccessStatusCode);

        // Wednesday morning of the first week. Monday's ordinary class was missed; and a row from before the
        // timetable changed, which matches no session, is kept as it always was.
        var (mon, wed) = (w.Date(0, 1), w.Date(0, 3));
        w.GoTo(wed, 9);
        await Mark(mon, "08:00");
        await w.Db(async db =>
        {
            db.Absences.Add(new Absence { StudentId = w.StudentRowIds[0], CourseId = course, Date = w.Date(0, 0), Start = new TimeOnly(17, 0), CreatedAt = w.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        });
        string[] kept = [$"{Iso(w.Date(0, 0))} 17:00", $"{Iso(mon)} 08:00"];

        // An extra class this afternoon, marked missed, then removed by the teacher: it can't be missed any more.
        var t2 = await w.Fresh(TeachWorld.Teacher2Email);
        var extra = await t2.Made(Extra([s.Cs1], wed, "15:00", "16:30"));
        await Mark(wed, "15:00");
        Assert.Equal([.. kept, $"{Iso(wed)} 15:00"], await Missed());
        await Json(await t2.Undo(extra));
        Assert.Equal(kept, await Missed());

        // Next Monday's class moved to this afternoon, marked missed, then moved once more.
        var move = await t2.Made(new { kind = "move", sectionId = s.Cs1, date = Iso(w.Date(1, 1)), start = "08:00", newDate = Iso(wed), newStart = "15:00" });
        Assert.Equal([.. kept, $"{Iso(wed)} 15:00"], await Missed());
        await Json(await t2.Replace(move, new { kind = "move", newDate = Iso(wed), newStart = "16:45" }));
        Assert.Equal(kept, await Missed());
        // A class is on at that time again: the mark counts again.
        await t2.Made(Extra([s.Cs1], wed, "15:00", "16:30"));
        Assert.Equal([.. kept, $"{Iso(wed)} 15:00"], await Missed());
    }

    [Fact]
    public async Task Each_date_of_a_merged_weekly_class_has_its_own_calendar_id()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        // One weekly class under three codes, Wednesdays 15:00 in C300; one code is cancelled on two Wednesdays.
        foreach (var id in new[] { s.Be, s.Ibsm, s.Log }) await w.AddMeeting(id, null, 3, "15:00", "16:30", "C300");
        var weds = new[] { w.Date(0, 3), w.Date(1, 3) };
        foreach (var wed in weds) await w.Teacher1.Made(Cancel(s.Ibsm, wed, "15:00", sectionIds: [s.Ibsm]));

        var items = await w.Db(async db =>
        {
            var semester = await db.Semesters.SingleAsync(x => x.Id == w.Semester);
            var teacher = await db.Teachers.SingleAsync(t => t.Id == w.Teacher1Id);
            return (await TeacherSchedule.ItemsAsync(db, semester, [teacher], slots: true, planner: false, default))[teacher.Id];
        });
        // The two codes still meeting on each of those dates are one item per date, named by that date.
        var split = items.Where(i => i.Date is DateOnly d && weds.Contains(d) && i.Start == new TimeOnly(15, 0)).OrderBy(i => i.Date).ToList();
        Assert.Equal(["BE1101 · LOG1101", "BE1101 · LOG1101"], split.Select(i => i.Code));
        Assert.Equal(weds.Select(d => $"class-{s.Be}-{split[0].MeetingId}-{d:yyyyMMdd}@classy"), split.Select(i => i.Uid));

        var url = (await Json(await w.Teacher1.GetAsync("/api/teach/calendar"))).GetProperty("url").GetString()!;
        var ics = (await w.Browser().GetStringAsync(new Uri(url).PathAndQuery)).Replace("\r\n ", "");
        var uids = ics.Split("\r\n").Where(l => l.StartsWith("UID:")).ToList();
        Assert.Equal(uids.Count, uids.Distinct().Count());
        foreach (var wed in weds) Assert.Equal(2, uids.Count(u => u.EndsWith($"-{wed:yyyyMMdd}@classy")));
    }

    [Fact]
    public async Task A_section_the_teacher_hid_is_not_part_of_their_class()
    {
        using var w = await TeachWorld.Create();
        var s = await Sections(w);
        var (sun, next) = (w.Date(0, 0), w.Date(1, 0));
        // "Not mine": one of the three codes under the teacher's name is hidden.
        await w.Teacher1.AddSection(s.Ibsm, on: false);

        // It isn't offered, isn't ticked by default and can't be ticked along.
        var preview = await Json(await w.Teacher1.Preview(Cancel(s.Be, sun)));
        Assert.Equal([s.Be, s.Log], preview.GetProperty("codes").EnumerateArray().Select(c => c.GetProperty("sectionId").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Teacher1.Change(Cancel(s.Be, sun, sectionIds: [s.Be, s.Ibsm]))).StatusCode);
        await w.Teacher1.Made(Cancel(s.Be, sun));
        Assert.Equal([s.Be, s.Log], (await w.Live()).Select(c => c.SectionId!.Value).Order());
        Assert.DoesNotContain(await w.Notices(TeachNotice.Students), n => n.SectionId == s.Ibsm);

        // The teacher's timetable agrees: the block is the two codes.
        var timetable = await Json(await w.Teacher1.GetAsync("/api/teach/timetable"));
        Assert.Equal(["BE1101", "LOG1101"], timetable.GetProperty("classes").EnumerateArray().Select(c => c.GetProperty("code").GetString()).Order());

        // They may still change the hidden one itself (it is under their name): alone, not as part of the block.
        await w.Teacher1.Made(Cancel(s.Ibsm, next));
        Assert.Equal([s.Ibsm], (await w.Live()).Where(c => c.OrigDate == next).Select(c => c.SectionId!.Value));
    }
}
