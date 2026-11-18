using Perga.AgentKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static readonly TimeSpan SteeringTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SteeringRequestTimeout = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, (string InputId, Task<SteeringResult> Attempt)> _steering = new(StringComparer.Ordinal);

    internal async Task<SteeringResult> SteerMessageAsync(string sessionId, string turnId, string content, CancellationToken ct)
    {
        var inputId = $"steer_{Guid.NewGuid():N}";
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
                RequireDispatchWorkspace(session);
                if (session.Status == "unknown")
                    throw KernelException.Conflict("Resolve the current turn before sending another message.");

                if ((!_activeTurns.TryGetValue(sessionId, out var active) || active.TurnDto.Id != turnId)
                    && !await _store.HasSessionTurnAsync(sessionId, turnId, admissionToken).ConfigureAwait(false))
                    throw KernelException.NotFound("The target turn was not found in this session.");

                admissionToken.ThrowIfCancellationRequested();
                _steering.TryGetValue(sessionId, out var previous);
                var next = RunSteeringAsync(previous.Attempt, sessionId, turnId, content, inputId, startedAt);
                _steering[sessionId] = (inputId, next);
                _ = TrackBackgroundJob(() => next);
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
        Task<SteeringResult>? previous, string sessionId, string turnId, string content, string inputId, long startedAt)
    {
        var ct = _actorShutdownCts.Token;
        try
        {
            if (previous is not null)
            {
                await ((Task)previous).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            var target = await EnqueueAsync(_ =>
            {
                RequireDispatchWorkspace(RequireSessionCore(sessionId));
                _activeTurns.TryGetValue(sessionId, out var current);
                return Task.FromResult(current?.TurnDto.Id == turnId ? current : null);
            }, ct).ConfigureAwait(false);
            var remaining = SteeringRequestTimeout - _timeProvider.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
                return new(inputId, "unconfirmed");

            var delivery = SteerDelivery.Declined;
            if (target is not null && target.AgentSession.SupportsNativeSteer)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(remaining < SteeringTimeout ? remaining : SteeringTimeout);
                try
                {
                    delivery = await target.AgentSession.SteerAsync(turnId, content, deadline.Token)
                        .WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    delivery = SteerDelivery.Unconfirmed;
                }
            }

            return delivery switch
            {
                SteerDelivery.Accepted => new(inputId, "accepted"),
                SteerDelivery.Unconfirmed => new(inputId, "unconfirmed"),
                SteerDelivery.Declined => await EnqueueAsync(_ =>
                {
                    if (_timeProvider.GetElapsedTime(startedAt) >= SteeringRequestTimeout)
                        return Task.FromResult(new SteeringResult(inputId, "unconfirmed"));

                    RequireSessionCore(sessionId);
                    EnqueuePendingWakeCore(sessionId, "user", content, inputId: inputId);
                    SchedulePendingWakeCore(sessionId);
                    return Task.FromResult(new SteeringResult(inputId, "queued"));
                }, ct).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unexpected steering outcome {delivery}."),
            };
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                await EnqueueAsync(_ =>
                {
                    if (_steering.TryGetValue(sessionId, out var latest) && latest.InputId == inputId)
                        _steering.Remove(sessionId);
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
        }
    }
}
