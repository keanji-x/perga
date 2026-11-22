using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;
using Perga.VcsKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    // ------------------- Flow `wake` and `watch` Runners -------------------

    private async Task<bool> StartFlowWakeRunCoreAsync(TaskDto task, CancellationToken ct)
    {
        var node = _flowSpec.Nodes.FirstOrDefault(n => string.Equals(n.Id, task.TaskKey, StringComparison.Ordinal));
        var targetSpec = node?.Do.Session;
        var targetSessionId = string.Equals(targetSpec, "lead", StringComparison.Ordinal)
            ? _track.LeadSessionId
            : targetSpec;

        if (string.IsNullOrWhiteSpace(targetSessionId) ||
            !_sessions.TryGetValue(targetSessionId, out var targetSession) ||
            targetSession.ArchivedAtMs is not null)
        {
            var failNow = NowMs();
            var failed = task with
            {
                Status = TaskRunStatus.Failed,
                RunCount = checked(task.RunCount + 1),
                Outcome = FlowOutcome.Blocked,
                OutcomeSummary = $"Wake target session '{targetSpec ?? "lead"}' is unavailable.",
                StatusDetail = $"Wake target session '{targetSpec ?? "lead"}' is unavailable.",
                FinishedAtMs = failNow,
                UpdatedAtMs = failNow
            };
            _taskCoordinator.ReplaceTaskInMemoryCore(failed);
            await _taskCoordinator.PersistAndEmitTaskUpdatedCoreAsync(failed, failNow, ct).ConfigureAwait(false);
            await OnTaskSettledFlowHookCoreAsync(failed, ct).ConfigureAwait(false);
            return false;
        }

        if (_scheduler.IsOccupied(targetSession))
        {
            // Target session is busy; retry once its current turn settles.
            return true;
        }

        var now = NowMs();
        var nextRunCount = checked(task.RunCount + 1);
        var prompt = InterpolateFlowTextCore(node?.Do.Prompt ?? task.GoalOrCmd) ?? task.GoalOrCmd;
        var runningTask = task with
        {
            SessionId = targetSessionId,
            RunCount = nextRunCount,
            Status = TaskRunStatus.Running,
            StatusDetail = null,
            Outcome = FlowOutcome.None,
            OutcomeSummary = null,
            StartedAtMs = now,
            FinishedAtMs = null,
            UpdatedAtMs = now
        };

        var admission = _options.DispatchAdmission.TryEnter();
        if (admission is null) return true;
        var evt = _taskCoordinator.CreateTaskUpdatedEventCore(now);
        try
        {
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.UpsertTaskAsync(runningTask, evt, ct).ConfigureAwait(false);
        }
        catch
        {
            admission.Dispose();
            throw;
        }
        _taskCoordinator.ReplaceTaskInMemoryCore(runningTask);

        var runCts = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        var active = new TrackTasks.ActiveTaskRunState(task.Id, nextRunCount, null, runCts);
        _taskCoordinator.RegisterActiveRun(active);

        var reservation = Guid.NewGuid();
        _scheduler.Reserve(targetSessionId, reservation);
        var runToken = runCts.Token;

        active.Execution = TrackBackgroundJob(async () =>
        {
            using (admission)
            {
                await RunFlowWakeInBackgroundAsync(task.Id, nextRunCount, targetSessionId, prompt, reservation, runToken)
                    .ConfigureAwait(false);
            }
        });

        try
        {
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, _actorShutdownCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Could not publish wake startup for task {0}: {1}", task.Id, ex);
        }
        return true;
    }

    private async Task RunFlowWakeInBackgroundAsync(
        string taskId,
        int runCount,
        string targetSessionId,
        string prompt,
        Guid reservation,
        CancellationToken ct)
    {
        SessionTurnDto? turn = null;
        string? errorMessage = null;
        try
        {
            var work = new FlowWakeWorkItem(this, targetSessionId, prompt, reservation);
            turn = await _turnRunner.RunAsync(work, ct, alreadyAdmitted: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            if (!_actorShutdownCts.IsCancellationRequested)
            {
                await EnqueueAsync(_ =>
                {
                    if (_scheduler.TryGetReservation(targetSessionId, out var held) && held == reservation)
                    {
                        _scheduler.ReleaseReservation(targetSessionId);
                    }

                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (_actorShutdownCts.IsCancellationRequested)
        {
            return;
        }

        await EnqueueAsync(async _ =>
        {
            if (!_taskCoordinator.TryGetActiveRun(taskId, out var active) || active.RunCount != runCount || active.Settling)
            {
                return;
            }

            _taskCoordinator.RemoveActiveRun(taskId);
            active.Cts.Dispose();

            var now = NowMs();
            var current = _taskCoordinator.RequireTaskCore(taskId);
            var succeeded = errorMessage is null && turn is { Status: "completed" };
            var settled = current with
            {
                Status = succeeded ? TaskRunStatus.Done : TaskRunStatus.Failed,
                Outcome = succeeded ? FlowOutcome.Ok : FlowOutcome.Attention,
                OutcomeSummary = succeeded ? "Wake turn completed." : (errorMessage ?? turn?.ErrorText ?? "Wake turn failed."),
                StatusDetail = succeeded ? null : (errorMessage ?? turn?.ErrorText ?? "Wake turn failed."),
                FinishedAtMs = now,
                UpdatedAtMs = now
            };
            _taskCoordinator.ReplaceTaskInMemoryCore(settled);
            await _taskCoordinator.PersistAndEmitTaskUpdatedCoreAsync(settled, now, CancellationToken.None, suppressDefaultWake: true).ConfigureAwait(false);
            await OnTaskSettledFlowHookCoreAsync(settled, CancellationToken.None).ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> StartFlowWatchRunCoreAsync(TaskDto task, MountedWorkspace mounted, CancellationToken ct)
    {
        var node = _flowSpec.Nodes.FirstOrDefault(n => string.Equals(n.Id, task.TaskKey, StringComparison.Ordinal));
        if (node is null)
        {
            return false;
        }

        var now = NowMs();
        var repeat = FlowConstants.IsRepeatingWatch(node);
        var priorProgress = task.Status == TaskRunStatus.Running ? TrackFlowProjector.ExtractPersistedWatchProgress(task) : null;
        var interval = FlowConstants.WatchInterval(node.Do);
        var timeoutSeconds = FlowConstants.WatchTimeoutSeconds(node.Do);
        var initialProgress = priorProgress ?? new FlowWatchProgressDto(
            ProbeKind: node.Do.PrChecks.HasValue ? "prChecks" : "cmd",
            Label: node.Do.PrChecks is { } pr ? $"PR #{pr}" : (node.Title ?? node.Id),
            PassedChecks: null, TotalChecks: null, LastCheckedAtMs: null,
            StartedAtMs: now, TimeoutAtMs: timeoutSeconds is { } seconds ? now + (long)(seconds * 1000) : null,
            Detail: "Starting probe...");

        if (priorProgress is null)
        {
            var runningTask = task with
            {
                RunCount = repeat ? task.RunCount : checked(task.RunCount + 1),
                Status = TaskRunStatus.Running,
                StatusDetail = null,
                Outcome = FlowOutcome.None,
                OutcomeSummary = null,
                Meta = JsonHelpers.ToElement(new { watchProgress = initialProgress }),
                StartedAtMs = now,
                FinishedAtMs = null,
                UpdatedAtMs = now
            };
            await _taskCoordinator.PersistAndEmitTaskUpdatedCoreAsync(runningTask, now, ct).ConfigureAwait(false);
            _taskCoordinator.ReplaceTaskInMemoryCore(runningTask);
        }

        var watchCts = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        var perProbeWorkspace = TaskGraphScheduler.UsesPerProbeWorkspace(node);
        var watchState = new ActiveWatchState(task.Id, node.Id, watchCts,
            TaskGraphScheduler.HoldsWorkspaceBetweenProbes(task, node), repeat, perProbeWorkspace, initialProgress)
        {
            LastPersistedCheckAtMs = priorProgress?.LastCheckedAtMs is not null ? task.UpdatedAtMs : null
        };
        _activeWatches[task.Id] = watchState;

        var interpolatedCmd = InterpolateFlowTextCore(node.Do.Cmd);
        var interpolatedRepo = InterpolateFlowTextCore(node.Do.Repo);
        var interpolatedHeadSha = InterpolateFlowTextCore(node.Do.HeadSha);
        var prNumber = node.Do.PrChecks;
        var cwd = mounted.MergedCwd;

        var watchToken = watchCts.Token;
        watchState.Execution = TrackBackgroundJob(() => RunWatchProbeLoopAsync(
            watchState, prNumber, interpolatedRepo, interpolatedHeadSha, interpolatedCmd, cwd, interval, watchToken));

        return true;
    }

    private async Task RunWatchProbeLoopAsync(
        ActiveWatchState watchState,
        int? prNumber,
        string? repo,
        string? expectedHeadSha,
        string? cmd,
        string workspaceCwd,
        TimeSpan interval,
        CancellationToken ct)
    {
        var timeoutAtMs = watchState.Progress.TimeoutAtMs ?? long.MaxValue;
        try
        {
            if (watchState.Progress.LastCheckedAtMs is { } lastCheck)
                await WaitForNextWatchProbeAsync(lastCheck, interval, timeoutAtMs, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested && NowMs() < timeoutAtMs)
            {
                var checkNow = NowMs();
                var maxProbeMs = prNumber.HasValue && timeoutAtMs != long.MaxValue ? uint.MaxValue - 1L : 60_000L;
                var budget = TimeSpan.FromMilliseconds(Math.Clamp(timeoutAtMs - checkNow, 1, maxProbeMs));
                FlowOutcome? outcome = null;
                string? summary = null;
                var progress = watchState.Progress;
                var probed = false;
                using (var admission = _options.DispatchAdmission.TryEnter())
                {
                    if (admission is not null && (!watchState.UsesPerProbeWorkspace || await TryEnterWatchProbeCoreAsync(watchState, ct).ConfigureAwait(false)))
                    {
                        probed = true;
                        using var deadline = new CancellationTokenSource(budget, _timeProvider);
                        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                        try
                        {
                            (outcome, summary, progress) = prNumber is { } pr
                                ? await EvaluatePrChecksProbeAsync(pr, repo ?? workspaceCwd, expectedHeadSha, progress, checkNow, probe.Token)
                                    .ConfigureAwait(false)
                                : await EvaluateCmdProbeAsync(cmd!, workspaceCwd, progress, checkNow, budget, probe.Token)
                                    .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
                ct.ThrowIfCancellationRequested();
                if (NowMs() >= timeoutAtMs)
                    break;

                if (outcome is { } finalOutcome)
                {
                    if (await SettleWatchAsync(watchState, finalOutcome, summary, progress).ConfigureAwait(false))
                        return;
                    await WaitForNextWatchProbeAsync(checkNow, interval, timeoutAtMs, ct).ConfigureAwait(false);
                    continue;
                }
                if (probed)
                    await UpdateWatchProgressAsync(watchState, progress).ConfigureAwait(false);

                await WaitForNextWatchProbeAsync(checkNow, interval, timeoutAtMs, ct).ConfigureAwait(false);
            }

            if (!ct.IsCancellationRequested)
            {
                var summary = $"Watch '{watchState.NodeId}' timed out.";
                await SettleWatchAsync(watchState, FlowOutcome.Blocked, summary, watchState.Progress with { Detail = summary })
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!_actorShutdownCts.IsCancellationRequested)
        {
            await SettleWatchAsync(watchState, FlowOutcome.Blocked, $"Watch probe error: {ex.Message}", watchState.Progress with { Detail = ex.Message })
                .ConfigureAwait(false);
        }
    }

    private async Task WaitForNextWatchProbeAsync(long checkAtMs, TimeSpan interval, long timeoutAtMs, CancellationToken ct)
    {
        var nextCheckAtMs = Math.Min(timeoutAtMs, checkAtMs + (long)interval.TotalMilliseconds);
        await WaitUntilWatchTimeAsync(nextCheckAtMs, ct).ConfigureAwait(false);
    }

    private async Task WaitUntilWatchTimeAsync(long atMs, CancellationToken ct)
    {
        while (atMs - NowMs() is > 0 and var delayMs)
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delayMs, uint.MaxValue - 1L)), _timeProvider, ct)
                .ConfigureAwait(false);
    }

    private Task<bool> TryEnterWatchProbeCoreAsync(ActiveWatchState watch, CancellationToken ct) =>
        EnqueueAsync(_ =>
        {
            if (ct.IsCancellationRequested || _policyUnavailableReason is not null ||
                _closingTrack || _deletingTrack || _track.ClosedAtMs is not null ||
                _activeWatches.Values.Any(w => w.HoldsWorkspace) || _taskCoordinator.HasCanceledRunStillSettling())
                return Task.FromResult(false);

            var tasks = _taskCoordinator.Snapshot();
            var nodes = _flowSpec.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            if (tasks.Any(task => task.ArchivedAtMs is null &&
                task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying &&
                TaskGraphScheduler.HoldsWorkspaceBetweenProbes(task, nodes.GetValueOrDefault(task.TaskKey))))
                return Task.FromResult(false);

            if (watch.Repeat && HasActiveDescendantCore(watch.NodeId))
                return Task.FromResult(false);

            watch.HoldsWorkspace = true;
            return Task.FromResult(true);
        }, ct);

    private async Task<(FlowOutcome? Outcome, string? Summary, FlowWatchProgressDto Progress)> EvaluateCmdProbeAsync(
        string cmd,
        string cwd,
        FlowWatchProgressDto prevProgress,
        long checkNow,
        TimeSpan probeTimeout,
        CancellationToken ct)
    {
        var res = await EffectiveCommandRunner.RunAsync(
            new ShellCommandRequest(cmd, cwd, probeTimeout, OutputTailChars: TrackTasks.TaskOutputTailChars),
            ct).ConfigureAwait(false);

        var tail = FlowConstants.ExtractTailLine(res.Stdout, res.Stderr);
        if (res.TimedOut)
        {
            return (FlowOutcome.Blocked, tail ?? "Watch command probe timed out.", prevProgress with { LastCheckedAtMs = checkNow, Detail = "Probe timed out" });
        }

        if (res.ExitCode == 0)
        {
            var summary = tail ?? "Command probe succeeded (exit 0).";
            return (FlowOutcome.Ok, summary, prevProgress with { LastCheckedAtMs = checkNow, Detail = summary });
        }

        if (res.ExitCode == 75)
        {
            // EX_TEMPFAIL: continue polling silently
            var detail = tail ?? "Waiting (exit 75)...";
            return (null, null, prevProgress with { LastCheckedAtMs = checkNow, Detail = detail });
        }

        var failSummary = tail ?? $"Command probe exited with code {res.ExitCode}.";
        return (FlowOutcome.Attention, failSummary, prevProgress with { LastCheckedAtMs = checkNow, Detail = failSummary });
    }

    private async Task<(FlowOutcome? Outcome, string? Summary, FlowWatchProgressDto Progress)> EvaluatePrChecksProbeAsync(
        int prNumber,
        string repoOrPath,
        string? expectedHeadSha,
        FlowWatchProgressDto prevProgress,
        long checkNow,
        CancellationToken ct)
    {
        var forge = Env.Forge;
        if (forge is null)
        {
            const string msg = "GitHub forge service is not configured on this execution target.";
            return (FlowOutcome.Blocked, msg, prevProgress with { LastCheckedAtMs = checkNow, Detail = msg });
        }

        var status = expectedHeadSha is null
            ? await forge.GetPrChecksAsync(repoOrPath, prNumber, ct).ConfigureAwait(false)
            : await forge.GetPrChecksSnapshotAsync(repoOrPath, prNumber, ct).ConfigureAwait(false);
        var total = status.Checks.Count;
        var passed = status.Checks.Count(c =>
            string.Equals(c.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
            c.Conclusion is not null &&
            (c.Conclusion.Equals("success", StringComparison.OrdinalIgnoreCase) ||
             c.Conclusion.Equals("neutral", StringComparison.OrdinalIgnoreCase) ||
             c.Conclusion.Equals("skipped", StringComparison.OrdinalIgnoreCase)));
        var failedChecks = status.Checks
            .Where(c => c.Conclusion is not null &&
                        (c.Conclusion.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
                         c.Conclusion.Equals("timed_out", StringComparison.OrdinalIgnoreCase) ||
                         c.Conclusion.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ||
                         c.Conclusion.Equals("action_required", StringComparison.OrdinalIgnoreCase) ||
                         c.Conclusion.Equals("startup_failure", StringComparison.OrdinalIgnoreCase) ||
                         c.Conclusion.Equals("stale", StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name)
            .ToArray();

        var progress = prevProgress with
        {
            PassedChecks = passed,
            TotalChecks = total,
            LastCheckedAtMs = checkNow
        };

        if (!string.IsNullOrWhiteSpace(expectedHeadSha) &&
            !string.Equals(status.HeadSha, expectedHeadSha, StringComparison.OrdinalIgnoreCase) &&
            !status.HeadSha.StartsWith(expectedHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, progress with
            {
                Detail = $"Waiting for headSha {expectedHeadSha} (current: {(string.IsNullOrEmpty(status.HeadSha) ? "unknown" : status.HeadSha)})"
            });
        }

        if (total == 0)
        {
            return (null, null, progress with { Detail = $"Waiting for checks on PR #{prNumber}..." });
        }

        if (failedChecks.Length > 0)
        {
            var summary = $"PR #{prNumber} check failed: {string.Join(", ", failedChecks)}";
            return (FlowOutcome.Attention, summary, progress with { Detail = summary });
        }

        if (status.AllCompleted && status.AllGreen)
        {
            var summary = $"PR #{prNumber} all {total} checks passed.";
            return (FlowOutcome.Ok, summary, progress with { Detail = summary });
        }

        if (status.AllCompleted && !status.AllGreen)
        {
            var summary = $"PR #{prNumber} checks completed with failures ({passed}/{total} passed).";
            return (FlowOutcome.Attention, summary, progress with { Detail = summary });
        }

        return (null, null, progress with { Detail = $"{passed}/{total} checks passed" });
    }

    private async Task UpdateWatchProgressAsync(ActiveWatchState watchState, FlowWatchProgressDto progress)
    {
        if (_actorShutdownCts.IsCancellationRequested) return;
        await EnqueueAsync(async _ =>
        {
            if (!_activeWatches.TryGetValue(watchState.TaskId, out var current) || !ReferenceEquals(current, watchState))
            {
                return;
            }

            var task = _taskCoordinator.Snapshot().FirstOrDefault(t => t.Id == watchState.TaskId);
            if (watchState.Cts.IsCancellationRequested || task?.Status != TaskRunStatus.Running)
                return;
            var now = NowMs();
            var persist = progress.ProbeKind != "cmd" ||
                watchState.LastPersistedCheckAtMs is not { } lastPersisted || now - lastPersisted >= 60_000 ||
                progress.Detail != watchState.Progress.Detail;
            watchState.Progress = progress;
            if (persist)
            {
                var updated = task with
                {
                    Meta = JsonHelpers.ToElement(new { watchProgress = progress }),
                    UpdatedAtMs = now
                };
                await _taskCoordinator.PersistAndEmitTaskUpdatedCoreAsync(updated, now, CancellationToken.None).ConfigureAwait(false);
                _taskCoordinator.ReplaceTaskInMemoryCore(updated);
                watchState.LastPersistedCheckAtMs = now;
            }
            if (watchState.UsesPerProbeWorkspace && watchState.HoldsWorkspace)
            {
                watchState.HoldsWorkspace = false;
                await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> SettleWatchAsync(
        ActiveWatchState watchState,
        FlowOutcome outcome,
        string? summary,
        FlowWatchProgressDto finalProgress)
    {
        if (_actorShutdownCts.IsCancellationRequested) return true;
        var shouldPokeLead = false;
        var settledRun = true;
        await EnqueueAsync(async _ =>
        {
            if (!_activeWatches.TryGetValue(watchState.TaskId, out var current) || !ReferenceEquals(current, watchState))
            {
                return;
            }

            var task = _taskCoordinator.RequireTaskCore(watchState.TaskId);
            if (watchState.Cts.IsCancellationRequested || task.Status != TaskRunStatus.Running)
                return;

            // Enforce Downstream Convergence Barrier right before publishing a new repeating wave:
            // an independent upstream (e.g. cron join wake) may have activated a descendant while patrolling.
            if (watchState.Repeat && outcome == FlowOutcome.Ok && HasActiveDescendantCore(watchState.NodeId))
            {
                settledRun = false;
                watchState.Progress = finalProgress with { Detail = "Waiting for active downstream to finish" };
                if (watchState.UsesPerProbeWorkspace && watchState.HoldsWorkspace)
                {
                    watchState.HoldsWorkspace = false;
                    await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                return;
            }

            var now = NowMs();
            var settled = task with
            {
                Status = TaskRunStatus.Done,
                RunCount = watchState.Repeat ? checked(task.RunCount + 1) : task.RunCount,
                Outcome = outcome,
                OutcomeSummary = summary,
                StatusDetail = outcome == FlowOutcome.Ok ? null : summary,
                Meta = JsonHelpers.ToElement(new { watchProgress = finalProgress }),
                FinishedAtMs = now,
                UpdatedAtMs = now
            };
            var wakePrompt = outcome == FlowOutcome.Blocked || (watchState.Repeat && outcome == FlowOutcome.Attention)
                ? $"Flow watch node '{settled.TaskKey}' settled as {FlowConstants.FormatOutcome(outcome)}: {summary}"
                : null;
            shouldPokeLead = wakePrompt is not null;

            await _taskCoordinator.PersistAndEmitTaskUpdatedCoreAsync(settled, now, CancellationToken.None, wakePrompt: wakePrompt, suppressDefaultWake: wakePrompt is null)
                .ConfigureAwait(false);
            _taskCoordinator.ReplaceTaskInMemoryCore(settled);
            _activeWatches.Remove(watchState.TaskId);
            watchState.Cts.Dispose();
            await OnTaskSettledFlowHookCoreAsync(settled, CancellationToken.None).ConfigureAwait(false);
            await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }, CancellationToken.None).ConfigureAwait(false);

        if (shouldPokeLead)
        {
            await PokePendingWakesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return settledRun;
    }

    private ActiveWatchState[] BeginWatchCancellationForNodeCore(string nodeId)
    {
        var canceled = new List<ActiveWatchState>();
        foreach (var kvp in _activeWatches.ToArray())
        {
            if (string.Equals(kvp.Value.NodeId, nodeId, StringComparison.Ordinal))
            {
                try { kvp.Value.Cts.Cancel(); } catch { }
                canceled.Add(kvp.Value);
            }
        }

        return [.. canceled];
    }

    private void CancelAllActiveWatchesCore()
    {
        ScheduleWatchRearmCore(null);
        foreach (var kvp in _activeWatches.ToArray())
        {
            _activeWatches.Remove(kvp.Key);
            try { kvp.Value.Cts.Cancel(); } catch { }
            kvp.Value.Cts.Dispose();
        }
    }

    private sealed class FlowWakeWorkItem(TrackActor actor, string sessionId, string prompt, Guid reservation)
        : ReservedTurnWorkItem(actor, sessionId, reservation)
    {
        public override string TriggerKind => "wake";
        protected override string? Presentation => TrackWakePresentations.System;
        public override Task<string?> PreparePromptAsync(CancellationToken ct) => Task.FromResult<string?>(prompt);
        public override Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId) =>
            Task.CompletedTask;
        public override async Task OnTurnSettledAsync(TurnSettlement settlement) =>
            await settlement.SettleAsync().ConfigureAwait(false);
    }
}
