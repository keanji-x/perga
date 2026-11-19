using System.Collections.Concurrent;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    // All scheduling decisions and slot transitions run on the Track mailbox. Dispatch
    // identity alone is atomic because a durable input starts outside that mailbox.
    private sealed class SessionTurnScheduler(TrackActor actor)
    {
        private sealed class SessionState
        {
            public ActiveTurnState? Active;
            public Guid? Reservation;
            public bool Compacting;
            public (string InputId, Task Attempt)? Steering;
            public SessionResumeHold? Resume;
            public string? DispatchInputId;
        }

        private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
        private SessionState State(string id) => _sessions.GetOrAdd(id, _ => new());

        public IEnumerable<ActiveTurnState> ActiveTurns => _sessions.Values.Select(s => s.Active).OfType<ActiveTurnState>();
        public IEnumerable<SessionResumeHold> ResumeHolds => _sessions.Values.Select(s => s.Resume).OfType<SessionResumeHold>();
        public bool HasActiveTurns => ActiveTurns.Any();
        public bool HasDispatchingInputs => _sessions.Values.Any(s => Volatile.Read(ref s.DispatchInputId) is not null);

        // Active turn
        public bool HasActive(string id) => _sessions.TryGetValue(id, out var state) && state.Active is not null;
        public void SetActive(string id, ActiveTurnState active) => State(id).Active = active;
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

        // Compaction
        public bool IsCompacting(string id) => _sessions.TryGetValue(id, out var state) && state.Compacting;
        public void StartCompacting(string id) => State(id).Compacting = true;
        public void EndCompacting(string id) => State(id).Compacting = false;

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

        // Resume hold
        public bool HasResumeHold(string id) => _sessions.TryGetValue(id, out var state) && state.Resume is not null;
        public void HoldResume(string id, SessionResumeHold hold) => State(id).Resume = hold;
        public bool TryGetResumeHold(string id, out SessionResumeHold hold)
        {
            hold = _sessions.TryGetValue(id, out var state) ? state.Resume! : null!;
            return hold is not null;
        }

        public bool TryHoldResume(string id, SessionResumeHold hold)
        {
            if (HasResumeHold(id)) return false;
            HoldResume(id, hold);
            return true;
        }

        public bool ReleaseResumeHold(string id)
        {
            if (!_sessions.TryGetValue(id, out var state)) return false;
            var held = state.Resume is not null;
            state.Resume = null;
            return held;
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
            if (!_sessions.TryGetValue(session.Id, out var state))
                return input is null && session.Status is "queued" or "running" or "unknown";
            return state.Active is not null || state.Compacting || state.Steering is not null || state.Resume is not null ||
                (state.Reservation is not null && state.Reservation != reservation) ||
                (!allowFinishingDispatch && Volatile.Read(ref state.DispatchInputId) is { } dispatch && dispatch != input?.Receipt.InputId) ||
                (input is null && session.Status is "queued" or "running" or "unknown");
        }

        public async Task<bool> HasQueuedMessageAsync(AgentSessionDto session, CancellationToken ct) =>
            actor._store.SessionMessages is not null &&
            await actor.Messages.NextAsync(session.Id, session.BoundAttemptId, ct).ConfigureAwait(false) is not null;

        public Task<Func<CancellationToken, Task>?> PokeLeadAsync(bool allowInline = false) =>
            actor._track.LeadSessionId is { } lead ? PokeAsync(lead, allowInline: allowInline) : Task.FromResult<Func<CancellationToken, Task>?>(null);

        public async Task<Func<CancellationToken, Task>?> PokeAsync(string sessionId, bool allowWorker = true, bool allowInline = false)
        {
            if (actor._options.DispatchAdmission.Draining || !actor.WorkspaceReady || actor._track.ClosedAtMs is not null ||
                !actor._sessions.TryGetValue(sessionId, out var session) || IsOccupied(session))
                return null;

            ActiveAttemptState? attempt = null;
            if (session.BoundAttemptId is { } attemptId &&
                (!allowWorker || !actor._activeAttempts.TryGetValue(attemptId, out attempt) || attempt.Paused || attempt.Settling ||
                 attempt.InterruptionUnconfirmed || !string.Equals(attempt.AttemptDto.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                 actor.RequireTaskCore(attempt.TaskId).LatestAttemptId != attemptId))
                return null;

            if (actor._store.SessionMessages is not null &&
                await actor.Messages.NextAsync(sessionId, session.BoundAttemptId, CancellationToken.None).ConfigureAwait(false) is { } next)
            {
                if (attempt is not null)
                    await actor.ScheduleWorkerMessageCoreAsync(next, attempt).ConfigureAwait(false);
                else
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
                }
                return null;
            }

            // Pending system work belongs to the Track. Resolve its Lead only when claiming.
            if (sessionId != actor._track.LeadSessionId) return null;
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
