using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static readonly TimeSpan ProviderStopGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionResumeTimeout = TimeSpan.FromSeconds(20);
    private readonly Dictionary<string, SessionResumeHold> _sessionResumeHolds = new(StringComparer.Ordinal);
    private int _sessionResumeHoldCount;

    private sealed class SessionResumeHold(string turnId, string nativeId)
    {
        public string TurnId { get; } = turnId;
        public string NativeId { get; } = nativeId;
        public bool Busy { get; set; }
        public bool Quiescent { get; set; }
    }

    private ProviderSessionResumeDto? ReadSessionResumeCore(AgentSessionDto session, ActiveTurnState? active, SessionInputReceipt? unknown)
    {
        if (_sessionResumeHolds.TryGetValue(session.Id, out var hold))
            return new(hold.TurnId, hold.Busy ? "resuming" : "paused",
                !hold.Busy && hold.Quiescent && unknown?.TurnId == hold.TurnId);
        if (session.BoundAttemptId is not null || session.BoundTaskId is not null ||
            session.ArchivedAtMs is not null || _track.ClosedAtMs is not null)
            return null;
        if (active?.AgentSession.SupportsSessionResume == true && !string.IsNullOrWhiteSpace(active.AgentSession.NativeThreadId ?? session.NativeThreadId))
            return new(active.TurnDto.Id, "available");
        if (unknown is not null && !string.IsNullOrWhiteSpace(session.NativeThreadId) &&
            _openAgentSessions.TryGetValue(session.Id, out var agent) && agent.SupportsSessionResume && agent.SupportsScopedClose)
            return new(unknown.TurnId, "available");
        return null;
    }

    private void RequireSessionDispatchAvailableCore(string sessionId)
    {
        if (_sessionResumeHolds.ContainsKey(sessionId))
            throw KernelException.PreconditionFailed("Resume the provider session before sending more input.");
    }

    private void ReleaseSessionResumeCore(string sessionId)
    {
        if (_sessionResumeHolds.Remove(sessionId))
            Interlocked.Decrement(ref _sessionResumeHoldCount);
    }

    private void RestoreSessionResumeFencesCore(IEnumerable<ProviderSessionResumeFence> fences)
    {
        foreach (var fence in fences)
        {
            if (_sessionResumeHolds.TryAdd(fence.SessionId, new(fence.TurnId, fence.NativeId) { Quiescent = fence.Quiescent }))
                Interlocked.Increment(ref _sessionResumeHoldCount);
        }
    }

    private async Task<SessionResumeHold> CreateSessionResumeHoldCoreAsync(
        string sessionId, string turnId, string nativeId, CancellationToken ct)
    {
        var hold = new SessionResumeHold(turnId, nativeId);
        await _store.SaveSessionResumeFenceAsync(new(sessionId, turnId, nativeId, false), ct).ConfigureAwait(false);
        _sessionResumeHolds.Add(sessionId, hold);
        Interlocked.Increment(ref _sessionResumeHoldCount);
        return hold;
    }

    private async Task MarkSessionResumeQuiescentCoreAsync(string sessionId, SessionResumeHold hold, CancellationToken ct)
    {
        if (hold.Quiescent)
            return;
        await _store.SaveSessionResumeFenceAsync(new(sessionId, hold.TurnId, hold.NativeId, true), ct).ConfigureAwait(false);
        hold.Quiescent = true;
    }

    internal async Task<AgentSessionDto> ResumeProviderSessionAsync(string sessionId, string turnId, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _actorShutdownCts.Token);
        deadline.CancelAfter(SessionResumeTimeout + ProviderStopGrace);
        try
        {
            return await AcceptSessionResumeAsync(sessionId, turnId, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_actorShutdownCts.IsCancellationRequested)
        {
            throw KernelException.PreconditionFailed("Provider recovery timed out. Check the current recovery status before retrying.");
        }
    }

    private async Task<AgentSessionDto> AcceptSessionResumeAsync(string sessionId, string turnId, CancellationToken ct)
    {
        var result = await EnqueueAsync<Task<AgentSessionDto>>(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireResumeWorkspaceCore(session);
            if (_compactingSessions.Contains(sessionId) || _reservedWakeSessions.ContainsKey(sessionId) ||
                _steering.ContainsKey(sessionId))
                throw KernelException.Conflict("Wait for the current session operation before resuming.");

            _sessionResumeHolds.TryGetValue(sessionId, out var hold);
            if (hold is { Busy: true } || (hold is not null && hold.TurnId != turnId))
                throw KernelException.Conflict("The session already has a recovery for a different or pending operation.");

            _activeTurns.TryGetValue(sessionId, out var active);
            _openAgentSessions.TryGetValue(sessionId, out var old);
            if (active is not null && active.TurnDto.Id != turnId)
                throw KernelException.Conflict("The observed provider turn is no longer current.");

            if (hold is null)
            {
                var unknown = active is null && session.Status == "unknown"
                    ? await _store.SessionInputs.FindUnresolvedAsync(sessionId, ct).ConfigureAwait(false) : null;
                if (active is null && (unknown?.Receipt.TurnId != turnId || old?.SupportsScopedClose != true))
                    throw KernelException.PreconditionFailed("Perga cannot confirm that the previous provider execution has stopped.");
                if ((active?.AgentSession ?? old)?.SupportsSessionResume != true)
                    throw KernelException.PreconditionFailed("This provider cannot confirm that it restored the same session.");
                var nativeId = active?.AgentSession.NativeThreadId ?? session.NativeThreadId;
                if (string.IsNullOrWhiteSpace(nativeId))
                    throw KernelException.PreconditionFailed("The provider has not returned a session ID to resume.");
                hold = await CreateSessionResumeHoldCoreAsync(sessionId, turnId, nativeId, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(hold.NativeId))
                throw KernelException.PreconditionFailed("The provider has not returned a session ID to resume.");

            hold.Busy = true;
            NotifyQueueChangedCore(sessionId);
            var completion = new TaskCompletionSource<AgentSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            var quiescent = hold.Quiescent;
            _ = actor.TrackBackgroundJob(async () =>
            {
                try
                {
                    completion.TrySetResult(await RunSessionResumeAsync(sessionId, hold, active, old, quiescent).ConfigureAwait(false));
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            });
            return completion.Task;
        }, ct).ConfigureAwait(false);
        return await result.WaitAsync(ct).ConfigureAwait(false);
    }

    private void RequireResumeWorkspaceCore(AgentSessionDto session)
    {
        RequireWorkspaceReadyCore();
        if (session.BoundAttemptId is not null || session.BoundTaskId is not null)
            throw KernelException.InvalidArgument("Use the Worker attempt controls to continue a Worker session.");
        RequireDispatchWorkspace(session);
        if (_track.ClosedAtMs is not null || session.ArchivedAtMs is not null)
            throw KernelException.PreconditionFailed("Reopen this conversation before resuming its provider session.");
    }

    private async Task<AgentSessionDto> RunSessionResumeAsync(
        string sessionId, SessionResumeHold hold, ActiveTurnState? active, IAgentSession? old, bool quiescent)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        deadline.CancelAfter(SessionResumeTimeout);
        var ct = deadline.Token;
        IAgentSession? replacement = null;
        var success = false;
        var rebinding = false;
        try
        {
            if (!quiescent && active is not null)
                quiescent = await TryObserveProviderStopAsync(active, ct).ConfigureAwait(false);
            if (!quiescent && old?.SupportsScopedClose == true)
                quiescent = await old.TryCloseSessionAsync(ct).WaitAsync(ct).ConfigureAwait(false);
            if (!quiescent)
                throw KernelException.PreconditionFailed("The previous provider execution could not be confirmed stopped. The session remains paused.");

            // A scoped close may complete without a terminal event on the old stream.
            // Stop observing it and let its existing pipeline settle before rebinding.
            await EnqueueAsync(async _ =>
            {
                await MarkSessionResumeQuiescentCoreAsync(sessionId, hold, CancellationToken.None).ConfigureAwait(false);
                if (active is not null && _activeTurns.TryGetValue(sessionId, out var current) && ReferenceEquals(current, active))
                {
                    active.Interrupted = true;
                    active.TurnCts.Cancel();
                }
            }, ct).ConfigureAwait(false);
            if (active is not null)
                await active.Settled.Task.WaitAsync(ct).ConfigureAwait(false);

            var spec = await EnqueueAsync(async _ =>
            {
                var session = RequireSessionCore(sessionId);
                RequireResumeWorkspaceCore(session);
                if (_activeTurns.ContainsKey(sessionId))
                    throw KernelException.Conflict("The previous provider turn has not settled.");
                if (session.Status == "running")
                    throw KernelException.PreconditionFailed("The previous turn outcome could not be recorded.");
                _openAgentSessions.Remove(sessionId);
                rebinding = true;
                _mcpBridge.RevokeSession(sessionId);
                return await BuildAgentLaunchSpecCoreAsync(session with { NativeThreadId = hold.NativeId }, null, ct)
                    .ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (old is not null)
                await old.DisposeAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
            try
            {
                replacement = await _agentRuntime.OpenSessionAsync(spec, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not (OperationCanceledException or NotSupportedException))
            {
                throw new KernelException(KernelErrorCode.PreconditionFailed,
                    "The provider could not reconnect to the original session.", innerException: error);
            }

            ct.ThrowIfCancellationRequested();
            if (!replacement.SupportsSessionResume || replacement.NativeThreadId != hold.NativeId)
                throw KernelException.PreconditionFailed("The provider did not restore the original session.");

            var restored = await EnqueueAsync(async _ =>
            {
                var session = RequireSessionCore(sessionId);
                RequireResumeWorkspaceCore(session);
                var updated = session with { Status = "idle", NativeThreadId = hold.NativeId, UpdatedAtMs = NowMs() };
                var unknown = session.Status == "unknown"
                    ? await _store.SessionInputs.FindUnresolvedAsync(sessionId, CancellationToken.None).ConfigureAwait(false)
                    : null;
                if (session.Status == "unknown" && unknown?.Receipt.TurnId != hold.TurnId)
                    throw KernelException.Conflict("The unsettled input no longer matches this recovery.");
                var evt = SessionUpdatedEvent(updated);
                await _eventHub.EnsureInitializedFromStoreAsync(_store, CancellationToken.None).ConfigureAwait(false);
                await _store.CompleteSessionResumeAsync(updated, unknown, evt, CancellationToken.None).ConfigureAwait(false);

                _sessions[sessionId] = updated;
                _openAgentSessions[sessionId] = replacement;
                return updated;
            }, CancellationToken.None).ConfigureAwait(false);
            success = true;
            return restored;
        }
        catch (NotSupportedException)
        {
            throw KernelException.PreconditionFailed("The provider does not support restoring this session.");
        }
        catch (OperationCanceledException) when (!_actorShutdownCts.IsCancellationRequested)
        {
            throw KernelException.PreconditionFailed("Provider recovery timed out. The session remains paused.");
        }
        finally
        {
            try
            {
                if (!success && replacement is not null)
                    await replacement.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (!_actorShutdownCts.IsCancellationRequested)
                    await EnqueueAsync(_ =>
                    {
                        hold.Busy = false;
                        if (success)
                        {
                            ReleaseSessionResumeCore(sessionId);
                            SchedulePendingWakeCore(sessionId);
                        }
                        else if (rebinding)
                            _mcpBridge.RevokeSession(sessionId);
                        NotifyQueueChangedCore(sessionId);
                        return Task.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> TryObserveProviderStopAsync(ActiveTurnState active, CancellationToken ct)
    {
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
        grace.CancelAfter(ProviderStopGrace);
        try
        {
            await active.AgentSession.CancelTurnAsync(grace.Token).WaitAsync(grace.Token).ConfigureAwait(false);
            return await active.Settled.Task.WaitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return active.Settled.Task is { IsCompletedSuccessfully: true, Result: true };
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            return active.Settled.Task is { IsCompletedSuccessfully: true, Result: true };
        }
    }
}
