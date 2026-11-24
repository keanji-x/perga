using System.Text.Json;
using Perga.Contracts;
using Perga.ReportDoc;
using Perga.VcsKit;

namespace Perga.Kernel.Ports;

public sealed record TrackAggregateSnapshot(
    TrackDto Track,
    ReportDocument Report,
    IReadOnlyList<TaskDto> Tasks,
    IReadOnlyList<AgentSessionDto> Sessions,
    IReadOnlyList<AskDto> OpenAsks,
    string? TemplateSnapshotSource,
    string? WorkspacePreparation = null)
{
    public IReadOnlyList<ApprovalDto> PendingApprovals { get; init; } = [];
}

public sealed record TemplateReportSeed(
    ReportDocument Document, byte[] CompressedBlocks, string AuthorSessionId, EventEnvelopeDto Event);

public sealed record ResourceCreationKey(string Operation, string ScopeId, string Key, string Hash);

// Stored as JSON numbers by SQLite.
public enum WorkspacePreparationPhase
{
    Pending = 0,
    Provisioning = 1,
    Mounting = 2,
    Initializing = 3,
    Failed = 4,
    Unknown = 5,
    Ready = 6,
    Provisioned = 7
}

public sealed record WorkspacePreparation(WorkspaceProvisionRequest Request, string? Command,
    WorkspacePreparationPhase Phase = WorkspacePreparationPhase.Pending, WorkspaceSpec? Spec = null,
    bool RepeatableProvision = false);

public sealed record TrackCreation(TrackDto Track, ReportDocument Report, string? TemplateSnapshot,
    AgentSessionDto Lead, IReadOnlyList<TaskDto> Tasks, SessionInputRequest? FirstInput,
    WorkspacePreparation? Preparation, IReadOnlyList<EventEnvelopeDto> Events, string? DailyDate = null, AreaDto? SystemArea = null)
{
    public IReadOnlyList<TrackScheduleDto> FlowClocks { get; init; } = [];
}

public sealed record TrackFlowUpdate(
    long ExpectedRev, IReadOnlyList<TaskDto> Tasks, IReadOnlyList<TrackScheduleDto> Clocks)
{
    public IReadOnlyList<string> CanceledClockIds { get; init; } = [];
    public IReadOnlySet<string>? ResetClockIds { get; init; }
    public IReadOnlyDictionary<string, string?>? SettlingTasks { get; init; }
}

public sealed record AreaTrackCounts(int OpenCount, int ClosedCount);

public sealed record TaskSettlementResult(bool QueueChanged, AgentSessionDto? Session = null);

public interface ITrackReadStore
{
    string DatabaseId => string.Empty;
    Task<TrackMedia?> OpenTrackMediaAsync(string trackId, string fileName, CancellationToken ct) =>
        Task.FromResult<TrackMedia?>(null);

    Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct, bool includeSystem = false);
    Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct);
    Task<IReadOnlyDictionary<string, AreaTrackCounts>> GetAreaTrackCountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TrackDto>> ListTracksAsync(string? areaId = null, bool includeClosed = false, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> GetReportSummariesAsync(IReadOnlyCollection<string> trackIds, CancellationToken ct = default);
    Task<TrackDto?> GetTrackAsync(string trackId, CancellationToken ct);
    Task<TrackAggregateSnapshot?> LoadTrackAggregateAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(string trackId, int docRev, CancellationToken ct);
    Task<IReadOnlyList<SessionItemDto>> ListSessionItemsAsync(string sessionId, long afterId = 0, int limit = 100, CancellationToken ct = default, bool latest = false, long? beforeId = null);
    async Task<(IReadOnlyList<SessionItemDto> Items, bool HasMoreBefore)> ListSessionItemsPageAsync(
        string sessionId,
        long afterId = 0,
        int limit = 100,
        CancellationToken ct = default,
        bool latest = false,
        long? beforeId = null)
    {
        var items = await ListSessionItemsAsync(sessionId, afterId, limit + 1, ct, latest, beforeId).ConfigureAwait(false);
        var reverse = latest || beforeId is not null;
        if (reverse && items.Count > limit)
        {
            return (items.Skip(items.Count - limit).ToArray(), true);
        }

        return (items.Take(limit).ToArray(), false);
    }

    Task<MailDto?> FindReceivedMailAsync(string trackId, string mailId, CancellationToken ct);
    Task<IReadOnlyList<MailDto>> ListMailsAsync(string trackId, bool unreadOnly = false, CancellationToken ct = default);
    Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(string trackId, CancellationToken ct);
    Task<long> GetEventTipIdAsync(CancellationToken ct);
    Task<IReadOnlyList<EventEnvelopeDto>> ListEventsSinceAsync(long sinceEventId, string? trackId = null, int limit = 500, CancellationToken ct = default);
    Task<ReadReceiptsResponseDto> GetReadReceiptsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TrackOverlayDto>> ListTrackOverlaysAsync(bool includeClosed = false, CancellationToken ct = default);
    Task<string?> FindTrackIdBySessionAsync(string sessionId, CancellationToken ct);
    Task<string?> FindTrackIdByTerminalAsync(string terminalId, CancellationToken ct) => Task.FromResult<string?>(null);
    Task<IReadOnlyList<StoredExecutionTarget>> ListExecutionTargetsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<StoredExecutionTarget>>([]);
}

public interface ITrackStore : ITrackReadStore
{
    Task<StoredExecutionTarget> UpsertExecutionTargetAsync(StoredExecutionTarget target, CancellationToken ct = default)
        => throw new NotSupportedException("Execution target persistence is not supported by this store.");
    Task<bool> DeleteExecutionTargetAsync(string targetId, CancellationToken ct = default)
        => throw new NotSupportedException("Execution target persistence is not supported by this store.");

    Task<ReadReceiptDto> MarkReadAsync(string kind, string targetId, long readThroughMs, CancellationToken ct = default);
    Task<ReadReceiptDto> MarkUnreadAsync(string kind, string targetId, CancellationToken ct = default);

    Task<AreaDto> UpsertAreaAsync(AreaDto area, CancellationToken ct);
    Task<string?> FindCreationAsync(ResourceCreationKey key, CancellationToken ct);
    Task<string> CreateAreaAsync(AreaDto area, ResourceCreationKey? key, CancellationToken ct, EventEnvelopeDto? evt = null);
    Task DeleteAreaAsync(string areaId, CancellationToken ct, bool requireEmpty = false);

    Task<string> CreateTrackAsync(TrackCreation creation, ResourceCreationKey? key, CancellationToken ct);
    Task<WorkspacePreparation?> GetWorkspacePreparationAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<PersistedSessionInput>> ListAcceptedTrackInputsAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<string>> RecoverWorkspacePreparationsAsync(CancellationToken ct);
    Task<bool> TryClaimWorkspacePreparationAsync(string trackId, WorkspacePreparationPhase expected, WorkspacePreparation claimed, CancellationToken ct);
    Task SaveWorkspacePreparationAsync(string trackId, WorkspacePreparation preparation, TrackDto? readyTrack, EventEnvelopeDto? evt, CancellationToken ct);
    Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct, bool writeData = false, Action? beforeWrite = null);
    Task RecordFlowFileHashAsync(string trackId, long flowRev, string sha256, CancellationToken ct);
    Task<IReadOnlyDictionary<string, TaskSettlementResult>> CommitTrackFlowAsync(
        TrackDto track,
        long expectedFlowRev,
        IReadOnlyList<TaskDto> upsertedTasks,
        IReadOnlyList<TrackScheduleDto> activeFlowSchedules,
        IReadOnlyList<string> canceledFlowScheduleIds,
        IReadOnlyList<EventEnvelopeDto> events,
        CancellationToken ct,
        Action? beforeWrite = null,
        IReadOnlyDictionary<string, string?>? settlingTasks = null) =>
        throw new NotSupportedException("Flow commit is not implemented by this store.");
    Task<IReadOnlyDictionary<string, TaskSettlementResult>> UpdateTrackTemplateAndEmitAsync(TrackDto track, string? templateSnapshot, EventEnvelopeDto evt, CancellationToken ct, bool writeData = false, TrackFlowUpdate? flow = null, TemplateReportSeed? reportSeed = null, Action? beforeWrite = null);
    Task DeleteTrackAsync(string trackId, CancellationToken ct);

    Task SaveReportAsync(
        string trackId,
        ReportDocument report,
        byte[] compressedRevisionBlocks,
        string? authorSessionId,
        string commitMessage,
        EventEnvelopeDto evt,
        CancellationToken ct,
        Action? beforeWrite = null);

    Task UpsertTaskAsync(TaskDto task, EventEnvelopeDto? evt, CancellationToken ct,
        AgentSessionDto? session = null, EventEnvelopeDto? sessionEvent = null,
        SessionMessage? initialMessage = null, bool writeNewRunBoundary = false);
    Task<TaskSettlementResult> SettleTaskAsync(TaskDto task, EventEnvelopeDto? evt, CancellationToken ct,
        string? interruptOwnedSessionId = null, string? wakePrompt = null, bool suppressDefaultWake = false,
        string? wakeCoalesceKey = null);
    Task<bool> HasSessionTurnAsync(string sessionId, string turnId, CancellationToken ct);
    Task<IReadOnlyList<SessionTurnDto>> ListSessionTurnsAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionTurnDto>>([]);
    Task<int> GetLastTurnIndexAsync(string sessionId, CancellationToken ct);
    ISessionInputStore SessionInputs { get; }
    ITrackWakeStore TrackWakes { get; }
    ITrackScheduleStore? TrackSchedules => null;
    ISessionMessageStore? SessionMessages => null;
    Task<IReadOnlyList<(string TrackId, PersistedSessionInput Input)>> RecoverSessionInputsAsync(CancellationToken ct);
    Task UpsertSessionAsync(AgentSessionDto session, EventEnvelopeDto? evt, CancellationToken ct);
    Task CompleteSessionResumeAsync(AgentSessionDto session, PersistedSessionInput? unresolved, EventEnvelopeDto sessionEvent, int expectedEpoch, CancellationToken ct, SessionItemDto? boundary = null);
    Task RecordTurnAndItemsAsync(SessionTurnDto turn, IReadOnlyList<SessionItemDto> newItems, AgentSessionDto? updatedSession, CancellationToken ct);
    Task<(SessionTurnDto Turn, IReadOnlyList<long> ItemIds)> RecordTurnAndItemsWithIdsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct = default, SessionMessage? dispatchMessage = null);
    Task<IReadOnlyList<long>> AppendSessionItemsAsync(
        string sessionId,
        IReadOnlyList<SessionItemDto> newItems,
        CancellationToken ct = default, EventEnvelopeDto? evt = null);
    Task UpsertAskAsync(AskDto ask, EventEnvelopeDto evt, CancellationToken ct);
    Task UpsertMailAsync(MailDto mail, EventEnvelopeDto evt, CancellationToken ct, bool requestWake = true, Action? beforeWrite = null);
    Task UpsertTerminalAsync(TerminalDto terminal, EventEnvelopeDto? evt, CancellationToken ct);
    Task DeleteTerminalAsync(string trackId, string terminalId, EventEnvelopeDto? evt, CancellationToken ct);
    Task AppendEventAsync(EventEnvelopeDto evt, CancellationToken ct);
    Task<IReadOnlyList<string>> MarkInFlightInterruptedOnBootAsync(CancellationToken ct);
    Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(long nowMs, CancellationToken ct);
}
