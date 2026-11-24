using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static readonly TimeSpan SteeringTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SteeringRequestTimeout = TimeSpan.FromSeconds(15);

    private async Task<SteeringResult> RunSteeringAsync(Task? previous, SessionMessage message,
        string turnId, bool queued, long startedAt, InteropPrincipal caller, ActiveTurnState? originatingTurn)
    {
        var ct = _actorShutdownCts.Token;
        if (previous is not null)
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        string outcome;
        KernelException? deterministicFailure = null;
        try
        {
            var delivery = await AttemptSteeringAsync(message, turnId, startedAt, caller, originatingTurn, ct).ConfigureAwait(false);
            outcome = delivery switch
            {
                SteerDelivery.Accepted => "delivered",
                SteerDelivery.Declined when !queued => "rejected",
                SteerDelivery.Declined => "queued",
                null => "rejected",
                _ => "unknown",
            };
        }
        catch (AttachmentDispatchException ex)
        {
            deterministicFailure = new KernelException(ex.Code == "attachments_unsupported"
                ? KernelErrorCode.AttachmentsUnsupported : KernelErrorCode.AttachmentUnavailable,
                ex.Message, innerException: ex);
            outcome = queued ? "queued" : "rejected";
        }
        catch (KernelException ex) when (caller is InteropPrincipal.Session)
        {
            deterministicFailure = ex;
            outcome = queued ? "queued" : "rejected";
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            outcome = "unknown";
        }

        var settled = await EnqueueAsync(async _ =>
        {
            string result;
            try
            {
                if (!queued)
                    result = (await SettleSteerCoreAsync(message, outcome, ct).ConfigureAwait(false)).Delivery;
                else
                {
                    // Only an open (non-frozen) session can take back a declined message for a later turn.
                    if (outcome is "queued" or "rejected" && _taskCoordinator.IsSessionFrozenForDispatchCore(RequireSessionCore(message.SessionId)))
                        outcome = "interrupted";
                    var item = outcome == "delivered" ? CreateSteeredItem(message, turnId) : null;
                    try
                    {
                        result = await Messages.SettleQueuedSteerAsync(message, turnId, outcome, item, ct).ConfigureAwait(false);
                    }
                    catch (Exception) when (item is not null && !ct.IsCancellationRequested)
                    {
                        result = await Messages.SettleQueuedSteerAsync(message, turnId, "unknown", null, ct).ConfigureAwait(false);
                    }
                    NotifyQueueChangedCore(message.SessionId);
                }
            }
            finally
            {
                if (_scheduler.TryGetSteering(message.SessionId, out var latest) && latest.InputId == message.Id)
                    _scheduler.EndSteering(message.SessionId);
                await PokeSessionCoreAsync(message.SessionId).ConfigureAwait(false);
            }
            return ToSteeringResult(message.Id, result);
        }, ct).ConfigureAwait(false);
        if (deterministicFailure is not null) throw deterministicFailure;
        return settled;
    }

    private SessionItemDto CreateSteeredItem(SessionMessage message, string turnId) =>
        new(0, message.SessionId, turnId, "user",
            JsonHelpers.ToElement(new { v = 1, content = message.Content, triggerKind = "user", inputId = message.Id, steered = true }), NowMs());

    private async Task<SessionMessage> SettleSteerCoreAsync(SessionMessage message, string outcome, CancellationToken ct)
    {
        var settled = message with { Delivery = outcome, DeliveredTurnId = outcome == "delivered" ? message.TargetTurnId : null };
        if (_store.SessionMessages is null) return settled;
        bool saved;
        if (outcome == "delivered")
        {
            var item = CreateSteeredItem(message, message.TargetTurnId!);
            try
            {
                saved = await Messages.DeliverSteerAsync(settled, item, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // A lost write acknowledgment must never make the prompt eligible for another send.
                settled = message with { Delivery = "unknown" };
                saved = await Messages.TransitionAsync(settled, "dispatching", ct).ConfigureAwait(false);
            }
        }
        else
            saved = await Messages.TransitionAsync(settled, "dispatching", ct).ConfigureAwait(false);

        NotifyQueueChangedCore(message.SessionId);
        return saved ? settled : (await Messages.GetAsync(message.SessionId, message.Id, ct)
            .ConfigureAwait(false)) ?? settled;
    }

    private static SteeringResult ToSteeringResult(string messageId, string delivery) => new(messageId, delivery switch
    {
        "delivered" => "accepted",
        "queued" => "queued",
        "rejected" or "withdrawn" or "interrupted" => "rejected",
        _ => "unconfirmed",
    });

    private async Task<SteerDelivery?> AttemptSteeringAsync(SessionMessage message, string turnId, long startedAt,
        InteropPrincipal caller, ActiveTurnState? originatingTurn, CancellationToken ct)
    {
        var target = await EnqueueAsync(_ =>
        {
            var session = RequireSessionCore(message.SessionId);
            RequireInteropCore(caller, session, write: true);
            _taskCoordinator.RequireDispatchWorkspace(session);
            if (_taskCoordinator.IsSessionFrozenForDispatchCore(session))
                return Task.FromResult<ActiveTurnState?>(null);
            _scheduler.TryGetActive(message.SessionId, out var current);
            return Task.FromResult(current is not null && current.TurnDto.Id == turnId && !current.Interrupted ? current : null);
        }, ct).ConfigureAwait(false);
        var remaining = SteeringRequestTimeout - _timeProvider.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero) return SteerDelivery.Declined;
        if (target is null) return null;
        if (!target.AgentSession.SupportsNativeSteer) return SteerDelivery.Declined;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(remaining < SteeringTimeout ? remaining : SteeringTimeout);
        var mayHaveWritten = 0;
        Task AuthorizeWriteAsync(CancellationToken token) => EnqueueAsync(_ =>
        {
            token.ThrowIfCancellationRequested();
            if (originatingTurn?.Interrupted == true)
                throw KernelException.Conflict("The originating turn stopped before steering was written.");
            var session = RequireSessionCore(message.SessionId);
            RequireInteropCore(caller, session, write: true);
            if (!_scheduler.TryGetActive(session.Id, out var active) || !ReferenceEquals(active, target) || target.Interrupted)
                throw KernelException.Conflict("The target turn changed before steering was written.");
            Volatile.Write(ref mayHaveWritten, 1);
            return Task.CompletedTask;
        }, token);

        try
        {
            return await target.AgentSession.SteerAsync(
                new TurnInput(message.Content, turnId, Attachments: ResolveTurnImages(message.Attachments)),
                deadline.Token, AuthorizeWriteAsync)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (KernelException) when (caller is InteropPrincipal.Human)
        {
            // A rejected write fence leaves the transport untouched.
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Volatile.Read(ref mayHaveWritten) == 0 ? SteerDelivery.Declined : SteerDelivery.Unconfirmed;
        }
    }
}
