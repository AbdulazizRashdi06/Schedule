using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;

namespace UniSchedule.Api.Students;

/// <summary>
/// Groups: friends who want to see when they're all free, and car groups who ride to uni together.
/// The free-time and ride maths runs here, so members never receive each other's timetables.
/// </summary>
public static class GroupEndpoints
{
    public const int MaxMembers = 12;
    public const int MaxGroups = 20;
    public const int MinFree = 30;
    static readonly string[] Kinds = ["friends", "car"];
    const string CodeChars = "abcdefghjkmnpqrstuvwxyz23456789";

    public record CreateBody(string? Name, string? Kind, string? DisplayName);
    public record JoinBody(string? DisplayName);
    public record MeBody(string? DisplayName, bool ShowClasses);
    public record RenameBody(string? Name);

    static string NewCode() => RandomNumberGenerator.GetString(CodeChars, 10);
    static string Clock(int min) => $"{min / 60:00}:{min % 60:00}";

    static string? Clean(string? value, int max, string what, out string clean)
    {
        clean = (value ?? "").Trim();
        return clean.Length is 0 || clean.Length > max ? $"Give {what} (up to {max} characters)." : null;
    }

    /// <summary>The group, if this student is in it (null otherwise, answered as not found).</summary>
    static Task<StudyGroup?> Mine(AppDb db, int groupId, int studentId, CancellationToken ct) =>
        db.StudyGroups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == groupId && g.Members.Any(m => m.StudentId == studentId), ct);

    static object Of(StudyGroup g, int me) => new
    {
        g.Id, g.Name, g.Kind, inviteCode = g.InviteCode, g.TravelMinutes, g.ArriveEarlyMinutes,
        members = g.Members.OrderBy(m => m.JoinedAt).Select(m => new { m.Id, name = m.DisplayName, me = m.StudentId == me, m.ShowClasses }),
    };

    /// <summary>The Sunday of the asked week, or of this week in Oman.</summary>
    static DateOnly WeekOf(string? start, TimeProvider clock) =>
        SemesterWeeks.Sunday(ScheduleImport.ParseDate(start ?? "") ?? DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(4)).DateTime));

    public static void MapGroups(this RouteGroupBuilder api)
    {
        var me = api.MapGroup("/me/groups").RequireAuthorization(Session.StudentPolicy);

        me.MapGet("", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            return Results.Ok(await db.StudyGroups.Where(g => g.Members.Any(m => m.StudentId == id)).OrderBy(g => g.Name)
                .Select(g => new { g.Id, g.Name, g.Kind, members = g.Members.Count }).ToListAsync(ct));
        });

        me.MapPost("", async (CreateBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var id = http.User.Id();
            if (Clean(body.Name, 40, "the group a name", out var name) is { } p1) return Errors.BadRequest(p1);
            if (Clean(body.DisplayName, 30, "your name for the group", out var display) is { } p2) return Errors.BadRequest(p2);
            var kind = Kinds.Contains(body.Kind) ? body.Kind! : "friends";
            if (await db.GroupMembers.CountAsync(m => m.StudentId == id, ct) >= MaxGroups) return Errors.BadRequest($"You can be in up to {MaxGroups} groups.");
            var now = clock.GetUtcNow();
            var group = new StudyGroup { Name = name, Kind = kind, InviteCode = NewCode(), CreatedById = id, CreatedAt = now };
            group.Members.Add(new GroupMember { StudentId = id, DisplayName = display, JoinedAt = now });
            db.StudyGroups.Add(group);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Of(group, id));
        });

        me.MapGet("/{groupId:int}", async (int groupId, HttpContext http, AppDb db, CancellationToken ct) =>
            await Mine(db, groupId, http.User.Id(), ct) is { } g ? Results.Ok(Of(g, http.User.Id())) : Errors.NotFound("That group doesn't exist."));

        me.MapPut("/{groupId:int}", async (int groupId, RenameBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            if (await Mine(db, groupId, http.User.Id(), ct) is not { } g) return Errors.NotFound("That group doesn't exist.");
            if (Clean(body.Name, 40, "the group a name", out var name) is { } p) return Errors.BadRequest(p);
            g.Name = name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(Of(g, http.User.Id()));
        });

        me.MapPut("/{groupId:int}/me", async (int groupId, MeBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            if (await Mine(db, groupId, id, ct) is not { } g) return Errors.NotFound("That group doesn't exist.");
            if (Clean(body.DisplayName, 30, "your name for the group", out var display) is { } p) return Errors.BadRequest(p);
            var m = g.Members.First(x => x.StudentId == id);
            (m.DisplayName, m.ShowClasses) = (display, body.ShowClasses);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Of(g, id));
        });

        // Leave; the last one out deletes the group.
        me.MapDelete("/{groupId:int}/me", async (int groupId, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            if (await Mine(db, groupId, id, ct) is not { } g) return Errors.NotFound("That group doesn't exist.");
            if (g.Members.Count == 1) db.StudyGroups.Remove(g);
            else db.GroupMembers.Remove(g.Members.First(m => m.StudentId == id));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // A new invite link; the old one stops working.
        me.MapPost("/{groupId:int}/invite", async (int groupId, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            if (await Mine(db, groupId, http.User.Id(), ct) is not { } g) return Errors.NotFound("That group doesn't exist.");
            g.InviteCode = NewCode();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { inviteCode = g.InviteCode });
        });

        // When everyone (or nearly everyone) is free that week, within uni hours.
        me.MapGet("/{groupId:int}/week", async (int groupId, string? start, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var id = http.User.Id();
            if (await Mine(db, groupId, id, ct) is not { } g) return Errors.NotFound("That group doesn't exist.");
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            var sunday = WeekOf(start, clock);
            var members = g.Members.ToList();
            var busy = semester is null ? members.ToDictionary(m => m.StudentId, _ => new Dictionary<DateOnly, List<Busy.Block>>())
                : await Busy.ForAsync(db, members.Select(m => m.StudentId).ToList(), semester.Id, sunday, sunday.AddDays(6), false, ct);
            var names = members.ToDictionary(m => m.StudentId, m => m.DisplayName);
            var showing = members.Where(m => m.ShowClasses).Select(m => m.StudentId).ToHashSet();

            var days = Enumerable.Range(0, 7).Select(i => sunday.AddDays(i))
                .Where(d => (int)d.DayOfWeek <= 4 || busy.Values.Any(x => x.ContainsKey(d)))
                .Select(d =>
                {
                    var that = busy.ToDictionary(kv => kv.Key, kv => kv.Value.TryGetValue(d, out var list) ? list : []);
                    var stretches = Busy.Stretches(that).Where(s => s.End - s.Start >= MinFree).ToList();
                    // "Nearly" needs at least three people: in a pair, one busy person is just a busy person.
                    var nearlyMax = members.Count >= 3 ? Math.Min(2, members.Count - 1) : 0;
                    return new
                    {
                        date = d.ToString("yyyy-MM-dd"), day = (int)d.DayOfWeek,
                        free = stretches.Where(s => s.BusyMembers.Count == 0)
                            .Select(s => new { start = Clock(s.Start), end = Clock(s.End), minutes = s.End - s.Start }),
                        nearly = stretches.Where(s => s.BusyMembers.Count > 0 && s.BusyMembers.Count <= nearlyMax)
                            .Select(s => new { start = Clock(s.Start), end = Clock(s.End), minutes = s.End - s.Start, busy = s.BusyMembers.Select(x => names[x]).Order() }),
                        // Only members who chose to show their classes; own slots stay private as "Busy".
                        classes = that.Where(kv => showing.Contains(kv.Key)).SelectMany(kv => kv.Value.Select(b => new
                        {
                            member = names[kv.Key], start = Clock(b.Start), end = Clock(b.End),
                            label = b.IsClass ? b.Label : "Busy", room = b.IsClass ? b.Room : "",
                        })).OrderBy(x => x.start),
                    };
                }).ToList();
            return Results.Ok(new { start = sunday.ToString("yyyy-MM-dd"), members = members.Count, days });
        });

        // ---- Invite links ----
        var invite = api.MapGroup("/groups/invite/{code}").RequireAuthorization(Session.StudentPolicy);

        invite.MapGet("", async (string code, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var g = await db.StudyGroups.Include(x => x.Members).FirstOrDefaultAsync(x => x.InviteCode == code, ct);
            if (g is null) return Errors.NotFound("That invite link doesn't work any more. Ask for a new one.");
            var id = http.User.Id();
            return Results.Ok(new { g.Id, g.Name, g.Kind, members = g.Members.Count, joined = g.Members.Any(m => m.StudentId == id), full = g.Members.Count >= MaxMembers });
        });

        invite.MapPost("/join", async (string code, JoinBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var g = await db.StudyGroups.Include(x => x.Members).FirstOrDefaultAsync(x => x.InviteCode == code, ct);
            if (g is null) return Errors.NotFound("That invite link doesn't work any more. Ask for a new one.");
            if (Clean(body.DisplayName, 30, "your name for the group", out var display) is { } p) return Errors.BadRequest(p);
            var already = g.Members.FirstOrDefault(m => m.StudentId == id);
            if (already is not null) already.DisplayName = display;
            else
            {
                if (g.Members.Count >= MaxMembers) return Errors.Conflict($"This group is full ({MaxMembers} people).");
                if (await db.GroupMembers.CountAsync(m => m.StudentId == id, ct) >= MaxGroups) return Errors.BadRequest($"You can be in up to {MaxGroups} groups.");
                g.Members.Add(new GroupMember { StudentId = id, DisplayName = display, JoinedAt = clock.GetUtcNow() });
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(Of(g, id));
        });
    }
}
