using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static readonly TimeSpan SteeringTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SteeringRequestTimeout = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, (string InputId, Task<SteeringResult> Attempt)> _steering = new(StringComparer.Ordinal);

    internal async Task<SteeringResult> SteerMessageAsync(string sessionId, string turnId, string content, CancellationToken ct)
    {
        return await AcceptSteeringAsync(sessionId, turnId, content, $"steer_{Guid.NewGuid():N}", ct).ConfigureAwait(false);
    }

    private async Task<SteeringResult> AcceptSteeringAsync(
        string sessionId, string turnId, string? content, string inputId, CancellationToken ct)
    {
        var startedAt = _timeProvider.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SteeringRequestTimeout);
        var admissionToken = deadline.Token;
        try
        {
            var attempt = await EnqueueAsync(async actor =>
            {
                admissionToken.ThrowIfCancellationRequested();
                await EnsureHydratedCoreAsync(admissionToken).ConfigureAwait(false);
                var session = RequireSessionCore(sessionId);
                if (session.BoundAttemptId is not null)
                    throw KernelException.InvalidArgument("Worker messages require an attempt target.");
                RequireDispatchWorkspace(session);
                if (session.Status == "unknown")
                    throw KernelException.Conflict("Resolve the current turn before sending another message.");

                QueuedMessage? message = null;
                if (content is null)
                {
                    message = RequireQueuedMessageCore(sessionId, inputId);
                    if (message.State != QueuedMessageState.Waiting)
                        throw KernelException.Conflict("This message is not waiting to be sent.");
                    if (!_activeTurns.TryGetValue(sessionId, out var running) || running.TurnDto.Id != turnId ||
                        !running.CanSteerQueuedMessages)
                        return Task.FromResult(new SteeringResult(inputId, "queued"));
                }
                else if ((!_activeTurns.TryGetValue(sessionId, out var active) || active.TurnDto.Id != turnId)
                    && !await _store.HasSessionTurnAsync(sessionId, turnId, admissionToken).ConfigureAwait(false))
                    throw KernelException.NotFound("The target turn was not found in this session.");

                admissionToken.ThrowIfCancellationRequested();
                if (message is not null)
                    message.State = QueuedMessageState.Steering;
                _steering.TryGetValue(sessionId, out var previous);
                var next = RunSteeringAsync(previous.Attempt, sessionId, turnId, content ?? message!.Text, inputId, startedAt, message);
                _steering[sessionId] = (inputId, next);
                _ = TrackBackgroundJob(() => next);
                if (message is not null)
                    NotifyQueueChangedCore(sessionId);
                return next;
            }, admissionToken).ConfigureAwait(false);

            return await attempt.WaitAsync(admissionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return new(inputId, "unconfirmed");
        }
    }

    private async Task<SteeringResult> RunSteeringAsync(
        Task<SteeringResult>? previous, string sessionId, string turnId, string content, string inputId, long startedAt, QueuedMessage? message)
    {
        var ct = _actorShutdownCts.Token;
        var delivery = SteerDelivery.Unconfirmed;
        SteeringResult? result = null;
        try
        {
            if (previous is not null)
                await ((Task)previous).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            delivery = await AttemptSteeringAsync(sessionId, turnId, content, startedAt, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                result = await EnqueueAsync(async _ =>
                {
                    if (_steering.TryGetValue(sessionId, out var latest) && latest.InputId == inputId)
                        _steering.Remove(sessionId);

                    if (delivery == SteerDelivery.Declined && _timeProvider.GetElapsedTime(startedAt) >= SteeringRequestTimeout)
                        delivery = SteerDelivery.Unconfirmed;

                    if (message is not null)
                    {
                        if (delivery == SteerDelivery.Accepted)
                        {
                            try
                            {
                                var item = new SessionItemDto(0, sessionId, turnId, "user",
                                    JsonHelpers.ToElement(new { v = 1, content = message.Text, triggerKind = "user", inputId, steered = true }),
                                    NowMs());
                                await _store.AppendSessionItemsAsync(sessionId, [item], ct).ConfigureAwait(false);
                                message.Remove(stored: true);
                            }
                            catch (Exception) when (!ct.IsCancellationRequested)
                            {
                                delivery = SteerDelivery.Unconfirmed;
                                message.State = QueuedMessageState.Unconfirmed;
                            }
                        }
                        else
                            message.State = delivery == SteerDelivery.Declined ? QueuedMessageState.Waiting : QueuedMessageState.Unconfirmed;
                        SchedulePendingWakeCore(sessionId);
                        NotifyQueueChangedCore(sessionId);
                    }
                    else if (delivery == SteerDelivery.Declined)
                    {
                        RequireSessionCore(sessionId);
                        EnqueuePendingWakeCore(sessionId, "user", content, inputId: inputId);
                        SchedulePendingWakeCore(sessionId);
                    }

                    return new SteeringResult(inputId, delivery switch
                    {
                        SteerDelivery.Accepted => "accepted",
                        SteerDelivery.Declined => "queued",
                        SteerDelivery.Unconfirmed => "unconfirmed",
                        _ => throw new InvalidOperationException($"Unexpected steering outcome {delivery}."),
                    });
                }, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return result!;
    }

    private async Task<SteerDelivery> AttemptSteeringAsync(
        string sessionId, string turnId, string content, long startedAt, CancellationToken ct)
    {
        var target = await EnqueueAsync(_ =>
        {
            RequireDispatchWorkspace(RequireSessionCore(sessionId));
            _activeTurns.TryGetValue(sessionId, out var current);
            return Task.FromResult(current?.TurnDto.Id == turnId ? current : null);
        }, ct).ConfigureAwait(false);
        var remaining = SteeringRequestTimeout - _timeProvider.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero)
            return SteerDelivery.Unconfirmed;
        if (target is null || !target.AgentSession.SupportsNativeSteer)
            return SteerDelivery.Declined;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(remaining < SteeringTimeout ? remaining : SteeringTimeout);
        try
        {
            return await target.AgentSession.SteerAsync(turnId, content, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SteerDelivery.Unconfirmed;
        }
    }
}
