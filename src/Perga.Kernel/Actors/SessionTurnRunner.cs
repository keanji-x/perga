using System.Text;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.PolicyVm;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private sealed class SessionTurnRunner(TrackActor actor)
    {
        public async Task<SessionTurnDto?> RunAsync(ITurnWorkItem work, CancellationToken ct, bool alreadyAdmitted = false)
        {
            var sessionId = work.SessionId;
            var input = work.DurableInput;
            var workerInput = work.WorkerMessage;
            var wakeReservation = work.Reservation;
            if (input is null)
                await actor.WaitForWorkspacePreparationAsync(ct).ConfigureAwait(false);
            using var dispatch = alreadyAdmitted ? null : await actor._options.DispatchAdmission.EnterAsync(ct).ConfigureAwait(false);
            var prompt = await work.PreparePromptAsync(ct).ConfigureAwait(false);
            var triggerKind = work.TriggerKind;
            if (work is TrackWakeBatchWorkItem && string.IsNullOrWhiteSpace(prompt))
                return null;
            Interlocked.Increment(ref actor._inFlightExternalOps);
            ActiveTurnState? pendingCleanup = null;

            try
            {
                var turnSetup = await actor.EnqueueAsync<(ActiveTurnState? ActiveState, string EffectivePrompt)>(async _ =>
                {
                    await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var session = actor.RequireSessionCore(sessionId);
                    actor.RequireSessionDispatchAvailableCore(sessionId);
                    if (actor._scheduler.IsOccupied(session, input, wakeReservation))
                        throw KernelException.Conflict($"Session '{sessionId}' already has an active or unresolved turn.");

                    var effectivePrompt = prompt;
                    var workerSettings = session.BoundAttemptId is null ? null
                        : await actor.ReadWorkerSettingsCoreAsync(session, ct).ConfigureAwait(false);
                    var now = actor.NowMs();
                    var turnIndex = input?.TurnIndex ?? actor.NextTurnIndexCore(session.Id);
                    var turnId = input?.Receipt.TurnId ?? $"trn_{turnIndex:D6}_{Guid.NewGuid():N}";
                    var turnDto = new SessionTurnDto(
                        Id: turnId,
                        SessionId: session.Id,
                        TurnIndex: turnIndex,
                        Epoch: input?.Epoch ?? session.Epoch,
                        TriggerKind: triggerKind,
                        Input: input is null ? JsonHelpers.ToElement(new { prompt, workerSettings }) : JsonHelpers.ToElement(new { }),
                        Status: "running",
                        Usage: null,
                        ErrorText: null,
                        StartedAtMs: now,
                        CompletedAtMs: null,
                        UserItemId: input?.UserItemId);

                    var runningSession = session with { Status = "running", UpdatedAtMs = now };
                    var runningEvent = actor.SessionUpdatedEvent(runningSession);
                    if (input is not null)
                    {
                        if (actor._track.ClosedAtMs is not null)
                            return (null, effectivePrompt);
                        await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
                        if (!await actor._store.SessionInputs.ClaimAsync(input, runningSession, runningEvent, ct).ConfigureAwait(false))
                            return (null, effectivePrompt);
                        actor._sessions[session.Id] = runningSession;
                        session = session with { Provider = input.Provider, Model = input.Model, ReasoningEffort = input.ReasoningEffort };
                    }

                    IAgentSession agentSession;
                    try
                    {
                        if (session.BoundAttemptId is { } workerAttemptId && actor.RequireCurrentWorkerCore(sessionId, workerAttemptId).Attempt.Paused)
                            throw KernelException.Conflict("The Worker is paused.");
                        if (input is not null || workerInput is not null)
                            actor.RequireDispatchWorkspace(session);

                        if (actor._policy is not null)
                        {
                            var turnCtx = new PolicyTurnContext(
                                TrackId: actor.TrackId,
                                SessionId: session.Id,
                                IsLead: session.IsLead,
                                BoundAttemptId: session.BoundAttemptId,
                                ContextUsageRatio: actor.CurrentContextUsageRatioCore(runningSession) ?? 0,
                                Inputs: actor._track.TemplateInputs);
                            var eval = actor._policyVm.BuildTurnContext(actor._policy, turnCtx, actor._track.TemplateState);
                            await actor.UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(eval.Output))
                            {
                                effectivePrompt = $"<policy_context>\n{eval.Output}\n</policy_context>\n\n{prompt}";
                            }
                        }

                        agentSession = await actor.GetOrOpenAgentSessionCoreAsync(session, overrideSystemPrompt: null, ct)
                            .ConfigureAwait(false);
                        runningSession = runningSession with { ContextUsage = actor.RequireSessionCore(session.Id).ContextUsage };
                        if (workerSettings is not null)
                            workerSettings = actor.ResolveWorkerTurnSettingsCore(session, workerSettings);
                    }
                    catch when (input is not null)
                    {
                        var failed = turnDto with { Status = "failed", ErrorText = "Message could not be prepared for dispatch.", CompletedAtMs = actor.NowMs() };
                        var idle = runningSession with { Status = "idle", UpdatedAtMs = failed.CompletedAtMs.Value };
                        var outcome = new SessionItemDto(0, sessionId, turnId, "turn_outcome",
                            JsonHelpers.ToElement(new { v = 1, status = "failed", error = failed.ErrorText }), idle.UpdatedAtMs);
                        await actor.SettleDurableTurnCoreAsync(input, "rejected", failed, [outcome], idle).ConfigureAwait(false);
                        await actor.PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                        return (null, effectivePrompt);
                    }
                    catch (KernelException ex) when (ex.Code == KernelErrorCode.Conflict && work is not TrackWakeBatchWorkItem)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var failed = turnDto with { Status = "failed", ErrorText = ex.Message, CompletedAtMs = actor.NowMs() };
                        var idle = runningSession with { Status = "idle", UpdatedAtMs = failed.CompletedAtMs.Value };
                        await actor._store.RecordTurnAndItemsAsync(failed, [], idle, CancellationToken.None).ConfigureAwait(false);
                        actor._sessions[session.Id] = idle;
                        await work.OnTurnCreatedAsync(failed).ConfigureAwait(false);
                        throw;
                    }
                    if (input is null)
                    {
                        var userItem = CreateWakeUserItem(turnDto, prompt, workerInput?.Id, presentation: work.Presentation);
                        var recorded = await actor._store.RecordTurnAndItemsWithIdsAsync(turnDto, [userItem], runningSession, ct, workerInput)
                            .ConfigureAwait(false);
                        turnDto = recorded.Turn;
                    }
                    await work.OnTurnCreatedAsync(turnDto).ConfigureAwait(false);

                    actor._sessions[session.Id] = runningSession;
                    var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, actor._actorShutdownCts.Token);
                    var activeState = new ActiveTurnState(session.Id, (session.Model, session.ReasoningEffort),
                        actor._liveContextUsage.GetOrCreateValue(agentSession), turnDto, agentSession, turnCts, true) { Settings = workerSettings };
                    actor._scheduler.SetActive(session.Id, activeState);
                    pendingCleanup = activeState;
                    if (wakeReservation is not null)
                        actor._scheduler.ReleaseReservation(session.Id);
                    if (input is null)
                        await actor.PersistAndEmitSessionUpdatedCoreAsync(runningSession, now, ct).ConfigureAwait(false);
                    else
                        await actor.PublishInputEventAsync(runningEvent).ConfigureAwait(false);
                    return (activeState, effectivePrompt);
                }, actor._actorShutdownCts.Token).ConfigureAwait(false);

                if (turnSetup.ActiveState is not { } activeTurn)
                    return new SessionTurnDto(input!.Receipt.TurnId, sessionId, input.TurnIndex, input.Epoch, triggerKind,
                        JsonHelpers.ToElement(new { }), "queued", null, null, null, null);

                var items = new List<SessionItemDto>();
                var textAccum = new StringBuilder();
                var thinkingAccum = new StringBuilder();
                TokenUsage? finalUsage = null;
                long? finalUsageRevision = null;
                long? completedPromptId = null;
                string? boundThreadId = null;
                string? errorText = null;
                var turnStatus = "completed";

                var delivered = false;
                var rejectedBeforePrompt = false;
                var conclusive = false;

                Task RecordActivityAsync(SessionItemDto item) =>
                    actor._store.AppendSessionItemsAsync(sessionId, [item], CancellationToken.None);

                async Task FlushThinkingChunkAsync(long createdAtMs)
                {
                    if (thinkingAccum.Length == 0)
                    {
                        return;
                    }

                    var chunk = thinkingAccum.ToString();
                    thinkingAccum.Clear();
                    var thinkingItem = new SessionItemDto(
                        Id: 0,
                        SessionId: sessionId,
                        TurnId: activeTurn.TurnDto.Id,
                        Kind: "thinking",
                        Payload: JsonHelpers.ToElement(new { v = 1, text = chunk }),
                        CreatedAtMs: createdAtMs);
                    await RecordActivityAsync(thinkingItem).ConfigureAwait(false);
                }

                try
                {
                    await foreach (var evt in activeTurn.AgentSession
                                       .StreamTurnAsync(new TurnInput(turnSetup.EffectivePrompt, activeTurn.TurnDto.Id,
                                           activeTurn.Settings?.Model, activeTurn.Settings?.ReasoningEffort), activeTurn.TurnCts.Token)
                                       .ConfigureAwait(false))
                    {
                        var evtNow = actor.NowMs();
                        var confirmsDelivery = evt is UnifiedAgentEvent.TurnAccepted or UnifiedAgentEvent.TextDelta or
                            UnifiedAgentEvent.ThinkingDelta or UnifiedAgentEvent.ToolCallStarted or
                            UnifiedAgentEvent.ToolCallCompleted or UnifiedAgentEvent.ApprovalRequested or
                            UnifiedAgentEvent.TurnCompleted or UnifiedAgentEvent.TurnInterrupted or
                            UnifiedAgentEvent.TurnError { IsConclusive: true, BeforePrompt: false };
                        if (confirmsDelivery || evt is UnifiedAgentEvent.TurnAccepted)
                        {
                            await work.OnProviderDeliveredAsync(activeTurn.TurnDto, evt, boundThreadId).ConfigureAwait(false);
                            delivered = true;
                        }
                        if (confirmsDelivery && !activeTurn.ProviderAccepted)
                        {
                            await actor.EnqueueAsync(_ =>
                            {
                                activeTurn.ProviderAccepted = true;
                                if (input is not null || workerInput is not null)
                                    actor.NotifyQueueChangedCore(sessionId);
                                return Task.CompletedTask;
                            }, actor._actorShutdownCts.Token).ConfigureAwait(false);
                        }
                        switch (evt)
                        {
                            case UnifiedAgentEvent.ThreadBound tb:
                                boundThreadId = tb.NativeThreadId;
                                break;

                            case UnifiedAgentEvent.TextDelta td:
                                await FlushThinkingChunkAsync(evtNow).ConfigureAwait(false);
                                textAccum.Append(td.Delta);
                                actor.PublishTurnActivity(activeTurn,
                                    KernelEventKinds.SessionTextDelta,
                                    JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, delta = td.Delta }),
                                    evtNow);
                                break;

                            case UnifiedAgentEvent.ThinkingDelta thd:
                                thinkingAccum.Append(thd.Delta);
                                actor.PublishTurnActivity(activeTurn,
                                    KernelEventKinds.SessionThinkingDelta,
                                    JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, delta = thd.Delta }),
                                    evtNow);
                                break;

                            case UnifiedAgentEvent.ToolCallStarted tcs:
                                await FlushThinkingChunkAsync(evtNow).ConfigureAwait(false);
                                actor.PublishTurnActivity(activeTurn,
                                    KernelEventKinds.SessionToolProgress,
                                    JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, phase = "started", callId = tcs.CallId, name = tcs.Name, args = tcs.Args }),
                                    evtNow);
                                await RecordActivityAsync(new SessionItemDto(
                                    Id: 0,
                                    SessionId: sessionId,
                                    TurnId: activeTurn.TurnDto.Id,
                                    Kind: "tool_call",
                                    Payload: JsonHelpers.ToElement(new { v = 1, callId = tcs.CallId, name = tcs.Name, args = tcs.Args }),
                                    CreatedAtMs: evtNow)).ConfigureAwait(false);
                                break;

                            case UnifiedAgentEvent.ToolCallCompleted tcc:
                                await FlushThinkingChunkAsync(evtNow).ConfigureAwait(false);
                                actor.PublishTurnActivity(activeTurn,
                                    KernelEventKinds.SessionToolProgress,
                                    JsonHelpers.ToElement(new { sessionId, turnId = activeTurn.TurnDto.Id, phase = "completed", callId = tcc.CallId, isError = tcc.IsError, output = activeTurn.Settings is null ? null : tcc.Output }),
                                    evtNow);
                                await RecordActivityAsync(new SessionItemDto(
                                    Id: 0,
                                    SessionId: sessionId,
                                    TurnId: activeTurn.TurnDto.Id,
                                    Kind: "tool_result",
                                    Payload: JsonHelpers.ToElement(new { v = 1, callId = tcc.CallId, output = tcc.Output, isError = tcc.IsError }),
                                    CreatedAtMs: evtNow)).ConfigureAwait(false);
                                break;

                            case UnifiedAgentEvent.ApprovalRequested approval:
                                if (activeTurn.Settings?.ApprovalMode == "deny")
                                {
                                    await activeTurn.AgentSession.RespondApprovalAsync(approval.RequestId, false,
                                        "The Worker approval mode denies this request.", activeTurn.TurnCts.Token).ConfigureAwait(false);
                                    break;
                                }
                                var providerOptions = approval.Options is { Count: > 0 } opts ? opts : null;
                                var options = providerOptions is not null
                                    ? providerOptions.Select(o => o.Label).ToArray()
                                    : new[] { "Approve", "Deny" };
                                var suspension = await actor.EnqueueAsync(async _ =>
                                {
                                    var susp = await actor.CreateAskSuspensionCoreAsync(
                                        $"Approve tool '{approval.ToolName}'?",
                                        options,
                                        sessionId,
                                        activeTurn.TurnCts.Token).ConfigureAwait(false);
                                    activeTurn.OwnedAskIds.Add(susp.Ask.Id);
                                    return susp;
                                }, activeTurn.TurnCts.Token).ConfigureAwait(false);

                                var humanAns = await suspension.WaiterTask.WaitAsync(activeTurn.TurnCts.Token).ConfigureAwait(false);
                                string? selectedOptionId = null;
                                bool approved;
                                if (providerOptions is not null &&
                                    AskAnswerEvaluator.TryGetSelectedOptionIndex(humanAns, options, out var selectedIndex))
                                {
                                    var chosen = providerOptions[selectedIndex];
                                    selectedOptionId = chosen.OptionId;
                                    approved = chosen.IsApproval;
                                }
                                else
                                {
                                    approved = AskAnswerEvaluator.IsApproved(humanAns, options);
                                }
                                await activeTurn.AgentSession.RespondApprovalAsync(
                                    approval.RequestId,
                                    approved,
                                    selectedOptionId,
                                    approved ? null : "Denied by user.",
                                    activeTurn.TurnCts.Token).ConfigureAwait(false);
                                break;

                            case UnifiedAgentEvent.ContextUsageUpdated contextUsage:
                                await actor.EnqueueAsync(async _ =>
                                {
                                    var current = actor.RequireSessionCore(sessionId);
                                    if (!activeTurn.MatchesSessionConfiguration(current))
                                        return;
                                    var updated = actor.ApplyContextUsageCore(current, activeTurn.AgentSession, activeTurn.ContextBinding,
                                        contextUsage.ContextUsageRevision, contextUsage.Usage, evtNow);
                                    if (!ReferenceEquals(updated, current))
                                        await actor.PersistAndEmitSessionUpdatedCoreAsync(updated, evtNow, CancellationToken.None)
                                            .ConfigureAwait(false);
                                }, CancellationToken.None).ConfigureAwait(false);
                                break;

                            case UnifiedAgentEvent.TurnCompleted tc:
                                conclusive = true;
                                finalUsage = tc.Usage;
                                finalUsageRevision = tc.ContextUsageRevision;
                                completedPromptId = tc.CompletedPromptId;
                                break;

                            case UnifiedAgentEvent.TurnInterrupted:
                                conclusive = true;
                                turnStatus = "interrupted";
                                errorText = "Provider confirmed the interruption.";
                                break;

                            case UnifiedAgentEvent.TurnError te:
                                rejectedBeforePrompt = te.BeforePrompt;
                                conclusive = te.IsConclusive;
                                turnStatus = "failed";
                                errorText = $"{te.Code}: {te.Message}";
                                break;
                        }
                    }
                }
                catch (OperationCanceledException) when (activeTurn.Interrupted || actor._actorShutdownCts.IsCancellationRequested)
                {
                    turnStatus = "interrupted";
                    errorText = "Turn interrupted.";
                }
                catch (OperationCanceledException) when (input is null && ct.IsCancellationRequested)
                {
                    turnStatus = "interrupted";
                    errorText = "Caller canceled turn.";
                    throw;
                }
                catch (Exception ex)
                {
                    turnStatus = "failed";
                    errorText = ex.Message;
                }

                if (activeTurn.Interrupted && turnStatus == "completed")
                {
                    turnStatus = "interrupted";
                    errorText = "Turn interrupted.";
                }

                if (input is not null && !conclusive)
                {
                    turnStatus = "unknown";
                    errorText ??= "Provider outcome could not be confirmed.";
                }

                var endNow = actor.NowMs();
                if (thinkingAccum.Length > 0)
                {
                    items.Add(new SessionItemDto(
                        Id: 0,
                        SessionId: sessionId,
                        TurnId: activeTurn.TurnDto.Id,
                        Kind: "thinking",
                        Payload: JsonHelpers.ToElement(new { v = 1, text = thinkingAccum.ToString() }),
                        CreatedAtMs: endNow));
                }

                if (textAccum.Length > 0)
                {
                    items.Add(new SessionItemDto(
                        Id: 0,
                        SessionId: sessionId,
                        TurnId: activeTurn.TurnDto.Id,
                        Kind: "assistant_text",
                        Payload: JsonHelpers.ToElement(new { v = 1, text = textAccum.ToString() }),
                        CreatedAtMs: endNow));
                }

                if (turnStatus is "failed" or "interrupted")
                {
                    items.Add(new SessionItemDto(
                        Id: 0,
                        SessionId: sessionId,
                        TurnId: activeTurn.TurnDto.Id,
                        Kind: "turn_outcome",
                        Payload: JsonHelpers.ToElement(new
                        {
                            v = 1,
                            status = turnStatus,
                            error = errorText ?? (turnStatus == "interrupted" ? "Turn interrupted." : "Turn failed.")
                        }),
                        CreatedAtMs: endNow));
                }

                (SessionTurnDto CompletedTurn, bool ShouldAutoCompact) finalizeOutcome = default;
                async Task<SessionTurnDto> SettleAsync()
                {
                    finalizeOutcome = await actor.EnqueueAsync<(SessionTurnDto CompletedTurn, bool ShouldAutoCompact)>(async _ =>
                    {
                        var currentSession = actor.RequireSessionCore(sessionId);
                        if (currentSession.BoundAttemptId is { } boundAttemptId && activeTurn.Interrupted &&
                            actor._activeAttempts.TryGetValue(boundAttemptId, out var pausedAttempt) && pausedAttempt.Paused)
                        {
                            turnStatus = "interrupted";
                            errorText = "Turn interrupted.";
                            await actor.CloseInterruptedWorkerCoreAsync(activeTurn, pausedAttempt).ConfigureAwait(false);
                        }

                        if (activeTurn.MatchesSessionConfiguration(currentSession))
                        {
                            if (finalUsage is { } usage)
                                currentSession = actor.ApplyContextUsageCore(currentSession, activeTurn.AgentSession, activeTurn.ContextBinding,
                                    finalUsageRevision, usage, endNow);
                            currentSession = actor.ApplyTrailingContextUsageCore(currentSession, activeTurn.AgentSession, activeTurn.ContextBinding, completedPromptId, endNow);
                        }

                        var updatedSession = currentSession with
                        {
                            NativeThreadId = boundThreadId ?? activeTurn.AgentSession.NativeThreadId ?? currentSession.NativeThreadId,
                            Status = turnStatus == "unknown" && input is not null ? "unknown" : "idle",
                            UpdatedAtMs = endNow
                        };
                        var completedTurn = activeTurn.TurnDto with
                        {
                            Input = activeTurn.Settings is null ? activeTurn.TurnDto.Input :
                                JsonHelpers.ToElement(new { prompt, workerSettings = activeTurn.ProviderAccepted ? activeTurn.Settings : null }),
                            Status = turnStatus,
                            Usage = finalUsage is null ? null : JsonHelpers.ToElement(finalUsage),
                            ErrorText = errorText,
                            CompletedAtMs = turnStatus == "unknown" ? null : endNow
                        };

                        if (!conclusive && input?.IsQueuedFollowUp == true && !actor._scheduler.HasResumeHold(sessionId))
                            await actor.CreateSessionResumeHoldCoreAsync(sessionId, completedTurn.Id,
                                updatedSession.NativeThreadId ?? string.Empty, CancellationToken.None).ConfigureAwait(false);

                        if (conclusive && actor._scheduler.TryGetResumeHold(sessionId, out var recovery) &&
                            recovery.TurnId == completedTurn.Id)
                            await actor.MarkSessionResumeQuiescentCoreAsync(sessionId, recovery, CancellationToken.None).ConfigureAwait(false);

                        if (input is not null)
                        {
                            await actor.SettleDurableTurnCoreAsync(input, delivered ? "delivered" : "unknown",
                                completedTurn, items, updatedSession).ConfigureAwait(false);
                        }
                        else
                        {
                            await actor.CompleteWakeTurnCoreAsync(completedTurn, items, updatedSession).ConfigureAwait(false);
                        }

                        actor._scheduler.RemoveActive(sessionId);
                        activeTurn.Settled.TrySetResult(conclusive);
                        var autoCompact = !actor._scheduler.HasResumeHold(sessionId) && actor._sessions[sessionId].BoundAttemptId is null &&
                                          actor._sessions[sessionId].Status == "idle" && turnStatus == "completed" &&
                                          actor.CurrentContextUsageRatioCore(updatedSession) >= actor._options.AutoCompactionThreshold;

                        return (completedTurn, autoCompact);
                    }, CancellationToken.None).ConfigureAwait(false);

                    return finalizeOutcome.CompletedTurn;
                }

                await work.OnTurnSettledAsync(new TurnSettlement(activeTurn.TurnDto.Id, turnStatus,
                    activeTurn.Interrupted, delivered, rejectedBeforePrompt, SettleAsync)).ConfigureAwait(false);

                if (finalizeOutcome.ShouldAutoCompact)
                {
                    try
                    {
                        await actor.CompactSessionInternalAsync(sessionId, CancellationToken.None, automatic: true).ConfigureAwait(false);
                    }
                    catch (KernelException ex) when (ex.Code == KernelErrorCode.Conflict)
                    {
                        // A new turn reserved the session before optional compaction.
                    }
                }

                await actor.EnqueueAsync(async _ =>
                {
                    await actor.PokeSessionCoreAsync(sessionId, allowWorker: turnStatus == "completed").ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);

                return finalizeOutcome.CompletedTurn ?? throw new InvalidOperationException("The turn work item did not settle its turn.");
            }
            finally
            {
                if (pendingCleanup is { } active)
                {
                    try
                    {
                        if (!actor._actorShutdownCts.IsCancellationRequested)
                            await actor.EnqueueAsync(async _ =>
                            {
                                if (actor._scheduler.TryGetActive(sessionId, out var current) && ReferenceEquals(current, active))
                                {
                                    actor._scheduler.RemoveActive(sessionId);
                                    if (input is null)
                                    {
                                        var idle = actor.RequireSessionCore(sessionId) with { Status = "idle", UpdatedAtMs = actor.NowMs() };
                                        await actor.PersistAndEmitSessionUpdatedCoreAsync(idle, idle.UpdatedAtMs, CancellationToken.None)
                                            .ConfigureAwait(false);
                                        actor._sessions[sessionId] = idle;
                                        await actor.PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                                    }
                                }
                            }, CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        active.Settled.TrySetResult(false);
                        active.TurnCts.Dispose();
                    }
                }
                Interlocked.Decrement(ref actor._inFlightExternalOps);
            }
        }

    }
}
