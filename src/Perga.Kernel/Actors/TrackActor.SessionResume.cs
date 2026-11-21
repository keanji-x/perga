using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private Task MarkSessionUnknownCoreAsync(AgentSessionDto session, string reason,
        bool? reconnectInvalidated = null, string? lastError = null)
    {
        var now = NowMs();
        var unknown = session with
        {
            Status = "unknown",
            UnknownReason = reason,
            ReconnectInvalidated = reconnectInvalidated ?? session.ReconnectInvalidated,
            UnknownLastError = lastError ?? session.UnknownLastError,
            UpdatedAtMs = now
        };
        return PersistAndEmitSessionUpdatedCoreAsync(unknown, now, CancellationToken.None);
    }

    private async Task<bool> MarkShutdownUnconfirmedIfNeededAsync(string sessionId, bool? reconnectInvalidated = null)
    {
        if (!_runtimes.IsShutdownUnconfirmed(sessionId)) return false;
        await MarkSessionUnknownCoreAsync(RequireSessionCore(sessionId), "shutdown_unconfirmed", reconnectInvalidated).ConfigureAwait(false);
        return true;
    }

    private SessionUnknownDetailDto? ReadUnknownDetailCore(AgentSessionDto session)
    {
        if (session.Status != "unknown") return null;
        var supports = _runtimes.TryGetOpen(session.Id, out var handle)
            ? handle.Agent.SupportsSessionResume : _agentRuntime.CanResumeSession(session.Provider);
        var reason = session.UnknownReason ?? "stream_lost";
        var detail = reason switch
        {
            "shutdown_unconfirmed" => "The previous provider subprocess did not confirm shutdown.",
            "session_lost" => "The original provider session no longer exists. Reset context to continue.",
            "stop_timeout" => "The provider did not confirm that the turn stopped.",
            "host_restarted" => "The host restarted before the provider outcome was confirmed.",
            _ => "The provider stream ended without a confirmed outcome."
        };
        if (string.IsNullOrWhiteSpace(session.NativeThreadId)) detail += " No original provider session ID is available.";
        var recoverable = !IsSessionFrozenForDispatchCore(session);
        return new(reason, detail, recoverable && supports && !session.ReconnectInvalidated &&
            !string.IsNullOrWhiteSpace(session.NativeThreadId), recoverable, _scheduler.IsRecovering(session.Id), session.UnknownLastError);
    }

    private void RequireSessionDispatchAvailableCore(string sessionId)
    {
        if (_runtimes.IsShutdownUnconfirmed(sessionId) || _scheduler.IsRecovering(sessionId) ||
            RequireSessionCore(sessionId).Status == "unknown")
            throw new KernelException(KernelErrorCode.SessionRecoveryRequired, "Recover the provider session before sending more input.");
    }

    private async Task<AgentSessionDto> RecoverSessionAsync(string sessionId, string action, CancellationToken ct, InteropPrincipal? principal = null, int? expectedEpoch = null)
    {
        if (action is not ("reconnect" or "reset_context"))
            throw KernelException.InvalidArgument("Recovery mode must be reconnect or reset_context.");
        var operation = await EnqueueAsync(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireSessionMutationCore(session, principal ?? new InteropPrincipal.Human(), expectedEpoch, destructive: true);
            RequireResumeWorkspaceCore(session);
            if (_scheduler.HasActive(sessionId) || _scheduler.IsCompacting(sessionId) ||
                _scheduler.HasReservation(sessionId) || _scheduler.IsSteering(sessionId))
                throw KernelException.Conflict("Wait for the current session operation before recovering.");
            if (session.Status != "unknown" && !(action == "reset_context" && session.Status == "idle"))
                throw KernelException.Conflict("Recovery is unavailable in this session state.");
            if (_scheduler.IsRecovering(session.Id)) throw KernelException.Conflict("Session recovery is already in progress.");
            if (action == "reconnect" && ReadUnknownDetailCore(session)?.CanReconnect != true)
            {
                if (string.IsNullOrWhiteSpace(session.NativeThreadId))
                    await PersistAndEmitSessionUpdatedCoreAsync(session with { ReconnectInvalidated = true,
                        UnknownLastError = "No original provider session ID is available.", UpdatedAtMs = NowMs() }, NowMs(), CancellationToken.None).ConfigureAwait(false);
                throw KernelException.PreconditionFailed("The original provider session cannot be reconnected.");
            }
            if (!_scheduler.TryBeginRecovery(sessionId))
                throw KernelException.Conflict("Session recovery is already in progress.");
            NotifyQueueChangedCore(sessionId);
            var completion = new TaskCompletionSource<AgentSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = actor.TrackBackgroundJob(async () =>
            {
                try { completion.TrySetResult(await RunSessionRecoveryAsync(sessionId, action, session.NativeThreadId, session.Epoch).ConfigureAwait(false)); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            return completion.Task;
        }, ct).ConfigureAwait(false);
        return await operation.WaitAsync(ct).ConfigureAwait(false);
    }

    private void RequireResumeWorkspaceCore(AgentSessionDto session)
    {
        RequireWorkspaceReadyCore();
        RequireSessionWritableCore(session);
        RequireDispatchWorkspace(session);
    }

    private async Task<AgentSessionDto> RunSessionRecoveryAsync(string sessionId, string mode, string? nativeId, int opEpoch)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        deadline.CancelAfter(KernelOptions.SessionResumeTimeout);
        var ct = deadline.Token;
        IAgentSession? replacement = null;
        var success = false;
        try
        {
            return await _runtimes.Get(sessionId).RunAsync(async owner =>
            {
                await owner.CloseLockedAsync(_options.SessionStartupTimeout, ct, scoped: mode == "reconnect").ConfigureAwait(false);
                var spec = await EnqueueAsync(async _ =>
                {
                    var session = RequireSessionCore(sessionId);
                    RequireResumeWorkspaceCore(session);
                    if (session.Epoch != opEpoch) throw KernelException.Conflict("Session epoch changed during recovery.");
                    _mcpBridge.RevokeSession(sessionId);
                    return mode == "reconnect" ? await BuildAgentLaunchSpecCoreAsync(session, null, ct).ConfigureAwait(false) : null;
                }, ct).ConfigureAwait(false);
                try
                {
                    if (spec is not null)
                    {
                        using var launch = await _options.DispatchAdmission.EnterAsync(ct).ConfigureAwait(false);
                        var opened = await owner.OpenLockedAsync(_agentRuntime, spec, agent =>
                        {
                            if (!agent.SupportsSessionResume || agent.NativeThreadId != nativeId)
                                throw new ProviderSessionNotFoundException("The provider did not restore the original session.");
                        }, _options.SessionStartupTimeout, ct).ConfigureAwait(false);
                        replacement = opened.Agent;
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
                        if (mode == "reset_context")
                        {
                            if (TryGetActiveTaskForSessionCore(sessionId) is { Active: var activeTask })
                                activeTask.Paused = true;
                            _scheduler.Clear(sessionId);
                        }
                        if (replacement is not null && spec is not null)
                            await RegisterConfirmedOpenAgentSessionCoreAsync(updated with { Model = spec.Model,
                                ReasoningEffort = spec.ReasoningEffort, PermissionMode = spec.PermissionMode }, replacement,
                                spec, replacement.ConfigurationState, CancellationToken.None).ConfigureAwait(false);
                        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
                        return updated;
                    }, CancellationToken.None).ConfigureAwait(false);
                    return restored;
                }
                catch
                {
                    try { await owner.CloseLockedAsync(_options.SessionStartupTimeout, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* Keep replacement owned until shutdown can be confirmed. */ }
                    throw;
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (!success)
        {
            if (!_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(async _ =>
                {
                    var session = RequireSessionCore(sessionId);
                    if (session.Epoch != opEpoch) return;
                    var updated = session with { UnknownLastError = error.Message,
                        Status = _runtimes.IsShutdownUnconfirmed(sessionId) || error is ProviderSessionNotFoundException ? "unknown" : session.Status,
                        UnknownReason = _runtimes.IsShutdownUnconfirmed(sessionId) ? "shutdown_unconfirmed" :
                            error is ProviderSessionNotFoundException ? "session_lost" : session.UnknownReason,
                        ReconnectInvalidated = session.ReconnectInvalidated || error is ProviderSessionNotFoundException,
                        UpdatedAtMs = NowMs() };
                    await PersistAndEmitSessionUpdatedCoreAsync(updated, updated.UpdatedAtMs, CancellationToken.None).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
            throw KernelException.PreconditionFailed(error is OperationCanceledException ? "Provider recovery timed out." : error.Message);
        }
        finally
        {
            _scheduler.EndRecovery(sessionId);
            if (!_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(async _ =>
                {
                    if (!success) _mcpBridge.RevokeSession(sessionId);
                    NotifyQueueChangedCore(sessionId);
                    if (success && mode != "reset_context") await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool HasStopEvidence(ActiveTurnState active) => active.Control.HasStopEvidence;
}
