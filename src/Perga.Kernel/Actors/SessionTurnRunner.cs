using System.Text;
using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private sealed class SessionTurnRunner(TrackActor actor)
    {
        private sealed record TurnStart(ActiveTurnState Active, string EffectivePrompt);

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

        public async Task<SessionTurnDto?> RunAsync(ITurnWorkItem work, CancellationToken ct, bool alreadyAdmitted = false, CancellationToken? startupCancellation = null, Action<int>? compactedEpoch = null)
        {
            var preparationCt = startupCancellation ?? ct;
            if (work.WaitForWorkspacePreparation)
                await actor.WaitForWorkspacePreparationAsync(preparationCt).ConfigureAwait(false);
            using var dispatch = alreadyAdmitted ? null : await actor._options.DispatchAdmission.EnterAsync(preparationCt).ConfigureAwait(false);
            if (await work.PreparePromptAsync(preparationCt).ConfigureAwait(false) is not { } prompt)
                return null;

            Interlocked.Increment(ref actor._inFlightExternalOps);
            ActiveTurnState? activated = null;
            try
            {
                var policyAdmission = await actor.PreparePolicyTurnAdmissionAsync(work.SessionId, preparationCt).ConfigureAwait(false);
                if (await StartTurnAsync(work, prompt, policyAdmission, active => activated = active, ct).ConfigureAwait(false) is not { } start)
                    return work.CreateUnstartedTurn();

                var outcome = await StreamTurnAsync(work, start.Active, start.EffectivePrompt, ct).ConfigureAwait(false);
                var settled = await SettleTurnAsync(work, start.Active, prompt, outcome).ConfigureAwait(false);
                if (settled.ShouldAutoCompact)
                {
                    var epoch = await TryAutoCompactAsync(work.SessionId).ConfigureAwait(false);
                    if (epoch is { } value) compactedEpoch?.Invoke(value);
                }
                await actor.EnqueueAsync(async _ =>
                {
                    if (actor.RequireSessionCore(work.SessionId).Epoch == start.Active.TurnDto.Epoch)
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

        // Claim and activate in the mailbox; provider startup runs between those steps
        // so an authorized stop can cancel it without waiting behind provider I/O.
        private async Task<TurnStart?> StartTurnAsync(ITurnWorkItem work, string prompt, TurnPolicyAdmission policyAdmission,
            Action<ActiveTurnState> onActivated, CancellationToken ct)
        {
            var claim = await actor.EnqueueAsync(async _ =>
            {
                await actor.EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var session = actor.RequireSessionCore(work.SessionId);
                actor.RequireSessionDispatchAvailableCore(session.Id);
                if (work.IsOccupiedCore(session))
                    throw KernelException.Conflict($"Session '{session.Id}' already has an active or unresolved turn.");
                var turn = work.CreateTurnCore(session, prompt, actor.NowMs());
                var running = session with { Status = "running", UpdatedAtMs = actor.NowMs() };
                var execution = await work.ClaimCoreAsync(session, running, actor.SessionUpdatedEvent(running), ct).ConfigureAwait(false);
                if (execution is null) return ((SessionTurnDto Turn, AgentSessionDto Session, CancellationTokenSource Startup)?)null;
                var startup = actor._runtimes.Get(session.Id).BeginLaunch(actor._actorShutdownCts.Token, turn.Id);
                startup.CancelAfter(actor._options.SessionStartupTimeout);
                return (turn, execution, startup);
            }, actor._actorShutdownCts.Token).ConfigureAwait(false);
            if (claim is not { } claimed) return null;
            using var startupDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct, claimed.Startup.Token);
            IAgentSession? startupAgent = null;
            long? priorGeneration = actor._runtimes.Get(work.SessionId).Snapshot?.Generation;
            try
            {
                await actor.EnqueueAsync(_ =>
                {
                    if (actor._taskCoordinator.IsSessionFrozenForDispatchCore(claimed.Session))
                        throw KernelException.Conflict("The session is no longer accepting execution.");
                    if (actor._taskCoordinator.IsSessionAutoDrainPausedCore(claimed.Session.Id))
                        throw KernelException.Conflict("The task worker is paused.");
                    if (work.IsQueuedMessage) actor._taskCoordinator.RequireDispatchWorkspace(claimed.Session);
                    if (policyAdmission.Failure is { } failure) throw failure;
                    if (actor._policyGeneration != policyAdmission.Generation)
                        throw KernelException.PolicyDenied("Active policy changed during turn authorization.");
                    return Task.CompletedTask;
                }, actor._actorShutdownCts.Token).ConfigureAwait(false);
                startupDeadline.Token.ThrowIfCancellationRequested();
                var agent = await actor.GetOrOpenAgentSessionCoreAsync(claimed.Session, null, startupDeadline.Token).ConfigureAwait(false);
                startupAgent = agent;
                return await actor.EnqueueAsync(async _ =>
                {
                    claimed.Startup.Token.ThrowIfCancellationRequested();
                    startupDeadline.Token.ThrowIfCancellationRequested();
                    var current = actor.RequireSessionCore(work.SessionId);
                    actor.RequireSessionDispatchAvailableCore(work.SessionId);
                    if (actor._taskCoordinator.IsSessionFrozenForDispatchCore(current) || actor._taskCoordinator.IsSessionAutoDrainPausedCore(current.Id))
                        throw KernelException.Conflict("The session changed during provider startup.");
                    if (current.Epoch != claimed.Session.Epoch ||
                        actor._policyGeneration != policyAdmission.Generation)
                        throw KernelException.Conflict("The session changed during provider startup.");
                    if (work.FinalizePromptCore(prompt) is not { } finalPrompt) return null;
                    prompt = finalPrompt;
                    var running = current with { Status = "running", UpdatedAtMs = actor.NowMs() };
                    var evt = actor.SessionUpdatedEvent(running);
                    var turn = await work.RecordTurnStartCoreAsync(claimed.Turn, prompt, running, ct).ConfigureAwait(false);
                    await work.OnTurnCreatedAsync(turn).ConfigureAwait(false);
                    actor._sessions[running.Id] = running;
                    var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, actor._actorShutdownCts.Token);
                    var active = new ActiveTurnState(running.Id,
                        SessionConfiguration.FromNullable(claimed.Session.Model, claimed.Session.ReasoningEffort, claimed.Session.PermissionMode),
                        actor._runtimes.Get(running.Id).ContextUsage, turn, agent, turnCts, true);
                    try
                    {
                        actor._scheduler.SetActive(running.Id, active);
                    }
                    catch
                    {
                        turnCts.Dispose();
                        throw;
                    }
                    onActivated(active);
                    await work.OnTurnActivatedCoreAsync(running, evt, running.UpdatedAtMs, ct).ConfigureAwait(false);
                    var effective = string.IsNullOrWhiteSpace(policyAdmission.Context) ? prompt :
                        $"<policy_context>\n{policyAdmission.Context}\n</policy_context>\n\n{prompt}";
                    return new TurnStart(active, effective);
                }, actor._actorShutdownCts.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Registration can fail before returning the newly opened agent to this caller.
                // A retained unconfirmed handle already went through startup cleanup.
                if (startupAgent is not null ||
                    actor._runtimes.TryGetOpen(work.SessionId, out var opened) && opened.Generation != priorGeneration &&
                    !actor._runtimes.IsShutdownUnconfirmed(work.SessionId))
                {
                    try { await actor._runtimes.Get(work.SessionId).CloseAsync(actor._options.SessionStartupTimeout, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* Setup settlement records unconfirmed shutdown. */ }
                }
                var settled = await actor.EnqueueAsync(_ => work.TrySettleSetupFailureCoreAsync(error, claimed.Turn,
                    actor.RequireSessionCore(work.SessionId)), CancellationToken.None).ConfigureAwait(false);
                if (settled) return null;
                throw;
            }
            finally
            {
                await actor.EnqueueAsync(_ =>
                {
                    actor._runtimes.Get(work.SessionId).EndLaunch(claimed.Startup);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task<TurnOutcome> StreamTurnAsync(ITurnWorkItem work, ActiveTurnState active, string effectivePrompt,
            CancellationToken ct)
        {
            var outcome = new TurnOutcome();
            try
            {
                IReadOnlyList<InputImageRef>? stagedImages = null;
                if (work.Attachments.Count > 0)
                {
                    if (!active.AgentSession.SupportsImages)
                    {
                        outcome.RejectedBeforePrompt = true;
                        outcome.Conclusive = true;
                        outcome.Status = "failed";
                        outcome.ErrorText = "unsupported: The active runtime does not support image attachments.";
                    }
                    else
                    {
                        var list = new List<InputImageRef>(work.Attachments.Count);
                        try
                        {
                            foreach (var att in work.Attachments)
                            {
                                var localPath = att.PhysicalPath;
                                if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                                {
                                    outcome.RejectedBeforePrompt = true;
                                    outcome.Conclusive = true;
                                    outcome.Status = "failed";
                                    outcome.ErrorText = $"attachment_missing: Image attachment '{att.Id}' could not be read from storage.";
                                    break;
                                }

                                var ext = SessionAttachmentValidation.ExtensionForContentType(att.ContentType)
                                    ?? throw new InvalidOperationException($"Unsupported image content type '{att.ContentType}'.");
                                var stagedPath = await actor.Env.Files
                                    .StageAttachmentAsync(actor.TrackId, att.Id, ext, localPath, att.Sha256, active.TurnCts.Token)
                                    .ConfigureAwait(false);
                                list.Add(new InputImageRef(stagedPath, att.ContentType));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception stageEx)
                        {
                            outcome.RejectedBeforePrompt = true;
                            outcome.Conclusive = true;
                            outcome.Status = "failed";
                            outcome.ErrorText = $"attachment_stage_failed: {stageEx.Message}";
                        }

                        if (!outcome.RejectedBeforePrompt)
                        {
                            stagedImages = list;
                        }
                    }
                }

                if (!outcome.RejectedBeforePrompt)
                {
                    var input = new TurnInput(
                        effectivePrompt,
                        active.TurnDto.Id,
                        active.AgentSession.ConfigurationState?.NativeModel ?? active.Configuration.Model.ValueOrNull,
                        active.AgentSession.ConfigurationState?.NativeEffort ?? active.Configuration.Effort.ValueOrNull,
                        stagedImages);
                    await foreach (var evt in actor.ObserveTurnAsync(active, input, active.TurnCts.Token).ConfigureAwait(false))
                        await HandleEventAsync(work, active, outcome, evt).ConfigureAwait(false);
                }
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

            if ((work.IsDurable || active.Interrupted) && !outcome.Conclusive)
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
                        JsonHelpers.ToElement(new { sessionId = active.SessionId, turnId = active.TurnDto.Id, phase = "completed", callId = tcc.CallId, isError = tcc.IsError, output = tcc.Output }),
                        now);
                    await RecordActivityAsync(TurnItem(active, "tool_result",
                        JsonHelpers.ToElement(new { v = 1, callId = tcc.CallId, output = tcc.Output, isError = tcc.IsError }), now)).ConfigureAwait(false);
                    break;

                case UnifiedAgentEvent.ApprovalRequested approval:
                    await actor.EnqueueAsync(_ =>
                    {
                        actor.AdmitProviderApproval(active, approval);
                        return Task.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(false);
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

            if (active.Interrupted && active.CloseRuntimeOnStop)
            {
                try
                {
                    await actor._runtimes.Get(active.SessionId).CloseAsync(actor._options.SessionStartupTimeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* SessionRuntime retains ownership and records unconfirmed shutdown. */ }
                var unconfirmed = actor._runtimes.IsShutdownUnconfirmed(active.SessionId);
                outcome.Status = unconfirmed ? "unknown" : "interrupted";
                outcome.ErrorText = "Turn interrupted.";
                outcome.Conclusive = !unconfirmed;
            }

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
                UnknownReason = outcome.Status == "unknown" ? (actor._runtimes.IsShutdownUnconfirmed(sessionId) ? "shutdown_unconfirmed" : active.Control.StopTimedOut ? "stop_timeout" : "stream_lost") : null,
                UnknownTurnId = outcome.Status == "unknown" ? active.TurnDto.Id : null,
                UnknownLastError = null, ReconnectInvalidated = false,
                UpdatedAtMs = endNow,
                LastNotifiableTurnAtMs = outcome.Status is "completed" or "failed"
                    ? Math.Max(currentSession.LastNotifiableTurnAtMs ?? 0, endNow)
                    : currentSession.LastNotifiableTurnAtMs
            };
            var completedTurn = active.TurnDto with
            {
                Status = outcome.Status,
                Usage = outcome.FinalUsage is null ? null : JsonHelpers.ToElement(outcome.FinalUsage),
                ErrorText = outcome.ErrorText,
                CompletedAtMs = outcome.Status == "unknown" ? null : endNow
            };

            await work.PersistSettlementCoreAsync(outcome.Delivered, outcome.RejectedBeforePrompt, completedTurn, items, updatedSession)
                .ConfigureAwait(false);

            await actor.AfterPolicyEventCoreAsync("turn.after", JsonHelpers.ToElement(new {
                sessionId, turnId = completedTurn.Id, status = outcome.Status,
                isLead = currentSession.IsLead, boundTaskId = actor._taskCoordinator.TryGetActiveTaskForSessionCore(sessionId)?.Task.Id
            }), CancellationToken.None).ConfigureAwait(false);
            actor._scheduler.RemoveActive(sessionId);
            if (actor._taskCoordinator.TryGetActiveOwningTaskCore(sessionId) is { Status: TaskRunStatus.Pending } ||
                actor.HasPendingFlowTaskForSessionCore(sessionId))
                await actor._taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
            await actor.NotifyParentCoreAsync(updatedSession, "turn_end", "turn:" + completedTurn.Id,
                $"Child session '{sessionId}' turn '{completedTurn.Id}' ended as {completedTurn.Status}.").ConfigureAwait(false);
            active.Settled.TrySetResult(outcome.Conclusive);
            var settledSession = actor._sessions[sessionId];
            var autoCompact = !actor._taskCoordinator.IsSessionFrozenForDispatchCore(settledSession) &&
                              !actor._taskCoordinator.IsSessionAutoDrainPausedCore(sessionId) &&
                              settledSession.Status == "idle" && outcome.Status == "completed" &&
                              actor.CurrentContextUsageRatioCore(settledSession) >= actor._options.AutoCompactionThreshold;
            return (completedTurn, autoCompact);
        }

        private async Task<int?> TryAutoCompactAsync(string sessionId)
        {
            try
            {
                return (await actor.CompactSessionInternalAsync(sessionId, CancellationToken.None, automatic: true).ConfigureAwait(false)).Epoch;
            }
            catch (KernelException ex) when (ex.Code == KernelErrorCode.Conflict)
            {
                // A new turn reserved the session before optional compaction.
                return null;
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
                        if (active.Interrupted && !active.CloseRuntimeOnStop && !HasStopEvidence(active))
                        {
                            var session = actor.RequireSessionCore(active.SessionId);
                            if (!work.IsDurable)
                            {
                                var unknown = session with { Status = "unknown", UnknownReason = "stream_lost", UnknownTurnId = active.TurnDto.Id, UpdatedAtMs = actor.NowMs() };
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
