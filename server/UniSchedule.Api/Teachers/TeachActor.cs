using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Teachers;

/// <summary>
/// Who a /api/teach request is acting as, and who is really signed in. There are three ways in: a teacher's own
/// cookie; a student with teaching rights (found from their student cookie); and an owner using "view as" (the
/// X-Teach-As header). All three are worked out here, once per request, so the kind always travels with the id.
/// </summary>
public sealed record TeachActor(
    Teacher? Profile,                  // the account acted for; null only when an owner views a name nobody claimed
    IReadOnlyList<string> Names,       // timetable names whose sections are theirs (auto or approved)
    IReadOnlyList<string> NameKeys,
    string ByKind, int ById,           // "teacher" | "student" | "admin": who is signed in
    string ByLabel,                    // staff email, university ID or dashboard username (for the log)
    string ByName,                     // what students see
    string? OnBehalfOf)                // set for an owner's "view as": the name being viewed
{
    /// <summary>The header an owner's browser tab sends on /api/teach calls to look at a teacher's view: a teacher key.</summary>
    public const string ViewAsHeader = "X-Teach-As";
    public const string StaffName = "Classy staff";
    public const string NotInViewAs = "Not available while viewing as a teacher.";
    public const string NoAccount = "This name has no Classy account yet.";
    /// <summary>What a frozen account is told wherever it could put something in front of students.</summary>
    public const string Frozen = "Your class changes are paused. Ask the Classy team.";
    const string ItemKey = "teach-actor";

    public bool ViewAs => OnBehalfOf is not null;
    /// <summary>Own slots, devices, calendar link and the name step belong to the teacher, not to an owner looking in.</summary>
    public bool Personal => !ViewAs;

    /// <summary>The actor of this /api/teach request (set by <see cref="Filter"/>).</summary>
    public static TeachActor Of(HttpContext http) =>
        http.Items[ItemKey] as TeachActor ?? throw new InvalidOperationException("Not a /api/teach request.");

    /// <summary>On the /api/teach group: works out the actor once, or answers 403/404 when there is none.</summary>
    public static async ValueTask<object?> Filter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var (actor, error) = await ResolveAsync(http, http.RequestServices.GetRequiredService<AppDb>(), http.RequestAborted);
        if (actor is null) return error;
        http.Items[ItemKey] = actor;
        return await next(context);
    }

    static async Task<(TeachActor? Actor, IResult? Error)> ResolveAsync(HttpContext http, AppDb db, CancellationToken ct)
    {
        var user = http.User;
        var viewAs = http.Request.Headers[ViewAsHeader].ToString().Trim();
        if (viewAs.Length > 0)
        {
            // Only owners (a student owner or a dashboard owner) may look at someone else's teacher view.
            if (!user.IsOwner()) return (null, Errors.Forbidden("Only owners can open another teacher's view."));
            var (target, name, error) = await ViewedAsync(db, viewAs, ct);
            if (error is not null) return (null, error);
            var links = target is null ? [] : await HeldAsync(db, target.Id, ct);
            // A bare name nobody claimed: its sections are the ones under that name.
            var names = target is null ? [name!] : links.Select(n => n.Name).ToList();
            var onBehalfOf = name ?? await TeacherNames.ShownAsync(db, target!, links, ct);
            return (new TeachActor(target, names, Keys(names), user.Kind()!, user.Id(), await OwnerEndpoints.ActorName(http, db, ct), StaffName, onBehalfOf), null);
        }

        if (user.IsTeacher())
        {
            // The cookie was checked on this request, so the row is there.
            var id = user.Id();
            var t = await db.Teachers.FirstOrDefaultAsync(x => x.Id == id && x.StudentId == null, ct);
            return t is null ? (null, Errors.Forbidden("Sign in again.")) : (await OwnAsync(db, t, "teacher", id, t.Email!, ct), null);
        }
        if (user.IsStudent())
        {
            var id = user.Id();
            var t = await db.Teachers.FirstOrDefaultAsync(x => x.StudentId == id, ct);
            if (t is null) return (null, Errors.Forbidden("Teaching isn't turned on for this account."));
            var universityId = await db.Students.Where(s => s.Id == id).Select(s => s.UniversityId).FirstAsync(ct);
            return (await OwnAsync(db, t, "student", id, universityId, ct), null);
        }
        return (null, Errors.Forbidden("Open the teacher view from the dashboard."));
    }

    static async Task<TeachActor> OwnAsync(AppDb db, Teacher t, string kind, int id, string label, CancellationToken ct)
    {
        var links = await HeldAsync(db, t.Id, ct);
        var names = links.Select(n => n.Name).ToList();
        return new TeachActor(t, names, Keys(names), kind, id, label, await TeacherNames.ShownAsync(db, t, links, ct), null);
    }

    /// <summary>The account's links that give classes (auto or approved), oldest first.</summary>
    static Task<List<TeacherName>> HeldAsync(AppDb db, int teacherId, CancellationToken ct) =>
        db.TeacherNames.Where(n => n.TeacherId == teacherId && (n.State == TeacherName.Auto || n.State == TeacherName.Approved))
            .OrderBy(n => n.Id).ToListAsync(ct);

    static List<string> Keys(IEnumerable<string> names) => names.Select(TeacherNames.NameKey).Distinct().ToList();

    /// <summary>
    /// What a "view as" key points at: an account (t-…), or a timetable name (n-…) with the account holding it, if any.
    /// <c>Name</c> is set for a name key: the name as written on the timetable.
    /// </summary>
    static async Task<(Teacher? Target, string? Name, IResult? Error)> ViewedAsync(AppDb db, string key, CancellationToken ct)
    {
        if (TeacherKeys.AccountId(key) is int teacherId)
        {
            var t = await db.Teachers.FirstOrDefaultAsync(x => x.Id == teacherId, ct);
            return t is null ? (null, null, Errors.NotFound("That teacher isn't on Classy any more.")) : (t, null, null);
        }
        if (TeacherKeys.Name(key) is not string typed || TeacherNames.NameKey(typed) is not { Length: > 0 } nameKey)
            return (null, null, Errors.NotFound("That name isn't on the timetable."));
        // Placeholders are allowed here: owners manage those sections this way.
        var written = (await TeacherNames.InstructorsAsync(db, ct)).Where(n => TeacherNames.NameKey(n) == nameKey)
            .OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault()?.Trim();
        var held = await db.TeacherNames.Where(n => n.NameKey == nameKey && (n.State == TeacherName.Auto || n.State == TeacherName.Approved))
            .FirstOrDefaultAsync(ct);
        if (written is null && held is null) return (null, null, Errors.NotFound("That name isn't on the timetable."));
        var holder = held is null ? null : await db.Teachers.FirstOrDefaultAsync(x => x.Id == held.TeacherId, ct);
        return (holder, written ?? held!.Name, null);
    }

    /// <summary>
    /// Null when this browser may change a class now, else the 403 that makes the app ask for a new emailed code:
    /// the last code must be at most 7 days old, and for a student acting as their own teaching profile, entered after
    /// they were given teaching rights. Dashboard password accounts are exempt (they have no code).
    /// </summary>
    public static IResult? CodeNeeded(HttpContext http, TimeProvider clock) =>
        CodeFresh(http, clock.GetUtcNow()) ? null : Errors.CodeNeeded("Confirm it's you with a new code before changing a class.");

    /// <summary>Whether <see cref="CodeNeeded"/> would let a change through right now.</summary>
    public static bool CodeFresh(HttpContext http, DateTimeOffset now)
    {
        if (http.User.IsAdmin()) return true;
        var actor = Of(http);
        // A browser where the student signed in before the grant can't change classes until a code is entered after it.
        var since = actor.ByKind == "student" && !actor.ViewAs ? actor.Profile?.CreatedAt : null;
        return http.User.CodeIsFresh(now, since);
    }
}

public static class TeachActorExtensions
{
    /// <summary>Who this /api/teach request is acting as. Handlers read this and never the cookie's id themselves.</summary>
    public static TeachActor Teach(this HttpContext http) => TeachActor.Of(http);
}

/// <summary>
/// Teacher keys, used in links and in "view as": <c>t-12</c> is the account with id 12, <c>n-…</c> is a timetable name
/// (base64url of its UTF-8 text), so a teacher who never signed in has one too.
/// </summary>
public static class TeacherKeys
{
    const int MaxLength = 400;
    static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string For(Teacher t) => $"t-{t.Id.ToString(CultureInfo.InvariantCulture)}";
    public static string ForName(string name) => "n-" + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(name.Trim()));

    /// <summary>The account id of a <c>t-</c> key, or null.</summary>
    public static int? AccountId(string? key) =>
        key is { Length: > 2 and <= 12 } && key.StartsWith("t-", StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The name of an <c>n-</c> key, or null when it isn't one.</summary>
    public static string? Name(string? key)
    {
        if (key is not { Length: > 2 and <= MaxLength } || !key.StartsWith("n-", StringComparison.Ordinal)) return null;
        try
        {
            return Strict.GetString(Base64Url.DecodeFromChars(key.AsSpan(2)));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
    }
}
