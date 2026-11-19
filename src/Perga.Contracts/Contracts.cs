using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perga.Contracts;

public enum AccessMode { ReadOnly, ReadWrite }
public enum TaskRunStatus { Draft, Pending, Running, Verifying, Done, Failed, Canceled }
public enum TaskTriggerSource { Manual, Scheduler, Wake }
public enum SemanticVerdict { None, Accepted, Rejected }
public enum AskDelivery { Wake, Hold }
public enum AskStatus { Open, Answered, Withdrawn }
public enum TerminalStatus { Running, Exited }
public enum EditActorKind { Human, Lead, Subagent, System }

public sealed record AreaDto(
    string Id,
    string Name,
    string Color,
    string? DefaultCwd,
    IReadOnlyList<string> Folders,
    double SortOrder,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record TemplateHeaderBadgeDto(string Label, string? Tone = null);
public sealed record TemplateHeaderNoticeDto(string Text, string? Tone = null);
public sealed record TemplateHeaderMetricDto(string Label, string Value, string? Delta = null,
    string? Tone = null, string? Sub = null, IReadOnlyList<double>? Spark = null);
public sealed record TemplateHeaderRuleDto(string Label, string Text);
public sealed record TemplateHeaderViewDto(
    IReadOnlyList<TemplateHeaderBadgeDto>? Badges = null,
    TemplateHeaderNoticeDto? Notice = null,
    IReadOnlyList<TemplateHeaderMetricDto>? Metrics = null,
    IReadOnlyList<TemplateHeaderRuleDto>? Rules = null);
public sealed record TemplateScheduleDto(string Id, string Cron, string Timezone);

public sealed record TrackDto(
    string Id,
    string AreaId,
    string? ParentTrackId,
    string? ParentAttemptId,
    string Title,
    string Cwd,
    string WorkspaceDriver,
    JsonElement WorkspaceSpec,
    string? LeadSessionId,
    string? TemplateId,
    JsonElement TemplateInputs,
    JsonElement TemplateState,
    IReadOnlyList<string> EnabledPlugins,
    long? ClosedAtMs,
    long? PinnedAtMs,
    double SortOrder,
    long CreatedAtMs,
    long UpdatedAtMs,
    string? TemplateTitle = null,
    TemplateHeaderViewDto? TemplateHeaderView = null,
    IReadOnlyList<TemplateScheduleDto>? TemplateSchedules = null);

public sealed record SessionContextUsage(
    long UsedTokens,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? WindowTokens,
    long ObservedAtMs)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public double? Ratio => WindowTokens is > 0
        ? Math.Clamp((double)UsedTokens / WindowTokens.Value, 0.0, 1.0)
        : null;
}

public sealed record AgentSessionDto(
    string Id,
    string TrackId,
    bool IsLead,
    string? BoundTaskId,
    string? BoundAttemptId,
    string? Title,
    string Provider,
    string? Model,
    string? NativeThreadId,
    int Epoch,
    string Status,
    long? ArchivedAtMs,
    long CreatedAtMs,
    long UpdatedAtMs,
    string? ReasoningEffort = null,
    string? PermissionMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] SessionContextUsage? ContextUsage = null);

public sealed record SessionTurnDto(
    string Id,
    string SessionId,
    int TurnIndex,
    int Epoch,
    string TriggerKind,
    JsonElement Input,
    string Status,
    JsonElement? Usage,
    string? ErrorText,
    long? StartedAtMs,
    long? CompletedAtMs,
    long? UserItemId = null);

public sealed record SessionInputReceipt(
    string InputId,
    string TurnId,
    string SessionId,
    string DeliveryState,
    string TurnStatus,
    long? ResolvedAtMs)
{
    public string MessageId => InputId;
    public string StatusUrl => $"/api/v1/sessions/{SessionId}/inputs/{InputId}";
}

public sealed record SessionCreationReceipt(AgentSessionDto Session, SessionInputReceipt? FirstInput);

public sealed record SessionItemDto(
    long Id,
    string SessionId,
    string TurnId,
    string Kind,
    JsonElement Payload,
    long CreatedAtMs)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TurnStatus { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? InputDelivery { get; init; }

    public string? MessageStatus { get; init; }
}

public sealed record GateStepDto(string Name, string Cmd);

public sealed record GateSpecDto(
    IReadOnlyList<GateStepDto> Steps,
    int TimeoutSecs = 1800);

public sealed record TaskDto(
    string Id,
    string TrackId,
    string BlockId,
    string TaskKey,
    int Generation,
    string Kind,
    string? Provider,
    AccessMode Access,
    bool Ready,
    IReadOnlyList<string> DependsOn,
    string GoalOrCmd,
    JsonElement? Context,
    string? Acceptance,
    GateSpecDto? Gate,
    TaskRunStatus Status,
    string? StatusDetail,
    SemanticVerdict SemanticVerdict,
    string? SemanticNote,
    string? LatestAttemptId,
    long CreatedAtMs,
    long UpdatedAtMs,
    string? Role = null);

public sealed record TaskAttemptDto(
    string AttemptId,
    string TaskId,
    string TrackId,
    string? SessionId,
    string Status,
    JsonElement? WorkerResult,
    JsonElement Meta,
    JsonElement? WorkspaceLease,
    JsonElement? GateVerdict,
    long StartedAtMs,
    long? FinishedAtMs);

public sealed record AskQuestionDto(
    string Question,
    IReadOnlyList<string> Options,
    bool IsMultiSelect = false);

public sealed record AskDto(
    string Id,
    string TrackId,
    AskStatus Status,
    IReadOnlyList<AskQuestionDto> Questions,
    JsonElement? Answer,
    long CreatedAtMs,
    long? AnsweredAtMs,
    AskDelivery Delivery,
    string? SessionId = null,
    string? TurnId = null);

public sealed record ApprovalDto(
    string Id,
    string ExecutionId,
    string SessionId,
    string? TurnId,
    IReadOnlyList<AskQuestionDto> Questions,
    long CreatedAtMs);

public sealed record MailDto(
    string Id,
    string FromTrackId,
    string ToTrackId,
    string Summary,
    string Body,
    long? ReadAtMs,
    long CreatedAtMs,
    string? ReplyToMailId = null);

public sealed record TerminalDto(
    string Id,
    string TrackId,
    string? AttemptId,
    string? IdempotencyKey,
    string Title,
    string? Program,
    string Cwd,
    TerminalStatus Status,
    int? ExitCode,
    long CreatedAtMs,
    long? ExitedAtMs);

public sealed record EventEnvelopeDto(
    long? EventId,
    string? AreaId,
    string? TrackId,
    string Kind,
    JsonElement Payload,
    long CreatedAtMs);

public sealed record DbMaintenanceStatsDto(
    int PrunedEvents,
    int ThinnedRevisions,
    int CompactedItems,
    int ReclaimedPages)
{
    public int CompactedAttempts { get; init; }
    public int ReclaimedBlobs { get; init; }
}

public sealed record TrackOverlayDto(
    string TrackId,
    string AreaId,
    bool IsWorking,
    int OpenAskCount,
    bool HasFailedTask,
    int UnreadMailCount,
    long UpdatedAtMs);
