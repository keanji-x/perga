using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel;

public sealed record BootstrapDto(
    string Version,
    IReadOnlyList<ProviderStatus> Providers,
    IReadOnlyList<CompiledTemplateManifest> Templates,
    IReadOnlyList<AreaDto> Areas);

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
    string? FirstMessage = null,
    string? ParentTrackId = null,
    string? ParentAttemptId = null,
    IReadOnlyList<string>? EnabledPlugins = null,
    string? BaseRevision = null,
    double? SortOrder = null);

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
    IReadOnlyList<ReportDiagnosticDto> Diagnostics);

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
    string? Model = null);

public sealed record SendMessageRequest(
    string Content,
    string IdempotencyKey,
    bool SteerIfRunning = false);

public sealed record SessionItemsPageDto(
    IReadOnlyList<SessionItemDto> Items,
    SessionTurnDto? ActiveTurn,
    AgentSessionDto Session);

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
    IReadOnlyList<WorkspaceFileEntryDto>? Entries);

public sealed record TrackStatusDto(
    TrackDto Track,
    IReadOnlyList<AgentSessionDto> Sessions,
    IReadOnlyList<TaskDto> Tasks,
    IReadOnlyList<AskDto> OpenAsks,
    IReadOnlyList<TerminalDto> Terminals,
    int UnreadMailCount);

public sealed record BootReconcileResult(
    IReadOnlyList<string> AffectedTrackIds,
    int DiscardedLeases,
    int WokenTracks);
