using System.Text.Json;
using Perga.Contracts;
using Perga.ReportDoc;

namespace Perga.Kernel.Ports;

public sealed record TrackAggregateSnapshot(
    TrackDto Track,
    ReportDocument Report,
    IReadOnlyList<TaskDto> Tasks,
    IReadOnlyList<AgentSessionDto> Sessions,
    IReadOnlyList<AskDto> OpenAsks,
    string? TemplateSnapshotSource);

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
    Task<IReadOnlyList<EventEnvelopeDto>> ListEventsSinceAsync(long sinceEventId, string? trackId = null, int limit = 500, CancellationToken ct = default);
    Task<string?> FindTrackIdBySessionAsync(string sessionId, CancellationToken ct);
}

public interface ITrackStore : ITrackReadStore
{
    Task<AreaDto> UpsertAreaAsync(AreaDto area, CancellationToken ct);
    Task DeleteAreaAsync(string areaId, CancellationToken ct);

    Task CreateTrackAsync(TrackDto track, ReportDocument initialReport, string? templateSnapshot, AgentSessionDto initialLeadSession, CancellationToken ct);
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
