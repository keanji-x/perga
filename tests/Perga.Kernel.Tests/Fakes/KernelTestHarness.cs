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

internal sealed partial class InMemoryTrackStore : ITrackStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AreaDto> _areas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrackDto> _tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReportDocument> _reports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _templateSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<(string TrackId, int DocRev), byte[]> _revisions = new();
    private readonly Dictionary<string, List<TaskDto>> _tasksByTrack = new(StringComparer.Ordinal);
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
    public Action? AfterTemplateCommit { get; set; }

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

    public Func<IReadOnlyList<EventEnvelopeDto>, Task>? BeforeEventPublication { get; set; }

    public async Task<IReadOnlyList<EventEnvelopeDto>> ListEventsSinceAsync(long sinceEventId, string? trackId = null, int limit = 500, CancellationToken ct = default)
    {
        IReadOnlyList<EventEnvelopeDto> events;
        lock (_gate)
        {
            var q = _events.Where(e => e.EventId > sinceEventId);
            if (trackId is not null) q = q.Where(e => e.TrackId == trackId);
            events = q.OrderBy(e => e.EventId).Take(limit).ToArray();
        }
        if (BeforeEventPublication is { } before) await before(events);
        return events;
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
                list.Add(new TrackOverlayDto(tr.Id, tr.AreaId, tr.ClosedAtMs is null && isWorking, tr.ClosedAtMs is null ? openAsks : 0, 0, false, false, tr.UpdatedAtMs));
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
        var (track, report, templateSnapshot, lead, tasks, firstInput, preparation, events, _, _) = creation;
        lock (_gate)
        {
            if (key is not null && FindCreationAsync(key, ct).Result is { } replay)
                return Task.FromResult(replay);
            if (firstInput is not null)
                throw new NotSupportedException("Durable first inputs are tested with SQLite.");
            _tracks.Add(track.Id, track);
            _trackSchedules.AddRange(creation.FlowClocks);
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

    public Task<IReadOnlyList<PersistedSessionInput>> ListAcceptedTrackInputsAsync(string trackId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<PersistedSessionInput>>(_inputs.Values
            .Where(i => _sessions[i.Receipt.SessionId].TrackId == trackId && i.Receipt.TurnStatus == "queued")
            .GroupBy(i => i.Receipt.TurnId).Select(g => g.First() with { Content = string.Join("\n\n", g.Select(i => i.Content)) }).ToArray());
    }

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

    public Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct, bool writeData = false)
    {
        lock (_gate)
        {
            if (_tracks.TryGetValue(track.Id, out var existing))
            {
                if (!writeData)
                    track = track with { Data = existing.Data, DataRev = existing.DataRev };
                track = track with { FlowRev = existing.FlowRev, FlowSpecJson = existing.FlowSpecJson, FlowFileSha256 = existing.FlowFileSha256 };
            }
            _tracks[track.Id] = track;
            if (track.ClosedAtMs is not null)
            {
                for (var i = 0; i < _trackWakes.Count; i++)
                    if (_trackWakes[i] is { State: TrackWakeStates.Pending or TrackWakeStates.Delivering } w && w.TrackId == track.Id)
                        _trackWakes[i] = w with { State = TrackWakeStates.Dropped, Reason = TrackWakeReasons.TrackClosed };
                for (var i = 0; i < _trackSchedules.Count; i++)
                    if (_trackSchedules[i] is { State: TrackScheduleStates.Active } s && s.TrackId == track.Id)
                        _trackSchedules[i] = s with { State = TrackScheduleStates.Canceled, NextFireAtMs = null, UpdatedAtMs = track.UpdatedAtMs };
            }
            if (evt is not null)
            {
                AppendEventLocked(evt);
            }

            return Task.CompletedTask;
        }
    }

    public Task RecordFlowFileHashAsync(string trackId, long flowRev, string sha256, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_tracks.TryGetValue(trackId, out var track) && track.FlowRev == flowRev)
                _tracks[trackId] = track with { FlowFileSha256 = sha256 };
        }
        return Task.CompletedTask;
    }

    public Task CommitTrackFlowAsync(
        TrackDto track,
        long expectedFlowRev,
        IReadOnlyList<TaskDto> upsertedTasks,
        IReadOnlyList<TrackScheduleDto> activeFlowSchedules,
        IReadOnlyList<string> canceledFlowScheduleIds,
        IReadOnlyList<EventEnvelopeDto> events,
        CancellationToken ct) =>
        CommitTrackFlow(track, expectedFlowRev, upsertedTasks, activeFlowSchedules, canceledFlowScheduleIds, events);

    private Task CommitTrackFlow(
        TrackDto track,
        long expectedFlowRev,
        IReadOnlyList<TaskDto> upsertedTasks,
        IReadOnlyList<TrackScheduleDto> activeFlowSchedules,
        IReadOnlyList<string> canceledFlowScheduleIds,
        IReadOnlyList<EventEnvelopeDto> events,
        IReadOnlySet<string>? resetClockIds = null)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(track.Id, out var existing))
                throw KernelException.NotFound($"Track '{track.Id}' not found.");
            if (existing.FlowRev != expectedFlowRev)
                throw KernelException.Conflict($"Flow revision conflict: expected {expectedFlowRev}, current {existing.FlowRev}.");

            _tracks[track.Id] = existing with
            {
                FlowSpecJson = track.FlowSpecJson,
                FlowRev = track.FlowRev,
                FlowFileSha256 = track.FlowFileSha256,
                UpdatedAtMs = track.UpdatedAtMs
            };

            if (!_tasksByTrack.TryGetValue(track.Id, out var list))
            {
                list = [];
                _tasksByTrack[track.Id] = list;
            }

            foreach (var task in upsertedTasks)
            {
                list.RemoveAll(t => t.TaskKey == task.TaskKey && t.Id != task.Id);
                var idx = list.FindIndex(t => t.Id == task.Id);
                if (idx >= 0) list[idx] = task;
                else list.Add(task);
            }

            foreach (var schedule in activeFlowSchedules)
            {
                var idx = _trackSchedules.FindIndex(s => s.TrackId == track.Id && s.Id == schedule.Id);
                if (idx >= 0)
                {
                    var prev = _trackSchedules[idx];
                    var keepProgress = resetClockIds?.Contains(schedule.Id) != true && prev.State != TrackScheduleStates.Canceled &&
                        prev.ScheduleKind == schedule.ScheduleKind &&
                        prev.CronExpr == schedule.CronExpr &&
                        prev.Timezone == schedule.Timezone &&
                        prev.RunAtMs == schedule.RunAtMs;
                    var nextFire = schedule.MaxOccurrences is { } cap && prev.FiredCount >= cap
                        ? null : prev.NextFireAtMs ?? schedule.NextFireAtMs;
                    _trackSchedules[idx] = keepProgress
                        ? schedule with
                        {
                            State = nextFire is null ? TrackScheduleStates.Done : TrackScheduleStates.Active,
                            NextFireAtMs = nextFire,
                            LastFiredAtMs = prev.LastFiredAtMs,
                            FiredCount = prev.FiredCount,
                            CreatedAtMs = prev.CreatedAtMs
                        }
                        : schedule with { CreatedAtMs = prev.CreatedAtMs };
                }
                else
                {
                    _trackSchedules.Add(schedule);
                }
            }

            foreach (var scheduleId in canceledFlowScheduleIds)
            {
                var idx = _trackSchedules.FindIndex(s => s.TrackId == track.Id && s.Id == scheduleId && s.State == TrackScheduleStates.Active);
                if (idx >= 0)
                {
                    _trackSchedules[idx] = _trackSchedules[idx] with
                    {
                        State = TrackScheduleStates.Canceled,
                        NextFireAtMs = null,
                        UpdatedAtMs = track.UpdatedAtMs
                    };
                }
            }

            foreach (var evt in events)
            {
                AppendEventLocked(evt);
            }

            return Task.CompletedTask;
        }
    }

    public Task UpdateTrackTemplateAndEmitAsync(TrackDto track, string? templateSnapshot, EventEnvelopeDto evt, CancellationToken ct, bool writeData = false, TrackFlowUpdate? flow = null)
    {
        lock (_gate)
        {
            if (FailTemplateUpdate) throw new IOException("template persistence failure");
            _templateSnapshots[track.Id] = templateSnapshot;
            if (flow is not null)
                CommitTrackFlow(track, flow.ExpectedRev, flow.Tasks, flow.Clocks, flow.CanceledClockIds, [], flow.ResetClockIds).GetAwaiter().GetResult();
            var commit = UpdateTrackAndEmitAsync(track, evt, ct, writeData);
            AfterTemplateCommit?.Invoke();
            return commit;
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

    public Task SaveReportAsync(
        string trackId,
        ReportDocument report,
        byte[] compressedRevisionBlocks,
        string? authorSessionId,
        string commitMessage,
        EventEnvelopeDto evt,
        CancellationToken ct)
    {
        lock (_gate)
        {
            _reports[trackId] = report;
            if (authorSessionId is null) EnqueueWakeLocked(TrackWakeRequests.Report(trackId, report.DocRev, evt.Payload));
            _revisions[(trackId, report.DocRev)] = compressedRevisionBlocks.ToArray();

            AppendEventLocked(evt);
            return Task.CompletedTask;
        }
    }

    public Action<TaskDto, AgentSessionDto?>? OnBeforeUpsertTask { get; set; }

    public Task UpsertTaskAsync(
        TaskDto task,
        EventEnvelopeDto? evt,
        CancellationToken ct,
        AgentSessionDto? session = null,
        EventEnvelopeDto? sessionEvent = null, string? wakePrompt = null, SessionMessage? initialMessage = null,
        bool writeNewRunBoundary = false,
        bool suppressDefaultWake = false)
    {
        lock (_gate)
        {
            OnBeforeUpsertTask?.Invoke(task, session);
            if (session is not null)
                _sessions[session.Id] = session;
            if (sessionEvent is not null)
                AppendEventLocked(sessionEvent);
            if (initialMessage is not null) _messages.Add(Key(initialMessage), initialMessage);
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

            if (task.FinishedAtMs is not null && !suppressDefaultWake)
            {
                var child = task.SessionId is { } childId ? _sessions.GetValueOrDefault(childId) : null;
                if (child?.ParentSessionId is { } parentId)
                {
                    if (child.NotifyParent == "task_settled" && _sessions.GetValueOrDefault(parentId)?.ArchivedAtMs is null)
                        EnqueueWakeLocked(TrackWakeRequests.TaskSettled(task, wakePrompt) with { TargetSessionId = parentId }, once: true);
                }
                else EnqueueWakeLocked(TrackWakeRequests.TaskSettled(task, wakePrompt), once: true);
            }

            if (evt is not null)
            {
                AppendEventLocked(evt);
            }

            return Task.CompletedTask;
        }
    }

    public Action<AgentSessionDto>? OnBeforeUpsertSession { get; set; }

    public Task UpsertSessionAsync(AgentSessionDto session, EventEnvelopeDto? evt, CancellationToken ct)
    {
        OnBeforeUpsertSession?.Invoke(session);
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

    public Task<IReadOnlyList<(string TrackId, PersistedSessionInput Input)>> RecoverSessionInputsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var group in _inputs.Values.Where(i => i.Receipt.TurnStatus == "running").GroupBy(i => i.Receipt.TurnId).ToArray())
            {
                var first = group.First();
                var session = _sessions[first.Receipt.SessionId];
                foreach (var member in group)
                {
                    _inputs[Key(member)] = member with { Receipt = member.Receipt with { TurnStatus = "unknown",
                        DeliveryState = member.Receipt.DeliveryState == "dispatching" ? "unknown" : member.Receipt.DeliveryState } };
                }
                var turns = _turnsBySession[session.Id];
                var index = turns.FindIndex(t => t.Id == first.Receipt.TurnId);
                turns[index] = turns[index] with { Status = "unknown", ErrorText = "Host stopped during dispatch." };
                _sessions[session.Id] = session with { Status = "unknown" };
                AppendEventLocked(new(null, _tracks[session.TrackId].AreaId, session.TrackId, KernelEventKinds.TurnCompleted,
                    TestJson.FromObject(turns[index]), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
            return Task.FromResult<IReadOnlyList<(string, PersistedSessionInput)>>(_inputs.Values.Where(i => i.Receipt.TurnStatus == "queued")
                .GroupBy(i => i.Receipt.TurnId).Select(g => (_sessions[g.First().Receipt.SessionId].TrackId,
                    g.First() with { Content = string.Join("\n\n", g.Select(i => i.Content)) })).ToArray());
        }
    }

    public Task CompleteSessionResumeAsync(AgentSessionDto session, PersistedSessionInput? unresolved,
        EventEnvelopeDto sessionEvent, int expectedEpoch, CancellationToken ct, SessionItemDto? boundary = null)
    {
        lock (_gate)
        {
            if (_sessions[session.Id].Epoch != expectedEpoch)
                throw new InvalidOperationException("Session epoch changed during recovery.");
            if (boundary is not null) AppendSessionItemsAsync(session.Id, [boundary], ct).GetAwaiter().GetResult();
            if (unresolved is not null) return ResolveAsync(unresolved, session, sessionEvent, ct);
            _sessions[session.Id] = session;
            AppendEventLocked(sessionEvent);
            return Task.CompletedTask;
        }
    }

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
            return Task.FromResult(RecordTurnAndItemsLocked(turn, newItems, updatedSession));
    }

    private (SessionTurnDto Turn, IReadOnlyList<long> ItemIds) RecordTurnAndItemsLocked(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession)
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

        return (storedTurn, ids);
    }

    public Task<IReadOnlyList<long>> AppendSessionItemsAsync(
        string sessionId,
        IReadOnlyList<SessionItemDto> newItems,
        CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(AppendSessionItemsLocked(sessionId, newItems));
    }

    private IReadOnlyList<long> AppendSessionItemsLocked(string sessionId, IReadOnlyList<SessionItemDto> newItems)
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

        return ids;
    }

    public Task UpsertAskAsync(AskDto ask, EventEnvelopeDto evt, CancellationToken ct)
    {
        lock (_gate)
        {
            _asks[ask.Id] = ask;
            if (ask.Delivery == AskDelivery.Wake && ask.AnsweredAtMs is not null)
                EnqueueWakeLocked(TrackWakeRequests.Ask(ask), once: true);
            AppendEventLocked(evt);
            return Task.CompletedTask;
        }
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
                    EnqueueWakeLocked(TrackWakeRequests.Mail(mail), once: true);
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

    public Func<EventEnvelopeDto, Task>? BeforeEventAppend { get; set; }

    public async Task AppendEventAsync(EventEnvelopeDto evt, CancellationToken ct)
    {
        if (BeforeEventAppend is { } before) await before(evt);
        lock (_gate)
        {
            AppendEventLocked(evt);
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

            foreach (var kvp in _tasksByTrack)
            {
                for (var i = 0; i < kvp.Value.Count; i++)
                {
                    var t = kvp.Value[i];
                    if (t.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)
                    {
                        kvp.Value[i] = t with { Status = TaskRunStatus.Failed, StatusDetail = "interrupted", FinishedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                        affected.Add(t.TrackId);
                    }
                }
            }

            foreach (var id in affected)
                if (_tracks[id].LeadSessionId is not null) EnqueueWakeLocked(TrackWakeRequests.Boot(id));
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
        _tasksByTrack.Remove(trackId);

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
        _trackSchedules.RemoveAll(s => s.TrackId == trackId);
        _trackWakes.RemoveAll(w => w.TrackId == trackId);
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
                "Call report_read to refresh anchor.");
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
                    "Call report_read before retrying.");
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
        "checkpoints" => ReportBlockKind.Checkpoints,
        "widget" => ReportBlockKind.Widget,
        _ => ReportBlockKind.Prose
    };
}

internal sealed class ConfigurableFakePolicyVm : IPolicyVm
{
    private int _compileCount;

    public int CompileCallCount => Volatile.Read(ref _compileCount);

    public Func<string, CompiledTemplateManifest>? CustomManifestFactory { get; set; }
    public Func<CompiledPolicy, string, JsonElement, JsonElement, GuardDecision>? OnBefore { get; set; }

    public TemplateHeaderView? EvaluateHeaderView(CompiledPolicy policy, PolicyHeaderViewContext ctx, JsonElement state) => null;

    public CompiledPolicy Compile(string jsModuleSource)
    {
        Interlocked.Increment(ref _compileCount);
        var source = jsModuleSource;
        CompiledTemplateManifest manifest;
        if (CustomManifestFactory is not null)
        {
            manifest = CustomManifestFactory(source);
        }
        else
        {
            var idMatch = Regex.Match(source, @"id\s*:\s*[""']([^""']+)[""']");
            var titleMatch = Regex.Match(source, @"title\s*:\s*[""']([^""']+)[""']");
            var afterCmdMatch = Regex.Match(source, @"afterWorkerDoneCmd\s*:\s*[""']([^""']+)[""']");
            var createCmdMatch = Regex.Match(source, @"onCreateCmd\s*:\s*[""']([^""']+)[""']");
            var closeCmdMatch = Regex.Match(source, @"onCloseCmd\s*:\s*[""']([^""']+)[""']");

            var id = idMatch.Success ? idMatch.Groups[1].Value : $"tpl_{_compileCount}";
            var title = titleMatch.Success ? titleMatch.Groups[1].Value : $"Template {id}";

            manifest = new CompiledTemplateManifest(
                Id: id,
                Title: title,
                Description: "Test template",
                DefaultPlugins: ["market"],
                InputsSchema: TestJson.Parse("{}"),
                RequireGateForReadWrite: false,
                WorkspaceDriver: "direct",
                WorkspaceLowerCacheDirs: [],
                WorkspaceCreateCmd: createCmdMatch.Success ? createCmdMatch.Groups[1].Value : null,
                WorkspaceCloseCmd: closeCmdMatch.Success ? closeCmdMatch.Groups[1].Value : null,
                AfterWorkerDoneCmd: afterCmdMatch.Success ? afterCmdMatch.Groups[1].Value : null,
                Flow: TestJson.Parse("[]"),
                Routing: []);
        }

        return new CompiledPolicy(manifest.Id, jsModuleSource, manifest);
    }

    public JsonElement InitData(CompiledPolicy policy, JsonElement inputs) => TestJson.Parse("{}");
    public IReadOnlyList<DataValidationError> ValidateData(CompiledPolicy policy, JsonElement inputs, JsonElement data,
        IReadOnlyList<string>? knownTaskKeys) => [];

    public PolicyTurnPreparation PrepareTurn(CompiledPolicy policy, PolicyTurnContext ctx, JsonElement data) =>
        new(null, new GuardDecision.Allow());

    public GuardDecision Before(CompiledPolicy policy, string action, string trackId, JsonElement payload, JsonElement inputs, JsonElement data) =>
        OnBefore?.Invoke(policy, action, payload, data) ?? new GuardDecision.Allow();

    public PolicyTaskRoute RouteTaskDispatch(CompiledPolicy policy, string? role, string access, string? provider, string? model, JsonElement inputs) => new(provider, model);
    public IReadOnlyList<PolicyTaskRoute> ResolveRoutingTargets(CompiledPolicy policy, JsonElement inputs) => [];

    public IReadOnlyList<FlowNodeSpecDto> ResolveFlow(CompiledPolicy policy, JsonElement inputs) => [];

    public void ValidateInputs(CompiledPolicy policy, JsonElement inputs) => new PolicyVm.PolicyVm().ValidateInputs(policy, inputs);
    public string InterpolateInputs(CompiledPolicy policy, string text, JsonElement inputs) => new PolicyVm.PolicyVm().InterpolateInputs(policy, text, inputs);

    public DataPatchValidationResult ValidateAndPatchData(
        CompiledPolicy policy,
        JsonElement inputs,
        JsonElement currentData,
        JsonElement patch,
        IReadOnlyList<string>? knownTaskKeys,
        string nowIso,
        string source) =>
        new(true, patch.Clone(), []);

    public TemplateViewsProjection ProjectViews(
        CompiledPolicy policy,
        JsonElement inputs,
        JsonElement currentData,
        JsonElement kernelSnapshot) =>
        new(
            policy.TemplateId,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
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
        ConfigurationState = new(SessionConfiguration.FromLaunch(launchSpec));
        NativeThreadId = initialNativeThreadId;
        _turnHandler = turnHandler;
    }

    private Action<IdleContextUsage>? _idleUsageHandler;
    private readonly object _approvalLock = new();
    private readonly HashSet<string> _seenApprovalRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingApprovalRequests = new(StringComparer.Ordinal);
    private bool _approvalsActive;

    internal Func<CancellationToken, Task>? InitializeHandler { get; set; }
    public Task InitializeAsync(CancellationToken ct) => InitializeHandler?.Invoke(ct) ?? Task.CompletedTask;

    public HarnessLaunchSpec LaunchSpec { get; }
    public IReadOnlyList<PermissionModeOption> AvailablePermissionModes { get; set; } = [];
    public string? NativeThreadId { get; set; }
    public bool SupportsNativeSteer { get; set; } = true;
    public bool CanReuseContextWindow => true;
    public bool SupportsNativeCompact { get; set; }
    public bool SupportsSessionResume { get; set; }
    public bool SupportsScopedClose { get; set; }
    public Func<CancellationToken, Task<bool>>? CloseSessionHandler { get; set; }
    public Task<bool> TryCloseSessionAsync(CancellationToken ct) =>
        CloseSessionHandler is not null ? CloseSessionHandler(ct) : Task.FromResult(SupportsScopedClose);
    public Func<ScriptableFakeAgentSession, CancellationToken, ValueTask<bool>>? ResolveNativeCompactSupportHandler { get; set; }
    public Func<ScriptableFakeAgentSession, CancellationToken, Task<NativeCompactResult>>? CompactHandler { get; set; }
    public IdleContextUsage? PendingIdleUsage { get; set; }
    public int CompactCalls { get; private set; }
    public bool Disposed { get; private set; }
    public bool CancelCalled { get; private set; }
    public Action? OnCancelTurn { get; set; }
    public ConcurrentQueue<string> SteeredMessages { get; } = new();
    public ConcurrentQueue<(string RequestId, bool Approved, string? Reason)> Approvals { get; } = new();
    public ConcurrentQueue<(string RequestId, bool Approved, string? SelectedOptionId, string? Reason)> DetailedApprovals { get; } = new();
    public ConcurrentQueue<TurnInput> RecordedInputs { get; } = new();

    public async IAsyncEnumerable<UnifiedAgentEvent> StreamTurnAsync(
        TurnInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        RecordedInputs.Enqueue(input);
        lock (_approvalLock)
            _approvalsActive = !Disposed;
        try
        {
            await foreach (var evt in _turnHandler(this, input, ct).ConfigureAwait(false))
            {
                lock (_approvalLock)
                {
                    if (evt is UnifiedAgentEvent.ApprovalRequested request
                        && _seenApprovalRequests.Add(request.RequestId) && _approvalsActive)
                        _pendingApprovalRequests.Add(request.RequestId);
                    if (evt is UnifiedAgentEvent.TurnCompleted or UnifiedAgentEvent.TurnInterrupted)
                        ExpireApprovals();
                }
                yield return evt;
            }
        }
        finally
        {
            lock (_approvalLock)
                ExpireApprovals();
        }
    }

    // Called under _approvalLock, shared by stream completion, cancellation and disposal.
    private void ExpireApprovals()
    {
        _approvalsActive = false;
        _pendingApprovalRequests.Clear();
    }

    public Task CancelTurnAsync(CancellationToken ct)
    {
        lock (_approvalLock)
        {
            ExpireApprovals();
            CancelCalled = true;
        }
        OnCancelTurn?.Invoke();
        return Task.CompletedTask;
    }

    public Func<Task>? TryClaimApprovalResponse(
        string requestId, bool approved, string? selectedOptionId, string? reason, CancellationToken ct)
    {
        lock (_approvalLock)
        {
            if (Disposed || !_approvalsActive || !_pendingApprovalRequests.Remove(requestId))
                return null;
        }
        return () =>
        {
            Approvals.Enqueue((requestId, approved, reason));
            DetailedApprovals.Enqueue((requestId, approved, selectedOptionId, reason));
            return Task.CompletedTask;
        };
    }

    public bool SupportsModelSwitch { get; set; }
    public ConcurrentQueue<string?> ModelSwitches { get; } = new();
    public SessionConfigurationCapabilities ConfigurationCapabilities => SessionConfigurationCapabilities.LaunchOnly with
    {
        Model = SessionConfigurationCapabilities.LaunchDimension() with { LiveChange = SupportsModelSwitch ? ConfigurationSupport.Supported : ConfigurationSupport.Unsupported },
        Effort = SessionConfigurationCapabilities.LaunchDimension() with { LiveChange = SupportsModelSwitch ? ConfigurationSupport.Supported : ConfigurationSupport.Unsupported }
    };
    public ConfirmedConfiguration? ConfigurationState { get; set; }
    public Func<SessionConfiguration, SessionConfigurationOutcome>? ConfigureHandler { get; set; }
    public ConcurrentQueue<SessionConfiguration> Configurations { get; } = new();
    public Task<SessionConfigurationOutcome> ConfigureAsync(SessionConfiguration configuration, CancellationToken ct = default)
    {
        if (ConfigurationState?.Selection == configuration)
            return Task.FromResult<SessionConfigurationOutcome>(new SessionConfigurationOutcome.Applied(ConfigurationState));
        Configurations.Enqueue(configuration);
        if (ConfigurationState?.Selection.Permission == configuration.Permission)
            ModelSwitches.Enqueue(configuration.Model.ValueOrNull);
        var outcome = ConfigureHandler?.Invoke(configuration) ?? ((SupportsModelSwitch && ConfigurationState?.Selection.Permission == configuration.Permission) || ConfigurationState?.Selection == configuration
            ? (SessionConfigurationOutcome)new SessionConfigurationOutcome.Applied(new(configuration))
            : new SessionConfigurationOutcome.RequiresReopen("Scripted launch-only configuration."));
        ConfigurationState = outcome is SessionConfigurationOutcome.Applied applied ? applied.Configuration : null;
        return Task.FromResult(outcome);
    }

    public ValueTask<bool> ResolveNativeCompactSupportAsync(CancellationToken ct) =>
        ResolveNativeCompactSupportHandler is not null
            ? ResolveNativeCompactSupportHandler(this, ct)
            : ValueTask.FromResult(SupportsNativeCompact);

    public Action? OnAttachIdleContextUsage { get; set; }

    public IReadOnlyList<IdleContextUsage> AttachIdleContextUsageHandler(Action<IdleContextUsage>? handler)
    {
        if (handler is not null) OnAttachIdleContextUsage?.Invoke();
        _idleUsageHandler = handler;
        if (handler is null)
        {
            return [];
        }

        var pending = PendingIdleUsage;
        PendingIdleUsage = null;
        return pending is null ? [] : [pending];
    }

    public void EmitIdleContextUsage(IdleContextUsage usage) => _idleUsageHandler?.Invoke(usage);
    private Action<UnifiedAgentEvent>? _runtimeActivityHandler;
    public IReadOnlyList<UnifiedAgentEvent> PendingRuntimeActivity { get; set; } = [];
    public IReadOnlyList<UnifiedAgentEvent> AttachRuntimeActivityHandler(Action<UnifiedAgentEvent>? handler)
    {
        _runtimeActivityHandler = handler;
        var pending = PendingRuntimeActivity;
        PendingRuntimeActivity = [];
        return handler is null ? [] : pending;
    }
    public void EmitRuntimeActivity(UnifiedAgentEvent observation) => _runtimeActivityHandler?.Invoke(observation);

    public Task<NativeCompactResult> CompactAsync(CancellationToken ct)
    {
        CompactCalls++;
        return CompactHandler is not null
            ? CompactHandler(this, ct)
            : Task.FromResult(new NativeCompactResult(null, "Context compacted."));
    }

    public SteerDelivery SteerResult { get; set; } = SteerDelivery.Accepted;

    public Task<SteerDelivery> SteerAsync(string turnCorrelationId, string message, CancellationToken ct)
    {
        if (!SupportsNativeSteer)
            return Task.FromResult(SteerDelivery.Declined);

        SteeredMessages.Enqueue(message);
        return Task.FromResult(SteerResult);
    }

    private bool? _shutdownConfirmed;
    public bool ShutdownConfirmed { get => _shutdownConfirmed ?? Disposed; set => _shutdownConfirmed = value; }
    public bool FailDisposal { get; set; }
    public Func<Task>? DisposeHandler { get; set; }
    public int DisposeCalls { get; private set; }

    public Task? DisposeTask { get; private set; }
    public ValueTask DisposeAsync() => new(DisposeTask = DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        DisposeCalls++;
        if (FailDisposal) throw new IOException("shutdown failed");
        if (DisposeHandler is not null) await DisposeHandler();
        lock (_approvalLock)
        {
            Disposed = true;
            ExpireApprovals();
        }
    }
}

internal sealed class ScriptableFakeAgentRuntime : IAgentRuntime
{
    public bool CanResumeSession(string provider) => SupportsSessionResume;
    private int _threadCounter;
    public bool SupportsSessionResume { get; set; }

    public ConcurrentQueue<ScriptableFakeAgentSession> OpenedSessions { get; } = new();
    public Action<HarnessLaunchSpec>? OnBeforeOpenSession { get; set; }
    public Func<Task>? BeforeOpenAsync { get; set; }
    public Action<ScriptableFakeAgentSession>? OnOpenedSession { get; set; }

    public Func<ScriptableFakeAgentSession, TurnInput, CancellationToken, IAsyncEnumerable<UnifiedAgentEvent>>? TurnScript { get; set; }

    public Func<CancellationToken, Task<IReadOnlyList<ProviderStatus>>>? ProbeScript { get; set; }

    // Most kernel tests use synthetic model IDs. Tests of discovery and validation supply a catalog.
    public IReadOnlyList<ProviderStatus> Providers { get; set; } =
    [
        new("codex", true, "1.0.0", null, []),
        new("claude", true, "1.0.0", null, []),
        new("acp:custom", true, "1.0.0", null, []),
        new("acp:other", true, "1.0.0", null, [])
    ];

    public int ProviderProbeCalls { get; private set; }
    public Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct)
    {
        ProviderProbeCalls++;
        return ProbeScript?.Invoke(ct) ?? Task.FromResult(Providers);
    }

    public IAgentSession CreateSession(HarnessLaunchSpec spec)
    {
        OnBeforeOpenSession?.Invoke(spec);
        var threadId = spec.ResumeNativeThreadId ?? $"thr_{Interlocked.Increment(ref _threadCounter)}";
        var session = new ScriptableFakeAgentSession(
            spec,
            threadId,
            (s, inp, token) => (TurnScript ?? DefaultTurnScript)(s, inp, token));
        session.SupportsSessionResume = SupportsSessionResume;
        session.InitializeHandler = async ct =>
        {
            if (BeforeOpenAsync is not null) await BeforeOpenAsync();
        };
        OpenedSessions.Enqueue(session);
        OnOpenedSession?.Invoke(session);
        return session;
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
    public SpyWorkspaceDriver(string driverId = "direct")
    {
        DriverId = driverId;
    }

    public string DriverId { get; }
    public IReadOnlyList<WorkspaceOverlayLayer>? OverlayLayersToInject { get; set; }
    public string? OverrideMountedMergedCwd { get; set; }
    public bool VerifyMountOnAccess { get; set; }
    public Task? ProvisionGate { get; set; }
    public Exception? TeardownError { get; set; }

    public ConcurrentQueue<WorkspaceProvisionRequest> ProvisionCalls { get; } = new();
    public ConcurrentQueue<WorkspaceSpec> EnsureMountedCalls { get; } = new();
    public ConcurrentQueue<WorkspaceSpec> TeardownCalls { get; } = new();
    public ConcurrentQueue<string> LifecycleTimeline { get; } = new();

    public async Task<WorkspaceSpec> ProvisionTrackAsync(WorkspaceProvisionRequest req, CancellationToken ct = default)
    {
        ProvisionCalls.Enqueue(req);
        if (ProvisionGate is { } gate) await gate.WaitAsync(ct);
        LifecycleTimeline.Enqueue($"provision:{req.TrackId}");
        var merged = OverrideMountedMergedCwd ?? req.SourcePath;
        return new WorkspaceSpec(
            DriverId: DriverId,
            LogicalRoot: req.SourcePath,
            LowerDirs: req.LowerCacheDirs,
            UpperDir: null,
            WorkDir: null,
            MergedCwd: merged,
            Metadata: TestJson.Parse("{}"));
    }

    public Task<MountedWorkspace> EnsureMountedAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        EnsureMountedCalls.Enqueue(spec);
        LifecycleTimeline.Enqueue($"ensure_mounted:{spec.MergedCwd}");
        var merged = OverrideMountedMergedCwd ?? spec.MergedCwd;
        return Task.FromResult(new MountedWorkspace(DriverId, merged, OverlayLayersToInject));
    }

    public Task TeardownTrackAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        if (TeardownError is { } error) throw error;
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

internal sealed partial class KernelTestContext : IAsyncDisposable
{
    public InMemoryTrackStore Store { get; } = new();
    public InMemoryReportDocEngine ReportEngine { get; } = new();
    public ConfigurableFakePolicyVm PolicyVm { get; } = new();
    public ScriptableFakeAgentRuntime AgentRuntime { get; } = new();
    public SpyWorkspaceDriver DirectDriver { get; } = new("direct");
    public SpyWorkspaceDriver CowOverlayDriver { get; } = new("cow-overlay");
    public FakeWorkspaceDriverRegistry DriverRegistry { get; }
    public FakeVcsService VcsService { get; } = new();
    public FakeTerminalBridgeClient TerminalClient { get; } = new();
    public FakeShellCommandRunner CommandRunner { get; } = new();
    public FakeManualTimeProvider Time { get; } = new();
    public EventHub EventHub { get; } = new();
    public string DataDir { get; }
    public PergaKernelService Kernel { get; }

    public KernelTestContext(
        KernelOptions? options = null,
        IPolicyVm? policyVm = null,
        IAgentRuntime? agentRuntime = null,
        TimeProvider? timeProvider = null,
        IGitHubForgeService? gitHubForgeService = null,
        IMcpSessionBridge? mcpSessionBridge = null)
    {
        DriverRegistry = new FakeWorkspaceDriverRegistry(DirectDriver, CowOverlayDriver);
        DataDir = Path.Combine(Path.GetTempPath(), $"perga-kernel-test-{Guid.NewGuid():N}");
        var effectiveOptions = (options ?? new KernelOptions
        {
            ActorEvictionSweepInterval = TimeSpan.Zero // Manual deterministic eviction in unit tests unless configured
        }) with
        {
            DataDir = options?.DataDir ?? DataDir
        };

        Kernel = new PergaKernelService(
            Store,
            ReportEngine,
            policyVm ?? PolicyVm,
            agentRuntime ?? AgentRuntime,
            DriverRegistry,
            VcsService,
            TerminalClient,
            options: effectiveOptions,
            commandRunner: CommandRunner,
            eventHub: EventHub,
            timeProvider: timeProvider ?? Time,
            scheduleCronEvaluator: new TestScheduleCronEvaluator(),
            gitHubForgeService: gitHubForgeService,
            mcpSessionBridge: mcpSessionBridge);
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

    public async ValueTask DisposeAsync()
    {
        await Kernel.DisposeAsync();
        try
        {
            if (Directory.Exists(DataDir))
                Directory.Delete(DataDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }
}

internal sealed class TestScheduleCronEvaluator : IScheduleCronEvaluator
{
    public bool TryValidate(string cron, string timezone, out string normalizedTimezone, out string? error)
    {
        normalizedTimezone = timezone; error = null; return true;
    }
    public long? GetNextOccurrenceMs(string cron, string timezone, long afterMs) => afterMs + 60_000;
}
