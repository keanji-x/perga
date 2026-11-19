using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private sealed record TurnSettlement(string TurnId, string Status, bool Interrupted, bool Delivered,
        bool RejectedBeforePrompt, Func<Task<SessionTurnDto>> SettleAsync);

    private interface ITurnWorkItem
    {
        string SessionId { get; }
        string TriggerKind { get; }
        Guid? Reservation { get; }
        string? Presentation => null;
        PersistedSessionInput? DurableInput => null;
        SessionMessage? WorkerMessage => null;
        Task<string> PreparePromptAsync(CancellationToken ct);
        Task OnTurnCreatedAsync(SessionTurnDto turn) => Task.CompletedTask;
        Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId);
        Task OnTurnSettledAsync(TurnSettlement settlement);
    }

    // The durable input carries one turn containing one or more user messages.
    private sealed class SessionInputBatchWorkItem(TrackActor actor, PersistedSessionInput input) : ITurnWorkItem
    {
        public string SessionId => input.Receipt.SessionId;
        public string TriggerKind => "user";
        public Guid? Reservation => null;
        public PersistedSessionInput DurableInput => input;
        private bool _delivered;
        public Task<string> PreparePromptAsync(CancellationToken ct) => Task.FromResult(input.Content);
        public async Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId)
        {
            if (_delivered && evt is not UnifiedAgentEvent.TurnAccepted) return;
            await actor._store.SessionInputs.MarkDeliveredAsync(input, nativeThreadId,
                (evt as UnifiedAgentEvent.TurnAccepted)?.NativeTurnId, CancellationToken.None).ConfigureAwait(false);
            _delivered = true;
        }
        public async Task OnTurnSettledAsync(TurnSettlement settlement) => await settlement.SettleAsync().ConfigureAwait(false);
    }

    private sealed class WorkerMessageWorkItem(TrackActor actor, SessionMessage message, ActiveAttemptState attempt, Guid reservation) : ITurnWorkItem
    {
        public string SessionId => message.SessionId;
        public string TriggerKind => "user";
        public Guid? Reservation => reservation;
        public SessionMessage WorkerMessage => message;
        private bool _delivered;
        public Task<string> PreparePromptAsync(CancellationToken ct)
        {
            attempt.Cts.Token.ThrowIfCancellationRequested();
            return Task.FromResult(message.Content);
        }
        public async Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId)
        {
            if (_delivered) return;
            await actor.Messages.TransitionAsync(message with { TurnId = turn.Id, DeliveredTurnId = turn.Id, Delivery = "delivered" },
                "dispatching", CancellationToken.None).ConfigureAwait(false);
            _delivered = true;
        }
        public async Task OnTurnSettledAsync(TurnSettlement settlement)
        {
            if (!_delivered)
                await actor.Messages.TransitionAsync(message with
                {
                    TurnId = settlement.TurnId,
                    Delivery = settlement.Status == "interrupted" ? "interrupted" : settlement.RejectedBeforePrompt ? "rejected" : "unknown"
                },
                    "dispatching", CancellationToken.None).ConfigureAwait(false);
            await settlement.SettleAsync().ConfigureAwait(false);
        }
    }

    private sealed class TrackWakeBatchWorkItem(TrackActor actor, string sessionId, IReadOnlyList<TrackWake> batch, Guid reservation) : ITurnWorkItem
    {
        public string SessionId => sessionId;
        public string TriggerKind
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
        public string? Presentation
        {
            get
            {
                if (DispatchBatch.Count != 1) return TrackWakePresentations.System;
                var payload = _preparedPayloads is { Count: 1 } prepared ? prepared[0] : TrackWakePayload.Parse(DispatchBatch[0].PayloadJson);
                return payload.Presentation ?? TrackWakePresentations.System;
            }
        }
        public Guid? Reservation => reservation;
        private bool _acked;
        private string? _turnId;
        private IReadOnlyList<TrackWake>? _preparedBatch;
        private IReadOnlyList<TrackWakePayload>? _preparedPayloads;
        private IReadOnlyList<TrackWake> DispatchBatch => _preparedBatch ?? batch;
        public async Task<string> PreparePromptAsync(CancellationToken ct)
        {
            var prompts = new List<string>();
            var dispatch = new List<TrackWake>();
            var payloads = new List<TrackWakePayload>();
            foreach (var wake in batch)
            {
                var payload = TrackWakePayload.Parse(wake.PayloadJson);
                var prompt = await actor.PrepareWakePromptAsync(wake, payload, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(prompt))
                    await actor._store.TrackWakes.MarkDroppedAsync([wake], TrackWakeReasons.Canceled, null, CancellationToken.None).ConfigureAwait(false);
                else
                {
                    prompts.Add(prompt);
                    dispatch.Add(wake);
                    payloads.Add(payload);
                }
            }
            _preparedBatch = dispatch;
            _preparedPayloads = payloads;
            if (dispatch.Count == 0) _acked = true;
            return string.Join("\n\n", prompts);
        }
        public async Task OnTurnCreatedAsync(SessionTurnDto turn)
        {
            _turnId = turn.Id;
            await actor._store.TrackWakes.LinkTurnAsync(DispatchBatch, turn.Id, CancellationToken.None).ConfigureAwait(false);
        }
        public async Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId)
        {
            _turnId = turn.Id;
            if (_acked) return;
            var reason = evt switch
            {
                UnifiedAgentEvent.TurnError => TrackWakeReasons.ConclusiveError,
                UnifiedAgentEvent.TurnInterrupted => TrackWakeReasons.Interrupted,
                _ => TrackWakeReasons.Delivered
            };
            await actor._store.TrackWakes.MarkDoneAsync(DispatchBatch, reason, turn.Id, CancellationToken.None).ConfigureAwait(false);
            _acked = true;
        }
        public async Task OnTurnSettledAsync(TurnSettlement settlement)
        {
            _turnId = settlement.TurnId;
            if (!_acked && (settlement.Interrupted || settlement.Status == "interrupted"))
            {
                await actor._store.TrackWakes.MarkDoneAsync(DispatchBatch, TrackWakeReasons.Interrupted, settlement.TurnId, CancellationToken.None).ConfigureAwait(false);
                _acked = true;
            }
            if (!_acked) await FailBeforeSettlementAsync().ConfigureAwait(false);
            var turn = await settlement.SettleAsync().ConfigureAwait(false);
            await NotifyUpgradeOutcomeAsync(turn.Status, turn.ErrorText).ConfigureAwait(false);
        }
        public async Task NotifyUpgradeOutcomeAsync(string? status, string? error)
        {
            if (actor._store is not ISystemUpgradeStore upgrades) return;
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
                await actor._store.TrackWakes.LinkTurnAsync(DispatchBatch, _turnId, CancellationToken.None).ConfigureAwait(false);
            await actor._store.TrackWakes.MarkDroppedAsync(DispatchBatch.Where(w => !w.IsCritical).ToArray(),
                TrackWakeReasons.RuntimeFailed, _turnId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Attempt startup is a reserved turn, never queued as a Track wake.
    private sealed class ContractWorkerLaunchWorkItem(TrackActor actor, string sessionId, string prompt, Guid reservation, string attemptId) : ITurnWorkItem
    {
        public string SessionId => sessionId;
        public string TriggerKind => "wake";
        public Guid? Reservation => reservation;
        public Task<string> PreparePromptAsync(CancellationToken ct) => Task.FromResult(prompt);
        public Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId) => Task.CompletedTask;
        public async Task OnTurnSettledAsync(TurnSettlement settlement)
        {
            var turn = await settlement.SettleAsync().ConfigureAwait(false);
            if (turn.Status != "failed") return;
            var stillActive = await actor.EnqueueAsync(_ => Task.FromResult(actor._activeAttempts.ContainsKey(attemptId)), CancellationToken.None)
                .ConfigureAwait(false);
            if (stillActive)
                await actor.CompleteAttemptFailByAttemptIdAsync(attemptId, turn.ErrorText ?? "Contract subagent turn failed.",
                    null, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
