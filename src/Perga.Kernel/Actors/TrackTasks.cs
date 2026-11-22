using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    // State transitions run in the owning Actor mailbox; external phases explicitly re-enter it.
    private sealed class TrackTasks(TrackActor actor)
    {
        private readonly List<TaskDto> _tasks = [];
        private readonly Dictionary<string, ActiveTaskRunState> _activeTasks = new(StringComparer.Ordinal);
        private int _activeTaskCount;

        public void SetPaused(ActiveTaskRunState run, bool paused) => run.SetPaused(paused);
        public TaskDto[] Snapshot() => _tasks.ToArray();
        public bool HasActiveWork => Volatile.Read(ref _activeTaskCount) > 0;
        public ActiveTaskRunState[] ActiveRuns => _activeTasks.Values.ToArray();
        public ActiveTaskRunState GetActiveRun(string id) => _activeTasks[id];
        public bool HasActiveRun(string id) => _activeTasks.ContainsKey(id);
        public bool TryGetActiveRun(string id, out ActiveTaskRunState run) => _activeTasks.TryGetValue(id, out run!);
        public void ReplaceDeclarations(IEnumerable<TaskDto> tasks)
        {
            _tasks.Clear();
            _tasks.AddRange(tasks);
        }
        public void RegisterActiveRun(ActiveTaskRunState run)
        {
            _activeTasks[run.TaskId] = run;
            Volatile.Write(ref _activeTaskCount, _activeTasks.Count);
        }
        public void RemoveActiveRun(string id)
        {
            _activeTasks.Remove(id);
            Volatile.Write(ref _activeTaskCount, _activeTasks.Count);
        }

        public Task RefreshTaskDiagnosticsAsync(CancellationToken ct) => actor.EnqueueAsync(async _ =>
        {
            var snapshot = await actor._store.LoadTrackAggregateAsync(actor.TrackId, ct).ConfigureAwait(false);
            if (snapshot is null) return;
            foreach (var persisted in snapshot.Tasks)
            {
                var current = _tasks.FirstOrDefault(task => task.Id == persisted.Id);
                if (current is not null && current.RunCount == persisted.RunCount && current.UpdatedAtMs == persisted.UpdatedAtMs)
                    ReplaceTaskInMemoryCore(current with { WorkerResult = persisted.WorkerResult, GateVerdict = persisted.GateVerdict });
            }
        }, ct);

        public Task<IReadOnlyList<TaskDto>> ListTasksAsync(CancellationToken ct = default)
        {
            return actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                return (IReadOnlyList<TaskDto>)_tasks.ToArray();
            }, ct);
        }

        public void RequireTaskOwnershipCore(string? callerSessionId, TaskDto task)
        {
            if (callerSessionId is null) return;
            var caller = actor.RequireSessionCore(callerSessionId);
            var target = task.SessionId is { } sid && actor._sessions.TryGetValue(sid, out var found) ? found : null;
            if (target is not null)
                SessionPolicy.RequireAccess(new InteropPrincipal.Session(actor.TrackId, caller.Id, caller.IsLead,
                    TryGetActiveOwningTaskCore(caller.Id)?.Id, caller.SpawnDepth, "", null), target, caller, write: true,
                    activeTurn: actor._scheduler.HasActive(target.Id), destructive: true);
            else if (!caller.IsLead || caller.ArchivedAtMs is not null)
                throw KernelException.Forbidden("Only the Track Lead can govern a task without a session.");
        }

        public async Task<TaskDto> CancelTaskAsync(string? callerSessionId, string taskKeyOrId, string? reason = null, CancellationToken ct = default)
        {
            async Task<TaskDto> SettleCoreAsync(TaskDto task, ActiveTaskRunState? active)
            {
                var now = actor.NowMs();
                if (active is not null)
                    await FinalizeCanceledTaskCoreAsync(active, CancellationToken.None).ConfigureAwait(false);
                else if (task.SessionId is { } sid)
                    await InterruptSessionQueueForTaskSettlementCoreAsync(sid, CancellationToken.None).ConfigureAwait(false);
                var updatedTask = task with
                {
                    Status = TaskRunStatus.Canceled,
                    Ready = false,
                    StatusDetail = string.IsNullOrWhiteSpace(reason) ? "Canceled." : reason!.Trim(),
                    FinishedAtMs = task.FinishedAtMs ?? now,
                    UpdatedAtMs = now
                };
                ReplaceTaskInMemoryCore(updatedTask);
                var after = actor.EvaluateAfterPolicyEventCore("task.settled", JsonHelpers.ToElement(new {
                    taskKey = task.TaskKey, accessMode = TrackActor.FormatAccessModeForPolicy(task.Access),
                    status = "canceled", workerResult = updatedTask.WorkerResult,
                    meta = updatedTask.Meta ?? JsonHelpers.EmptyObject, gateVerdict = (JsonElement?)null
                }));
                await PersistAndEmitTaskUpdatedCoreAsync(updatedTask, after.NextState, now, CancellationToken.None, after.Output).ConfigureAwait(false);
                await ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await actor.OnTaskSettledFlowHookCoreAsync(updatedTask, CancellationToken.None).ConfigureAwait(false);
                return updatedTask;
            }

            var cancellation = await actor.EnqueueAsync<(TaskDto? Completed, ActiveTaskRunState? Active, Task Cleanup)>(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var task = RequireTaskCore(taskKeyOrId);
                RequireTaskOwnershipCore(callerSessionId, task);
                if (FlowConstants.IsFlowTask(task))
                {
                    var cleanup = await actor.CancelFlowTaskCoreAsync(task, reason, ct).ConfigureAwait(false);
                    return (RequireTaskCore(task.Id), null, cleanup);
                }
                var active = _activeTasks.TryGetValue(task.Id, out var found) ? found : null;
                ct.ThrowIfCancellationRequested();
                // A pending task must be canceled atomically, before another mailbox operation can start it.
                if (active is null) return (await SettleCoreAsync(task, null).ConfigureAwait(false), null, Task.CompletedTask);
                BeginTaskCancellationCore(active);
                return (null, active, Task.CompletedTask);
            }, CancellationToken.None).ConfigureAwait(false);
            await cancellation.Cleanup.ConfigureAwait(false);
            if (cancellation.Completed is { } completed) return completed;
            await CloseCanceledWorkerAsync(cancellation.Active!).ConfigureAwait(false);
            return await actor.EnqueueAsync(_ => SettleCoreAsync(RequireTaskCore(taskKeyOrId), cancellation.Active), CancellationToken.None).ConfigureAwait(false);
        }

        public async Task<TaskDto> CompleteTaskDoneAsync(
            string callerSessionId,
            JsonElement? workerResult = null,
            CancellationToken ct = default,
            WorkerCompletionOrigin? origin = null,
            FlowOutcome outcome = FlowOutcome.Ok,
            string? summary = null)
        {
            var taskId = await actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var (_, active) = actor.RequireBoundTaskSessionCore(callerSessionId, "task_done");
                return active.TaskId;
            }, ct).ConfigureAwait(false);

            return await CompleteTaskDoneByTaskIdAsync(taskId, workerResult, ct, origin, outcome, summary).ConfigureAwait(false);
        }

        public async Task<TaskDto> CompleteTaskFailAsync(
            string callerSessionId,
            string reason,
            JsonElement? workerResult = null,
            CancellationToken ct = default, WorkerCompletionOrigin? origin = null)
        {
            var taskId = await actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var (_, active) = actor.RequireBoundTaskSessionCore(callerSessionId, "task_fail");
                return active.TaskId;
            }, ct).ConfigureAwait(false);

            return await CompleteTaskFailByTaskIdAsync(taskId, reason, workerResult, ct, origin).ConfigureAwait(false);
        }

        // ------------------- Asks, Mail, Terminals, Policy Hooks & Schedules -------------------

        public Task TriggerSchedulerAsync(CancellationToken ct = default)
        {
            return actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                await actor.EvaluateFlowAfterTriggersCoreAsync(ct).ConfigureAwait(false);
                await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
                await actor.TryRunFlowGcCoreAsync(ct).ConfigureAwait(false);
            }, ct);
        }

        public async Task ScheduleEligibleTasksCoreAsync(CancellationToken ct, IReadOnlyList<ProviderStatus>? providers = null)
        {
            if (actor._policyUnavailableReason is not null || actor._options.DispatchAdmission.Draining || actor._deletingTrack || actor._closingTrack || actor._track.ClosedAtMs is not null || !actor.WorkspaceReady)
            {
                return;
            }

            if (_activeTasks.Values.Any(run => run.Settling &&
                _tasks.Any(t => t.Id == run.TaskId && t.Status == TaskRunStatus.Canceled)))
                return;

            var mounted = await actor.EnsureWorkspaceMountedCoreAsync(ct).ConfigureAwait(false);
            foreach (var watch in _tasks.Where(t => t.Kind == "flow_watch" && FlowConstants.IsFlowTask(t) && t.Status == TaskRunStatus.Running &&
                !actor._activeWatches.ContainsKey(t.Id)).ToArray())
                await actor.StartFlowWatchRunCoreAsync(watch, mounted, ct).ConfigureAwait(false);
            bool preparationFailed;
            do
            {
                preparationFailed = false;
                var hasActiveCommandWatch = actor._activeWatches.Values.Any(watch => !watch.IsWorkspaceExempt);
                var schedulable = TaskGraphScheduler.ComputeSchedulable(_tasks, flowSpec: actor._flowSpec, hasActiveCommandWatch: hasActiveCommandWatch);
                foreach (var task in schedulable)
                {
                    if (task.Kind == "flow_wake" && FlowConstants.IsFlowTask(task))
                    {
                        if (!await actor.StartFlowWakeRunCoreAsync(task, ct).ConfigureAwait(false))
                            preparationFailed = true;
                        continue;
                    }

                    if (task.Kind == "flow_watch" && FlowConstants.IsFlowTask(task))
                    {
                        var isCmdWatch = task.Context is { ValueKind: JsonValueKind.Object } wctx &&
                                         wctx.TryGetProperty("cmd", out var cmdEl) &&
                                         cmdEl.ValueKind == JsonValueKind.String &&
                                         !string.IsNullOrWhiteSpace(cmdEl.GetString());
                        if (isCmdWatch)
                        {
                            try
                            {
                                var watchRoute = actor.RouteTaskCore(task);
                                if (!await actor.EnsureTaskDispatchApprovedCoreAsync(task, watchRoute.Route, ct).ConfigureAwait(false))
                                    continue;
                            }
                            catch (Exception ex) when (!ct.IsCancellationRequested)
                            {
                                var failedWatch = task with
                                {
                                    Status = TaskRunStatus.Failed,
                                    RunCount = checked(task.RunCount + 1),
                                    Outcome = FlowOutcome.Attention,
                                    OutcomeSummary = ex.Message,
                                    StatusDetail = ex.Message,
                                    FinishedAtMs = actor.NowMs(),
                                    UpdatedAtMs = actor.NowMs()
                                };
                                await PersistAndEmitTaskUpdatedCoreAsync(failedWatch, null, failedWatch.UpdatedAtMs, ct).ConfigureAwait(false);
                                ReplaceTaskInMemoryCore(failedWatch);
                                preparationFailed |= await actor.EvaluateFlowAfterTriggersCoreAsync(ct).ConfigureAwait(false);
                                await actor.TryRunFlowGcCoreAsync(ct).ConfigureAwait(false);
                                continue;
                            }
                        }

                        if (!await actor.StartFlowWatchRunCoreAsync(task, mounted, ct).ConfigureAwait(false))
                            preparationFailed = true;
                        continue;
                    }

                    PolicyTaskRoute route;
                    try
                    {
                        var taskRoute = actor.RouteTaskCore(task);
                        route = taskRoute.Route;
                        if (!await actor.EnsureTaskDispatchApprovedCoreAsync(task, route, ct).ConfigureAwait(false)) continue;
                        if (TrackActor.NeedsTaskProviderCatalog(route))
                        {
                            if (providers is null)
                            {
                                actor.StartTaskProviderProbeCore();
                                continue;
                            }
                            actor.ValidateTaskProviderModelCore(route, providers, validateModel: !taskRoute.UsesPreparedDefault);
                        }
                    }
                    catch (KernelException ex) when (ex.Code == KernelErrorCode.Conflict && !ct.IsCancellationRequested)
                    {
                        // Probe outages are retryable; retain the pending task for the next scheduling pass.
                        continue;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        var failed = task with
                        {
                            Status = TaskRunStatus.Failed,
                            RunCount = FlowConstants.IsFlowTask(task) ? checked(task.RunCount + 1) : task.RunCount,
                            Outcome = FlowOutcome.Attention,
                            OutcomeSummary = ex.Message,
                            StatusDetail = ex.Message,
                            FinishedAtMs = actor.NowMs(),
                            UpdatedAtMs = actor.NowMs()
                        };
                        await PersistAndEmitTaskUpdatedCoreAsync(failed, null, failed.UpdatedAtMs, ct).ConfigureAwait(false);
                        ReplaceTaskInMemoryCore(failed);
                        preparationFailed |= await actor.EvaluateFlowAfterTriggersCoreAsync(ct).ConfigureAwait(false);
                        await actor.TryRunFlowGcCoreAsync(ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!await StartTaskRunCoreAsync(task, route, mounted, ct).ConfigureAwait(false))
                        preparationFailed = true;
                }
            } while (preparationFailed);
        }

        public async Task<bool> StartTaskRunCoreAsync(
            TaskDto task,
            PolicyTaskRoute route,
            MountedWorkspace mounted,
            CancellationToken ct)
        {
            var admission = actor._options.DispatchAdmission.TryEnter();
            if (admission is null) return true;
            var transferred = false;

            try
            {
                var now = actor.NowMs();
                var nextRunCount = checked(task.RunCount + 1);
                var isCommandTask = string.Equals(task.Kind, "command", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(task.Kind, "cmd", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(task.Kind, "shell", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(task.Kind, "terminal", StringComparison.OrdinalIgnoreCase);
                var workerProvider = route.Provider ?? actor.GetDefaultProviderCore();
                var previousSession = actor.ReadTaskDispatchSessionCore(task);
                var preserveSessionContext = TrackActor.IsFlowSessionReuse(task) && previousSession is not null;
                if (previousSession is not null &&
                    TryGetActiveOwningTaskCore(previousSession.Id) is { } owningTask &&
                    owningTask.Id != task.Id)
                {
                    if (preserveSessionContext)
                    {
                        return true;
                    }

                    // Historical session is currently leased to another active task; spawn a fresh session instead of reclaiming it.
                    previousSession = null;
                }
                // Orphaned task sessions can be rebound after live provider ownership is closed.
                var orphaned = !preserveSessionContext && previousSession is not null && !_activeTasks.ContainsKey(task.Id);
                if (previousSession is not null && actor._scheduler.IsOccupied(
                    orphaned ? previousSession with { Status = "idle" } : previousSession)) return true;
                if (previousSession is not null && (task.RunCount > 0 || preserveSessionContext))
                {
                    actor._mcpBridge.RevokeSession(previousSession.Id);
                    if (actor._runtimes.TryGetOpen(previousSession.Id, out _))
                    {
                        var cleanupReservation = Guid.NewGuid();
                        actor._scheduler.Reserve(previousSession.Id, cleanupReservation);
                        _ = actor.TrackBackgroundJob(async () =>
                        {
                            var confirmed = false;
                            try
                            {
                                await actor._runtimes.Get(previousSession.Id).CloseAsync(actor._options.SessionStartupTimeout,
                                    actor._actorShutdownCts.Token).ConfigureAwait(false);
                                confirmed = true;
                            }
                            catch { /* Retain ownership and fence reuse until explicit recovery. */ }
                            if (!actor._actorShutdownCts.IsCancellationRequested)
                                await actor.EnqueueAsync(async _ =>
                                {
                                    actor._scheduler.ReleaseReservation(previousSession.Id);
                                    if (confirmed) await ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
                                    else
                                    {
                                        await actor.MarkShutdownUnconfirmedIfNeededAsync(previousSession.Id).ConfigureAwait(false);
                                    }
                                }, CancellationToken.None).ConfigureAwait(false);
                        });
                        return true;
                    }
                    actor._scheduler.Clear(previousSession.Id);
                }
                var subagent = isCommandTask ? null : previousSession is not null ? (preserveSessionContext ? previousSession with
                {
                    Status = "idle",
                    ArchivedAtMs = null,
                    UnknownReason = null, UnknownTurnId = null, UnknownLastError = null, ReconnectInvalidated = false,
                    UpdatedAtMs = now
                } : previousSession with
                {
                    Status = "idle", NativeThreadId = null, ContextUsage = null,
                    ArchivedAtMs = null,
                    UnknownReason = null, UnknownTurnId = null, UnknownLastError = null, ReconnectInvalidated = false,
                    Epoch = task.RunCount == 0 ? previousSession.Epoch : checked(previousSession.Epoch + 1),
                    UpdatedAtMs = now
                }) : new AgentSessionDto(
                    Id: $"ses_{Guid.NewGuid():N}",
                    TrackId: actor.TrackId,
                    IsLead: false,
                    Title: $"Task: {task.TaskKey}",
                    Provider: workerProvider,
                    Model: route.Model ?? actor.ResolveTaskWorkerModelCore(workerProvider),
                    NativeThreadId: null,
                    Epoch: 1,
                    Status: "idle",
                    ArchivedAtMs: null,
                    CreatedAtMs: now,
                    UpdatedAtMs: now,
                    PermissionMode: RuntimePermissionModes.Resolve(workerProvider, null));

                var effectiveTask = actor.InterpolateFlowTaskForExecutionCore(task);
                var runningTask = task with
                {
                    SessionId = subagent?.Id,
                    RunCount = nextRunCount,
                    Status = TaskRunStatus.Running,
                    StatusDetail = null,
                    Outcome = FlowOutcome.None,
                    OutcomeSummary = null,
                    WorkerResult = null,
                    Meta = JsonHelpers.EmptyObject,
                    GateVerdict = null,
                    StartedAtMs = now,
                    FinishedAtMs = null,
                    UpdatedAtMs = now
                };
                var sessionEvent = subagent is null ? null : actor.SessionUpdatedEvent(subagent);
                var prompt = subagent is null ? null : TrackActor.BuildContractSubagentPrompt(effectiveTask with { SessionId = subagent.Id, RunCount = nextRunCount });
                var initial = subagent is null ? null : new SessionMessage(
                    $"inp_{task.Id}_{nextRunCount}",
                    subagent.Id,
                    InstallationAuthor,
                    $"initial:{task.Id}:{nextRunCount}",
                    RequestIdentity.Hash(new { prompt }),
                    $"trn_{Guid.NewGuid():N}",
                    null,
                    prompt!,
                    "dispatching",
                    now,
                    "worker");
                try
                {
                    var taskEvent = CreateTaskUpdatedEventCore(now);
                    await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
                    await actor._store.UpsertTaskAsync(runningTask, null, taskEvent, ct, subagent, sessionEvent, initialMessage: initial, writeNewRunBoundary: previousSession is not null && task.RunCount > 0 && !preserveSessionContext).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var failedAt = actor.NowMs();
                    var failed = task with { Status = TaskRunStatus.Failed, Outcome = FlowOutcome.Attention, OutcomeSummary = ex.Message, StatusDetail = ex.Message, FinishedAtMs = failedAt, UpdatedAtMs = failedAt };
                    await PersistAndEmitTaskUpdatedCoreAsync(failed, null, failed.UpdatedAtMs, CancellationToken.None).ConfigureAwait(false);
                    ReplaceTaskInMemoryCore(failed);
                    return false;
                }

                ReplaceTaskInMemoryCore(runningTask);
                if (subagent is not null)
                {
                    actor._sessions[subagent.Id] = subagent;
                    actor._nextTurnIndexBySession[subagent.Id] = await actor._store.GetLastTurnIndexAsync(subagent.Id, CancellationToken.None).ConfigureAwait(false);
                }

                var runCts = CancellationTokenSource.CreateLinkedTokenSource(actor._actorShutdownCts.Token);
                _activeTasks[task.Id] = new ActiveTaskRunState(task.Id, nextRunCount, subagent?.Id, runCts);
                Volatile.Write(ref _activeTaskCount, _activeTasks.Count);
                var runToken = runCts.Token;
                if (subagent is null)
                    _activeTasks[task.Id].Execution = actor.TrackBackgroundJob(async () =>
                    {
                        using (admission) await RunCommandTaskInBackgroundAsync(task.Id, effectiveTask with { RunCount = nextRunCount }, mounted.MergedCwd, runToken).ConfigureAwait(false);
                    });
                else
                {
                    var startupReservation = Guid.NewGuid();
                    actor._scheduler.Reserve(subagent.Id, startupReservation);
                    _ = actor.TrackBackgroundJob(async () =>
                    {
                        using (admission) await RunSubagentTaskInBackgroundAsync(task.Id, nextRunCount, initial!, startupReservation, runToken).ConfigureAwait(false);
                    });
                }

                transferred = true;
                var evt = sessionEvent ?? CreateTaskUpdatedEventCore(now);
                await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, evt, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            finally { if (!transferred) admission.Dispose(); }
        }

        public const int TaskOutputTailChars = 4096;

        public async Task RunCommandTaskInBackgroundAsync(
            string taskId,
            TaskDto task,
            string workspaceCwd,
            CancellationToken ct)
        {
            try
            {
                var timeoutSecs = task.Gate?.TimeoutSecs ?? actor._options.DefaultHookTimeoutSecs;
                var cmdRes = await actor.EffectiveCommandRunner.RunAsync(
                    new ShellCommandRequest(task.GoalOrCmd, workspaceCwd, TimeSpan.FromSeconds(Math.Max(1, timeoutSecs)),
                        OutputTailChars: TaskOutputTailChars),
                    ct).ConfigureAwait(false);

                var payload = JsonHelpers.ToElement(new
                {
                    exitCode = cmdRes.ExitCode,
                    stdout = cmdRes.Stdout,
                    stderr = cmdRes.Stderr,
                    stdoutTruncated = cmdRes.StdoutTruncated,
                    stderrTruncated = cmdRes.StderrTruncated,
                    timedOut = cmdRes.TimedOut,
                    durationMs = cmdRes.DurationMs
                });

                if (FlowConstants.IsFlowTask(task))
                {
                    var tail = FlowConstants.ExtractTailLine(cmdRes.Stdout, cmdRes.Stderr);
                    if (cmdRes.TimedOut)
                    {
                        await CompleteTaskDoneByTaskIdAsync(taskId, payload, ct, outcome: FlowOutcome.Blocked,
                            summary: $"Command timed out after {timeoutSecs}s.").ConfigureAwait(false);
                    }
                    else if (cmdRes.ExitCode == 0)
                    {
                        await CompleteTaskDoneByTaskIdAsync(taskId, payload, ct, outcome: FlowOutcome.Ok,
                            summary: tail).ConfigureAwait(false);
                    }
                    else
                    {
                        await CompleteTaskDoneByTaskIdAsync(taskId, payload, ct, outcome: FlowOutcome.Attention,
                            summary: tail ?? $"Command exited with code {cmdRes.ExitCode}.").ConfigureAwait(false);
                    }
                }
                else if (cmdRes.ExitCode == 0 && !cmdRes.TimedOut)
                {
                    await CompleteTaskDoneByTaskIdAsync(taskId, payload, ct).ConfigureAwait(false);
                }
                else
                {
                    var err = cmdRes.TimedOut
                        ? $"Command timed out after {timeoutSecs}s."
                        : $"Command exited with code {cmdRes.ExitCode}.";
                    await CompleteTaskFailByTaskIdAsync(taskId, err, payload, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Canceled via governance or track teardown.
            }
            catch (Exception ex)
            {
                try
                {
                    await CompleteTaskFailByTaskIdAsync(taskId, ex.Message, null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Ignore if already settled.
                }
            }
        }

        public async Task RunSubagentTaskInBackgroundAsync(
            string taskId,
            int runCount,
            SessionMessage initial,
            Guid reservation,
            CancellationToken ct)
        {
            try
            {
                var turn = await actor._turnRunner.RunAsync(new TaskLaunchWorkItem(actor, initial, reservation), ct, alreadyAdmitted: true).ConfigureAwait(false);
                if (turn?.Status == "failed")
                    await FailCurrentRunAsync(turn.ErrorText ?? "Contract subagent turn failed.").ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Task or session was canceled.
            }
            catch (Exception ex)
            {
                await FailCurrentRunAsync(ex.Message).ConfigureAwait(false);
            }
            finally
            {
                if (!actor._actorShutdownCts.IsCancellationRequested)
                    await actor.EnqueueAsync(_ =>
                    {
                        if (actor._scheduler.TryGetReservation(initial.SessionId, out var held) && held == reservation)
                            actor._scheduler.ReleaseReservation(initial.SessionId);
                        return Task.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
            }

            async Task FailCurrentRunAsync(string reason)
            {
                var stillActive = await actor.EnqueueAsync(_ => Task.FromResult(
                    _activeTasks.TryGetValue(taskId, out var active) && active.RunCount == runCount &&
                    !active.Settling && !active.Paused), CancellationToken.None).ConfigureAwait(false);
                if (stillActive)
                    await CompleteTaskFailByTaskIdAsync(taskId, reason, null, CancellationToken.None,
                        expectedRunCount: runCount).ConfigureAwait(false);
            }
        }

        public async Task<TaskDto> CompleteTaskDoneByTaskIdAsync(
            string taskId,
            JsonElement? workerResult,
            CancellationToken ct,
            WorkerCompletionOrigin? origin = null,
            FlowOutcome outcome = FlowOutcome.Ok,
            string? summary = null)
        {
            Interlocked.Increment(ref actor._inFlightExternalOps);
            try
            {
                var verificationToken = actor._actorShutdownCts.Token;
                var prep = await actor.EnqueueAsync<(TaskDto Task, ActiveTaskRunState Active, string WorkspaceCwd, string? AfterWorkerDoneCmd)>(async _ =>
                {
                    await actor.EnsureHydratedCoreAsync(verificationToken).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (!_activeTasks.TryGetValue(taskId, out var active))
                    {
                        throw KernelException.Conflict($"Task '{taskId}' is not active on track '{actor.TrackId}'.");
                    }

                    actor.CheckWorkerCompletionCore(active, origin);
                    var mounted = await actor.EnsureWorkspaceMountedCoreAsync(verificationToken).ConfigureAwait(false);
                    var task = RequireTaskCore(active.TaskId);
                    var now = actor.NowMs();
                    var clonedWorkerResult = workerResult?.Clone();

                    var verifyingTask = task with
                    {
                        Status = TaskRunStatus.Verifying,
                        WorkerResult = clonedWorkerResult,
                        UpdatedAtMs = now
                    };
                    ReplaceTaskInMemoryCore(verifyingTask);
                    if (active.SessionId is { } sid) actor._mcpBridge.RevokeSession(sid);

                    await PersistAndEmitTaskUpdatedCoreAsync(verifyingTask, nextTemplateState: null, now, verificationToken)
                        .ConfigureAwait(false);

                    return (actor.InterpolateFlowTaskForExecutionCore(verifyingTask), active, mounted.MergedCwd, actor._policy?.Manifest.AfterWorkerDoneCmd);
                }, verificationToken).ConfigureAwait(false);

                var mergedMeta = prep.Task.Meta ?? JsonHelpers.EmptyObject;
                var gatePassed = true;
                string? failedStepReason = null;
                JsonElement? gateVerdict = null;

                try
                {
                    // 1. Run AfterWorkerDoneCmd (if non-empty) in WorkspaceCwd and merge last valid JSON stdout line into task.Meta.
                    if (!string.IsNullOrWhiteSpace(prep.AfterWorkerDoneCmd))
                    {
                        var hookResult = await actor.EffectiveCommandRunner.RunAsync(
                            new ShellCommandRequest(
                                prep.AfterWorkerDoneCmd!,
                                prep.WorkspaceCwd,
                                TimeSpan.FromSeconds(actor._options.DefaultHookTimeoutSecs)),
                            verificationToken).ConfigureAwait(false);

                        mergedMeta = JsonHelpers.MergeLastValidJsonLine(mergedMeta, hookResult.Stdout);
                    }

                    // 2. Run GateSpecDto steps in WorkspaceCwd.
                    if (prep.Task.Gate is { Steps.Count: > 0 } gateSpec)
                    {
                        var stepRecords = new List<object>(gateSpec.Steps.Count);
                        var stepTimeout = TimeSpan.FromSeconds(Math.Max(1, gateSpec.TimeoutSecs));

                        foreach (var step in gateSpec.Steps)
                        {
                            var res = await actor.EffectiveCommandRunner.RunAsync(
                                new ShellCommandRequest(step.Cmd, prep.WorkspaceCwd, stepTimeout, OutputTailChars: TaskOutputTailChars),
                                verificationToken).ConfigureAwait(false);

                            var stepPassed = res.ExitCode == 0 && !res.TimedOut;
                            var combinedOut = string.IsNullOrEmpty(res.Stderr)
                                ? res.Stdout
                                : string.IsNullOrEmpty(res.Stdout)
                                    ? res.Stderr
                                    : $"{res.Stdout}\n{res.Stderr}";
                            var outputTail = combinedOut.Length <= 4096
                                ? combinedOut
                                : combinedOut[^4096..];

                            stepRecords.Add(new
                            {
                                name = step.Name,
                                cmd = step.Cmd,
                                passed = stepPassed,
                                exitCode = res.ExitCode,
                                stdout = res.Stdout,
                                stderr = res.Stderr,
                                stdoutTruncated = res.StdoutTruncated,
                                stderrTruncated = res.StderrTruncated,
                                outputTail,
                                timedOut = res.TimedOut,
                                durationMs = res.DurationMs
                            });

                            if (!stepPassed)
                            {
                                gatePassed = false;
                                failedStepReason = res.TimedOut
                                    ? $"Gate step '{step.Name}' timed out."
                                    : $"Gate step '{step.Name}' exited with code {res.ExitCode}.";
                                break;
                            }
                        }

                        gateVerdict = JsonHelpers.ToElement(new
                        {
                            v = 1,
                            passed = gatePassed,
                            failedReason = failedStepReason,
                            steps = stepRecords
                        });
                    }
                }
                catch (Exception ex) when (!verificationToken.IsCancellationRequested)
                {
                    gatePassed = false;
                    failedStepReason = $"Verification could not finish: {ex.Message}";
                    gateVerdict = JsonHelpers.ToElement(new { v = 1, passed = false, failedReason = failedStepReason });
                }

                var (finalTask, wakePrompt) = await actor.EnqueueAsync<(TaskDto FinalTask, string WakePrompt)>(async _ =>
                {
                    var currentTask = RequireTaskCore(prep.Task.Id);
                    if (!_activeTasks.TryGetValue(taskId, out var current) || !ReferenceEquals(current, prep.Active) ||
                        currentTask.Status != TaskRunStatus.Verifying)
                        throw KernelException.Conflict("The task ended while its gate was running.");
                    _activeTasks.Remove(taskId);
                    Volatile.Write(ref _activeTaskCount, _activeTasks.Count);
                    current.Cts.Dispose();
                    await InterruptSessionQueueForTaskSettlementCoreAsync(current.SessionId, verificationToken).ConfigureAwait(false);
                    var now = actor.NowMs();

                    var resolvedOutcome = gatePassed
                        ? (outcome == FlowOutcome.None ? FlowOutcome.Ok : outcome)
                        : FlowOutcome.Attention;
                    var resolvedSummary = gatePassed
                        ? (!string.IsNullOrWhiteSpace(summary) ? summary.Trim() : ExtractSummaryFromWorkerResult(currentTask.WorkerResult))
                        : failedStepReason;

                    var settledTask = currentTask with
                    {
                        Status = gatePassed ? TaskRunStatus.Done : TaskRunStatus.Failed,
                        StatusDetail = gatePassed ? null : failedStepReason,
                        Outcome = resolvedOutcome,
                        OutcomeSummary = resolvedSummary,
                        Meta = mergedMeta,
                        GateVerdict = gateVerdict,
                        FinishedAtMs = now,
                        UpdatedAtMs = now
                    };
                    ReplaceTaskInMemoryCore(settledTask);

                    var statusLabel = gatePassed ? "done" : "failed";
                    var after = actor.EvaluateAfterPolicyEventCore("task.settled", JsonHelpers.ToElement(new {
                        taskKey = currentTask.TaskKey, accessMode = TrackActor.FormatAccessModeForPolicy(currentTask.Access),
                        status = statusLabel, outcome = FlowConstants.FormatOutcome(resolvedOutcome),
                        outcomeSummary = resolvedSummary, workerResult = settledTask.WorkerResult,
                        meta = mergedMeta, gateVerdict
                    }));
                    var nextTemplateState = after.NextState;
                    var policyNote = after.Output;

                    var summarySb = new StringBuilder();
                    summarySb.Append($"Task '{settledTask.TaskKey}' (run #{settledTask.RunCount}) settled as {settledTask.Status} (outcome={FlowConstants.FormatOutcome(resolvedOutcome)}).");
                    if (!string.IsNullOrWhiteSpace(failedStepReason))
                    {
                        summarySb.Append($" Reason: {failedStepReason}");
                    }
                    else if (!string.IsNullOrWhiteSpace(resolvedSummary))
                    {
                        summarySb.Append($" Summary: {resolvedSummary}");
                    }

                    if (!string.IsNullOrWhiteSpace(policyNote))
                    {
                        summarySb.Append('\n').Append(policyNote);
                    }

                    await PersistAndEmitTaskUpdatedCoreAsync(settledTask, nextTemplateState, now, verificationToken, summarySb.ToString())
                        .ConfigureAwait(false);

                    await actor.EvaluateFlowAfterTriggersCoreAsync(verificationToken).ConfigureAwait(false);
                    await ScheduleEligibleTasksCoreAsync(verificationToken).ConfigureAwait(false);
                    await actor.TryRunFlowGcCoreAsync(verificationToken).ConfigureAwait(false);

                    return (settledTask, summarySb.ToString());
                }, verificationToken).ConfigureAwait(false);

                await actor.PokePendingWakesAsync(verificationToken).ConfigureAwait(false);
                return finalTask;
            }
            finally
            {
                Interlocked.Decrement(ref actor._inFlightExternalOps);
            }
        }

        public async Task<TaskDto> CompleteTaskFailByTaskIdAsync(
            string taskId,
            string reason,
            JsonElement? workerResult,
            CancellationToken ct, WorkerCompletionOrigin? origin = null, int? expectedRunCount = null)
        {
            Interlocked.Increment(ref actor._inFlightExternalOps);
            try
            {
                var active = await actor.EnqueueAsync(async _ =>
                {
                    await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                    if (!_activeTasks.TryGetValue(taskId, out var state))
                    {
                        throw KernelException.Conflict($"Task '{taskId}' is not active on track '{actor.TrackId}'.");
                    }

                    if (expectedRunCount is { } run && (state.RunCount != run || state.Settling || state.Paused))
                        throw KernelException.Conflict("The task run already changed.");
                    actor.CheckWorkerCompletionCore(state, origin);
                    state.BeginSettlement();
                    if (state.SessionId is { } sid) actor._mcpBridge.RevokeSession(sid);
                    return state;
                }, ct).ConfigureAwait(false);

                var (failedTask, wakePrompt) = await actor.EnqueueAsync<(TaskDto FailedTask, string WakePrompt)>(async _ =>
                {
                    if (!_activeTasks.TryGetValue(taskId, out var current) || !ReferenceEquals(current, active))
                        throw KernelException.Conflict("The task run already ended.");
                    _activeTasks.Remove(taskId);
                    Volatile.Write(ref _activeTaskCount, _activeTasks.Count);
                    current.Cts.Dispose();
                    await InterruptSessionQueueForTaskSettlementCoreAsync(current.SessionId, ct).ConfigureAwait(false);
                    var currentTask = RequireTaskCore(active.TaskId);
                    var now = actor.NowMs();
                    var clonedWorkerResult = workerResult?.Clone();

                    var updatedTask = currentTask with
                    {
                        Status = TaskRunStatus.Failed,
                        StatusDetail = reason,
                        Outcome = FlowOutcome.Attention,
                        OutcomeSummary = reason,
                        WorkerResult = clonedWorkerResult,
                        FinishedAtMs = now,
                        UpdatedAtMs = now
                    };
                    ReplaceTaskInMemoryCore(updatedTask);

                    var after = actor.EvaluateAfterPolicyEventCore("task.settled", JsonHelpers.ToElement(new {
                        taskKey = currentTask.TaskKey, accessMode = TrackActor.FormatAccessModeForPolicy(currentTask.Access),
                        status = "failed", outcome = "attention", outcomeSummary = reason, workerResult = clonedWorkerResult,
                        meta = currentTask.Meta ?? JsonHelpers.EmptyObject, gateVerdict = (JsonElement?)null
                    }));
                    var nextTemplateState = after.NextState;
                    var policyNote = after.Output;

                    var msg = string.IsNullOrWhiteSpace(policyNote)
                        ? $"Task '{updatedTask.TaskKey}' (run #{updatedTask.RunCount}) failed: {reason}"
                        : $"Task '{updatedTask.TaskKey}' (run #{updatedTask.RunCount}) failed: {reason}\n{policyNote}";
                    await PersistAndEmitTaskUpdatedCoreAsync(updatedTask, nextTemplateState, now, ct, msg)
                        .ConfigureAwait(false);
                    await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
                    await actor.OnTaskSettledFlowHookCoreAsync(updatedTask, ct).ConfigureAwait(false);

                    return (updatedTask, msg);
                }, ct).ConfigureAwait(false);

                await actor.PokePendingWakesAsync(ct).ConfigureAwait(false);
                return failedTask;
            }
            finally
            {
                Interlocked.Decrement(ref actor._inFlightExternalOps);
            }
        }

        public TaskDto? TryGetActiveOwningTaskCore(string sessionId)
        {
            TaskDto? pending = null;
            foreach (var task in _tasks)
            {
                if (!string.Equals(task.SessionId, sessionId, StringComparison.Ordinal) ||
                    (task.Kind == "flow_wake" && FlowConstants.IsFlowTask(task)))
                {
                    continue;
                }

                if (_activeTasks.ContainsKey(task.Id))
                {
                    return task;
                }

                if (task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)
                {
                    return task;
                }

                if (task.Status == TaskRunStatus.Pending && !TrackActor.IsFlowSessionReuse(task))
                {
                    pending ??= task;
                }
            }

            return pending;
        }

        public bool IsTaskOwnedSessionCore(string sessionId) => TryGetActiveOwningTaskCore(sessionId) is not null;

        public (TaskDto Task, ActiveTaskRunState Active)? TryGetActiveTaskForSessionCore(string sessionId)
        {
            if (TryGetActiveOwningTaskCore(sessionId) is not { } task) return null;
            return _activeTasks.TryGetValue(task.Id, out var active) && active.SessionId == sessionId
                ? (task, active)
                : null;
        }

        public bool IsSessionFrozenForDispatchCore(AgentSessionDto session)
        {
            if (session.ArchivedAtMs is not null || actor._track.ClosedAtMs is not null) return true;
            if (TryGetActiveOwningTaskCore(session.Id) is not { } task) return false;
            return !_activeTasks.TryGetValue(task.Id, out var active) || active.SessionId != session.Id || active.Settling || task.Status != TaskRunStatus.Running;
        }

        public bool IsSessionAutoDrainPausedCore(string sessionId) =>
            TryGetActiveTaskForSessionCore(sessionId) is { Active.Paused: true };

        public async Task InterruptSessionQueueForTaskSettlementCoreAsync(string? sessionId, CancellationToken ct)
        {
            if (sessionId is null || actor._store.SessionMessages is null) return;
            var queued = await actor.Messages.ListQueueAsync(sessionId, ct).ConfigureAwait(false);
            var changed = false;
            foreach (var message in queued)
            {
                if (message.Delivery is not ("queued" or "unknown")) continue;
                if (await actor.Messages.TransitionAsync(message with { Delivery = "interrupted" }, message.Delivery, ct).ConfigureAwait(false))
                    changed = true;
            }
            if (changed) actor.NotifyQueueChangedCore(sessionId);
        }

        public void RequireDispatchWorkspace(AgentSessionDto session)
        {
            if (TryGetActiveOwningTaskCore(session.Id) is { } task &&
                (!_activeTasks.TryGetValue(task.Id, out var active) || active.SessionId != session.Id || active.Settling || task.Status != TaskRunStatus.Running))
                throw KernelException.Conflict("The task workspace is no longer available.");
        }

        public static string? ExtractSummaryFromWorkerResult(JsonElement? workerResult)
        {
            if (workerResult is not { ValueKind: JsonValueKind.Object } obj)
            {
                return null;
            }

            if (JsonHelpers.TryGetPropertyIgnoreCase(obj, ["summary", "message", "note"], out var el) &&
                el.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(el.GetString()))
            {
                return el.GetString()!.Trim();
            }

            return null;
        }

        public void RequireTasksOutsideVerification(IEnumerable<ActiveTaskRunState> runs)
        {
            if (runs.Any(run => _tasks.FirstOrDefault(t => string.Equals(t.Id, run.TaskId, StringComparison.Ordinal))?.Status == TaskRunStatus.Verifying))
                throw KernelException.Conflict("Wait for the current verification to finish.");
        }

        public void BeginTaskCancellationCore(ActiveTaskRunState active)
        {
            RequireTasksOutsideVerification([active]);
            active.BeginSettlement();
            active.Cts.Cancel();
            if (active.SessionId is { } sessionId)
            {
                actor._runtimes.Get(sessionId).CancelLaunch();
                actor._mcpBridge.RevokeSession(sessionId);
                if (actor._scheduler.TryGetActive(sessionId, out var turn)) actor.AbortTurn(turn);
            }
        }

        // The provider may call back into the actor during shutdown, so never await it in the mailbox.
        public async Task CloseCanceledWorkerAsync(ActiveTaskRunState active)
        {
            if (active.SessionId is not { } sessionId)
            {
                if (active.Execution is { } execution)
                    await execution.WaitAsync(actor._options.SessionStartupTimeout).ConfigureAwait(false);
                return;
            }
            try
            {
                await actor._runtimes.Get(sessionId).CloseAsync(actor._options.SessionStartupTimeout, CancellationToken.None).ConfigureAwait(false);
                // Provider shutdown can finish before the runner clears its active runtime work.
                var settled = await actor.EnqueueAsync(_ => Task.FromResult(
                    actor._scheduler.TryGetActive(sessionId, out var turn) ? turn.Settled.Task : Task.CompletedTask),
                    CancellationToken.None).ConfigureAwait(false);
                await settled.WaitAsync(actor._options.SessionStartupTimeout).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                throw new KernelException(KernelErrorCode.SessionRecoveryRequired,
                    $"Worker shutdown must be confirmed before releasing its workspace: {error.Message}");
            }
        }

        public async Task FinalizeCanceledTaskCoreAsync(ActiveTaskRunState active, CancellationToken ct)
        {
            if (!_activeTasks.TryGetValue(active.TaskId, out var current) || !ReferenceEquals(active, current))
                throw KernelException.Conflict("The task run already ended.");
            if (active.SessionId is { } id && (actor._scheduler.IsBusy(id) || actor._runtimes.Get(id).HasActiveWork || actor._runtimes.TryGetOpen(id, out _)))
                throw new KernelException(KernelErrorCode.SessionRecoveryRequired, "Worker shutdown has not been confirmed.");
            _activeTasks.Remove(active.TaskId);
            Volatile.Write(ref _activeTaskCount, _activeTasks.Count);
            active.Cts.Dispose();
            await InterruptSessionQueueForTaskSettlementCoreAsync(active.SessionId, ct).ConfigureAwait(false);
        }

        public async Task CancelAllInFlightTasksAsync(string reason, CancellationToken ct)
        {
            var (runs, watches) = await actor.EnqueueAsync(async _ =>
            {
                RequireTasksOutsideVerification(_activeTasks.Values);
                var activeRuns = _activeTasks.Values.ToArray();
                ct.ThrowIfCancellationRequested();
                var canceledWatches = new List<ActiveWatchState>();
                foreach (var watch in actor._activeWatches.Values.ToArray())
                {
                    var task = RequireTaskCore(watch.TaskId);
                    var now = actor.NowMs();
                    var canceled = task with
                    {
                        Status = TaskRunStatus.Canceled, Ready = false, StatusDetail = reason,
                        FinishedAtMs = now, UpdatedAtMs = now
                    };
                    await PersistAndEmitTaskUpdatedCoreAsync(canceled, null, now, CancellationToken.None, suppressDefaultWake: true)
                        .ConfigureAwait(false);
                    ReplaceTaskInMemoryCore(canceled);
                    canceledWatches.AddRange(actor.BeginWatchCancellationForNodeCore(watch.NodeId));
                }
                foreach (var active in activeRuns) BeginTaskCancellationCore(active);
                return (activeRuns, canceledWatches.ToArray());
            }, CancellationToken.None).ConfigureAwait(false);
            if (watches.Length > 0)
            {
                await actor.FinishFlowCancellationAsync([], watches).ConfigureAwait(false);
            }
            foreach (var active in runs)
            {
                await CloseCanceledWorkerAsync(active).ConfigureAwait(false);
                await actor.EnqueueAsync(async _ =>
                {
                    var now = actor.NowMs();
                    await FinalizeCanceledTaskCoreAsync(active, CancellationToken.None).ConfigureAwait(false);
                    var task = RequireTaskCore(active.TaskId);
                    var canceledTask = task with { Status = TaskRunStatus.Canceled, Ready = false, StatusDetail = reason, FinishedAtMs = now, UpdatedAtMs = now };
                    ReplaceTaskInMemoryCore(canceledTask);
                    await PersistAndEmitTaskUpdatedCoreAsync(canceledTask, nextTemplateState: null, now, CancellationToken.None).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }

        public TaskDto RequireTaskCore(string taskKeyOrId)
        {
            var task = _tasks.FirstOrDefault(t =>
                           string.Equals(t.TaskKey, taskKeyOrId, StringComparison.Ordinal) ||
                           string.Equals(t.Id, taskKeyOrId, StringComparison.Ordinal))
                       ?? throw KernelException.NotFound($"Task '{taskKeyOrId}' not found on track '{actor.TrackId}'.");
            return task;
        }

        public void ReplaceTaskInMemoryCore(TaskDto updated)
        {
            for (var i = 0; i < _tasks.Count; i++)
            {
                if (string.Equals(_tasks[i].Id, updated.Id, StringComparison.Ordinal))
                {
                    _tasks[i] = updated;
                    return;
                }
            }

            _tasks.Add(updated);
        }

        public EventEnvelopeDto CreateTaskUpdatedEventCore(long now) =>
            new(null, actor._track.AreaId, actor.TrackId, KernelEventKinds.TaskUpdated, JsonHelpers.EmptyObject, now);

        public async Task PersistAndEmitTaskUpdatedCoreAsync(
            TaskDto task,
            JsonElement? nextTemplateState,
            long now,
            CancellationToken ct, string? wakePrompt = null, bool suppressDefaultWake = false)
        {
            var evt = CreateTaskUpdatedEventCore(now);
            await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
            await actor._store.UpsertTaskAsync(task, nextTemplateState, evt, ct, wakePrompt: wakePrompt, suppressDefaultWake: suppressDefaultWake).ConfigureAwait(false);
            if (task.FinishedAtMs is not null && task.SessionId is { } childId && actor._sessions.TryGetValue(childId, out var child) &&
                child.NotifyParent == "task_settled" && child.ParentSessionId is { } parentId)
                await actor.PokeSessionCoreAsync(parentId).ConfigureAwait(false);
            if (nextTemplateState is { } state) actor._track = actor._track with { TemplateState = state };
            await actor._eventHub.PublishSyncedAfterStoreWriteAsync(actor._store, evt, ct).ConfigureAwait(false);
        }

        public sealed class ActiveTaskRunState(
            string taskId,
            int runCount,
            string? sessionId,
            CancellationTokenSource cts)
        {
            public string TaskId { get; } = taskId;
            public Task? Execution { get; set; }
            public int RunCount { get; } = runCount;
            public string? SessionId { get; } = sessionId;
            public CancellationTokenSource Cts { get; } = cts;
            public CancellationToken Token { get; } = cts.Token;
            public bool Paused { get; private set; }
            public void SetPaused(bool paused) => Paused = paused;
            public bool Settling { get; private set; }
            public void BeginSettlement() => Settling = true;
        }

    }
}
