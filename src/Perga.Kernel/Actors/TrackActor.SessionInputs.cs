using System.Collections.Concurrent;
using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private readonly ConcurrentDictionary<string, byte> _dispatchingInputs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _compactingSessions = new(StringComparer.Ordinal);

    public async Task<MessageAcceptance> AcceptMessageAsync(
        string sessionId, SendMessageRequest request, CancellationToken ct)
    {
        ValidateRequestKey(request.IdempotencyKey);
        var hash = RequestIdentity.Hash(new { version = 1, request.Content, request.SteerIfRunning });
        var result = await EnqueueAsync<MessageAcceptance>(async _ =>
        {
            var existing = await _store.SessionInputs.FindByKeyAsync(sessionId, request.IdempotencyKey, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                RequireMatchingHash(existing.RequestHash, hash);
                return new(await ReplayInputCoreAsync(existing, ct).ConfigureAwait(false), null, true);
            }

            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            if (session.BoundAttemptId is not null)
                throw KernelException.InvalidArgument("Worker messages require attemptId and the active turnId when steering.");
            if (_queuedMessageRequests.TryGetValue((sessionId, request.IdempotencyKey), out var prior))
            {
                RequireMatchingHash(prior.Hash, hash);
                if (!request.QueueIfBusy || request.SteerIfRunning)
                    throw KernelException.Conflict("This request key was already used to queue a message.");
                return new(null, null, true, new(sessionId, prior.Id));
            }

            RequireSessionDispatchAvailableCore(sessionId);
            if (request.SteerIfRunning && (_activeTurns.ContainsKey(sessionId) ||
                _idempotencyTurns.ContainsKey((sessionId, request.IdempotencyKey))))
                return new(null, null, false);
            if (_idempotencyTurns.ContainsKey((sessionId, request.IdempotencyKey)))
                throw KernelException.Conflict("This request key was already used by a live turn.");
            if (_activeTurns.ContainsKey(sessionId) || _compactingSessions.Contains(sessionId) || _reservedWakeSessions.ContainsKey(sessionId) || HoldsDurableSlot(session) || HasPendingUserMessagesCore(sessionId))
            {
                if (!request.QueueIfBusy || session.Status == "unknown")
                    throw KernelException.Conflict($"Session '{sessionId}' already has an active or unresolved turn.");

                RequireWorkspaceReadyCore();
                RequireDispatchWorkspace(session);
                var message = AddQueuedMessageCore(sessionId, request, hash);
                SchedulePendingWakeCore(sessionId);
                NotifyQueueChangedCore(sessionId);
                return new(null, null, false, new(sessionId, message.Id));
            }

            session = session with { Status = "queued", UpdatedAtMs = NowMs() };
            var sessionEvent = SessionUpdatedEvent(session);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            var input = await _store.SessionInputs.AcceptAsync(session,
                new(request.IdempotencyKey, hash, request.Content), sessionEvent, ct).ConfigureAwait(false);
            RegisterAcceptedInput(session, input);
            await PublishInputEventAsync(sessionEvent).ConfigureAwait(false);
            return new(input.Receipt, null, false);
        }, ct).ConfigureAwait(false);

        if (result.Receipt is not null || result.QueueReceipt is not null)
            return result;

        var steered = await SendMessageAsync(sessionId, request, ct, requireActiveTurn: true).ConfigureAwait(false);
        return new(null, steered, false);
    }

    public Task<(SessionCreationReceipt Receipt, bool Replay)> CreateSessionWithReceiptAsync(
        CreateSessionRequest request, string key, CancellationToken ct)
    {
        ValidateRequestKey(key);
        if (request.Text is { } text && string.IsNullOrWhiteSpace(text))
            throw KernelException.InvalidArgument("First message cannot be empty.");

        var hash = RequestIdentity.Hash(new { version = 1, request.Title, request.Provider, request.Model, request.Text });
        var intent = new SessionCreateIntent(TrackId, key, hash);
        return EnqueueAsync<(SessionCreationReceipt, bool)>(async _ =>
        {
            var existing = await _store.SessionInputs.FindCreationAsync(intent, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                RequireMatchingHash(existing.RequestHash, hash);
                var created = existing.Session ?? throw KernelException.Gone("The created session was deleted.");
                var replayedInput = existing.FirstInput is { } originalInput
                    ? await ReplayInputCoreAsync(originalInput, ct).ConfigureAwait(false)
                    : null;
                return (new(created, replayedInput), true);
            }

            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = NewSubagentSessionCore(request);
            SessionInputRequest? first = null;
            if (request.Text is { } content)
            {
                var firstHash = RequestIdentity.Hash(new { version = 1, Content = content, SteerIfRunning = false });
                first = new(key, firstHash, content);
                session = session with { Status = "queued" };
            }

            var sessionEvent = SessionUpdatedEvent(session);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            var input = await _store.SessionInputs.CreateAsync(intent, session, first, sessionEvent, ct).ConfigureAwait(false);
            if (input is null)
                _sessions[session.Id] = session;
            else
                RegisterAcceptedInput(session, input);
            await PublishInputEventAsync(sessionEvent).ConfigureAwait(false);
            return (new(session, input?.Receipt), false);
        }, ct);
    }

    public Task<SessionInputReceipt> ResolveUnknownInputAsync(string sessionId, string inputId, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            var input = await _store.SessionInputs.FindByIdAsync(sessionId, inputId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound("Session input not found.");
            if (input.Receipt.ResolvedAtMs is not null)
                return input.Receipt;
            if (input.Receipt.TurnStatus != "unknown" || session.Status != "unknown")
                throw KernelException.Conflict("Only an unresolved unknown input can be abandoned.");

            _sessionResumeHolds.TryGetValue(sessionId, out var resume);
            if (resume is not null && (resume.Busy || !resume.Quiescent || resume.TurnId != input.Receipt.TurnId))
                throw KernelException.PreconditionFailed("Confirm that the previous provider execution has stopped before replacing its session.");

            // The previous provider thread may still contain effects from the unknown turn.
            var updated = session with
            {
                Status = "idle", Epoch = checked(session.Epoch + 1), NativeThreadId = null,
                ContextUsageRatio = 0, UpdatedAtMs = NowMs()
            };
            _mcpBridge.RevokeSession(sessionId);
            if (_openAgentSessions.Remove(sessionId, out var agent))
                await agent.DisposeAsync().ConfigureAwait(false);
            var sessionEvent = SessionUpdatedEvent(updated);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            if (resume is null)
                await _store.SessionInputs.ResolveAsync(input, updated, sessionEvent, ct).ConfigureAwait(false);
            else
                await _store.CompleteSessionResumeAsync(updated, input, sessionEvent, ct).ConfigureAwait(false);

            _sessions[sessionId] = updated;
            ReleaseSessionResumeCore(sessionId);
            SchedulePendingWakeCore(sessionId);
            NotifyQueueChangedCore(sessionId);
            return input.Receipt with { ResolvedAtMs = updated.UpdatedAtMs };
        }, ct);

    private void RegisterAcceptedInput(AgentSessionDto session, PersistedSessionInput input)
    {
        _sessions[session.Id] = session;
        _nextTurnIndexBySession[session.Id] = input.TurnIndex;
        DispatchAcceptedInput(input);
    }

    internal void DispatchAcceptedInput(PersistedSessionInput input)
    {
        TrackBackgroundJob(async () =>
        {
            await WaitForWorkspacePreparationAsync(_actorShutdownCts.Token).ConfigureAwait(false);
            if (!_dispatchingInputs.TryAdd(input.Receipt.InputId, 0))
                return;
            try
            {
                await ExecuteSessionTurnPipelineAsync(input.Receipt.SessionId, "user", input.Content,
                    _actorShutdownCts.Token, input).ConfigureAwait(false);
            }
            finally
            {
                _dispatchingInputs.TryRemove(input.Receipt.InputId, out _);
            }
        });
    }

    private async Task<SessionInputReceipt> ReplayInputCoreAsync(PersistedSessionInput input, CancellationToken ct)
    {
        if (input.Receipt.DeliveryState == "accepted")
            DispatchAcceptedInput(input);
        else if (input.Receipt.TurnStatus == "running" && !_dispatchingInputs.ContainsKey(input.Receipt.InputId))
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            return await RecoverDispatchCoreAsync(input).ConfigureAwait(false);
        }

        return input.Receipt;
    }

    private async Task<SessionInputReceipt> RecoverDispatchCoreAsync(PersistedSessionInput input)
    {
        var now = NowMs();
        var session = RequireSessionCore(input.Receipt.SessionId) with { Status = "unknown", UpdatedAtMs = now };
        var turn = new SessionTurnDto(input.Receipt.TurnId, session.Id, input.TurnIndex, input.Epoch,
            "user", JsonHelpers.ToElement(new { }), "unknown", null, "Dispatch outcome could not be saved.", null, null);
        var delivery = input.Receipt.DeliveryState == "delivered" ? "delivered" : "unknown";
        await SettleDurableTurnCoreAsync(input, delivery, turn, [], session).ConfigureAwait(false);
        return input.Receipt with { DeliveryState = delivery, TurnStatus = "unknown" };
    }

    private static bool HoldsDurableSlot(AgentSessionDto session) => session.Status is "queued" or "running" or "unknown";

    private EventEnvelopeDto SessionUpdatedEvent(AgentSessionDto session) =>
        new(null, _track.AreaId, TrackId, KernelEventKinds.SessionUpdated,
            JsonHelpers.ToElement(session), session.UpdatedAtMs);

    private EventEnvelopeDto TurnCompletedEvent(SessionTurnDto turn, long now) =>
        new(null, _track.AreaId, TrackId, KernelEventKinds.TurnCompleted, JsonHelpers.ToElement(turn), now);

    private async Task SettleDurableTurnCoreAsync(
        PersistedSessionInput input, string delivery, SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
        AgentSessionDto session)
    {
        var turnEvent = TurnCompletedEvent(turn, session.UpdatedAtMs);
        var sessionEvent = SessionUpdatedEvent(session);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _store.SessionInputs.SettleAsync(input, delivery, turn, items, session,
                [turnEvent, sessionEvent], CancellationToken.None).ConfigureAwait(false);
        }
        catch when (turn.Status != "unknown" || items.Count > 0)
        {
            var observed = await _store.SessionInputs.FindByIdAsync(session.Id, input.Receipt.InputId,
                CancellationToken.None).ConfigureAwait(false);
            if (observed is null)
                throw;
            if (observed.Receipt.TurnStatus == "running")
            {
                await RecoverDispatchCoreAsync(observed).ConfigureAwait(false);
                return;
            }
            if (observed.Receipt.TurnStatus != turn.Status)
                throw;
        }
        _sessions[session.Id] = session;
        _activeTurns.Remove(session.Id);
        await PublishInputEventAsync(turnEvent).ConfigureAwait(false);
    }

    private Task PublishInputEventAsync(EventEnvelopeDto evt) =>
        _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None, allowFallback: false);

    private static void ValidateRequestKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
            throw KernelException.InvalidArgument("Idempotency-Key must contain 1 to 256 characters.");
    }

    private static void RequireMatchingHash(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw KernelException.Conflict("This request key was already used with different input.");
    }
}
