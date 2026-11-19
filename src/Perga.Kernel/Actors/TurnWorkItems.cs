using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private sealed record TurnSettlement(string TurnId, string Status, bool Interrupted, bool Delivered,
        bool RejectedBeforePrompt, Func<Task<SessionTurnDto>> SettleAsync);

    // One unit of work the SessionTurnRunner dispatches as a provider turn. Members ending in Core run on
    // the actor while the runner starts or settles the turn.
    private interface ITurnWorkItem
    {
        string SessionId { get; }
        string TriggerKind { get; }

        // A durable input was accepted and stored before dispatch. Its provider outcome must be confirmed,
        // so an inconclusive stream settles as unknown, and caller cancellation never abandons it.
        bool IsDurable { get; }

        // A queued session message needs the dispatch workspace, and the provider accepting it changes the
        // visible queue.
        bool IsQueuedMessage { get; }

        // True when RunAsync must wait for workspace preparation before entering DispatchAdmission. Reserved work
        // waits here; durable inputs already waited in TrackActor.SessionInputs before TryDispatch, so they say false.
        bool WaitForWorkspacePreparation { get; }

        // A queued follow-up whose outcome is inconclusive holds the session until its resume is resolved.
        bool HoldsResumeWhenInconclusive { get; }

        // Returns null when nothing remains to dispatch.
        Task<string?> PreparePromptAsync(CancellationToken ct);

        bool IsOccupiedCore(AgentSessionDto session);
        SessionTurnDto CreateTurnCore(AgentSessionDto session, string prompt, WorkerSettings? workerSettings, long now);

        // Returns the session that executes the turn, or null when the turn must not start.
        Task<AgentSessionDto?> ClaimCoreAsync(AgentSessionDto session, AgentSessionDto runningSession,
            EventEnvelopeDto runningEvent, CancellationToken ct);

        // Returns true when the failure was settled and the turn stays unstarted. Otherwise the runner rethrows.
        Task<bool> TrySettleSetupFailureCoreAsync(Exception ex, SessionTurnDto turn, AgentSessionDto runningSession);

        Task<SessionTurnDto> RecordTurnStartCoreAsync(SessionTurnDto turn, string prompt, AgentSessionDto runningSession,
            CancellationToken ct);
        Task OnTurnCreatedAsync(SessionTurnDto turn);
        Task OnTurnActivatedCoreAsync(AgentSessionDto runningSession, EventEnvelopeDto runningEvent, long now, CancellationToken ct);
        SessionTurnDto CreateUnstartedTurn();
        Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId);
        Task PersistSettlementCoreAsync(bool delivered, bool rejectedBeforePrompt, SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
            AgentSessionDto session);
        Task OnTurnSettledAsync(TurnSettlement settlement);

        // The turn left the runner while still active, without settling.
        Task OnTurnAbandonedCoreAsync();
    }

    // The durable input carries one turn containing one or more user messages.
    private sealed class SessionInputBatchWorkItem(TrackActor actor, PersistedSessionInput input) : ITurnWorkItem
    {
        private bool _delivered;
        public string SessionId => input.Receipt.SessionId;
        public string TriggerKind => "user";
        public bool IsDurable => true;
        public bool IsQueuedMessage => true;
        public bool WaitForWorkspacePreparation => false;
        public bool HoldsResumeWhenInconclusive => input.IsQueuedFollowUp;
        public Task<string?> PreparePromptAsync(CancellationToken ct) => Task.FromResult<string?>(input.Content);
        public bool IsOccupiedCore(AgentSessionDto session) => actor._scheduler.IsOccupied(session, input);

        public SessionTurnDto CreateTurnCore(AgentSessionDto session, string prompt, WorkerSettings? workerSettings, long now) =>
            new(Id: input.Receipt.TurnId, SessionId: session.Id, TurnIndex: input.TurnIndex, Epoch: input.Epoch,
                TriggerKind: TriggerKind, Input: JsonHelpers.ToElement(new { }), Status: "running", Usage: null,
                ErrorText: null, StartedAtMs: now, CompletedAtMs: null, UserItemId: input.UserItemId);

        public async Task<AgentSessionDto?> ClaimCoreAsync(AgentSessionDto session, AgentSessionDto runningSession,
            EventEnvelopeDto runningEvent, CancellationToken ct)
        {
            if (actor._track.ClosedAtMs is not null)
                return null;
            await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, ct).ConfigureAwait(false);
            if (!await actor._store.SessionInputs.ClaimAsync(input, runningSession, runningEvent, ct).ConfigureAwait(false))
                return null;
            actor._sessions[session.Id] = runningSession;
            // The turn executes the selection frozen when the input was accepted.
            return session with { Provider = input.Provider, Model = input.Model, ReasoningEffort = input.ReasoningEffort };
        }

        public async Task<bool> TrySettleSetupFailureCoreAsync(Exception ex, SessionTurnDto turn, AgentSessionDto runningSession)
        {
            var failed = turn with { Status = "failed", ErrorText = "Message could not be prepared for dispatch.", CompletedAtMs = actor.NowMs() };
            var idle = actor.RequireSessionCore(SessionId) with { Status = "idle", UpdatedAtMs = failed.CompletedAtMs.Value };
            var outcome = new SessionItemDto(0, SessionId, turn.Id, "turn_outcome",
                JsonHelpers.ToElement(new { v = 1, status = "failed", error = failed.ErrorText }), idle.UpdatedAtMs);
            await actor.SettleDurableTurnCoreAsync(input, "rejected", failed, [outcome], idle).ConfigureAwait(false);
            await actor.PokeSessionCoreAsync(SessionId).ConfigureAwait(false);
            return true;
        }

        // Claiming already moved the stored turn to running.
        public Task<SessionTurnDto> RecordTurnStartCoreAsync(SessionTurnDto turn, string prompt, AgentSessionDto runningSession,
            CancellationToken ct) => Task.FromResult(turn);

        public Task OnTurnCreatedAsync(SessionTurnDto turn) => Task.CompletedTask;

        public Task OnTurnActivatedCoreAsync(AgentSessionDto runningSession, EventEnvelopeDto runningEvent, long now,
            CancellationToken ct) => actor.PublishInputEventAsync(runningEvent);

        public SessionTurnDto CreateUnstartedTurn() =>
            new(input.Receipt.TurnId, SessionId, input.TurnIndex, input.Epoch, TriggerKind,
                JsonHelpers.ToElement(new { }), "queued", null, null, null, null);

        public async Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId)
        {
            if (_delivered && evt is not UnifiedAgentEvent.TurnAccepted) return;
            await actor._store.SessionInputs.MarkDeliveredAsync(input, nativeThreadId,
                (evt as UnifiedAgentEvent.TurnAccepted)?.NativeTurnId, CancellationToken.None).ConfigureAwait(false);
            _delivered = true;
        }

        public Task PersistSettlementCoreAsync(bool delivered, bool rejectedBeforePrompt, SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
            AgentSessionDto session) =>
            actor.SettleDurableTurnCoreAsync(input, delivered ? "delivered" : rejectedBeforePrompt ? "rejected" : "unknown",
                turn, items, session);

        public async Task OnTurnSettledAsync(TurnSettlement settlement) => await settlement.SettleAsync().ConfigureAwait(false);

        // Recovery resolves a stored running turn that never settled.
        public Task OnTurnAbandonedCoreAsync() => Task.CompletedTask;
    }

    // A reserved turn runs under the session reservation its caller holds and records its own turn once it starts.
    private abstract class ReservedTurnWorkItem(TrackActor actor, string sessionId, Guid reservation) : ITurnWorkItem
    {
        protected TrackActor Actor { get; } = actor;
        public string SessionId { get; } = sessionId;
        public abstract string TriggerKind { get; }
        public bool IsDurable => false;
        public virtual bool IsQueuedMessage => false;
        public bool WaitForWorkspacePreparation => true;
        public bool HoldsResumeWhenInconclusive => false;
        protected virtual SessionMessage? DispatchMessage => null;
        protected virtual string? Presentation => null;

        // A Conflict during setup normally leaves no turn behind.
        protected virtual bool RecordsSetupConflicts => false;

        public abstract Task<string?> PreparePromptAsync(CancellationToken ct);
        public bool IsOccupiedCore(AgentSessionDto session) => Actor._scheduler.IsOccupied(session, reservation: reservation);

        public SessionTurnDto CreateTurnCore(AgentSessionDto session, string prompt, WorkerSettings? workerSettings, long now)
        {
            var turnIndex = Actor.NextTurnIndexCore(session.Id);
            return new SessionTurnDto(Id: $"trn_{turnIndex:D6}_{Guid.NewGuid():N}", SessionId: session.Id, TurnIndex: turnIndex,
                Epoch: session.Epoch, TriggerKind: TriggerKind, Input: JsonHelpers.ToElement(new { prompt, workerSettings }),
                Status: "running", Usage: null, ErrorText: null, StartedAtMs: now, CompletedAtMs: null);
        }

        public Task<AgentSessionDto?> ClaimCoreAsync(AgentSessionDto session, AgentSessionDto runningSession,
            EventEnvelopeDto runningEvent, CancellationToken ct) => Task.FromResult<AgentSessionDto?>(session);

        public async Task<bool> TrySettleSetupFailureCoreAsync(Exception ex, SessionTurnDto turn, AgentSessionDto runningSession)
        {
            if (ex is KernelException { Code: KernelErrorCode.Conflict } && !RecordsSetupConflicts)
                return false;
            var failed = turn with { Status = "failed", ErrorText = ex.Message, CompletedAtMs = Actor.NowMs() };
            var idle = Actor.RequireSessionCore(SessionId) with { Status = "idle", UpdatedAtMs = failed.CompletedAtMs.Value };
            await Actor._store.RecordTurnAndItemsAsync(failed, [], idle, CancellationToken.None).ConfigureAwait(false);
            Actor._sessions[SessionId] = idle;
            await OnTurnCreatedAsync(failed).ConfigureAwait(false);
            return false;
        }

        public async Task<SessionTurnDto> RecordTurnStartCoreAsync(SessionTurnDto turn, string prompt,
            AgentSessionDto runningSession, CancellationToken ct)
        {
            var userItem = CreateWakeUserItem(turn, prompt, DispatchMessage?.Id, presentation: Presentation);
            var recorded = await Actor._store.RecordTurnAndItemsWithIdsAsync(turn, [userItem], runningSession, ct, DispatchMessage)
                .ConfigureAwait(false);
            return recorded.Turn;
        }

        public virtual Task OnTurnCreatedAsync(SessionTurnDto turn) => Task.CompletedTask;

        // The active turn now owns the session, so the reservation is handed over.
        public async Task OnTurnActivatedCoreAsync(AgentSessionDto runningSession, EventEnvelopeDto runningEvent, long now,
            CancellationToken ct)
        {
            Actor._scheduler.ReleaseReservation(SessionId);
            await Actor.PersistAndEmitSessionUpdatedCoreAsync(runningSession, now, ct).ConfigureAwait(false);
        }

        public SessionTurnDto CreateUnstartedTurn() =>
            throw new InvalidOperationException("A reserved turn either starts or fails its setup.");

        public abstract Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId);

        public Task PersistSettlementCoreAsync(bool delivered, bool rejectedBeforePrompt, SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
            AgentSessionDto session) => Actor.CompleteWakeTurnCoreAsync(turn, items, session);

        public abstract Task OnTurnSettledAsync(TurnSettlement settlement);

        // Return the abandoned session to idle so queued work can run.
        public async Task OnTurnAbandonedCoreAsync()
        {
            var idle = Actor.RequireSessionCore(SessionId) with { Status = "idle", UpdatedAtMs = Actor.NowMs() };
            await Actor.PersistAndEmitSessionUpdatedCoreAsync(idle, idle.UpdatedAtMs, CancellationToken.None).ConfigureAwait(false);
            Actor._sessions[SessionId] = idle;
            await Actor.PokeSessionCoreAsync(SessionId).ConfigureAwait(false);
        }
    }

    private sealed class WorkerMessageWorkItem(TrackActor actor, SessionMessage message, ActiveAttemptState attempt, Guid reservation)
        : ReservedTurnWorkItem(actor, message.SessionId, reservation)
    {
        private bool _delivered;
        public override string TriggerKind => "user";
        public override bool IsQueuedMessage => true;
        protected override SessionMessage DispatchMessage => message;

        public override Task<string?> PreparePromptAsync(CancellationToken ct)
        {
            attempt.Cts.Token.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(message.Content);
        }

        public override async Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId)
        {
            if (_delivered) return;
            await Actor.Messages.TransitionAsync(message with { TurnId = turn.Id, DeliveredTurnId = turn.Id, Delivery = "delivered" },
                "dispatching", CancellationToken.None).ConfigureAwait(false);
            _delivered = true;
        }

        public override async Task OnTurnSettledAsync(TurnSettlement settlement)
        {
            if (!_delivered)
                await Actor.Messages.TransitionAsync(message with
                {
                    TurnId = settlement.TurnId,
                    Delivery = settlement.Status == "interrupted" ? "interrupted" : settlement.RejectedBeforePrompt ? "rejected" : "unknown"
                },
                    "dispatching", CancellationToken.None).ConfigureAwait(false);
            await settlement.SettleAsync().ConfigureAwait(false);
        }
    }

    private sealed class TrackWakeBatchWorkItem(TrackActor actor, string sessionId, IReadOnlyList<TrackWake> batch, Guid reservation)
        : ReservedTurnWorkItem(actor, sessionId, reservation)
    {
        public override string TriggerKind
        {
            get
            {
                if (DispatchBatch.Count != 1) return "system";
                if (TrackWakeRequests.IsSystemUpgradeMail(DispatchBatch[0])) return "system";
                return DispatchBatch[0].Kind switch
                {
                    TrackWakeKinds.Schedule => "scheduler",
                    TrackWakeKinds.Boot => "system",
                    _ => "wake"
                };
            }
        }
        protected override string? Presentation
        {
            get
            {
                if (DispatchBatch.Count != 1) return TrackWakePresentations.System;
                var payload = _preparedPayloads is { Count: 1 } prepared ? prepared[0] : TrackWakePayload.Parse(DispatchBatch[0].PayloadJson);
                return payload.Presentation ?? TrackWakePresentations.System;
            }
        }

        // A Conflict thrown by PrepareDispatchCoreAsync (for example a provider-open conflict or a paused Worker) still
        // records a failed turn, so claimed wakes link to it. That runs after the occupancy check has passed; an
        // occupied session is rejected earlier and records nothing.
        protected override bool RecordsSetupConflicts => true;
        private bool _acked;
        private string? _turnId;
        private IReadOnlyList<TrackWake>? _preparedBatch;
        private IReadOnlyList<TrackWakePayload>? _preparedPayloads;
        private IReadOnlyList<TrackWake> DispatchBatch => _preparedBatch ?? batch;

        // Wakes whose prompt is blank are suppressed. With none left the batch is acknowledged without a turn.
        public override async Task<string?> PreparePromptAsync(CancellationToken ct)
        {
            var prompts = new List<string>();
            var dispatch = new List<TrackWake>();
            var payloads = new List<TrackWakePayload>();
            foreach (var wake in batch)
            {
                var payload = TrackWakePayload.Parse(wake.PayloadJson);
                var prompt = await Actor.PrepareWakePromptAsync(wake, payload, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(prompt))
                    await Actor._store.TrackWakes.MarkDroppedAsync([wake], TrackWakeReasons.Canceled, null, CancellationToken.None).ConfigureAwait(false);
                else
                {
                    prompts.Add(prompt);
                    dispatch.Add(wake);
                    payloads.Add(payload);
                }
            }
            _preparedBatch = dispatch;
            _preparedPayloads = payloads;
            if (dispatch.Count == 0)
            {
                _acked = true;
                return null;
            }
            return string.Join("\n\n", prompts);
        }
        public override async Task OnTurnCreatedAsync(SessionTurnDto turn)
        {
            _turnId = turn.Id;
            await Actor._store.TrackWakes.LinkTurnAsync(DispatchBatch, turn.Id, CancellationToken.None).ConfigureAwait(false);
        }
        public override async Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId)
        {
            _turnId = turn.Id;
            if (_acked) return;
            var reason = evt switch
            {
                UnifiedAgentEvent.TurnError => TrackWakeReasons.ConclusiveError,
                UnifiedAgentEvent.TurnInterrupted => TrackWakeReasons.Interrupted,
                _ => TrackWakeReasons.Delivered
            };
            await Actor._store.TrackWakes.MarkDoneAsync(DispatchBatch, reason, turn.Id, CancellationToken.None).ConfigureAwait(false);
            _acked = true;
        }
        public override async Task OnTurnSettledAsync(TurnSettlement settlement)
        {
            _turnId = settlement.TurnId;
            if (!_acked && (settlement.Interrupted || settlement.Status == "interrupted"))
            {
                await Actor._store.TrackWakes.MarkDoneAsync(DispatchBatch, TrackWakeReasons.Interrupted, settlement.TurnId, CancellationToken.None).ConfigureAwait(false);
                _acked = true;
            }
            if (!_acked) await FailBeforeSettlementAsync().ConfigureAwait(false);
            var turn = await settlement.SettleAsync().ConfigureAwait(false);
            await NotifyUpgradeOutcomeAsync(turn.Status, turn.ErrorText).ConfigureAwait(false);
        }
        public async Task NotifyUpgradeOutcomeAsync(string? status, string? error)
        {
            if (Actor._store is not ISystemUpgradeStore upgrades) return;
            foreach (var wake in DispatchBatch)
            {
                if (!TrackWakeRequests.TryGetSystemUpgradeMailId(wake, out var mailId)) continue;
                // Once acked, the wake is already 'done' and will never be retried, so a later turn
                // failure must not leave the receipt stuck in delivery_failed.
                if (_acked)
                    await upgrades.AcknowledgeUpgradeNotificationAsync(mailId, CancellationToken.None).ConfigureAwait(false);
                else
                    await upgrades.MarkUpgradeNotificationFailedAsync(mailId, error ?? status ?? "Wake setup failed.", CancellationToken.None).ConfigureAwait(false);
            }
        }
        public async Task FailBeforeSettlementAsync()
        {
            if (_acked) return;
            if (_turnId is not null)
                await Actor._store.TrackWakes.LinkTurnAsync(DispatchBatch, _turnId, CancellationToken.None).ConfigureAwait(false);
            await Actor._store.TrackWakes.MarkDroppedAsync(DispatchBatch.Where(w => !w.IsCritical).ToArray(),
                TrackWakeReasons.RuntimeFailed, _turnId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Attempt startup is a reserved turn, never queued as a Track wake.
    private sealed class ContractWorkerLaunchWorkItem(TrackActor actor, string sessionId, string prompt, Guid reservation, string attemptId)
        : ReservedTurnWorkItem(actor, sessionId, reservation)
    {
        public override string TriggerKind => "wake";
        public override Task<string?> PreparePromptAsync(CancellationToken ct) => Task.FromResult<string?>(prompt);
        public override Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId) => Task.CompletedTask;
        public override async Task OnTurnSettledAsync(TurnSettlement settlement)
        {
            var turn = await settlement.SettleAsync().ConfigureAwait(false);
            if (turn.Status != "failed") return;
            var stillActive = await Actor.EnqueueAsync(_ => Task.FromResult(Actor._activeAttempts.ContainsKey(attemptId)), CancellationToken.None)
                .ConfigureAwait(false);
            if (stillActive)
                await Actor.CompleteAttemptFailByAttemptIdAsync(attemptId, turn.ErrorText ?? "Contract subagent turn failed.",
                    null, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
