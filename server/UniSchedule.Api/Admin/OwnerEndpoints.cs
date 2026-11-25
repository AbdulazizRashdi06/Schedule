using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Admin;

/// <summary>Dashboard accounts (owners and RSA) and the owner-only student pages.</summary>
public static partial class OwnerEndpoints
{
    public record DeleteAllBody(string? Confirm);
    public record AccountBody(string? Username, string? Role);
    public record ResetBody(string? What);
    public record OwnerBody(bool IsOwner);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{2,31}$")]
    private static partial Regex UsernameFormat();

    /// <summary>Readable temporary password: no 0/O or 1/l/I.</summary>
    public static string TempPassword()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var groups = Enumerable.Range(0, 3).Select(_ => new string(Enumerable.Range(0, 4).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray()));
        return string.Join("-", groups);
    }

    public static string? CheckUsername(string? raw, out string username)
    {
        username = raw?.Trim().ToLowerInvariant() ?? "";
        return UsernameFormat().IsMatch(username) ? null : "Usernames are 3–32 letters, digits, dots, dashes or underscores.";
    }

    /// <summary>Who made a change, for the accounts list: a university ID, a dashboard username or a teacher's email.</summary>
    internal static async Task<string> ActorName(HttpContext http, AppDb db, CancellationToken ct)
    {
        var id = http.User.Id();
        return http.User.Kind() switch
        {
            "student" => await db.Students.Where(s => s.Id == id).Select(s => s.UniversityId).FirstAsync(ct),
            "admin" => await db.AdminUsers.Where(a => a.Id == id).Select(a => a.Username).FirstAsync(ct),
            "teacher" => await db.Teachers.Where(t => t.Id == id).Select(t => t.Email!).FirstAsync(ct),
            var kind => throw new InvalidOperationException($"No name for a sign-in of kind '{kind}'."),
        };
    }

    /// <summary>
    /// An actor for a "by" field: a student owner's university ID is student data, so RSA sees "an owner" instead.
    /// Dashboard usernames are shown as they are.
    /// </summary>
    internal static string? ShownActor(string? by, bool isOwner) =>
        isOwner || by is null || StudentIds.Normalize(by) is null ? by : "an owner";

    /// <summary>
    /// An owner who leaves takes their Classy Sync tokens with them: they could have kept a copy. A GUtech IT token they
    /// made has to be made again by another owner.
    /// </summary>
    static Task<int> RevokeTokensOf(AppDb db, string by, CancellationToken ct) =>
        db.SyncTokens.Where(t => t.CreatedBy == by).ExecuteDeleteAsync(ct);

    public static void MapOwner(this RouteGroupBuilder api)
    {
        // ---- Accounts: RSA can manage RSA accounts only; owners can manage all ----
        var accounts = api.MapGroup("/admin/accounts").RequireAuthorization(Session.StaffPolicy);

        accounts.MapGet("/", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var isOwner = http.User.IsOwner();
            var me = http.User.IsAdmin() ? http.User.Id() : -1;
            var admins = await db.AdminUsers.OrderBy(a => a.Role).ThenBy(a => a.Username).ToListAsync(ct);
            // Owners who are students: their IDs are student data, so RSA doesn't see them.
            var studentOwners = isOwner
                ? await db.Students.Where(s => s.IsOwner).OrderBy(s => s.UniversityId).Select(s => new { s.Id, s.UniversityId, s.LastSeenAt }).ToListAsync(ct)
                : [];
            return Results.Ok(new
            {
                accounts = admins.Select(a => new
                {
                    a.Id, a.Username, role = a.Role == AdminRole.Owner ? "owner" : "rsa", a.MustChangePassword, a.CreatedAt, createdBy = ShownActor(a.CreatedBy, isOwner), a.LastSeenAt,
                    isMe = a.Id == me,
                    canManage = a.Id != me && (isOwner || a.Role == AdminRole.Rsa),
                }),
                studentOwners,
            });
        });

        accounts.MapPost("/", async (AccountBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var problem = CheckUsername(body.Username, out var username);
            if (problem is not null) return Errors.BadRequest(problem);
            var role = body.Role?.ToLowerInvariant() switch { "owner" => AdminRole.Owner, "rsa" => AdminRole.Rsa, _ => (AdminRole?)null };
            if (role is null) return Errors.BadRequest("Pick Owner or RSA.");
            if (role == AdminRole.Owner && !http.User.IsOwner()) return Errors.Forbidden("Only owners can add owners.");
            if (await db.AdminUsers.AnyAsync(a => a.Username == username, ct)) return Errors.Conflict($"The username {username} is taken.");
            var password = TempPassword();
            var user = new AdminUser { Username = username, Role = role.Value, MustChangePassword = true, CreatedAt = clock.GetUtcNow(), CreatedBy = await ActorName(http, db, ct) };
            user.PasswordHash = AuthEndpoints.HashPassword(user, password);
            db.AdminUsers.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { user.Id, user.Username, temporaryPassword = password });
        });

        accounts.MapPost("/{id:int}/reset-password", async (int id, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var (user, error) = await Manageable(id, http, db, ct);
            if (error is not null) return error;
            var password = TempPassword();
            user!.PasswordHash = AuthEndpoints.HashPassword(user, password);
            user.MustChangePassword = true;
            user.FailedLogins = 0;
            user.FailedSince = null;
            user.LockedUntil = null;
            // Wherever the account is signed in now gets signed out.
            user.SessionVersion++;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { user.Id, user.Username, temporaryPassword = password });
        });

        accounts.MapDelete("/{id:int}", async (int id, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var (user, error) = await Manageable(id, http, db, ct);
            if (error is not null) return error;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await RevokeTokensOf(db, user!.Username, ct);
            db.AdminUsers.Remove(user);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // ---- Students: owners only ----
        var students = api.MapGroup("/admin/students").RequireAuthorization(Session.OwnerPolicy);

        students.MapGet("/", async (string? q, string? status, AppDb db, CancellationToken ct) =>
        {
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            var query = db.Students.AsQueryable();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                query = query.Where(s => s.UniversityId.Contains(term) || (s.Major != null && (s.Major.Code.ToLower().Contains(term) || s.Major.Name.ToLower().Contains(term))));
            }
            var cid = current?.Id;
            if (status == "done") query = query.Where(s => s.SetupSemesterId == cid && s.SetupCompletedAt != null);
            else if (status == "pending") query = query.Where(s => !(s.SetupSemesterId == cid && s.SetupCompletedAt != null));
            var total = await query.CountAsync(ct);
            var list = await query.OrderBy(s => s.UniversityId).Take(300).Select(s => new
            {
                s.Id, s.UniversityId, s.IsOwner, s.Year, s.MajorId,
                major = s.Major == null ? null : s.Major.Code,
                s.SetupSemesterId, s.SetupStep, s.SetupCompletedAt, s.CreatedAt, s.LastSeenAt,
                needsRepick = s.Picks.Any(p => p.Course.SemesterId == cid && p.SectionId == null),
                courses = s.Picks.Count(p => p.Course.SemesterId == cid),
            }).ToListAsync(ct);
            return Results.Ok(new
            {
                total,
                students = list.Select(s =>
                {
                    var stub = new Student { SetupSemesterId = s.SetupSemesterId, SetupStep = s.SetupStep, SetupCompletedAt = s.SetupCompletedAt, MajorId = s.MajorId, Year = s.Year };
                    var (step, done) = StudentEndpoints.Progress(stub, current);
                    return new { s.Id, s.UniversityId, s.IsOwner, s.major, s.Year, step, done, s.needsRepick, s.courses, s.CreatedAt, s.LastSeenAt };
                }),
            });
        });

        students.MapGet("/{id:int}", async (int id, AppDb db, CancellationToken ct) =>
        {
            var s = await db.Students.Include(x => x.Major).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (s is null) return Errors.NotFound("That student doesn't exist.");
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            var (step, done) = StudentEndpoints.Progress(s, current);
            var picks = current is null ? [] : await db.Picks.Where(p => p.StudentId == id && p.Course.SemesterId == current.Id)
                .Include(p => p.Course).Include(p => p.Section).ThenInclude(x => x!.Meetings)
                .AsSplitQuery().ToListAsync(ct);
            var withSection = picks.Where(p => p.Section is not null).ToList();
            return Results.Ok(new
            {
                s.Id, s.UniversityId, s.Email, s.IsOwner, s.Year, s.CreatedAt, s.LastSeenAt, s.SetupCompletedAt,
                major = s.Major is null ? null : new { s.Major.Id, s.Major.Code, s.Major.Name },
                step, done,
                semester = current is null ? null : Dto.Of(current),
                picks = picks.OrderBy(p => p.Course.Type).ThenBy(p => p.Course.Code).Select(p => new
                {
                    p.CourseId, p.Course.Code, p.Course.Name, type = p.Course.Type.ToString(),
                    section = p.Section is null ? null : Dto.Of(p.Section),
                    p.SectionRemoved,
                }),
                clashes = Clashes.Find(withSection.Select(p => p.Section!).ToList()),
            });
        });

        // Reset major: pick a major again. Reset picks: keep major and year, pick courses again. Either way setup starts over.
        students.MapPost("/{id:int}/reset", async (int id, ResetBody body, AppDb db, CancellationToken ct) =>
        {
            if (body.What is not ("major" or "picks")) return Errors.BadRequest("Choose what to reset.");
            var s = await db.Students.FindAsync([id], ct);
            if (s is null) return Errors.NotFound("That student doesn't exist.");
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is not null) await db.Picks.Where(p => p.StudentId == id && p.Course.SemesterId == current.Id).ExecuteDeleteAsync(ct);
            if (body.What == "major") { s.MajorId = null; s.Year = null; }
            s.SetupSemesterId = current?.Id;
            s.SetupStep = "major";
            s.SetupCompletedAt = null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Owners only, after three confirmations in the dashboard: every semester (courses, sections, picks, defaults, uploads),
        // every major and every student who isn't an owner. Dashboard accounts and owners stay; owners start setup again.
        api.MapPost("/admin/delete-all-data", async (DeleteAllBody body, AppDb db, CancellationToken ct) =>
        {
            if (body.Confirm != "DELETE ALL DATA") return Errors.BadRequest("Confirm with DELETE ALL DATA.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.UsageDays.ExecuteDeleteAsync(ct);
            await db.UsageHours.ExecuteDeleteAsync(ct);
            // Sign-in codes name the IDs that asked for them (codes still in flight stop working).
            await db.LoginCodes.ExecuteDeleteAsync(ct);
            // Teacher accounts and their class changes, before the semesters they point at. Names, added sections,
            // own slots, devices and office hours go with each account. Blocked emails stay blocked.
            await db.TeachNotices.ExecuteDeleteAsync(ct);
            await db.ClassChanges.ExecuteDeleteAsync(ct);
            var teachers = await db.Teachers.ExecuteDeleteAsync(ct);
            await db.AppSettings.Where(x => x.Key == Teachers.TeachReminders.Since).ExecuteDeleteAsync(ct);
            var semesters = await db.Semesters.ExecuteDeleteAsync(ct);
            var students = await db.Students.Where(s => !s.IsOwner).ExecuteDeleteAsync(ct);
            // Their memberships went with them; a group nobody is left in goes too (its name is student data).
            await db.StudyGroups.Where(g => !g.Members.Any()).ExecuteDeleteAsync(ct);
            await db.Students.ExecuteUpdateAsync(u => u
                .SetProperty(s => s.MajorId, (int?)null).SetProperty(s => s.Year, (int?)null)
                .SetProperty(s => s.SetupSemesterId, (int?)null).SetProperty(s => s.SetupStep, (string?)null)
                .SetProperty(s => s.SetupCompletedAt, (DateTimeOffset?)null), ct);
            var majors = await db.Majors.ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { semesters, students, majors, teachers });
        }).RequireAuthorization(Session.OwnerPolicy);

        students.MapDelete("/{id:int}", async (int id, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            if (http.User.IsStudent() && http.User.Id() == id) return Errors.BadRequest("You can't remove yourself.");
            var s = await db.Students.FindAsync([id], ct);
            if (s is null) return Errors.NotFound("That student doesn't exist.");
            var groups = await db.GroupMembers.Where(m => m.StudentId == id).Select(m => m.GroupId).ToListAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (s.IsOwner) await RevokeTokensOf(db, s.UniversityId, ct);
            db.Students.Remove(s);
            await db.SaveChangesAsync(ct);
            // Groups they were the last one in go too, as when the last member leaves.
            await db.StudyGroups.Where(g => groups.Contains(g.Id) && !g.Members.Any()).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        students.MapPut("/{id:int}/owner", async (int id, OwnerBody body, HttpContext http, AppDb db, IConfiguration config, CancellationToken ct) =>
        {
            var s = await db.Students.FindAsync([id], ct);
            if (s is null) return Errors.NotFound("That student doesn't exist.");
            if (!body.IsOwner && AuthEndpoints.IsConfiguredOwner(config, s.UniversityId))
                return Errors.Conflict("This owner is set in the server settings (OWNER_IDS); remove them there.");
            if (!body.IsOwner && http.User.IsStudent() && http.User.Id() == id) return Errors.BadRequest("You can't remove your own owner access.");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (s.IsOwner && !body.IsOwner) await RevokeTokensOf(db, s.UniversityId, ct);
            s.IsOwner = body.IsOwner;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
    }

    static async Task<(AdminUser? User, IResult? Error)> Manageable(int id, HttpContext http, AppDb db, CancellationToken ct)
    {
        var user = await db.AdminUsers.FindAsync([id], ct);
        if (user is null) return (null, Errors.NotFound("That account doesn't exist."));
        if (http.User.IsAdmin() && http.User.Id() == id) return (null, Errors.BadRequest("You can't do that to your own account."));
        if (user.Role == AdminRole.Owner && !http.User.IsOwner()) return (null, Errors.Forbidden("Only owners can manage owner accounts."));
        return (user, null);
    }
}
