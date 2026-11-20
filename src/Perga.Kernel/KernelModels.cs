using System.Text.Json;
using System.Text.Json.Serialization;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel;

public sealed record BootstrapDto(
    string Version,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<CompiledTemplateManifest> Templates,
    IReadOnlyList<AreaDto> Areas,
    string DatabaseId = "",
    string DbInstanceId = "",
    bool AuthRequired = false);

public sealed record CreateAreaRequest(
    string Name,
    string? Color = null,
    string? DefaultCwd = null,
    IReadOnlyList<string>? Folders = null,
    double? SortOrder = null);

public sealed record PatchAreaRequest(
    string? Name = null,
    string? Color = null,
    string? DefaultCwd = null,
    bool ClearDefaultCwd = false,
    IReadOnlyList<string>? Folders = null,
    double? SortOrder = null);

public sealed record CreateTrackRequest(
    string AreaId,
    string? Title = null,
    string? Cwd = null,
    string? WorkspaceDriver = null,
    string? TemplateId = null,
    JsonElement? TemplateInputs = null,
    string? Provider = null,
    string? Model = null,
    string? ParentTrackId = null,
    string? ParentAttemptId = null,
    IReadOnlyList<string>? EnabledPlugins = null,
    string? BaseRevision = null,
    double? SortOrder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkspaceOptionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PermissionMode = null);

public sealed record TrackWorkspaceOptionDto(string Id, string Label, string Description);

public sealed record TrackWorkspaceOptionsDto(string? DefaultId, IReadOnlyList<TrackWorkspaceOptionDto> Options);

public sealed record PatchTrackRequest(
    string? Title = null,
    bool? Closed = null,
    bool? Pinned = null,
    IReadOnlyList<string>? EnabledPlugins = null,
    string? LeadSessionId = null,
    double? SortOrder = null);

public sealed record ReportDiagnosticDto(
    string Severity,
    string Code,
    string Message,
    string? BlockId = null);

public sealed record ReportReadResult(
    int DocRev,
    string Summary,
    string RenderedMarkdown,
    IReadOnlyList<ReportBlock> Blocks,
    IReadOnlyList<TaskDto> Tasks,
    ReadAnchor Anchor,
    IReadOnlyList<ReportDiagnosticDto> Diagnostics,
    [property: JsonIgnore] IReadOnlyList<BlockChangeRecord>? Changes = null);

public sealed record ReportCommitRequest(
    ReadAnchor Anchor,
    IReadOnlyList<BlockOp> Ops,
    string? Summary = null,
    string? CommitMessage = null);

public sealed record HumanBlockUpdateRequest(
    int DocRev,
    int KnownBlockRev,
    ReportBlockKind? Kind = null,
    string? Markdown = null,
    JsonElement? Payload = null,
    string? Summary = null,
    string? CommitMessage = null);

public sealed record CreateSessionRequest(
    string? Title = null,
    string? Provider = null,
    string? Model = null,
    string? Text = null,
    string? ReasoningEffort = null,
    string? IdempotencyKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PermissionMode = null);

public sealed record PatchSessionRequest(
    string? Title = null,
    string? Provider = null,
    string? Model = null,
    bool ClearModel = false,
    bool? Archived = null);

public sealed record SteeringResult(string InputId, string Delivery);

public sealed record UpdateSessionModelRequest(string? Model, string? ReasoningEffort = null);

public sealed record SendMessageRequest(
    string Content,
    string IdempotencyKey,
    bool SteerIfRunning = false,
    bool QueueIfBusy = false);

public sealed record QueuedMessageReceipt(string SessionId, string InputId, bool Queued = true, string? Delivery = null)
{
    public string MessageId => InputId;
}
public sealed record QueuedSessionMessageDto(string Id, string Text, long QueuedAtMs, string State);
public sealed record MessageAcceptance(SessionInputReceipt? Receipt, SessionTurnDto? SteeredTurn, bool Replay, QueuedMessageReceipt? QueueReceipt = null);

public sealed record ProviderSessionResumeDto(string TurnId, string State, bool CanStartFresh = false);

public sealed record SessionItemsPageDto(
    IReadOnlyList<SessionItemDto> Items,
    SessionTurnDto? ActiveTurn,
    AgentSessionDto Session,
    SessionInputReceipt? UnknownInput,
    bool HasMoreBefore,
    IReadOnlyList<QueuedSessionMessageDto> QueuedMessages,
    bool CanSteer,
    IReadOnlyList<string> QueueHandoffIds,
    ProviderSessionResumeDto? Resume,
    WorkerActivityDto? Worker = null,
    IReadOnlyList<PermissionModeOption>? PermissionModes = null);

public sealed record WorkerActivityEvent(long Sequence, string Kind, JsonElement Payload, long CreatedAtMs);

public sealed record WorkerActivityDto(
    TaskDto Task, string AttemptId, string Phase, bool NativeSteer,
    IReadOnlyList<ApprovalDto> PendingApprovals, long Sequence, bool Reset,
    IReadOnlyList<WorkerActivityEvent> Events, IReadOnlyList<WorkerMessageActivity> Messages,
    string Text, bool TextTruncated, WorkerSettings NextSettings, WorkerSettings? ActiveSettings,
    bool CanChangeModel, IReadOnlyList<string> ReasoningEfforts, string? LastError,
    WorkerProviderConfiguration? ProviderConfiguration = null, bool ApprovalAuthorizationConfigured = false);

public sealed record WorkerSettings(long Version, string? Model, string? ReasoningEffort, string ApprovalMode);

public sealed record UpdateWorkerSettingsRequest(
    [property: System.Text.Json.Serialization.JsonRequired] string AttemptId,
    [property: System.Text.Json.Serialization.JsonRequired] long Version,
    [property: System.Text.Json.Serialization.JsonRequired] string? Model,
    [property: System.Text.Json.Serialization.JsonRequired] string? ReasoningEffort,
    [property: System.Text.Json.Serialization.JsonRequired] string ApprovalMode);

public sealed record WorkerMessageActivity(string Id, string Author, string Content, string? TurnId, string Delivery, long CreatedAtMs);

public enum WorkerMessageMode { Steer, Queue }

public sealed record WorkerMessageRequest(
    string AttemptId, string Content, string RequestKey, string? TurnId = null, bool AllowQueue = false,
    WorkerMessageMode? Mode = null);

public sealed record WorkerCompletionOrigin(string Token, string TurnId);

public sealed record WorkerMessageReceipt(string? TurnId, string Delivery, string InputId)
{
    public string MessageId => InputId;
}

public sealed record DispatchWorkerRequest(string Goal, string? Access = null, string? Acceptance = null,
    IReadOnlyList<string>? DependsOn = null, string? Gate = null, string? Provider = null, string? Model = null, string? Role = null);

public sealed record TemplateSetResult(TrackDto Track, IReadOnlyList<string> Warnings, bool DryRun);

public sealed record WorkerDispatchReceipt(TaskDto Task, string? SessionId, string RequestKey);

public sealed record OpenTerminalRequest(
    string? Title = null,
    string? Program = null,
    IReadOnlyList<string>? Args = null,
    string? Cwd = null,
    IReadOnlyDictionary<string, string>? Env = null,
    string? IdempotencyKey = null,
    ushort Cols = 120,
    ushort Rows = 40);

public sealed record WorkspaceFileEntryDto(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long SizeBytes);

public sealed record WorkspaceFileResultDto(
    string RelativePath,
    bool IsDirectory,
    string? Content,
    bool Truncated,
    IReadOnlyList<WorkspaceFileEntryDto>? Entries,
    bool IsBinary = false);

public sealed record HostDirEntryDto(
    string Name,
    bool IsDir,
    bool IsSymlink);

public sealed record HostListDirResultDto(
    string Path,
    string? Parent,
    IReadOnlyList<HostDirEntryDto> Entries);

public sealed record TrackOverlaysResponseDto(
    IReadOnlyList<TrackOverlayDto> Tracks);

public sealed record TrackStatusDto(
    TrackDto Track,
    IReadOnlyList<AgentSessionDto> Sessions,
    IReadOnlyList<TaskDto> Tasks,
    IReadOnlyList<AskDto> OpenAsks,
    IReadOnlyList<TerminalDto> Terminals,
    int UnreadMailCount)
{
    public IReadOnlyList<ApprovalDto> PendingApprovals { get; init; } = [];
}

public sealed record BootReconcileResult(
    IReadOnlyList<string> AffectedTrackIds,
    int DiscardedLeases,
    int WokenTracks);
