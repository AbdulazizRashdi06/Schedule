using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Teachers;
using static UniSchedule.Tests.TeacherTestKit;

namespace UniSchedule.Tests;

/// <summary>Which timetable name a teacher's account is linked to: by its email at sign-in, or picked and approved by staff.</summary>
public class TeacherLinkTests
{
    static string Email(string local) => $"{local}@gutech.edu.om";

    static Task<List<TeacherName>> Links(ApiFactory factory, string email) => factory.Services.Db(db =>
        db.TeacherNames.Where(n => db.Teachers.Any(t => t.Id == n.TeacherId && t.Email == email)).OrderBy(n => n.Id).AsNoTracking().ToListAsync());

    /// <summary>What staff do in the dashboard (its endpoints come later): decide a link by writing the row.</summary>
    static Task Decide(ApiFactory factory, int linkId, string state) => factory.Services.Db(db =>
        db.TeacherNames.Where(n => n.Id == linkId).ExecuteUpdateAsync(u => u
            .SetProperty(n => n.State, state).SetProperty(n => n.Conflict, false)
            .SetProperty(n => n.DecidedAt, DateTimeOffset.UtcNow).SetProperty(n => n.DecidedBy, "test")));

    static async Task<JsonElement> Me(HttpClient c) => await Json(await c.GetAsync("/api/teach/me"));
    static async Task<string?> SessionName(HttpClient c) =>
        (await Json(await c.GetAsync("/api/auth/session"))).GetProperty("teacher").GetProperty("displayName").GetString();

    [Fact]
    public async Task An_email_that_matches_exactly_one_name_is_linked_at_once()
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Hadad", "Noor Saleh", "Dr. Fatma Al-Rawahi");
        var c = factory.Client();
        var verify = await SignInTeacher(c, factory.Emails, Email("karim.hadad"));
        Assert.True(verify.GetProperty("isNew").GetBoolean());
        Assert.Equal("linked", verify.GetProperty("status").GetString());

        var link = Assert.Single(await Links(factory, Email("karim.hadad")));
        Assert.Equal(("Karim Hadad", "karim hadad", TeacherName.Auto, TeacherName.ByEmail, false), (link.Name, link.NameKey, link.State, link.How, link.Conflict));

        var me = await Me(c);
        Assert.Equal("linked", me.GetProperty("status").GetString());
        Assert.Equal("Karim Hadad", me.GetProperty("name").GetString());
        Assert.Equal("staff", me.GetProperty("kind").GetString());
        Assert.Equal(Email("karim.hadad"), me.GetProperty("email").GetString());
        var name = Assert.Single(me.GetProperty("names").EnumerateArray());
        Assert.Equal(("Karim Hadad", "auto"), (name.GetProperty("name").GetString(), name.GetProperty("state").GetString()));
        Assert.Equal("Karim Hadad", await SessionName(c));

        // The name as written on the timetable, title and all; signing in again changes nothing.
        var fatma = await SignInTeacher(factory, Email("fatma.rawahi"));
        Assert.Equal("Dr. Fatma Al-Rawahi", (await Me(fatma)).GetProperty("name").GetString());
        var again = await SignInTeacher(factory.Client(), factory.Emails, Email("karim.hadad"));
        Assert.False(again.GetProperty("isNew").GetBoolean());
        Assert.Single(await Links(factory, Email("karim.hadad")));
    }

    [Theory]
    [InlineData("omar.saadi")]      // two names it could be
    [InlineData("karim.hadad2")]    // a digit
    [InlineData("karim_hadad")]     // first_last
    [InlineData("karim-hadad")]     // first-last
    [InlineData("k.hadad")]         // an initial
    [InlineData("cs1.cs1")]         // a placeholder
    public async Task Anything_less_clear_is_not_linked_and_goes_to_the_picker(string local)
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Hadad", "Omar Saadi", "Omar Al Saadi", "CS1 CS1");
        var c = factory.Client();
        var verify = await SignInTeacher(c, factory.Emails, Email(local));
        Assert.Equal("pick-name", verify.GetProperty("status").GetString());
        var me = await Me(c);
        Assert.Equal("pick-name", me.GetProperty("status").GetString());
        Assert.Empty(me.GetProperty("names").EnumerateArray());
        Assert.Empty(await Links(factory, Email(local)));
    }

    [Fact]
    public async Task The_picker_lists_real_names_once_and_no_placeholders()
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Hadad", "CS1 CS1", "", "Noor Saleh", "Karim Hadad", "LS2 LS2", "Staff");
        var c = await SignInTeacher(factory, Email("registrar"));
        var names = (await Json(await c.GetAsync("/api/teach/names"))).GetProperty("names").EnumerateArray()
            .Select(n => (n.GetProperty("name").GetString(), n.GetProperty("sections").GetInt32())).ToList();
        Assert.Equal([("Karim Hadad", 2), ("Noor Saleh", 1)], names);

        // Only a name from that list can be asked for.
        foreach (var name in new[] { "CS1 CS1", "Staff", "Somebody Else", "", null })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/teach/names", new { name })).StatusCode);
        Assert.Empty(await Links(factory, Email("registrar")));
    }

    [Fact]
    public async Task A_picked_name_waits_for_staff_and_is_then_approved_or_rejected()
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Hadad", "Noor Saleh");
        var c = await SignInTeacher(factory, Email("k.hadad"));

        // Asking for one name, then another: only the last request stands.
        Assert.Equal("pending", (await Json(await c.PostAsJsonAsync("/api/teach/names", new { name = "Noor Saleh" }))).GetProperty("status").GetString());
        var picked = await Json(await c.PostAsJsonAsync("/api/teach/names", new { name = "karim hadad" }));
        Assert.Equal("pending", picked.GetProperty("status").GetString());
        var link = Assert.Single(await Links(factory, Email("k.hadad")));
        Assert.Equal(("Karim Hadad", TeacherName.Pending, TeacherName.Picked, false), (link.Name, link.State, link.How, link.Conflict));

        // A pending name isn't theirs yet: it gives no shown name.
        var me = await Me(c);
        Assert.Equal("pending", me.GetProperty("status").GetString());
        Assert.Equal("K Hadad", me.GetProperty("name").GetString());
        Assert.Equal("pending", Assert.Single(me.GetProperty("names").EnumerateArray()).GetProperty("state").GetString());

        await Decide(factory, link.Id, TeacherName.Approved);
        me = await Me(c);
        Assert.Equal("linked", me.GetProperty("status").GetString());
        Assert.Equal("Karim Hadad", me.GetProperty("name").GetString());
        // Asking again for the name they hold changes nothing.
        Assert.Equal("linked", (await Json(await c.PostAsJsonAsync("/api/teach/names", new { name = "Karim Hadad" }))).GetProperty("status").GetString());
        Assert.Equal(TeacherName.Approved, Assert.Single(await Links(factory, Email("k.hadad"))).State);

        // Rejected: back to the picker, and the account is not shown under the name.
        var other = await SignInTeacher(factory, Email("n.s"));
        await Json(await other.PostAsJsonAsync("/api/teach/names", new { name = "Noor Saleh" }));
        await Decide(factory, Assert.Single(await Links(factory, Email("n.s"))).Id, TeacherName.Rejected);
        me = await Me(other);
        Assert.Equal("pick-name", me.GetProperty("status").GetString());
        Assert.Equal("N S", me.GetProperty("name").GetString());
        Assert.Equal("rejected", Assert.Single(me.GetProperty("names").EnumerateArray()).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_matching_email_wins_over_someone_who_picked_the_name_by_hand()
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Hadad");
        var picker = await SignInTeacher(factory, Email("k.hadad"));
        await Json(await picker.PostAsJsonAsync("/api/teach/names", new { name = "Karim Hadad" }));
        await Decide(factory, Assert.Single(await Links(factory, Email("k.hadad"))).Id, TeacherName.Approved);
        Assert.Equal("linked", (await Me(picker)).GetProperty("status").GetString());

        var owner = factory.Client();
        Assert.Equal("linked", (await SignInTeacher(owner, factory.Emails, Email("karim.hadad"))).GetProperty("status").GetString());
        var won = Assert.Single(await Links(factory, Email("karim.hadad")));
        Assert.Equal((TeacherName.Auto, TeacherName.ByEmail, true), (won.State, won.How, won.Conflict));
        // The earlier holder waits again, and staff see the conflict on both sides.
        var lost = Assert.Single(await Links(factory, Email("k.hadad")));
        Assert.Equal((TeacherName.Pending, TeacherName.Picked, true), (lost.State, lost.How, lost.Conflict));
        Assert.Equal("pending", (await Me(picker)).GetProperty("status").GetString());
        Assert.Equal("K Hadad", (await Me(picker)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_name_held_by_email_or_attached_by_staff_stays_and_the_newcomer_waits()
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Al Hadad", "Noor Saleh");
        // Held by an email match.
        var first = await SignInTeacher(factory, Email("karim.alhadad"));
        var second = factory.Client();
        Assert.Equal("pending", (await SignInTeacher(second, factory.Emails, Email("karim.hadad"))).GetProperty("status").GetString());
        var waiting = Assert.Single(await Links(factory, Email("karim.hadad")));
        Assert.Equal((TeacherName.Pending, TeacherName.ByEmail, true), (waiting.State, waiting.How, waiting.Conflict));
        Assert.Equal(TeacherName.Auto, Assert.Single(await Links(factory, Email("karim.alhadad"))).State);
        Assert.Equal("Karim Al Hadad", (await Me(first)).GetProperty("name").GetString());
        // The one who waits is shown by its own email name, which is not the holder's exact name.
        Assert.Equal("Karim Hadad", (await Me(second)).GetProperty("name").GetString());

        // Attached by staff on purpose.
        await factory.Services.AddTeacher(Email("n.s"), "Noor Saleh");
        Assert.Equal("pending", (await SignInTeacher(factory.Client(), factory.Emails, Email("noor.saleh"))).GetProperty("status").GetString());
        Assert.Equal((TeacherName.Approved, TeacherName.ByStaff), (await Links(factory, Email("n.s"))).Select(n => (n.State, n.How)).Single());
        Assert.True(Assert.Single(await Links(factory, Email("noor.saleh"))).Conflict);

        // Picking a name someone holds is flagged as a conflict too.
        var third = await SignInTeacher(factory, Email("registrar"));
        await Json(await third.PostAsJsonAsync("/api/teach/names", new { name = "Noor Saleh" }));
        Assert.True(Assert.Single(await Links(factory, Email("registrar"))).Conflict);
    }

    [Fact]
    public async Task One_account_per_name_but_staff_can_attach_several_names_to_one_account()
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Hadad", "Noor Saleh");
        var c = await SignInTeacher(factory, Email("karim.hadad"));
        var other = await factory.Services.AddTeacher(Email("someone.else"));

        // The database itself refuses a second holder, however the row is written.
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.Services.Db(async db =>
        {
            db.TeacherNames.Add(new TeacherName { TeacherId = other.Id, Name = "KARIM HADAD", NameKey = TeacherNames.NameKey("KARIM HADAD"), State = TeacherName.Approved, How = TeacherName.ByStaff });
            await db.SaveChangesAsync();
        }));
        // Any number may wait for it.
        await factory.Services.Db(async db =>
        {
            db.TeacherNames.Add(new TeacherName { TeacherId = other.Id, Name = "Karim Hadad", NameKey = "karim hadad", State = TeacherName.Pending, How = TeacherName.Picked });
            await db.SaveChangesAsync();
        });

        // Staff attach a second name to the first account.
        var id = (await Me(c)).GetProperty("id").GetInt32();
        await factory.Services.Db(async db =>
        {
            db.TeacherNames.Add(new TeacherName { TeacherId = id, Name = "Noor Saleh", NameKey = "noor saleh", State = TeacherName.Approved, How = TeacherName.ByStaff });
            await db.SaveChangesAsync();
        });
        var me = await Me(c);
        Assert.Equal("linked", me.GetProperty("status").GetString());
        Assert.Equal([("Karim Hadad", "auto"), ("Noor Saleh", "approved")],
            me.GetProperty("names").EnumerateArray().Select(n => (n.GetProperty("name").GetString(), n.GetProperty("state").GetString())));
        // Shown by the oldest of them.
        Assert.Equal("Karim Hadad", me.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_reversed_automatic_link_is_not_made_again()
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Hadad");
        var c = await SignInTeacher(factory, Email("karim.hadad"));
        await Decide(factory, Assert.Single(await Links(factory, Email("karim.hadad"))).Id, TeacherName.Rejected);

        Assert.Equal("pick-name", (await Me(c)).GetProperty("status").GetString());
        Assert.Equal("pick-name", (await SignInTeacher(factory.Client(), factory.Emails, Email("karim.hadad"))).GetProperty("status").GetString());
        Assert.Equal(TeacherName.Rejected, Assert.Single(await Links(factory, Email("karim.hadad"))).State);
        // Not theirs any more, so not shown under it either.
        Assert.Equal("karim.hadad", (await Me(c)).GetProperty("name").GetString());
    }

    [Fact]
    public async Task My_name_isnt_here_carries_on_with_no_name()
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Hadad");
        var c = await SignInTeacher(factory, Email("it.support"));
        Assert.Equal("pick-name", (await Me(c)).GetProperty("status").GetString());
        var skipped = await Json(await c.PostAsJsonAsync("/api/teach/names", new { none = true }));
        Assert.Equal("none", skipped.GetProperty("status").GetString());
        var me = await Me(c);
        Assert.Equal("none", me.GetProperty("status").GetString());
        Assert.Equal("It Support", me.GetProperty("name").GetString());
        Assert.Equal("none", (await SignInTeacher(factory.Client(), factory.Emails, Email("it.support"))).GetProperty("status").GetString());
        Assert.Empty(await Links(factory, Email("it.support")));
    }

    [Fact]
    public async Task A_name_that_appears_on_the_timetable_later_is_linked_when_the_home_loads()
    {
        using var factory = new ApiFactory();
        // Nothing published yet: nothing to link to.
        var c = factory.Client();
        Assert.Equal("pick-name", (await SignInTeacher(c, factory.Emails, Email("karim.hadad"))).GetProperty("status").GetString());
        var me = await Me(c);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("semester").ValueKind);
        Assert.Equal("pick-name", me.GetProperty("status").GetString());

        await factory.Services.AddSemester("Noor Saleh");
        Assert.Equal("pick-name", (await Me(c)).GetProperty("status").GetString());
        await factory.Services.AddSection("Karim Hadad");
        me = await Me(c);
        Assert.Equal("linked", me.GetProperty("status").GetString());
        Assert.Equal("Karim Hadad", me.GetProperty("name").GetString());
        Assert.Equal("Fall 2026", me.GetProperty("semester").GetProperty("name").GetString());
        var link = Assert.Single(await Links(factory, Email("karim.hadad")));
        Assert.Equal((TeacherName.Auto, TeacherName.ByEmail), (link.State, link.How));
    }

    [Fact]
    public async Task An_account_that_holds_no_name_is_shown_by_its_local_part_when_its_email_name_is_a_real_teachers()
    {
        using var factory = new ApiFactory();
        await factory.Services.AddSemester("Karim Hadad", "Noor Saleh");
        var c = await SignInTeacher(factory, Email("karim_hadad"));
        Assert.Equal("karim_hadad", (await Me(c)).GetProperty("name").GetString());
        Assert.Equal("karim_hadad", await SessionName(c));
        // Still so while its request for the name waits.
        await Json(await c.PostAsJsonAsync("/api/teach/names", new { name = "Karim Hadad" }));
        Assert.Equal("karim_hadad", (await Me(c)).GetProperty("name").GetString());

        // A name held by an account counts even when it is on no section this semester.
        await factory.Services.AddTeacher(Email("x.y"), "Sara Jaber");
        var sara = await SignInTeacher(factory, Email("sara-jaber"));
        Assert.Equal("sara-jaber", (await Me(sara)).GetProperty("name").GetString());

        // Nobody's name: shown as the email reads.
        var free = await SignInTeacher(factory, Email("huda_nasser"));
        Assert.Equal("Huda Nasser", (await Me(free)).GetProperty("name").GetString());
        Assert.Equal("Huda Nasser", await SessionName(free));
    }
}
