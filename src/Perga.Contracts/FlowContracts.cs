using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perga.Contracts;

/// <summary>
/// Domain-neutral business settlement outcome for Flow nodes and Tasks.
/// </summary>
public enum FlowOutcome
{
    None,
    Ok,
    Attention,
    Blocked,
}

public sealed record FlowAfterRefDto(
    string Node,
    string On = "ok");

public sealed record FlowWhenSpecDto(
    IReadOnlyList<FlowAfterRefDto>? After = null,
    string? On = null,
    string? Cron = null,
    string? Tz = null,
    int? MaxOccurrences = null,
    string? At = null);

public sealed record FlowDoSpecDto(
    string Kind,
    string? Session = null,
    string? Role = null,
    string? Access = null,
    string? Provider = null,
    string? Model = null,
    string? Gate = null,
    string? Prompt = null,
    string? Acceptance = null,
    string? Cmd = null,
    int? PrChecks = null,
    string? Repo = null,
    string? HeadSha = null,
    string? Interval = null,
    string? Timeout = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Repeat = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReasoningEffort = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PermissionMode = null);

public sealed record FlowNodeSpecDto(
    string Id,
    FlowDoSpecDto Do,
    string? Title = null,
    string Source = "agent",
    FlowWhenSpecDto? When = null,
    bool Canceled = false);

public sealed record FlowSpecDto(
    long Rev,
    IReadOnlyList<FlowNodeSpecDto> Nodes,
    [property: JsonPropertyName("$schema"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Schema = null);

public sealed record FlowValidationErrorDto(
    string Path,
    string Message,
    string? Suggestion = null);

public sealed record FlowPlanDiffDto(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Canceled,
    IReadOnlyList<string> Archived,
    IReadOnlyList<string> Unchanged);

public sealed record FlowDependencyProjectionDto(
    string Node,
    string On);

public sealed record FlowGateCheckProjectionDto(
    string Label,
    string Status,
    string? Detail = null,
    string? Command = null,
    string? Output = null);

public sealed record FlowWatchProgressDto(
    string ProbeKind,
    string Label,
    int? PassedChecks = null,
    int? TotalChecks = null,
    long? LastCheckedAtMs = null,
    long? StartedAtMs = null,
    long? TimeoutAtMs = null,
    string? Detail = null);

public sealed record FlowNodeProjectionDto(
    string Id,
    string Title,
    string Source,
    string WhenKind,
    IReadOnlyList<FlowDependencyProjectionDto> After,
    string? Cron,
    string? Tz,
    string? At,
    string DoKind,
    string? Role,
    string? Access,
    string? TargetSession,
    string? Provider,
    string? Model,
    string? Prompt,
    string? Cmd,
    string? Gate,
    bool Canceled,
    string Status,
    string Outcome,
    string? OutcomeSummary,
    int RunCount,
    string? SessionId,
    long? StartedAtMs,
    long? FinishedAtMs,
    long? NextFireAtMs,
    long? LastFiredAtMs,
    int FiredCount,
    IReadOnlyList<string> WaitingOn,
    IReadOnlyList<FlowGateCheckProjectionDto> Gates,
    FlowWatchProgressDto? WatchProgress = null,
    bool Repeat = false,
    string? ReasoningEffort = null,
    string? PermissionMode = null);

public sealed record FlowArchivedNodeProjectionDto(
    string Id,
    string Title,
    string DoKind,
    string Status,
    string Outcome,
    string? OutcomeSummary,
    int RunCount,
    string? SessionId,
    string? Model,
    long? FinishedAtMs,
    long ArchivedAtMs);

public sealed record FlowProjectionDto(
    long Rev,
    string? FilePath,
    IReadOnlyList<FlowNodeProjectionDto> Nodes,
    IReadOnlyList<FlowArchivedNodeProjectionDto> Archived);

public sealed record FlowCommitResultDto(
    bool Ok,
    bool DryRun,
    long FlowRev,
    FlowPlanDiffDto Plan,
    IReadOnlyList<FlowValidationErrorDto> Diagnostics,
    FlowProjectionDto Flow);

public sealed record FlowCommitRequest(
    bool? DryRun = null,
    long? ExpectedFlowRev = null,
    string? Content = null);

public sealed record FlowSnapshotDto(
    long FlowRev,
    string? FilePath,
    JsonElement Spec,
    FlowProjectionDto Projection);
