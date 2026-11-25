using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;
using UniSchedule.Api.Teachers;

namespace UniSchedule.Api.Admin;

/// <summary>
/// The dashboard's side of teachers, for owners and RSA: approve and attach timetable names, freeze, sign out, remove
/// and unblock, and the log of every teacher change with undo. Owners alone switch all class changes off and give a
/// student account teaching rights. These answers hold staff emails, teacher names and counts only: RSA still see
/// nothing about students (a teaching assistant is "a teaching assistant" to them, never a university ID).
/// </summary>
public static class TeacherAdminEndpoints
{
    public record NameBody(string? Name);
    public record FrozenBody(bool Frozen);
    public record RemoveBody(bool UndoChanges = false);
    public record EmailBody(string? Email);
    public record SwitchBody(bool Enabled);
    public record TeachingBody(string? DisplayName);

    public const int LogRows = 200;
    const int MaxDisplayName = 80, MaxName = 120;
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string Plain(string? text) => new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

    /// <summary>Midnight in Oman as a UTC instant: stored times are UTC, and the database compares them as stored.</summary>
    static DateTimeOffset OmanMidnight(DateTimeOffset now)
    {
        var oman = now.ToOffset(Oman);
        return new DateTimeOffset(oman.Date, oman.Offset).ToUniversalTime();
    }

    /// <summary>
    /// Who made a change, for the log. Owners see the label as stored. RSA see a staff email or a dashboard username,
    /// but never a university ID: a teaching assistant is "a teaching assistant" (the teacher's name is in "by"), and
    /// a student owner who used "view as" is "an owner".
    /// </summary>
    public static string ShownBy(bool isOwner, string byKind, string byLabel, string? onBehalfOf) =>
        isOwner || byKind is "teacher" or "admin" ? byLabel : onBehalfOf is null ? "a teaching assistant" : "an owner";

    /// <summary>Who ended a change, for the log: stored as teacher:{email}, assistant:{university ID}, staff:{name} or system.</summary>
    public static string? ShownEndedBy(bool isOwner, string? endedBy)
    {
        if (endedBy is null) return null;
        if (endedBy == ClassChanges.BySystem) return "the university timetable";
        var cut = endedBy.IndexOf(':');
        if (cut < 0) return isOwner ? endedBy : "someone";
        var (kind, label) = (endedBy[..cut], endedBy[(cut + 1)..]);
        return kind switch
        {
            "teacher" => label,
            "assistant" => isOwner ? label : "a teaching assistant",
            _ => OwnerEndpoints.ShownActor(label, isOwner),
        };
    }

    /// <summary>An account as staff name it in a message: its email, or for a teaching assistant its shown name. Never a university ID.</summary>
    static async Task<string> LabelAsync(AppDb db, Teacher t, CancellationToken ct) =>
        t.Email ?? await TeacherNames.ShownAsync(db, t, await db.TeacherNames.Where(n => n.TeacherId == t.Id).ToListAsync(ct), ct);

    static TeachNotice Notice(int teacherId, string kind, string title, string body, DateTimeOffset now) => new()
    {
        Audience = TeachNotice.ToTeacher, TeacherId = teacherId, Kind = kind, Title = TeachNotices.Cut(title, 120), Body = TeachNotices.Cut(body, 400),
        Url = "/teach", Tag = $"name-{teacherId.ToString(Inv)}", CreatedAt = now,
    };

    /// <summary>The account that holds this name (an auto or approved link), other than <paramref name="exceptTeacherId"/>.</summary>
    static async Task<Teacher?> HolderAsync(AppDb db, string nameKey, int exceptTeacherId, CancellationToken ct)
    {
        var held = await db.TeacherNames.FirstOrDefaultAsync(n => n.NameKey == nameKey && n.TeacherId != exceptTeacherId
            && (n.State == TeacherName.Auto || n.State == TeacherName.Approved), ct);
        return held is null ? null : await db.Teachers.FirstOrDefaultAsync(t => t.Id == held.TeacherId, ct);
    }

    /// <summary>Everyone still waiting for a name is told whether it is held now.</summary>
    static Task MarkWaitingAsync(AppDb db, string nameKey, int exceptTeacherId, bool held, CancellationToken ct) =>
        db.TeacherNames.Where(n => n.NameKey == nameKey && n.TeacherId != exceptTeacherId && n.State == TeacherName.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.Conflict, held), ct);

    /// <summary>
    /// The live changes an account made that can still be undone right now: what "upcoming" means on the Teachers
    /// page, in the remove dialog and on removal. A class moved to an earlier day and already held is not among them.
    /// </summary>
    static async Task<List<ClassChange>> UpcomingAsync(AppDb db, int teacherId, DateTimeOffset now, CancellationToken ct) =>
        (await db.ClassChanges.Where(c => c.TeacherId == teacherId && c.EndedAt == null).ToListAsync(ct)).Where(c => ChangeRules.CanUndo(c, now)).ToList();

    /// <summary>
    /// Removes a teaching account in the caller's transaction: with <paramref name="undoChanges"/> its upcoming
    /// changes end first (students who were told get "back on"); a staff address is blocked and its outstanding
    /// sign-in codes go; then the row is deleted, which signs every cookie of the account out on its next request.
    /// The log keeps who made each change. Returns how many changes were undone.
    /// </summary>
    static async Task<int> RemoveAsync(AppDb db, Teacher teacher, bool undoChanges, string by, DateTimeOffset now, CancellationToken ct)
    {
        var undone = 0;
        if (undoChanges)
        {
            var rows = await UpcomingAsync(db, teacher.Id, now, ct);
            undone = rows.Select(c => c.GroupId).Distinct().Count();
            // The account being removed isn't told about its own changes.
            await ChangeRules.EndAsync(db, rows, "undone-on-remove", $"staff:{by}", now, ct, actingTeacherId: teacher.Id);
        }
        if (teacher.Email is { } email)
        {
            if (!await db.BlockedEmails.AnyAsync(b => b.Email == email, ct)) db.BlockedEmails.Add(new BlockedEmail { Email = email, BlockedAt = now, BlockedBy = by });
            // A code asked for before the block must not sign in after it.
            await db.LoginCodes.Where(c => c.UniversityId == email).ExecuteDeleteAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        // Session validation doesn't read the block list: deleting the row in this same transaction is what ends the sign-ins.
        await db.Teachers.Where(t => t.Id == teacher.Id).ExecuteDeleteAsync(ct);
        return undone;
    }

    public static void MapTeacherAdmin(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").RequireAuthorization(Session.StaffPolicy);
        var owners = api.MapGroup("/admin").RequireAuthorization(Session.OwnerPolicy);

        // ---- The Teachers page: everything it needs in one answer ----

        admin.MapGet("/teachers", async (HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var isOwner = http.User.IsOwner();
            var now = clock.GetUtcNow();
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            var teachers = await db.Teachers.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct);
            var links = await db.TeacherNames.AsNoTracking().OrderBy(n => n.Id).ToListAsync(ct);
            var instructors = (await TeacherNames.InstructorsAsync(db, ct)).Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
            // How many sections each name has this semester, by name key.
            var sections = instructors.GroupBy(TeacherNames.NameKey).ToDictionary(g => g.Key, g => g.Count());
            var taken = instructors.Select(TeacherNames.NameKey).Concat(links.Where(n => n.Holds).Select(n => n.NameKey)).Where(k => k.Length > 0).ToHashSet();
            var shown = teachers.ToDictionary(t => t.Id, t => TeacherNames.Shown(t, links, taken));
            var byId = teachers.ToDictionary(t => t.Id);
            var holders = links.Where(n => n.Holds).GroupBy(n => n.NameKey).ToDictionary(g => g.Key, g => g.First().TeacherId);
            // A university ID is student data: only owners get it.
            var universityIds = isOwner
                ? await db.Students.Where(s => db.Teachers.Any(t => t.StudentId == s.Id)).ToDictionaryAsync(s => s.Id, s => s.UniversityId, ct)
                : [];
            string Label(int teacherId) => byId[teacherId].Email ?? shown[teacherId];

            var rows = semester is null ? [] : await (
                from r in db.TeacherSections.AsNoTracking()
                where r.SemesterId == semester.Id
                join s in db.Sections on r.SectionId equals s.Id into found
                from s in found.DefaultIfEmpty()
                select new { r.TeacherId, r.Mode, r.CourseCode, r.SectionNumber, r.By, Named = s == null ? null : s.Instructor }).ToListAsync(ct);
            var midnight = OmanMidnight(now);
            var today = (await db.ClassChanges.Where(c => c.TeacherId != null && c.OnBehalfOf == null && c.CreatedAt >= midnight)
                    .Select(c => new { TeacherId = c.TeacherId!.Value, c.GroupId }).Distinct().ToListAsync(ct))
                .GroupBy(c => c.TeacherId).ToDictionary(g => g.Key, g => g.Count());
            var upcoming = (await db.ClassChanges.AsNoTracking().Where(c => c.TeacherId != null && c.EndedAt == null).ToListAsync(ct))
                .Where(c => ChangeRules.CanUndo(c, now)).GroupBy(c => c.TeacherId!.Value).ToDictionary(g => g.Key, g => g.Select(c => c.GroupId).Distinct().Count());

            return Results.Ok(new
            {
                changesEnabled = await ClassChanges.EnabledAsync(db, ct),
                waiting = links.Where(n => n.State == TeacherName.Pending && byId.ContainsKey(n.TeacherId)).Select(n => new
                {
                    linkId = n.Id, teacherId = n.TeacherId, email = byId[n.TeacherId].Email, teacher = shown[n.TeacherId], name = n.Name,
                    sections = sections.GetValueOrDefault(n.NameKey),
                    conflict = n.Conflict,
                    // Who has the name now, when someone does: staff reject or detach that one first.
                    heldBy = holders.TryGetValue(n.NameKey, out var holder) && holder != n.TeacherId ? Label(holder) : null,
                    at = n.CreatedAt,
                }),
                teachers = teachers.Select(t => new
                {
                    id = t.Id, email = t.Email, name = shown[t.Id],
                    // "staff" signs in by email; "student" is a student account that was given teaching rights.
                    kind = t.StudentId is null ? "staff" : "student",
                    universityId = t.StudentId is int student ? universityIds.GetValueOrDefault(student) : null,
                    studentId = isOwner ? t.StudentId : null,
                    frozen = t.FrozenAt is not null, frozenAt = t.FrozenAt, frozenBy = OwnerEndpoints.ShownActor(t.FrozenBy, isOwner),
                    lastSeenAt = t.LastSeenAt, createdAt = t.CreatedAt, createdBy = OwnerEndpoints.ShownActor(t.CreatedBy, isOwner),
                    names = links.Where(n => n.TeacherId == t.Id).Select(n => new
                    {
                        linkId = n.Id, name = n.Name, state = n.State, how = n.How, conflict = n.Conflict,
                        sections = sections.GetValueOrDefault(n.NameKey), suggestedName = n.SuggestedName,
                    }),
                    // Who added themselves to whose class; "by" is the owner for an add made through "view as".
                    added = rows.Where(r => r.TeacherId == t.Id && r.Mode == TeacherSection.AddedMode)
                        .OrderBy(r => r.CourseCode, StringComparer.Ordinal).ThenBy(r => r.SectionNumber.PadLeft(4, '0'), StringComparer.Ordinal)
                        .Select(r => new { code = r.CourseCode, number = r.SectionNumber, named = r.Named ?? "", removed = r.Named is null, by = OwnerEndpoints.ShownActor(r.By, isOwner) }),
                    hidden = rows.Count(r => r.TeacherId == t.Id && r.Mode == TeacherSection.HiddenMode),
                    changesToday = today.GetValueOrDefault(t.Id),
                    upcomingChanges = upcoming.GetValueOrDefault(t.Id),
                    key = TeacherKeys.For(t),
                }),
                // Every distinct instructor of this semester, with the account linked to it.
                names = instructors.GroupBy(TeacherNames.NameKey).Where(g => g.Key.Length > 0)
                    .Select(g => (Name: g.OrderBy(n => n, StringComparer.Ordinal).First(), g.Key, Count: g.Count()))
                    .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(n => new
                    {
                        name = n.Name, sections = n.Count, placeholder = TeacherNames.IsPlaceholder(n.Name),
                        teacherId = holders.TryGetValue(n.Key, out var holder) ? holder : (int?)null,
                        key = TeacherKeys.ForName(n.Name),
                    }),
                blocked = (await db.BlockedEmails.AsNoTracking().ToListAsync(ct)).OrderBy(b => b.Email, StringComparer.Ordinal)
                    .Select(b => new { email = b.Email, blockedAt = b.BlockedAt, by = OwnerEndpoints.ShownActor(b.BlockedBy, isOwner) }),
            });
        });

        // ---- Timetable names ----

        // A name a teacher picked: approved, it gives them its classes.
        admin.MapPost("/teachers/links/{linkId:int}/approve", async (int linkId, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var link = await db.TeacherNames.FirstOrDefaultAsync(n => n.Id == linkId, ct);
            if (link is null) return Errors.NotFound("That request is gone. Reload.");
            if (link.State != TeacherName.Pending) return Errors.Conflict("That request was already decided. Reload.");
            if (await HolderAsync(db, link.NameKey, link.TeacherId, ct) is { } holder)
                return Errors.Conflict($"{await LabelAsync(db, holder, ct)} already has this name. Reject or detach that one first.");
            (link.State, link.Conflict, link.DecidedAt, link.DecidedBy) = (TeacherName.Approved, false, now, await OwnerEndpoints.ActorName(http, db, ct));
            db.TeachNotices.Add(Notice(link.TeacherId, "name-approved", "Your name was approved", $"You're linked to {link.Name}. Its classes are on your timetable.", now));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Someone else took the name in the same moment: one account per name.
                return Errors.Conflict("Another account just got this name. Reload.");
            }
            await MarkWaitingAsync(db, link.NameKey, link.TeacherId, true, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // Turns a request down. Also the one-tap reverse of an automatic link: the row stays, so the account is never
        // linked by itself again.
        admin.MapPost("/teachers/links/{linkId:int}/reject", async (int linkId, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var link = await db.TeacherNames.FirstOrDefaultAsync(n => n.Id == linkId, ct);
            if (link is null) return Errors.NotFound("That request is gone. Reload.");
            if (link.State == TeacherName.Rejected) return Results.NoContent();
            var held = link.Holds;
            (link.State, link.Conflict, link.SuggestedName, link.DecidedAt, link.DecidedBy) = (TeacherName.Rejected, false, null, now, await OwnerEndpoints.ActorName(http, db, ct));
            db.TeachNotices.Add(held
                ? Notice(link.TeacherId, "name-rejected", $"You're no longer linked to {link.Name}", "Classy staff took this name off your account. Ask the Classy team if that's wrong.", now)
                : Notice(link.TeacherId, "name-rejected", "Your name wasn't approved", $"{link.Name} wasn't linked to your account. Ask the Classy team if that's wrong.", now));
            await db.SaveChangesAsync(ct);
            // Nobody holds it any more: those waiting for it are no longer in anyone's way.
            if (held) await MarkWaitingAsync(db, link.NameKey, link.TeacherId, false, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // The university re-spelled a name: one tap moves the link to the new spelling. Never done by itself.
        admin.MapPost("/teachers/links/{linkId:int}/same-person", async (int linkId, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var link = await db.TeacherNames.FirstOrDefaultAsync(n => n.Id == linkId, ct);
            if (link is null) return Errors.NotFound("That name is gone. Reload.");
            if (link.SuggestedName is not { Length: > 0 } suggested) return Errors.Conflict("There's no new spelling to accept. Reload.");
            var key = TeacherNames.NameKey(suggested);
            if (link.Holds && await HolderAsync(db, key, link.TeacherId, ct) is { } holder)
                return Errors.Conflict($"{await LabelAsync(db, holder, ct)} already has that name.");
            // The account's own other link to the new spelling (a request it made meanwhile) gives way to this one.
            await db.TeacherNames.Where(n => n.TeacherId == link.TeacherId && n.NameKey == key && n.Id != link.Id).ExecuteDeleteAsync(ct);
            (link.Name, link.NameKey, link.SuggestedName, link.DecidedAt, link.DecidedBy) = (suggested, key, null, clock.GetUtcNow(), await OwnerEndpoints.ActorName(http, db, ct));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return Errors.Conflict("Another account just got that name. Reload.");
            }
            if (link.Holds) await MarkWaitingAsync(db, key, link.TeacherId, true, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // Staff attach a name on purpose (a second name, or the name of a teacher whose email doesn't match).
        admin.MapPost("/teachers/{id:int}/names", async (int id, NameBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            if (!await db.Teachers.AnyAsync(t => t.Id == id, ct)) return Errors.NotFound("That teacher isn't on Classy any more.");
            var key = TeacherNames.NameKey(body.Name);
            // The name as the timetable writes it. A placeholder ("CS1 CS1") is nobody's name.
            var name = (await TeacherNames.PickableAsync(db, ct)).Where(n => TeacherNames.NameKey(n.Name) == key).Select(n => n.Name).FirstOrDefault();
            if (key.Length == 0 || name is null || name.Length > MaxName) return Errors.BadRequest("Pick a name from the timetable.");
            if (await HolderAsync(db, key, id, ct) is { } holder) return Errors.Conflict($"{await LabelAsync(db, holder, ct)} already has this name.");
            var link = await db.TeacherNames.FirstOrDefaultAsync(n => n.TeacherId == id && n.NameKey == key, ct);
            if (link is null) db.TeacherNames.Add(link = new TeacherName { TeacherId = id, NameKey = key, CreatedAt = now });
            var had = link.Holds && link.Id != 0;
            (link.Name, link.State, link.How, link.Conflict, link.SuggestedName, link.DecidedAt, link.DecidedBy) =
                (name, TeacherName.Approved, TeacherName.ByStaff, false, null, now, await OwnerEndpoints.ActorName(http, db, ct));
            if (!had) db.TeachNotices.Add(Notice(id, "name-approved", $"You're linked to {name}", "Classy staff added this name to your account. Its classes are on your timetable.", now));
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                return Errors.Conflict("Another account just got this name. Reload.");
            }
            await MarkWaitingAsync(db, key, id, true, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { linkId = link.Id });
        });

        // Takes a name off an account. A link the email made is kept as "rejected" instead of deleted: an account with
        // no link at all would be linked by its email again at its next visit.
        admin.MapDelete("/teachers/{id:int}/names/{linkId:int}", async (int id, int linkId, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var link = await db.TeacherNames.FirstOrDefaultAsync(n => n.Id == linkId && n.TeacherId == id, ct);
            if (link is null) return Errors.NotFound("That name is already off this account.");
            var held = link.Holds;
            if (link.How == TeacherName.ByEmail)
                (link.State, link.Conflict, link.SuggestedName, link.DecidedAt, link.DecidedBy) = (TeacherName.Rejected, false, null, now, await OwnerEndpoints.ActorName(http, db, ct));
            else db.TeacherNames.Remove(link);
            if (held) db.TeachNotices.Add(Notice(id, "name-rejected", $"You're no longer linked to {link.Name}", "Classy staff took this name off your account. Ask the Classy team if that's wrong.", now));
            await db.SaveChangesAsync(ct);
            if (held) await MarkWaitingAsync(db, link.NameKey, id, false, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        // ---- One account ----

        // Freeze: no class changes, no added sections, no office-hours edits. Live changes stay as they are.
        admin.MapPut("/teachers/{id:int}/frozen", async (int id, FrozenBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var teacher = await db.Teachers.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (teacher is null) return Errors.NotFound("That teacher isn't on Classy any more.");
            if (body.Frozen && teacher.FrozenAt is null) (teacher.FrozenAt, teacher.FrozenBy) = (clock.GetUtcNow(), await OwnerEndpoints.ActorName(http, db, ct));
            else if (!body.Frozen) (teacher.FrozenAt, teacher.FrozenBy) = (null, null);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { frozen = teacher.FrozenAt is not null });
        });

        // Ends every sign-in of a staff account on its next request. The account and everything on it stay.
        admin.MapPost("/teachers/{id:int}/sign-out", async (int id, AppDb db, CancellationToken ct) =>
        {
            var teacher = await db.Teachers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (teacher is null) return Errors.NotFound("That teacher isn't on Classy any more.");
            if (teacher.StudentId is not null) return Errors.Conflict("A teaching assistant signs in as a student. Remove their teaching rights instead.");
            await db.Teachers.Where(t => t.Id == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.SessionVersion, t => t.SessionVersion + 1), ct);
            return Results.NoContent();
        });

        // Remove and block. The email can't sign in again until staff unblock it.
        admin.MapPost("/teachers/{id:int}/remove", async (int id, RemoveBody? body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var teacher = await db.Teachers.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (teacher is null) return Errors.NotFound("That teacher isn't on Classy any more.");
            var undone = await RemoveAsync(db, teacher, body?.UndoChanges == true, await OwnerEndpoints.ActorName(http, db, ct), now, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { undone });
        });

        admin.MapPost("/teachers/unblock", async (EmailBody body, AppDb db, CancellationToken ct) =>
        {
            var email = (body.Email ?? "").Trim().ToLowerInvariant();
            return await db.BlockedEmails.Where(b => b.Email == email).ExecuteDeleteAsync(ct) == 0
                ? Errors.NotFound("That email isn't blocked.")
                : Results.NoContent();
        });

        // ---- The log of every teacher change, newest first ----

        admin.MapGet("/teacher-changes", async (int? semesterId, int? teacherId, bool? flagged, bool? live, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var isOwner = http.User.IsOwner();
            var now = clock.GetUtcNow();
            var sid = semesterId ?? (await StudentEndpoints.CurrentSemester(db, ct))?.Id;
            if (sid is null) return Results.Ok(new { changes = Array.Empty<object>() });
            var query = db.ClassChanges.AsNoTracking().Where(c => c.SemesterId == sid);
            if (teacherId is int teacher) query = query.Where(c => c.TeacherId == teacher);
            if (flagged == true) query = query.Where(c => c.Flagged);
            if (live == true) query = query.Where(c => c.EndedAt == null);
            else if (live == false) query = query.Where(c => c.EndedAt != null);
            // One row of the log is one teacher action on one real class (its codes share a group) in one state. Newest is the highest id.
            var rows = await query.OrderByDescending(c => c.Id).Take(LogRows * 8).ToListAsync(ct);
            var log = rows.GroupBy(c => (c.GroupId, State: c.EndReason ?? "live")).Take(LogRows)
                .Select(g => g.OrderBy(c => c.CourseCode, StringComparer.Ordinal).ThenBy(c => c.Id).ToList()).ToList();
            // How many Classy users each row reaches. Only ever counted.
            var sectionIds = log.SelectMany(g => g).Where(c => c.SectionId is not null).Select(c => c.SectionId!.Value).Distinct().ToList();
            var picks = sectionIds.Count == 0 ? null : (await db.Picks.Where(p => p.SectionId != null && sectionIds.Contains(p.SectionId.Value))
                .Select(p => new { SectionId = p.SectionId!.Value, p.StudentId }).ToListAsync(ct)).ToLookup(p => p.SectionId, p => p.StudentId);

            static object? At(DateOnly? date, TimeOnly? start, TimeOnly? end, string? room) => date is DateOnly on && start is TimeOnly from
                ? new { date = PlannerEngine.Iso(on), start = Timeslots.Format(from), end = end is TimeOnly to ? Timeslots.Format(to) : null, room = room ?? "" }
                : null;
            return Results.Ok(new
            {
                changes = log.Select(g =>
                {
                    var c = g[0];
                    var isLive = c.EndedAt is null;
                    return new
                    {
                        id = g.Min(x => x.Id), group = c.GroupId, kind = c.Kind, type = c.SessionType,
                        codes = g.Select(x => x.CourseCode).Distinct().ToList(), section = c.SectionNumber, name = c.CourseName,
                        from = At(c.OrigDate, c.OrigStart, c.OrigEnd, c.OrigRoom),
                        to = c.Kind == ClassChanges.Cancel ? null : At(c.NewDate, c.NewStart, c.NewEnd, c.NewRoom),
                        note = c.Note, by = c.ByName, byLabel = ShownBy(isOwner, c.ByKind, c.ByLabel, c.OnBehalfOf), onBehalfOf = c.OnBehalfOf,
                        teacherId = c.TeacherId, namedTeacher = c.NamedTeacher, flagged = g.Any(x => x.Flagged), at = c.CreatedAt,
                        state = isLive ? "live" : c.EndReason, endedBy = ShownEndedBy(isOwner, c.EndedBy), endedAt = c.EndedAt,
                        students = picks is null ? 0 : g.Where(x => x.SectionId is not null).SelectMany(x => picks[x.SectionId!.Value]).Distinct().Count(),
                        // The same limit a teacher has: until the class starts.
                        canUndo = isLive && g.All(x => ChangeRules.CanUndo(x, now)),
                    };
                }),
            });
        });

        // Staff undo any live change, until the class starts. Students who were told get "back on"; the maker is told
        // staff undid it. Not held by a freeze, the switch or the fresh-code rule.
        admin.MapPost("/teacher-changes/{groupId}/undo", async (string groupId, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            // The write lock first, as every change takes it: a teacher's undo and this one end the change once.
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var rows = await db.ClassChanges.Where(c => c.GroupId == groupId && c.EndedAt == null).ToListAsync(ct);
            if (rows.Count == 0) return Errors.Conflict(ChangeRules.Gone);
            if (rows.Any(c => !ChangeRules.CanUndo(c, now))) return Errors.BadRequest(ChangeRules.Started);
            await ChangeRules.EndAsync(db, rows, "undone-by-staff", $"staff:{await OwnerEndpoints.ActorName(http, db, ct)}", now, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { undone = rows.Count });
        });

        // ---- Owners only ----

        // The emergency switch: off stops every new class change at once. Live changes stay, and undo still works.
        owners.MapPut("/teachers/switch", async (SwitchBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var setting = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == ClassChanges.EnabledSetting, ct);
            if (setting is null) db.AppSettings.Add(setting = new AppSetting { Key = ClassChanges.EnabledSetting });
            (setting.Value, setting.UpdatedBy, setting.UpdatedAt) = (body.Enabled ? "true" : "false", await OwnerEndpoints.ActorName(http, db, ct), clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { changesEnabled = body.Enabled });
        });

        // Teaching rights for a student account (a teaching assistant): a teaching profile found from their student
        // sign-in. Timetable names are attached on the Teachers page, as for anyone.
        owners.MapGet("/students/{id:int}/teaching", async (int id, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await db.Students.AnyAsync(s => s.Id == id, ct)) return Errors.NotFound("That student doesn't exist.");
            var t = await db.Teachers.AsNoTracking().FirstOrDefaultAsync(x => x.StudentId == id, ct);
            return Results.Ok(new
            {
                teaching = t is null ? null : new
                {
                    teacherId = t.Id, displayName = t.DisplayName, createdAt = t.CreatedAt, createdBy = t.CreatedBy, frozen = t.FrozenAt is not null,
                    upcomingChanges = (await UpcomingAsync(db, t.Id, clock.GetUtcNow(), ct)).Select(c => c.GroupId).Distinct().Count(),
                    key = TeacherKeys.For(t),
                },
            });
        });

        owners.MapPut("/students/{id:int}/teaching", async (int id, TeachingBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var name = Plain(body.DisplayName);
            if (name.Length is 0 or > MaxDisplayName) return Errors.BadRequest($"Enter the name students will see (up to {MaxDisplayName} characters).");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var universityId = await db.Students.Where(s => s.Id == id).Select(s => s.UniversityId).FirstOrDefaultAsync(ct);
            if (universityId is null) return Errors.NotFound("That student doesn't exist.");
            var t = await db.Teachers.FirstOrDefaultAsync(x => x.StudentId == id, ct);
            if (t is null)
            {
                db.Teachers.Add(t = new Teacher { StudentId = id, CreatedAt = now, CreatedBy = await OwnerEndpoints.ActorName(http, db, ct) });
                // A sign-in code asked for before the grant must not count as one entered after it: the first class
                // change needs a code from after this moment.
                await db.LoginCodes.Where(c => c.UniversityId == universityId).ExecuteDeleteAsync(ct);
            }
            t.DisplayName = name;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Granted twice at once: one of them made the profile.
                return Errors.Conflict("That didn't save. Try again.");
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new { teaching = new { teacherId = t.Id, displayName = t.DisplayName } });
        });

        // Takes teaching rights away, with the same offer as removing a teacher: also undo their upcoming changes.
        owners.MapDelete("/students/{id:int}/teaching", async (int id, bool? undoChanges, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var t = await db.Teachers.FirstOrDefaultAsync(x => x.StudentId == id, ct);
            if (t is null) return Errors.NotFound("This student has no teaching rights.");
            var undone = await RemoveAsync(db, t, undoChanges == true, await OwnerEndpoints.ActorName(http, db, ct), now, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { undone });
        });
    }
}
