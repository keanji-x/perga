using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.PolicyVm;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private readonly TrackFlowFileStore _flowFileStore;
    private readonly Dictionary<string, ActiveWatchState> _activeWatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlowNodeSpecDto> _gcArchivedSinceSemantic = new(StringComparer.Ordinal);
    private FlowSpecDto _flowSpec = new(0, [], FlowConstants.SchemaPointer);
    private long _lastSemanticFlowRev;

    private async Task HydrateFlowCoreAsync(CancellationToken ct)
    {
        _flowSpec = TrackFlowParser.ParsePersisted(_track.FlowSpecJson, _track.FlowRev);
        MarkSemanticFlowRevisionCore(_track.FlowRev);
        _flowFileStore.EnsureSchemaMaterialized();
        await ProjectCommittedFlowFileCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task ProjectCommittedFlowFileCoreAsync(CancellationToken ct)
    {
        var canonical = TrackFlowSerializer.SerializeCanonical(_flowSpec);
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

    public Task<FlowSnapshotDto> GetFlowSnapshotAsync(CancellationToken ct = default, InteropPrincipal.Session? authorization = null) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (authorization is not null)
            {
                RequireMcpToolAccessCore(authorization.SessionId, "perga", KernelMcpToolNames.FlowRead);
                RequireRuntimeBindingCore(authorization);
            }
            await ProjectCommittedFlowFileCoreAsync(ct).ConfigureAwait(false);
            var projection = await BuildFlowProjectionCoreAsync(ct).ConfigureAwait(false);
            var spec = JsonHelpers.ParseClone(TrackFlowSerializer.SerializeCanonical(_flowSpec));
            return new FlowSnapshotDto(_track.FlowRev, _flowFileStore.GetFlowFilePath(TrackId), spec, projection);
        }, ct);

    public async Task<FlowCommitResultDto> CommitFlowAsync(
        bool dryRun,
        long? expectedFlowRev,
        InteropPrincipal caller,
        CancellationToken ct = default,
        string? content = null)
    {
        var authorization = caller as InteropPrincipal.Session;
        var (result, cleanup) = await EnqueueAsync<(FlowCommitResultDto, Task)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireFlowGovernanceCore(caller, KernelMcpToolNames.FlowCommit);
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot commit flow on a closed Track.");

            RequirePolicyAvailableCore();
            var (rawText, draftSha) = content is null
                ? _flowFileStore.ReadFlowFile(TrackId)
                : (content.TrimStart('\uFEFF'), (string?)null);
            var (parsedSpec, errors) = TrackFlowParser.Parse(rawText);
            if (parsedSpec is not null)
            {
                var expectedRev = expectedFlowRev ?? parsedSpec.Rev;
                if (expectedRev != _track.FlowRev)
                    parsedSpec = ReconcileFlowDraftAfterGcCore(parsedSpec, expectedRev);

                errors = TrackFlowValidator.Validate(
                    parsedSpec, _flowSpec, _taskCoordinator.Snapshot(), CollectActiveFlowTaskIdsCore(), _sessions,
                    _track.LeadSessionId, _policy, _policyVm, _track.TemplateInputs, _scheduleCronEvaluator, _timeProvider,
                    workerCatalog: await GetFlowWorkerCatalogCoreAsync(parsedSpec, ct).ConfigureAwait(false));
                RequireMcpAuthorizationCore(authorization);
                if (errors.Count == 0)
                {
                    // An explicit submission replaces the current Host draft. Keep its hash
                    // so projection and restart recovery still preserve any newer file edit.
                    if (content is not null && !dryRun)
                        draftSha = _flowFileStore.TryReadFlowFile(TrackId)?.Sha256;
                    return await ApplyFlowSpecCoreAsync(parsedSpec, dryRun, draftSha, ct,
                        authorization: authorization).ConfigureAwait(false);
                }
            }

            var projection = await BuildFlowProjectionCoreAsync(ct).ConfigureAwait(false);
            return (new FlowCommitResultDto(false, dryRun, _track.FlowRev, new([], [], [], [], []), errors, projection), Task.CompletedTask);
        }, ct).ConfigureAwait(false);

        await cleanup.ConfigureAwait(false);
        return result;
    }

    private FlowSpecDto ReconcileFlowDraftAfterGcCore(FlowSpecDto draft, long expectedRev)
    {
        if (expectedRev < _lastSemanticFlowRev || expectedRev >= _track.FlowRev)
            throw RevisionConflict();

        var remainingNodes = draft.Nodes.Where(node =>
            !_gcArchivedSinceSemantic.TryGetValue(node.Id, out var archived) ||
            !TrackFlowReconciler.NodeSpecEquals(archived, node)).ToArray();
        if (remainingNodes.Any(node => node.When?.After?.Any(dep => _gcArchivedSinceSemantic.ContainsKey(dep.Node)) == true))
            throw RevisionConflict();

        return draft with { Nodes = remainingNodes };

        KernelException RevisionConflict() => KernelException.Conflict(
            $"Flow revision conflict: expected {expectedRev}, current {_track.FlowRev}. Read the current flow using flow_read with format 'spec' or flow.json and retry.");
    }

    private void MarkSemanticFlowRevisionCore(long rev)
    {
        _lastSemanticFlowRev = rev;
        _gcArchivedSinceSemantic.Clear();
    }

    private async Task<Task> CancelFlowTaskCoreAsync(TaskDto task, string? reason, CancellationToken ct,
        InteropPrincipal.Session? authorization)
    {
        if (task.ArchivedAtMs is not null)
            return _taskCoordinator.TryGetActiveRun(task.Id, out var stopping)
                ? BeginFlowCancellationCore([stopping], [])
                : Task.CompletedTask;
        var node = _flowSpec.Nodes.FirstOrDefault(node => node.Id == task.TaskKey);
        if (node is null)
            throw KernelException.Conflict("The flow task has no live node.");
        if (node.Canceled)
        {
            var runs = _taskCoordinator.TryGetActiveRun(task.Id, out var active) ? new[] { active } : [];
            return BeginFlowCancellationCore(runs, [task.TaskKey]);
        }
        if (_track.ClosedAtMs is not null)
            throw KernelException.Conflict("Cannot cancel a Flow node on a closed Track.");

        var proposal = _flowSpec with
        {
            Nodes = _flowSpec.Nodes.Select(existing => existing.Id == node.Id ? existing with { Canceled = true } : existing).ToArray()
        };
        var (_, cleanup) = await ApplyFlowSpecCoreAsync(proposal, false, _track.FlowFileSha256, ct,
            task.Id, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), authorization).ConfigureAwait(false);
        return cleanup;
    }

    private async Task<(FlowCommitResultDto Result, Task Cleanup)> ApplyFlowSpecCoreAsync(
        FlowSpecDto proposed, bool dryRun, string? sourceSha, CancellationToken ct,
        string? canceledTaskId = null, string? cancellationReason = null, InteropPrincipal.Session? authorization = null)
    {
        // A targeted cancellation commits the node's canceled flag. Normal GC can archive it later.
        var prunedIds = canceledTaskId is null
            ? TrackFlowReconciler.ComputeGcEligibleNodeIds(proposed, _taskCoordinator.Snapshot(), CollectActiveFlowTaskIdsCore())
            : [];
        var plan = TrackFlowReconciler.ComputePlanDiff(_flowSpec, proposed, prunedIds);
        var nextSpec = new FlowSpecDto(
            dryRun ? _track.FlowRev : checked(_track.FlowRev + 1),
            proposed.Nodes.Where(n => !prunedIds.Contains(n.Id)).ToArray(),
            FlowConstants.SchemaPointer);
        var canceledNodes = proposed.Nodes.Where(n => n.Canceled).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var canceledRuns = _taskCoordinator.Snapshot().Where(t => canceledNodes.Contains(t.TaskKey) && _taskCoordinator.HasActiveRun(t.Id))
            .Select(t => _taskCoordinator.GetActiveRun(t.Id)).ToArray();
        _taskCoordinator.RequireTasksOutsideVerification(canceledRuns, allowFailedSettlement: true);

        if (dryRun)
        {
            var preview = TrackFlowProjector.BuildProjection(
                nextSpec, _flowFileStore.GetFlowFilePath(TrackId), _taskCoordinator.Snapshot(), _sessions,
                await ListTrackSchedulesSafeCoreAsync(ct).ConfigureAwait(false), _activeWatches, InterpolateFlowTextCore);
            return (new FlowCommitResultDto(true, true, _track.FlowRev, plan, [], preview), Task.CompletedTask);
        }

        var now = NowMs();
        var tasksByKey = TrackFlowReconciler.BuildTasksByKey(_taskCoordinator.Snapshot());
        var proposedById = proposed.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var activeIds = nextSpec.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var archivedIds = _flowSpec.Nodes.Select(n => n.Id).Where(id => !activeIds.Contains(id)).Concat(prunedIds)
            .ToHashSet(StringComparer.Ordinal);
        var upsertedTasks = new List<TaskDto>();
        foreach (var id in archivedIds)
        {
            if (tasksByKey.TryGetValue(id, out var existingArchived))
            {
                if (existingArchived.ArchivedAtMs is null)
                {
                    var archived = proposedById.TryGetValue(id, out var canceled) && canceled.Canceled
                        ? TrackFlowReconciler.BuildOrUpdateTaskForNode(TrackId, canceled, existingArchived, now)
                        : existingArchived;
                    upsertedTasks.Add(TrackFlowReconciler.ArchiveTaskForGc(archived, now));
                }
            }
            else if (proposedById.TryGetValue(id, out var prunedNewNode))
            {
                var created = TrackFlowReconciler.BuildOrUpdateTaskForNode(TrackId, prunedNewNode, null, now, ResolveFlowTaskAccessCore(prunedNewNode));
                upsertedTasks.Add(TrackFlowReconciler.ArchiveTaskForGc(created, now));
            }
        }
        var schedules = new List<TrackScheduleDto>();
        var canceledClocks = archivedIds.Select(FlowConstants.MakeScheduleId).ToList();
        var previousNodes = _flowSpec.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (var node in nextSpec.Nodes)
        {
            tasksByKey.TryGetValue(node.Id, out var existing);
            if (canceledTaskId is not null && existing?.Id != canceledTaskId)
                continue;
            previousNodes.TryGetValue(node.Id, out var previous);
            if (existing is not null && previous is not null &&
                !TrackFlowValidator.WhenEquals(previous.When, node.When))
                existing = existing with { Ready = false, TriggerRunKey = null };
            var updated = TrackFlowReconciler.BuildOrUpdateTaskForNode(TrackId, node, existing, now, ResolveFlowTaskAccessCore(node), previous);
            if (existing is not null && TaskSessionOwnership.OwnsSessionLifecycle(existing) &&
                TaskSessionOwnership.UsesBorrowedSession(updated))
            {
                // Keep the old worker's durable ownership evidence when this node switches to borrowing.
                var archivedId = $"tsk_{Guid.NewGuid():N}";
                upsertedTasks.Add(TrackFlowReconciler.ArchiveTaskForGc(existing with
                    { Id = archivedId, TaskKey = FlowConstants.MakeArchivedTaskKey(archivedId) }, now));
            }
            upsertedTasks.Add(updated);
            var schedule = TrackFlowReconciler.BuildScheduleForNode(TrackId, node, now, _scheduleCronEvaluator, _timeProvider);
            if (schedule is not null)
                schedules.Add(node.Source == "template" && _policy is not null
                    ? TemplateFlow.ResolveClockText(schedule, _track with { UpdatedAtMs = now }, _policy, _policyVm)
                    : schedule);
            else
                canceledClocks.Add(FlowConstants.MakeScheduleId(node.Id));
        }

        if (canceledTaskId is not null && cancellationReason is not null)
        {
            var index = upsertedTasks.FindIndex(task => task.Id == canceledTaskId);
            if (index >= 0) upsertedTasks[index] = upsertedTasks[index] with { StatusDetail = cancellationReason };
        }
        var events = await PersistFlowCoreAsync(nextSpec, sourceSha, upsertedTasks, schedules, canceledClocks, now, ct, authorization)
            .ConfigureAwait(false);
        MarkSemanticFlowRevisionCore(nextSpec.Rev);
        var cleanup = BeginFlowCancellationCore(canceledRuns, canceledNodes);
        if (canceledTaskId is null && canceledNodes.Count > 0 && cleanup == Task.CompletedTask)
            _ = TrackBackgroundJob(() => TriggerSchedulerAsync(_actorShutdownCts.Token));

        await PublishFlowCommitCoreAsync(events).ConfigureAwait(false);
        await EvaluateFlowAfterTriggersCoreAsync(CancellationToken.None).ConfigureAwait(false);
        await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
        await TryPokeLeadForPendingWakesCoreAsync().ConfigureAwait(false);
        var projection = await BuildFlowProjectionCoreAsync(CancellationToken.None).ConfigureAwait(false);
        return (new FlowCommitResultDto(true, false, _track.FlowRev, plan, [], projection), cleanup);
    }

    private Task BeginFlowCancellationCore(TrackTasks.ActiveTaskRunState[] runs, IEnumerable<string> nodeIds)
    {
        foreach (var run in runs) _taskCoordinator.BeginTaskCancellationCore(run);
        var watches = nodeIds.SelectMany(BeginWatchCancellationForNodeCore).ToArray();
        return runs.Length == 0 && watches.Length == 0 ? Task.CompletedTask :
            TrackBackgroundJob(() => FinishFlowCancellationAsync(runs, watches));
    }

    private Task FinishFlowCancellationAsync(TrackTasks.ActiveTaskRunState[] runs, ActiveWatchState[] watches) =>
        Task.WhenAll(runs.Select(FinishCanceledFlowRunAsync).Concat(watches.Select(FinishCanceledFlowWatchAsync)));

    private async Task FinishCanceledFlowRunAsync(TrackTasks.ActiveTaskRunState run)
    {
        try
        {
            await _taskCoordinator.CloseCanceledWorkerAsync(run).ConfigureAwait(false);
            await EnqueueAsync(async _ =>
            {
                if (_taskCoordinator.TryGetActiveRun(run.TaskId, out var current) && ReferenceEquals(current, run))
                {
                    _taskCoordinator.FinalizeCanceledTaskCore(run);
                    var task = _taskCoordinator.RequireTaskCore(run.TaskId);
                    if (TaskSessionOwnership.UsesBorrowedSession(task) && task.SessionId is { } sessionId)
                        await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                }
                await EvaluateFlowAfterTriggersCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await TryRunFlowGcCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await TryPokeLeadForPendingWakesCoreAsync().ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Could not finish canceling Flow task {0}: {1}", run.TaskId, ex);
            throw;
        }
    }

    private async Task FinishCanceledFlowWatchAsync(ActiveWatchState watch)
    {
        if (watch.Execution is { } exec)
        {
            try { await exec.ConfigureAwait(false); } catch { }
        }

        await EnqueueAsync(async _ =>
        {
            if (_activeWatches.TryGetValue(watch.TaskId, out var current) && ReferenceEquals(current, watch))
                _activeWatches.Remove(watch.TaskId);
            watch.Cts.Dispose();
            await EvaluateFlowAfterTriggersCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await TryRunFlowGcCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await TryPokeLeadForPendingWakesCoreAsync().ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<EventEnvelopeDto[]> PersistFlowCoreAsync(
        FlowSpecDto nextSpec,
        string? sourceSha,
        IReadOnlyList<TaskDto> tasks,
        IReadOnlyList<TrackScheduleDto> schedules,
        IReadOnlyList<string> canceledClocks,
        long now,
        CancellationToken ct,
        InteropPrincipal.Session? authorization = null)
    {
        // Persist the accepted source hash before projecting the canonical file. Hydration can
        // finish an interrupted write only while the file still contains those accepted bytes.
        var nextTrack = _track with
        {
            FlowSpecJson = TrackFlowSerializer.SerializeCanonical(nextSpec),
            FlowRev = nextSpec.Rev,
            FlowFileSha256 = sourceSha,
            UpdatedAtMs = now
        };
        EventEnvelopeDto[] events =
        [
            new(null, nextTrack.AreaId, TrackId, KernelEventKinds.TrackUpdated,
                JsonHelpers.ToElement(TemplatePresentation.Project(nextTrack, _policy, _policyVm)), now),
            _taskCoordinator.CreateTaskUpdatedEventCore(now)
        ];
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var settled = await _store.CommitTrackFlowAsync(nextTrack, _track.FlowRev, tasks, schedules, canceledClocks, events, ct,
            beforeWrite: CaptureMcpWriteFenceCore(authorization),
            settlingTasks: _taskCoordinator.FlowSettlementsCore(tasks)).ConfigureAwait(false);
        _track = nextTrack;
        _flowSpec = nextSpec;
        foreach (var task in tasks)
            _taskCoordinator.ReplaceTaskInMemoryCore(task);
        foreach (var (sessionId, result) in settled)
            _taskCoordinator.ApplyTaskSessionSettlementCore(sessionId, result);
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
            if (_track.ClosedAtMs is not null || _store.TrackSchedules is not { } store)
                return false;

            var dueAt = due.DueAtMs;
            var clock = await store.GetAsync(TrackId, due.ScheduleId, ct).ConfigureAwait(false);
            var node = _flowSpec.Nodes.FirstOrDefault(n => n.Id == due.NodeKey);
            if (node is null || node.Canceled || clock is null || clock.NodeKey != node.Id ||
                clock.State != TrackScheduleStates.Active || clock.NextFireAtMs != dueAt)
                return false;

            var existing = _taskCoordinator.Snapshot().FirstOrDefault(t => t.TaskKey == node.Id && t.ArchivedAtMs is null);
            // A one-shot can arm behind a replaced watch. Dispatch still waits for that watch to exit.
            var canArmBehindOldWatch = clock.CronExpr is null && existing is { Status: TaskRunStatus.Pending, Ready: false };
            var busy = _activeWatches.Values.Any(watch => watch.NodeId == node.Id &&
                    (!canArmBehindOldWatch || watch.TaskId == existing?.Id)) ||
                (existing is not null && (_taskCoordinator.HasActiveRun(existing.Id) ||
                    existing.Status is TaskRunStatus.Running or TaskRunStatus.Verifying ||
                    (existing.Status == TaskRunStatus.Pending && existing.Ready) ||
                    (FlowConstants.IsRepeatingWatch(node) && existing.RunCount > 0)));

            // Busy ticks skip arming without consuming an occurrence toward maxOccurrences.
            var count = busy ? clock.FiredCount : checked(clock.FiredCount + 1);
            var nextFire = clock.CronExpr is { } cron && (clock.MaxOccurrences is null || count < clock.MaxOccurrences)
                ? _scheduleCronEvaluator?.GetNextOccurrenceMs(cron, clock.Timezone, now)
                : null;
            var nextClock = clock with
            {
                FiredCount = count,
                LastFiredAtMs = busy ? clock.LastFiredAtMs : now,
                NextFireAtMs = nextFire,
                State = nextFire is null ? TrackScheduleStates.Done : TrackScheduleStates.Active,
                UpdatedAtMs = now
            };
            var access = busy ? null : ResolveFlowTaskAccessCore(node);
            var scheduledTask = busy ? null : existing ??
                TrackFlowReconciler.BuildOrUpdateTaskForNode(TrackId, node, null, now, access);
            var armed = scheduledTask is null ? null : scheduledTask with
            {
                Access = access ?? scheduledTask.Access,
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
            var evt = _taskCoordinator.CreateTaskUpdatedEventCore(now);
            if (!await store.CommitFlowFireAsync(nextClock, dueAt, armed, evt, ct).ConfigureAwait(false))
                return false;
            if (armed is not null)
            {
                _taskCoordinator.ReplaceTaskInMemoryCore(armed);
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
                await EvaluateFlowAfterTriggersCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await TryPokeLeadForPendingWakesCoreAsync().ConfigureAwait(false);
            return true;
        }, ct);

    private async Task<FlowProjectionDto> BuildFlowProjectionCoreAsync(CancellationToken ct)
    {
        var schedules = await ListTrackSchedulesSafeCoreAsync(ct).ConfigureAwait(false);
        return TrackFlowProjector.BuildProjection(
            _flowSpec,
            _flowFileStore.GetFlowFilePath(TrackId),
            _taskCoordinator.Snapshot(),
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
            await _taskCoordinator.ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
        }

        await TryRunFlowGcCoreAsync(ct).ConfigureAwait(false);
        await TryPokeLeadForPendingWakesCoreAsync().ConfigureAwait(false);
    }

    private async Task TryPokeLeadForPendingWakesCoreAsync()
    {
        try
        {
            await _scheduler.PokeLeadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Could not check pending Lead wakes for Track {0}: {1}", TrackId, ex);
        }
    }

    private async Task<bool> EvaluateFlowAfterTriggersCoreAsync(CancellationToken ct)
    {
        if (_flowSpec.Nodes.Count == 0 || _closingTrack || _deletingTrack || _track.ClosedAtMs is not null)
        {
            ScheduleWatchRearmCore(null);
            return false;
        }

        var tasksByKey = TrackFlowReconciler.BuildTasksByKey(_taskCoordinator.Snapshot());
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

            if (_taskCoordinator.HasActiveRun(task.Id) || _activeWatches.ContainsKey(task.Id) ||
                (FlowConstants.IsRepeatingWatch(node) && (task.Status == TaskRunStatus.Done || FlowConstants.IsWatchPaused(task))) ||
                task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)
            {
                continue;
            }

            var waveKey = FlowConstants.SatisfiedWaveKey(afterList, tasksByKey);
            if (waveKey is null)
            {
                if (task.Status == TaskRunStatus.Pending && (task.Ready || task.TriggerRunKey is not null))
                {
                    var disarmed = task with { Ready = false, TriggerRunKey = null, UpdatedAtMs = now };
                    await _taskCoordinator.PersistAndEmitTaskUpdatedCoreAsync(disarmed, now, ct).ConfigureAwait(false);
                    _taskCoordinator.ReplaceTaskInMemoryCore(disarmed);
                    tasksByKey[node.Id] = disarmed;
                }
                continue;
            }

            if (string.Equals(task.TriggerRunKey, waveKey, StringComparison.Ordinal))
            {
                // Already triggered for this exact upstream wave.
                continue;
            }

            var armed = task with
            {
                Access = ResolveFlowTaskAccessCore(node) ?? task.Access,
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
            await _taskCoordinator.PersistAndEmitTaskUpdatedCoreAsync(armed, now, ct).ConfigureAwait(false);
            _taskCoordinator.ReplaceTaskInMemoryCore(armed);
            tasksByKey[node.Id] = armed;
            armedAny = true;
        }

        return await RearmConvergedWatchesCoreAsync(tasksByKey, now, ct).ConfigureAwait(false) || armedAny;
    }

    private async Task TryRunFlowGcCoreAsync(CancellationToken ct)
    {
        if (_flowSpec.Nodes.Count == 0)
        {
            return;
        }

        var activeIds = CollectActiveFlowTaskIdsCore();
        var eligible = TrackFlowReconciler.ComputeGcEligibleNodeIds(_flowSpec, _taskCoordinator.Snapshot(), activeIds);
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
        var archivedNodes = _flowSpec.Nodes.Where(n => eligible.Contains(n.Id)).ToArray();
        var remainingNodes = _flowSpec.Nodes.Where(n => !eligible.Contains(n.Id)).ToArray();
        var nextSpec = new FlowSpecDto(checked(_track.FlowRev + 1), remainingNodes, FlowConstants.SchemaPointer);
        var tasksByKey = TrackFlowReconciler.BuildTasksByKey(_taskCoordinator.Snapshot());
        var archivedTasks = eligible.Where(tasksByKey.ContainsKey).Select(id => tasksByKey[id])
            .Where(task => task.ArchivedAtMs is null)
            .Select(task => TrackFlowReconciler.ArchiveTaskForGc(task, now))
            .ToArray();
        var canceledClocks = eligible.Select(FlowConstants.MakeScheduleId).ToArray();
        var events = await PersistFlowCoreAsync(nextSpec, _track.FlowFileSha256, archivedTasks, [], canceledClocks, now, ct)
            .ConfigureAwait(false);
        foreach (var node in archivedNodes)
            _gcArchivedSinceSemantic[node.Id] = node;
        await PublishFlowCommitCoreAsync(events).ConfigureAwait(false);
    }

    private AccessMode? ResolveFlowTaskAccessCore(FlowNodeSpecDto node) =>
        FlowTaskRouting.ResolveAccess(node, _policy, _policyVm, _track.TemplateInputs, _sessions);

    private string? InterpolateFlowTextCore(string? text) =>
        TemplateFlow.Interpolate(text, _track.TemplateInputs, _policy, _policyVm);

    private TaskDto ProjectFlowTaskCore(TaskDto task) => !FlowConstants.IsFlowTask(task) ? task : task with
    {
        GoalOrCmd = InterpolateFlowTextCore(task.GoalOrCmd) ?? task.GoalOrCmd,
        Acceptance = InterpolateFlowTextCore(task.Acceptance)
    };

    private TaskDto InterpolateFlowTaskForExecutionCore(TaskDto task)
    {
        if (!FlowConstants.IsFlowTask(task))
        {
            return task;
        }

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

        return ProjectFlowTaskCore(task) with { Gate = gate };
    }

    private bool HasPendingFlowTaskForSessionCore(string sessionId) =>
        _taskCoordinator.Snapshot().Any(task => task.Status == TaskRunStatus.Pending && task.Ready &&
            ((task.Kind == "flow_wake" && FlowConstants.IsFlowTask(task)) || (FlowConstants.IsFlowSessionReuse(task) && ReadTaskDispatchSessionCore(task)?.Id == sessionId)));

    private AgentSessionDto? ReadTaskDispatchSessionCore(TaskDto task)
    {
        var id = FlowConstants.IsFlowSessionReuse(task) ? task.Context!.Value.GetProperty("targetSession").GetString() : task.SessionId;
        return id is not null && _sessions.TryGetValue(id, out var session) ? session : null;
    }

    private HashSet<string> CollectActiveFlowTaskIdsCore()
    {
        var set = new HashSet<string>(_taskCoordinator.ActiveRuns.Select(run => run.TaskId), StringComparer.Ordinal);
        foreach (var id in _activeWatches.Keys)
        {
            set.Add(id);
        }

        return set;
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
