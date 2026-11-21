using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Templates;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private readonly TrackFlowFileStore _flowFileStore;
    private readonly Dictionary<string, ActiveWatchState> _activeWatches = new(StringComparer.Ordinal);
    private FlowSpecDto _flowSpec = new(0, [], FlowConstants.SchemaPointer);
    private async Task HydrateFlowCoreAsync(CancellationToken ct)
    {
        _flowSpec = TrackFlowParser.ParsePersisted(_track.FlowSpecJson, _track.FlowRev);
        _flowFileStore.EnsureSchemaMaterialized();
        await ProjectCommittedFlowFileCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task ProjectCommittedFlowFileCoreAsync(CancellationToken ct)
    {
        var canonical = TrackFlowParser.SerializeCanonical(_flowSpec);
        var canonicalSha = TrackFlowFileStore.ComputeSha256Hex(canonical);
        try
        {
            var (_, created, _) = _flowFileStore.EnsureFlowFileMaterialized(TrackId, _flowSpec);
            var onDisk = created ? canonicalSha : _flowFileStore.ReadFlowFile(TrackId).Sha256;
            if (onDisk != canonicalSha &&
                !_flowFileStore.TryWriteFlowFileIfUnmodified(TrackId, canonical, _track.FlowFileSha256).Written)
            {
                return;
            }

            if (_track.FlowFileSha256 != canonicalSha)
            {
                await _store.RecordFlowFileHashAsync(TrackId, _track.FlowRev, canonicalSha, ct).ConfigureAwait(false);
                _track = _track with { FlowFileSha256 = canonicalSha };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not project committed flow for Track {0}: {1}", TrackId, ex.Message);
        }
    }

    public Task<FlowSnapshotDto> GetFlowSnapshotAsync(CancellationToken ct = default) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await ProjectCommittedFlowFileCoreAsync(ct).ConfigureAwait(false);
            var projection = await BuildFlowProjectionCoreAsync(ct).ConfigureAwait(false);
            var spec = JsonHelpers.ParseClone(TrackFlowParser.SerializeCanonical(_flowSpec));
            return new FlowSnapshotDto(_track.FlowRev, _flowFileStore.GetFlowFilePath(TrackId), spec, projection);
        }, ct);

    public async Task<FlowCommitResultDto> CommitFlowAsync(
        bool dryRun,
        long? expectedFlowRev,
        InteropPrincipal caller,
        CancellationToken ct = default)
    {
        var (result, cleanup) = await EnqueueAsync<(FlowCommitResultDto, Task)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireFlowGovernanceCore(caller, KernelMcpToolNames.FlowCommit);
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot commit flow on a closed Track.");

            RequirePolicyAvailableCore();
            var (rawText, draftSha) = _flowFileStore.ReadFlowFile(TrackId);
            var (parsedSpec, errors) = TrackFlowParser.Parse(rawText);
            if (parsedSpec is not null)
            {
                var expectedRev = expectedFlowRev ?? parsedSpec.Rev;
                if (expectedRev != _track.FlowRev)
                    throw KernelException.Conflict($"Flow revision conflict: expected {expectedRev}, current {_track.FlowRev}. Re-read flow.json and retry.");

                errors = TrackFlowValidator.Validate(
                    parsedSpec, _flowSpec, _tasks, CollectActiveFlowTaskIdsCore(), _sessions,
                    _track.LeadSessionId, _policy, _policyVm, _track.TemplateInputs, _scheduleCronEvaluator, _timeProvider);
                if (errors.Count == 0)
                    return await ApplyFlowSpecCoreAsync(parsedSpec, dryRun, draftSha, ct).ConfigureAwait(false);
            }

            var projection = await BuildFlowProjectionCoreAsync(ct).ConfigureAwait(false);
            return (new FlowCommitResultDto(false, dryRun, _track.FlowRev, new([], [], [], [], []), errors, projection), Task.CompletedTask);
        }, ct).ConfigureAwait(false);

        await cleanup.ConfigureAwait(false);
        return result;
    }

    public async Task<FlowCommitResultDto> CancelFlowNodeAsync(
        string nodeId,
        InteropPrincipal caller,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        var (result, cleanup) = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireFlowGovernanceCore(caller, KernelMcpToolNames.FlowCommit);
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot cancel a flow node on a closed Track.");
            if (!_flowSpec.Nodes.Any(n => n.Id == nodeId))
                throw KernelException.NotFound($"Flow node '{nodeId}' was not found on track '{TrackId}'.");

            var nodes = _flowSpec.Nodes.Select(n => n.Id == nodeId ? n with { Canceled = true } : n).ToArray();
            return await ApplyFlowSpecCoreAsync(_flowSpec with { Nodes = nodes }, false, _track.FlowFileSha256, ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        await cleanup.ConfigureAwait(false);
        return result;
    }

    private async Task<(FlowCommitResultDto Result, Task Cleanup)> ApplyFlowSpecCoreAsync(
        FlowSpecDto proposed, bool dryRun, string? sourceSha, CancellationToken ct)
    {
        var prunedIds = TrackFlowReconciler.ComputeGcEligibleNodeIds(proposed, _tasks, CollectActiveFlowTaskIdsCore());
        var plan = TrackFlowReconciler.ComputePlanDiff(_flowSpec, proposed, prunedIds);
        var nextSpec = new FlowSpecDto(
            dryRun ? _track.FlowRev : checked(_track.FlowRev + 1),
            proposed.Nodes.Where(n => !prunedIds.Contains(n.Id)).ToArray(),
            FlowConstants.SchemaPointer);
        if (dryRun)
        {
            var preview = TrackFlowReconciler.BuildProjection(
                nextSpec, _flowFileStore.GetFlowFilePath(TrackId), _tasks, _sessions,
                await ListTrackSchedulesSafeCoreAsync(ct).ConfigureAwait(false), _activeWatches, InterpolateFlowTextCore);
            return (new FlowCommitResultDto(true, true, _track.FlowRev, plan, [], preview), Task.CompletedTask);
        }

        var canceledNodes = proposed.Nodes.Where(n => n.Canceled).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var canceledRuns = _tasks.Where(t => canceledNodes.Contains(t.TaskKey) && _activeTasks.ContainsKey(t.Id))
            .Select(t => _activeTasks[t.Id]).ToArray();
        RequireTasksOutsideVerification(canceledRuns);

        var now = NowMs();
        var tasksByKey = BuildCurrentTasksByKeyCore();
        var activeIds = nextSpec.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var archivedIds = _flowSpec.Nodes.Select(n => n.Id).Where(id => !activeIds.Contains(id)).Concat(prunedIds)
            .ToHashSet(StringComparer.Ordinal);
        var upsertedTasks = archivedIds.Where(tasksByKey.ContainsKey).Select(id => tasksByKey[id])
            .Where(task => task.ArchivedAtMs is null)
            .Select(task => task with { Ready = false, ArchivedAtMs = now, UpdatedAtMs = now }).ToList();
        var schedules = new List<TrackScheduleDto>();
        var canceledClocks = archivedIds.Select(FlowConstants.MakeScheduleId).ToList();
        var previousNodes = _flowSpec.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var node in nextSpec.Nodes)
        {
            tasksByKey.TryGetValue(node.Id, out var existing);
            if (existing is not null && previousNodes.TryGetValue(node.Id, out var previous) &&
                !TrackFlowValidator.WhenEquals(previous.When, node.When))
                existing = existing with { Ready = false, TriggerRunKey = null };
            upsertedTasks.Add(TrackFlowReconciler.BuildOrUpdateTaskForNode(TrackId, node, existing, now));
            var schedule = TrackFlowReconciler.BuildScheduleForNode(TrackId, node, now, _scheduleCronEvaluator, _timeProvider);
            if (schedule is not null)
                schedules.Add(schedule);
            else
                canceledClocks.Add(FlowConstants.MakeScheduleId(node.Id));
        }

        var events = await PersistFlowCoreAsync(nextSpec, sourceSha, upsertedTasks, schedules, canceledClocks, now, ct)
            .ConfigureAwait(false);
        foreach (var run in canceledRuns)
            BeginTaskCancellationCore(run);
        foreach (var nodeId in canceledNodes)
            CancelWatchForNodeCore(nodeId);
        var cleanup = canceledRuns.Length > 0
            ? TrackBackgroundJob(() => FinishFlowCancellationAsync(canceledRuns))
            : Task.CompletedTask;
        if (canceledNodes.Count > 0 && canceledRuns.Length == 0)
            _ = TrackBackgroundJob(() => TriggerSchedulerAsync(_actorShutdownCts.Token));

        await PublishFlowCommitCoreAsync(events).ConfigureAwait(false);
        await EvaluateFlowAfterTriggersCoreAsync(CancellationToken.None).ConfigureAwait(false);
        await ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
        var projection = await BuildFlowProjectionCoreAsync(CancellationToken.None).ConfigureAwait(false);
        return (new FlowCommitResultDto(true, false, _track.FlowRev, plan, [], projection), cleanup);
    }

    private async Task FinishFlowCancellationAsync(ActiveTaskRunState[] runs)
    {
        foreach (var run in runs)
        {
            await CloseCanceledWorkerAsync(run).ConfigureAwait(false);
            await EnqueueAsync(async _ =>
            {
                if (_activeTasks.TryGetValue(run.TaskId, out var current) && ReferenceEquals(current, run))
                    await FinalizeCanceledTaskCoreAsync(run, CancellationToken.None).ConfigureAwait(false);
                await ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await TryRunFlowGcCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<EventEnvelopeDto[]> PersistFlowCoreAsync(
        FlowSpecDto nextSpec,
        string? sourceSha,
        IReadOnlyList<TaskDto> tasks,
        IReadOnlyList<TrackScheduleDto> schedules,
        IReadOnlyList<string> canceledClocks,
        long now,
        CancellationToken ct)
    {
        // Persist the accepted source hash before projecting the canonical file. Hydration can
        // finish an interrupted write only while the file still contains those accepted bytes.
        var nextTrack = _track with
        {
            FlowSpecJson = TrackFlowParser.SerializeCanonical(nextSpec),
            FlowRev = nextSpec.Rev,
            FlowFileSha256 = sourceSha,
            UpdatedAtMs = now
        };
        EventEnvelopeDto[] events =
        [
            new(null, nextTrack.AreaId, TrackId, KernelEventKinds.TrackUpdated,
                JsonHelpers.ToElement(TemplatePresentation.Project(nextTrack, _policy, _policyVm)), now),
            CreateTaskUpdatedEventCore(now)
        ];
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.CommitTrackFlowAsync(nextTrack, _track.FlowRev, tasks, schedules, canceledClocks, events, ct)
            .ConfigureAwait(false);
        _track = nextTrack;
        _flowSpec = nextSpec;
        foreach (var task in tasks)
            ReplaceTaskInMemoryCore(task);
        return events;
    }

    private async Task PublishFlowCommitCoreAsync(EventEnvelopeDto[] events)
    {
        await ProjectCommittedFlowFileCoreAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var evt in events)
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
    }

    internal Task<bool> FireFlowScheduleAsync(FiredTrackSchedule due, long now, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_track.ClosedAtMs is not null || _store.TrackSchedules is not { } store || due.DueAtMs is not { } dueAt)
                return false;

            var clock = await store.GetAsync(TrackId, due.ScheduleId, ct).ConfigureAwait(false);
            var node = _flowSpec.Nodes.FirstOrDefault(n => n.Id == due.NodeKey);
            if (node is null || node.Canceled || clock is null || clock.NodeKey != node.Id ||
                clock.State != TrackScheduleStates.Active || clock.NextFireAtMs != dueAt)
                return false;

            var existing = _tasks.FirstOrDefault(t => t.TaskKey == node.Id && t.ArchivedAtMs is null);
            var busy = existing is not null && (_activeTasks.ContainsKey(existing.Id) ||
                _activeWatches.ContainsKey(existing.Id) || existing.Status is TaskRunStatus.Running or TaskRunStatus.Verifying ||
                (existing.Status == TaskRunStatus.Pending && existing.Ready));

            var count = checked(clock.FiredCount + 1);
            var nextFire = clock.CronExpr is { } cron && (clock.MaxOccurrences is null || count < clock.MaxOccurrences)
                ? _scheduleCronEvaluator?.GetNextOccurrenceMs(cron, clock.Timezone, now)
                : null;
            var nextClock = clock with
            {
                FiredCount = count,
                LastFiredAtMs = now,
                NextFireAtMs = nextFire,
                State = nextFire is null ? TrackScheduleStates.Done : TrackScheduleStates.Active,
                UpdatedAtMs = now
            };
            TaskDto? armed = busy ? null : (existing ?? TrackFlowReconciler.BuildOrUpdateTaskForNode(TrackId, node, null, now)) with
            {
                Ready = true,
                Status = TaskRunStatus.Pending,
                StatusDetail = null,
                Outcome = FlowOutcome.None,
                OutcomeSummary = null,
                SemanticVerdict = SemanticVerdict.None,
                SemanticNote = null,
                FinishedAtMs = null,
                UpdatedAtMs = now
            };
            var evt = CreateTaskUpdatedEventCore(now);
            if (!await store.CommitFlowFireAsync(nextClock, dueAt, armed, evt, ct).ConfigureAwait(false))
                return false;
            if (armed is not null)
            {
                ReplaceTaskInMemoryCore(armed);
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
                await ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return true;
        }, ct);

    private async Task<FlowProjectionDto> BuildFlowProjectionCoreAsync(CancellationToken ct)
    {
        var schedules = await ListTrackSchedulesSafeCoreAsync(ct).ConfigureAwait(false);
        return TrackFlowReconciler.BuildProjection(
            _flowSpec,
            _flowFileStore.GetFlowFilePath(TrackId),
            _tasks,
            _sessions,
            schedules,
            _activeWatches,
            InterpolateFlowTextCore);
    }

    private async Task<IReadOnlyList<TrackScheduleDto>> ListTrackSchedulesSafeCoreAsync(CancellationToken ct)
    {
        if (_store.TrackSchedules is not { } scheduleStore)
        {
            return [];
        }

        return await scheduleStore.ListForTrackAsync(TrackId, includeTerminal: true, ct).ConfigureAwait(false);
    }

    private async Task OnTaskSettledFlowHookCoreAsync(TaskDto settledTask, CancellationToken ct)
    {
        if (_flowSpec.Nodes.Count == 0)
        {
            return;
        }

        var armedAny = await EvaluateFlowAfterTriggersCoreAsync(ct).ConfigureAwait(false);
        if (armedAny)
        {
            await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
        }

        await TryRunFlowGcCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> EvaluateFlowAfterTriggersCoreAsync(CancellationToken ct)
    {
        if (_flowSpec.Nodes.Count == 0 || _track.ClosedAtMs is not null)
        {
            return false;
        }

        var tasksByKey = BuildCurrentTasksByKeyCore();
        var now = NowMs();
        var armedAny = false;

        foreach (var node in _flowSpec.Nodes)
        {
            if (node.Canceled || node.When?.After is not { Count: > 0 } afterList)
            {
                continue;
            }

            if (!tasksByKey.TryGetValue(node.Id, out var task) || task.ArchivedAtMs is not null)
            {
                continue;
            }

            if (_activeTasks.ContainsKey(task.Id) || _activeWatches.ContainsKey(task.Id) ||
                task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying ||
                (task.Status == TaskRunStatus.Pending && task.Ready))
            {
                continue;
            }

            var allSatisfied = true;
            foreach (var dep in afterList)
            {
                if (!tasksByKey.TryGetValue(dep.Node, out var depTask) ||
                    !FlowConstants.OutcomeMatchesCondition(depTask, dep.On))
                {
                    allSatisfied = false;
                    break;
                }
            }

            if (!allSatisfied)
            {
                continue;
            }

            var waveKey = FlowConstants.ComputeWaveTriggerKey(afterList, tasksByKey);
            if (string.Equals(task.TriggerRunKey, waveKey, StringComparison.Ordinal))
            {
                // Already triggered for this exact upstream wave.
                continue;
            }

            var armed = task with
            {
                Ready = true,
                Status = TaskRunStatus.Pending,
                StatusDetail = null,
                Outcome = FlowOutcome.None,
                OutcomeSummary = null,
                SemanticVerdict = SemanticVerdict.None,
                SemanticNote = null,
                TriggerRunKey = waveKey,
                FinishedAtMs = null,
                UpdatedAtMs = now
            };
            await PersistAndEmitTaskUpdatedCoreAsync(armed, null, now, ct).ConfigureAwait(false);
            ReplaceTaskInMemoryCore(armed);
            tasksByKey[node.Id] = armed;
            armedAny = true;
        }

        return armedAny;
    }

    private async Task TryRunFlowGcCoreAsync(CancellationToken ct)
    {
        if (_flowSpec.Nodes.Count == 0)
        {
            return;
        }

        var activeIds = CollectActiveFlowTaskIdsCore();
        var eligible = TrackFlowReconciler.ComputeGcEligibleNodeIds(_flowSpec, _tasks, activeIds);
        if (eligible.Count == 0)
        {
            return;
        }

        // Leave both the draft and its committed graph intact until the Lead submits it.
        if (_track.FlowFileSha256 is not { } cleanSha)
            return;
        try
        {
            if (_flowFileStore.ReadFlowFile(TrackId).Sha256 != cleanSha)
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KernelException)
        {
            return;
        }

        var now = NowMs();
        var remainingNodes = _flowSpec.Nodes.Where(n => !eligible.Contains(n.Id)).ToArray();
        var nextSpec = new FlowSpecDto(_track.FlowRev, remainingNodes, FlowConstants.SchemaPointer);
        var tasksByKey = BuildCurrentTasksByKeyCore();
        var archivedTasks = eligible.Where(tasksByKey.ContainsKey).Select(id => tasksByKey[id])
            .Where(task => task.ArchivedAtMs is null)
            .Select(task => task with { Ready = false, ArchivedAtMs = now, UpdatedAtMs = now }).ToArray();
        var canceledClocks = eligible.Select(FlowConstants.MakeScheduleId).ToArray();
        var events = await PersistFlowCoreAsync(nextSpec, _track.FlowFileSha256, archivedTasks, [], canceledClocks, now, ct)
            .ConfigureAwait(false);
        await PublishFlowCommitCoreAsync(events).ConfigureAwait(false);
    }

    private string? InterpolateFlowTextCore(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (_policy is not null && text.Contains("${inputs.", StringComparison.Ordinal))
        {
            try
            {
                return _policyVm.InterpolateInputs(_policy, text, _track.TemplateInputs);
            }
            catch
            {
                // Fall back to pure JSON interpolation if policy VM rejects an unknown input key.
            }
        }

        return FlowConstants.InterpolateInputs(text, _track.TemplateInputs);
    }

    private TaskDto InterpolateFlowTaskForExecutionCore(TaskDto task)
    {
        if (!FlowConstants.IsFlowTask(task))
        {
            return task;
        }

        var goal = InterpolateFlowTextCore(task.GoalOrCmd) ?? task.GoalOrCmd;
        var acceptance = InterpolateFlowTextCore(task.Acceptance);
        GateSpecDto? gate = task.Gate;
        if (gate is { Steps.Count: > 0 })
        {
            gate = gate with
            {
                Steps = gate.Steps
                    .Select(s => s with { Cmd = InterpolateFlowTextCore(s.Cmd) ?? s.Cmd })
                    .ToArray()
            };
        }

        return task with { GoalOrCmd = goal, Acceptance = acceptance, Gate = gate };
    }

    private bool HasPendingFlowTaskForSessionCore(string sessionId) =>
        _tasks.Any(task => task.Status == TaskRunStatus.Pending && task.Ready &&
            ((task.Kind == "flow_wake" && FlowConstants.IsFlowTask(task)) || (IsFlowSessionReuse(task) && ReadTaskDispatchSessionCore(task)?.Id == sessionId)));

    private AgentSessionDto? ReadTaskDispatchSessionCore(TaskDto task)
    {
        var id = IsFlowSessionReuse(task) ? task.Context!.Value.GetProperty("targetSession").GetString() : task.SessionId;
        return id is not null && _sessions.TryGetValue(id, out var session) ? session : null;
    }

    private static bool IsFlowSessionReuse(TaskDto task) =>
        FlowConstants.IsFlowTask(task) &&
        task.Context is { ValueKind: JsonValueKind.Object } ctx &&
        ctx.TryGetProperty("reuseSession", out var el) &&
        el.ValueKind == JsonValueKind.True;

    private HashSet<string> CollectActiveFlowTaskIdsCore()
    {
        var set = new HashSet<string>(_activeTasks.Keys, StringComparer.Ordinal);
        foreach (var id in _activeWatches.Keys)
        {
            set.Add(id);
        }

        return set;
    }

    private Dictionary<string, TaskDto> BuildCurrentTasksByKeyCore()
    {
        var map = new Dictionary<string, TaskDto>(StringComparer.Ordinal);
        foreach (var task in _tasks)
        {
            if (!map.TryGetValue(task.TaskKey, out var existing) ||
                (existing.ArchivedAtMs is not null && task.ArchivedAtMs is null))
            {
                map[task.TaskKey] = task;
            }
        }

        return map;
    }

    private void RequireFlowGovernanceCore(InteropPrincipal caller, string toolName)
    {
        if (caller is InteropPrincipal.Human)
        {
            return;
        }

        var session = (InteropPrincipal.Session)caller;
        if (session.TrackId != TrackId)
        {
            throw KernelException.Forbidden("The caller belongs to another Track.");
        }

        var current = RequireInteropCore(session, write: true)!;
        if (current.Id != _track.LeadSessionId || current.ArchivedAtMs is not null)
            throw KernelException.Forbidden("Only the current Track Lead can commit flow.");
        RequireGovernanceSessionCore(session.SessionId, toolName);
    }

}
