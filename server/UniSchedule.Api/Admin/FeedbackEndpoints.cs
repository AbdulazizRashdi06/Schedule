using Microsoft.EntityFrameworkCore;
using UniSchedule.Api.Auth;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Admin;

/// <summary>"Rate Classy": staff push a message, students answer it once with 1–5 stars and a comment, or "Not now".</summary>
public static class FeedbackEndpoints
{
    public const int MaxMessage = 300;
    public const int MaxComment = 1000;

    public record PromptBody(string? Message);
    public record AnswerBody(int PromptId, int? Rating, string? Comment, bool? Dismissed);

    public static void MapFeedback(this RouteGroupBuilder api)
    {
        // ---- Students ----
        var me = api.MapGroup("/me/feedback").RequireAuthorization(Session.StudentPolicy);

        me.MapGet("", async (HttpContext http, AppDb db, CancellationToken ct) =>
        {
            var id = http.User.Id();
            var prompt = await db.FeedbackPrompts.Where(p => p.ClosedAt == null)
                .Where(p => !db.FeedbackResponses.Any(r => r.PromptId == p.Id && r.StudentId == id))
                .OrderByDescending(p => p.Id).Select(p => new { p.Id, p.Message }).FirstOrDefaultAsync(ct);
            return Results.Ok(new { prompt });
        });

        me.MapPost("", async (AnswerBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var id = http.User.Id();
            if (!await db.FeedbackPrompts.AnyAsync(p => p.Id == body.PromptId && p.ClosedAt == null, ct))
                return Errors.NotFound("That question has closed.");
            var dismissed = body.Dismissed == true;
            if (!dismissed && body.Rating is not (>= 1 and <= 5)) return Errors.BadRequest("Pick 1 to 5 stars.");
            var comment = dismissed || string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
            if (comment?.Length > MaxComment) return Errors.BadRequest($"Keep it under {MaxComment} characters.");
            if (await db.FeedbackResponses.AnyAsync(r => r.PromptId == body.PromptId && r.StudentId == id, ct))
                return Results.NoContent();
            db.FeedbackResponses.Add(new FeedbackResponse
            {
                PromptId = body.PromptId, StudentId = id, Rating = dismissed ? null : body.Rating,
                Comment = comment, Dismissed = dismissed, At = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- Staff: no student IDs, so ratings stay anonymous ----
        var admin = api.MapGroup("/admin/feedback").RequireAuthorization(Session.StaffPolicy);

        admin.MapGet("", async (AppDb db, CancellationToken ct) =>
        {
            var prompts = await db.FeedbackPrompts.OrderByDescending(p => p.Id).ToListAsync(ct);
            var answers = await db.FeedbackResponses.ToListAsync(ct);
            return Results.Ok(prompts.Select(p =>
            {
                var mine = answers.Where(r => r.PromptId == p.Id).ToList();
                var rated = mine.Where(r => r.Rating is not null).ToList();
                return new
                {
                    p.Id, p.Message, p.By, p.CreatedAt, p.ClosedAt, open = p.ClosedAt == null,
                    ratings = rated.Count, notNow = mine.Count(r => r.Dismissed),
                    average = rated.Count == 0 ? (double?)null : Math.Round(rated.Average(r => r.Rating!.Value), 2),
                    stars = Enumerable.Range(1, 5).Select(s => rated.Count(r => r.Rating == s)).ToArray(),
                    comments = rated.Where(r => r.Comment != null).OrderByDescending(r => r.At)
                        .Select(r => new { r.Rating, r.Comment, r.At }).ToList(),
                };
            }));
        });

        admin.MapPost("", async (PromptBody body, HttpContext http, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var message = body.Message?.Trim() ?? "";
            if (message.Length is 0 or > MaxMessage) return Errors.BadRequest($"Write a message (up to {MaxMessage} characters).");
            var now = clock.GetUtcNow();
            await db.FeedbackPrompts.Where(p => p.ClosedAt == null).ExecuteUpdateAsync(s => s.SetProperty(p => p.ClosedAt, now), ct);
            var prompt = new FeedbackPrompt { Message = message, By = await OwnerEndpoints.ActorName(http, db, ct), CreatedAt = now };
            db.FeedbackPrompts.Add(prompt);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { prompt.Id });
        });

        admin.MapPost("/{id:int}/close", async (int id, AppDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var prompt = await db.FeedbackPrompts.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (prompt is null) return Errors.NotFound("That message doesn't exist.");
            prompt.ClosedAt ??= clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
