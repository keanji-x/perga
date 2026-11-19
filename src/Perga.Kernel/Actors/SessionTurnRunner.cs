using System.Text;
using System.Text.Json;
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
        private sealed record TurnStart(ActiveTurnState Active, string EffectivePrompt);

        private sealed record PreparedDispatch(IAgentSession AgentSession, string EffectivePrompt);

        // What the provider stream established about the turn. Settlement can still mark it interrupted.
        private sealed class TurnOutcome
        {
            public string Status { get; set; } = "completed";
            public string? ErrorText { get; set; }
            public TokenUsage? FinalUsage { get; set; }
            public long? FinalUsageRevision { get; set; }
            public long? CompletedPromptId { get; set; }
            public string? BoundThreadId { get; set; }
            public bool Delivered { get; set; }
            public bool RejectedBeforePrompt { get; set; }
            public bool Conclusive { get; set; }
            public StringBuilder Text { get; } = new();
            public StringBuilder Thinking { get; } = new();
        }

        public async Task<SessionTurnDto?> RunAsync(ITurnWorkItem work, CancellationToken ct, bool alreadyAdmitted = false)
        {
            if (work.WaitForWorkspacePreparation)
                await actor.WaitForWorkspacePreparationAsync(ct).ConfigureAwait(false);
            using var dispatch = alreadyAdmitted ? null : await actor._options.DispatchAdmission.EnterAsync(ct).ConfigureAwait(false);
            if (await work.PreparePromptAsync(ct).ConfigureAwait(false) is not { } prompt)
                return null;

            Interlocked.Increment(ref actor._inFlightExternalOps);
            ActiveTurnState? activated = null;
            try
            {
                if (await StartTurnAsync(work, prompt, active => activated = active, ct).ConfigureAwait(false) is not { } start)
                    return work.CreateUnstartedTurn();

                var outcome = await StreamTurnAsync(work, start.Active, start.EffectivePrompt, ct).ConfigureAwait(false);
                var settled = await SettleTurnAsync(work, start.Active, prompt, outcome).ConfigureAwait(false);
                if (settled.ShouldAutoCompact)
                    await TryAutoCompactAsync(work.SessionId).ConfigureAwait(false);
                await actor.EnqueueAsync(async _ =>
                {
                    await actor.PokeSessionCoreAsync(work.SessionId, allowWorker: outcome.Status == "completed").ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);

                return settled.Turn ?? throw new InvalidOperationException("The turn work item did not settle its turn.");
            }
            finally
            {
                try
                {
                    if (activated is { } active)
                        await CleanUpAbandonedTurnAsync(work, active).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref actor._inFlightExternalOps);
                }
            }
        }

        // Claims the session, records the running turn and makes it the session's active turn, all in one actor step.
        // Returns null when the work item left the turn unstarted. The active turn is reported to onActivated as
        // soon as the scheduler sees it, so it is cleaned up even if announcing it fails.
        private Task<TurnStart?> StartTurnAsync(ITurnWorkItem work, string prompt, Action<ActiveTurnState> onActivated,
            CancellationToken ct) => actor.EnqueueAsync<TurnStart?>(async _ =>
        {
            await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var sessionId = work.SessionId;
            var session = actor.RequireSessionCore(sessionId);
            actor.RequireSessionDispatchAvailableCore(sessionId);
            if (work.IsOccupiedCore(session))
                throw KernelException.Conflict($"Session '{sessionId}' already has an active or unresolved turn.");

            var workerSettings = session.BoundAttemptId is null ? null
                : await actor.ReadWorkerSettingsCoreAsync(session, ct).ConfigureAwait(false);
            var now = actor.NowMs();
            var turn = work.CreateTurnCore(session, prompt, workerSettings, now);
            var runningSession = session with { Status = "running", UpdatedAtMs = now };
            var runningEvent = actor.SessionUpdatedEvent(runningSession);
            if (await work.ClaimCoreAsync(session, runningSession, runningEvent, ct).ConfigureAwait(false) is not { } executionSession)
                return null;

            PreparedDispatch prepared;
            try
            {
                prepared = await PrepareDispatchCoreAsync(work, executionSession, runningSession, prompt, ct).ConfigureAwait(false);
                runningSession = runningSession with { ContextUsage = actor.RequireSessionCore(sessionId).ContextUsage };
                if (workerSettings is not null)
                    workerSettings = actor.ResolveWorkerTurnSettingsCore(executionSession, workerSettings);
            }
            catch (Exception ex)
            {
                if (await work.TrySettleSetupFailureCoreAsync(ex, turn, runningSession).ConfigureAwait(false))
                    return null;
                throw;
            }

            turn = await work.RecordTurnStartCoreAsync(turn, prompt, runningSession, ct).ConfigureAwait(false);
            await work.OnTurnCreatedAsync(turn).ConfigureAwait(false);

            actor._sessions[sessionId] = runningSession;
            var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, actor._actorShutdownCts.Token);
            var active = new ActiveTurnState(sessionId, (executionSession.Model, executionSession.ReasoningEffort),
                actor._liveContextUsage.GetOrCreateValue(prepared.AgentSession), turn, prepared.AgentSession, turnCts, true)
            {
                Settings = workerSettings
            };
            actor._scheduler.SetActive(sessionId, active);
            onActivated(active);
            await work.OnTurnActivatedCoreAsync(runningSession, runningEvent, now, ct).ConfigureAwait(false);
            return new TurnStart(active, prepared.EffectivePrompt);
        }, actor._actorShutdownCts.Token);

        private async Task<PreparedDispatch> PrepareDispatchCoreAsync(ITurnWorkItem work, AgentSessionDto session,
            AgentSessionDto runningSession, string prompt, CancellationToken ct)
        {
            if (session.BoundAttemptId is { } workerAttemptId && actor.RequireCurrentWorkerCore(session.Id, workerAttemptId).Attempt.Paused)
                throw KernelException.Conflict("The Worker is paused.");
            if (work.IsQueuedMessage)
                actor.RequireDispatchWorkspace(session);

            var effectivePrompt = await ApplyPolicyContextCoreAsync(session, runningSession, prompt, ct).ConfigureAwait(false);
            var agentSession = await actor.GetOrOpenAgentSessionCoreAsync(session, overrideSystemPrompt: null, ct)
                .ConfigureAwait(false);
            return new PreparedDispatch(agentSession, effectivePrompt);
        }

        private async Task<string> ApplyPolicyContextCoreAsync(AgentSessionDto session, AgentSessionDto runningSession, string prompt,
            CancellationToken ct)
        {
            if (actor._policy is null)
                return prompt;
            var turnCtx = new PolicyTurnContext(
                TrackId: actor.TrackId,
                SessionId: session.Id,
                IsLead: session.IsLead,
                BoundAttemptId: session.BoundAttemptId,
                ContextUsageRatio: actor.CurrentContextUsageRatioCore(runningSession) ?? 0,
                Inputs: actor._track.TemplateInputs);
            var eval = actor._policyVm.BuildTurnContext(actor._policy, turnCtx, actor._track.TemplateState);
            await actor.UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(eval.Output) ? prompt : $"<policy_context>\n{eval.Output}\n</policy_context>\n\n{prompt}";
        }

        private async Task<TurnOutcome> StreamTurnAsync(ITurnWorkItem work, ActiveTurnState active, string effectivePrompt,
            CancellationToken ct)
        {
            var outcome = new TurnOutcome();
            try
            {
                var input = new TurnInput(effectivePrompt, active.TurnDto.Id, active.Settings?.Model, active.Settings?.ReasoningEffort);
                await foreach (var evt in actor.ObserveTurnAsync(active, input, active.TurnCts.Token).ConfigureAwait(false))
                    await HandleEventAsync(work, active, outcome, evt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (active.Interrupted || actor._actorShutdownCts.IsCancellationRequested)
            {
                outcome.Status = "interrupted";
                outcome.ErrorText = "Turn interrupted.";
            }
            catch (OperationCanceledException) when (!work.IsDurable && ct.IsCancellationRequested)
            {
                // The caller stopped waiting. Abandoned-turn cleanup returns the session to idle.
                throw;
            }
            catch (Exception ex)
            {
                outcome.Status = "failed";
                outcome.ErrorText = ex.Message;
            }

            try
            {
                await actor.SealTurn(active).ConfigureAwait(false);
            }
            catch (KernelException) when (active.Interrupted)
            {
                // The Stop caller sees the failure. Settlement records available evidence.
            }

            if (active.Control.HasUnfinishedCancel)
                outcome.Conclusive = false;
            bool stoppedBeforeDispatch;
            lock (active.Control.Sync)
                stoppedBeforeDispatch = active.Control.StoppedBeforeDispatch;
            if (stoppedBeforeDispatch)
            {
                outcome.Conclusive = true;
                outcome.RejectedBeforePrompt = true;
            }

            if (active.Interrupted && (outcome.Status == "completed" || outcome.RejectedBeforePrompt))
            {
                outcome.Status = "interrupted";
                outcome.ErrorText = "Turn interrupted.";
            }

            if ((work.IsDurable || active.Interrupted && active.Settings is null) && !outcome.Conclusive)
            {
                outcome.Status = "unknown";
                outcome.ErrorText ??= "Provider outcome could not be confirmed.";
            }

            return outcome;
        }

        private async Task HandleEventAsync(ITurnWorkItem work, ActiveTurnState active, TurnOutcome outcome, UnifiedAgentEvent evt)
        {
            var now = actor.NowMs();
            await RecordDeliveryEvidenceAsync(work, active, outcome, evt).ConfigureAwait(false);
            switch (evt)
            {
                case UnifiedAgentEvent.ThreadBound tb:
                    outcome.BoundThreadId = tb.NativeThreadId;
                    break;

                case UnifiedAgentEvent.TextDelta td:
                    await FlushThinkingChunkAsync(active, outcome, now).ConfigureAwait(false);
                    outcome.Text.Append(td.Delta);
                    actor.PublishTurnActivity(active,
                        KernelEventKinds.SessionTextDelta,
                        JsonHelpers.ToElement(new { sessionId = active.SessionId, turnId = active.TurnDto.Id, delta = td.Delta }),
                        now);
                    break;

                case UnifiedAgentEvent.ThinkingDelta thd:
                    outcome.Thinking.Append(thd.Delta);
                    actor.PublishTurnActivity(active,
                        KernelEventKinds.SessionThinkingDelta,
                        JsonHelpers.ToElement(new { sessionId = active.SessionId, turnId = active.TurnDto.Id, delta = thd.Delta }),
                        now);
                    break;

                case UnifiedAgentEvent.ToolCallStarted tcs:
                    await FlushThinkingChunkAsync(active, outcome, now).ConfigureAwait(false);
                    actor.PublishTurnActivity(active,
                        KernelEventKinds.SessionToolProgress,
                        JsonHelpers.ToElement(new { sessionId = active.SessionId, turnId = active.TurnDto.Id, phase = "started", callId = tcs.CallId, name = tcs.Name, args = tcs.Args }),
                        now);
                    await RecordActivityAsync(TurnItem(active, "tool_call",
                        JsonHelpers.ToElement(new { v = 1, callId = tcs.CallId, name = tcs.Name, args = tcs.Args }), now)).ConfigureAwait(false);
                    break;

                case UnifiedAgentEvent.ToolCallCompleted tcc:
                    await FlushThinkingChunkAsync(active, outcome, now).ConfigureAwait(false);
                    actor.PublishTurnActivity(active,
                        KernelEventKinds.SessionToolProgress,
                        JsonHelpers.ToElement(new { sessionId = active.SessionId, turnId = active.TurnDto.Id, phase = "completed", callId = tcc.CallId, isError = tcc.IsError, output = active.Settings is null ? null : tcc.Output }),
                        now);
                    await RecordActivityAsync(TurnItem(active, "tool_result",
                        JsonHelpers.ToElement(new { v = 1, callId = tcc.CallId, output = tcc.Output, isError = tcc.IsError }), now)).ConfigureAwait(false);
                    break;

                case UnifiedAgentEvent.ApprovalRequested approval:
                    actor.AdmitProviderApproval(active, approval);
                    break;

                case UnifiedAgentEvent.ContextUsageUpdated contextUsage:
                    await actor.EnqueueAsync(async _ =>
                    {
                        var current = actor.RequireSessionCore(active.SessionId);
                        if (!active.MatchesSessionConfiguration(current))
                            return;
                        var updated = actor.ApplyContextUsageCore(current, active.AgentSession, active.ContextBinding,
                            contextUsage.ContextUsageRevision, contextUsage.Usage, now);
                        if (!ReferenceEquals(updated, current))
                            await actor.PersistAndEmitSessionUpdatedCoreAsync(updated, now, CancellationToken.None).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
                    break;

                case UnifiedAgentEvent.TurnCompleted tc:
                    outcome.Conclusive = true;
                    outcome.FinalUsage = tc.Usage;
                    outcome.FinalUsageRevision = tc.ContextUsageRevision;
                    outcome.CompletedPromptId = tc.CompletedPromptId;
                    break;

                case UnifiedAgentEvent.TurnInterrupted:
                    outcome.Conclusive = true;
                    outcome.Status = "interrupted";
                    outcome.ErrorText = "Provider confirmed the interruption.";
                    break;

                case UnifiedAgentEvent.TurnError te:
                    outcome.RejectedBeforePrompt = te.BeforePrompt;
                    outcome.Conclusive = te.IsConclusive;
                    outcome.Status = "failed";
                    outcome.ErrorText = $"{te.Code}: {te.Message}";
                    break;
            }
        }

        // Any event that proves the provider received the prompt confirms delivery to the work item.
        private async Task RecordDeliveryEvidenceAsync(ITurnWorkItem work, ActiveTurnState active, TurnOutcome outcome,
            UnifiedAgentEvent evt)
        {
            var confirmsDelivery = evt is UnifiedAgentEvent.TurnAccepted or UnifiedAgentEvent.TextDelta or
                UnifiedAgentEvent.ThinkingDelta or UnifiedAgentEvent.ToolCallStarted or
                UnifiedAgentEvent.ToolCallCompleted or UnifiedAgentEvent.ApprovalRequested or
                UnifiedAgentEvent.TurnCompleted or UnifiedAgentEvent.TurnInterrupted or
                UnifiedAgentEvent.TurnError { IsConclusive: true, BeforePrompt: false };
            if (!confirmsDelivery)
                return;

            await work.OnProviderDeliveredAsync(active.TurnDto, evt, outcome.BoundThreadId).ConfigureAwait(false);
            outcome.Delivered = true;
            if (active.ProviderAccepted)
                return;
            await actor.EnqueueAsync(_ =>
            {
                active.ProviderAccepted = true;
                if (work.IsQueuedMessage)
                    actor.NotifyQueueChangedCore(active.SessionId);
                return Task.CompletedTask;
            }, actor._actorShutdownCts.Token).ConfigureAwait(false);
        }

        private async Task FlushThinkingChunkAsync(ActiveTurnState active, TurnOutcome outcome, long now)
        {
            if (outcome.Thinking.Length == 0)
                return;
            var chunk = outcome.Thinking.ToString();
            outcome.Thinking.Clear();
            await RecordActivityAsync(TurnItem(active, "thinking", JsonHelpers.ToElement(new { v = 1, text = chunk }), now))
                .ConfigureAwait(false);
        }

        private Task RecordActivityAsync(SessionItemDto item) =>
            actor._store.AppendSessionItemsAsync(item.SessionId, [item], CancellationToken.None);

        private static SessionItemDto TurnItem(ActiveTurnState active, string kind, JsonElement payload, long now) =>
            new(Id: 0, SessionId: active.SessionId, TurnId: active.TurnDto.Id, Kind: kind, Payload: payload, CreatedAtMs: now);

        // Items stored with the settled turn: unflushed thinking, the assistant text and any failure outcome.
        private static List<SessionItemDto> BuildFinalItems(ActiveTurnState active, TurnOutcome outcome, long now)
        {
            var items = new List<SessionItemDto>();
            if (outcome.Thinking.Length > 0)
                items.Add(TurnItem(active, "thinking", JsonHelpers.ToElement(new { v = 1, text = outcome.Thinking.ToString() }), now));
            if (outcome.Text.Length > 0)
                items.Add(TurnItem(active, "assistant_text", JsonHelpers.ToElement(new { v = 1, text = outcome.Text.ToString() }), now));
            if (outcome.Status is "failed" or "interrupted")
                items.Add(TurnItem(active, "turn_outcome", JsonHelpers.ToElement(new
                {
                    v = 1,
                    status = outcome.Status,
                    error = outcome.ErrorText ?? (outcome.Status == "interrupted" ? "Turn interrupted." : "Turn failed.")
                }), now));
            return items;
        }

        // The work item decides when its turn is persisted by calling the settlement's SettleAsync.
        // Turn is null when the work item never settled.
        private async Task<(SessionTurnDto? Turn, bool ShouldAutoCompact)> SettleTurnAsync(ITurnWorkItem work, ActiveTurnState active,
            string prompt, TurnOutcome outcome)
        {
            var endNow = actor.NowMs();
            var items = BuildFinalItems(active, outcome, endNow);
            (SessionTurnDto? Turn, bool ShouldAutoCompact) settled = default;

            async Task<SessionTurnDto> SettleAsync()
            {
                var result = await actor.EnqueueAsync(_ => SettleCoreAsync(work, active, prompt, outcome, items, endNow),
                    CancellationToken.None).ConfigureAwait(false);
                settled = result;
                return result.Turn;
            }

            await work.OnTurnSettledAsync(new TurnSettlement(active.TurnDto.Id, outcome.Status,
                active.Interrupted, outcome.Delivered, outcome.RejectedBeforePrompt, SettleAsync)).ConfigureAwait(false);
            return settled;
        }

        private async Task<(SessionTurnDto Turn, bool ShouldAutoCompact)> SettleCoreAsync(ITurnWorkItem work, ActiveTurnState active,
            string prompt, TurnOutcome outcome, IReadOnlyList<SessionItemDto> items, long endNow)
        {
            var sessionId = active.SessionId;
            var currentSession = actor.RequireSessionCore(sessionId);
            if (currentSession.BoundAttemptId is { } boundAttemptId && active.Interrupted &&
                actor._activeAttempts.TryGetValue(boundAttemptId, out var pausedAttempt) && pausedAttempt.Paused)
            {
                outcome.Status = "interrupted";
                outcome.ErrorText = "Turn interrupted.";
                await actor.CloseInterruptedWorkerCoreAsync(active, pausedAttempt).ConfigureAwait(false);
            }

            if (active.MatchesSessionConfiguration(currentSession))
            {
                if (outcome.FinalUsage is { } usage)
                    currentSession = actor.ApplyContextUsageCore(currentSession, active.AgentSession, active.ContextBinding,
                        outcome.FinalUsageRevision, usage, endNow);
                currentSession = actor.ApplyTrailingContextUsageCore(currentSession, active.AgentSession, active.ContextBinding,
                    outcome.CompletedPromptId, endNow);
            }

            var updatedSession = currentSession with
            {
                NativeThreadId = outcome.BoundThreadId ?? active.AgentSession.NativeThreadId ?? currentSession.NativeThreadId,
                Status = outcome.Status == "unknown" ? "unknown" : "idle",
                UpdatedAtMs = endNow
            };
            var completedTurn = active.TurnDto with
            {
                Input = active.Settings is null ? active.TurnDto.Input :
                    JsonHelpers.ToElement(new { prompt, workerSettings = active.ProviderAccepted ? active.Settings : null }),
                Status = outcome.Status,
                Usage = outcome.FinalUsage is null ? null : JsonHelpers.ToElement(outcome.FinalUsage),
                ErrorText = outcome.ErrorText,
                CompletedAtMs = outcome.Status == "unknown" ? null : endNow
            };

            await UpdateResumeHoldCoreAsync(work, active, outcome.Conclusive, updatedSession).ConfigureAwait(false);
            await work.PersistSettlementCoreAsync(outcome.Delivered, outcome.RejectedBeforePrompt, completedTurn, items, updatedSession)
                .ConfigureAwait(false);

            actor._scheduler.RemoveActive(sessionId);
            active.Settled.TrySetResult(outcome.Conclusive);
            var settledSession = actor._sessions[sessionId];
            var autoCompact = !actor._scheduler.HasResumeHold(sessionId) && settledSession.BoundAttemptId is null &&
                              settledSession.Status == "idle" && outcome.Status == "completed" &&
                              actor.CurrentContextUsageRatioCore(updatedSession) >= actor._options.AutoCompactionThreshold;
            return (completedTurn, autoCompact);
        }

        // An inconclusive queued follow-up holds the session for resume. A conclusive outcome quiesces the hold
        // this turn created.
        private async Task UpdateResumeHoldCoreAsync(ITurnWorkItem work, ActiveTurnState active, bool conclusive, AgentSessionDto updatedSession)
        {
            var sessionId = updatedSession.Id;
            var turnId = active.TurnDto.Id;
            if (!conclusive && (work.HoldsResumeWhenInconclusive || active.Interrupted && active.Settings is null) &&
                !actor._scheduler.HasResumeHold(sessionId))
                await actor.CreateSessionResumeHoldCoreAsync(sessionId, turnId,
                    updatedSession.NativeThreadId ?? string.Empty, CancellationToken.None).ConfigureAwait(false);

            if (conclusive && actor._scheduler.TryGetResumeHold(sessionId, out var recovery) && recovery.TurnId == turnId)
                await actor.MarkSessionResumeQuiescentCoreAsync(sessionId, recovery, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task TryAutoCompactAsync(string sessionId)
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

        // Runs on every exit once the turn was activated. It only acts on a turn that is still the session's
        // active turn, which means the turn never settled.
        private async Task CleanUpAbandonedTurnAsync(ITurnWorkItem work, ActiveTurnState active)
        {
            ObserveTaskFault(actor.SealTurn(active));
            try
            {
                if (!actor._actorShutdownCts.IsCancellationRequested)
                    await actor.EnqueueAsync(async _ =>
                    {
                        if (!actor._scheduler.TryGetActive(active.SessionId, out var current) || !ReferenceEquals(current, active))
                            return;
                        if (active.Interrupted && active.Settings is null && !HasStopEvidence(active))
                        {
                            var session = actor.RequireSessionCore(active.SessionId);
                            if (!actor._scheduler.HasResumeHold(active.SessionId))
                                await actor.CreateSessionResumeHoldCoreAsync(active.SessionId, active.TurnDto.Id,
                                    active.AgentSession.NativeThreadId ?? session.NativeThreadId ?? string.Empty,
                                    CancellationToken.None).ConfigureAwait(false);
                            if (!work.IsDurable)
                            {
                                var unknown = session with { Status = "unknown", UpdatedAtMs = actor.NowMs() };
                                await actor.PersistAndEmitSessionUpdatedCoreAsync(unknown, unknown.UpdatedAtMs, CancellationToken.None)
                                    .ConfigureAwait(false);
                            }
                            actor._scheduler.RemoveActive(active.SessionId);
                            return;
                        }
                        actor._scheduler.RemoveActive(active.SessionId);
                        await work.OnTurnAbandonedCoreAsync().ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                active.Settled.TrySetResult(false);
                active.TurnCts.Dispose();
            }
        }
    }
}
