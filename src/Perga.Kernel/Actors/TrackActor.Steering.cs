using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static readonly TimeSpan SteeringTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SteeringRequestTimeout = TimeSpan.FromSeconds(15);

    private enum SteeringAttemptOutcome
    {
        Accepted,
        DeclinedByProvider,
        PreDispatchFailed,
        TurnUnavailable,
        Unconfirmed,
    }

    private async Task<SteeringResult> RunSteeringAsync(Task? previous, SessionMessage message,
        string turnId, bool queued, long startedAt)
    {
        var ct = _actorShutdownCts.Token;
        if (previous is not null)
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        string outcome;
        try
        {
            var delivery = await AttemptSteeringAsync(message, turnId, startedAt, ct).ConfigureAwait(false);
            outcome = delivery switch
            {
                SteeringAttemptOutcome.Accepted => "delivered",
                SteeringAttemptOutcome.PreDispatchFailed => queued ? "queued" : "rejected",
                SteeringAttemptOutcome.DeclinedByProvider when !queued => "rejected",
                SteeringAttemptOutcome.DeclinedByProvider when _timeProvider.GetElapsedTime(startedAt) < SteeringRequestTimeout => "queued",
                SteeringAttemptOutcome.TurnUnavailable => "rejected",
                _ => "unknown",
            };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            outcome = "unknown";
        }

        return await EnqueueAsync(async _ =>
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

    private async Task<SteeringAttemptOutcome> AttemptSteeringAsync(SessionMessage message, string turnId, long startedAt, CancellationToken ct)
    {
        var target = await EnqueueAsync(_ =>
        {
            var session = RequireSessionCore(message.SessionId);
            _taskCoordinator.RequireDispatchWorkspace(session);
            if (_taskCoordinator.IsSessionFrozenForDispatchCore(session))
                return Task.FromResult<ActiveTurnState?>(null);
            _scheduler.TryGetActive(message.SessionId, out var current);
            return Task.FromResult(current is not null && current.TurnDto.Id == turnId && !current.Interrupted ? current : null);
        }, ct).ConfigureAwait(false);
        var remaining = SteeringRequestTimeout - _timeProvider.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero) return SteeringAttemptOutcome.PreDispatchFailed;
        if (target is null) return SteeringAttemptOutcome.TurnUnavailable;
        if (!target.AgentSession.SupportsNativeSteer) return SteeringAttemptOutcome.DeclinedByProvider;

        IReadOnlyList<InputImageRef>? stagedImages = null;
        if (message.Attachments is { Count: > 0 } attachments)
        {
            if (!target.AgentSession.SupportsImages) return SteeringAttemptOutcome.PreDispatchFailed;
            using var stagingDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stagingDeadline.CancelAfter(remaining < SteeringTimeout ? remaining : SteeringTimeout);
            try
            {
                var list = new List<InputImageRef>(attachments.Count);
                foreach (var att in attachments)
                {
                    if (string.IsNullOrWhiteSpace(att.PhysicalPath) || !File.Exists(att.PhysicalPath))
                        return SteeringAttemptOutcome.PreDispatchFailed;
                    var ext = SessionAttachmentValidation.ExtensionForContentType(att.ContentType);
                    if (ext is null) return SteeringAttemptOutcome.PreDispatchFailed;
                    var stagedPath = await Env.Files
                        .StageAttachmentAsync(TrackId, att.Id, ext, att.PhysicalPath, att.Sha256, stagingDeadline.Token)
                        .WaitAsync(stagingDeadline.Token)
                        .ConfigureAwait(false);
                    list.Add(new InputImageRef(stagedPath, att.ContentType));
                }
                stagedImages = list;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Provider SteerAsync has not been invoked yet; never mark un-dispatched staging failures/timeouts as unknown.
                return SteeringAttemptOutcome.PreDispatchFailed;
            }

            remaining = SteeringRequestTimeout - _timeProvider.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero) return SteeringAttemptOutcome.PreDispatchFailed;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(remaining < SteeringTimeout ? remaining : SteeringTimeout);
        try
        {
            var delivery = await target.AgentSession.SteerAsync(turnId, message.Content, stagedImages, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            return delivery switch
            {
                SteerDelivery.Accepted => SteeringAttemptOutcome.Accepted,
                SteerDelivery.Declined => SteeringAttemptOutcome.DeclinedByProvider,
                _ => SteeringAttemptOutcome.Unconfirmed,
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SteeringAttemptOutcome.Unconfirmed;
        }
    }
}
