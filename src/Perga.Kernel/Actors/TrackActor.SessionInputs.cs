using System.Text.Json;
using System.Text.Json.Nodes;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    internal async Task<(SessionCreationReceipt Receipt, bool Replay)> CreateConversationAsync(
        CreateSessionRequest request, string key, CancellationToken ct, InteropPrincipal? principal = null)
    {
        ValidateRequestKey(key);
        var images = ImageAttachmentPolicy.Validate(request.Images);
        if (images.Count > 0) request = request with { Prompt = request.Prompt ?? "" };
        if (request.NotifyParent is not (null or "none" or "turn_end"))
            throw KernelException.InvalidArgument("notifyParent must be 'none' or 'turn_end'.");
        if (images.Count == 0 && request.Prompt is { } prompt && string.IsNullOrWhiteSpace(prompt))
            throw KernelException.InvalidArgument("First message cannot be empty.");
        if (request.SourceExcerpt is not null && (request.SourceSessionId is null || request.Prompt is null))
            throw KernelException.InvalidArgument("sourceExcerpt requires sourceSessionId and a first message.");
        request = request with { ReasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? null : request.ReasoningEffort.Trim() };
        // Plain-session retries must still match receipts written before Task and Role were retired.
        var receiptIdentity = JsonSerializer.SerializeToNode(request)!.AsObject();
        receiptIdentity["Task"] = null;
        receiptIdentity["Role"] = null;
        if (images.Count > 0)
            receiptIdentity["Attachments"] = JsonSerializer.SerializeToNode(images.Select(image => image.Attachment.Id));
        var hash = RequestIdentity.Hash(receiptIdentity);
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
                RequireCreate(parent);
                return null;
            }, ct).ConfigureAwait(false);
            if (initialReplay is not null) return (initialReplay, true);

            var providers = await ProbeSessionProvidersAsync(ct).ConfigureAwait(false);
            return await EnqueueAsync<(SessionCreationReceipt, bool)>(async _ =>
            {
                var parent = RequireInteropCore(caller, write: true);
                var replay = await ReplayCreationCoreAsync(intent, ct).ConfigureAwait(false);
                if (replay is not null) return (replay, true);
                RequireCreate(parent);
                var session = NewSubagentSessionCore(request, providers,
                    requireAvailable: caller is InteropPrincipal.Session || request.Prompt is not null, parent);
                SessionInputRequest? first = null;
                if (request.Prompt is { } content)
                {
                    var firstHash = HashMessage(content, "default", null, null, images);
                    if (images.Count > 0)
                    {
                        RequireSessionWritableCore(session);
                        var providerImages = providers.FirstOrDefault(status =>
                            SessionLaunchValidator.NormalizeProvider(status.Provider) == session.Provider)?.SupportsImages;
                        RequireImagesSupported(providerImages ?? ReadAttachmentSupportCore(session));
                    }
                    first = new(key, firstHash, content, intent.AuthorId, request.SourceExcerpt) { MediaWrites = images };
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

        void RequireCreate(AgentSessionDto? parent)
        {
            SessionPolicy.RequireCreate(caller, parent, TrackId,
                callerIsTaskOwned: parent is not null && _taskCoordinator.IsTaskOwnedSessionCore(parent.Id), _options.MaxSessionSpawnDepth);
            if (request.SourceSessionId is { } sourceId)
                RequireInteropCore(caller, RequireSessionCore(sourceId));
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
            SessionTurnDto? completedTurn = null;
            try
            {
                await WaitForWorkspacePreparationAsync(startupToken).ConfigureAwait(false);
                completedTurn = await _turnRunner.RunAsync(new SessionInputBatchWorkItem(this, input), _actorShutdownCts.Token,
                    startupCancellation: startupToken, compactedEpoch: epoch => expectedEpoch = epoch).ConfigureAwait(false);
            }
            catch (Exception error) when (!_actorShutdownCts.IsCancellationRequested &&
                (error is OperationCanceledException || error is KernelException { Code: KernelErrorCode.SessionRecoveryRequired }))
            {
                // Stop or an unconfirmed idle shutdown can arrive before the runner claims the durable input.
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
                        if (HasPendingFlowTaskForSessionCore(input.Receipt.SessionId))
                            await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
                        if (RequireSessionCore(input.Receipt.SessionId).Epoch == expectedEpoch)
                            await PokeSessionCoreAsync(input.Receipt.SessionId, allowWorker: completedTurn?.Status == "completed").ConfigureAwait(false);
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
        if (_scheduler.TryGetActive(session.Id, out var active) && active.TurnDto.Id == turn.Id)
            active.RuntimeActivity.OutputAwaitingSettlement.Clear();
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
        await ReleaseTurnAndDrainRuntimeOutputCoreAsync(session, turn.Id).ConfigureAwait(false);
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

    private const string InstallationAuthor = "owner:installation";
    private ISessionMessageStore Messages => _store.SessionMessages ?? throw new InvalidOperationException("Session message storage is unavailable.");

    private async Task<(IReadOnlyList<QueuedSessionMessageDto> Queue, IReadOnlyList<string> Handoffs)> ReadMessageQueueCoreAsync(
        string sessionId, CancellationToken ct)
    {
        if (_store.SessionMessages is null) return ([], []);
        var messages = await Messages.ListQueueAsync(sessionId, ct).ConfigureAwait(false);
        var queue = messages.Select(message => new QueuedSessionMessageDto(message.Id, message.Content, message.CreatedAtMs,
            message.Delivery switch { "dispatching" => "steering", "unknown" => "unconfirmed", _ => "waiting" },
            message.Attachments.Count == 0 ? null : message.Attachments.Select(image => image.ToDto(TrackId)).ToArray())).ToArray();
        return (queue, await Messages.ListHandoffsAsync(sessionId, ct).ConfigureAwait(false));
    }

    internal Task ResumeSchedulingAsync(CancellationToken ct = default) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        await _taskCoordinator.ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
        foreach (var sessionId in _sessions.Keys.ToArray())
            await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
    }, ct);

    private async Task PokeSessionCoreAsync(string sessionId, bool allowWorker = true) =>
        await _scheduler.PokeAsync(sessionId, allowWorker).ConfigureAwait(false);

    internal Task RecoverQueuedMessagesAsync(string sessionId, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
        }, ct);

    private static SessionItemDto WithMessageStatus(SessionItemDto item, ActiveTurnState? running)
    {
        if (item.Kind != "user" || item.Payload.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !item.Payload.TryGetProperty("inputId", out var id) || id.ValueKind != System.Text.Json.JsonValueKind.String)
            return item;

        var state = item.TurnStatus switch
        {
            "queued" => "queued",
            "running" when running?.TurnDto.Id != item.TurnId => "unconfirmed",
            "running" when item.InputDelivery == "delivered" ||
                           (item.Payload.TryGetProperty("steered", out var steered) && steered.ValueKind == System.Text.Json.JsonValueKind.True) ||
                           (running is not null && running.TurnDto.Id == item.TurnId && running.ProviderAccepted) => "processing",
            "running" => "sending",
            "completed" => "completed",
            "unknown" => "unconfirmed",
            "failed" or "interrupted" => "failed",
            _ => null,
        };
        return item with { MessageStatus = state };
    }

    private void NotifyQueueChangedCore(string sessionId)
    {
        _eventHub.PublishTransient(_track.AreaId, TrackId, KernelEventKinds.SessionStateChanged,
            JsonHelpers.ToElement(new { sessionId }), NowMs());
    }

    private Task RemoveQueuedMessageAsync(string sessionId, string inputId, CancellationToken ct, InteropPrincipal principal) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireSessionMutationCore(session, principal);
            var message = await Messages.GetAsync(sessionId, inputId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound("Message not found in this session.");
            if (message.Delivery == "withdrawn") return;
            if (message.TurnId is not null || message.Delivery is not ("queued" or "unknown") ||
                !await Messages.TransitionAsync(message with { Delivery = "withdrawn" }, message.Delivery, ct).ConfigureAwait(false))
                throw KernelException.Conflict("Only waiting messages can be withdrawn.");

            NotifyQueueChangedCore(sessionId);
            await PokeSessionCoreAsync(sessionId, allowWorker: false).ConfigureAwait(false);
        }, ct);
}
