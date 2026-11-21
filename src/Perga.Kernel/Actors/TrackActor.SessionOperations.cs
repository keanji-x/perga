using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static SessionActivityDto ReadActivityCore(ActiveTurnState? active, string? turnId, long? after)
    {
        if (active is null) return new(null, 0, turnId is not null, [], "", false);
        var snapshot = active.Activity.Read(after, active.TurnDto.Id == turnId);
        return new(active.TurnDto.Id, snapshot.Sequence, snapshot.Reset, snapshot.Events, snapshot.Text, snapshot.TextTruncated);
    }

    public async Task<SessionItemsPageDto> GetSessionItemsAsync(string sessionId, SessionReadQuery query,
        InteropPrincipal caller, CancellationToken ct)
    {
        await using var events = _eventHub.Subscribe(TrackId);
        Task<SessionItemsPageDto> Read() => ReadSessionItemsAsync(sessionId, query.AfterId, query.Limit, ct,
            query.Latest, query.BeforeId, activityTurnId: query.ActivityTurnId, afterSequence: query.AfterSequence, principal: caller);
        var page = await Read().ConfigureAwait(false);
        bool Waiting() => query.ActivityTurnId is { } turnId && query.AfterSequence is { } sequence
            ? page.ActiveTurn?.Id == turnId && page.Activity is { Reset: false } activity && activity.Sequence <= sequence
            : page.State is "running" or "stopping" or "compacting" or "unknown";
        if (query.WaitMs <= 0 || !Waiting()) return page;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Min(query.WaitMs, 10_000));
        while (Waiting())
        {
            EventEnvelopeDto evt;
            try { evt = await events.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return await Read().ConfigureAwait(false); }
            var relevant = evt.Payload.TryGetProperty("sessionId", out var target) && target.GetString() == sessionId ||
                evt.Kind == KernelEventKinds.SessionUpdated && evt.Payload.TryGetProperty("id", out var id) && id.GetString() == sessionId ||
                evt.Kind == KernelEventKinds.AskChanged;
            if (relevant) return await Read().ConfigureAwait(false);
        }
        return page;
    }

    public async Task<SessionMessageReceipt> SendSessionMessageAsync(string sessionId, SendSessionMessageRequest request,
        InteropPrincipal caller, CancellationToken ct)
    {
        if (request.Mode is not ("default" or "queue" or "steer") ||
            (request.Content is null) == (request.MessageId is null) ||
            request.Mode == "steer" && request.TurnId is null || request.Mode != "steer" && request.TurnId is not null ||
            request.MessageId is not null && request.Mode != "steer")
            throw KernelException.InvalidArgument("Choose content or a queued messageId and a valid delivery mode.");
        if (request.Content is not null && string.IsNullOrWhiteSpace(request.Content))
            throw KernelException.InvalidArgument("Message content cannot be empty.");
        if (request.Content is not null) ValidateRequestKey(request.RequestKey!);
        var hash = RequestIdentity.Hash(new { version = 4, request.Content, request.Mode, request.TurnId, request.ExpectedEpoch });
        var author = caller is InteropPrincipal.Session identity ? identity.SessionId : InstallationAuthor;
        var startedAt = _timeProvider.GetTimestamp();
        var delivery = await EnqueueAsync(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireInteropCore(caller, session, write: true);
            if (request.ExpectedEpoch is { } expectedEpoch && session.Epoch != expectedEpoch)
                throw KernelException.Conflict("The session epoch changed.");

            var existing = request.MessageId is { } id
                ? await Messages.GetAsync(sessionId, id, ct).ConfigureAwait(false)
                    ?? throw KernelException.NotFound("Message not found in this session.")
                : await Messages.FindAsync(sessionId, author, request.RequestKey!, ct).ConfigureAwait(false);
            if (request.MessageId is null)
            {
                if (existing is not null)
                {
                    RequireMatchingHash(existing.RequestHash, hash);
                    if (existing.Kind == "input" && await _store.SessionInputs.FindByIdAsync(sessionId, existing.Id, ct).ConfigureAwait(false) is { } input)
                    {
                        var replay = await ReplayInputCoreAsync(input, ct).ConfigureAwait(false);
                        return Task.FromResult(new SessionMessageReceipt(replay.MessageId, sessionId, replay.TurnId, replay.DeliveryState, true));
                    }
                    if (existing.Delivery == "dispatching" && _scheduler.TryGetSteering(sessionId, out var pending) &&
                        pending.InputId == existing.Id && pending.Attempt is Task<SteeringResult> steering)
                        return CompleteSteeringReceiptAsync(steering, sessionId, existing.TargetTurnId!, true);
                    return Task.FromResult(MessageReceipt(existing, replay: true));
                }
            }
            else
            {
                var prior = await Messages.FindQueuedSteerAsync(existing!, request.TurnId!, ct).ConfigureAwait(false);
                if (prior is not null)
                {
                    if (prior == "dispatching" && _scheduler.TryGetSteering(sessionId, out var pending) &&
                        pending.InputId == existing!.Id && pending.Attempt is Task<SteeringResult> steering)
                        return CompleteSteeringReceiptAsync(steering, sessionId, request.TurnId!, true);
                    return Task.FromResult(new SessionMessageReceipt(existing!.Id, sessionId, request.TurnId!, prior, true));
                }
            }

            RequireSessionWritableCore(session);
            RequireSessionDispatchAvailableCore(sessionId);
            _scheduler.TryGetActive(sessionId, out var active);
            var turnId = request.TurnId;
            if (turnId is not null)
            {
                if (request.MessageId is not null)
                {
                    if (existing!.Kind != "queue" || existing.Delivery != "queued" || existing.TurnId is not null)
                        throw KernelException.Conflict("Only waiting messages can be steered.");
                }
                if (active?.TurnDto.Id != turnId || active.Interrupted)
                {
                    if (!await _store.HasSessionTurnAsync(sessionId, turnId, ct).ConfigureAwait(false))
                        throw KernelException.NotFound("The target turn was not found in this session.");
                    throw KernelException.Conflict("The target turn changed. Refresh before steering.");
                }
                RequireDispatchWorkspace(session);
                if (request.Mode == "steer" && (!active.AgentSession.SupportsNativeSteer ||
                    request.MessageId is not null && !active.CanSteerQueuedMessages))
                    throw KernelException.PreconditionFailed("The runtime does not support steering this turn.");
                var message = existing ?? new SessionMessage($"inp_{Guid.NewGuid():N}", sessionId,
                    author, request.RequestKey!, hash, turnId, null, request.Content!, "dispatching", NowMs(), "steer");
                if (existing is null) await Messages.InsertAsync(message, ct).ConfigureAwait(false);
                else if (!await Messages.ClaimQueuedSteerAsync(message, turnId, ct).ConfigureAwait(false))
                    throw KernelException.Conflict("This message is already being sent or has been withdrawn.");
                _scheduler.TryGetSteering(sessionId, out var previous);
                var send = RunSteeringAsync(previous.Attempt, message, turnId, existing is not null, startedAt);
                _scheduler.SetSteering(sessionId, (message.Id, send));
                _ = TrackBackgroundJob(() => send);
                NotifyQueueChangedCore(sessionId);
                return CompleteSteeringReceiptAsync(send, sessionId, turnId, false);
            }

            var activeTask = TryGetActiveTaskForSessionCore(sessionId)?.Active;
            var busy = _scheduler.IsOccupied(session);
            var resuming = activeTask?.Paused == true && !busy && request.Mode == "default";
            var occupied = busy || await _scheduler.HasQueuedMessageAsync(session, ct).ConfigureAwait(false);
            if (occupied && request.Mode != "queue" && !resuming)
                throw KernelException.Conflict("The session already has an active or unresolved turn.");
            if (occupied || request.Mode == "queue")
            {
                RequireWorkspaceReadyCore();
                RequireDispatchWorkspace(session);
                var message = new SessionMessage($"inp_{Guid.NewGuid():N}", sessionId, author,
                    request.RequestKey!, hash, null, null, request.Content!, "queued", NowMs(), "queue");
                await Messages.InsertAsync(message, ct).ConfigureAwait(false);
                // Persist the resume input before opening the existing queue for dispatch.
                if (resuming) activeTask!.Paused = false;
                await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                NotifyQueueChangedCore(sessionId);
                return Task.FromResult(MessageReceipt(message));
            }
            session = session with { Status = "queued", UpdatedAtMs = NowMs() };
            var evt = SessionUpdatedEvent(session);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            var accepted = await _store.SessionInputs.AcceptAsync(session,
                new(request.RequestKey!, hash, request.Content!, author), evt, ct).ConfigureAwait(false);
            if (resuming) activeTask!.Paused = false;
            RegisterAcceptedInput(session, accepted);
            await PublishInputEventAsync(evt).ConfigureAwait(false);
            return Task.FromResult(new SessionMessageReceipt(accepted.Receipt.MessageId, sessionId,
                accepted.Receipt.TurnId, accepted.Receipt.DeliveryState));
        }, ct).ConfigureAwait(false);
        return await delivery.WaitAsync(ct).ConfigureAwait(false);
    }

    private static SessionMessageReceipt MessageReceipt(SessionMessage message, bool replay = false) =>
        new(message.Id, message.SessionId, message.DeliveredTurnId ?? message.TurnId ?? message.TargetTurnId, message.Delivery, replay);

    private static async Task<SessionMessageReceipt> CompleteSteeringReceiptAsync(Task<SteeringResult> send,
        string sessionId, string turnId, bool replay)
    {
        var result = await send.ConfigureAwait(false);
        return new(result.InputId, sessionId, turnId, result.Delivery switch
        { "accepted" => "delivered", "unconfirmed" => "unknown", _ => result.Delivery }, replay);
    }

    public Task WithdrawSessionMessageAsync(string sessionId, string messageId,
        InteropPrincipal caller, CancellationToken ct) => RemoveQueuedMessageAsync(sessionId, messageId, ct, principal: caller);

    public async Task<AgentSessionDto> ControlSessionAsync(string sessionId, SessionControlRequest request,
        InteropPrincipal caller, CancellationToken ct)
    {
        switch (request.Action)
        {
            case "stop":
                if (request.TurnId is null) throw KernelException.InvalidArgument("stop requires turnId.");
                var (stop, snapshot) = await EnqueueAsync(async _ =>
                {
                    await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                    var session = RequireSessionCore(sessionId);
                    RequireSessionMutationCore(session, caller, request.ExpectedEpoch, destructive: true);
                    var activeTask = TryGetActiveTaskForSessionCore(sessionId)?.Active;
                    if (_scheduler.TryGetActive(sessionId, out var active) && active.TurnDto.Id == request.TurnId)
                    {
                        var stopping = RequestTurnStop(active, pauseTask: activeTask);
                        return (stopping, session with { Status = "stopping", UpdatedAtMs = NowMs() });
                    }
                    if (_runtimes.Get(sessionId).CancelLaunch(request.TurnId))
                    {
                        if (activeTask is not null)
                        {
                            activeTask.Paused = true;
                            _mcpBridge.RevokeSession(sessionId);
                        }
                        var stopping = session with { Status = "stopping", UpdatedAtMs = NowMs() };
                        await PersistAndEmitSessionUpdatedCoreAsync(stopping, stopping.UpdatedAtMs, CancellationToken.None).ConfigureAwait(false);
                        return (Task.CompletedTask, stopping);
                    }
                    throw KernelException.Conflict("The observed turn is no longer current.");
                }, ct).ConfigureAwait(false);
                await stop.WaitAsync(ct).ConfigureAwait(false);
                // Stopping persistence may be waiting for SQLite; do not re-enter the mailbox for the response.
                return snapshot;
            case "reconnect":
            case "reset_context":
                return await RecoverSessionAsync(sessionId, request.Action, ct, caller, request.ExpectedEpoch).ConfigureAwait(false);
            case "compact":
                return await CompactSessionAsync(sessionId, ct, caller, request.ExpectedEpoch).ConfigureAwait(false);
            default:
                throw KernelException.InvalidArgument("Unknown session control action.");
        }
    }

    public async Task<SessionInteractionResult> RespondInteractionAsync(string sessionId, string interactionId,
        RespondInteractionRequest request, InteropPrincipal caller, CancellationToken ct)
    {
        if (request.Answers is { } answer)
        {
            await AnswerAskAsync(interactionId, answer, ct, caller, sessionId).ConfigureAwait(false);
            return new(interactionId, "ask");
        }
        if (request.ExecutionId is null || request.OptionId is null)
            throw KernelException.InvalidArgument("Approval responses require executionId and optionId.");
        await DecideApprovalAsync(request.ExecutionId, interactionId, request.OptionId, ct, caller, sessionId).ConfigureAwait(false);
        return new(interactionId, "approval");
    }
}
