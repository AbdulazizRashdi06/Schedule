using System.Text.Json;
using UniSchedule.Api.Data;

namespace UniSchedule.Api.Planner;

public class BatchRepAssignment
{
    public int StudentId { get; set; }
    public string HomeFamily { get; set; } = "";
    public string AssignedBy { get; set; } = "";
    public DateTimeOffset AssignedAt { get; set; }
}

public class PlannerWorkspace
{
    public int Id { get; set; }
    public int SemesterId { get; set; }
    public string Family { get; set; } = "";
    public int DailyExamWarning { get; set; } = 2;
    public int WeeklyExamWarning { get; set; } = 3;
    public int Version { get; set; } = 1;
}

public class PlannerBatch
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public int? MajorId { get; set; }
    public int? Year { get; set; }
    public string TargetsJson { get; set; } = "[]";
    public int Version { get; set; } = 1;
}

public class PlannerEvent
{
    public int Id { get; set; }
    public int SemesterId { get; set; }
    public int Version { get; set; } = 1;
    public string PayloadJson { get; set; } = "{}";
    public bool Withdrawn { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class PlannerProposal
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public int? EventId { get; set; }
    public int BaseEventVersion { get; set; }
    public int Version { get; set; } = 1;
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = "draft";
    public string UpdatedBy { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

public class PlannerActivity
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public int? EventId { get; set; }
    public int? ProposalId { get; set; }
    public string Action { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public DateTimeOffset At { get; set; }
}

public class PlannerAiJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int WorkspaceId { get; set; }
    public int StudentId { get; set; }
    public bool OwnerAccount { get; set; }
    public string Status { get; set; } = "queued";
    public string InputJson { get; set; } = "{}";
    public string ResultJson { get; set; } = "{}";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public record PlannerTarget(int CourseId, int[] SectionIds);
public record PlannerSource(int CourseId, int SectionId, string Fingerprint, string Date, string Start, string End);
public record EventSpec(string Kind, string Action, string Title, string Date, string Start, string End,
    string Room, PlannerTarget[] Targets, string? RepeatUntil = null, PlannerSource? Source = null, string? Notes = null);
public record CalendarEntry(string Key, int? EventId, int? ProposalId, int Version, string Kind, string Title,
    string Date, string Start, string End, string Room, PlannerTarget[] Targets, string Status,
    PlannerSource? Source = null, bool Stale = false, string? Notes = null);
public record PlannerConflict(string Kind, string Date, string Message, string[] Courses, int[] SectionIds);
public record PlannerAnalysis(string Token, List<PlannerConflict> Conflicts, List<string> CoverageWarnings);
public record PlannerAiInput(string Text, string[] Images);

public static class PlannerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
    public static string Family(Major major) => string.IsNullOrWhiteSpace(major.Family)
        ? $"major:{major.Id}" : $"family:{major.Family.Trim().ToUpperInvariant()}";
}
