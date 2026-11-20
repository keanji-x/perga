using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private int _sessionResumeHoldCount;
    // Registered by the mailbox; shutdown joins these after the mailbox stops producing work.
    private readonly List<Task> _sessionRecoveryJobs = [];
    private readonly HashSet<string> _recoveringSessions = new(StringComparer.Ordinal);

    private sealed class SessionResumeHold(string turnId, string nativeId)
    {
        public string TurnId { get; } = turnId;
        public string NativeId { get; } = nativeId;
        public bool Quiescent { get; set; }
    }

    private SessionUnknownDetailDto? ReadUnknownDetailCore(AgentSessionDto session)
    {
        if (session.Status != "unknown") return null;
        var reason = session.UnknownReason ?? "stream_lost";
        var detail = reason switch
        {
            "session_lost" => "The original provider session no longer exists. Reset context to continue.",
            "stop_timeout" => "The provider did not confirm that the turn stopped.",
            "host_restarted" => "The host restarted before the provider outcome was confirmed.",
            _ => "The provider stream ended without a confirmed outcome."
        };
        if (string.IsNullOrWhiteSpace(session.NativeThreadId)) detail += " No original provider session ID is available.";
        return new(reason, detail, CanReconnectProviderCore(session), !IsWorkerSession(session),
            _recoveringSessions.Contains(session.Id), session.UnknownLastError);
    }

    private SessionProviderCleanupDto? ReadProviderCleanupCore(AgentSessionDto session)
    {
        if (session.Status != "idle" || IsWorkerSession(session)) return null;
        var recovering = _recoveringSessions.Contains(session.Id);
        if (!recovering && !HasOutstandingProviderOwnershipCore(session.Id)) return null;
        return new(CanReconnectProviderCore(session), recovering);
    }

    private static bool IsWorkerSession(AgentSessionDto session) =>
        session.BoundTaskId is not null || session.BoundAttemptId is not null;

    private bool CanReconnectProviderCore(AgentSessionDto session)
    {
        if (IsWorkerSession(session) || session.ReconnectInvalidated || string.IsNullOrWhiteSpace(session.NativeThreadId)) return false;
        return !_recoveringSessions.Contains(session.Id) && _openSessionHandles.TryGetValue(session.Id, out var handle)
            ? handle.Agent.SupportsSessionResume : _agentRuntime.CanResumeSession(session.Provider);
    }

    private bool HasOutstandingProviderOwnershipCore(string sessionId) =>
        _sessionShutdownPending.ContainsKey(sessionId) || _pendingSessionLaunches.ContainsKey(sessionId);

    private void RequireSessionDispatchAvailableCore(string sessionId)
    {
        if (HasOutstandingProviderOwnershipCore(sessionId) || _recoveringSessions.Contains(sessionId) ||
            _scheduler.HasResumeHold(sessionId) || RequireSessionCore(sessionId).Status == "unknown")
            throw KernelException.SessionRecoveryRequired("Recover the provider session before sending more input.");
    }

    private void ReleaseSessionResumeCore(string sessionId)
    {
        if (_scheduler.ReleaseResumeHold(sessionId)) Interlocked.Decrement(ref _sessionResumeHoldCount);
    }

    private void RestoreSessionResumeFencesCore(IEnumerable<ProviderSessionResumeFence> fences)
    {
        foreach (var fence in fences)
            if (_scheduler.TryHoldResume(fence.SessionId, new(fence.TurnId, fence.NativeId) { Quiescent = true }))
                Interlocked.Increment(ref _sessionResumeHoldCount);
    }

    private async Task<SessionResumeHold> CreateSessionResumeHoldCoreAsync(string sessionId, string turnId, string nativeId, CancellationToken ct)
    {
        var hold = new SessionResumeHold(turnId, nativeId);
        await _store.SaveSessionResumeFenceAsync(new(sessionId, turnId, nativeId, false), ct).ConfigureAwait(false);
        _scheduler.HoldResume(sessionId, hold);
        Interlocked.Increment(ref _sessionResumeHoldCount);
        return hold;
    }

    private Task MarkSessionResumeQuiescentCoreAsync(string sessionId, SessionResumeHold hold, CancellationToken ct)
    {
        hold.Quiescent = true;
        return Task.CompletedTask;
    }

    internal async Task<AgentSessionDto> RecoverSessionAsync(string sessionId, RecoverSessionRequest request, CancellationToken ct)
    {
        if (request.Mode is not ("reconnect" or "reset_context"))
            throw KernelException.InvalidArgument("Recovery mode must be reconnect or reset_context.");
        var operation = await EnqueueAsync(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireResumeWorkspaceCore(session);
            if (_scheduler.HasActive(sessionId) || _scheduler.IsCompacting(sessionId) ||
                _scheduler.HasReservation(sessionId) || _scheduler.IsSteering(sessionId))
                throw KernelException.Conflict("Wait for the current session operation before recovering.");
            if (_recoveringSessions.Contains(sessionId))
                throw KernelException.SessionRecoveryRequired("Session recovery is already in progress.");
            var cleanupPending = HasOutstandingProviderOwnershipCore(sessionId);
            if (session.Status != "unknown" && !(session.Status == "idle" && (cleanupPending || request.Mode == "reset_context")))
                throw KernelException.Conflict("Recovery is unavailable in this session state.");
            if (request.Mode == "reconnect" && !CanReconnectProviderCore(session))
            {
                if (session.Status == "unknown" && string.IsNullOrWhiteSpace(session.NativeThreadId))
                    await PersistAndEmitSessionUpdatedCoreAsync(session with { ReconnectInvalidated = true,
                        UnknownLastError = "No original provider session ID is available.", UpdatedAtMs = NowMs() }, NowMs(), CancellationToken.None).ConfigureAwait(false);
                throw KernelException.SessionRecoveryRequired("The original provider session cannot be reconnected.");
            }

            var originalProviderId = _scheduler.TryGetResumeHold(sessionId, out var resume) ? resume.NativeId : session.NativeThreadId;
            _recoveringSessions.Add(sessionId);
            var completion = new TaskCompletionSource<AgentSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            _sessionRecoveryJobs.RemoveAll(job => job.IsCompleted);
            _sessionRecoveryJobs.Add(actor.TrackBackgroundJob(async () =>
            {
                try { completion.TrySetResult(await RunSessionRecoveryAsync(sessionId, request.Mode, originalProviderId, session.Epoch).ConfigureAwait(false)); }
                catch (Exception error) { completion.TrySetException(error); }
            }));
            return completion.Task;
        }, ct).ConfigureAwait(false);
        return await operation.WaitAsync(ct).ConfigureAwait(false);
    }

    private void RequireResumeWorkspaceCore(AgentSessionDto session)
    {
        RequireWorkspaceReadyCore();
        if (session.BoundAttemptId is not null || session.BoundTaskId is not null)
            throw KernelException.InvalidArgument("Use the Worker attempt controls to continue a Worker session.");
        RequireDispatchWorkspace(session);
        if (_track.ClosedAtMs is not null || session.ArchivedAtMs is not null)
            throw KernelException.PreconditionFailed("Reopen this conversation before recovering its provider session.");
    }

    private async Task<AgentSessionDto> RunSessionRecoveryAsync(string sessionId, string mode, string? originalProviderId, int opEpoch)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        deadline.CancelAfter(KernelOptions.SessionResumeTimeout);
        var ct = deadline.Token;
        IAgentSession? replacement = null;
        var success = false;
        try
        {
            var pending = await EnqueueAsync(_ => Task.FromResult(
                _pendingSessionLaunches.GetValueOrDefault(sessionId)), ct).ConfigureAwait(false);
            if (pending is not null)
            {
                if (await ReadPendingLaunchAgentAsync(pending.Launch, ct).ConfigureAwait(false) is { } late)
                {
                    // Join the original cleanup before deciding whether another disposal is needed.
                    try { await pending.Cleanup.WaitAsync(ct).ConfigureAwait(false); }
                    catch (Exception) when (pending.Cleanup.IsCompleted && !ct.IsCancellationRequested) { }
                    if (!pending.Cleanup.IsCompletedSuccessfully || !late.ShutdownConfirmed)
                        await ConfirmProviderShutdownAsync(sessionId, late, ct).ConfigureAwait(false);
                }
                await EnqueueAsync(_ => { _pendingSessionLaunches.TryRemove(sessionId, out var removedLaunch); return Task.CompletedTask; }, ct).ConfigureAwait(false);
            }
            var old = await EnqueueAsync(_ => Task.FromResult(
                _openSessionHandles.TryGetValue(sessionId, out var handle) ? handle.Agent : null), ct).ConfigureAwait(false);
            // Adapter disposal kills the entire owned subprocess tree. Never clear identity before confirmation.
            if (old is not null)
            {
                if (mode == "reconnect" && old.SupportsScopedClose)
                    await old.TryCloseSessionAsync(ct).WaitAsync(ct).ConfigureAwait(false);
                await ConfirmProviderShutdownAsync(sessionId, old, ct).ConfigureAwait(false);
            }
            var spec = await EnqueueAsync(async _ =>
            {
                var session = RequireSessionCore(sessionId);
                RequireResumeWorkspaceCore(session);
                if (session.Epoch != opEpoch) throw KernelException.Conflict("Session epoch changed during recovery.");
                _openSessionHandles.Remove(sessionId);
                _sessionShutdownPending.TryRemove(sessionId, out var removedShutdown);
                _mcpBridge.RevokeSession(sessionId);
                return mode == "reconnect" ? await BuildAgentLaunchSpecCoreAsync(session, null, ct).ConfigureAwait(false) : null;
            }, ct).ConfigureAwait(false);
            if (spec is not null)
            {
                using var launch = await _options.DispatchAdmission.EnterAsync(ct).ConfigureAwait(false);
                replacement = await OpenAgentSessionBoundedAsync(sessionId, spec, ct).ConfigureAwait(false);
                // Retain ownership even if validation or cleanup fails, so a later reset can stop this process.
                await EnqueueAsync(_ =>
                {
                    _openSessionHandles[sessionId] = new(replacement, spec, SessionConfiguration.FromLaunch(spec), replacement.ConfigurationState);
                    _pendingSessionLaunches.TryRemove(sessionId, out var transferredLaunch);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
                if (!replacement.SupportsSessionResume || replacement.NativeThreadId != originalProviderId)
                    throw new ProviderSessionNotFoundException("The provider did not restore the original session.");
                var launched = await EnqueueAsync(_ => Task.FromResult(RequireSessionCore(sessionId) with
                    { Model = spec.Model, ReasoningEffort = spec.ReasoningEffort, PermissionMode = spec.PermissionMode }), ct).ConfigureAwait(false);
                RequireConfirmedConfiguration(launched, replacement, replacement.ConfigurationState);
            }
            var restored = await EnqueueAsync(async _ =>
            {
                var session = RequireSessionCore(sessionId);
                RequireResumeWorkspaceCore(session);
                if (session.Epoch != opEpoch) throw KernelException.Conflict("Session epoch changed during recovery.");
                var now = NowMs();
                var updated = session with { Status = "idle", UnknownReason = null, UnknownTurnId = null,
                    UnknownLastError = null, ReconnectInvalidated = false, UpdatedAtMs = now,
                    Epoch = mode == "reset_context" ? checked(session.Epoch + 1) : session.Epoch,
                    NativeThreadId = mode == "reset_context" ? null : session.NativeThreadId,
                    ContextUsage = mode == "reset_context" ? null : session.ContextUsage };
                var unknown = session.Status == "unknown"
                    ? await _store.SessionInputs.FindUnresolvedAsync(sessionId, CancellationToken.None).ConfigureAwait(false) : null;
                SessionItemDto? boundary = mode == "reset_context" ? new(0, sessionId, null, "epoch_boundary",
                    JsonHelpers.ToElement(new { epoch = updated.Epoch, reason = "reset_context" }), now) : null;
                await _eventHub.EnsureInitializedFromStoreAsync(_store, CancellationToken.None).ConfigureAwait(false);
                var evt = SessionUpdatedEvent(updated);
                await _store.CompleteSessionResumeAsync(updated, unknown, evt, opEpoch, CancellationToken.None, boundary).ConfigureAwait(false);
                _sessions[sessionId] = updated;
                success = true;
                ReleaseSessionResumeCore(sessionId);
                if (mode == "reset_context") _scheduler.Clear(sessionId);
                if (replacement is not null && spec is not null)
                    await RegisterConfirmedOpenAgentSessionCoreAsync(updated with { Model = spec.Model,
                        ReasoningEffort = spec.ReasoningEffort, PermissionMode = spec.PermissionMode }, replacement,
                        spec, replacement.ConfigurationState, CancellationToken.None).ConfigureAwait(false);
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
                return updated;
            }, CancellationToken.None).ConfigureAwait(false);
            return restored;
        }
        catch (Exception error) when (!success)
        {
            if (!_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(async _ =>
                {
                    var session = RequireSessionCore(sessionId);
                    if (session.Epoch != opEpoch) return;
                    var lost = error is ProviderSessionNotFoundException;
                    if (!lost && session.Status != "unknown") return;
                    var updated = session with { Status = lost ? "unknown" : session.Status, UnknownLastError = error.Message,
                        UnknownReason = lost ? "session_lost" : session.UnknownReason,
                        ReconnectInvalidated = session.ReconnectInvalidated || lost,
                        UpdatedAtMs = NowMs() };
                    await PersistAndEmitSessionUpdatedCoreAsync(updated, updated.UpdatedAtMs, CancellationToken.None).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
            throw KernelException.SessionRecoveryRequired(error is OperationCanceledException ? "Provider recovery timed out." : error.Message);
        }
        finally
        {
            var replacementShutdownConfirmed = false;
            if (!success && replacement is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(KernelOptions.SessionResumeTimeout);
                    await ConfirmProviderShutdownAsync(sessionId, replacement, cleanup.Token).ConfigureAwait(false);
                    replacementShutdownConfirmed = true;
                }
                catch { /* Keep ownership for the next recovery if shutdown could not be confirmed. */ }
            }
            if (!_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(async _ =>
                {
                    _recoveringSessions.Remove(sessionId);
                    if (RequireSessionCore(sessionId).Epoch != (success && mode == "reset_context" ? opEpoch + 1 : opEpoch)) return;
                    if (!success) _mcpBridge.RevokeSession(sessionId);
                    if (!success && replacement is not null &&
                        _openSessionHandles.TryGetValue(sessionId, out var current) && ReferenceEquals(current.Agent, replacement))
                    {
                        if (replacementShutdownConfirmed)
                            _openSessionHandles.Remove(sessionId);
                        else
                            _sessionShutdownPending.TryAdd(sessionId, 0);
                    }
                    if (RequireSessionCore(sessionId).Status == "idle" && (!success || mode != "reset_context"))
                        await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool HasStopEvidence(ActiveTurnState active) => active.Control.HasStopEvidence;
}
