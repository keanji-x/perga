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
    public IReadOnlyList<TrackScheduleDto> TemplateSchedules { get; init; } = [];
}

public sealed record WorkerDispatchIntent(string TrackId, string AuthorId, string RequestKey, string RequestHash, string TaskKey);
public sealed record StoredWorkerDispatch(string RequestHash, string TaskId);
public sealed record ProviderSessionResumeFence(string SessionId, string TurnId, string NativeId, bool Quiescent);

public sealed record InterruptedAttemptLeaseDto(
    string AttemptId, string TaskId, string TrackId, JsonElement WorkspaceLease);

public interface ITrackReadStore
{
    string DatabaseId => string.Empty;

    Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct);
    Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct);
    Task<IReadOnlyList<TrackDto>> ListTracksAsync(string? areaId = null, bool includeClosed = false, CancellationToken ct = default);
    Task<TrackDto?> GetTrackAsync(string trackId, CancellationToken ct);
    Task<TrackAggregateSnapshot?> LoadTrackAggregateAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(string trackId, int docRev, CancellationToken ct);
    Task<IReadOnlyList<TaskAttemptDto>> ListAttemptsForTaskAsync(string taskId, CancellationToken ct);
    Task<IReadOnlyList<ProviderSessionResumeFence>> ListSessionResumeFencesAsync(string trackId, CancellationToken ct);
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
}

public interface ITrackStore : ITrackReadStore
{
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
    Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct);
    Task UpdateTrackTemplateAndEmitAsync(TrackDto track, string? templateSnapshot, EventEnvelopeDto evt, CancellationToken ct, IReadOnlyList<TrackScheduleDto>? templateSchedules = null, bool refreshPromptsOnly = false);
    Task DeleteTrackAsync(string trackId, CancellationToken ct);

    Task SaveReportAndTasksAsync(
        string trackId,
        ReportDocument report,
        byte[] compressedRevisionBlocks,
        string? authorSessionId,
        string commitMessage,
        IReadOnlyList<TaskDto> syncedTasks,
        JsonElement nextTemplateState,
        EventEnvelopeDto evt,
        CancellationToken ct,
        WorkerDispatchIntent? dispatch = null);

    Task<StoredWorkerDispatch?> FindWorkerDispatchAsync(WorkerDispatchIntent dispatch, CancellationToken ct) =>
        Task.FromResult<StoredWorkerDispatch?>(null);
    Task<IReadOnlyList<string>> ListPendingWorkerDispatchTracksAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    Task UpsertTaskAndAttemptAsync(TaskDto task, TaskAttemptDto? attempt, JsonElement? nextTemplateState, EventEnvelopeDto? evt, CancellationToken ct,
        AgentSessionDto? session = null, EventEnvelopeDto? sessionEvent = null, string? wakePrompt = null);
    Task<bool> HasSessionTurnAsync(string sessionId, string turnId, CancellationToken ct);
    Task<IReadOnlyList<SessionTurnDto>> ListSessionTurnsAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SessionTurnDto>>([]);
    Task<int> GetLastTurnIndexAsync(string sessionId, CancellationToken ct);
    ISessionInputStore SessionInputs { get; }
    ITrackWakeStore TrackWakes { get; }
    ITrackScheduleStore? TrackSchedules => null;
    ISessionMessageStore? SessionMessages => null;
    IWorkerSettingsStore? WorkerSettings => null;
    Task<IReadOnlyList<(string TrackId, PersistedSessionInput Input)>> RecoverSessionInputsAsync(CancellationToken ct);
    Task UpsertSessionAsync(AgentSessionDto session, EventEnvelopeDto? evt, CancellationToken ct);
    Task SaveSessionResumeFenceAsync(ProviderSessionResumeFence fence, CancellationToken ct);
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
        CancellationToken ct = default);
    Task UpsertAskAsync(AskDto ask, EventEnvelopeDto evt, CancellationToken ct);
    Task UpsertMailAsync(MailDto mail, EventEnvelopeDto evt, CancellationToken ct, bool requestWake = true);
    Task UpsertTerminalAsync(TerminalDto terminal, EventEnvelopeDto? evt, CancellationToken ct);
    Task DeleteTerminalAsync(string trackId, string terminalId, EventEnvelopeDto? evt, CancellationToken ct);
    Task AppendEventAsync(EventEnvelopeDto evt, CancellationToken ct);
    Task<IReadOnlyList<InterruptedAttemptLeaseDto>> ListInFlightAttemptLeasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> MarkInFlightInterruptedOnBootAsync(CancellationToken ct);
    Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(long nowMs, CancellationToken ct);
}
