using System.Collections.Concurrent;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    // Turn admission and scheduling run on the Track mailbox. Durable dispatch identity
    // is atomic because dispatch begins outside that mailbox.
    private sealed class SessionTurnScheduler(TrackActor actor)
    {
        private sealed class SessionState
        {
            public volatile ActiveTurnState? Active;
            public volatile bool Compacting;
            public volatile bool Recovering;
            public Guid? Reservation;
            public (string InputId, Task Attempt)? Steering;
            public string? DispatchInputId;
        }

        private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
        private SessionState State(string id) => _sessions.GetOrAdd(id, _ => new());

        public IEnumerable<ActiveTurnState> ActiveTurns => _sessions.Values.Select(s => s.Active).OfType<ActiveTurnState>();
        public bool HasActiveTurns => ActiveTurns.Any();
        public bool HasDispatchingInputs => _sessions.Values.Any(s => Volatile.Read(ref s.DispatchInputId) is not null);

        public bool HasRecoveringSessions => _sessions.Values.Any(s => s.Recovering);
        public bool HasActiveWork => actor._runtimes.HasActiveWork || _sessions.Values.Any(s =>
            s.Active is not null || s.Compacting || s.Recovering || s.Reservation is not null ||
            s.Steering is not null || Volatile.Read(ref s.DispatchInputId) is not null);

        public bool IsBusy(string id) => HasActive(id) || IsCompacting(id) || IsRecovering(id);
        public bool IsCompacting(string id) => _sessions.TryGetValue(id, out var state) && state.Compacting;
        public void BeginCompacting(string id) => State(id).Compacting = true;
        public void EndCompacting(string id) => State(id).Compacting = false;
        public bool IsRecovering(string id) => _sessions.TryGetValue(id, out var state) && state.Recovering;
        public bool TryBeginRecovery(string id)
        {
            var runtime = actor._runtimes.Get(id);
            if (IsBusy(id) || HasReservation(id) || IsSteering(id) ||
                runtime.IsDisposing || runtime.HasInFlightOperation) return false;
            State(id).Recovering = true;
            return true;
        }
        public void EndRecovery(string id) => State(id).Recovering = false;

        public void Clear(string id)
        {
            actor._runtimes.Get(id).CancelLaunch();
            actor._runtimes.Get(id).EndLaunch();
            if (_sessions.TryGetValue(id, out var state))
            {
                state.Active = null;
                state.Reservation = null;
                state.Steering = null;
                Interlocked.Exchange(ref state.DispatchInputId, null);
            }
        }

        // Active turn
        public bool HasActive(string id) => _sessions.TryGetValue(id, out var state) && state.Active is not null;
        public void SetActive(string id, ActiveTurnState active)
        {
            actor._runtimes.Get(id).ThrowIfStartupCanceled();
            var state = State(id);
            state.Active = active;
        }

        public void RemoveActive(string id) => State(id).Active = null;
        public bool TryGetActive(string id, out ActiveTurnState active)
        {
            active = _sessions.TryGetValue(id, out var state) ? state.Active! : null!;
            return active is not null;
        }

        // Reservation
        public bool HasReservation(string id) => _sessions.TryGetValue(id, out var state) && state.Reservation is not null;
        public void ReleaseReservation(string id) => State(id).Reservation = null;
        public bool TryGetReservation(string id, out Guid value)
        {
            var reservation = _sessions.TryGetValue(id, out var state) ? state.Reservation : null;
            value = reservation.GetValueOrDefault();
            return reservation.HasValue;
        }

        public void Reserve(string id, Guid value)
        {
            if (State(id).Reservation is not null) throw KernelException.Conflict("The session is already reserved.");
            State(id).Reservation = value;
        }

        // Steering
        public bool IsSteering(string id) => _sessions.TryGetValue(id, out var state) && state.Steering is not null;
        public void SetSteering(string id, (string InputId, Task Attempt) value) => State(id).Steering = value;
        public void EndSteering(string id) => State(id).Steering = null;
        public bool TryGetSteering(string id, out (string InputId, Task Attempt) value)
        {
            var steer = _sessions.TryGetValue(id, out var state) ? state.Steering : null;
            value = steer.GetValueOrDefault();
            return steer.HasValue;
        }

        // Durable input dispatch identity (atomic: a durable input starts outside the mailbox)
        public bool TryDispatch(string id, string inputId) =>
            Interlocked.CompareExchange(ref State(id).DispatchInputId, inputId, null) is null;
        public bool IsDispatching(string id, string inputId) =>
            _sessions.TryGetValue(id, out var state) && Volatile.Read(ref state.DispatchInputId) == inputId;
        public void EndDispatch(string id, string inputId) =>
            Interlocked.CompareExchange(ref State(id).DispatchInputId, null, inputId);

        public bool IsOccupied(AgentSessionDto session, PersistedSessionInput? input = null, Guid? reservation = null, bool allowFinishingDispatch = false)
        {
            if (actor._runtimes.IsShutdownUnconfirmed(session.Id) || IsRecovering(session.Id)) return true;
            if (!_sessions.TryGetValue(session.Id, out var state))
                return input is null && session.Status is "queued" or "running" or "unknown";
            return state.Active is not null || state.Compacting || state.Steering is not null ||
                (state.Reservation is not null && state.Reservation != reservation) ||
                (!allowFinishingDispatch && Volatile.Read(ref state.DispatchInputId) is { } dispatch && dispatch != input?.Receipt.InputId) ||
                (input is null && session.Status is "queued" or "running" or "unknown");
        }

        public async Task<bool> HasQueuedMessageAsync(AgentSessionDto session, CancellationToken ct) =>
            actor._store.SessionMessages is not null &&
            await actor.Messages.NextAsync(session.Id, ct).ConfigureAwait(false) is not null;

        public Task<Func<CancellationToken, Task>?> PokeLeadAsync(bool allowInline = false) =>
            actor._track.LeadSessionId is { } lead ? PokeAsync(lead, allowInline: allowInline) : Task.FromResult<Func<CancellationToken, Task>?>(null);

        public async Task<Func<CancellationToken, Task>?> PokeAsync(string sessionId, bool allowWorker = true, bool allowInline = false)
        {
            if (actor._options.DispatchAdmission.Draining || !actor.WorkspaceReady || actor._track.ClosedAtMs is not null ||
                !actor._sessions.TryGetValue(sessionId, out var session) || session.ArchivedAtMs is not null || IsOccupied(session))
                return null;

            if (actor._taskCoordinator.IsSessionFrozenForDispatchCore(session) || actor._taskCoordinator.IsSessionAutoDrainPausedCore(sessionId))
                return null;
            if (!allowWorker && actor._taskCoordinator.IsTaskOwnedSessionCore(sessionId))
                return null;

            if (actor._store.SessionMessages is not null &&
                await actor.Messages.NextAsync(sessionId, CancellationToken.None).ConfigureAwait(false) is { } next)
            {
                var queued = session with { Status = "queued", UpdatedAtMs = actor.NowMs() };
                var evt = actor.SessionUpdatedEvent(queued);
                await actor._eventHub.EnsureInitializedFromStoreAsync(actor._store, CancellationToken.None).ConfigureAwait(false);
                var input = await actor._store.SessionInputs.PromoteQueuedBatchAsync(queued, next, evt, CancellationToken.None).ConfigureAwait(false);
                if (input is not null)
                {
                    actor.RegisterAcceptedInput(queued, input);
                    await actor.PublishInputEventAsync(evt).ConfigureAwait(false);
                }
                return null;
            }

            if (actor.TryScheduleApprovalNotificationCore(sessionId)) return null;

            // Pending system work belongs to the Track. Resolve its Lead only when claiming.
            var batch = await actor._store.TrackWakes.ClaimBatchAsync(actor.TrackId, sessionId, 32, CancellationToken.None).ConfigureAwait(false);
            if (batch.Count == 0) return null;
            var reservation = Guid.NewGuid();
            Reserve(sessionId, reservation);
            Task Run(CancellationToken ct) => actor.RunReservedWakeAsync(sessionId, batch, reservation, ct);
            // Only a caller outside the mailbox can await a synchronous wake safely.
            if (allowInline && !actor._options.DispatchWakesInBackground) return Run;
            _ = actor.TrackBackgroundJob(() => Run(actor._actorShutdownCts.Token));
            return null;
        }
    }
}
