using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Admin;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;
using UniSchedule.Api.Schedule;
using UniSchedule.Api.Students;

namespace UniSchedule.Api.Planner;

public static class PlannerEndpoints
{
    public record RepBody(int HomeMajorId);
    public record OpenBody(int SemesterId, string? Family);
    public record BatchBody(string Name, PlannerTarget[] Targets, int? MajorId, int? Year, int Version = 0, bool Prefill = false);
    public record ProposalBody(EventSpec Spec, int? EventId, int BaseEventVersion, int Version = 0);
    public record PublishBody(int Version, string AnalysisToken, string? Explanation);
    public record SettingsBody(int Version, int DailyExamWarning, int WeeklyExamWarning);
    public record EnabledBody(bool Enabled);
    public record WithdrawBody(int Version, string Explanation);
    public record SearchBody(PlannerTarget[] Targets, string Kind, string From, string To, int Duration,
        int[] Weekdays, string Start = "08:00", string End = "18:15", string Room = "", string? RepeatUntil = null);

    public static async Task<object?> RepInfo(AppDb db, int studentId, CancellationToken ct) =>
        await db.BatchRepAssignments.Where(r => r.StudentId == studentId).Select(r => new { r.HomeFamily, r.AssignedAt }).FirstOrDefaultAsync(ct);

    static object Proposal(PlannerProposal p) => new { p.Id, p.EventId, p.BaseEventVersion, p.Version, p.Status, p.UpdatedBy, p.UpdatedAt, spec = PlannerJson.Read<EventSpec>(p.PayloadJson) };
    static object Batch(PlannerBatch b) => new { b.Id, b.Name, b.MajorId, b.Year, b.Version, targets = PlannerJson.Read<PlannerTarget[]>(b.TargetsJson) };

    public static async Task<PlannerWorkspace?> Workspace(HttpContext http, AppDb db, int id, CancellationToken ct)
    {
        var w = await db.PlannerWorkspaces.FindAsync([id], ct);
        if (w is null || http.User.IsOwner()) return w;
        var student = http.User.Id();
        return await db.BatchRepAssignments.AnyAsync(r => r.StudentId == student && r.HomeFamily == w.Family, ct) ? w : null;
    }

    /// <summary>
    /// Whether this workspace may change a published event: the one it was first published from. Owners may change any.
    /// Reps can still target other majors' courses, but can't edit or withdraw another family's events.
    /// </summary>
    static async Task<bool> OwnsEvent(HttpContext http, AppDb db, PlannerWorkspace w, int eventId, CancellationToken ct) =>
        http.User.IsOwner() || await db.PlannerActivities.Where(a => a.EventId == eventId && a.Action == "published")
            .OrderBy(a => a.Id).Select(a => (int?)a.WorkspaceId).FirstOrDefaultAsync(ct) == w.Id;

    static async Task Activity(AppDb db, HttpContext http, PlannerWorkspace w, string action, string snapshot, TimeProvider clock, CancellationToken ct, int? eventId = null, int? proposalId = null, string explanation = "")
    {
        db.PlannerActivities.Add(new() { WorkspaceId = w.Id, Action = action, Actor = await OwnerEndpoints.ActorName(http, db, ct), SnapshotJson = snapshot, At = clock.GetUtcNow(), EventId = eventId, ProposalId = proposalId, Explanation = explanation });
    }

    public static void MapPlanner(this RouteGroupBuilder api)
    {
        var owner = api.MapGroup("/admin/planner").RequireAuthorization(Session.OwnerPolicy);
        owner.MapGet("/settings", async (AppDb db, IConfiguration config, CancellationToken ct) => Results.Ok(new { enabled = await PlannerEngine.Enabled(db, config, ct) }));
        owner.MapPut("/settings", async (EnabledBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) => {
            var setting = await db.AppSettings.FindAsync(["planner.enabled"], ct);
            if (setting is null) { setting = new() { Key = "planner.enabled" }; db.AppSettings.Add(setting); }
            setting.Value = body.Enabled ? "true" : "false";
            setting.UpdatedAt = clock.GetUtcNow(); setting.UpdatedBy = await OwnerEndpoints.ActorName(http, db, ct);
            await db.SaveChangesAsync(ct); return Results.Ok(new { body.Enabled });
        });
        api.MapGet("/admin/students/{id:int}/batch-rep", async (int id, AppDb db, CancellationToken ct) => Results.Ok(new { assignment = await RepInfo(db, id, ct) })).RequireAuthorization(Session.OwnerPolicy);
        api.MapPut("/admin/students/{id:int}/batch-rep", async (int id, RepBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) => {
            if (!await db.Students.AnyAsync(s => s.Id == id, ct)) return Errors.NotFound("That student doesn't exist.");
            var major = await db.Majors.FindAsync([body.HomeMajorId], ct);
            if (major is null) return Errors.BadRequest("Choose the rep's home major.");
            var r = await db.BatchRepAssignments.FindAsync([id], ct);
            if (r is null) { r = new() { StudentId = id }; db.BatchRepAssignments.Add(r); }
            r.HomeFamily = PlannerJson.Family(major); r.AssignedAt = clock.GetUtcNow(); r.AssignedBy = await OwnerEndpoints.ActorName(http, db, ct);
            await db.SaveChangesAsync(ct); return Results.Ok(new { r.HomeFamily });
        }).RequireAuthorization(Session.OwnerPolicy);
        api.MapDelete("/admin/students/{id:int}/batch-rep", async (int id, AppDb db, CancellationToken ct) => {
            await db.BatchRepAssignments.Where(r => r.StudentId == id).ExecuteDeleteAsync(ct); return Results.NoContent();
        }).RequireAuthorization(Session.OwnerPolicy);

        var rep = api.MapGroup("/rep").RequireAuthorization(Session.RepPolicy);
        rep.AddEndpointFilter(async (context, next) => {
            var http = context.HttpContext;
            var db = http.RequestServices.GetRequiredService<AppDb>();
            if (!http.User.IsOwner() && !await PlannerEngine.Enabled(db, http.RequestServices.GetRequiredService<IConfiguration>(), http.RequestAborted)) return Errors.Conflict("The semester planner isn't enabled yet.");
            try { return await next(context); }
            catch (DbUpdateConcurrencyException) { return Errors.Conflict("Another rep changed this item. Refresh it before saving."); }
        });
        rep.MapGet("/bootstrap", async (int? semesterId, HttpContext http, AppDb db, IConfiguration config, CancellationToken ct) => {
            var semesters = await db.Semesters.OrderByDescending(s => s.Year).ThenBy(s => s.Name).ToListAsync(ct);
            var semester = semesters.FirstOrDefault(s => s.Id == semesterId) ?? semesters.FirstOrDefault(s => s.IsCurrent) ?? semesters.FirstOrDefault();
            var majors = await db.Majors.OrderBy(m => m.Code).ToListAsync(ct);
            var assignment = http.User.IsStudent() ? await RepInfo(db, http.User.Id(), ct) : null;
            var courses = semester is null ? [] : await db.Courses.Include(c => c.Majors).ThenInclude(m => m.Major).Include(c => c.Sections).ThenInclude(s => s.Meetings).Where(c => c.SemesterId == semester.Id).AsSplitQuery().ToListAsync(ct);
            return Results.Ok(new { enabled = await PlannerEngine.Enabled(db, config, ct), isOwner = http.User.IsOwner(), assignment,
                semesters = semesters.Select(Dto.Of), semesterId = semester?.Id,
                majors = majors.Select(m => new { m.Id, m.Code, m.Name, family = PlannerJson.Family(m) }),
                courses = courses.Select(Dto.Of), weeks = semester is null ? null : await SemesterWeeks.ForAsync(db, semester, ct) });
        });
        rep.MapPost("/workspaces/open", async (OpenBody body, HttpContext http, AppDb db, CancellationToken ct) => {
            if (!await db.Semesters.AnyAsync(s => s.Id == body.SemesterId, ct)) return Errors.BadRequest("Choose a semester.");
            var assignment = http.User.IsStudent() ? await db.BatchRepAssignments.FindAsync([http.User.Id()], ct) : null;
            var family = http.User.IsOwner() ? body.Family ?? assignment?.HomeFamily : assignment?.HomeFamily;
            var majors = await db.Majors.ToListAsync(ct);
            if (family is null || !majors.Any(m => PlannerJson.Family(m) == family)) return Errors.BadRequest("Choose a home family or major.");
            var w = await db.PlannerWorkspaces.FirstOrDefaultAsync(w => w.SemesterId == body.SemesterId && w.Family == family, ct);
            if (w is null) {
                w = new() { SemesterId = body.SemesterId, Family = family }; db.PlannerWorkspaces.Add(w);
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { db.ChangeTracker.Clear(); w = await db.PlannerWorkspaces.FirstAsync(w => w.SemesterId == body.SemesterId && w.Family == family, ct); }
            }
            return Results.Ok(w);
        });
        var work = rep.MapGroup("/workspaces/{workspaceId:int}");
        work.MapGet("", async (int workspaceId, HttpContext http, AppDb db, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound("That workspace isn't available.");
            var batches = await db.PlannerBatches.Where(b => b.WorkspaceId == w.Id).OrderBy(b => b.Name).ToListAsync(ct);
            var drafts = await db.PlannerProposals.Where(p => p.WorkspaceId == w.Id && p.Status == "draft").OrderByDescending(p => p.UpdatedAt).ToListAsync(ct);
            var history = await db.PlannerActivities.Where(a => a.WorkspaceId == w.Id).OrderByDescending(a => a.At).Take(100).ToListAsync(ct);
            return Results.Ok(new { workspace = w, batches = batches.Select(Batch), proposals = drafts.Select(Proposal), history });
        });
        work.MapPut("/settings", async (int workspaceId, SettingsBody body, HttpContext http, AppDb db, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            if (body.Version != w.Version) return Errors.Conflict("Workspace settings changed. Refresh first.");
            if (body.DailyExamWarning is < 1 or > 10 || body.WeeklyExamWarning is < 1 or > 20) return Errors.BadRequest("Choose warning thresholds from 1–10 daily and 1–20 weekly.");
            w.DailyExamWarning = body.DailyExamWarning; w.WeeklyExamWarning = body.WeeklyExamWarning; w.Version++;
            await db.SaveChangesAsync(ct); return Results.Ok(w);
        });
        work.MapGet("/calendar", async (int workspaceId, string from, string to, HttpContext http, AppDb db, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            if (!PlannerEngine.Date(from, out var a) || !PlannerEngine.Date(to, out var z) || z < a || z.DayNumber - a.DayNumber > 150) return Errors.BadRequest("Choose a calendar range of up to 150 days.");
            return Results.Ok(await PlannerEngine.Calendar(db, w.SemesterId, a, z, w.Id, ct));
        });
        work.MapPost("/batches", async (int workspaceId, BatchBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 80) return Errors.BadRequest("Name this batch (up to 80 characters).");
            var targets = body.Targets;
            if (body.Prefill && body.MajorId is int mid && body.Year is int year) {
                if (year is < 0 or > 5 || !await db.Majors.AnyAsync(m => m.Id == mid, ct)) return Errors.BadRequest("Choose a major and study year.");
                targets = (await StudentEndpoints.DefaultCourseIds(db, w.SemesterId, mid, year, ct)).Select(id => new PlannerTarget(id, [])).ToArray();
            }
            if (targets is null || targets.Length > 100) return Errors.BadRequest("Choose up to 100 courses.");
            if (targets.Length > 0 && await PlannerEngine.ValidateTargets(db, w.SemesterId, targets, ct) is { } p) return Errors.BadRequest(p);
            var b = new PlannerBatch { WorkspaceId = w.Id, Name = body.Name.Trim(), MajorId = body.MajorId, Year = body.Year, TargetsJson = PlannerJson.Write(targets) };
            db.PlannerBatches.Add(b); await Activity(db, http, w, "batch-created", b.TargetsJson, clock, ct);
            await db.SaveChangesAsync(ct); return Results.Ok(Batch(b));
        });
        work.MapPut("/batches/{id:int}", async (int workspaceId, int id, BatchBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            var b = await db.PlannerBatches.FirstOrDefaultAsync(b => b.Id == id && b.WorkspaceId == w.Id, ct); if (b is null) return Errors.NotFound();
            if (b.Version != body.Version) return Errors.Conflict("Another rep changed this batch. Refresh first.");
            if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Length > 80 || body.Targets is null || body.Targets.Length > 100) return Errors.BadRequest("Name the batch and choose up to 100 courses.");
            if (body.Targets.Length > 0 && await PlannerEngine.ValidateTargets(db, w.SemesterId, body.Targets, ct) is { } p) return Errors.BadRequest(p);
            b.Name = body.Name.Trim(); b.TargetsJson = PlannerJson.Write(body.Targets); b.MajorId = body.MajorId; b.Year = body.Year; b.Version++;
            await Activity(db, http, w, "batch-updated", b.TargetsJson, clock, ct); await db.SaveChangesAsync(ct); return Results.Ok(Batch(b));
        });
        work.MapDelete("/batches/{id:int}", async (int workspaceId, int id, int version, HttpContext http, AppDb db, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            var b = await db.PlannerBatches.FirstOrDefaultAsync(b => b.Id == id && b.WorkspaceId == w.Id, ct); if (b is null) return Errors.NotFound();
            if (b.Version != version) return Errors.Conflict("This batch changed. Refresh first.");
            db.PlannerBatches.Remove(b); await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        work.MapPost("/proposals", async (int workspaceId, ProposalBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            if (body.Spec is null) return Errors.BadRequest("Enter event details.");
            if (await PlannerEngine.Validate(db, w.SemesterId, body.Spec, ct) is { } problem) return Errors.BadRequest(problem);
            var published = body.EventId is null ? null : await db.PlannerEvents.FirstOrDefaultAsync(e => e.Id == body.EventId && e.SemesterId == w.SemesterId, ct);
            if (body.EventId is not null && (published is null || published.Version != body.BaseEventVersion)) return Errors.Conflict("The published event changed. Refresh it first.");
            if (published is not null && !await OwnsEvent(http, db, w, published.Id, ct)) return Errors.Forbidden("Another family published this event. Ask their rep to change it.");
            var p = new PlannerProposal { WorkspaceId = w.Id, EventId = body.EventId, BaseEventVersion = published?.Version ?? 0, PayloadJson = PlannerJson.Write(body.Spec), UpdatedBy = await OwnerEndpoints.ActorName(http, db, ct), UpdatedAt = clock.GetUtcNow() };
            db.PlannerProposals.Add(p); await db.SaveChangesAsync(ct); return Results.Ok(Proposal(p));
        });
        work.MapPut("/proposals/{id:int}", async (int workspaceId, int id, ProposalBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            var p = await db.PlannerProposals.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == w.Id && p.Status == "draft", ct); if (p is null) return Errors.NotFound();
            if (body.Version != p.Version) return Errors.Conflict("Another rep changed this draft. Refresh it first.");
            if (body.Spec is null || await PlannerEngine.Validate(db, w.SemesterId, body.Spec, ct) is { } problem) return Errors.BadRequest("Review the date, times, targets and original class before saving.");
            p.PayloadJson = PlannerJson.Write(body.Spec); p.UpdatedAt = clock.GetUtcNow(); p.UpdatedBy = await OwnerEndpoints.ActorName(http, db, ct); p.Version++;
            await db.SaveChangesAsync(ct); return Results.Ok(Proposal(p));
        });
        work.MapDelete("/proposals/{id:int}", async (int workspaceId, int id, int version, HttpContext http, AppDb db, CancellationToken ct) => {
            if (await Workspace(http, db, workspaceId, ct) is null) return Errors.NotFound();
            var p = await db.PlannerProposals.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId && p.Status == "draft", ct); if (p is null) return Errors.NotFound();
            if (p.Version != version) return Errors.Conflict("This draft changed. Refresh first.");
            p.Status = "discarded"; p.Version++; await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        work.MapPost("/proposals/{id:int}/analyze", async (int workspaceId, int id, HttpContext http, AppDb db, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            var p = await db.PlannerProposals.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == w.Id && p.Status == "draft", ct); if (p is null) return Errors.NotFound();
            return Results.Ok(await PlannerEngine.Analyze(db, w, PlannerJson.Read<EventSpec>(p.PayloadJson), ct, p.Id, p.EventId));
        });
        work.MapPost("/proposals/{id:int}/publish", async (int workspaceId, int id, PublishBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) => {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            var p = await db.PlannerProposals.FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == w.Id && p.Status == "draft", ct); if (p is null) return Errors.NotFound();
            if (body.Version != p.Version) return Errors.Conflict("This draft changed. Review it again.");
            var spec = PlannerJson.Read<EventSpec>(p.PayloadJson);
            if (await PlannerEngine.Validate(db, w.SemesterId, spec, ct) is { } problem) return Errors.Conflict(problem);
            var analysis = await PlannerEngine.Analyze(db, w, spec, ct, p.Id, p.EventId);
            if (analysis.Token != body.AnalysisToken) return Errors.Conflict("The schedule changed. Run the conflict check again.");
            if ((analysis.Conflicts.Count > 0 || analysis.CoverageWarnings.Count > 0) && string.IsNullOrWhiteSpace(body.Explanation)) return Errors.BadRequest("Explain why you're publishing despite these warnings.");
            if (body.Explanation?.Length > 1000) return Errors.BadRequest("Keep the explanation under 1,000 characters.");
            var e = p.EventId is int eid ? await db.PlannerEvents.FindAsync([eid], ct) : null;
            if (p.EventId is not null && (e is null || e.Version != p.BaseEventVersion)) return Errors.Conflict("Another rep published a change to this event. Refresh first.");
            if (e is not null && !await OwnsEvent(http, db, w, e.Id, ct)) return Errors.Forbidden("Another family published this event. Ask their rep to change it.");
            if (spec.Source is { } source) {
                var sourceDates = PlannerEngine.Dates(spec).Select(d => DateOnly.Parse(source.Date).AddDays(d.DayNumber - DateOnly.Parse(spec.Date).DayNumber)).ToHashSet();
                var active = await db.PlannerEvents.Where(x => x.SemesterId == w.SemesterId && !x.Withdrawn && x.Id != p.EventId).ToListAsync(ct);
                foreach (var existing in active.Select(x => PlannerJson.Read<EventSpec>(x.PayloadJson)))
                    if (existing.Source is { } other && other.Fingerprint == source.Fingerprint
                        && PlannerEngine.Dates(existing).Any(d => sourceDates.Contains(DateOnly.Parse(other.Date).AddDays(d.DayNumber - DateOnly.Parse(existing.Date).DayNumber))))
                        return Errors.Conflict("This university occurrence already has a published adjustment. Edit or withdraw that adjustment instead.");
            }
            if (e is null) { e = new() { SemesterId = w.SemesterId }; db.PlannerEvents.Add(e); }
            else e.Version++;
            e.PayloadJson = p.PayloadJson; e.Withdrawn = false; e.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            p.EventId = e.Id; p.Status = "published"; p.Version++;
            await Activity(db, http, w, "published", p.PayloadJson, clock, ct, e.Id, p.Id, body.Explanation?.Trim() ?? "");
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(new { e.Id, e.Version });
        });
        work.MapGet("/events/{id:int}", async (int workspaceId, int id, HttpContext http, AppDb db, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            var e = await db.PlannerEvents.FirstOrDefaultAsync(e => e.Id == id && e.SemesterId == w.SemesterId, ct);
            return e is null ? Errors.NotFound() : Results.Ok(new { e.Id, e.Version, e.Withdrawn, spec = PlannerJson.Read<EventSpec>(e.PayloadJson) });
        });
        work.MapPost("/events/{id:int}/withdraw", async (int workspaceId, int id, WithdrawBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            var e = await db.PlannerEvents.FirstOrDefaultAsync(e => e.Id == id && e.SemesterId == w.SemesterId, ct); if (e is null) return Errors.NotFound();
            if (!await OwnsEvent(http, db, w, e.Id, ct)) return Errors.Forbidden("Another family published this event. Ask their rep to withdraw it.");
            if (e.Version != body.Version) return Errors.Conflict("This event changed. Refresh first.");
            if (string.IsNullOrWhiteSpace(body.Explanation) || body.Explanation.Length > 1000) return Errors.BadRequest("Explain the withdrawal (up to 1,000 characters).");
            e.Withdrawn = true; e.Version++; e.UpdatedAt = clock.GetUtcNow();
            await Activity(db, http, w, "withdrawn", e.PayloadJson, clock, ct, e.Id, explanation: body.Explanation);
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        work.MapPost("/slots", Search);
        work.MapPost("/ai-jobs", async (int workspaceId, PlannerAiInput body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) => {
            var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
            if (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > 20000 || body.Images is null || body.Images.Length > 3 || body.Images.Any(s => s is null) || body.Images.Sum(s => s.Length) > 8 * 1024 * 1024 + 200 || body.Images.Any(s => !System.Text.RegularExpressions.Regex.IsMatch(s, @"^data:image/(png|jpeg);base64,[A-Za-z0-9+/]+={0,2}$"))) return Errors.BadRequest("Enter a message (up to 20,000 characters) and up to three PNG/JPEG images, under 6 MB combined.");
            var sid = http.User.Id(); var ownerAccount = !http.User.IsStudent();
            if (await db.PlannerAiJobs.CountAsync(j => j.StudentId == sid && j.OwnerAccount == ownerAccount && (j.Status == "queued" || j.Status == "running"), ct) >= 2) return Results.Json(new { error = "Wait for your current requests to finish." }, statusCode: 429);
            var job = new PlannerAiJob { WorkspaceId = w.Id, StudentId = sid, OwnerAccount = ownerAccount, InputJson = PlannerJson.Write(body), CreatedAt = clock.GetUtcNow() };
            db.PlannerAiJobs.Add(job); await db.SaveChangesAsync(ct); return Results.Accepted($"/api/rep/workspaces/{w.Id}/ai-jobs/{job.Id}", new { job.Id, job.Status });
        });
        work.MapGet("/ai-jobs/{id}", async (int workspaceId, string id, HttpContext http, AppDb db, CancellationToken ct) => {
            if (await Workspace(http, db, workspaceId, ct) is null) return Errors.NotFound();
            var job = await db.PlannerAiJobs.FirstOrDefaultAsync(j => j.Id == id && j.WorkspaceId == workspaceId && j.StudentId == http.User.Id() && j.OwnerAccount == !http.User.IsStudent(), ct);
            return job is null ? Errors.NotFound() : Results.Ok(new { job.Id, job.Status, job.Error, result = System.Text.Json.JsonDocument.Parse(job.ResultJson).RootElement });
        });
        work.MapDelete("/ai-jobs/{id}", async (int workspaceId, string id, HttpContext http, AppDb db, CancellationToken ct) => {
            if (await Workspace(http, db, workspaceId, ct) is null) return Errors.NotFound();
            await db.PlannerAiJobs.Where(j => j.Id == id && j.WorkspaceId == workspaceId && j.StudentId == http.User.Id() && j.OwnerAccount == !http.User.IsStudent()).ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, "cancelled").SetProperty(j => j.InputJson, "{}"), ct);
            return Results.NoContent();
        });
    }

    static async Task<IResult> Search(int workspaceId, SearchBody body, HttpContext http, AppDb db, CancellationToken ct)
    {
        var w = await Workspace(http, db, workspaceId, ct); if (w is null) return Errors.NotFound();
        if (!PlannerEngine.Date(body.From, out var from) || !PlannerEngine.Date(body.To, out var to) || to < from || to.DayNumber - from.DayNumber > 90 || body.Duration is < 15 or > 600 || body.Kind is not ("exam" or "class") || body.Weekdays is null || body.Weekdays.Length == 0 || body.Weekdays.Any(d => d is < 0 or > 6) || !PlannerEngine.Time(body.Start, out var start) || !PlannerEngine.Time(body.End, out var end) || end <= start || body.Room is null || body.Room.Length > 100) return Errors.BadRequest("Choose a search range of up to 90 days, weekdays, hours and a duration of 15–600 minutes.");
        if (await PlannerEngine.ValidateTargets(db, w.SemesterId, body.Targets, ct) is { } problem) return Errors.BadRequest(problem);
        if (body.RepeatUntil is not null && (body.Kind != "class" || !PlannerEngine.Date(body.RepeatUntil, out var repeat) || repeat < to || repeat.DayNumber - from.DayNumber > 190)) return Errors.BadRequest("Recurring class searches must end after the search range and within 190 days.");
        var results = new List<object>();
        var ranked = new List<(EventSpec Spec, PlannerAnalysis Analysis, int Score)>();
        // Load once; evaluate candidate overlaps in memory, then run the complete analysis for the top results.
        var until = PlannerEngine.Date(body.RepeatUntil, out var recurringEnd) ? recurringEnd : to;
        var entries = await PlannerEngine.Calendar(db, w.SemesterId, from.AddDays(-6), until.AddDays(6), w.Id, ct);
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (!body.Weekdays.Contains((int)d.DayOfWeek)) continue;
            for (var minute = start.Hour * 60 + start.Minute; minute + body.Duration <= end.Hour * 60 + end.Minute; minute += 15)
            {
                string Clock(int m) => $"{m / 60:00}:{m % 60:00}";
                var spec = new EventSpec(body.Kind, "add", body.Kind == "exam" ? "Exam" : "Extra class", PlannerEngine.Iso(d), Clock(minute), Clock(minute + body.Duration), body.Room, body.Targets, body.RepeatUntil);
                var dates = PlannerEngine.Dates(spec).Select(PlannerEngine.Iso).ToHashSet();
                var overlaps = entries.Count(e => !e.Stale && e.Status != "cancelled-draft" && dates.Contains(e.Date) && string.CompareOrdinal(e.Start, spec.End) < 0 && string.CompareOrdinal(spec.Start, e.End) < 0 && (PlannerEngine.SharedTargets(body.Targets, e.Targets) || (body.Room.Length > 0 && e.Room.Equals(body.Room, StringComparison.OrdinalIgnoreCase))));
                var exams = entries.Where(e => e.Kind == "exam" && PlannerEngine.SharedTargets(body.Targets, e.Targets)).ToList();
                var nearby = exams.Where(e => Math.Abs(DateOnly.Parse(e.Date).DayNumber - d.DayNumber) <= 6).Select(PlannerEngine.ExamIdentity).Distinct().Count();
                var sameDay = exams.Where(e => e.Date == spec.Date).Select(PlannerEngine.ExamIdentity).Distinct().Count();
                var gap = exams.Count == 0 ? 14 : Math.Min(14, exams.Min(e => Math.Abs(DateOnly.Parse(e.Date).DayNumber - d.DayNumber)));
                ranked.Add((spec, new("", [], []), overlaps * 10000 + sameDay * 1000 + nearby * 100 - gap));
            }
        }
        foreach (var item in ranked.OrderBy(x => x.Score).ThenBy(x => x.Spec.Date).ThenBy(x => x.Spec.Start).Take(12)) {
            var analysis = await PlannerEngine.Analyze(db, w, item.Spec, ct);
            results.Add(new { spec = item.Spec, score = item.Score, analysis, explanation = analysis.Conflicts.Count == 0 ? "No recorded academic clashes; ranked for lighter exam load and preparation time." : $"{analysis.Conflicts.Count} warning(s); review the affected courses before choosing." });
        }
        return Results.Ok(new { candidates = results, coverage = "Based on selected course and section timetables, published changes and workspace drafts. Private appointments and unrecorded classes are not included." });
    }
}
