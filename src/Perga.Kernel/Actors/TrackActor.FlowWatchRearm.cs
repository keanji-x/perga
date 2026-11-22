using Perga.Contracts;
using Perga.Kernel.Flow;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private long? _watchRearmAtMs;
    private CancellationTokenSource? _watchRearmTimer;

    private async Task<bool> RearmConvergedWatchesCoreAsync(
        Dictionary<string, TaskDto> tasks, long now, CancellationToken ct)
    {
        var watches = _flowSpec.Nodes.Where(node => !node.Canceled && FlowConstants.IsRepeatingWatch(node)).ToArray();
        if (watches.Length == 0)
        {
            ScheduleWatchRearmCore(null);
            return false;
        }

        var downstream = TrackFlowReconciler.BuildDownstreamIndex(_flowSpec);
        var nodes = _flowSpec.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var activeIds = CollectActiveFlowTaskIdsCore();
        var descendantsByWatch = watches.ToDictionary(node => node.Id,
            node => TrackFlowReconciler.DownstreamClosure(node.Id, downstream), StringComparer.Ordinal);
        // A nested watch is armed by the upstream wave. Rearming it on its own would
        // keep the upstream watch's barrier occupied and could race the next upstream wave.
        var waveDriven = watches
            .Where(node => !tasks.TryGetValue(node.Id, out var task) || !FlowConstants.IsWatchPaused(task))
            .SelectMany(node => descendantsByWatch[node.Id])
            .ToHashSet(StringComparer.Ordinal);

        long? nextDue = null;
        var armedAny = false;
        foreach (var watch in watches)
        {
            if (tasks.GetValueOrDefault(watch.Id) is not { Status: TaskRunStatus.Done, Outcome: FlowOutcome.Ok, FinishedAtMs: { } finished } task ||
                !TrackFlowReconciler.IsWaveConverged(descendantsByWatch[watch.Id], nodes, tasks, activeIds))
                continue;

            var waveKey = task.TriggerRunKey;
            if (watch.When?.After is { Count: > 0 } dependencies)
            {
                waveKey = FlowConstants.SatisfiedWaveKey(dependencies, tasks);
                if (waveKey is null || (waveDriven.Contains(watch.Id) && waveKey == task.TriggerRunKey))
                    continue;
            }

            var due = finished + (long)FlowConstants.WatchInterval(watch.Do).TotalMilliseconds;
            if (due > now)
            {
                nextDue = Math.Min(due, nextDue ?? due);
                continue;
            }

            var armed = task with
            {
                Ready = true,
                Status = TaskRunStatus.Pending,
                Outcome = FlowOutcome.None,
                OutcomeSummary = null,
                StatusDetail = null,
                TriggerRunKey = waveKey,
                FinishedAtMs = null,
                UpdatedAtMs = now
            };
            await _taskCoordinator.PersistAndEmitTaskUpdatedCoreAsync(armed, now, ct).ConfigureAwait(false);
            _taskCoordinator.ReplaceTaskInMemoryCore(armed);
            tasks[watch.Id] = armed;
            armedAny = true;
        }

        ScheduleWatchRearmCore(nextDue);
        return armedAny;
    }

    private bool HasActiveDescendantCore(string watchNodeId)
    {
        var downstream = TrackFlowReconciler.BuildDownstreamIndex(_flowSpec);
        var descendants = TrackFlowReconciler.DownstreamClosure(watchNodeId, downstream);
        if (descendants.Count == 0)
            return false;
        var tasks = TrackFlowReconciler.BuildTasksByKey(_taskCoordinator.Snapshot());
        var activeIds = CollectActiveFlowTaskIdsCore();
        foreach (var id in descendants)
        {
            if (!tasks.TryGetValue(id, out var task))
                continue;
            if (activeIds.Contains(task.Id) ||
                _pendingTaskDispatchApprovals.Contains(task.Id) ||
                task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying ||
                (task.Status == TaskRunStatus.Pending && task.Ready))
                return true;
        }
        return false;
    }

    private void ScheduleWatchRearmCore(long? due)
    {
        if (_watchRearmAtMs == due)
            return;

        _watchRearmTimer?.Cancel();
        _watchRearmTimer?.Dispose();
        _watchRearmTimer = null;
        _watchRearmAtMs = due;
        if (due is not { } at)
            return;

        _watchRearmTimer = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        var ct = _watchRearmTimer.Token;
        _ = TrackBackgroundJob(async () =>
        {
            try
            {
                await WaitUntilWatchTimeAsync(at, ct).ConfigureAwait(false);
                await EnqueueAsync(async _ =>
                {
                    if (ct.IsCancellationRequested)
                        return;
                    _watchRearmAtMs = null;
                    _watchRearmTimer?.Dispose();
                    _watchRearmTimer = null;
                    var lifecycle = _actorShutdownCts.Token;
                    await EvaluateFlowAfterTriggersCoreAsync(lifecycle).ConfigureAwait(false);
                    await _taskCoordinator.ScheduleEligibleTasksCoreAsync(lifecycle).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });
    }
}
