using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Teachers;

/// <summary>
/// What students read about teachers: who teaches each class (with office hours and details, on every class of a
/// timetable), and the teacher page. Nothing here says anything about students.
/// </summary>
public static class TeacherPages
{
    static readonly TimeSpan Oman = TimeSpan.FromHours(4);

    static IEnumerable<object> HoursOf(IEnumerable<OfficeHour> hours) =>
        hours.OrderBy(h => h.Day).ThenBy(h => h.Start).ThenBy(h => h.Id)
            .Select(h => new { day = h.Day, start = Timeslots.Format(h.Start), end = Timeslots.Format(h.End), room = h.Room });

    /// <summary>The address students may write to: the sign-in address, and only when the teacher turned it on.</summary>
    static string? EmailOf(Teacher t) => t.ShowEmail ? t.Email : null;

    /// <summary>The names students see for these accounts (<see cref="TeacherNames.Shown"/>), by account id.</summary>
    static async Task<Dictionary<int, string>> ShownAsync(AppDb db, IReadOnlyCollection<Teacher> accounts, CancellationToken ct)
    {
        if (accounts.Count == 0) return [];
        var ids = accounts.Select(t => t.Id).ToList();
        var names = await db.TeacherNames.Where(n => ids.Contains(n.TeacherId)).ToListAsync(ct);
        var unlinked = accounts.Any(t => !names.Any(n => n.TeacherId == t.Id && n.Holds));
        IReadOnlySet<string> taken = unlinked ? await TeacherNames.TakenKeysAsync(db, ct) : new HashSet<string>();
        return accounts.ToDictionary(t => t.Id, t => TeacherNames.Shown(t, names, taken));
    }

    /// <summary>
    /// The teachers of each section, for the class page: first the instructor named on the timetable (with the details
    /// of the account holding that name, when there is one), then each account that added the section.
    /// </summary>
    public static async Task<Dictionary<int, List<object>>> RefsAsync(AppDb db, IReadOnlyCollection<Section> sections, CancellationToken ct)
    {
        var result = new Dictionary<int, List<object>>();
        if (sections.Count == 0) return result;
        var (holders, adders) = await TeacherSchedule.AccountsAsync(db, sections.Select(s => (s.Id, s.Instructor)).ToList(), ct);
        var accounts = holders.Values.Concat(adders.SelectMany(g => g)).DistinctBy(t => t.Id).ToList();
        var ids = accounts.Select(t => t.Id).ToList();
        var hours = (ids.Count == 0 ? [] : await db.OfficeHours.Where(h => ids.Contains(h.TeacherId)).ToListAsync(ct)).ToLookup(h => h.TeacherId);
        var shown = await ShownAsync(db, adders.SelectMany(g => g).DistinctBy(t => t.Id).ToList(), ct);

        Dictionary<string, object?> Ref(string name, string key, bool tappable, bool listed, Teacher? account)
        {
            var r = new Dictionary<string, object?> { ["name"] = name, ["key"] = key, ["tappable"] = tappable, ["listed"] = listed };
            if (account is null) return r;
            r["officeRoom"] = account.OfficeRoom;
            r["note"] = account.ProfileNote;
            r["email"] = EmailOf(account);
            r["officeHours"] = HoursOf(hours[account.Id]).ToList();
            return r;
        }

        foreach (var s in sections)
        {
            if (result.ContainsKey(s.Id)) continue;
            var refs = result[s.Id] = [];
            Teacher? named = null;
            if (s.Instructor.Trim() is { Length: > 0 } instructor)
            {
                // A placeholder ("CS1 CS1") is plain text: no page, and no account behind it.
                var person = !TeacherNames.IsPlaceholder(instructor);
                named = person ? holders.GetValueOrDefault(TeacherNames.NameKey(instructor)) : null;
                refs.Add(Ref(instructor, TeacherKeys.ForName(instructor), person, true, named));
            }
            // Someone who added the class shows on it straight away.
            foreach (var t in adders[s.Id].Where(t => t.Id != named?.Id).DistinctBy(t => t.Id))
                refs.Add(Ref(shown[t.Id], TeacherKeys.For(t), true, false, t));
        }
        return result;
    }

    /// <summary>This Oman week, Sunday to Saturday.</summary>
    static (DateOnly From, DateOnly To) Week(DateTimeOffset now)
    {
        var sunday = SemesterWeeks.Sunday(DateOnly.FromDateTime(now.ToOffset(Oman).DateTime));
        return (sunday, sunday.AddDays(6));
    }

    /// <summary>The sessions of these sections in a week, one row per real class.</summary>
    static List<object> Classes(IReadOnlyList<Taught> on, SemesterSpan span, DateOnly from, DateOnly to)
    {
        var rows = new List<(DateOnly Date, TimeOnly Start, string Code, object Row)>();
        foreach (var block in TeacherSchedule.Blocks(on))
        {
            var (lead, m) = block.Lead;
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                if (!TeacherSchedule.On(span, m, d)) continue;
                rows.Add((d, m.Start, lead.Course.Code, new
                {
                    codes = block.Members.Select(x => x.Class.Course.Code).ToList(),
                    name = lead.Course.Name,
                    section = lead.Section.Number,
                    date = PlannerEngine.Iso(d),
                    start = Timeslots.Format(m.Start),
                    end = Timeslots.Format(m.End),
                    room = m.Room,
                    type = SessionKinds.Of(m.Room),
                    // Set once teachers can change a class: cancelled, moved, room or extra.
                    status = (string?)null,
                    note = (string?)null,
                }));
            }
        }
        return rows.OrderBy(r => r.Date).ThenBy(r => r.Start).ThenBy(r => r.Code, StringComparer.Ordinal).Select(r => r.Row).ToList();
    }

    public static void MapTeacherPages(this RouteGroupBuilder api)
    {
        // A teacher's page, as students see it: office hours and details, and their classes this week. The key is an
        // account (t-12) or a timetable name (n-…), so a teacher who never signed in has a page too.
        api.MapGet("/teachers/{key}", async (string key, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var gone = Errors.NotFound("That teacher isn't on Classy.");
            var semester = await StudentEndpoints.CurrentSemester(db, ct);
            var instructors = await TeacherNames.InstructorsAsync(db, ct);

            Teacher? account;
            string name;
            List<TeacherName> links;
            if (TeacherKeys.AccountId(key) is int id)
            {
                account = await db.Teachers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
                if (account is null) return gone;
                links = await db.TeacherNames.Where(n => n.TeacherId == id).ToListAsync(ct);
                // Ids are counted easily, so an account has a page only once students can see it somewhere: it holds a
                // name on this semester's timetable, or it added a section. The account itself and owners always may.
                var user = http.User;
                var self = (user.IsTeacher() && user.Id() == id) || (user.IsStudent() && account.StudentId == user.Id());
                if (!self && !user.IsOwner())
                {
                    var written = instructors.Select(TeacherNames.NameKey).ToHashSet();
                    var seen = links.Any(n => n.Holds && written.Contains(n.NameKey))
                        || (semester is not null && await db.TeacherSections.AnyAsync(r => r.TeacherId == id && r.SemesterId == semester.Id
                            && r.Mode == TeacherSection.AddedMode && r.SectionId != null, ct));
                    if (!seen) return gone;
                }
                name = await TeacherNames.ShownAsync(db, account, links, ct);
            }
            else
            {
                if (TeacherKeys.Name(key) is not string typed || TeacherNames.NameKey(typed) is not { Length: > 0 } nameKey) return gone;
                // The name as the timetable writes it. A placeholder ("CS1 CS1") or a name that isn't there has no page.
                var written = instructors.Where(n => TeacherNames.NameKey(n) == nameKey && !TeacherNames.IsPlaceholder(n))
                    .Select(n => n.Trim()).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
                if (written is null) return gone;
                name = written;
                var held = await db.TeacherNames.FirstOrDefaultAsync(n => n.NameKey == nameKey && (n.State == TeacherName.Auto || n.State == TeacherName.Approved), ct);
                account = held is null ? null : await db.Teachers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == held.TeacherId, ct);
                links = account is null ? [] : await db.TeacherNames.Where(n => n.TeacherId == account.Id).ToListAsync(ct);
                if (account is null) links = [new TeacherName { Name = written, NameKey = nameKey, State = TeacherName.Approved }];
            }

            var (from, to) = Week(clock.GetUtcNow());
            List<object> classes = [];
            if (semester is not null)
            {
                // An account: the sections on its timetable (its names, plus added, minus hidden). A bare name: the sections under it.
                var keys = links.Where(n => n.Holds).Select(n => n.NameKey).Distinct().ToList();
                var on = (await TeacherSchedule.SectionsAsync(db, semester.Id, account, keys, ct)).Where(t => !t.Hidden).ToList();
                classes = Classes(on, await SemesterSpan.ForAsync(db, semester, ct), from, to);
            }
            var hours = account is null ? [] : await db.OfficeHours.Where(h => h.TeacherId == account.Id).ToListAsync(ct);
            return Results.Ok(new
            {
                name,
                hasAccount = account is not null,
                officeRoom = account?.OfficeRoom ?? "",
                note = account?.ProfileNote ?? "",
                email = account is null ? null : EmailOf(account),
                officeHours = HoursOf(hours),
                week = new { from = PlannerEngine.Iso(from), to = PlannerEngine.Iso(to) },
                classes,
            });
        }).RequireAuthorization(Session.SignedInPolicy);
    }
}
