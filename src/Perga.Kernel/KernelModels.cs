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
    bool AuthRequired = true,
    IReadOnlyList<ExecutionTargetDto>? ExecutionTargets = null);

public sealed record CreateAreaRequest(
    string Name,
    string? Color = null,
    string? DefaultCwd = null,
    IReadOnlyList<string>? Folders = null,
    double? SortOrder = null,
    string? ExecutionTargetId = null);

public sealed record PatchAreaRequest(
    string? Name = null,
    string? Color = null,
    string? DefaultCwd = null,
    bool ClearDefaultCwd = false,
    IReadOnlyList<string>? Folders = null,
    double? SortOrder = null,
    string? ExecutionTargetId = null);

public sealed record CreateTrackRequest(
    string AreaId,
    string? Title = null,
    string? Cwd = null,
    string? WorkspaceDriver = null,
    string? TemplateId = null,
    JsonElement? TemplateInputs = null,
    string? Provider = null,
    string? Model = null,
    IReadOnlyList<string>? EnabledPlugins = null,
    string? BaseRevision = null,
    double? SortOrder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkspaceOptionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PermissionMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExecutionTargetId = null);

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
    [property: JsonIgnore] IReadOnlyList<BlockChangeRecord>? Changes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? DataRev = null);

public sealed record ReportSnapshot(
    int DocRev,
    string Summary,
    string RenderedMarkdown,
    IReadOnlyList<ReportBlock> Blocks,
    long? DataRev,
    IReadOnlyDictionary<string, JsonElement>? TemplateViews);

public sealed record ReportCommitRequest(
    ReadAnchor Anchor,
    IReadOnlyList<BlockOp> Ops,
    string? Summary = null,
    string? CommitMessage = null);

public sealed record TrackProvidersDto(
    string DefaultProvider, long ObservedAtMs, IReadOnlyList<TrackProviderEntryDto> Providers);

public sealed record TrackProviderEntryDto(
    string Provider, bool Available,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DefaultModel,
    string PermissionCatalogSource, IReadOnlyList<TrackProviderModelDto> Models,
    IReadOnlyList<PermissionModeOption> PermissionModes);

public sealed record TrackProviderModelDto(
    string Id, string DisplayName, bool IsDefault,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DefaultReasoningEffort,
    IReadOnlyList<string> ReasoningEfforts);

public sealed record CreateSessionRequest(
    string? Title = null,
    string? Provider = null,
    string? Model = null,
    string? ReasoningEffort = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PermissionMode = null,
    string? SystemPrompt = null, string? Prompt = null, string? NotifyParent = null);

public sealed record SteeringResult(string InputId, string Delivery);

public sealed record QueuedSessionMessageDto(string Id, string Text, long QueuedAtMs, string State);
public sealed record SessionUnknownDetailDto(string Reason, string Detail, bool CanReconnect,
    bool CanResetContext, bool Recovering, string? LastError);

public sealed record SessionItemsPageDto(
    IReadOnlyList<SessionItemDto> Items,
    SessionTurnDto? ActiveTurn,
    AgentSessionDto Session,
    bool HasMoreBefore,
    IReadOnlyList<QueuedSessionMessageDto> QueuedMessages,
    bool CanSteer,
    IReadOnlyList<string> QueueHandoffIds,
    string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReadonlyReason,
    bool CanReopen,
    bool WorkerPaused,
    SessionTurnDto? LastTurn,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] SessionUnknownDetailDto? UnknownDetail,
    IReadOnlyList<PermissionModeOption>? PermissionModes = null,
    SessionActivityDto? Activity = null, SessionTaskBindingDto? TaskBinding = null,
    SessionCapabilitiesDto? Capabilities = null, IReadOnlyList<PendingInteractionDto>? PendingInteractions = null,
    AgentRuntimeStatusDto? RuntimeStatus = null, IReadOnlyList<AgentRuntimeTaskDto>? RuntimeTasks = null);

public sealed record WorkerActivityEvent(long Sequence, string Kind, JsonElement Payload, long CreatedAtMs);


public sealed record WorkerCompletionOrigin(string Token, string RuntimeBindingId, string TaskId, int RunCount)
{
    internal string? AuthorizedPolicyGeneration { get; init; }
}

public sealed record TemplateSetResult(TrackDto Track, IReadOnlyList<string> Warnings, bool DryRun, TemplateSetPlanDto Plan);

public sealed record TemplateSetPlanDto(
    TemplateFlowDiffDto FlowDiff, string DataAction, string ReportAction, IReadOnlyList<string> PluginsAdded);

public sealed record TemplateFlowDiffDto(IReadOnlyList<string> Added, IReadOnlyList<string> Updated, IReadOnlyList<string> Canceled);

public sealed record TemplateInputsUpdateResult(TrackDto Track, IReadOnlyList<string> Warnings);

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
    int WokenTracks);
