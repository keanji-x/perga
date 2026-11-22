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

    private void RegisterRuntimeActivityCore(AgentSessionDto session, IAgentSession provider)
    {
        if (!_runtimes.TryGetOpen(session.Id, out var open)) return;
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
            ApplyRuntimeActivityCore(session.Id, session.Epoch, provider, state, observation);
    }

    private async Task EnqueueRuntimeActivityAsync(string sessionId, int epoch, IAgentSession provider,
        LiveRuntimeActivity state, UnifiedAgentEvent observation)
    {
        try
        {
            await EnqueueAsync(_ =>
            {
                ApplyRuntimeActivityCore(sessionId, epoch, provider, state, observation);
                return Task.FromResult(true);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) when (_actorShutdownCts.IsCancellationRequested) { }
    }

    private void ApplyRuntimeActivityCore(string sessionId, int epoch, IAgentSession provider,
        LiveRuntimeActivity state, UnifiedAgentEvent observation)
    {
        if (_disposed || _track.ClosedAtMs is not null || !_sessions.TryGetValue(sessionId, out var session) ||
            session.Epoch != epoch || session.Status == "unknown" ||
            !_runtimes.TryGetOpen(sessionId, out var open) || !ReferenceEquals(open.Agent, provider) ||
            !ReferenceEquals(state, open.RuntimeActivity) || state.Exited) return;
        var now = NowMs();
        if (observation is UnifiedAgentEvent.RuntimeExited)
        {
            state.Exited = true;
            state.Status = null;
            state.Tasks.Clear();
            state.EndedOrder.Clear();
            _eventHub.PublishTransient(_track.AreaId, TrackId, KernelEventKinds.SessionRuntimeStatus,
                JsonHelpers.ToElement(new { sessionId, status = (AgentRuntimeStatusDto?)null }), now);
            return;
        }
        if (observation is UnifiedAgentEvent.AgentStatusChanged changed)
        {
            var status = changed.Status;
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
}
