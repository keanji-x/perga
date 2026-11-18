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
    string? WorkspacePreparation = null);

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
    WorkspacePreparationPhase Phase = WorkspacePreparationPhase.Pending, WorkspaceSpec? Spec = null);

public sealed record TrackCreation(TrackDto Track, ReportDocument Report, string? TemplateSnapshot,
    AgentSessionDto Lead, IReadOnlyList<TaskDto> Tasks, SessionInputRequest? FirstInput,
    WorkspacePreparation? Preparation, IReadOnlyList<EventEnvelopeDto> Events);

public interface ITrackReadStore
{
    Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct);
    Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct);
    Task<IReadOnlyList<TrackDto>> ListTracksAsync(string? areaId = null, bool includeClosed = false, CancellationToken ct = default);
    Task<TrackDto?> GetTrackAsync(string trackId, CancellationToken ct);
    Task<TrackAggregateSnapshot?> LoadTrackAggregateAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(string trackId, int docRev, CancellationToken ct);
    Task<IReadOnlyList<TaskAttemptDto>> ListAttemptsForTaskAsync(string taskId, CancellationToken ct);
    Task<IReadOnlyList<SessionItemDto>> ListSessionItemsAsync(string sessionId, long afterId = 0, int limit = 100, CancellationToken ct = default, bool latest = false, long? beforeId = null);
    Task<IReadOnlyList<MailDto>> ListPendingMailWakesAsync(CancellationToken ct);
    Task<MailDto?> FindReceivedMailAsync(string trackId, string mailId, CancellationToken ct);
    Task<IReadOnlyList<MailDto>> ListMailsAsync(string trackId, bool unreadOnly = false, CancellationToken ct = default);
    Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(string trackId, CancellationToken ct);
    Task<long> GetEventTipIdAsync(CancellationToken ct);
    Task<IReadOnlyList<EventEnvelopeDto>> ListEventsSinceAsync(long sinceEventId, string? trackId = null, int limit = 500, CancellationToken ct = default);
    Task<string?> FindTrackIdBySessionAsync(string sessionId, CancellationToken ct);
}

public interface ITrackStore : ITrackReadStore
{
    Task<AreaDto> UpsertAreaAsync(AreaDto area, CancellationToken ct);
    Task<string?> FindCreationAsync(ResourceCreationKey key, CancellationToken ct);
    Task<string> CreateAreaAsync(AreaDto area, ResourceCreationKey? key, CancellationToken ct);
    Task DeleteAreaAsync(string areaId, CancellationToken ct, bool requireEmpty = false);

    Task<string> CreateTrackAsync(TrackCreation creation, ResourceCreationKey? key, CancellationToken ct);
    Task<WorkspacePreparation?> GetWorkspacePreparationAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<PersistedSessionInput>> ListAcceptedTrackInputsAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<string>> RecoverWorkspacePreparationsAsync(CancellationToken ct);
    Task<bool> TryClaimWorkspacePreparationAsync(string trackId, WorkspacePreparationPhase expected, WorkspacePreparation claimed, CancellationToken ct);
    Task SaveWorkspacePreparationAsync(string trackId, WorkspacePreparation preparation, TrackDto? readyTrack, EventEnvelopeDto? evt, CancellationToken ct);
    Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct);
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
        CancellationToken ct);

    Task UpsertTaskAndAttemptAsync(TaskDto task, TaskAttemptDto? attempt, JsonElement? nextTemplateState, EventEnvelopeDto? evt, CancellationToken ct);
    Task<bool> HasSessionTurnAsync(string sessionId, string turnId, CancellationToken ct);
    Task<int> GetLastTurnIndexAsync(string sessionId, CancellationToken ct);
    ISessionInputStore SessionInputs { get; }
    Task<IReadOnlyList<(string TrackId, PersistedSessionInput Input)>> RecoverSessionInputsAsync(CancellationToken ct);
    Task UpsertSessionAsync(AgentSessionDto session, EventEnvelopeDto? evt, CancellationToken ct);
    Task RecordTurnAndItemsAsync(SessionTurnDto turn, IReadOnlyList<SessionItemDto> newItems, AgentSessionDto? updatedSession, CancellationToken ct);
    Task UpsertAskAsync(AskDto ask, EventEnvelopeDto evt, CancellationToken ct);
    Task AcknowledgeMailWakeAsync(string mailId, CancellationToken ct);
    Task UpsertMailAsync(MailDto mail, EventEnvelopeDto evt, CancellationToken ct, bool requestWake = true);
    Task UpsertTerminalAsync(TerminalDto terminal, EventEnvelopeDto? evt, CancellationToken ct);
    Task AppendEventAsync(EventEnvelopeDto evt, CancellationToken ct);
    Task<IReadOnlyList<string>> MarkInFlightInterruptedOnBootAsync(CancellationToken ct);
    Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(long nowMs, CancellationToken ct);
}
