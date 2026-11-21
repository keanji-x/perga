using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private async Task<(SessionCreationReceipt Receipt, bool Replay)> CreateConversationAsync(
        CreateSessionRequest request, string key, CancellationToken ct, InteropPrincipal? principal = null)
    {
        ValidateRequestKey(key);
        if (request.Prompt is { } prompt && string.IsNullOrWhiteSpace(prompt))
            throw KernelException.InvalidArgument("First message cannot be empty.");
        request = request with { ReasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? null : request.ReasoningEffort.Trim() };
        var hash = RequestIdentity.Hash(request);
        var intent = new SessionCreateIntent(TrackId, key, hash,
            principal is InteropPrincipal.Session identity ? identity.SessionId : InstallationAuthor);
        var caller = principal ?? new InteropPrincipal.Human();
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            var initialReplay = await EnqueueAsync(async _ =>
            {
                AgentSessionDto? parent = null;
                if (caller is InteropPrincipal.Session)
                {
                    await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                    parent = RequireInteropCore(caller, write: true);
                }

                // Installation receipts can outlive the Track and return Gone without hydrating it.
                var replay = await ReplayCreationCoreAsync(intent, ct).ConfigureAwait(false);
                if (replay is not null) return replay;
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                SessionPolicy.RequireCreate(caller, parent, TrackId, _options.MaxSessionSpawnDepth);
                return null;
            }, ct).ConfigureAwait(false);
            if (initialReplay is not null) return (initialReplay, true);

            var providers = await ProbeSessionProvidersAsync(ct).ConfigureAwait(false);
            return await EnqueueAsync<(SessionCreationReceipt, bool)>(async _ =>
            {
                var parent = RequireInteropCore(caller, write: true);
                var replay = await ReplayCreationCoreAsync(intent, ct).ConfigureAwait(false);
                if (replay is not null) return (replay, true);
                SessionPolicy.RequireCreate(caller, parent, TrackId, _options.MaxSessionSpawnDepth);
                var session = NewSubagentSessionCore(request, providers,
                    requireAvailable: caller is InteropPrincipal.Session || request.Prompt is not null, parent);
                SessionInputRequest? first = null;
                if (request.Prompt is { } content)
                {
                    var firstHash = RequestIdentity.Hash(new { version = 3, Content = content, Mode = "default", TurnId = (string?)null, ExpectedAttemptId = (string?)null });
                    first = new(key, firstHash, content, intent.AuthorId);
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
                return (new(session, input?.Receipt, RequestKey: key), false);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private async Task<SessionCreationReceipt?> ReplayCreationCoreAsync(SessionCreateIntent intent, CancellationToken ct)
    {
        var existing = await _store.SessionInputs.FindCreationAsync(intent, ct).ConfigureAwait(false);
        if (existing is null) return null;

        RequireMatchingHash(existing.RequestHash, intent.Hash);
        var session = existing.Session ?? throw KernelException.Gone("The created session was deleted.");
        var replayedInput = existing.FirstInput is { } originalInput
            ? await ReplayInputCoreAsync(originalInput, ct).ConfigureAwait(false)
            : null;
        return new(session, replayedInput, RequestKey: intent.Key);
    }

    private void RegisterAcceptedInput(AgentSessionDto session, PersistedSessionInput input)
    {
        _sessions[session.Id] = session;
        _nextTurnIndexBySession[session.Id] = input.TurnIndex;
        DispatchAcceptedInput(input);
    }

    internal void DispatchAcceptedInput(PersistedSessionInput input)
    {
        if (!_scheduler.TryDispatch(input.Receipt.SessionId, input.Receipt.InputId)) return;
        var startup = _runtimes.Get(input.Receipt.SessionId).BeginLaunch(_actorShutdownCts.Token, input.Receipt.TurnId);
        TrackBackgroundJob(async () =>
        {
            // Capture a token before activation can dispose the scheduler's source.
            var startupToken = startup.Token;
            var expectedEpoch = input.Epoch;
            try
            {
                await WaitForWorkspacePreparationAsync(startupToken).ConfigureAwait(false);
                await _turnRunner.RunAsync(new SessionInputBatchWorkItem(this, input), _actorShutdownCts.Token,
                    startupCancellation: startupToken, compactedEpoch: epoch => expectedEpoch = epoch).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (!_actorShutdownCts.IsCancellationRequested)
            {
                // Stop can arrive while queued behind workspace/admission/policy work,
                // before the runner has claimed the durable input.
                await EnqueueAsync(async _ =>
                {
                    var session = RequireSessionCore(input.Receipt.SessionId);
                    if (session.Epoch != input.Epoch) return;
                    var work = new SessionInputBatchWorkItem(this, input);
                    var running = session with { Status = "running", UpdatedAtMs = NowMs() };
                    if (await work.ClaimCoreAsync(session, running, SessionUpdatedEvent(running), CancellationToken.None).ConfigureAwait(false) is null) return;
                    var turn = work.CreateTurnCore(session, input.Content, NowMs());
                    await work.TrySettleSetupFailureCoreAsync(error, turn, running).ConfigureAwait(false);
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _runtimes.Get(input.Receipt.SessionId).EndLaunch(startup);
                _scheduler.EndDispatch(input.Receipt.SessionId, input.Receipt.InputId);
                if (!_actorShutdownCts.IsCancellationRequested)
                    await EnqueueAsync(async _ =>
                    {
                        if (RequireSessionCore(input.Receipt.SessionId).Epoch == expectedEpoch)
                            await PokeSessionCoreAsync(input.Receipt.SessionId).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
            }
        });
    }

    private async Task<SessionInputReceipt> ReplayInputCoreAsync(PersistedSessionInput input, CancellationToken ct)
    {
        if (input.Receipt.DeliveryState == "accepted")
        {
            DispatchAcceptedInput(input);
        }
        else if (input.Receipt.TurnStatus == "running" && !_scheduler.IsDispatching(input.Receipt.SessionId, input.Receipt.InputId))
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            return await RecoverDispatchCoreAsync(input).ConfigureAwait(false);
        }

        return input.Receipt;
    }

    private async Task<SessionInputReceipt> RecoverDispatchCoreAsync(PersistedSessionInput input)
    {
        var now = NowMs();
        var session = RequireSessionCore(input.Receipt.SessionId) with { Status = "unknown", UnknownReason = "stream_lost", UnknownTurnId = input.Receipt.TurnId, UpdatedAtMs = now };
        var turn = new SessionTurnDto(input.Receipt.TurnId, session.Id, input.TurnIndex, input.Epoch,
            "user", JsonHelpers.ToElement(new { }), "unknown", null, "Dispatch outcome could not be saved.", null, null);
        var delivery = input.Receipt.DeliveryState == "delivered" ? "delivered" : "unknown";
        await SettleDurableTurnCoreAsync(input, delivery, turn, [], session).ConfigureAwait(false);
        return input.Receipt with { DeliveryState = delivery, TurnStatus = "unknown" };
    }

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
        _scheduler.RemoveActive(session.Id);
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
