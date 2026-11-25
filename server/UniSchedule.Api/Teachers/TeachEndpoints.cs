using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Planner;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Teachers;

/// <summary>
/// The teacher side (/api/teach): who I am, my timetable name and inbox of notices, my teaching timetable, the
/// sections on it, my own slots, and my office hours and details.
/// </summary>
public static class TeachEndpoints
{
    public record NameBody(string? Name, bool None = false);
    public record SeenNoticesBody(List<int>? Ids);

    /// <summary>Notices older than this drop out of the teacher's inbox even when never dismissed.</summary>
    static readonly TimeSpan NoticesFor = TimeSpan.FromDays(14);
    const int MaxNoticeIds = 100;

    public record SeenBody(int? SectionId);
    public record SectionBody(bool? On);
    public record HourBody(int? Day, string? Start, string? End, string? Room);
    public record OfficeBody(string? OfficeRoom, string? Note, bool? ShowEmail, List<HourBody>? Hours);

    public const int MaxAddedSections = 60;
    public const int MaxOfficeHours = 10;
    const int MaxSearchCourses = 50;
    static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    static string PaddedNumber(Section s) => s.Number.PadLeft(4, '0');

    /// <summary>Plain text of a limited length: trimmed, with control characters taken out.</summary>
    static string Plain(string? text) => new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

    static bool TryTime(string? s, out TimeOnly t) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out t);

    static object Of(TeacherSlot s) => PersonalEndpoints.Of(s.Id, new(s.Title, s.Day, s.Start, s.End, s.Date, s.Place, s.Note));

    static string? Fill(TeacherSlot slot, PersonalEndpoints.SlotBody body) => PersonalEndpoints.Fill(slot, body, (s, f) =>
        (s.Title, s.Day, s.Start, s.End, s.Date, s.Place, s.Note) = (f.Title, f.Day, f.Start, f.End, f.Date, f.Place, f.Note));

    /// <summary>What a section is to this teacher: under their name, added, hidden, or nothing.</summary>
    static string? StateOf(Taught? t) => t is null ? null : t.Hidden ? "hidden" : t.Own ? "own" : "added";

    /// <summary>A row of "Edit my classes". <c>when</c> reads "Sun 08:00 · Tue 11:30".</summary>
    static object Row(Course course, Section section, string? state) => new
    {
        id = section.Id,
        code = course.Code,
        number = section.Number,
        instructor = section.Instructor,
        when = string.Join(" · ", section.Meetings.Select(m => (m.Day, m.Start)).Distinct().OrderBy(x => x.Day).ThenBy(x => x.Start)
            .Select(x => $"{DayNames[Math.Clamp(x.Day, 0, 6)]} {Timeslots.Format(x.Start)}")),
        state,
    };

    /// <summary>A notice in the teacher's inbox that an owner changed something of theirs through "view as". One tag, so a run of edits is one notification.</summary>
    static TeachNotice StaffEdit(TeachActor actor, string body, string url, DateTimeOffset now) => new()
    {
        Audience = TeachNotice.ToTeacher, TeacherId = actor.Profile!.Id, Kind = "staff-edit", Title = body, Body = "", Url = url,
        Tag = $"staff-edit-{actor.Profile.Id.ToString(CultureInfo.InvariantCulture)}", ByLabel = actor.ByLabel, CreatedAt = now,
    };

    static async Task<object> OfficeOf(AppDb db, Teacher p, string name, CancellationToken ct) => new
    {
        name,
        email = p.Email,
        officeRoom = p.OfficeRoom,
        note = p.ProfileNote,
        showEmail = p.ShowEmail,
        hours = (await db.OfficeHours.Where(h => h.TeacherId == p.Id).ToListAsync(ct)).OrderBy(h => h.Day).ThenBy(h => h.Start).ThenBy(h => h.Id)
            .Select(h => new { id = h.Id, day = h.Day, start = Timeslots.Format(h.Start), end = Timeslots.Format(h.End), room = h.Room }),
    };

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
                // Owners can switch all class changes off at once.
                changesEnabled = await ClassChanges.EnabledAsync(db, ct),
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

        // ---- The teaching timetable ----

        // The same shape as a student's timetable, for the sections on the teacher's timetable: under their names, plus
        // the ones they added, minus the ones they hid.
        teach.MapGet("/timetable", async (HttpContext http, AppDb db, IConfiguration config, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = http.Teach();
            var p = actor.Profile;
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is null)
            {
                var none = TimetableView.Empty();
                (none["officeHours"], none["changes"], none["mergeSizes"], none["removed"]) = (Array.Empty<object>(), Array.Empty<object>(), new Dictionary<string, int>(), Array.Empty<object>());
                return Results.Ok(none);
            }
            var on = (await TeacherSchedule.SectionsAsync(db, current.Id, actor, ct)).Where(t => !t.Hidden).ToList();
            // Class size: the exact number of Classy users who picked the section. Numbers only.
            var sizes = await TeacherSchedule.SizesAsync(db, on.Select(t => t.Section.Id).ToList(), ct);
            // "Changed" is the teacher's own badge: an owner looking in neither sees nor clears it.
            var seen = p is null || actor.ViewAs ? [] : await db.TeacherSeens.Where(x => x.TeacherId == p.Id).ToDictionaryAsync(x => x.SectionId, x => x.SeenAt, ct);
            bool Changed(Section s) => p is not null && !actor.ViewAs && s.ChangedAt is DateTimeOffset at
                && at > (seen.TryGetValue(s.Id, out var seenAt) ? seenAt : p.CreatedAt);
            var changes = await ClassChanges.LoadAsync(db, current.Id, on.Select(t => t.Section).ToList(), ct);
            // The batch reps' published events for these sections, once the planner is on. A teacher's change wins over one.
            var planner = await PlannerEngine.Enabled(db, config, ct)
                ? (await PlannerEngine.PublishedAsync(db, current.Id, ct)).For(TeacherSchedule.AsPicks(on), changes.Empty ? null : changes.Shadows)
                : (Events: new List<PlannerOccurrence>(), Excluded: new Dictionary<string, List<string>>());
            // Own slots are private: an owner looking in gets none.
            var slots = p is null || actor.ViewAs ? [] : await db.TeacherSlots.Where(s => s.TeacherId == p.Id && s.SemesterId == current.Id).ToListAsync(ct);
            var hours = p is null ? [] : await db.OfficeHours.Where(h => h.TeacherId == p.Id).ToListAsync(ct);
            var removed = p is null ? [] : await db.TeacherSections
                .Where(r => r.TeacherId == p.Id && r.SemesterId == current.Id && r.Mode == TeacherSection.AddedMode && r.SectionId == null)
                .OrderBy(r => r.CourseCode).ThenBy(r => r.SectionNumber).Select(r => new { code = r.CourseCode, number = r.SectionNumber }).ToListAsync(ct);

            var timetable = await TimetableView.BuildAsync(db, current,
                on.Select(t => new ClassRow(t.Course, t.Section, Changed(t.Section), null, sizes.GetValueOrDefault(t.Section.Id), t.Own, t.Added)).ToList(),
                slots.OrderBy(s => s.Date).ThenBy(s => s.Day).ThenBy(s => s.Start).Select(Of),
                planner, [], actor, ct,
                // What this teacher may still do with each change: every section on the timetable is one they may change.
                new ChangeViewer(actor, await ClassChanges.EnabledAsync(db, ct), clock.GetUtcNow(), on.Select(t => t.Section.Id).ToHashSet()));
            // Office hours are weekly and skip the days off; the app also keeps them inside the teaching weeks.
            var daysOff = await DaysOff.ForAsync(db, current.Id, ct);
            timetable["officeHours"] = hours.OrderBy(h => h.Day).ThenBy(h => h.Start).ThenBy(h => h.Id).Select(h => new
            {
                id = h.Id, day = h.Day, start = Timeslots.Format(h.Start), end = Timeslots.Format(h.End), room = h.Room,
                excludedDates = DaysOff.Skipped(daysOff, h.Day).ToList(),
            }).ToList();
            // One real class under several codes: how many Classy users it has in all, each counted once.
            timetable["mergeSizes"] = await TeacherSchedule.MergeSizesAsync(db, on, changes, ct);
            // Sections they added that the university has since removed.
            timetable["removed"] = removed;
            return Results.Ok(timetable);
        });

        // Opening a class clears its "Changed" badge. Nothing happens for an owner looking in: the badge is the teacher's.
        teach.MapPost("/seen", async (SeenBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (actor.Profile is not { } p || actor.ViewAs || body.SectionId is not int sectionId) return Results.NoContent();
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is null || !(await TeacherSchedule.SectionsAsync(db, current.Id, actor, ct)).Any(t => t.Section.Id == sectionId)) return Results.NoContent();
            var now = clock.GetUtcNow();
            if (await db.TeacherSeens.Where(x => x.TeacherId == p.Id && x.SectionId == sectionId).ExecuteUpdateAsync(u => u.SetProperty(x => x.SeenAt, now), ct) == 0)
            {
                db.TeacherSeens.Add(new TeacherSeen { TeacherId = p.Id, SectionId = sectionId, SeenAt = now });
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException)
                {
                    // Sent twice at once: the other request made the row.
                }
            }
            return Results.NoContent();
        });

        // ---- Edit my classes ----

        // My sections (hidden ones too), last semester's added ones to tick again, and, with q, a search over every
        // course by code, name or teacher.
        teach.MapGet("/sections", async (string? q, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            var p = actor.Profile;
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is null) return Results.Ok(new { semester = (object?)null, mine = Array.Empty<object>(), previous = Array.Empty<object>(), courses = Array.Empty<object>() });
            var taught = await TeacherSchedule.SectionsAsync(db, current.Id, actor, ct);
            var mine = taught.ToDictionary(t => t.Section.Id);

            // A new semester starts with no added sections: the latest earlier semester's are offered again, by their text.
            List<object> previous = [];
            if (p is not null)
            {
                var earlier = await db.TeacherSections.Where(r => r.TeacherId == p.Id && r.SemesterId != current.Id && r.Mode == TeacherSection.AddedMode)
                    .OrderByDescending(r => r.SemesterId).Select(r => (int?)r.SemesterId).FirstOrDefaultAsync(ct);
                if (earlier is int last)
                {
                    var wanted = await db.TeacherSections.Where(r => r.TeacherId == p.Id && r.SemesterId == last && r.Mode == TeacherSection.AddedMode)
                        .Select(r => new { r.CourseCode, r.SectionNumber }).ToListAsync(ct);
                    var codes = wanted.Select(r => r.CourseCode).Distinct().ToList();
                    var now = await db.Sections.Where(x => x.Course.SemesterId == current.Id && codes.Contains(x.Course.Code))
                        .Select(x => new { x.Id, x.Course.Code, x.Number }).ToListAsync(ct);
                    previous = now.Where(x => !mine.ContainsKey(x.Id) && wanted.Any(r => r.CourseCode == x.Code && r.SectionNumber == x.Number))
                        .OrderBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Number.PadLeft(4, '0'), StringComparer.Ordinal)
                        .Select(x => (object)new { id = x.Id, code = x.Code, number = x.Number }).ToList();
                }
            }

            List<object> courses = [];
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                var compact = term.Replace(" ", "");
                var found = await db.Courses.Where(c => c.SemesterId == current.Id)
                    .Where(c => c.Code.ToLower().Contains(compact) || c.Name.ToLower().Contains(term) || c.Sections.Any(x => x.Instructor.ToLower().Contains(term)))
                    .OrderBy(c => c.Code).Take(MaxSearchCourses)
                    .Include(c => c.Sections).ThenInclude(x => x.Meetings).AsSplitQuery().ToListAsync(ct);
                courses = found.Select(c => (object)new
                {
                    id = c.Id, code = c.Code, name = c.Name,
                    sections = c.Sections.OrderBy(PaddedNumber, StringComparer.Ordinal).Select(x => Row(c, x, StateOf(mine.GetValueOrDefault(x.Id)))),
                }).ToList();
            }
            return Results.Ok(new
            {
                semester = new { id = current.Id, name = current.Name },
                mine = taught.Select(t => Row(t.Course, t.Section, StateOf(t))),
                previous,
                courses,
            });
        });

        // Ticks a section on or off. Under my name: off hides it for me, on brings it back. Any other section: on adds
        // it (it shows on my timetable, and I show on the class, at once), off removes it.
        teach.MapPut("/sections/{sectionId:int}", async (int sectionId, SectionBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (actor.Profile is not { } p) return Errors.Conflict(TeachActor.NoAccount);
            if (body.On is not bool on) return Errors.BadRequest("Say whether the class is on or off.");
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is null) return Errors.Conflict("No semester has been published yet.");
            var section = await db.Sections.Include(x => x.Course).Include(x => x.Meetings)
                .FirstOrDefaultAsync(x => x.Id == sectionId && x.Course.SemesterId == current.Id, ct);
            if (section is null) return Errors.NotFound("That class isn't on this semester's timetable.");
            var own = TeacherNames.NameKey(section.Instructor) is { Length: > 0 } key && actor.NameKeys.Contains(key);
            // One row per section and teacher, found by its text: a section the university removed and brought back keeps its row.
            var row = await db.TeacherSections.FirstOrDefaultAsync(r => r.TeacherId == p.Id && r.SemesterId == current.Id
                && r.CourseCode == section.Course.Code && r.SectionNumber == section.Number, ct);
            var rowId = row?.Id ?? 0;
            var mode = own ? (on ? null : TeacherSection.HiddenMode) : (on ? TeacherSection.AddedMode : null);
            var now = clock.GetUtcNow();
            var changed = false;
            if (mode is null)
            {
                // Under my name an added row means nothing: only a hidden one is taken away, and the other way round.
                if (row is not null && (row.SectionId != section.Id || row.Mode == (own ? TeacherSection.HiddenMode : TeacherSection.AddedMode)))
                {
                    changed = row.SectionId == section.Id;
                    db.TeacherSections.Remove(row);
                }
            }
            else if (row is null || row.Mode != mode || row.SectionId != section.Id)
            {
                if (mode == TeacherSection.AddedMode)
                {
                    // A frozen account can't put itself on another class in front of students. Hiding and removing still work.
                    if (p.FrozenAt is not null) return Errors.Forbidden(TeachActor.Frozen);
                    if (await db.TeacherSections.CountAsync(r => r.TeacherId == p.Id && r.SemesterId == current.Id && r.Mode == TeacherSection.AddedMode && r.Id != rowId, ct) >= MaxAddedSections)
                        return Errors.BadRequest($"You can add up to {MaxAddedSections} classes.");
                }
                if (row is null) db.TeacherSections.Add(row = new TeacherSection { TeacherId = p.Id, SemesterId = current.Id, CourseCode = section.Course.Code, SectionNumber = section.Number });
                (row.SectionId, row.Mode, row.CreatedAt) = (section.Id, mode, now);
                // Done through "view as": kept under the owner who did it.
                row.By = actor.ViewAs ? actor.ByLabel : null;
                changed = true;
            }
            if (changed && actor.ViewAs)
            {
                var what = $"{section.Course.Code} section {section.Number}";
                db.TeachNotices.Add(StaffEdit(actor, mode == TeacherSection.AddedMode ? $"Classy staff added {what} to your classes"
                    : mode == TeacherSection.HiddenMode ? $"Classy staff hid {what} on your timetable"
                    : own ? $"Classy staff put {what} back on your timetable"
                    : $"Classy staff removed {what} from your classes", "/teach/classes", now));
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // The same tick sent twice at once.
                return Errors.Conflict("That didn't save. Try again.");
            }
            var state = own ? (on ? "own" : "hidden") : (on ? "added" : null);
            return Results.Ok(Row(section.Course, section, state));
        });

        // ---- Own slots (current semester): the teacher's own, never an owner's who is looking in ----

        teach.MapGet("/slots", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is null) return Results.Ok(Array.Empty<object>());
            var me = actor.Profile!.Id;
            var slots = await db.TeacherSlots.Where(s => s.TeacherId == me && s.SemesterId == current.Id).ToListAsync(ct);
            return Results.Ok(slots.OrderBy(s => s.Date).ThenBy(s => s.Day).ThenBy(s => s.Start).Select(Of));
        });

        teach.MapPost("/slots", async (PersonalEndpoints.SlotBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            var current = await StudentEndpoints.CurrentSemester(db, ct);
            if (current is null) return Errors.Conflict("No semester has been published yet.");
            var me = actor.Profile!.Id;
            if (await db.TeacherSlots.CountAsync(s => s.TeacherId == me && s.SemesterId == current.Id, ct) >= PersonalEndpoints.MaxSlots)
                return Errors.BadRequest($"You can have up to {PersonalEndpoints.MaxSlots} slots.");
            var slot = new TeacherSlot { TeacherId = me, SemesterId = current.Id, CreatedAt = clock.GetUtcNow() };
            if (Fill(slot, body) is string problem) return Errors.BadRequest(problem);
            db.TeacherSlots.Add(slot);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Of(slot));
        });

        // Another account's slot is a 404, like one that doesn't exist.
        teach.MapPut("/slots/{slotId:int}", async (int slotId, PersonalEndpoints.SlotBody body, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            var me = actor.Profile!.Id;
            var slot = await db.TeacherSlots.FirstOrDefaultAsync(s => s.Id == slotId && s.TeacherId == me, ct);
            if (slot is null) return Errors.NotFound("That slot doesn't exist.");
            if (Fill(slot, body) is string problem) return Errors.BadRequest(problem);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Of(slot));
        });

        teach.MapDelete("/slots/{slotId:int}", async (int slotId, HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (!actor.Personal) return Errors.Conflict(TeachActor.NotInViewAs);
            var me = actor.Profile!.Id;
            var removed = await db.TeacherSlots.Where(s => s.Id == slotId && s.TeacherId == me).ExecuteDeleteAsync(ct);
            return removed == 0 ? Errors.NotFound("That slot doesn't exist.") : Results.NoContent();
        });

        // ---- Office hours and the details students see ----

        teach.MapGet("/office", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (actor.Profile is not { } p) return Errors.Conflict(TeachActor.NoAccount);
            return Results.Ok(await OfficeOf(db, p, actor.OnBehalfOf ?? actor.ByName, ct));
        });

        // Replaces the office hours and the details. An owner looking in may edit them (the teacher is told), but can
        // never make a teacher's email show.
        teach.MapPut("/office", async (OfficeBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var actor = http.Teach();
            if (actor.Profile is not { } p) return Errors.Conflict(TeachActor.NoAccount);
            // Office hours and details show to students, so a frozen account can't change them.
            if (p.FrozenAt is not null) return Errors.Forbidden(TeachActor.Frozen);
            if (actor.ViewAs && body.ShowEmail == true && !p.ShowEmail) return Errors.Conflict("Only the teacher can show their email.");
            var officeRoom = Plain(body.OfficeRoom);
            if (officeRoom.Length > 60) return Errors.BadRequest("Keep the office room under 60 characters.");
            var note = Plain(body.Note);
            if (note.Length > 200) return Errors.BadRequest("Keep the note under 200 characters.");
            var asked = body.Hours ?? [];
            if (asked.Count > MaxOfficeHours) return Errors.BadRequest($"You can have up to {MaxOfficeHours} office hours.");
            var hours = new List<OfficeHour>();
            foreach (var h in asked)
            {
                if (h?.Day is not int day || day is < 0 or > 6) return Errors.BadRequest("Pick a day for each office hour.");
                if (!TryTime(h.Start, out var start) || !TryTime(h.End, out var end)) return Errors.BadRequest("Enter times like 08:00.");
                if (end <= start) return Errors.BadRequest("An office hour ends before it starts.");
                var room = Plain(h.Room);
                if (room.Length > 60) return Errors.BadRequest("Keep the room under 60 characters.");
                hours.Add(new OfficeHour { TeacherId = p.Id, Day = day, Start = start, End = end, Room = room });
            }

            (p.OfficeRoom, p.ProfileNote) = (officeRoom, note);
            // Through "view as" the email can only be turned off.
            if (body.ShowEmail is bool show && !(actor.ViewAs && show)) p.ShowEmail = show;
            db.OfficeHours.RemoveRange(await db.OfficeHours.Where(h => h.TeacherId == p.Id).ToListAsync(ct));
            db.OfficeHours.AddRange(hours.OrderBy(h => h.Day).ThenBy(h => h.Start));
            if (actor.ViewAs) db.TeachNotices.Add(StaffEdit(actor, "Classy staff changed your office hours", "/teach/office", clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return Results.Ok(await OfficeOf(db, p, actor.OnBehalfOf ?? actor.ByName, ct));
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

        teach.MapClassChanges();
    }

    /// <summary>Picking a name is for staff accounts: owners attach names to a teaching assistant in the dashboard.</summary>
    static IResult? NameStepRefusal(TeachActor actor) =>
        !actor.Personal ? Errors.Conflict(TeachActor.NotInViewAs)
        : actor.ByKind != "teacher" ? Errors.Conflict("Ask the Classy team to link your name to your classes.")
        : null;
}
