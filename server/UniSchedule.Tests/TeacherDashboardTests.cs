using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Data;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.ChangeKit;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>
/// The dashboard's side of teachers (/api/admin/teachers, /api/admin/teacher-changes): owners and RSA approve and
/// attach names, freeze, sign out, remove and unblock, and see and undo every teacher change; owners alone switch
/// class changes off and give a student account teaching rights. RSA never see a university ID.
/// </summary>
public class TeacherDashboardTests
{
    static async Task<JsonElement> Teachers(HttpClient staff) => await Json(await staff.GetAsync("/api/admin/teachers"));
    static async Task<List<JsonElement>> Log(HttpClient staff, string query = "") =>
        (await Json(await staff.GetAsync("/api/admin/teacher-changes" + query))).GetProperty("changes").EnumerateArray().ToList();
    static JsonElement Account(JsonElement page, int id) => page.GetProperty("teachers").EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == id);
    static async Task<JsonElement> Attention(HttpClient staff) => (await Json(await staff.GetAsync("/api/admin/stats"))).GetProperty("attention");
    static async Task<JsonElement> Me(HttpClient teacher) => await Json(await teacher.GetAsync("/api/teach/me"));

    /// <summary>A student account given teaching rights by the owner, signed in again afterwards (so its code is newer than the grant).</summary>
    static async Task<(HttpClient Browser, int TeacherId)> Assistant(TeachWorld w, int student, string name)
    {
        var granted = await Json(await w.Owner.PutAsJsonAsync($"/api/admin/students/{w.StudentRowIds[student]}/teaching", new { displayName = name }));
        w.Time.Advance(TimeSpan.FromSeconds(61));
        return (await w.SignInStudent(TeachWorld.StudentIdsList[student]), granted.GetProperty("teaching").GetProperty("teacherId").GetInt32());
    }

    [Fact]
    public async Task Only_owners_and_RSA_reach_the_teacher_dashboard_and_only_owners_the_switch_and_teaching_rights()
    {
        using var w = await TeachWorld.Create();
        var (assistant, _) = await Assistant(w, 2, "Ali Said");
        var staffRoutes = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Get, "/api/admin/teachers"),
            (HttpMethod.Get, "/api/admin/teacher-changes"),
            (HttpMethod.Post, "/api/admin/teachers/links/999/approve"),
            (HttpMethod.Post, "/api/admin/teachers/links/999/reject"),
            (HttpMethod.Post, "/api/admin/teachers/links/999/same-person"),
            (HttpMethod.Post, "/api/admin/teachers/999/names"),
            (HttpMethod.Delete, "/api/admin/teachers/999/names/999"),
            (HttpMethod.Put, "/api/admin/teachers/999/frozen"),
            (HttpMethod.Post, "/api/admin/teachers/999/sign-out"),
            (HttpMethod.Post, "/api/admin/teachers/999/remove"),
            (HttpMethod.Post, "/api/admin/teachers/unblock"),
            (HttpMethod.Post, "/api/admin/teacher-changes/00000000000000000000000000000000/undo"),
        };
        var ownerRoutes = new (HttpMethod Method, string Url)[]
        {
            (HttpMethod.Put, "/api/admin/teachers/switch"),
            (HttpMethod.Get, "/api/admin/students/999/teaching"),
            (HttpMethod.Put, "/api/admin/students/999/teaching"),
            (HttpMethod.Delete, "/api/admin/students/999/teaching"),
        };
        static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod method, string url) =>
            c.SendAsync(new HttpRequestMessage(method, url) { Content = method == HttpMethod.Get ? null : JsonContent.Create(new { name = "x", frozen = true, email = "x", enabled = true, displayName = "x" }) });

        foreach (var (method, url) in staffRoutes.Concat(ownerRoutes))
        {
            // A student, a teacher and a teaching assistant are not staff. Signed out: nobody.
            foreach (var outsider in new[] { w.Students[0], w.Teacher1, assistant })
                Assert.True((await Send(outsider, method, url)).StatusCode == HttpStatusCode.Forbidden, $"{method} {url}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(w.Browser(), method, url)).StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, (await Send(w.Owner, method, url)).StatusCode);
        }
        foreach (var (method, url) in staffRoutes) Assert.NotEqual(HttpStatusCode.Forbidden, (await Send(w.Rsa, method, url)).StatusCode);
        foreach (var (method, url) in ownerRoutes) Assert.True((await Send(w.Rsa, method, url)).StatusCode == HttpStatusCode.Forbidden, $"{method} {url}");
        // None of that changed anything.
        Assert.True((await Teachers(w.Owner)).GetProperty("changesEnabled").GetBoolean());
        // An owner who is a student manages teachers too.
        var studentOwner = await w.SignInStudentOwner();
        Assert.Equal(HttpStatusCode.OK, (await studentOwner.GetAsync("/api/admin/teachers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await studentOwner.PutAsJsonAsync("/api/admin/teachers/switch", new { enabled = true })).StatusCode);
    }

    [Fact]
    public async Task Staff_approve_reject_attach_and_detach_timetable_names()
    {
        using var w = await TeachWorld.Create();
        const string first = "khalid.said@gutech.edu.om", second = "noor.hamed@gutech.edu.om";
        var khalid = await w.SignInTeacher(first);
        Assert.Equal("pick-name", (await Me(khalid)).GetProperty("status").GetString());
        await Json(await khalid.PostAsJsonAsync("/api/teach/names", new { name = TeachWorld.UnclaimedName }));
        var khalidId = (await Me(khalid)).GetProperty("id").GetInt32();

        // The Teachers page lists who is waiting, and the home counts them.
        var waiting = Assert.Single((await Teachers(w.Rsa)).GetProperty("waiting").EnumerateArray());
        Assert.Equal((khalidId, first, TeachWorld.UnclaimedName, 1, false), (waiting.GetProperty("teacherId").GetInt32(), waiting.GetProperty("email").GetString(),
            waiting.GetProperty("name").GetString(), waiting.GetProperty("sections").GetInt32(), waiting.GetProperty("conflict").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("heldBy").ValueKind);
        Assert.Equal(1, (await Attention(w.Rsa)).GetProperty("teachersWaiting").GetInt32());
        var link = waiting.GetProperty("linkId").GetInt32();

        // RSA approves: the name is theirs, they are told, and its class is on their timetable.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rsa.PostAsync($"/api/admin/teachers/links/{link}/approve", null)).StatusCode);
        var me = await Me(khalid);
        Assert.Equal(("linked", TeachWorld.UnclaimedName), (me.GetProperty("status").GetString(), me.GetProperty("name").GetString()));
        Assert.Equal("name-approved", Assert.Single(me.GetProperty("notices").EnumerateArray()).GetProperty("kind").GetString());
        var stored = await w.Db(db => db.TeacherNames.SingleAsync(n => n.Id == link));
        Assert.Equal((TeacherName.Approved, TeacherName.Picked, TeachWorld.RsaName), (stored.State, stored.How, stored.DecidedBy));
        Assert.Contains("BE1101", (await (await khalid.GetAsync("/api/teach/timetable")).Content.ReadAsStringAsync()));
        Assert.Equal(0, (await Attention(w.Rsa)).GetProperty("teachersWaiting").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rsa.PostAsync($"/api/admin/teachers/links/{link}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rsa.PostAsync("/api/admin/teachers/links/99999/approve", null)).StatusCode);

        // Someone else asks for the same name: staff see who holds it, and can't approve while it is held.
        var noor = await w.SignInTeacher(second);
        await Json(await noor.PostAsJsonAsync("/api/teach/names", new { name = TeachWorld.UnclaimedName }));
        var noorId = (await Me(noor)).GetProperty("id").GetInt32();
        waiting = Assert.Single((await Teachers(w.Owner)).GetProperty("waiting").EnumerateArray());
        Assert.Equal((true, first), (waiting.GetProperty("conflict").GetBoolean(), waiting.GetProperty("heldBy").GetString()));
        var other = waiting.GetProperty("linkId").GetInt32();
        var held = await w.Owner.PostAsync($"/api/admin/teachers/links/{other}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        Assert.Equal($"{first} already has this name. Reject or detach that one first.", await Error(held));
        // Attaching it to a third account is refused the same way.
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.PostAsJsonAsync($"/api/admin/teachers/{w.Teacher1Id}/names", new { name = TeachWorld.UnclaimedName })).StatusCode);

        // Staff detach it from the first account (a hand-picked link is simply removed), and approve the second.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.DeleteAsync($"/api/admin/teachers/{khalidId}/names/{link}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.DeleteAsync($"/api/admin/teachers/{khalidId}/names/{link}")).StatusCode);
        me = await Me(khalid);
        Assert.Equal("pick-name", me.GetProperty("status").GetString());
        Assert.Contains(me.GetProperty("notices").EnumerateArray(), n => n.GetProperty("kind").GetString() == "name-rejected");
        // Nobody holds it now, so the one waiting is in nobody's way.
        Assert.False(Assert.Single((await Teachers(w.Owner)).GetProperty("waiting").EnumerateArray()).GetProperty("conflict").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostAsync($"/api/admin/teachers/links/{other}/approve", null)).StatusCode);
        Assert.Equal("linked", (await Me(noor)).GetProperty("status").GetString());

        // Reject: the request is turned down and the teacher is told.
        await Json(await khalid.PostAsJsonAsync("/api/teach/names", new { name = TeachWorld.Teacher1Name }));
        var again = Assert.Single((await Teachers(w.Rsa)).GetProperty("waiting").EnumerateArray());
        Assert.Equal(TeachWorld.Teacher1Email, again.GetProperty("heldBy").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rsa.PostAsync($"/api/admin/teachers/links/{again.GetProperty("linkId").GetInt32()}/reject", null)).StatusCode);
        Assert.Equal(TeacherName.Rejected, await w.Db(db => db.TeacherNames.Where(n => n.TeacherId == khalidId).Select(n => n.State).SingleAsync()));
        Assert.Contains((await Me(khalid)).GetProperty("notices").EnumerateArray(), n => n.GetProperty("title").GetString() == "Your name wasn't approved");
        Assert.Empty((await Teachers(w.Rsa)).GetProperty("waiting").EnumerateArray());

        // Attach a second name to an account: approved at once, by staff. Only real names of the timetable.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Owner.PostAsync($"/api/admin/teachers/links/{other}/reject", null)).StatusCode);
        var attached = await Json(await w.Rsa.PostAsJsonAsync($"/api/admin/teachers/{w.Teacher1Id}/names", new { name = "maryam  NASSER" }));
        var names = Account(await Teachers(w.Rsa), w.Teacher1Id).GetProperty("names").EnumerateArray().ToList();
        Assert.Equal([(TeachWorld.Teacher1Name, "auto", "email", 3), (TeachWorld.UnclaimedName, "approved", "staff", 1)],
            names.Select(n => (n.GetProperty("name").GetString()!, n.GetProperty("state").GetString()!, n.GetProperty("how").GetString()!, n.GetProperty("sections").GetInt32())));
        Assert.Equal(attached.GetProperty("linkId").GetInt32(), names[1].GetProperty("linkId").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rsa.PostAsJsonAsync($"/api/admin/teachers/{w.Teacher1Id}/names", new { name = TeachWorld.PlaceholderName })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Rsa.PostAsJsonAsync($"/api/admin/teachers/{w.Teacher1Id}/names", new { name = "Nobody Here" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rsa.PostAsJsonAsync("/api/admin/teachers/99999/names", new { name = TeachWorld.UnclaimedName })).StatusCode);
        // The names list says who each name belongs to; placeholders are marked.
        var listed = (await Teachers(w.Rsa)).GetProperty("names").EnumerateArray().ToList();
        Assert.Equal(w.Teacher1Id, listed.Single(n => n.GetProperty("name").GetString() == TeachWorld.UnclaimedName).GetProperty("teacherId").GetInt32());
        Assert.True(listed.Single(n => n.GetProperty("name").GetString() == TeachWorld.PlaceholderName).GetProperty("placeholder").GetBoolean());
        Assert.Equal(TeacherKeys.ForName(TeachWorld.Teacher2Name), listed.Single(n => n.GetProperty("name").GetString() == TeachWorld.Teacher2Name).GetProperty("key").GetString());

        // The one-tap reverse of an automatic link: the row stays as "rejected", so the email never links by itself again.
        var auto = await w.Db(db => db.TeacherNames.SingleAsync(n => n.TeacherId == w.Teacher2Id));
        Assert.Equal(TeacherName.Auto, auto.State);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rsa.PostAsync($"/api/admin/teachers/links/{auto.Id}/reject", null)).StatusCode);
        me = await Me(w.Teacher2);
        Assert.Equal("pick-name", me.GetProperty("status").GetString());
        Assert.Contains(me.GetProperty("notices").EnumerateArray(), n => n.GetProperty("title").GetString() == $"You're no longer linked to {TeachWorld.Teacher2Name}");
        Assert.Equal(TeacherName.Rejected, await w.Db(db => db.TeacherNames.Where(n => n.TeacherId == w.Teacher2Id).Select(n => n.State).SingleAsync()));
        // Detaching an email link does the same instead of deleting the row.
        var own = names[0].GetProperty("linkId").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rsa.DeleteAsync($"/api/admin/teachers/{w.Teacher1Id}/names/{own}")).StatusCode);
        await Me(w.Teacher1);
        Assert.Equal(TeacherName.Rejected, await w.Db(db => db.TeacherNames.Where(n => n.Id == own).Select(n => n.State).SingleAsync()));
    }

    [Fact]
    public async Task A_respelled_name_is_accepted_with_one_tap_and_never_by_itself()
    {
        using var w = await TeachWorld.Create();
        const string respelled = "Dr. Huda Al-Kindy";
        var export = await (await w.Owner.GetAsync($"/api/admin/semesters/{w.Semester}/export.csv")).Content.ReadAsStringAsync();
        using (var file = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(export.Replace(TeachWorld.Teacher2Name, respelled))), "file", "t.csv" } })
            await Json(await w.Owner.PostAsync($"/api/admin/semesters/{w.Semester}/upload/apply", file));

        // Her classes are under a name she doesn't hold: nothing links by itself, staff get the hint.
        Assert.DoesNotContain("CS2201", await (await w.Teacher2.GetAsync("/api/teach/timetable")).Content.ReadAsStringAsync());
        var name = Assert.Single(Account(await Teachers(w.Rsa), w.Teacher2Id).GetProperty("names").EnumerateArray());
        Assert.Equal((TeachWorld.Teacher2Name, respelled, 0), (name.GetProperty("name").GetString(), name.GetProperty("suggestedName").GetString(), name.GetProperty("sections").GetInt32()));
        var link = name.GetProperty("linkId").GetInt32();

        Assert.Equal(HttpStatusCode.NoContent, (await w.Rsa.PostAsync($"/api/admin/teachers/links/{link}/same-person", null)).StatusCode);
        name = Assert.Single(Account(await Teachers(w.Rsa), w.Teacher2Id).GetProperty("names").EnumerateArray());
        Assert.Equal((respelled, JsonValueKind.Null, 2), (name.GetProperty("name").GetString(), name.GetProperty("suggestedName").ValueKind, name.GetProperty("sections").GetInt32()));
        Assert.Contains("CS2201", await (await w.Teacher2.GetAsync("/api/teach/timetable")).Content.ReadAsStringAsync());
        Assert.Equal(respelled, (await Me(w.Teacher2)).GetProperty("name").GetString());
        // Nothing left to accept.
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rsa.PostAsync($"/api/admin/teachers/links/{link}/same-person", null)).StatusCode);
    }

    [Fact]
    public async Task Staff_freeze_a_teacher_and_end_their_sign_ins()
    {
        using var w = await TeachWorld.Create();
        var be = await w.Section("BE1101", "1");
        object cancel = new { kind = "cancel", sectionId = be, date = Iso(w.Date(0, 0)), start = "08:00" };

        var frozen = await Json(await w.Rsa.PutAsJsonAsync($"/api/admin/teachers/{w.Teacher1Id}/frozen", new { frozen = true }));
        Assert.True(frozen.GetProperty("frozen").GetBoolean());
        var account = Account(await Teachers(w.Rsa), w.Teacher1Id);
        Assert.Equal((true, TeachWorld.RsaName), (account.GetProperty("frozen").GetBoolean(), account.GetProperty("frozenBy").GetString()));
        Assert.True((await Me(w.Teacher1)).GetProperty("frozen").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Teacher1.Change(cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Teacher1.PutAsJsonAsync("/api/teach/office", new { officeRoom = "C1", note = "", showEmail = false, hours = Array.Empty<object>() })).StatusCode);
        // The other teacher is untouched.
        Assert.False(Account(await Teachers(w.Rsa), w.Teacher2Id).GetProperty("frozen").GetBoolean());

        await Json(await w.Owner.PutAsJsonAsync($"/api/admin/teachers/{w.Teacher1Id}/frozen", new { frozen = false }));
        Assert.False(Account(await Teachers(w.Owner), w.Teacher1Id).GetProperty("frozen").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await w.Teacher1.Change(cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rsa.PutAsJsonAsync("/api/admin/teachers/99999/frozen", new { frozen = true })).StatusCode);

        // "Sign out everywhere" by staff: every browser of the account is out on its next request; the account stays.
        var elsewhere = await w.Fresh(TeachWorld.Teacher1Email);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rsa.PostAsync($"/api/admin/teachers/{w.Teacher1Id}/sign-out", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.Teacher1.GetAsync("/api/teach/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/teach/me")).StatusCode);
        Assert.Single((await w.Live()).Select(c => c.GroupId).Distinct());
        var back = await w.Fresh(TeachWorld.Teacher1Email);
        Assert.Equal("linked", (await Me(back)).GetProperty("status").GetString());
        // A teaching assistant signs in as a student: there is nothing to end here.
        var (_, assistantId) = await Assistant(w, 2, "Ali Said");
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rsa.PostAsync($"/api/admin/teachers/{assistantId}/sign-out", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rsa.PostAsync("/api/admin/teachers/99999/sign-out", null)).StatusCode);
    }

    [Fact]
    public async Task Removing_a_teacher_blocks_the_email_and_undoes_only_what_can_still_be_undone()
    {
        using var w = await TeachWorld.Create();
        var (be, one, two) = (await w.Section("BE1101", "1"), await w.Section("CS2201", "1"), await w.Section("CS2201", "2"));
        // Teacher 1: one cancel of a three-code class. Teacher 2: a cancel next week, and a class moved from next
        // week to this Sunday, which will have been held by the time she is removed.
        var kept = await w.Teacher1.Made(new { kind = "cancel", sectionId = be, date = Iso(w.Date(1, 0)), start = "08:00" });
        var upcoming = await w.Teacher2.Made(new { kind = "cancel", sectionId = one, date = Iso(w.Date(1, 1)), start = "08:00" });
        var held = await w.Teacher2.Made(new { kind = "move", sectionId = two, date = Iso(w.Date(1, 2)), start = "08:00", newDate = Iso(w.Date(0, 0)), newStart = "10:00", room = "B202" });
        await w.MarkSent();
        w.GoTo(w.Date(0, 0), 11);

        var page = await Teachers(w.Rsa);
        Assert.Equal(1, Account(page, w.Teacher1Id).GetProperty("upcomingChanges").GetInt32());
        // The move was held this morning: it can't be undone any more, so it isn't "upcoming".
        Assert.Equal(1, Account(page, w.Teacher2Id).GetProperty("upcomingChanges").GetInt32());

        // A code asked for before the removal must not sign in after it.
        var waiting = w.Browser();
        var code = await RequestTeacherCode(waiting, w.Emails, TeachWorld.Teacher2Email);

        // Without the tick: the changes stay, and the log keeps who made them.
        var removed = await Json(await w.Rsa.PostAsJsonAsync($"/api/admin/teachers/{w.Teacher1Id}/remove", new { undoChanges = false }));
        Assert.Equal(0, removed.GetProperty("undone").GetInt32());
        var rows = (await w.Live()).Where(c => c.GroupId == kept).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, c => Assert.Equal(((int?)null, TeachWorld.Teacher1Name, TeachWorld.Teacher1Email), (c.TeacherId, c.ByName, c.ByLabel)));
        // Their cookie is rejected on the next request, and the email is blocked.
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.Teacher1.GetAsync("/api/teach/me")).StatusCode);
        var blocked = Assert.Single((await Teachers(w.Rsa)).GetProperty("blocked").EnumerateArray());
        Assert.Equal((TeachWorld.Teacher1Email, TeachWorld.RsaName), (blocked.GetProperty("email").GetString(), blocked.GetProperty("by").GetString()));
        Assert.DoesNotContain((await Teachers(w.Rsa)).GetProperty("teachers").EnumerateArray(), t => t.GetProperty("id").GetInt32() == w.Teacher1Id);
        var asked = w.Browser();
        Assert.Equal(HttpStatusCode.OK, (await asked.PostAsJsonAsync("/api/auth/teacher/request-code", new { email = TeachWorld.Teacher1Email })).StatusCode);
        Assert.True(w.Emails.CannotSignIn(TeachWorld.Teacher1Email));

        // With the tick: her upcoming cancel is undone and students are told it is back on; the held move is left.
        removed = await Json(await w.Owner.PostAsJsonAsync($"/api/admin/teachers/{w.Teacher2Id}/remove", new { undoChanges = true }));
        Assert.Equal(1, removed.GetProperty("undone").GetInt32());
        var all = await w.Rows();
        var undone = all.Single(c => c.GroupId == upcoming);
        Assert.Equal(("undone-on-remove", $"staff:{TeachWorld.OwnerName}"), (undone.EndReason, undone.EndedBy));
        Assert.Null(all.Single(c => c.GroupId == held).EndedAt);
        Assert.Single(await w.Notices(TeachNotice.Students), n => n.Kind == "back-on");
        Assert.Equal(one, (await w.Notices(TeachNotice.Students)).Single(n => n.Kind == "back-on").SectionId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.Teacher2.GetAsync("/api/teach/me")).StatusCode);
        // The code from before the block is gone with it.
        Assert.NotEqual(HttpStatusCode.OK, (await VerifyTeacher(waiting, TeachWorld.Teacher2Email, code)).StatusCode);
        Assert.Equal(0, await w.Db(db => db.LoginCodes.CountAsync(c => c.UniversityId == TeachWorld.Teacher2Email)));
        Assert.Equal(0, await w.Db(db => db.Teachers.CountAsync(t => t.Email != null)));
        await w.AssertNoOrphans();

        // Unblock: the email can sign in again, as a new account.
        Assert.Equal(HttpStatusCode.NoContent, (await w.Rsa.PostAsJsonAsync("/api/admin/teachers/unblock", new { email = " " + TeachWorld.Teacher1Email.ToUpperInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rsa.PostAsJsonAsync("/api/admin/teachers/unblock", new { email = TeachWorld.Teacher1Email })).StatusCode);
        Assert.Equal(TeachWorld.Teacher2Email, Assert.Single((await Teachers(w.Owner)).GetProperty("blocked").EnumerateArray()).GetProperty("email").GetString());
        w.Time.Advance(TimeSpan.FromMinutes(2));
        var back = await w.SignInTeacher(TeachWorld.Teacher1Email);
        Assert.Equal("linked", (await Me(back)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await w.Rsa.PostAsJsonAsync("/api/admin/teachers/99999/remove", new { undoChanges = true })).StatusCode);
    }

    [Fact]
    public async Task The_log_lists_every_change_flags_the_ones_not_by_the_named_teacher_and_staff_undo_tells_the_teacher()
    {
        using var w = await TeachWorld.Create();
        var (one, two) = (await w.Section("CS2201", "1"), await w.Section("CS2201", "2"));
        var be = await w.Section("BE1101", "1");
        // Her own class; then someone else's class (flagged); then the three-code class.
        var own = await w.Teacher2.Made(new { kind = "room", sectionId = two, date = Iso(w.Date(0, 2)), start = "08:00", room = "C201", note = "Projector" });
        await w.Teacher1.AddSection(one);
        var flagged = await w.Teacher1.Made(new { kind = "cancel", sectionId = one, date = Iso(w.Date(0, 1)), start = "08:00" });
        var merged = await w.Teacher1.Made(new { kind = "move", sectionId = be, date = Iso(w.Date(1, 0)), start = "08:00", newDate = Iso(w.Date(1, 3)), newStart = "10:00", room = "" });
        await w.MarkSent();

        var log = await Log(w.Rsa);
        Assert.Equal([merged, flagged, own], log.Select(r => r.GetProperty("group").GetString()));
        var row = log[1];
        Assert.Equal(("cancel", "live", true, TeachWorld.Teacher1Name, TeachWorld.Teacher1Email, TeachWorld.Teacher2Name, 2, true),
            (row.GetProperty("kind").GetString(), row.GetProperty("state").GetString(), row.GetProperty("flagged").GetBoolean(), row.GetProperty("by").GetString(),
                row.GetProperty("byLabel").GetString(), row.GetProperty("namedTeacher").GetString(), row.GetProperty("students").GetInt32(), row.GetProperty("canUndo").GetBoolean()));
        Assert.Equal((Iso(w.Date(0, 1)), "08:00", "09:30", "B105"), (row.GetProperty("from").GetProperty("date").GetString(), row.GetProperty("from").GetProperty("start").GetString(),
            row.GetProperty("from").GetProperty("end").GetString(), row.GetProperty("from").GetProperty("room").GetString()));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("to").ValueKind);
        Assert.Equal(w.Teacher1Id, row.GetProperty("teacherId").GetInt32());
        // One row for the three codes of one class, counting each of its students once.
        Assert.Equal(["BE1101", "IBSM1101", "LOG1101"], log[0].GetProperty("codes").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal((3, false, Iso(w.Date(1, 3)), ""), (log[0].GetProperty("students").GetInt32(), log[0].GetProperty("flagged").GetBoolean(),
            log[0].GetProperty("to").GetProperty("date").GetString(), log[0].GetProperty("to").GetProperty("room").GetString()));
        Assert.Equal(("room", false, "Projector", "C201", 1), (log[2].GetProperty("kind").GetString(), log[2].GetProperty("flagged").GetBoolean(), log[2].GetProperty("note").GetString(),
            log[2].GetProperty("to").GetProperty("room").GetString(), log[2].GetProperty("students").GetInt32()));

        // Filters: flagged, by teacher, live or ended, and another semester.
        Assert.Equal([flagged], (await Log(w.Rsa, "?flagged=true")).Select(r => r.GetProperty("group").GetString()));
        Assert.Equal([own], (await Log(w.Owner, $"?teacherId={w.Teacher2Id}")).Select(r => r.GetProperty("group").GetString()));
        Assert.Equal(3, (await Log(w.Owner, $"?semesterId={w.Semester}&live=true")).Count);
        Assert.Empty(await Log(w.Owner, "?live=false"));
        Assert.Empty(await Log(w.Owner, "?semesterId=99999"));

        // RSA undo the flagged one. A freeze or the switch doesn't stop staff, and no code is asked for.
        await Json(await w.Rsa.PutAsJsonAsync($"/api/admin/teachers/{w.Teacher1Id}/frozen", new { frozen = true }));
        await Json(await w.Owner.PutAsJsonAsync("/api/admin/teachers/switch", new { enabled = false }));
        await w.Db(db => db.TeachNotices.Where(n => n.Audience == TeachNotice.ToTeacher).ExecuteDeleteAsync());
        Assert.Equal(HttpStatusCode.OK, (await w.Rsa.PostAsync($"/api/admin/teacher-changes/{flagged}/undo", null)).StatusCode);
        var ended = Assert.Single(await w.Rows(), c => c.GroupId == flagged);
        Assert.Equal(("undone-by-staff", $"staff:{TeachWorld.RsaName}"), (ended.EndReason, ended.EndedBy));
        // Students who were told get "back on"; the maker and the named teacher are told staff undid it.
        Assert.Equal(one, Assert.Single(await w.Notices(TeachNotice.Students), n => n.Kind == "back-on").SectionId);
        var told = (await w.Notices(TeachNotice.ToTeacher)).Where(n => n.Kind == "undone").ToList();
        Assert.Equal([w.Teacher1Id, w.Teacher2Id], told.Select(n => n.TeacherId!.Value).Order());
        Assert.All(told, n => Assert.EndsWith("was undone by Classy staff", n.Title));
        row = (await Log(w.Rsa, "?live=false")).Single();
        Assert.Equal(("undone-by-staff", TeachWorld.RsaName, false), (row.GetProperty("state").GetString(), row.GetProperty("endedBy").GetString(), row.GetProperty("canUndo").GetBoolean()));
        // Twice is once.
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rsa.PostAsync($"/api/admin/teacher-changes/{flagged}/undo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Rsa.PostAsync("/api/admin/teacher-changes/nothing/undo", null)).StatusCode);

        // The same limit as a teacher's: once the class has started, staff can't undo it either.
        w.GoTo(w.Date(0, 2), 8, 1);
        Assert.False((await Log(w.Owner)).Single(r => r.GetProperty("group").GetString() == own).GetProperty("canUndo").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PostAsync($"/api/admin/teacher-changes/{own}/undo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Owner.PostAsync($"/api/admin/teacher-changes/{merged}/undo", null)).StatusCode);
        Assert.Equal(3, (await w.Rows()).Count(c => c.GroupId == merged && c.EndedBy == $"staff:{TeachWorld.OwnerName}"));
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task RSA_never_see_a_university_id_a_teaching_assistant_is_a_teaching_assistant_and_a_student_owner_an_owner()
    {
        using var w = await TeachWorld.Create();
        var (one, two) = (await w.Section("CS2201", "1"), await w.Section("CS2201", "2"));
        // A teaching assistant adds a class and changes it; a student owner changes one through "view as".
        var (assistant, assistantId) = await Assistant(w, 2, "Ali Said");
        await assistant.AddSection(two);
        var theirs = await assistant.Made(new { kind = "cancel", sectionId = two, date = Iso(w.Date(1, 2)), start = "08:00" });
        var studentOwner = TeachWorld.ViewAs(await w.SignInStudentOwner(), TeacherKeys.For(new Teacher { Id = w.Teacher2Id }));
        var viewed = await studentOwner.Made(new { kind = "room", sectionId = one, date = Iso(w.Date(1, 1)), start = "08:00", room = "C201" });
        // The owner also adds a class to the teacher's list through "view as", and the assistant undoes a change of their own.
        await studentOwner.AddSection(await w.Section("BE1101", "2"));
        var undone = await assistant.Made(new { kind = "cancel", sectionId = two, date = Iso(w.Date(0, 2)), start = "08:00" });
        Assert.Equal(HttpStatusCode.OK, (await assistant.Undo(undone)).StatusCode);
        // And the student owner freezes the assistant, removes a third account and blocks its email.
        Assert.Equal(HttpStatusCode.OK, (await studentOwner.PutAsJsonAsync($"/api/admin/teachers/{assistantId}/frozen", new { frozen = true })).StatusCode);
        var third = await w.SignInTeacher("khalid.said@gutech.edu.om");
        var thirdId = (await Me(third)).GetProperty("id").GetInt32();
        await Json(await studentOwner.PostAsJsonAsync($"/api/admin/teachers/{thirdId}/remove", new { undoChanges = false }));
        string[] ids = [.. TeachWorld.StudentIdsList, ApiFactory.OwnerId];

        // What RSA see.
        var log = await Log(w.Rsa);
        var byAssistant = log.Single(r => r.GetProperty("group").GetString() == theirs);
        Assert.Equal(("Ali Said", "a teaching assistant", true), (byAssistant.GetProperty("by").GetString(), byAssistant.GetProperty("byLabel").GetString(), byAssistant.GetProperty("flagged").GetBoolean()));
        var byOwner = log.Single(r => r.GetProperty("group").GetString() == viewed);
        Assert.Equal((TeachActor.StaffName, "an owner", TeachWorld.Teacher2Name, true), (byOwner.GetProperty("by").GetString(), byOwner.GetProperty("byLabel").GetString(),
            byOwner.GetProperty("onBehalfOf").GetString(), byOwner.GetProperty("flagged").GetBoolean()));
        Assert.Equal("a teaching assistant", log.Single(r => r.GetProperty("group").GetString() == undone).GetProperty("endedBy").GetString());
        var page = await Teachers(w.Rsa);
        var account = Account(page, assistantId);
        Assert.Equal(("student", "Ali Said", "an owner", TeachWorld.OwnerName), (account.GetProperty("kind").GetString(), account.GetProperty("name").GetString(),
            account.GetProperty("frozenBy").GetString(), account.GetProperty("createdBy").GetString()));
        Assert.Equal(JsonValueKind.Null, account.GetProperty("email").ValueKind);
        Assert.Equal(JsonValueKind.Null, account.GetProperty("universityId").ValueKind);
        Assert.Equal(JsonValueKind.Null, account.GetProperty("studentId").ValueKind);
        Assert.Equal("an owner", Assert.Single(page.GetProperty("blocked").EnumerateArray()).GetProperty("by").GetString());
        // The sections each account added, and whose class each is; an add through "view as" names who did it.
        var added = Assert.Single(Account(page, w.Teacher2Id).GetProperty("added").EnumerateArray());
        Assert.Equal(("BE1101", "2", TeachWorld.UnclaimedName, "an owner"), (added.GetProperty("code").GetString(), added.GetProperty("number").GetString(),
            added.GetProperty("named").GetString(), added.GetProperty("by").GetString()));
        var own = Assert.Single(account.GetProperty("added").EnumerateArray());
        Assert.Equal(("CS2201", "2", TeachWorld.Teacher2Name, JsonValueKind.Null), (own.GetProperty("code").GetString(), own.GetProperty("number").GetString(),
            own.GetProperty("named").GetString(), own.GetProperty("by").ValueKind));
        // No answer RSA can ask for holds a university ID.
        foreach (var url in new[] { "/api/admin/teachers", "/api/admin/teacher-changes", "/api/admin/teacher-changes?live=false", "/api/admin/stats",
                     $"/api/admin/courses/{await w.CourseOf(two)}", $"/api/admin/semesters/{w.Semester}/sync" })
        {
            var text = await (await w.Rsa.GetAsync(url)).Content.ReadAsStringAsync();
            Assert.All(ids, id => Assert.DoesNotContain(id, text));
        }

        // Owners see who it was.
        log = await Log(w.Owner);
        Assert.Equal(TeachWorld.StudentIdsList[2], log.Single(r => r.GetProperty("group").GetString() == theirs).GetProperty("byLabel").GetString());
        Assert.Equal(ApiFactory.OwnerId, log.Single(r => r.GetProperty("group").GetString() == viewed).GetProperty("byLabel").GetString());
        Assert.Equal(TeachWorld.StudentIdsList[2], log.Single(r => r.GetProperty("group").GetString() == undone).GetProperty("endedBy").GetString());
        account = Account(await Teachers(w.Owner), assistantId);
        Assert.Equal((TeachWorld.StudentIdsList[2], w.StudentRowIds[2], ApiFactory.OwnerId), (account.GetProperty("universityId").GetString(), account.GetProperty("studentId").GetInt32(),
            account.GetProperty("frozenBy").GetString()));
    }

    [Fact]
    public async Task Only_an_owner_switches_all_class_changes_off()
    {
        using var w = await TeachWorld.Create();
        var be = await w.Section("BE1101", "1");
        object cancel = new { kind = "cancel", sectionId = be, date = Iso(w.Date(0, 0)), start = "08:00" };
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Rsa.PutAsJsonAsync("/api/admin/teachers/switch", new { enabled = false })).StatusCode);
        Assert.True((await Teachers(w.Rsa)).GetProperty("changesEnabled").GetBoolean());

        Assert.False((await Json(await w.Owner.PutAsJsonAsync("/api/admin/teachers/switch", new { enabled = false }))).GetProperty("changesEnabled").GetBoolean());
        Assert.False((await Teachers(w.Rsa)).GetProperty("changesEnabled").GetBoolean());
        Assert.False((await Me(w.Teacher1)).GetProperty("changesEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await w.Teacher1.Change(cancel)).StatusCode);
        var setting = await w.Db(db => db.AppSettings.SingleAsync(x => x.Key == UniSchedule.Api.Schedule.ClassChanges.EnabledSetting));
        Assert.Equal(("false", TeachWorld.OwnerName), (setting.Value, setting.UpdatedBy));

        await Json(await w.Owner.PutAsJsonAsync("/api/admin/teachers/switch", new { enabled = true }));
        Assert.True((await Me(w.Teacher1)).GetProperty("changesEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await w.Teacher1.Change(cancel)).StatusCode);
    }

    [Fact]
    public async Task An_owner_gives_a_student_teaching_rights_and_takes_them_away()
    {
        using var w = await TeachWorld.Create();
        var student = w.StudentRowIds[2];
        var universityId = TeachWorld.StudentIdsList[2];
        var two = await w.Section("CS2201", "2");
        Assert.Equal(JsonValueKind.Null, (await Json(await w.Owner.GetAsync($"/api/admin/students/{student}/teaching"))).GetProperty("teaching").ValueKind);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Students[2].GetAsync("/api/teach/me")).StatusCode);

        // A sign-in code asked for before the grant, in another browser.
        w.Time.Advance(TimeSpan.FromSeconds(61));
        var early = w.Browser();
        Assert.Equal(HttpStatusCode.OK, (await early.PostAsJsonAsync("/api/auth/request-code", new { universityId })).StatusCode);
        var code = w.Emails.CodeFor(UniSchedule.Api.Auth.StudentIds.EmailFor(universityId));
        w.Time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.PutAsJsonAsync($"/api/admin/students/{student}/teaching", new { displayName = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.PutAsJsonAsync("/api/admin/students/99999/teaching", new { displayName = "Ali Said" })).StatusCode);
        var granted = (await Json(await w.Owner.PutAsJsonAsync($"/api/admin/students/{student}/teaching", new { displayName = "  Ali Said " }))).GetProperty("teaching");
        var teacherId = granted.GetProperty("teacherId").GetInt32();
        Assert.Equal("Ali Said", granted.GetProperty("displayName").GetString());
        var profile = await w.Db(db => db.Teachers.SingleAsync(t => t.Id == teacherId));
        Assert.Equal((student, null, TeachWorld.OwnerName), (profile.StudentId, profile.Email, profile.CreatedBy));

        // The code from before the grant went with it: it can't be entered now and pass for a code from after.
        Assert.Equal(0, await w.Db(db => db.LoginCodes.CountAsync(c => c.UniversityId == universityId)));
        Assert.NotEqual(HttpStatusCode.OK, (await early.PostAsJsonAsync("/api/auth/verify", new { universityId, code })).StatusCode);
        // The browser signed in before the grant reaches the teacher side, but must enter a new code to change a class.
        var me = await Me(w.Students[2]);
        Assert.Equal(("student", "Ali Said", false), (me.GetProperty("kind").GetString(), me.GetProperty("name").GetString(), me.GetProperty("codeFresh").GetBoolean()));
        await w.Students[2].AddSection(two);
        object cancel = new { kind = "cancel", sectionId = two, date = Iso(w.Date(1, 2)), start = "08:00" };
        var refused = await w.Students[2].Change(cancel);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("needsCode").GetBoolean());
        w.Time.Advance(TimeSpan.FromSeconds(61));
        var assistant = await w.SignInStudent(universityId);
        var group = await assistant.Made(cancel);
        await w.MarkSent();

        // Granting again renames the same profile, and asks for no new code.
        var renamed = (await Json(await w.Owner.PutAsJsonAsync($"/api/admin/students/{student}/teaching", new { displayName = "Ali S." }))).GetProperty("teaching");
        Assert.Equal((teacherId, "Ali S."), (renamed.GetProperty("teacherId").GetInt32(), renamed.GetProperty("displayName").GetString()));
        Assert.True((await Me(assistant)).GetProperty("codeFresh").GetBoolean());
        var shown = (await Json(await w.Owner.GetAsync($"/api/admin/students/{student}/teaching"))).GetProperty("teaching");
        Assert.Equal((teacherId, "Ali S.", 1), (shown.GetProperty("teacherId").GetInt32(), shown.GetProperty("displayName").GetString(), shown.GetProperty("upcomingChanges").GetInt32()));
        Assert.Equal("student", Account(await Teachers(w.Owner), teacherId).GetProperty("kind").GetString());

        // Revoke, undoing their upcoming changes: students are told the class is back on, and the student is a student again.
        var revoked = await Json(await w.Owner.DeleteAsync($"/api/admin/students/{student}/teaching?undoChanges=true"));
        Assert.Equal(1, revoked.GetProperty("undone").GetInt32());
        var row = Assert.Single(await w.Rows(), c => c.GroupId == group);
        Assert.Equal(("undone-on-remove", (int?)null), (row.EndReason, row.TeacherId));
        Assert.Single(await w.Notices(TeachNotice.Students), n => n.Kind == "back-on");
        Assert.Equal(HttpStatusCode.Forbidden, (await assistant.GetAsync("/api/teach/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await assistant.GetAsync("/api/me/timetable")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(await w.Owner.GetAsync($"/api/admin/students/{student}/teaching"))).GetProperty("teaching").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Owner.DeleteAsync($"/api/admin/students/{student}/teaching")).StatusCode);
        // A student has no email to block.
        Assert.Empty((await Teachers(w.Owner)).GetProperty("blocked").EnumerateArray());
        await w.AssertNoOrphans();
    }

    [Fact]
    public async Task The_dashboard_home_shows_a_teacher_who_made_more_than_five_changes_today()
    {
        using var w = await TeachWorld.Create();
        var (one, two) = (await w.Section("CS2201", "1"), await w.Section("CS2201", "2"));
        // Not across midnight in Oman: the count is per day.
        if (w.Time.GetUtcNow().ToOffset(Oman).Hour >= 23) w.Time.Advance(TimeSpan.FromMinutes(90));
        var attention = await Attention(w.Rsa);
        Assert.Equal(0, attention.GetProperty("teachersWaiting").GetInt32());
        Assert.Empty(attention.GetProperty("busyTeachers").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, attention.GetProperty("teacherSignInPaused").ValueKind);

        foreach (var (section, date) in new[] { (one, w.Date(0, 1)), (one, w.Date(1, 1)), (two, w.Date(0, 2)), (two, w.Date(1, 2)) })
            await w.Teacher2.Made(new { kind = "cancel", sectionId = section, date = Iso(date), start = "08:00" });
        await w.Teacher2.Made(new { kind = "extra", sectionIds = new[] { one }, newDate = Iso(w.Date(0, 3)), newStart = "15:00", newEnd = "16:00", type = "lecture" });
        // Five in a day is still quiet.
        Assert.Empty((await Attention(w.Rsa)).GetProperty("busyTeachers").EnumerateArray());
        await w.Teacher2.Made(new { kind = "extra", sectionIds = new[] { two }, newDate = Iso(w.Date(0, 3)), newStart = "16:15", newEnd = "17:15", type = "lab" });

        // The sixth: owners who are students get a push (the staff notice), and RSA see it on the dashboard.
        var notice = Assert.Single(await w.Notices(TeachNotice.Staff));
        Assert.Equal((ChangeRules.ManyChanges, w.Teacher2Id), (notice.Kind, notice.TeacherId));
        var busy = Assert.Single((await Attention(w.Rsa)).GetProperty("busyTeachers").EnumerateArray());
        Assert.Equal((w.Teacher2Id, TeachWorld.Teacher2Name, 6), (busy.GetProperty("id").GetInt32(), busy.GetProperty("name").GetString(), busy.GetProperty("changes").GetInt32()));
        var account = Account(await Teachers(w.Rsa), w.Teacher2Id);
        Assert.Equal((6, 6), (account.GetProperty("changesToday").GetInt32(), account.GetProperty("upcomingChanges").GetInt32()));
        Assert.Equal(0, Account(await Teachers(w.Rsa), w.Teacher1Id).GetProperty("changesToday").GetInt32());
        // The badge on the Teachers link is waiting names plus busy teachers.
        var third = await w.SignInTeacher("khalid.said@gutech.edu.om");
        await Json(await third.PostAsJsonAsync("/api/teach/names", new { name = TeachWorld.UnclaimedName }));
        attention = await Attention(w.Owner);
        Assert.Equal(2, attention.GetProperty("teachersWaiting").GetInt32() + attention.GetProperty("busyTeachers").GetArrayLength());

        // The next Oman day starts from nothing.
        w.Time.Advance(TimeSpan.FromHours(24));
        Assert.Empty((await Attention(w.Rsa)).GetProperty("busyTeachers").EnumerateArray());
        Assert.Equal(0, Account(await Teachers(w.Rsa), w.Teacher2Id).GetProperty("changesToday").GetInt32());
    }

    [Fact]
    public async Task The_dashboard_home_says_when_new_teacher_sign_ins_are_paused()
    {
        // One wrong code for an address with no account is enough here to pause first-time sign-ins.
        using var w = await TeachWorld.Create(seed: false, configure: b => b.UseSetting("RateLimits:TeacherWrongCodesNewPerHour", "1"));
        await w.AddAdmin(TeachWorld.RsaName, AdminRole.Rsa);
        var rsa = await SignInAdmin(w.Browser(), TeachWorld.RsaName, TeachWorld.Password);
        Assert.Equal(JsonValueKind.Null, (await Attention(rsa)).GetProperty("teacherSignInPaused").ValueKind);

        const string email = "new.address@gutech.edu.om";
        var c = w.Browser("203.0.113.9");
        var code = await RequestTeacherCode(c, w.Emails, email);
        Assert.NotEqual(HttpStatusCode.OK, (await VerifyTeacher(c, email, WrongFor(code))).StatusCode);

        var paused = (await Attention(rsa)).GetProperty("teacherSignInPaused");
        Assert.Equal(w.Time.GetUtcNow().AddHours(1), paused.GetDateTimeOffset());
        // The staff notice of the pause points at the Teachers page.
        Assert.Equal("/admin/teachers", (await w.Notices(TeachNotice.Staff)).Single(n => n.Kind == "sign-in-guessing").Url);
        w.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(JsonValueKind.Null, (await Attention(rsa)).GetProperty("teacherSignInPaused").ValueKind);
    }
}
