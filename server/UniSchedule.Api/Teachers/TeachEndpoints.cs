using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Teachers;

/// <summary>The teacher side (/api/teach): who I am, my timetable name, and my inbox of notices.</summary>
public static class TeachEndpoints
{
    public record NameBody(string? Name, bool None = false);
    public record SeenNoticesBody(List<int>? Ids);

    /// <summary>Notices older than this drop out of the teacher's inbox even when never dismissed.</summary>
    static readonly TimeSpan NoticesFor = TimeSpan.FromDays(14);
    const int MaxNoticeIds = 100;

    static IEnumerable<object> NamesOf(IEnumerable<TeacherName> names) =>
        names.OrderBy(n => n.Id).Select(n => new { name = n.Name, state = n.State });

    public static void MapTeach(this RouteGroupBuilder api)
    {
        // Anyone signed in may knock; the filter decides who is acting (a teacher, a student with teaching rights, or
        // an owner viewing as a teacher) and answers 403 for everyone else.
        var teach = api.MapGroup("/teach").RequireAuthorization(Session.SignedInPolicy).AddEndpointFilter(TeachActor.Filter);

        teach.MapGet("/me", async (HttpContext http, AppDb db, TimeProvider clock, IConfiguration config, CancellationToken ct) =>
        {
            var actor = http.Teach();
            var p = actor.Profile;
            var now = clock.GetUtcNow();
            // A name that wasn't on the timetable at sign-in may be there now. Never while an owner is looking in.
            if (p is not null && actor.Personal) await TeacherNames.LinkOnSignInAsync(db, p, now, ct);
            var names = p is null ? [] : await db.TeacherNames.Where(n => n.TeacherId == p.Id).ToListAsync(ct);
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            var since = now - NoticesFor;
            var notices = p is null ? [] : await db.TeachNotices
                .Where(n => n.Audience == TeachNotice.ToTeacher && n.TeacherId == p.Id && n.SeenAt == null && n.CreatedAt > since)
                .OrderByDescending(n => n.Id).Take(50).ToListAsync(ct);
            return Results.Ok(new
            {
                id = p?.Id,
                email = p?.Email,
                // The shown name; for a name nobody has claimed, the name itself.
                name = p is null ? actor.OnBehalfOf! : await TeacherNames.ShownAsync(db, p, names, ct),
                kind = actor.ViewAs ? "view" : p!.StudentId is null ? "staff" : "student",
                @as = actor.OnBehalfOf,
                personal = actor.Personal,
                semester = semester is null ? null : new { semester.Id, semester.Name, weeks = await SemesterWeeks.ForAsync(db, semester, ct) },
                plannerEnabled = await PlannerEngine.Enabled(db, config, ct),
                // A name on the timetable that has no account still has its classes.
                status = p is null ? "linked" : TeacherNames.Status(p, names),
                names = NamesOf(names),
                frozen = p?.FrozenAt is not null,
                // Class changes can't be switched off yet.
                changesEnabled = true,
                codeFresh = TeachActor.CodeFresh(http, now),
                notices = notices.Select(n => new { id = n.Id, kind = n.Kind, title = n.Title, body = n.Body, url = n.Url, at = n.CreatedAt }),
            });
        });

        // ---- The name step: staff accounts only, and never for an owner looking in ----
        teach.MapGet("/names", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            if (NameStepRefusal(http.Teach()) is { } refusal) return refusal;
            return Results.Ok(new { names = (await TeacherNames.PickableAsync(db, ct)).Select(n => new { name = n.Name, sections = n.Sections }) });
        });

        // Asks for a name: it waits for an owner or RSA to approve it, and adds nothing to the timetable until then.
        // { none: true } is "My name isn't here": carry on with an empty timetable.
        teach.MapPost("/names", async (NameBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (NameStepRefusal(actor) is { } refusal) return refusal;
            var p = actor.Profile!;
            var now = clock.GetUtcNow();
            if (body.None)
            {
                p.NameSkippedAt ??= now;
                await db.SaveChangesAsync(ct);
            }
            else
            {
                var key = TeacherNames.NameKey(body.Name);
                var name = (await TeacherNames.PickableAsync(db, ct)).Where(n => TeacherNames.NameKey(n.Name) == key).Select(n => n.Name).FirstOrDefault();
                if (key.Length == 0 || name is null) return Errors.BadRequest("Pick your name from the list.");
                var mine = await db.TeacherNames.Where(n => n.TeacherId == p.Id).ToListAsync(ct);
                var link = mine.FirstOrDefault(n => n.NameKey == key);
                // Already theirs: nothing to ask for.
                if (link is null || !link.Holds)
                {
                    // One request at a time: asking for another name drops the earlier request.
                    db.TeacherNames.RemoveRange(mine.Where(n => n.State == TeacherName.Pending && n.NameKey != key));
                    if (link is null) db.TeacherNames.Add(link = new TeacherName { TeacherId = p.Id, NameKey = key });
                    link.Name = name;
                    link.State = TeacherName.Pending;
                    link.How = TeacherName.Picked;
                    link.Conflict = await db.TeacherNames.AnyAsync(n => n.NameKey == key && n.TeacherId != p.Id && (n.State == TeacherName.Auto || n.State == TeacherName.Approved), ct);
                    link.SuggestedName = null;
                    link.CreatedAt = now;
                    link.DecidedAt = null;
                    link.DecidedBy = null;
                    try
                    {
                        await db.SaveChangesAsync(ct);
                    }
                    catch (DbUpdateException)
                    {
                        // The same request sent twice at once: one of them made the row.
                        return Errors.Conflict("That didn't save. Try again.");
                    }
                }
            }
            var names = await db.TeacherNames.Where(n => n.TeacherId == p.Id).ToListAsync(ct);
            return Results.Ok(new { status = TeacherNames.Status(p, names), names = NamesOf(names) });
        });

        // "Got it" on notices in the banner. Only the caller's own: counting ids can't clear another teacher's inbox.
        teach.MapPost("/notices/seen", async (SeenNoticesBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            if (body.Ids is not { Count: > 0 and <= MaxNoticeIds } ids) return Errors.BadRequest($"Send between 1 and {MaxNoticeIds} notices.");
            var me = actor.Profile!.Id;
            var now = clock.GetUtcNow();
            await db.TeachNotices
                .Where(n => ids.Contains(n.Id) && n.Audience == TeachNotice.ToTeacher && n.TeacherId == me && n.SeenAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(n => n.SeenAt, now), ct);
            return Results.NoContent();
        });

        // A lost phone or a copied cookie: every other sign-in of this staff account ends on its next request. This
        // browser stays signed in, with the same code time as before (it gives no fresh code).
        teach.MapPost("/sign-out-everywhere", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            // A teaching assistant's sign-in is their student one.
            if (actor.ByKind != "teacher") return Errors.Conflict("This is for staff accounts. Sign out from the student menu instead.");
            var id = actor.Profile!.Id;
            await db.Teachers.Where(t => t.Id == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.SessionVersion, t => t.SessionVersion + 1), ct);
            var teacher = await db.Teachers.AsNoTracking().FirstAsync(t => t.Id == id, ct);
            await Session.SignInTeacherAsync(http, teacher, http.User.CodeAt() ?? DateTimeOffset.UnixEpoch);
            return Results.NoContent();
        });
    }

    /// <summary>Picking a name is for staff accounts: owners attach names to a teaching assistant in the dashboard.</summary>
    static IResult? NameStepRefusal(TeachActor actor) =>
        !actor.Personal ? Errors.Conflict(TeachActor.NotInViewAs)
        : actor.ByKind != "teacher" ? Errors.Conflict("Ask the Classy team to link your name to your classes.")
        : null;
}
