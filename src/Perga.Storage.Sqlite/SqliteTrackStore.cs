using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Storage.Sqlite;

/// <summary>SQLite port facade. Domain repositories share one storage lifetime and writer.</summary>
public sealed class SqliteTrackStore : ITrackStore, IAsyncDisposable, ISessionInputStore, ISessionMessageStore, ITrackScheduleStore, ITrackWakeStore, ITodayReadStore, ISystemUpgradeStore, IAuthStore
{
    private readonly SqliteStoreContext _context;
    private readonly AuthRepository _authRepository;
    private readonly MaintenanceRepository _maintenanceRepository;
    private readonly SessionInputsRepository _sessionInputsRepository;
    private readonly TodayRepository _todayRepository;
    private readonly ReadReceiptsRepository _readReceiptsRepository;
    private readonly SessionMessagesRepository _sessionMessagesRepository;
    private readonly SystemUpgradeRepository _systemUpgradeRepository;
    private readonly ExecutionTargetsRepository _executionTargetsRepository;
    private readonly SessionResumeRepository _sessionResumeRepository;
    private readonly AreaRepository _areaRepository;
    private readonly TrackCreationRepository _trackCreationRepository;
    private readonly TrackReadRepository _trackReadRepository;
    private readonly TrackUpdateRepository _trackUpdateRepository;
    private readonly TrackDeletionRepository _trackDeletionRepository;
    private readonly ReportRepository _reportRepository;
    private readonly TaskRepository _taskRepository;
    private readonly SessionRepository _sessionRepository;
    private readonly CorrespondenceRepository _correspondenceRepository;
    private readonly TerminalRepository _terminalRepository;
    private readonly EventRepository _eventRepository;
    private readonly OverlayRepository _overlayRepository;
    private readonly BootRecoveryRepository _bootRecoveryRepository;
    private readonly TrackSchedulesRepository _trackSchedulesRepository;
    private readonly TrackWakesRepository _trackWakesRepository;
    private readonly CreationRepository _creationRepository;

    public SqliteTrackStore(
        string dbPath,
        string? dataDir = null,
        TimeProvider? timeProvider = null)
        : this(
            new SqliteStorageOptions
            {
                DbPath = dbPath,
                DataDir = dataDir
            },
            migrator: null,
            timeProvider: timeProvider)
    {
    }

    public SqliteTrackStore(
        SqliteStorageOptions options,
        SqliteMigrator? migrator = null,
        TimeProvider? timeProvider = null,
        SqliteJsonUpcaster? upcaster = null)
    {
        _context = new SqliteStoreContext(options, migrator, timeProvider, upcaster);
        _authRepository = new AuthRepository(_context);
        _maintenanceRepository = new MaintenanceRepository(_context);
        _sessionRepository = new SessionRepository(_context);
        _taskRepository = new TaskRepository(_context, _sessionRepository);
        _trackReadRepository = new TrackReadRepository(_context, _taskRepository);
        _sessionInputsRepository = new SessionInputsRepository(_context, _sessionRepository, _trackReadRepository);
        _todayRepository = new TodayRepository(_context);
        _readReceiptsRepository = new ReadReceiptsRepository(_context);
        _sessionMessagesRepository = new SessionMessagesRepository(_context, _sessionRepository);
        _correspondenceRepository = new CorrespondenceRepository(_context);
        _systemUpgradeRepository = new SystemUpgradeRepository(_context, _correspondenceRepository);
        _executionTargetsRepository = new ExecutionTargetsRepository(_context);
        _sessionResumeRepository = new SessionResumeRepository(_context, _sessionRepository);
        _areaRepository = new AreaRepository(_context);
        _trackCreationRepository = new TrackCreationRepository(_context, _taskRepository);
        _trackUpdateRepository = new TrackUpdateRepository(_context, _taskRepository);
        _trackDeletionRepository = new TrackDeletionRepository(_context);
        _reportRepository = new ReportRepository(_context);
        _terminalRepository = new TerminalRepository(_context);
        _eventRepository = new EventRepository(_context);
        _overlayRepository = new OverlayRepository(_context);
        _bootRecoveryRepository = new BootRecoveryRepository(_context);
        _trackSchedulesRepository = new TrackSchedulesRepository(_context, _taskRepository);
        _trackWakesRepository = new TrackWakesRepository(_context, _sessionRepository);
        _creationRepository = new CreationRepository(_context);
    }

    public SqliteStorageOptions Options => _context.Options;
    public SqliteJsonUpcaster Upcaster => _context.Upcaster;
    public string DatabaseId => _context.DatabaseId;

    public static async Task<SqliteTrackStore> OpenAsync(
        string dbPath,
        string? dataDir = null,
        CancellationToken ct = default)
    {
        var store = new SqliteTrackStore(dbPath, dataDir);
        try
        {
            await store.InitializeAsync(ct).ConfigureAwait(false);
            return store;
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task InitializeAsync(CancellationToken ct = default) => _context.InitializeAsync(ct);
    public ValueTask DisposeAsync() => _context.DisposeAsync();

    public ISessionInputStore SessionInputs => this;
    public ISessionMessageStore SessionMessages => this;
    public ITrackScheduleStore? TrackSchedules => this;
    public ITrackWakeStore TrackWakes => this;

    public Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(long nowMs, CancellationToken ct) =>
        _maintenanceRepository.RunMaintenancePassAsync(nowMs, ct);

    public Task<int> PruneTrackWakesAsync(long nowMs, CancellationToken ct) =>
        _maintenanceRepository.PruneTrackWakesAsync(nowMs, ct);

    public Task<int> PruneTrackSchedulesAsync(long nowMs, CancellationToken ct) =>
        _maintenanceRepository.PruneTrackSchedulesAsync(nowMs, ct);

    public Task<int> PruneAuthSessionsAsync(long nowMs, CancellationToken ct) =>
        _maintenanceRepository.PruneAuthSessionsAsync(nowMs, ct);

    public Task<bool> HasSessionTurnAsync(string sessionId, string turnId, CancellationToken ct) =>
        _sessionInputsRepository.HasSessionTurnAsync(sessionId, turnId, ct);

    public Task<int> GetLastTurnIndexAsync(string sessionId, CancellationToken ct) =>
        _sessionInputsRepository.GetLastTurnIndexAsync(sessionId, ct);

    public Task<PersistedSessionInput?> FindByKeyAsync(string sessionId, string key, CancellationToken ct) =>
        _sessionInputsRepository.FindByKeyAsync(sessionId, key, ct);

    public Task<PersistedSessionInput?> FindByIdAsync(string sessionId, string inputId, CancellationToken ct) =>
        _sessionInputsRepository.FindByIdAsync(sessionId, inputId, ct);

    public Task<PersistedSessionInput?> FindUnresolvedAsync(string sessionId, CancellationToken ct) =>
        _sessionInputsRepository.FindUnresolvedAsync(sessionId, ct);

    public Task<IReadOnlyList<PersistedSessionInput>> ListAcceptedTrackInputsAsync(string trackId, CancellationToken ct) =>
        _sessionInputsRepository.ListAcceptedTrackInputsAsync(trackId, ct);

    public Task<StoredSessionCreation?> FindCreationAsync(SessionCreateIntent intent, CancellationToken ct) =>
        _sessionInputsRepository.FindCreationAsync(intent, ct);

    public Task<PersistedSessionInput> AcceptAsync(
        AgentSessionDto session, SessionInputRequest request, EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _sessionInputsRepository.AcceptAsync(session, request, sessionEvent, ct);

    public Task<PersistedSessionInput?> PromoteQueuedBatchAsync(AgentSessionDto session, SessionMessage message,
        EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _sessionInputsRepository.PromoteQueuedBatchAsync(session, message, sessionEvent, ct);

    public Task<PersistedSessionInput?> CreateAsync(
        SessionCreateIntent intent, AgentSessionDto session, SessionInputRequest? firstInput,
        EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _sessionInputsRepository.CreateAsync(intent, session, firstInput, sessionEvent, ct);

    public Task<bool> ClaimAsync(
        PersistedSessionInput input, AgentSessionDto session, EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _sessionInputsRepository.ClaimAsync(input, session, sessionEvent, ct);

    public Task MarkDeliveredAsync(PersistedSessionInput input, string? nativeThreadId, string? providerTurnId, CancellationToken ct) =>
        _sessionInputsRepository.MarkDeliveredAsync(input, nativeThreadId, providerTurnId, ct);

    public Task SettleAsync(
        PersistedSessionInput input, string delivery, SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
        AgentSessionDto session, IReadOnlyList<EventEnvelopeDto> events, CancellationToken ct) =>
        _sessionInputsRepository.SettleAsync(input, delivery, turn, items, session, events, ct);

    public Task ResolveAsync(
        PersistedSessionInput input, AgentSessionDto session, EventEnvelopeDto sessionEvent, CancellationToken ct) =>
        _sessionInputsRepository.ResolveAsync(input, session, sessionEvent, ct);

    public Task<IReadOnlyList<(string TrackId, PersistedSessionInput Input)>> RecoverSessionInputsAsync(CancellationToken ct) =>
        _sessionInputsRepository.RecoverSessionInputsAsync(ct);

    public Task<string?> GetDailyTrackIdAsync(string date, CancellationToken ct) =>
        _todayRepository.GetDailyTrackIdAsync(date, ct);

    public Task<IReadOnlyList<DailyTrackCalendarPresence>> ListDailyTrackPresenceAsync(
        IReadOnlyList<string> dates, CancellationToken ct) =>
        _todayRepository.ListDailyTrackPresenceAsync(dates, ct);

    public Task<IReadOnlyList<TodayScheduleEntry>> ListCalendarSchedulesInWindowAsync(
        long startMs, long endMs, CancellationToken ct) =>
        _todayRepository.ListCalendarSchedulesInWindowAsync(startMs, endMs, ct);

    public Task<IReadOnlyList<DailyReportRevision>> ListDailyReportRevisionsAsync(
        long startMs, long endMs, long throughEventId, CancellationToken ct) =>
        _todayRepository.ListDailyReportRevisionsAsync(startMs, endMs, throughEventId, ct);

    public Task<ReadReceiptsResponseDto> GetReadReceiptsAsync(CancellationToken ct = default) =>
        _readReceiptsRepository.GetReadReceiptsAsync(ct);

    public Task<ReadReceiptDto> MarkReadAsync(string kind, string targetId, long readThroughMs, CancellationToken ct = default) =>
        _readReceiptsRepository.MarkReadAsync(kind, targetId, readThroughMs, ct);

    public Task<ReadReceiptDto> MarkUnreadAsync(string kind, string targetId, CancellationToken ct = default) =>
        _readReceiptsRepository.MarkUnreadAsync(kind, targetId, ct);

    Task<SessionMessage?> ISessionMessageStore.FindAsync(string sessionId, string authorId, string requestKey, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).FindAsync(sessionId, authorId, requestKey, ct);

    Task<SessionMessage?> ISessionMessageStore.GetAsync(string sessionId, string messageId, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).GetAsync(sessionId, messageId, ct);

    Task<SessionMessage?> ISessionMessageStore.NextAsync(string sessionId, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).NextAsync(sessionId, ct);

    Task ISessionMessageStore.InsertAsync(SessionMessage message, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).InsertAsync(message, ct);

    Task<bool> ISessionMessageStore.TransitionAsync(SessionMessage message, string expected, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).TransitionAsync(message, expected, ct);

    Task<string?> ISessionMessageStore.FindQueuedSteerAsync(SessionMessage message, string turnId, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).FindQueuedSteerAsync(message, turnId, ct);

    Task<bool> ISessionMessageStore.ClaimQueuedSteerAsync(SessionMessage message, string turnId, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).ClaimQueuedSteerAsync(message, turnId, ct);

    Task<string> ISessionMessageStore.SettleQueuedSteerAsync(SessionMessage message, string turnId, string outcome,
        SessionItemDto? item, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).SettleQueuedSteerAsync(message, turnId, outcome, item, ct);

    Task<bool> ISessionMessageStore.DeliverSteerAsync(SessionMessage message, SessionItemDto item, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).DeliverSteerAsync(message, item, ct);

    Task<IReadOnlyList<SessionMessage>> ISessionMessageStore.ListQueueAsync(string sessionId, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).ListQueueAsync(sessionId, ct);

    Task<IReadOnlyList<string>> ISessionMessageStore.ListHandoffsAsync(string sessionId, CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).ListHandoffsAsync(sessionId, ct);

    Task<IReadOnlyList<(string TrackId, string SessionId)>> ISessionMessageStore.ListRecoverableQueuesAsync(CancellationToken ct) =>
        ((ISessionMessageStore)_sessionMessagesRepository).ListRecoverableQueuesAsync(ct);

    public Task<int> GetSchemaVersionAsync(CancellationToken ct) =>
        _systemUpgradeRepository.GetSchemaVersionAsync(ct);

    public Task<IReadOnlyList<string>> PrepareUpgradeNotificationRetriesAsync(CancellationToken ct) =>
        _systemUpgradeRepository.PrepareUpgradeNotificationRetriesAsync(ct);

    public Task AcknowledgeUpgradeNotificationAsync(string mailId, CancellationToken ct) =>
        _systemUpgradeRepository.AcknowledgeUpgradeNotificationAsync(mailId, ct);

    public Task MarkUpgradeNotificationFailedAsync(string mailId, string error, CancellationToken ct) =>
        _systemUpgradeRepository.MarkUpgradeNotificationFailedAsync(mailId, error, ct);

    public Task<bool> ConsumeUpgradeReceiptAsync(SystemUpgradeReceipt receipt, CancellationToken ct) =>
        _systemUpgradeRepository.ConsumeUpgradeReceiptAsync(receipt, ct);

    public Task<IReadOnlyList<StoredExecutionTarget>> ListExecutionTargetsAsync(CancellationToken ct = default) =>
        _executionTargetsRepository.ListExecutionTargetsAsync(ct);

    public Task<StoredExecutionTarget> UpsertExecutionTargetAsync(StoredExecutionTarget target, CancellationToken ct = default) =>
        _executionTargetsRepository.UpsertExecutionTargetAsync(target, ct);

    public Task<bool> DeleteExecutionTargetAsync(string targetId, CancellationToken ct = default) =>
        _executionTargetsRepository.DeleteExecutionTargetAsync(targetId, ct);

    public Task CompleteSessionResumeAsync(AgentSessionDto session, PersistedSessionInput? unresolved,
        EventEnvelopeDto sessionEvent, int expectedEpoch, CancellationToken ct, SessionItemDto? boundary = null) =>
        _sessionResumeRepository.CompleteSessionResumeAsync(session, unresolved, sessionEvent, expectedEpoch, ct, boundary);

    public Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct) =>
        _areaRepository.ListAreasAsync(ct);

    public Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct) =>
        _areaRepository.GetAreaAsync(areaId, ct);

    public Task<AreaDto> UpsertAreaAsync(AreaDto area, CancellationToken ct) =>
        _areaRepository.UpsertAreaAsync(area, ct);

    public Task DeleteAreaAsync(string areaId, CancellationToken ct, bool requireEmpty = false) =>
        _areaRepository.DeleteAreaAsync(areaId, ct, requireEmpty);

    public Task<string> CreateTrackAsync(TrackDto track, ReportDocument report, string? templateSnapshot,
        AgentSessionDto lead, CancellationToken ct) =>
        _trackCreationRepository.CreateTrackAsync(track, report, templateSnapshot, lead, ct);

    public Task<string> CreateTrackAsync(TrackCreation creation, ResourceCreationKey? key, CancellationToken ct) =>
        _trackCreationRepository.CreateTrackAsync(creation, key, ct);

    public Task<IReadOnlyList<TrackDto>> ListTracksAsync(
        string? areaId = null,
        bool includeClosed = false,
        CancellationToken ct = default) =>
        _trackReadRepository.ListTracksAsync(areaId, includeClosed, ct);

    public Task<TrackDto?> GetTrackAsync(string trackId, CancellationToken ct) =>
        _trackReadRepository.GetTrackAsync(trackId, ct);

    public Task<TrackAggregateSnapshot?> LoadTrackAggregateAsync(string trackId, CancellationToken ct) =>
        _trackReadRepository.LoadTrackAggregateAsync(trackId, ct);

    public Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct, bool writeData = false) =>
        _trackUpdateRepository.UpdateTrackAndEmitAsync(track, evt, ct, writeData);

    public Task UpdateTrackTemplateAndEmitAsync(TrackDto track, string? templateSnapshot, EventEnvelopeDto evt, CancellationToken ct, bool writeData = false, TrackFlowUpdate? flow = null) =>
        _trackUpdateRepository.UpdateTrackTemplateAndEmitAsync(track, templateSnapshot, evt, ct, writeData, flow);

    public Task RecordFlowFileHashAsync(string trackId, long flowRev, string sha256, CancellationToken ct) =>
        _trackUpdateRepository.RecordFlowFileHashAsync(trackId, flowRev, sha256, ct);

    public Task CommitTrackFlowAsync(
        TrackDto track,
        long expectedFlowRev,
        IReadOnlyList<TaskDto> upsertedTasks,
        IReadOnlyList<TrackScheduleDto> activeFlowSchedules,
        IReadOnlyList<string> canceledFlowScheduleIds,
        IReadOnlyList<EventEnvelopeDto> events,
        CancellationToken ct) =>
        _trackUpdateRepository.CommitTrackFlowAsync(track, expectedFlowRev, upsertedTasks, activeFlowSchedules, canceledFlowScheduleIds, events, ct);

    public Task DeleteTrackAsync(string trackId, CancellationToken ct) =>
        _trackDeletionRepository.DeleteTrackAsync(trackId, ct);

    public Task SaveReportAsync(
        string trackId,
        ReportDocument report,
        byte[] compressedRevisionBlocks,
        string? authorSessionId,
        string commitMessage,
        JsonElement nextTemplateState,
        EventEnvelopeDto evt,
        CancellationToken ct) =>
        _reportRepository.SaveReportAsync(trackId, report, compressedRevisionBlocks, authorSessionId, commitMessage, nextTemplateState, evt, ct);

    public Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(
        string trackId,
        int docRev,
        CancellationToken ct) =>
        _reportRepository.GetReportRevisionBlocksAsync(trackId, docRev, ct);

    public Task UpsertTaskAsync(
        TaskDto task,
        JsonElement? nextTemplateState,
        EventEnvelopeDto? evt,
        CancellationToken ct,
        AgentSessionDto? session = null,
        EventEnvelopeDto? sessionEvent = null,
        string? wakePrompt = null,
        SessionMessage? initialMessage = null,
        bool writeNewRunBoundary = false,
        bool suppressDefaultWake = false) =>
        _taskRepository.UpsertTaskAsync(task, nextTemplateState, evt, ct, session, sessionEvent, wakePrompt, initialMessage, writeNewRunBoundary, suppressDefaultWake);

    public Task UpsertSessionAsync(AgentSessionDto session, EventEnvelopeDto? evt, CancellationToken ct) =>
        _sessionRepository.UpsertSessionAsync(session, evt, ct);

    public Task<string?> FindTrackIdBySessionAsync(string sessionId, CancellationToken ct) =>
        _sessionRepository.FindTrackIdBySessionAsync(sessionId, ct);

    public Task RecordTurnAndItemsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct) =>
        _sessionRepository.RecordTurnAndItemsAsync(turn, newItems, updatedSession, ct);

    public Task<(SessionTurnDto Turn, IReadOnlyList<long> ItemIds)> RecordTurnAndItemsWithIdsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct = default, SessionMessage? dispatchMessage = null) =>
        _sessionRepository.RecordTurnAndItemsWithIdsAsync(turn, newItems, updatedSession, ct, dispatchMessage);

    public Task<IReadOnlyList<long>> AppendSessionItemsAsync(
        string sessionId,
        IReadOnlyList<SessionItemDto> newItems,
        CancellationToken ct = default) =>
        _sessionRepository.AppendSessionItemsAsync(sessionId, newItems, ct);

    public Task<IReadOnlyList<SessionTurnDto>> ListSessionTurnsAsync(
        string sessionId,
        CancellationToken ct = default) =>
        _sessionRepository.ListSessionTurnsAsync(sessionId, ct);

    public Task<IReadOnlyList<SessionItemDto>> ListSessionItemsAsync(
        string sessionId,
        long afterId = 0,
        int limit = 100,
        CancellationToken ct = default, bool latest = false, long? beforeId = null) =>
        _sessionRepository.ListSessionItemsAsync(sessionId, afterId, limit, ct, latest, beforeId);

    public Task UpsertAskAsync(AskDto ask, EventEnvelopeDto evt, CancellationToken ct) =>
        _correspondenceRepository.UpsertAskAsync(ask, evt, ct);

    public Task<AskDto?> GetAskAsync(string askId, CancellationToken ct = default) =>
        _correspondenceRepository.GetAskAsync(askId, ct);

    public Task UpsertMailAsync(MailDto mail, EventEnvelopeDto evt, CancellationToken ct, bool requestWake = true) =>
        _correspondenceRepository.UpsertMailAsync(mail, evt, ct, requestWake);

    public Task MarkMailReadAsync(string mailId, long readAtMs, CancellationToken ct = default) =>
        _correspondenceRepository.MarkMailReadAsync(mailId, readAtMs, ct);

    public Task AcknowledgeMailWakeAsync(string mailId, CancellationToken ct) =>
        _correspondenceRepository.AcknowledgeMailWakeAsync(mailId, ct);

    public Task<MailDto?> FindReceivedMailAsync(string trackId, string mailId, CancellationToken ct) =>
        _correspondenceRepository.FindReceivedMailAsync(trackId, mailId, ct);

    public Task<IReadOnlyList<MailDto>> ListMailsAsync(string trackId, bool unreadOnly = false, CancellationToken ct = default) =>
        _correspondenceRepository.ListMailsAsync(trackId, unreadOnly, ct);

    public Task UpsertTerminalAsync(TerminalDto terminal, EventEnvelopeDto? evt, CancellationToken ct) =>
        _terminalRepository.UpsertTerminalAsync(terminal, evt, ct);

    public Task DeleteTerminalAsync(string trackId, string terminalId, EventEnvelopeDto? evt, CancellationToken ct) =>
        _terminalRepository.DeleteTerminalAsync(trackId, terminalId, evt, ct);

    public Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(string trackId, CancellationToken ct) =>
        _terminalRepository.ListTerminalsAsync(trackId, ct);

    public Task<string?> FindTrackIdByTerminalAsync(string terminalId, CancellationToken ct) =>
        _terminalRepository.FindTrackIdByTerminalAsync(terminalId, ct);

    public Task AppendEventAsync(EventEnvelopeDto evt, CancellationToken ct) =>
        _eventRepository.AppendEventAsync(evt, ct);

    public Task<EventEnvelopeDto> AppendEventWithIdAsync(EventEnvelopeDto evt, CancellationToken ct = default) =>
        _eventRepository.AppendEventWithIdAsync(evt, ct);

    public Task<long> GetEventTipIdAsync(CancellationToken ct) =>
        _eventRepository.GetEventTipIdAsync(ct);

    public Task<IReadOnlyList<EventEnvelopeDto>> ListEventsSinceAsync(
        long sinceEventId,
        string? trackId = null,
        int limit = 500,
        CancellationToken ct = default) =>
        _eventRepository.ListEventsSinceAsync(sinceEventId, trackId, limit, ct);

    public Task<IReadOnlyList<TrackOverlayDto>> ListTrackOverlaysAsync(
        bool includeClosed = false,
        CancellationToken ct = default) =>
        _overlayRepository.ListTrackOverlaysAsync(includeClosed, ct);

    public Task<IReadOnlyList<string>> MarkInFlightInterruptedOnBootAsync(CancellationToken ct) =>
        _bootRecoveryRepository.MarkInFlightInterruptedOnBootAsync(ct);

    public Task<TrackScheduleDto?> GetAsync(string trackId, string scheduleId, CancellationToken ct) =>
        _trackSchedulesRepository.GetAsync(trackId, scheduleId, ct);

    public Task<IReadOnlyList<TrackScheduleDto>> ListForTrackAsync(string trackId, bool includeTerminal, CancellationToken ct) =>
        _trackSchedulesRepository.ListForTrackAsync(trackId, includeTerminal, ct);

    public Task<IReadOnlyList<FiredTrackSchedule>> ListDueFlowClocksAsync(long nowMs, CancellationToken ct) =>
        _trackSchedulesRepository.ListDueFlowClocksAsync(nowMs, ct);

    public Task<bool> CommitFlowFireAsync(TrackScheduleDto nextClock, long expectedDueAtMs, TaskDto? armedTask,
        EventEnvelopeDto taskEvent, CancellationToken ct) =>
        _trackSchedulesRepository.CommitFlowFireAsync(nextClock, expectedDueAtMs, armedTask, taskEvent, ct);

    public Task EnqueueAsync(TrackWakeRequest request, CancellationToken ct) =>
        _trackWakesRepository.EnqueueAsync(request, ct);

    public Task<IReadOnlyList<TrackWake>> ListAsync(string trackId, CancellationToken ct) =>
        _trackWakesRepository.ListAsync(trackId, ct);

    public Task<IReadOnlyList<string>> ListPendingTracksAsync(CancellationToken ct) =>
        _trackWakesRepository.ListPendingTracksAsync(ct);

    public Task<IReadOnlyList<TrackWake>> ClaimBatchAsync(string trackId, string targetSessionId, int limit, CancellationToken ct) =>
        _trackWakesRepository.ClaimBatchAsync(trackId, targetSessionId, limit, ct);

    public Task MarkDoneAsync(IReadOnlyList<TrackWake> batch, string reason, string turnId, CancellationToken ct) =>
        _trackWakesRepository.MarkDoneAsync(batch, reason, turnId, ct);

    public Task MarkDroppedAsync(IReadOnlyList<TrackWake> batch, string reason, string? turnId, CancellationToken ct) =>
        _trackWakesRepository.MarkDroppedAsync(batch, reason, turnId, ct);

    public Task LinkTurnAsync(IReadOnlyList<TrackWake> batch, string turnId, CancellationToken ct) =>
        _trackWakesRepository.LinkTurnAsync(batch, turnId, ct);

    public Task<bool> CancelPendingWakeAsync(string trackId, string wakeId, CancellationToken ct) =>
        _trackWakesRepository.CancelPendingWakeAsync(trackId, wakeId, ct);

    public Task<IReadOnlyList<string>> RecoverOnBootAsync(CancellationToken ct) =>
        _trackWakesRepository.RecoverOnBootAsync(ct);

    public Task<string?> FindCreationAsync(ResourceCreationKey key, CancellationToken ct) =>
        _creationRepository.FindCreationAsync(key, ct);

    public Task<string> CreateAreaAsync(AreaDto area, ResourceCreationKey? key, CancellationToken ct, EventEnvelopeDto? evt = null) =>
        _creationRepository.CreateAreaAsync(area, key, ct, evt);

    public Task<WorkspacePreparation?> GetWorkspacePreparationAsync(string trackId, CancellationToken ct) =>
        _creationRepository.GetWorkspacePreparationAsync(trackId, ct);

    public Task<IReadOnlyList<string>> RecoverWorkspacePreparationsAsync(CancellationToken ct) =>
        _creationRepository.RecoverWorkspacePreparationsAsync(ct);

    public Task<bool> TryClaimWorkspacePreparationAsync(string trackId, WorkspacePreparationPhase expected, WorkspacePreparation claimed, CancellationToken ct) =>
        _creationRepository.TryClaimWorkspacePreparationAsync(trackId, expected, claimed, ct);

    public Task SaveWorkspacePreparationAsync(string trackId, WorkspacePreparation preparation, TrackDto? readyTrack, EventEnvelopeDto? evt, CancellationToken ct) =>
        _creationRepository.SaveWorkspacePreparationAsync(trackId, preparation, readyTrack, evt, ct);

    public Task<AuthUserRecord?> GetOwnerAsync(CancellationToken ct) =>
        _authRepository.GetOwnerAsync(ct);

    public Task<SessionIdentityDto?> TryCreateSessionIfVersionMatchesAsync(AuthUserRecord owner, NewAuthSession session, CancellationToken ct) =>
        _authRepository.TryCreateSessionIfVersionMatchesAsync(owner, session, ct);

    public Task<bool> UpgradePasswordHashIfVersionMatchesAsync(AuthUserRecord owner, string hash, long nowMs, CancellationToken ct) =>
        _authRepository.UpgradePasswordHashIfVersionMatchesAsync(owner, hash, nowMs, ct);

    public Task<SessionIdentityDto?> GetValidOwnerSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct) =>
        _authRepository.GetValidOwnerSessionByTokenHashAsync(tokenHash, nowMs, ct);

    public Task<bool> IsOwnerSessionValidAsync(string id, long nowMs, CancellationToken ct) =>
        _authRepository.IsOwnerSessionValidAsync(id, nowMs, ct);

    public Task TouchSessionLastSeenIfStaleAsync(string id, long nowMs, CancellationToken ct) =>
        _authRepository.TouchSessionLastSeenIfStaleAsync(id, nowMs, ct);

    public Task RevokeSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct) =>
        _authRepository.RevokeSessionByTokenHashAsync(tokenHash, nowMs, ct);

    public Task<SystemAuthStatusDto> GetAuthStatusAsync(long nowMs, CancellationToken ct) =>
        _authRepository.GetAuthStatusAsync(nowMs, ct);
}
