using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private LiveRuntimeActivity? ReadRuntimeActivityCore(AgentSessionDto session) =>
        _runtimes.TryGetOpen(session.Id, out var open) && open.RuntimeActivity.Epoch == session.Epoch &&
        session.Status != "unknown" && session.ArchivedAtMs is null ? open.RuntimeActivity : null;

    private async Task RegisterRuntimeActivityCoreAsync(AgentSessionDto session, IAgentSession provider)
    {
        if (!_runtimes.TryGetOpen(session.Id, out var open) || !ReferenceEquals(open.Agent, provider) ||
            !HasRuntimeCredential(session.Id, open))
            throw KernelException.Conflict("The runtime binding changed during provider startup.");
        lock (open.RuntimeControl.Sync)
        {
            if (open.RuntimeControl.Phase == ExecutionPhase.Ended)
                throw KernelException.Conflict("The runtime binding was revoked during provider startup.");
            open.RuntimeControl.Phase = ExecutionPhase.Running;
        }
        var state = open.RuntimeActivity;
        if (state.Epoch != session.Epoch)
        {
            state.Status = null;
            state.Tasks.Clear();
            state.EndedOrder.Clear();
            state.Epoch = session.Epoch;
        }
        var pending = provider.AttachRuntimeActivityHandler(observation =>
        {
            if (_disposed || _actorShutdownCts.IsCancellationRequested) return;
            _ = EnqueueRuntimeActivityAsync(session.Id, session.Epoch, provider, state, observation);
        });
        foreach (var observation in pending)
            await ApplyRuntimeActivityCoreAsync(session.Id, session.Epoch, provider, state, observation).ConfigureAwait(false);
    }

    private async Task EnqueueRuntimeActivityAsync(string sessionId, int epoch, IAgentSession provider,
        LiveRuntimeActivity state, UnifiedAgentEvent observation)
    {
        await EnqueueRuntimeObservationAsync(() =>
            ApplyRuntimeActivityCoreAsync(sessionId, epoch, provider, state, observation)).ConfigureAwait(false);
    }

    private async Task EnqueueRuntimeObservationAsync(Func<Task> apply)
    {
        try { await EnqueueAsync(_ => apply(), _actorShutdownCts.Token).ConfigureAwait(false); }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (OperationCanceledException) when (_actorShutdownCts.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError("Runtime activity for {0} could not be stored: {1}", TrackId, ex); }
    }

    private async Task ApplyRuntimeActivityCoreAsync(string sessionId, int epoch, IAgentSession provider,
        LiveRuntimeActivity state, UnifiedAgentEvent observation)
    {
        if (_disposed || !_sessions.TryGetValue(sessionId, out var session) || session.Epoch != epoch ||
            !_runtimes.TryGetOpen(sessionId, out var open) || !ReferenceEquals(open.Agent, provider) ||
            !ReferenceEquals(state, open.RuntimeActivity) || state.Exited) return;
        var now = NowMs();
        if (observation is UnifiedAgentEvent.RuntimeExited)
        {
            state.Exited = true;
            EndRuntimeBinding(sessionId, open);
            state.Status = null;
            state.Tasks.Clear();
            state.EndedOrder.Clear();
            _eventHub.PublishTransient(_track.AreaId, TrackId, KernelEventKinds.SessionRuntimeStatus,
                JsonHelpers.ToElement(new { sessionId, status = (AgentRuntimeStatusDto?)null }), now);
            await FlushRuntimeOutputCoreAsync(session, state).ConfigureAwait(false);
            return;
        }
        if (_track.ClosedAtMs is not null || session.Status == "unknown" || session.ArchivedAtMs is not null) return;
        lock (open.RuntimeControl.Sync)
            if (open.RuntimeControl.Phase != ExecutionPhase.Running) return;
        _runtimes.Get(sessionId).TouchUsed();
        if (observation is UnifiedAgentEvent.AgentStatusChanged changed)
        {
            var status = changed.Status;
            if (status.IdleActivity is { } idle && state.Status?.IdleActivity?.LastEventAt != idle.LastEventAt)
                state.LastIdleActivityTimestamp = _timeProvider.GetTimestamp();
            state.Status = new(status.Kind, status.Phase, status.ToolName, status.ToolCallId, status.Writing,
                status.SinceSeq, status.RequestId, status.InputId, status.Released,
                status.LastTurnOutcome is { } outcome ? new(outcome.Kind, outcome.Failure, outcome.Reason,
                    outcome.Status, outcome.Credential, outcome.ResetsAt, outcome.RateLimitType, outcome.StructuredOutput) : null,
                status.IdleActivity is { } activity ? new(activity.Since, activity.LastEventAt) : null,
                status.PendingPrompt is { } pending ? new(pending.InputId, pending.SinceSeq, pending.RequestId,
                    pending.Stop is { } pendingStop ? new(pendingStop.PendingAbortIds, pendingStop.AbortedOnExit) : null) : null,
                status.Stop is { } stop ? new(stop.PendingAbortIds, stop.AbortedOnExit) : null,
                status.Compaction is { } compact ? new(compact.RequestId, compact.Started) : null);
            if (status.Released == true)
            {
                state.Tasks.Clear();
                state.EndedOrder.Clear();
            }
            _eventHub.PublishTransient(_track.AreaId, TrackId, KernelEventKinds.SessionRuntimeStatus,
                JsonHelpers.ToElement(new { sessionId, status = state.Status }), now);
            return;
        }
        // The provider sends output here only when it has no prompt stream to receive it.
        // Its stream may have ended before the actor settles the corresponding durable turn.
        switch (observation)
        {
            case UnifiedAgentEvent.TextDelta text:
                await BufferRuntimeOutputCoreAsync(session, state, "assistant_text", text.Delta).ConfigureAwait(false);
                return;
            case UnifiedAgentEvent.ThinkingDelta thinking:
                await BufferRuntimeOutputCoreAsync(session, state, "thinking", thinking.Delta).ConfigureAwait(false);
                return;
            case UnifiedAgentEvent.ToolCallStarted tool:
                await FlushRuntimeOutputCoreAsync(session, state, new(0, sessionId, null, "tool_call",
                    JsonHelpers.ToElement(new { v = 1, callId = tool.CallId, name = tool.Name, args = tool.Args }), now)).ConfigureAwait(false);
                return;
            case UnifiedAgentEvent.ToolCallCompleted tool:
                await FlushRuntimeOutputCoreAsync(session, state, new(0, sessionId, null, "tool_result",
                    JsonHelpers.ToElement(new { v = 1, callId = tool.CallId, output = tool.Output, isError = tool.IsError }), now)).ConfigureAwait(false);
                return;
        }

        var task = observation switch
        {
            UnifiedAgentEvent.TaskStarted started => started.Task,
            UnifiedAgentEvent.TaskUpdated updated => updated.Task,
            UnifiedAgentEvent.TaskEnded ended => ended.Task,
            _ => null
        };
        if (task is null) return;
        state.Tasks.TryGetValue(task.TaskId, out var previous);
        var reopened = previous?.Status is "completed" or "failed" or "stopped" &&
            (observation is UnifiedAgentEvent.TaskStarted || task.Status is "running" or "pending" or "paused");
        var next = new AgentRuntimeTaskDto(task.TaskId, task.TaskType ?? previous?.TaskType,
            task.NativeType ?? previous?.NativeType, task.Description ?? previous?.Description,
            task.ToolCallId ?? previous?.ToolCallId, task.ChildSessionId ?? previous?.ChildSessionId,
            task.Background ?? previous?.Background, task.Ambient ?? previous?.Ambient,
            task.Status ?? (observation is UnifiedAgentEvent.TaskStarted ? "running" : previous?.Status),
            task.Error ?? (reopened ? null : previous?.Error), task.Summary ?? (reopened ? null : previous?.Summary),
            task.OutputFile ?? (reopened ? null : previous?.OutputFile), task.Seq ?? previous?.Seq, task.Ts ?? previous?.Ts);
        state.Tasks.Remove(task.TaskId);
        state.Tasks[task.TaskId] = next;
        if (next.Status is "completed" or "failed" or "stopped")
            state.EndedOrder[task.TaskId] = ++state.NextTaskSequence;
        else state.EndedOrder.Remove(task.TaskId);
        while (state.EndedOrder.Count > 64)
        {
            var expired = state.EndedOrder.MinBy(entry => entry.Value).Key;
            state.EndedOrder.Remove(expired);
            state.Tasks.Remove(expired);
        }
        var payload = JsonHelpers.ToElement(new { sessionId, task = next });
        if (_scheduler.TryGetActive(sessionId, out var active))
            PublishTurnActivity(active, KernelEventKinds.SessionTaskProgress, payload, now);
        else _eventHub.PublishTransient(_track.AreaId, TrackId, KernelEventKinds.SessionTaskProgress, payload, now);
    }

    private bool HasRecentBackgroundActivityCore(string sessionId) =>
        _runtimes.TryGetOpen(sessionId, out var open) &&
        (open.RuntimeActivity.Tasks.Values.Any(task => task.Status is "running" or "pending") ||
         open.RuntimeActivity.LastIdleActivityTimestamp is { } last && _timeProvider.GetElapsedTime(last) < TimeSpan.FromSeconds(5));

    private static readonly TimeSpan RuntimeOutputQuietPeriod = TimeSpan.FromSeconds(1);

    private async Task BufferRuntimeOutputCoreAsync(AgentSessionDto session, LiveRuntimeActivity state, string kind, string delta)
    {
        if (delta.Length == 0) return;
        if (state.OutputKind != kind)
            await FlushRuntimeOutputCoreAsync(session, state).ConfigureAwait(false);
        state.OutputKind = kind;
        state.Output.Append(delta);
        state.LastOutputTimestamp = _timeProvider.GetTimestamp();
        state.OutputTimer ??= _timeProvider.CreateTimer(_state =>
        {
            if (!_disposed) _ = EnqueueRuntimeObservationAsync(() => FlushQuietRuntimeOutputCoreAsync(session.Id, state));
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        state.OutputTimer.Change(RuntimeOutputQuietPeriod, Timeout.InfiniteTimeSpan);
    }

    private Task FlushQuietRuntimeOutputCoreAsync(string sessionId, LiveRuntimeActivity state)
    {
        if (_disposed || state.OutputTimer is null || !_runtimes.TryGetOpen(sessionId, out var open) ||
            !ReferenceEquals(open.RuntimeActivity, state) ||
            !_sessions.TryGetValue(sessionId, out var session) || session.Epoch != state.Epoch)
            return Task.CompletedTask;

        var remaining = RuntimeOutputQuietPeriod - _timeProvider.GetElapsedTime(state.LastOutputTimestamp);
        if (remaining <= TimeSpan.Zero) return FlushRuntimeOutputCoreAsync(session, state);
        state.OutputTimer.Change(remaining, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    private Task FlushRuntimeOutputCoreAsync(AgentSessionDto session) =>
        _runtimes.TryGetOpen(session.Id, out var open) ? FlushRuntimeOutputCoreAsync(session, open.RuntimeActivity) : Task.CompletedTask;

    private async Task FlushRuntimeOutputCoreAsync(AgentSessionDto session, LiveRuntimeActivity state, SessionItemDto? tool = null)
    {
        var items = new List<SessionItemDto>(2);
        if (state.OutputKind is { } kind && state.Output.Length > 0)
            items.Add(new(0, session.Id, null, kind, JsonHelpers.ToElement(new { v = 1, text = state.Output.ToString() }), NowMs()));
        if (tool is not null) items.Add(tool);
        if (items.Count == 0) return;

        var evt = SessionUpdatedEvent(session) with { CreatedAtMs = NowMs() };
        await _eventHub.EnsureInitializedFromStoreAsync(_store, CancellationToken.None).ConfigureAwait(false);
        await _store.AppendSessionItemsAsync(session.Id, items, CancellationToken.None, evt).ConfigureAwait(false);
        state.Output.Clear();
        state.OutputKind = null;
        state.OutputTimer?.Dispose();
        state.OutputTimer = null;
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
    }

    private void RevokeSessionRuntime(string sessionId)
    {
        if (_runtimes.TryGetOpen(sessionId, out var open)) EndRuntimeBinding(sessionId, open);
        // A launch may have registered its credentials before creating the provider handle.
        else _mcpBridge.RevokeSession(sessionId);
    }

    private void EndRuntimeBinding(string sessionId, OpenSessionHandle open)
    {
        _mcpBridge.RevokeSession(sessionId, open.RuntimeBindingId);
        List<PendingApproval>? expired;
        lock (open.RuntimeControl.Sync)
        {
            open.RuntimeControl.Phase = ExecutionPhase.Ended;
            expired = CollectExpiredApprovalsLocked(open.RuntimeControl);
        }
        FinishExpiredApprovals(expired);
        _interactions.ExpireRuntimeWaiters(open.RuntimeControl);
    }

    // Close admission is serialized with credential checks, callbacks, and writes.
    private async Task<bool> PrepareRuntimeCloseAsync(string sessionId, OpenSessionHandle open, Func<bool>? shouldClose)
    {
        async Task<bool> PrepareAsync()
        {
            if (shouldClose is not null && !shouldClose()) return false;
            EndRuntimeBinding(sessionId, open);
            if (_sessions.TryGetValue(sessionId, out var session))
                await FlushRuntimeOutputCoreAsync(session, open.RuntimeActivity).ConfigureAwait(false);
            return true;
        }

        if (!_disposed)
        {
            try { return await EnqueueAsync(_ => PrepareAsync(), _actorShutdownCts.Token, touchActivity: false).ConfigureAwait(false); }
            catch (ObjectDisposedException) when (_disposed) { }
            catch (OperationCanceledException) when (_disposed) { }
        }
        await _mailboxLoopTask.ConfigureAwait(false);
        try { return await PrepareAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Runtime {0} output could not be flushed at shutdown: {1}", open.RuntimeBindingId, ex);
            open.RuntimeActivity.OutputTimer?.Dispose();
            return true;
        }
    }
}
