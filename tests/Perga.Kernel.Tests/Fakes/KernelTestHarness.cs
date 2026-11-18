using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Kernel.Tests.Fakes;

internal static class TestJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static JsonElement FromObject<T>(T value) =>
        JsonSerializer.SerializeToElement(value, Options).Clone();
}

internal sealed class FakeManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta)
    {
        _utcNow = _utcNow.Add(delta);
    }
}

internal sealed class InMemoryTrackStore : ITrackStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AreaDto> _areas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackDto> _tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReportDocument> _reports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _templateSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<(string TrackId, int DocRev), byte[]> _revisions = new();
    private readonly Dictionary<string, List<TaskDto>> _tasksByTrack = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TaskAttemptDto>> _attemptsByTask = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentSessionDto> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SessionTurnDto>> _turnsBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SessionItemDto>> _itemsBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AskDto> _asks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MailDto>> _mailsByToTrack = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalDto> _terminals = new(StringComparer.Ordinal);
    private readonly List<EventEnvelopeDto> _events = [];
    private long _nextItemId = 1;
    private long _nextEventId = 1;

    public bool FailTemplateUpdate { get; set; }

    public int MarkInterruptedCalls { get; private set; }

    public IReadOnlyList<EventEnvelopeDto> PersistedEvents
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public IReadOnlyList<SessionTurnDto> GetTurnsForSession(string sessionId)
    {
        lock (_gate)
        {
            return _turnsBySession.TryGetValue(sessionId, out var list) ? list.ToArray() : [];
        }
    }

    public void SeedDirectAggregate(
        TrackDto track,
        ReportDocument report,
        IReadOnlyList<TaskDto> tasks,
        IReadOnlyList<TaskAttemptDto> attempts,
        IReadOnlyList<AgentSessionDto> sessions,
        IReadOnlyList<SessionTurnDto>? turns = null,
        string? templateSnapshot = null)
    {
        lock (_gate)
        {
            _tracks[track.Id] = track;
            _reports[track.Id] = report;
            _templateSnapshots[track.Id] = templateSnapshot;
            _tasksByTrack[track.Id] = tasks.ToList();

            foreach (var att in attempts)
            {
                if (!_attemptsByTask.TryGetValue(att.TaskId, out var attList))
                {
                    attList = [];
                    _attemptsByTask[att.TaskId] = attList;
                }

                attList.Add(att);
            }

            foreach (var ses in sessions)
            {
                _sessions[ses.Id] = ses;
            }

            if (turns is not null)
            {
                foreach (var trn in turns)
                {
                    if (!_turnsBySession.TryGetValue(trn.SessionId, out var tList))
                    {
                        tList = [];
                        _turnsBySession[trn.SessionId] = tList;
                    }

                    tList.Add(trn);
                }
            }
        }
    }

    public Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AreaDto>>(
                _areas.Values.OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAtMs).ToArray());
        }
    }

    public Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_areas.TryGetValue(areaId, out var a) ? a : null);
        }
    }

    public Task<IReadOnlyList<TrackDto>> ListTracksAsync(string? areaId = null, bool includeClosed = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var q = _tracks.Values.AsEnumerable();
            if (areaId is not null)
            {
                q = q.Where(t => t.AreaId == areaId);
            }

            if (!includeClosed)
            {
                q = q.Where(t => t.ClosedAtMs is null);
            }

            return Task.FromResult<IReadOnlyList<TrackDto>>(q.OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAtMs).ToArray());
        }
    }

    public Task<TrackDto?> GetTrackAsync(string trackId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_tracks.TryGetValue(trackId, out var t) ? t : null);
        }
    }

    public Task<TrackAggregateSnapshot?> LoadTrackAggregateAsync(string trackId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(trackId, out var track))
            {
                return Task.FromResult<TrackAggregateSnapshot?>(null);
            }

            var report = _reports.TryGetValue(trackId, out var r) ? r : new ReportDocument(0, "", "", []);
            var tasks = _tasksByTrack.TryGetValue(trackId, out var tl) ? tl.ToArray() : [];
            var sessions = _sessions.Values.Where(s => s.TrackId == trackId).OrderBy(s => s.CreatedAtMs).ToArray();
            var openAsks = _asks.Values.Where(a => a.TrackId == trackId && a.Status == AskStatus.Open).OrderBy(a => a.CreatedAtMs).ToArray();
            _templateSnapshots.TryGetValue(trackId, out var snap);

            return Task.FromResult<TrackAggregateSnapshot?>(
                new TrackAggregateSnapshot(track, report, tasks, sessions, openAsks, snap));
        }
    }

    public Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(string trackId, int docRev, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_revisions.TryGetValue((trackId, docRev), out var compressed))
            {
                return Task.FromResult<IReadOnlyList<ReportBlock>>([]);
            }

            var json = Encoding.UTF8.GetString(compressed);
            var blocks = JsonSerializer.Deserialize<List<ReportBlock>>(json) ?? [];
            return Task.FromResult<IReadOnlyList<ReportBlock>>(blocks);
        }
    }

    public Task<IReadOnlyList<TaskAttemptDto>> ListAttemptsForTaskAsync(string taskId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TaskAttemptDto>>(
                _attemptsByTask.TryGetValue(taskId, out var list) ? list.OrderBy(a => a.StartedAtMs).ToArray() : []);
        }
    }

    public Task<IReadOnlyList<SessionItemDto>> ListSessionItemsAsync(string sessionId, long afterId = 0, int limit = 100, CancellationToken ct = default, bool latest = false, long? beforeId = null)
    {
        lock (_gate)
        {
            if (!_itemsBySession.TryGetValue(sessionId, out var list))
            {
                return Task.FromResult<IReadOnlyList<SessionItemDto>>([]);
            }

            return Task.FromResult<IReadOnlyList<SessionItemDto>>(
                (latest || beforeId is not null
                    ? list.Where(i => i.Id > afterId && (beforeId is null || i.Id < beforeId)).OrderByDescending(i => i.Id).Take(limit).Reverse()
                    : list.Where(i => i.Id > afterId).OrderBy(i => i.Id).Take(limit)).ToArray());
        }
    }

    public Task<IReadOnlyList<MailDto>> ListMailsAsync(string trackId, bool unreadOnly = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_mailsByToTrack.TryGetValue(trackId, out var list))
            {
                return Task.FromResult<IReadOnlyList<MailDto>>([]);
            }

            var q = unreadOnly ? list.Where(m => m.ReadAtMs is null) : list;
            return Task.FromResult<IReadOnlyList<MailDto>>(q.OrderByDescending(m => m.CreatedAtMs).ToArray());
        }
    }

    public Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(string trackId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TerminalDto>>(
                _terminals.Values.Where(t => t.TrackId == trackId).OrderBy(t => t.CreatedAtMs).ToArray());
        }
    }

    public Task<long> GetEventTipIdAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_events.Max(e => e.EventId) ?? 0);
        }
    }

    public Task<IReadOnlyList<EventEnvelopeDto>> ListEventsSinceAsync(long sinceEventId, string? trackId = null, int limit = 500, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var q = _events.Where(e => e.EventId > sinceEventId);
            if (trackId is not null)
            {
                q = q.Where(e => e.TrackId == trackId);
            }

            return Task.FromResult<IReadOnlyList<EventEnvelopeDto>>(q.OrderBy(e => e.EventId).Take(limit).ToArray());
        }
    }

    public string DatabaseId { get; } = Guid.NewGuid().ToString("N");

    public Task<IReadOnlyList<TrackOverlayDto>> ListTrackOverlaysAsync(bool includeClosed = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var tracks = _tracks.Values
                .Where(t => includeClosed || t.ClosedAtMs is null)
                .OrderByDescending(t => t.UpdatedAtMs)
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .ToArray();

            var list = new List<TrackOverlayDto>(tracks.Length);
            foreach (var tr in tracks)
            {
                var isWorking = _sessions.Values.Any(s => s.TrackId == tr.Id && s.Status is "queued" or "running")
                                || (_tasksByTrack.TryGetValue(tr.Id, out var tl) && tl.Any(t => t.Status is TaskRunStatus.Running or TaskRunStatus.Verifying));
                var openAsks = _asks.Values.Count(a => a.TrackId == tr.Id && a.Status == AskStatus.Open);
                var hasFailed = _tasksByTrack.TryGetValue(tr.Id, out var tfl) && tfl.Any(t => t.Status == TaskRunStatus.Failed);
                var unreadMails = _mailsByToTrack.TryGetValue(tr.Id, out var ml) ? ml.Count(m => m.ReadAtMs is null) : 0;
                list.Add(new TrackOverlayDto(tr.Id, tr.AreaId, isWorking, openAsks, hasFailed, unreadMails, tr.UpdatedAtMs));
            }

            return Task.FromResult<IReadOnlyList<TrackOverlayDto>>(list);
        }
    }

    public Task<string?> FindTrackIdBySessionAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_sessions.TryGetValue(sessionId, out var s) ? s.TrackId : null);
        }
    }

    public Task<AreaDto> UpsertAreaAsync(AreaDto area, CancellationToken ct)
    {
        lock (_gate)
        {
            _areas[area.Id] = area;
            return Task.FromResult(area);
        }
    }

    public Task DeleteAreaAsync(string areaId, CancellationToken ct, bool requireEmpty = false)
    {
        lock (_gate)
        {
            _areas.Remove(areaId);
            foreach (var track in _tracks.Values.Where(t => t.AreaId == areaId).ToArray())
            {
                DeleteTrackLocked(track.Id);
            }

            return Task.CompletedTask;
        }
    }

    private readonly Dictionary<(string Operation, string Scope, string Key), (string Id, string Hash)> _creationReceipts = [];
    private readonly Dictionary<string, WorkspacePreparation> _workspacePreparations = [];

    public Task<string?> FindCreationAsync(ResourceCreationKey key, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_creationReceipts.TryGetValue((key.Operation, key.ScopeId, key.Key), out var receipt))
                return Task.FromResult<string?>(null);
            if (receipt.Hash != key.Hash)
                throw KernelException.Conflict("This request key was already used with different input.");
            return Task.FromResult<string?>(receipt.Id);
        }
    }

    public Task<string> CreateAreaAsync(AreaDto area, ResourceCreationKey? key, CancellationToken ct, EventEnvelopeDto? evt = null)
    {
        lock (_gate)
        {
            if (key is not null && FindCreationAsync(key, ct).Result is { } replay)
                return Task.FromResult(replay);
            _areas.Add(area.Id, area);
            SaveReceipt(key, area.Id);
            if (evt is not null)
                AppendEventLocked(evt);
            return Task.FromResult(area.Id);
        }
    }

    public Task<string> CreateTrackAsync(TrackCreation creation, ResourceCreationKey? key, CancellationToken ct)
    {
        var (track, report, templateSnapshot, lead, tasks, firstInput, preparation, events) = creation;
        lock (_gate)
        {
            if (key is not null && FindCreationAsync(key, ct).Result is { } replay)
                return Task.FromResult(replay);
            if (firstInput is not null)
                throw new NotSupportedException("Durable first inputs are tested with SQLite.");
            _tracks.Add(track.Id, track);
            _reports[track.Id] = report;
            _revisions[(track.Id, report.DocRev)] = new InMemoryReportDocEngine().CompressZstd(report.Blocks);
            _templateSnapshots[track.Id] = templateSnapshot;
            _tasksByTrack[track.Id] = tasks.ToList();
            _sessions[lead.Id] = lead;
            if (preparation is not null)
                _workspacePreparations.Add(track.Id, preparation);
            foreach (var evt in events)
                AppendEventLocked(evt);
            SaveReceipt(key, track.Id);
            return Task.FromResult(track.Id);
        }
    }

    private void SaveReceipt(ResourceCreationKey? key, string id)
    {
        if (key is not null)
            _creationReceipts.Add((key.Operation, key.ScopeId, key.Key), (id, key.Hash));
    }

    public Task<WorkspacePreparation?> GetWorkspacePreparationAsync(string trackId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_workspacePreparations.GetValueOrDefault(trackId));
    }

    public Task<IReadOnlyList<PersistedSessionInput>> ListAcceptedTrackInputsAsync(string trackId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PersistedSessionInput>>([]);

    public Task<IReadOnlyList<string>> RecoverWorkspacePreparationsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<bool> TryClaimWorkspacePreparationAsync(string trackId, WorkspacePreparationPhase expected, WorkspacePreparation claimed, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_workspacePreparations.TryGetValue(trackId, out var preparation) || preparation.Phase != expected)
                return Task.FromResult(false);
            _workspacePreparations[trackId] = claimed;
            return Task.FromResult(true);
        }
    }

    public Task SaveWorkspacePreparationAsync(string trackId, WorkspacePreparation preparation, TrackDto? readyTrack, EventEnvelopeDto? evt, CancellationToken ct)
    {
        lock (_gate)
        {
            _workspacePreparations[trackId] = preparation;
            if (readyTrack is not null)
                _tracks[trackId] = readyTrack;
            if (evt is not null)
                AppendEventLocked(evt);
            return Task.CompletedTask;
        }
    }

    public Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct)
    {
        lock (_gate)
        {
            _tracks[track.Id] = track;
            if (evt is not null)
            {
                AppendEventLocked(evt);
            }

            return Task.CompletedTask;
        }
    }

    public Task UpdateTrackTemplateAndEmitAsync(TrackDto track, string templateSnapshot, EventEnvelopeDto evt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (FailTemplateUpdate) throw new IOException("template persistence failure");
            _templateSnapshots[track.Id] = templateSnapshot;
            return UpdateTrackAndEmitAsync(track, evt, ct);
        }
    }

    public Task DeleteTrackAsync(string trackId, CancellationToken ct)
    {
        lock (_gate)
        {
            DeleteTrackLocked(trackId);
            return Task.CompletedTask;
        }
    }

    public Task SaveReportAndTasksAsync(
        string trackId,
        ReportDocument report,
        byte[] compressedRevisionBlocks,
        string? authorSessionId,
        string commitMessage,
        IReadOnlyList<TaskDto> syncedTasks,
        JsonElement nextTemplateState,
        EventEnvelopeDto evt,
        CancellationToken ct,
        WorkerDispatchIntent? dispatch = null)
    {
        lock (_gate)
        {
            _reports[trackId] = report;
            _revisions[(trackId, report.DocRev)] = compressedRevisionBlocks.ToArray();
            _tasksByTrack[trackId] = syncedTasks.ToList();

            if (_tracks.TryGetValue(trackId, out var track))
            {
                _tracks[trackId] = track with { TemplateState = nextTemplateState.Clone() };
            }

            AppendEventLocked(evt);
            return Task.CompletedTask;
        }
    }

    public Task UpsertTaskAndAttemptAsync(
        TaskDto task,
        TaskAttemptDto? attempt,
        JsonElement? nextTemplateState,
        EventEnvelopeDto? evt,
        CancellationToken ct,
        AgentSessionDto? session = null,
        EventEnvelopeDto? sessionEvent = null)
    {
        lock (_gate)
        {
            if (session is not null)
                _sessions[session.Id] = session;
            if (sessionEvent is not null)
                AppendEventLocked(sessionEvent);
            if (!_tasksByTrack.TryGetValue(task.TrackId, out var list))
            {
                list = [];
                _tasksByTrack[task.TrackId] = list;
            }

            var idx = list.FindIndex(t => t.Id == task.Id);
            if (idx >= 0)
            {
                list[idx] = task;
            }
            else
            {
                list.Add(task);
            }

            if (attempt is not null)
            {
                if (!_attemptsByTask.TryGetValue(task.Id, out var attList))
                {
                    attList = [];
                    _attemptsByTask[task.Id] = attList;
                }

                var aIdx = attList.FindIndex(a => a.AttemptId == attempt.AttemptId);
                if (aIdx >= 0)
                {
                    attList[aIdx] = attempt;
                }
                else
                {
                    attList.Add(attempt);
                }
            }

            if (nextTemplateState is { } ns && _tracks.TryGetValue(task.TrackId, out var track))
            {
                _tracks[task.TrackId] = track with { TemplateState = ns.Clone() };
            }

            if (evt is not null)
            {
                AppendEventLocked(evt);
            }

            return Task.CompletedTask;
        }
    }

    public Task UpsertSessionAsync(AgentSessionDto session, EventEnvelopeDto? evt, CancellationToken ct)
    {
        lock (_gate)
        {
            _sessions[session.Id] = session;
            if (evt is not null)
            {
                AppendEventLocked(evt);
            }

            return Task.CompletedTask;
        }
    }

    public ISessionInputStore SessionInputs => throw new NotSupportedException();

    public Task<IReadOnlyList<(string TrackId, PersistedSessionInput Input)>> RecoverSessionInputsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(string, PersistedSessionInput)>>([]);

    public Task<IReadOnlyList<ProviderSessionResumeFence>> ListSessionResumeFencesAsync(string trackId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderSessionResumeFence>>([]);

    public Task SaveSessionResumeFenceAsync(ProviderSessionResumeFence fence, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task CompleteSessionResumeAsync(AgentSessionDto session, PersistedSessionInput? unresolved,
        EventEnvelopeDto sessionEvent, CancellationToken ct) => throw new NotSupportedException();

    public Task<bool> HasSessionTurnAsync(string sessionId, string turnId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_turnsBySession.TryGetValue(sessionId, out var turns) && turns.Any(turn => turn.Id == turnId));
    }

    public Task<int> GetLastTurnIndexAsync(string sessionId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_turnsBySession.TryGetValue(sessionId, out var turns) && turns.Count > 0
                ? turns.Max(turn => turn.TurnIndex)
                : 0);
    }

    public async Task RecordTurnAndItemsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct)
    {
        _ = await RecordTurnAndItemsWithIdsAsync(turn, newItems, updatedSession, ct);
    }

    public Task<(SessionTurnDto Turn, IReadOnlyList<long> ItemIds)> RecordTurnAndItemsWithIdsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct = default, SessionMessage? dispatchMessage = null)
    {
        lock (_gate)
        {
            if (!_turnsBySession.TryGetValue(turn.SessionId, out var turns))
            {
                turns = [];
                _turnsBySession[turn.SessionId] = turns;
            }

            if (!_itemsBySession.TryGetValue(turn.SessionId, out var items))
            {
                items = [];
                _itemsBySession[turn.SessionId] = items;
            }

            var ids = new List<long>(newItems.Count);
            long? userItemId = turn.UserItemId;
            foreach (var item in newItems)
            {
                var assignedId = _nextItemId++;
                items.Add(item with { Id = assignedId });
                ids.Add(assignedId);
                if (userItemId is null && item.Kind == "user")
                {
                    userItemId = assignedId;
                }
            }

            userItemId ??= items.FirstOrDefault(i => i.TurnId == turn.Id && i.Kind == "user")?.Id;
            var storedTurn = userItemId == turn.UserItemId ? turn : turn with { UserItemId = userItemId };

            var tIdx = turns.FindIndex(t => t.Id == storedTurn.Id);
            if (tIdx >= 0)
            {
                turns[tIdx] = storedTurn;
            }
            else
            {
                if (turns.Any(t => t.TurnIndex == storedTurn.TurnIndex))
                {
                    throw new InvalidOperationException(
                        $"Duplicate (session_id={storedTurn.SessionId}, turn_index={storedTurn.TurnIndex}) violated!");
                }

                turns.Add(storedTurn);
            }

            if (updatedSession is not null)
            {
                _sessions[updatedSession.Id] = updatedSession;
            }

            return Task.FromResult<(SessionTurnDto, IReadOnlyList<long>)>((storedTurn, ids));
        }
    }

    public Task<IReadOnlyList<long>> AppendSessionItemsAsync(
        string sessionId,
        IReadOnlyList<SessionItemDto> newItems,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_itemsBySession.TryGetValue(sessionId, out var items))
            {
                items = [];
                _itemsBySession[sessionId] = items;
            }

            var ids = new List<long>(newItems.Count);
            foreach (var item in newItems)
            {
                var id = _nextItemId++;
                items.Add(item with { Id = id });
                ids.Add(id);
            }

            return Task.FromResult<IReadOnlyList<long>>(ids);
        }
    }

    public Task UpsertAskAsync(AskDto ask, EventEnvelopeDto evt, CancellationToken ct)
    {
        lock (_gate)
        {
            _asks[ask.Id] = ask;
            AppendEventLocked(evt);
            return Task.CompletedTask;
        }
    }

    private readonly HashSet<string> _pendingMailWakes = [];

    public Task AcknowledgeMailWakeAsync(string mailId, CancellationToken ct)
    {
        lock (_gate)
            _pendingMailWakes.Remove(mailId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MailDto>> ListPendingMailWakesAsync(CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<MailDto>>(_mailsByToTrack.Values.SelectMany(list => list)
                .Where(mail => _pendingMailWakes.Contains(mail.Id)).ToList());
    }

    public Task<MailDto?> FindReceivedMailAsync(string trackId, string mailId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_mailsByToTrack.GetValueOrDefault(trackId)?.Find(mail => mail.Id == mailId));
    }

    public Task UpsertMailAsync(MailDto mail, EventEnvelopeDto evt, CancellationToken ct, bool requestWake = true)
    {
        lock (_gate)
        {
            if (!_mailsByToTrack.TryGetValue(mail.ToTrackId, out var list))
            {
                list = [];
                _mailsByToTrack[mail.ToTrackId] = list;
            }

            var idx = list.FindIndex(m => m.Id == mail.Id);
            if (idx >= 0)
            {
                list[idx] = mail;
            }
            else
            {
                list.Add(mail);
                if (requestWake)
                    _pendingMailWakes.Add(mail.Id);
            }

            AppendEventLocked(evt);
            return Task.CompletedTask;
        }
    }

    public Task UpsertTerminalAsync(TerminalDto terminal, EventEnvelopeDto? evt, CancellationToken ct)
    {
        lock (_gate)
        {
            _terminals[terminal.Id] = terminal;
            if (evt is not null)
            {
                AppendEventLocked(evt);
            }

            return Task.CompletedTask;
        }
    }

    public Task DeleteTerminalAsync(string trackId, string terminalId, EventEnvelopeDto? evt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_terminals.TryGetValue(terminalId, out var existing) && existing.TrackId == trackId)
            {
                _terminals.Remove(terminalId);
            }

            if (evt is not null)
            {
                AppendEventLocked(evt);
            }

            return Task.CompletedTask;
        }
    }

    public Task AppendEventAsync(EventEnvelopeDto evt, CancellationToken ct)
    {
        lock (_gate)
        {
            AppendEventLocked(evt);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<InterruptedAttemptLeaseDto>> ListInFlightAttemptLeasesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<InterruptedAttemptLeaseDto> leases = _attemptsByTask.Values.SelectMany(attempts => attempts)
                .Where(attempt => attempt.WorkspaceLease.HasValue &&
                    (string.Equals(attempt.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(attempt.Status, "verifying", StringComparison.OrdinalIgnoreCase)))
                .Select(attempt => new InterruptedAttemptLeaseDto(
                    attempt.AttemptId, attempt.TaskId, attempt.TrackId, attempt.WorkspaceLease!.Value))
                .ToArray();
            return Task.FromResult(leases);
        }
    }

    public Task<IReadOnlyList<string>> MarkInFlightInterruptedOnBootAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            MarkInterruptedCalls++;
            var affected = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kvp in _turnsBySession)
            {
                for (var i = 0; i < kvp.Value.Count; i++)
                {
                    var turn = kvp.Value[i];
                    if (string.Equals(turn.Status, "running", StringComparison.OrdinalIgnoreCase))
                    {
                        kvp.Value[i] = turn with { Status = "interrupted", ErrorText = "Interrupted on boot." };
                        if (_sessions.TryGetValue(turn.SessionId, out var ses))
                        {
                            _sessions[ses.Id] = ses with { Status = "idle" };
                            affected.Add(ses.TrackId);
                        }
                    }
                }
            }

            foreach (var kvp in _sessions.ToArray())
            {
                if (string.Equals(kvp.Value.Status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    _sessions[kvp.Key] = kvp.Value with { Status = "idle" };
                    affected.Add(kvp.Value.TrackId);
                }
            }

            foreach (var kvp in _attemptsByTask)
            {
                for (var i = 0; i < kvp.Value.Count; i++)
                {
                    var att = kvp.Value[i];
                    if (string.Equals(att.Status, "Running", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(att.Status, "Verifying", StringComparison.OrdinalIgnoreCase))
                    {
                        kvp.Value[i] = att with { Status = "interrupted" };
                        affected.Add(att.TrackId);
                    }
                }
            }

            foreach (var kvp in _tasksByTrack)
            {
                for (var i = 0; i < kvp.Value.Count; i++)
                {
                    var t = kvp.Value[i];
                    if (t.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)
                    {
                        kvp.Value[i] = t with { Status = TaskRunStatus.Failed, StatusDetail = "interrupted" };
                        affected.Add(t.TrackId);
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(affected.ToArray());
        }
    }

    public Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(long nowMs, CancellationToken ct) =>
        Task.FromResult(new DbMaintenanceStatsDto(0, 0, 0, 0));

    private void AppendEventLocked(EventEnvelopeDto evt)
    {
        if (KernelEventKinds.IsTransient(evt.Kind))
        {
            throw new InvalidOperationException($"Transient event '{evt.Kind}' must never be written to ITrackStore!");
        }

        var persisted = evt with { EventId = _nextEventId++ };
        _events.Add(persisted);
    }

    private void DeleteTrackLocked(string trackId)
    {
        _tracks.Remove(trackId);
        _reports.Remove(trackId);
        _templateSnapshots.Remove(trackId);
        if (_tasksByTrack.TryGetValue(trackId, out var tasks))
        {
            foreach (var t in tasks)
            {
                _attemptsByTask.Remove(t.Id);
            }

            _tasksByTrack.Remove(trackId);
        }

        foreach (var ses in _sessions.Values.Where(s => s.TrackId == trackId).ToArray())
        {
            _sessions.Remove(ses.Id);
            _turnsBySession.Remove(ses.Id);
            _itemsBySession.Remove(ses.Id);
        }

        foreach (var ask in _asks.Values.Where(a => a.TrackId == trackId).ToArray())
        {
            _asks.Remove(ask.Id);
        }

        _mailsByToTrack.Remove(trackId);
        foreach (var term in _terminals.Values.Where(t => t.TrackId == trackId).ToArray())
        {
            _terminals.Remove(term.Id);
        }
    }
}

internal sealed class InMemoryReportDocEngine : IReportDocEngine
{
    private int _blockIdCounter;

    public ReportDocument Parse(string rawMarkdown, int docRev = 0, string summary = "")
    {
        var blocks = new List<ReportBlock>();
        if (string.IsNullOrWhiteSpace(rawMarkdown))
        {
            return new ReportDocument(docRev, summary, "", blocks);
        }

        var lines = rawMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? currentSection = null;
        string? pendingMarkerId = null;
        var proseBuffer = new List<string>();

        void FlushProse()
        {
            var text = string.Join("\n", proseBuffer).Trim();
            proseBuffer.Clear();
            if (text.Length == 0)
            {
                return;
            }

            var id = pendingMarkerId ?? NextBlockId();
            pendingMarkerId = null;
            blocks.Add(new ReportBlock(id, 1, ReportBlockKind.Prose, currentSection, text, null));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var markerMatch = Regex.Match(line.Trim(), @"^<!--\s*perga:(b_[a-zA-Z0-9_]+)\s*-->$");
            if (markerMatch.Success)
            {
                FlushProse();
                pendingMarkerId = markerMatch.Groups[1].Value;
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushProse();
                currentSection = line[2..].Trim();
                proseBuffer.Add(line);
                continue;
            }

            if (line.TrimStart().StartsWith("```perga-block", StringComparison.Ordinal))
            {
                FlushProse();
                var kindToken = line.TrimStart()["```perga-block".Length..].Trim();
                var blockKind = ParseKind(kindToken);
                var fenceLines = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    fenceLines.Add(lines[i]);
                    i++;
                }

                var rawBody = string.Join("\n", fenceLines).Trim();
                JsonElement? payload = null;
                if (rawBody.StartsWith('{'))
                {
                    payload = TestJson.Parse(rawBody);
                }

                var id = pendingMarkerId ?? NextBlockId();
                pendingMarkerId = null;
                blocks.Add(new ReportBlock(id, 1, blockKind, currentSection, rawBody, payload));
                continue;
            }

            proseBuffer.Add(line);
        }

        FlushProse();
        return new ReportDocument(docRev, summary, "", blocks);
    }

    public string RenderMarkdown(ReportDocument doc, bool withMarkers = false)
    {
        var sb = new StringBuilder();
        foreach (var b in doc.Blocks)
        {
            if (withMarkers)
            {
                sb.AppendLine($"<!-- perga:{b.Id} -->");
            }

            if (b.Kind == ReportBlockKind.Prose)
            {
                sb.AppendLine(b.Markdown ?? string.Empty);
            }
            else
            {
                var kindLower = b.Kind.ToString().ToLowerInvariant();
                sb.AppendLine($"```perga-block {kindLower}");
                sb.AppendLine(b.Payload is { } p ? p.GetRawText() : (b.Markdown ?? "{}"));
                sb.AppendLine("```");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    public ReportCommitResult ApplyCommit(
        ReportDocument current,
        ReadAnchor callerAnchor,
        IReadOnlyList<BlockOp> ops,
        string? newSummary = null)
    {
        if (callerAnchor.WholeDocumentRead && callerAnchor.DocRev != current.DocRev)
        {
            throw new ReportConflictException(
                "DOC_REV_CONFLICT",
                $"Expected docRev {callerAnchor.DocRev} but current is {current.DocRev}.",
                "Call perga_report_read to refresh anchor.");
        }

        var nextBlocks = current.Blocks.ToList();
        var changes = new List<BlockChangeRecord>();

        void VerifyBlockAnchor(ReportBlock target)
        {
            if (callerAnchor.WholeDocumentRead)
            {
                return;
            }

            if (!callerAnchor.KnownBlockRevs.TryGetValue(target.Id, out var knownRev) || knownRev != target.Rev)
            {
                throw new ReportConflictException(
                    "BLOCK_REV_CONFLICT",
                    $"Block '{target.Id}' is at rev {target.Rev}, caller anchor had {(callerAnchor.KnownBlockRevs.TryGetValue(target.Id, out var kr) ? kr.ToString() : "none")}.",
                    "Call perga_report_read before retrying.");
            }
        }

        foreach (var op in ops)
        {
            switch (op)
            {
                case BlockOp.UpsertBlockOp upsert:
                {
                    var existingIdx = upsert.Id is null
                        ? -1
                        : nextBlocks.FindIndex(b => b.Id == upsert.Id);

                    if (existingIdx >= 0)
                    {
                        var existing = nextBlocks[existingIdx];
                        VerifyBlockAnchor(existing);
                        var updated = existing with
                        {
                            Rev = existing.Rev + 1,
                            Kind = upsert.Kind,
                            Markdown = upsert.Markdown ?? existing.Markdown,
                            Payload = upsert.Payload?.Clone() ?? existing.Payload
                        };
                        nextBlocks[existingIdx] = updated;
                        changes.Add(new BlockChangeRecord(updated.Id, updated.Kind, existing.Rev, updated.Rev, existingIdx, Moved: false));
                    }
                    else
                    {
                        var newId = upsert.Id ?? NextBlockId();
                        var created = new ReportBlock(
                            newId,
                            1,
                            upsert.Kind,
                            SectionH1: nextBlocks.LastOrDefault()?.SectionH1,
                            Markdown: upsert.Markdown,
                            Payload: upsert.Payload?.Clone());
                        var insertPos = upsert.Position.HasValue
                            ? Math.Clamp(upsert.Position.Value, 0, nextBlocks.Count)
                            : nextBlocks.Count;
                        nextBlocks.Insert(insertPos, created);
                        changes.Add(new BlockChangeRecord(created.Id, created.Kind, null, 1, insertPos, Moved: false));
                    }

                    break;
                }

                case BlockOp.DeleteBlockOp del:
                {
                    var idx = nextBlocks.FindIndex(b => b.Id == del.Id);
                    if (idx < 0)
                    {
                        throw new ReportConflictException("BLOCK_NOT_FOUND", $"Block '{del.Id}' does not exist.", "Re-read report.");
                    }

                    var target = nextBlocks[idx];
                    VerifyBlockAnchor(target);
                    nextBlocks.RemoveAt(idx);
                    changes.Add(new BlockChangeRecord(target.Id, target.Kind, target.Rev, null, null, Moved: false));
                    break;
                }

                case BlockOp.MoveBlockOp move:
                {
                    var idx = nextBlocks.FindIndex(b => b.Id == move.Id);
                    if (idx < 0)
                    {
                        throw new ReportConflictException("BLOCK_NOT_FOUND", $"Block '{move.Id}' does not exist.", "Re-read report.");
                    }

                    var target = nextBlocks[idx];
                    VerifyBlockAnchor(target);
                    nextBlocks.RemoveAt(idx);
                    var dest = Math.Clamp(move.ToIndex, 0, nextBlocks.Count);
                    nextBlocks.Insert(dest, target);
                    changes.Add(new BlockChangeRecord(target.Id, target.Kind, target.Rev, target.Rev, dest, Moved: true));
                    break;
                }

                case BlockOp.ReplaceSectionOp replaceSec:
                {
                    var normalizedTarget = replaceSec.SectionH1.TrimStart('#').Trim();
                    var matchingIndices = new List<int>();
                    for (var i = 0; i < nextBlocks.Count; i++)
                    {
                        if (string.Equals(nextBlocks[i].SectionH1, normalizedTarget, StringComparison.OrdinalIgnoreCase))
                        {
                            VerifyBlockAnchor(nextBlocks[i]);
                            matchingIndices.Add(i);
                        }
                    }

                    var parsedReplacement = Parse($"# {normalizedTarget}\n\n{replaceSec.Markdown}");
                    var insertAt = matchingIndices.Count > 0 ? matchingIndices[0] : nextBlocks.Count;

                    for (var i = matchingIndices.Count - 1; i >= 0; i--)
                    {
                        var removed = nextBlocks[matchingIndices[i]];
                        nextBlocks.RemoveAt(matchingIndices[i]);
                        changes.Add(new BlockChangeRecord(removed.Id, removed.Kind, removed.Rev, null, null, Moved: false));
                    }

                    for (var j = 0; j < parsedReplacement.Blocks.Count; j++)
                    {
                        var b = parsedReplacement.Blocks[j] with { SectionH1 = normalizedTarget };
                        nextBlocks.Insert(insertAt + j, b);
                        changes.Add(new BlockChangeRecord(b.Id, b.Kind, null, b.Rev, insertAt + j, Moved: false));
                    }

                    break;
                }
            }
        }

        var nextDoc = new ReportDocument(
            DocRev: current.DocRev + 1,
            Summary: newSummary ?? current.Summary,
            LeadingComment: current.LeadingComment,
            Blocks: nextBlocks);

        return new ReportCommitResult(nextDoc, changes);
    }

    public byte[] CompressZstd(IReadOnlyList<ReportBlock> blocks) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(blocks));

    public IReadOnlyList<ReportBlock> DecompressZstd(byte[] compressed) =>
        JsonSerializer.Deserialize<List<ReportBlock>>(Encoding.UTF8.GetString(compressed)) ?? [];

    private string NextBlockId() => $"b_{Interlocked.Increment(ref _blockIdCounter):x4}";

    private static ReportBlockKind ParseKind(string raw) => raw.ToLowerInvariant() switch
    {
        "task" => ReportBlockKind.Task,
        "html" => ReportBlockKind.Html,
        "table" => ReportBlockKind.Table,
        "view" => ReportBlockKind.View,
        _ => ReportBlockKind.Prose
    };
}

internal sealed class ConfigurableFakePolicyVm : IPolicyVm
{
    private int _compileCount;

    public int CompileCallCount => Volatile.Read(ref _compileCount);

    public Func<string, CompiledTemplateManifest>? CustomManifestFactory { get; set; }
    public Func<CompiledPolicy, JsonElement, PolicyEvalResult<JsonElement>>? OnInitState { get; set; }
    public Func<CompiledPolicy, PolicyTurnContext, JsonElement, PolicyEvalResult<string?>>? OnBuildTurnContext { get; set; }
    public Func<CompiledPolicy, PolicyReportCommitContext, JsonElement, PolicyEvalResult<GuardDecision>>? OnGuardReportCommit { get; set; }
    public Func<CompiledPolicy, PolicyToolCallContext, JsonElement, PolicyEvalResult<GuardDecision>>? OnGuardToolCall { get; set; }
    public Func<CompiledPolicy, PolicyTaskSettledContext, JsonElement, PolicyEvalResult<string?>>? OnTaskSettledHook { get; set; }
    public Func<CompiledPolicy, string, JsonElement, JsonElement, PolicyEvalResult<string?>>? OnBuildScheduleWake { get; set; }

    public ConcurrentQueue<PolicyTaskSettledContext> RecordedSettledContexts { get; } = new();
    public ConcurrentQueue<PolicyToolCallContext> RecordedToolGuardContexts { get; } = new();

    public CompiledPolicy Compile(string jsModuleSource)
    {
        Interlocked.Increment(ref _compileCount);
        CompiledTemplateManifest manifest;
        if (CustomManifestFactory is not null)
        {
            manifest = CustomManifestFactory(jsModuleSource);
        }
        else
        {
            var idMatch = Regex.Match(jsModuleSource, @"id\s*:\s*[""']([^""']+)[""']");
            var titleMatch = Regex.Match(jsModuleSource, @"title\s*:\s*[""']([^""']+)[""']");
            var afterCmdMatch = Regex.Match(jsModuleSource, @"afterWorkerDoneCmd\s*:\s*[""']([^""']+)[""']");
            var createCmdMatch = Regex.Match(jsModuleSource, @"onCreateCmd\s*:\s*[""']([^""']+)[""']");
            var closeCmdMatch = Regex.Match(jsModuleSource, @"onCloseCmd\s*:\s*[""']([^""']+)[""']");

            var id = idMatch.Success ? idMatch.Groups[1].Value : $"tpl_{_compileCount}";
            var title = titleMatch.Success ? titleMatch.Groups[1].Value : $"Template {id}";

            manifest = new CompiledTemplateManifest(
                Id: id,
                Title: title,
                Description: "Test template",
                DefaultPlugins: ["market"],
                InputsSchema: TestJson.Parse("{}"),
                Sections: [],
                MaxProseChars: null,
                RequireGateForReadWrite: false,
                WorkspaceDriver: "direct",
                WorkspaceLowerCacheDirs: [],
                WorkspaceCreateCmd: createCmdMatch.Success ? createCmdMatch.Groups[1].Value : null,
                WorkspaceCloseCmd: closeCmdMatch.Success ? closeCmdMatch.Groups[1].Value : null,
                AfterWorkerDoneCmd: afterCmdMatch.Success ? afterCmdMatch.Groups[1].Value : null,
                Schedules: [new ScheduleSpec("daily-check", "0 9 * * *", "UTC", "Static daily check prompt")],
                InitialReportMarkdown: "# Overview\n\nInitial template markdown.\n");
        }

        return new CompiledPolicy(manifest.Id, jsModuleSource, manifest);
    }

    public PolicyEvalResult<JsonElement> InitState(CompiledPolicy policy, JsonElement inputs)
    {
        if (OnInitState is not null)
        {
            return OnInitState(policy, inputs);
        }

        var init = TestJson.Parse("""{"initialized":true,"count":0}""");
        return new PolicyEvalResult<JsonElement>(init, init);
    }

    public PolicyEvalResult<string?> BuildTurnContext(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement state)
    {
        if (OnBuildTurnContext is not null)
        {
            return OnBuildTurnContext(policy, ctx, state);
        }

        return new PolicyEvalResult<string?>(state, null);
    }

    public PolicyEvalResult<GuardDecision> GuardReportCommit(CompiledPolicy policy, PolicyReportCommitContext ctx, JsonElement state)
    {
        if (OnGuardReportCommit is not null)
        {
            return OnGuardReportCommit(policy, ctx, state);
        }

        return new PolicyEvalResult<GuardDecision>(state, new GuardDecision.Allow());
    }

    public PolicyEvalResult<GuardDecision> GuardToolCall(CompiledPolicy policy, PolicyToolCallContext ctx, JsonElement state)
    {
        RecordedToolGuardContexts.Enqueue(ctx);
        if (OnGuardToolCall is not null)
        {
            return OnGuardToolCall(policy, ctx, state);
        }

        return new PolicyEvalResult<GuardDecision>(state, new GuardDecision.Allow());
    }

    public PolicyEvalResult<string?> OnTaskSettled(CompiledPolicy policy, PolicyTaskSettledContext ctx, JsonElement state)
    {
        RecordedSettledContexts.Enqueue(ctx);
        if (OnTaskSettledHook is not null)
        {
            return OnTaskSettledHook(policy, ctx, state);
        }

        return new PolicyEvalResult<string?>(state, $"Policy noted {ctx.TaskKey} -> {ctx.AttemptStatus}");
    }

    public PolicyEvalResult<string?> BuildScheduleWakePrompt(CompiledPolicy policy, string scheduleId, JsonElement inputs, JsonElement state)
    {
        if (OnBuildScheduleWake is not null)
        {
            return OnBuildScheduleWake(policy, scheduleId, inputs, state);
        }

        return new PolicyEvalResult<string?>(state, $"Dynamic wake prompt for schedule '{scheduleId}'.");
    }
}

internal sealed class ScriptableFakeAgentSession : IAgentSession
{
    private readonly Func<ScriptableFakeAgentSession, TurnInput, CancellationToken, IAsyncEnumerable<UnifiedAgentEvent>> _turnHandler;

    public ScriptableFakeAgentSession(
        HarnessLaunchSpec launchSpec,
        string initialNativeThreadId,
        Func<ScriptableFakeAgentSession, TurnInput, CancellationToken, IAsyncEnumerable<UnifiedAgentEvent>> turnHandler)
    {
        LaunchSpec = launchSpec;
        NativeThreadId = initialNativeThreadId;
        _turnHandler = turnHandler;
    }

    public HarnessLaunchSpec LaunchSpec { get; }
    public string? NativeThreadId { get; set; }
    public bool SupportsNativeSteer { get; set; } = true;
    public bool Disposed { get; private set; }
    public bool CancelCalled { get; private set; }
    public ConcurrentQueue<string> SteeredMessages { get; } = new();
    public ConcurrentQueue<(string RequestId, bool Approved, string? Reason)> Approvals { get; } = new();
    public ConcurrentQueue<TurnInput> RecordedInputs { get; } = new();

    public IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(TurnInput input, CancellationToken ct)
    {
        RecordedInputs.Enqueue(input);
        return _turnHandler(this, input, ct);
    }

    public Task CancelTurnAsync(CancellationToken ct)
    {
        CancelCalled = true;
        return Task.CompletedTask;
    }

    public Task RespondApprovalAsync(string requestId, bool approved, string? reason = null, CancellationToken ct = default)
    {
        Approvals.Enqueue((requestId, approved, reason));
        return Task.CompletedTask;
    }

    public bool SupportsModelSwitch { get; set; }
    public ConcurrentQueue<string?> ModelSwitches { get; } = new();
    public Task<bool> TrySetModelAsync(string? model, string? reasoningEffort = null, CancellationToken ct = default)
    {
        ModelSwitches.Enqueue(model);
        return Task.FromResult(SupportsModelSwitch);
    }

    public Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct)
    {
        if (!SupportsNativeSteer)
            return Task.FromResult(SteerDelivery.Declined);

        SteeredMessages.Enqueue(message);
        return Task.FromResult(SteerDelivery.Accepted);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScriptableFakeAgentRuntime : IAgentRuntime
{
    private int _threadCounter;

    public ConcurrentQueue<ScriptableFakeAgentSession> OpenedSessions { get; } = new();
    public Action<HarnessLaunchSpec>? OnBeforeOpenSession { get; set; }

    public Func<ScriptableFakeAgentSession, TurnInput, CancellationToken, IAsyncEnumerable<UnifiedAgentEvent>>? TurnScript { get; set; }

    public Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProviderStatus>>(
        [
            new ProviderStatus("codex", true, "1.0.0", null, ["gpt-5"])
        ]);

    public Task<IAgentSession> OpenSessionAsync(HarnessLaunchSpec spec, CancellationToken ct)
    {
        OnBeforeOpenSession?.Invoke(spec);
        var threadId = spec.ResumeNativeThreadId ?? $"thr_{Interlocked.Increment(ref _threadCounter)}";
        var session = new ScriptableFakeAgentSession(
            spec,
            threadId,
            (s, inp, token) => (TurnScript ?? DefaultTurnScript)(s, inp, token));
        OpenedSessions.Enqueue(session);
        return Task.FromResult<IAgentSession>(session);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> DefaultTurnScript(
        ScriptableFakeAgentSession session,
        TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new UnifiedAgentEvent.ThreadBound(session.NativeThreadId ?? "thr_default");
        yield return new UnifiedAgentEvent.ThinkingDelta("Thinking...");
        yield return new UnifiedAgentEvent.TextDelta($"Ack: {input.Prompt[..Math.Min(40, input.Prompt.Length)]}");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(100, 50, 0, 100_000));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SpyWorkspaceDriver : IWorkspaceDriver
{
    public SpyWorkspaceDriver(string driverId = "direct", bool supportsParallelWriteLeases = false)
    {
        DriverId = driverId;
        SupportsParallelIsolatedWriteLeases = supportsParallelWriteLeases;
    }

    public string DriverId { get; }
    public bool SupportsParallelIsolatedWriteLeases { get; set; }
    public IReadOnlyList<WorkspaceOverlayLayer>? OverlayLayersToInject { get; set; }
    public string? OverrideMountedMergedCwd { get; set; }

    public ConcurrentQueue<WorkspaceProvisionRequest> ProvisionCalls { get; } = new();
    public ConcurrentQueue<WorkspaceSpec> EnsureMountedCalls { get; } = new();
    public ConcurrentQueue<(MountedWorkspace Workspace, string AttemptId, bool ReadWrite)> AcquireLeaseCalls { get; } = new();
    public ConcurrentQueue<(AttemptWorkspaceLease Lease, AttemptLeaseDisposition Disposition)> SettleLeaseCalls { get; } = new();
    public ConcurrentQueue<WorkspaceSpec> TeardownCalls { get; } = new();
    public ConcurrentQueue<string> LifecycleTimeline { get; } = new();

    public Task<WorkspaceSpec> ProvisionTrackAsync(WorkspaceProvisionRequest req, CancellationToken ct = default)
    {
        ProvisionCalls.Enqueue(req);
        LifecycleTimeline.Enqueue($"provision:{req.TrackId}");
        var merged = OverrideMountedMergedCwd ?? req.SourcePath;
        return Task.FromResult(new WorkspaceSpec(
            DriverId: DriverId,
            LogicalRoot: req.SourcePath,
            LowerDirs: req.LowerCacheDirs,
            UpperDir: null,
            WorkDir: null,
            MergedCwd: merged,
            Metadata: TestJson.Parse("{}")));
    }

    public Task<MountedWorkspace> EnsureMountedAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        EnsureMountedCalls.Enqueue(spec);
        LifecycleTimeline.Enqueue($"ensure_mounted:{spec.MergedCwd}");
        var merged = OverrideMountedMergedCwd ?? spec.MergedCwd;
        return Task.FromResult(new MountedWorkspace(DriverId, merged, OverlayLayersToInject));
    }

    public Task<AttemptWorkspaceLease> AcquireAttemptLeaseAsync(
        MountedWorkspace workspace,
        string attemptId,
        bool readWrite,
        CancellationToken ct = default)
    {
        AcquireLeaseCalls.Enqueue((workspace, attemptId, readWrite));
        LifecycleTimeline.Enqueue($"acquire_lease:{attemptId}:{(readWrite ? "rw" : "ro")}");
        var effectiveCwd = Path.Combine(workspace.MergedCwd, "leases", attemptId);
        return Task.FromResult(new AttemptWorkspaceLease(
            DriverId: DriverId,
            AttemptId: attemptId,
            EffectiveCwd: effectiveCwd,
            IsIsolatedCopyOnWrite: SupportsParallelIsolatedWriteLeases && readWrite,
            Metadata: TestJson.FromObject(new { readWrite }),
            ProcessOverlayLayers: OverlayLayersToInject));
    }

    public Task SettleAttemptLeaseAsync(
        AttemptWorkspaceLease lease,
        AttemptLeaseDisposition disposition,
        CancellationToken ct = default)
    {
        SettleLeaseCalls.Enqueue((lease, disposition));
        LifecycleTimeline.Enqueue($"settle_lease:{lease.AttemptId}:{disposition}");
        return Task.CompletedTask;
    }

    public Task TeardownTrackAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        TeardownCalls.Enqueue(spec);
        LifecycleTimeline.Enqueue($"teardown:{spec.MergedCwd}");
        return Task.CompletedTask;
    }
}

internal sealed class FakeWorkspaceDriverRegistry : IWorkspaceDriverRegistry
{
    private readonly Dictionary<string, IWorkspaceDriver> _drivers = new(StringComparer.Ordinal);

    public FakeWorkspaceDriverRegistry(params IWorkspaceDriver[] drivers)
    {
        foreach (var d in drivers)
        {
            _drivers[d.DriverId] = d;
        }
    }

    public void Register(IWorkspaceDriver driver) => _drivers[driver.DriverId] = driver;

    public IWorkspaceDriver GetRequired(string driverId)
    {
        if (!_drivers.TryGetValue(driverId, out var d))
        {
            throw new KeyNotFoundException($"Workspace driver '{driverId}' is not registered.");
        }

        return d;
    }

    public IReadOnlyList<string> RegisteredDrivers => _drivers.Keys.ToArray();
}

internal sealed class FakeShellCommandRunner : IShellCommandRunner
{
    public ConcurrentQueue<ShellCommandRequest> ExecutedCommands { get; } = new();
    public Func<ShellCommandRequest, CancellationToken, Task<ShellCommandResult>>? Handler { get; set; }

    public Task<ShellCommandResult> RunAsync(ShellCommandRequest request, CancellationToken ct = default)
    {
        ExecutedCommands.Enqueue(request);
        if (Handler is not null)
        {
            return Handler(request, ct);
        }

        return Task.FromResult(new ShellCommandResult(0, "", "", TimedOut: false, DurationMs: 5));
    }
}

internal sealed class FakeTerminalBridgeClient : ITerminalBridgeClient
{
    public ConcurrentQueue<TermEnsureRequest> EnsureCalls { get; } = new();
    public ConcurrentQueue<TermObserveRequest> ObserveCalls { get; } = new();
    public ConcurrentQueue<TermActionRequest> ActionCalls { get; } = new();
    public ConcurrentQueue<string> KillCalls { get; } = new();

    public Func<TermObserveRequest, TermObserveResult>? ObserveHandler { get; set; }

    public Task<TermEnsureResult> EnsureAsync(TermEnsureRequest req, CancellationToken ct)
    {
        EnsureCalls.Enqueue(req);
        return Task.FromResult(new TermEnsureResult(req.TerminalId, Pid: 4242, Created: true));
    }

    public Task<TermObserveResult> ObserveAsync(TermObserveRequest req, CancellationToken ct)
    {
        ObserveCalls.Enqueue(req);
        if (ObserveHandler is not null)
        {
            return Task.FromResult(ObserveHandler(req));
        }

        var frame = new TerminalFrame(120, 40, new TerminalCursor(0, 0, true), false, 0, 0, 0, ["$ ready"]);
        return Task.FromResult(new TermObserveResult(1, frame, Exited: false, ExitCode: null));
    }

    public Task<TermActionResult> SendActionAsync(TermActionRequest req, CancellationToken ct)
    {
        ActionCalls.Enqueue(req);
        return Task.FromResult(new TermActionResult(2));
    }

    public Task ResizeAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct) =>
        Task.CompletedTask;

    public Task KillAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default)
    {
        KillCalls.Enqueue(terminalId);
        return Task.CompletedTask;
    }

    public Uri GetWebSocketRelayUri(string terminalId) =>
        new($"ws://127.0.0.1:19400/ws/{terminalId}");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeVcsService : IVcsService
{
    public Task<VcsBackendKind> DetectAsync(string path, CancellationToken ct) =>
        Task.FromResult(VcsBackendKind.JjColocated);

    public IReadOnlyList<VcsBackendKind> ListWorkspaceBackends(string path) =>
        [VcsBackendKind.JjColocated, VcsBackendKind.GitWorktree];

    public Task<IsolatedWorkspace> CreateWorkspaceAsync(string repoPath, string workspaceName, string targetDir, string? baseRev = null,
        CancellationToken ct = default, VcsBackendKind? backend = null) =>
        Task.FromResult(new IsolatedWorkspace(backend ?? VcsBackendKind.JjColocated, repoPath, workspaceName, targetDir, baseRev));

    public Task RemoveWorkspaceAsync(IsolatedWorkspace workspace, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<RevisionSnapshot> SnapshotWorkingCopyAsync(string workspacePath, string? commitMessageIfGit = null, CancellationToken ct = default) =>
        Task.FromResult(new RevisionSnapshot(VcsBackendKind.JjColocated, "kkmpptxz", "abc123", "op_1", false, []));

    public Task RestoreOperationAsync(string workspacePath, string opId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<RevisionSnapshot> RebaseOntoUpstreamAsync(string workspacePath, string upstreamBranch = "main", CancellationToken ct = default) =>
        Task.FromResult(new RevisionSnapshot(VcsBackendKind.JjColocated, "kkmpptxz", "def456", "op_2", false, []));

    public Task<DiffSummary> DiffAsync(string workspacePath, string? fromRev = null, string? toRev = null, CancellationToken ct = default) =>
        Task.FromResult(new DiffSummary($"diff in {workspacePath}", Truncated: false, ChangedFiles: ["README.md"]));

    public Task<PushResult> PushBookmarkAsync(string workspacePath, string bookmarkOrBranch, string? targetRev = null, CancellationToken ct = default) =>
        Task.FromResult(new PushResult(bookmarkOrBranch, "origin", "def456"));
}

internal sealed class KernelTestContext : IAsyncDisposable
{
    public InMemoryTrackStore Store { get; } = new();
    public InMemoryReportDocEngine ReportEngine { get; } = new();
    public ConfigurableFakePolicyVm PolicyVm { get; } = new();
    public ScriptableFakeAgentRuntime AgentRuntime { get; } = new();
    public SpyWorkspaceDriver DirectDriver { get; } = new("direct", supportsParallelWriteLeases: false);
    public SpyWorkspaceDriver CowOverlayDriver { get; } = new("cow-overlay", supportsParallelWriteLeases: true);
    public FakeWorkspaceDriverRegistry DriverRegistry { get; }
    public FakeVcsService VcsService { get; } = new();
    public FakeTerminalBridgeClient TerminalClient { get; } = new();
    public FakeShellCommandRunner CommandRunner { get; } = new();
    public FakeManualTimeProvider Time { get; } = new();
    public EventHub EventHub { get; } = new();
    public PergaKernelService Kernel { get; }

    public KernelTestContext(KernelOptions? options = null, IPolicyVm? policyVm = null)
    {
        DriverRegistry = new FakeWorkspaceDriverRegistry(DirectDriver, CowOverlayDriver);
        var effectiveOptions = options ?? new KernelOptions
        {
            ActorEvictionSweepInterval = TimeSpan.Zero // Manual deterministic eviction in unit tests unless configured
        };

        Kernel = new PergaKernelService(
            Store,
            ReportEngine,
            policyVm ?? PolicyVm,
            AgentRuntime,
            DriverRegistry,
            VcsService,
            TerminalClient,
            options: effectiveOptions,
            commandRunner: CommandRunner,
            eventHub: EventHub,
            timeProvider: Time);
    }

    public async Task<(AreaDto Area, TrackAggregateSnapshot TrackSnap)> CreateDefaultTrackAsync(
        string? templateId = null,
        string? workspaceDriver = null,
        string? cwd = "/workspace/demo")
    {
        var area = await Kernel.CreateAreaAsync(new CreateAreaRequest("Core Engineering", DefaultCwd: cwd));
        var snap = await Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            Title: "Demo Track",
            Cwd: cwd,
            WorkspaceDriver: workspaceDriver,
            TemplateId: templateId));
        await Kernel.Actors.GetOrActivate(snap.Track.Id).EnsureMountedAsync();
        return (area, await Kernel.GetTrackAggregateAsync(snap.Track.Id));
    }

    public ValueTask DisposeAsync() => Kernel.DisposeAsync();
}
