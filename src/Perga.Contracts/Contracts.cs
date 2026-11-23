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

public sealed record ExecutionTargetDto(
    string Id,
    string Label,
    string Kind,
    bool IsDefault = false,
    string? Destination = null,
    bool Removable = false);

public sealed record StoredExecutionTarget(
    string Id,
    string Label,
    string Kind,
    string Host,
    int Port,
    string User,
    string? IdentityFile,
    string WorkspacesRoot,
    long CreatedAtMs,
    long UpdatedAtMs);

public sealed record CreateExecutionTargetRequest(
    string? Target = null,
    string? Label = null,
    string? Host = null,
    int? Port = null,
    string? User = null,
    string? IdentityFile = null,
    string? WorkspacesRoot = null);

public sealed record AreaDto(
    string Id,
    string Name,
    string Color,
    string? DefaultCwd,
    IReadOnlyList<string> Folders,
    double SortOrder,
    long CreatedAtMs,
    long UpdatedAtMs,
    string ExecutionTargetId = "local");

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
    JsonElement Data = default,
    long DataRev = 0,
    string ExecutionTargetId = "local",
    long FlowRev = 0,
    string? FlowSpecJson = null,
    string? FlowFileSha256 = null)
{
    private static readonly JsonElement EmptyData = JsonSerializer.SerializeToElement(new { });

    public JsonElement Data { get; init; } = Data.ValueKind == JsonValueKind.Undefined ? EmptyData : Data;
}

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] SessionContextUsage? ContextUsage = null,
    string? UnknownReason = null,
    string? UnknownTurnId = null,
    string? UnknownLastError = null,
    bool ReconnectInvalidated = false,
    long? LastNotifiableTurnAtMs = null,
    string? ParentSessionId = null,
    int SpawnDepth = 0,
    long ConfigVersion = 0,
    string NotifyParent = "none",
    string? SystemPrompt = null);

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
}

public sealed record SessionCreationReceipt(AgentSessionDto Session, SessionInputReceipt? FirstInput, string? RequestKey = null);

public sealed record SessionAttachmentDto(
    string Id,
    string ContentType,
    long Size,
    string Url);

public sealed record UploadAttachmentResponseDto(
    string AttachmentId,
    string ContentType,
    long Size,
    string Url);

public sealed record SessionItemDto(
    long Id,
    string SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TurnId,
    string Kind,
    JsonElement Payload,
    long CreatedAtMs)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? TurnStatus { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? InputDelivery { get; init; }

    public string? MessageStatus { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SessionAttachmentDto>? Attachments { get; init; }
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
    long CreatedAtMs,
    long UpdatedAtMs,
    string? Role = null,
    string? SessionId = null,
    int RunCount = 0,
    JsonElement? WorkerResult = null,
    JsonElement? Meta = null,
    JsonElement? GateVerdict = null,
    long? StartedAtMs = null,
    long? FinishedAtMs = null,
    FlowOutcome Outcome = FlowOutcome.None,
    string? OutcomeSummary = null,
    string? TriggerRunKey = null,
    long? ArchivedAtMs = null);

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
    string? TurnId = null,
    string? TargetSessionId = null);

public sealed record ApprovalChoiceDto(
    string Id,
    string Label,
    bool IsApproval);

public sealed record ApprovalDto(
    string Id,
    string ExecutionId,
    string SessionId,
    string? TurnId,
    string Title,
    IReadOnlyList<ApprovalChoiceDto> Options,
    long CreatedAtMs,
    string Source = "policy",
    string? ToolName = null,
    JsonElement? Arguments = null,
    string? DelegateSessionId = null);


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
    string? SessionId,
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
    public int CompactedTasks { get; init; }
    public int ReclaimedBlobs { get; init; }
    public int ReclaimedAttachments { get; init; }
}

public sealed record TrackOverlayDto(
    string TrackId,
    string AreaId,
    bool IsWorking,
    int OpenAskCount,
    int PendingApprovalCount,
    bool HasBlockedSession,
    bool WorkspaceFailed,
    long UpdatedAtMs,
    IReadOnlyList<NotifiableSessionWatermarkDto>? NotifiableSessions = null);

public sealed record NotifiableSessionWatermarkDto(string SessionId, long LastNotifiableTurnAtMs);
public sealed record ReadReceiptDto(string Kind, string TargetId, long ReadThroughMs, bool ManualUnread);
public sealed record ReadReceiptsResponseDto(long BaselineMs, IReadOnlyList<ReadReceiptDto> Receipts);
public sealed record MarkReadRequest(long? ReadThroughMs = null);

public sealed record TrackDataPatchResult(bool Ok, long DataRev, JsonElement Data);
