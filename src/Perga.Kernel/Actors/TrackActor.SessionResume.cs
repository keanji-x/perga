using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.Kernel.Events;
using Perga.Kernel.Flow;
using Perga.Kernel.Mcp;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;
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
        RevokeSessionRuntime(session.Id);
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
            ? handle.Agent.SupportsSessionResume : EffectiveAgentRuntime.CanResumeSession(session.Provider);
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
        var recoverable = !_taskCoordinator.IsSessionFrozenForDispatchCore(session);
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
            RevokeSessionRuntime(sessionId);
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
        _taskCoordinator.RequireDispatchWorkspace(session);
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
                HarnessLaunchSpec? spec = null;
                try
                {
                    if (mode == "reconnect")
                    {
                        using var launch = await _options.DispatchAdmission.EnterAsync(ct).ConfigureAwait(false);
                        var opened = await owner.OpenLockedAsync(EffectiveAgentRuntime, bindingId => EnqueueAsync(async _ =>
                        {
                            var session = RequireSessionCore(sessionId);
                            RequireResumeWorkspaceCore(session);
                            if (session.Epoch != opEpoch) throw KernelException.Conflict("Session epoch changed during recovery.");
                            return await BuildAgentLaunchSpecCoreAsync(session, null, bindingId, ct).ConfigureAwait(false);
                        }, ct), agent =>
                        {
                            if (!agent.SupportsSessionResume || agent.NativeThreadId != nativeId)
                                throw new ProviderSessionNotFoundException("The provider did not restore the original session.");
                        }, _options.SessionStartupTimeout, ct).ConfigureAwait(false);
                        replacement = opened.Agent;
                        spec = opened.LaunchSpec;
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
                            ResetMemoryInjection(sessionId);
                            if (_taskCoordinator.TryGetActiveTaskForSessionCore(sessionId) is { Active: var activeTask })
                                _taskCoordinator.SetPaused(activeTask, true);
                            _scheduler.Clear(sessionId);
                        }
                        if (replacement is not null && spec is not null)
                            await RegisterConfirmedOpenAgentSessionCoreAsync(updated with { Model = spec.Model,
                                ReasoningEffort = spec.ReasoningEffort, PermissionMode = spec.PermissionMode }, replacement,
                                spec, replacement.ConfigurationState, CancellationToken.None).ConfigureAwait(false);
                        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
                        return updated;
                    }, CancellationToken.None).ConfigureAwait(false);
                    if (replacement is not null) owner.TouchUsed();
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
                    NotifyQueueChangedCore(sessionId);
                    if (success && mode != "reset_context") await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool HasStopEvidence(ActiveTurnState active) => active.Control.HasStopEvidence;
    private async Task<AgentSessionDto> CompactSessionInternalAsync(string sessionId, CancellationToken ct, bool automatic = false, InteropPrincipal? principal = null, int? expectedEpoch = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _actorShutdownCts.Token);
        deadline.CancelAfter(_options.SessionCompactionTimeout);
        ct = deadline.Token;
        using var dispatch = automatic ? null : await _options.DispatchAdmission.EnterAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            return await RunWithMailboxReservationAsync(ReserveAsync, RunAsync, ReleaseAsync).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }

        async Task<(AgentSessionDto Session, int HandoverTurnIndex)?> ReserveAsync()
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireSessionMutationCore(session, principal ?? new InteropPrincipal.Human(), expectedEpoch, destructive: true);
            RequireSessionDispatchAvailableCore(sessionId);
            if (automatic && (!(CurrentContextUsageRatioCore(session) >= AutoCompactionThreshold) ||
                HasRecentBackgroundActivityCore(sessionId)))
                return null;
            if (_scheduler.IsOccupied(session, allowFinishingDispatch: true))
            {
                throw KernelException.Conflict($"Cannot compact session '{sessionId}' while a turn is actively running.");
            }

            ct.ThrowIfCancellationRequested();
            var turnIdx = NextTurnIndexCore(sessionId);
            _scheduler.BeginCompacting(sessionId);
            return (session, turnIdx);
        }

        async Task<AgentSessionDto> RunAsync((AgentSessionDto Session, int HandoverTurnIndex)? preparation)
        {
            try
            {
                if (preparation is not { } prepared)
                    return await EnqueueAsync(_ => Task.FromResult(RequireSessionCore(sessionId)), ct).ConfigureAwait(false);

                await EnqueueAsync(async _ =>
                {
                    var session = RequireSessionCore(sessionId);
                    await PersistAndEmitSessionUpdatedCoreAsync(session with { Status = "compacting", UpdatedAtMs = NowMs() }, NowMs(), ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);

                var oldAgent = await GetOrOpenAgentSessionCoreAsync(prepared.Session, null, ct).ConfigureAwait(false);
                var prep = (OldAgentSession: oldAgent, ContextBinding: _runtimes.Get(sessionId).ContextUsage,
                    HandoverTurnIndex: prepared.HandoverTurnIndex);
                var native = await prep.OldAgentSession.ResolveNativeCompactSupportAsync(ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
                var currentSession = await EnqueueAsync(_ =>
                {
                    var session = RequireSessionCore(sessionId);
                    RequireSessionMutationCore(session, principal ?? new InteropPrincipal.Human(), expectedEpoch, destructive: true);
                    return Task.FromResult((Session: session, Skip: automatic && HasRecentBackgroundActivityCore(sessionId)));
                }, ct).ConfigureAwait(false);
                if (currentSession.Skip) return currentSession.Session;
                if (native)
                {
                    var compactTurnId = $"trn_{prep.HandoverTurnIndex:D6}_{Guid.NewGuid():N}";
                    var nativeStartedAt = NowMs();
                    var nativeResult = await prep.OldAgentSession.CompactAsync(ct).WaitAsync(ct).ConfigureAwait(false);
                    _runtimes.Get(sessionId).TouchUsed();
                    var compactSummary = string.IsNullOrWhiteSpace(nativeResult.Summary)
                        ? "Context compacted."
                        : nativeResult.Summary.Trim();

                    return await EnqueueAsync(async _ =>
                    {
                        var current = RequireSessionCore(sessionId);
                        if (principal is InteropPrincipal.Session)
                            RequireSessionMutationCore(current, principal, expectedEpoch, destructive: true);
                        var now = NowMs();

                        var compactTurn = new SessionTurnDto(
                            Id: compactTurnId,
                            SessionId: sessionId,
                            TurnIndex: prep.HandoverTurnIndex,
                            Epoch: current.Epoch,
                            TriggerKind: "system",
                            Input: JsonHelpers.ToElement(new { prompt = "/compact", kind = "native_compact" }),
                            Status: "completed",
                            Usage: nativeResult.Usage is null ? null : JsonHelpers.ToElement(nativeResult.Usage),
                            ErrorText: null,
                            StartedAtMs: nativeStartedAt,
                            CompletedAtMs: now);

                        var compactItems = new List<SessionItemDto>
                        {
                            new(0, sessionId, compactTurnId, "user", JsonHelpers.ToElement(new { v = 1, content = "/compact" }), nativeStartedAt),
                            new(0, sessionId, compactTurnId, "assistant_text", JsonHelpers.ToElement(new { v = 1, text = compactSummary }), now)
                        };

                        var compacted = current;
                        if (IsCurrentContextBindingCore(current, prep.OldAgentSession, prep.ContextBinding))
                        {
                            compacted = nativeResult.Usage is { } usage
                                ? ApplyContextUsageCore(current, prep.OldAgentSession, prep.ContextBinding, nativeResult.ContextUsageRevision, usage, now)
                                : current with { ContextUsage = null };
                            compacted = ApplyTrailingContextUsageCore(compacted, prep.OldAgentSession, prep.ContextBinding, nativeResult.CompletedPromptId, now);
                        }
                        compacted = compacted with
                        {
                            NativeThreadId = current.NativeThreadId ?? prep.OldAgentSession.NativeThreadId,
                            Status = "idle",
                            LastNotifiableTurnAtMs = Math.Max(current.LastNotifiableTurnAtMs ?? 0, now),
                            UpdatedAtMs = now
                        };
                        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
                        await _store.RecordTurnAndItemsAsync(compactTurn, compactItems, compacted, ct).ConfigureAwait(false);
                        _sessions[sessionId] = compacted;
                        ResetMemoryInjection(sessionId);
                        await PersistAndEmitSessionUpdatedCoreAsync(compacted, now, ct).ConfigureAwait(false);

                        return compacted;
                    }, ct).ConfigureAwait(false);
                }

                // Fallback: Request structured handover summary turn on current IAgentSession and bump Epoch.
                const string handoverPrompt =
                    "Epoch handover requested. Summarize concisely in structured sections: " +
                    "(1) Current Progress, (2) Key Paths & Artifacts, (3) Verified Conclusions, and (4) Immediate Next Steps.";

                var handoverTurnId = $"trn_{prep.HandoverTurnIndex:D6}_{Guid.NewGuid():N}";
                var startedAt = NowMs();
                var summaryBuilder = new StringBuilder();
                TokenUsage? handoverUsage = null;

                await foreach (var evt in ReadCompactionEventsAsync(prep.OldAgentSession,
                                   new TurnInput(handoverPrompt, handoverTurnId), ct).ConfigureAwait(false))
                {
                    switch (evt)
                    {
                        case UnifiedAgentEvent.TextDelta td:
                            summaryBuilder.Append(td.Delta);
                            break;
                        case UnifiedAgentEvent.TurnCompleted tc:
                            handoverUsage = tc.Usage;
                            break;
                    }
                }

                var handoverSummary = summaryBuilder.ToString().Trim();

                if (principal is InteropPrincipal.Session)
                    await EnqueueAsync(_ =>
                    {
                        RequireSessionMutationCore(RequireSessionCore(sessionId), principal, expectedEpoch, destructive: true);
                        return Task.CompletedTask;
                    }, ct).ConfigureAwait(false);

                // 2. Dispose old IAgentSession instance.
                await _runtimes.Get(sessionId).CloseAsync(_options.SessionStartupTimeout, ct).ConfigureAwait(false);

                // 3 & 4. Update Epoch = Epoch + 1, NativeThreadId = null, clear context usage -> seed fresh IAgentSession with latest Report + handover summary!
                var handover = await EnqueueAsync(async _ =>
                {

                    var current = RequireSessionCore(sessionId);
                    if (principal is InteropPrincipal.Session)
                        RequireSessionMutationCore(current, principal, expectedEpoch, destructive: true);
                    var now = NowMs();

                    var handoverTurn = new SessionTurnDto(
                        Id: handoverTurnId,
                        SessionId: sessionId,
                        TurnIndex: prep.HandoverTurnIndex,
                        Epoch: current.Epoch,
                        TriggerKind: "system",
                        Input: JsonHelpers.ToElement(new { prompt = handoverPrompt, kind = "epoch_handover" }),
                        Status: "completed",
                        Usage: handoverUsage is null ? null : JsonHelpers.ToElement(handoverUsage),
                        ErrorText: null,
                        StartedAtMs: startedAt,
                        CompletedAtMs: now);

                    var handoverItems = new List<SessionItemDto>
                    {
            new(0, sessionId, handoverTurnId, "user", JsonHelpers.ToElement(new { v = 1, content = handoverPrompt }), startedAt),
            new(0, sessionId, handoverTurnId, "assistant_text", JsonHelpers.ToElement(new { v = 1, text = handoverSummary }), now)
                    };

                    var compacted = current with
                    {
                        Epoch = current.Epoch + 1,
                        NativeThreadId = null,
                        ContextUsage = null,
                        Status = "idle",
                        LastNotifiableTurnAtMs = Math.Max(current.LastNotifiableTurnAtMs ?? 0, now),
                        UpdatedAtMs = now
                    };
                    await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
                    await _store.RecordTurnAndItemsAsync(handoverTurn, handoverItems, compacted, ct).ConfigureAwait(false);
                    _sessions[sessionId] = compacted;
                    await PersistAndEmitSessionUpdatedCoreAsync(compacted, now, ct).ConfigureAwait(false);

                    var latestReportMarkdown = _reportEngine.RenderMarkdown(_reports.Document, withMarkers: true);
                    var seedPrompt = BuildEpochSeedSystemPrompt(compacted, latestReportMarkdown, handoverSummary);

                    return (Session: compacted, SeedPrompt: seedPrompt);
                }, ct).ConfigureAwait(false);
                await GetOrOpenAgentSessionCoreAsync(handover.Session, handover.SeedPrompt, ct).ConfigureAwait(false);
                return await EnqueueAsync(_ => Task.FromResult(RequireSessionCore(sessionId)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !_actorShutdownCts.IsCancellationRequested)
            {
                // A timed-out compaction may still be mutating the provider. Fence dispatch
                // until explicit recovery confirms shutdown or reconnects the original session.
                await EnqueueAsync(async _ =>
                {
                    await MarkSessionUnknownCoreAsync(RequireSessionCore(sessionId), "stream_lost",
                        lastError: "Provider compaction timed out.").ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        async Task ReleaseAsync((AgentSessionDto Session, int HandoverTurnIndex)? preparation)
        {
            if (preparation is null || _actorShutdownCts.IsCancellationRequested) return;
            _scheduler.EndCompacting(sessionId);
            var current = RequireSessionCore(sessionId);
            if (!await MarkShutdownUnconfirmedIfNeededAsync(sessionId).ConfigureAwait(false) && current.Status == "compacting")
                await PersistAndEmitSessionUpdatedCoreAsync(current with { Status = "idle", UpdatedAtMs = NowMs() }, NowMs(), CancellationToken.None).ConfigureAwait(false);
            await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
        }
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> ReadCompactionEventsAsync(
        IAgentSession agent, TurnInput input, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var events = agent.StreamTurnAsync(input, ct).GetAsyncEnumerator(ct);
        try
        {
            while (await events.MoveNextAsync().AsTask().WaitAsync(ct).ConfigureAwait(false))
                yield return events.Current;
        }
        finally
        {
            // Dispose may itself wait for the outstanding read; do not block fallback cleanup.
            try { await events.DisposeAsync().AsTask().WaitAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (ct.IsCancellationRequested) { }
        }
    }

    // Called only outside the mailbox. The runtime gate serializes provider I/O;
    // launch-spec construction and durable publication use short mailbox operations.

    public async Task PokePendingWakesAsync(CancellationToken ct = default, bool allowInline = true)
    {
        var runs = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var pending = new List<Func<CancellationToken, Task>>();
            foreach (var sessionId in _sessions.Keys)
                if (await _scheduler.PokeAsync(sessionId, allowInline: allowInline).ConfigureAwait(false) is { } run)
                    pending.Add(run);
            return pending;
        }, ct).ConfigureAwait(false);
        foreach (var run in runs) await run(ct).ConfigureAwait(false);
    }

    public Task<bool> CancelPendingWakeAsync(string wakeId, CancellationToken ct = default) =>
        _store.TrackWakes.CancelPendingWakeAsync(TrackId, wakeId, ct);

    private async Task RunReservedWakeAsync(string sessionId, IReadOnlyList<TrackWake> batch, Guid reservation, CancellationToken ct)
    {
        var work = new TrackWakeBatchWorkItem(this, sessionId, batch, reservation);
        try
        {
            await _turnRunner.RunAsync(work, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await work.FailBeforeSettlementAsync().ConfigureAwait(false);
            await work.NotifyUpgradeOutcomeAsync(null, ex.Message).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(async _ =>
                {
                    if (_scheduler.TryGetReservation(sessionId, out var current) && current == reservation)
                    {
                        _scheduler.ReleaseReservation(sessionId);
                        await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                    }
                }, CancellationToken.None).ConfigureAwait(false);
        }
    }

}
