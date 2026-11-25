using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.ChangeKit;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// Writes that cross: each change is one transaction that takes the database's write lock before it reads, so two
/// people acting on one session at the same moment end with one change, one undo and one notice.
/// </summary>
public class ClassChangeConcurrencyTests
{
    /// <summary>Sends the requests as close together as the test host allows.</summary>
    static async Task<List<HttpStatusCode>> Together(params Func<Task<HttpResponseMessage>>[] requests)
    {
        using var go = new ManualResetEventSlim();
        var tasks = requests.Select(send => Task.Run(async () =>
        {
            go.Wait();
            return (await send()).StatusCode;
        })).ToList();
        go.Set();
        return [.. await Task.WhenAll(tasks)];
    }

    [Fact]
    public async Task Two_teachers_changing_the_same_session_at_once_make_one_change()
    {
        using var w = await TeachWorld.Create();
        var cs = await w.Section("CS2201", "1");
        await w.Teacher1.AddSection(cs);
        foreach (var date in new[] { w.Date(0, 1), w.Date(1, 1) })
        {
            var statuses = await Together(
                () => w.Teacher1.Change(new { kind = "cancel", sectionId = cs, date = Iso(date), start = "08:00" }),
                () => w.Teacher2.Change(new { kind = "room", sectionId = cs, date = Iso(date), start = "08:00", room = "B200" }),
                () => w.Teacher2.Change(new { kind = "move", sectionId = cs, date = Iso(date), start = "08:00", newDate = Iso(date), newStart = "12:00" }));
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.Conflict], statuses.Order());
            var live = Assert.Single(await w.Live(), c => c.OrigDate == date);
            // Students are told about the one that was made, once.
            Assert.Equal(live.GroupId, Assert.Single(await w.Notices(TeachNotice.Students), n => n.Tag == live.Tag).GroupId);
        }
        Assert.Equal(2, (await w.Rows()).Count);

        // Extra classes for one section at one time, sent together: one.
        var extras = await Together(Enumerable.Range(0, 4).Select(_ => (Func<Task<HttpResponseMessage>>)(() =>
            w.Teacher2.Change(new { kind = "extra", sectionIds = new[] { cs }, newDate = Iso(w.Date(0, 4)), newStart = "15:00", newEnd = "16:00", type = "lecture" }))).ToArray());
        Assert.Equal(1, extras.Count(x => x == HttpStatusCode.OK));
        Assert.Equal(3, extras.Count(x => x == HttpStatusCode.Conflict));
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task Undo_sent_twice_ends_the_change_once_and_tells_everyone_once()
    {
        using var w = await TeachWorld.Create();
        var be = await w.Section("BE1101", "1");
        var group = await w.Teacher1.Made(new { kind = "cancel", sectionId = be, date = Iso(w.Date(0, 0)), start = "08:00" });
        await w.MarkSent();

        var statuses = await Together(() => w.Teacher1.Undo(group), () => w.Teacher1.Undo(group), () => w.Teacher1.Undo(group));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.Conflict], statuses.Order());
        Assert.Empty(await w.Live());
        // One "back on" for each of the three sections, not three.
        var told = (await w.Notices(TeachNotice.Students)).Where(n => n.Kind == "back-on").ToList();
        Assert.Equal(3, told.Count);
        Assert.Equal(3, told.Select(n => n.SectionId).Distinct().Count());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Teacher1.Undo(group)).StatusCode);
        Assert.Equal(3, (await w.Notices(TeachNotice.Students)).Count(n => n.Kind == "back-on"));
    }

    [Fact]
    public async Task A_teachers_undo_racing_a_staff_undo_or_an_edit_ends_one_way()
    {
        using var w = await TeachWorld.Create();
        var cs = await w.Section("CS2201", "1");
        var owner = TeachWorld.ViewAs(w.Owner, TeacherKeys.ForName(TeachWorld.Teacher2Name));
        await w.Teacher1.AddSection(cs);

        // The teacher and an owner both undo.
        var first = await w.Teacher2.Made(new { kind = "cancel", sectionId = cs, date = Iso(w.Date(0, 1)), start = "08:00" });
        await w.MarkSent();
        var undone = await Together(() => w.Teacher2.Undo(first), () => owner.Undo(first));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], undone.Order());
        Assert.Single(await w.Notices(TeachNotice.Students), n => n.Kind == "back-on");
        var row = Assert.Single(await w.Rows());
        Assert.Equal("undone", row.EndReason);
        Assert.Contains(row.EndedBy, new[] { $"teacher:{TeachWorld.Teacher2Email}", $"staff:{TeachWorld.OwnerName}" });
        // The co-teacher is told once, whoever it was.
        Assert.Single(await w.Notices(TeachNotice.ToTeacher), n => n.Kind == "undone" && n.TeacherId == w.Teacher1Id);

        // One undoes while another edits: either the edit lands on a live change and then nothing is left to undo, or
        // the undo comes first and the edit finds nothing. Never an edited change that is also undone.
        var second = await w.Teacher2.Made(new { kind = "cancel", sectionId = cs, date = Iso(w.Date(1, 1)), start = "08:00" });
        var crossed = await Together(() => w.Teacher2.Undo(second), () => w.Teacher1.Replace(second, new { kind = "room", room = "B200" }));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], crossed.Order());
        var rows = (await w.Rows()).Where(c => c.OrigDate == w.Date(1, 1)).ToList();
        if (crossed[0] == HttpStatusCode.OK)
        {
            Assert.Equal("undone", Assert.Single(rows).EndReason);
            Assert.Empty(await w.Live());
        }
        else
        {
            Assert.Equal(["replaced", null], rows.Select(c => c.EndReason));
            Assert.Equal("room", Assert.Single(await w.Live()).Kind);
        }
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task A_change_posted_while_a_timetable_apply_runs_is_either_reconciled_or_refused()
    {
        using var w = await TeachWorld.Create();
        var cs = await w.Section("CS2201", "1");
        var timetable = await (await w.Owner.GetAsync($"/api/admin/semesters/{w.Semester}/export.csv")).Content.ReadAsStringAsync();
        foreach (var date in new[] { w.Date(0, 1), w.Date(1, 1) })
        {
            // The upload moves the very session the teacher is cancelling.
            var line = timetable.Split('\n').Single(l => l.StartsWith("CS2201,") && l.Contains(Iso(date)));
            timetable = timetable.Replace(line, line.Replace("08:00-09:30", "10:00-11:30"));
            var upload = System.Text.Encoding.UTF8.GetBytes(timetable);
            var statuses = await Together(
                () => w.Teacher2.Change(new { kind = "cancel", sectionId = cs, date = Iso(date), start = "08:00" }),
                async () =>
                {
                    using var file = new MultipartFormDataContent { { new ByteArrayContent(upload), "file", "t.csv" } };
                    return await w.Owner.PostAsync($"/api/admin/semesters/{w.Semester}/upload/apply", file);
                });
            Assert.Equal(HttpStatusCode.OK, statuses[1]);
            var rows = (await w.Rows()).Where(c => c.OrigDate == date).ToList();
            if (statuses[0] == HttpStatusCode.OK)
            {
                // The change committed first: the apply found it and dropped it, and told the teacher.
                var row = Assert.Single(rows);
                Assert.Equal(("dropped-moved", "system"), (row.EndReason, row.EndedBy));
                Assert.Single(await w.Notices(TeachNotice.ToTeacher), n => n.Kind == "dropped" && n.GroupId == row.GroupId);
            }
            else
            {
                // The apply committed first: the change was checked against the new timetable, which has no session then.
                Assert.Equal(HttpStatusCode.BadRequest, statuses[0]);
                Assert.Empty(rows);
            }
            // Either way no change is left on a session that is gone, and students see the class at its new time.
            Assert.Empty(await w.Live());
            var seen = Sessions(await Json(await w.Students[0].GetAsync("/api/me/timetable")), date, date);
            Assert.Equal("10:00", Assert.Single(seen, s => s.Code == "CS2201").Start);
        }
        await w.AssertNoOrphans();
    }
}
