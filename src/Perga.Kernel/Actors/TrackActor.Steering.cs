using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static readonly TimeSpan SteeringTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SteeringRequestTimeout = TimeSpan.FromSeconds(15);
    private readonly Dictionary<string, (string InputId, Task Attempt)> _steering = new(StringComparer.Ordinal);

    internal Task<SteeringResult> SteerMessageAsync(string sessionId, string turnId, string content, CancellationToken ct, string? requestKey = null)
    {
        var key = requestKey ?? $"steer_{Guid.NewGuid():N}";
        ValidateRequestKey(key);
        return AcceptSteeringAsync(sessionId, turnId, content, key, ct,
            RequestIdentity.Hash(new { version = 1, turnId, content }));
    }

    private async Task<SteeringResult> AcceptSteeringAsync(
        string sessionId, string turnId, string? content, string keyOrId, CancellationToken ct, string? requestHash = null,
        string? callerSessionId = null, string? attemptId = null)
    {
        var startedAt = _timeProvider.GetTimestamp();
        var inputId = content is null ? keyOrId : $"steer_{Guid.NewGuid():N}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SteeringRequestTimeout);
        var admissionToken = deadline.Token;
        try
        {
            var attempt = await EnqueueAsync(async actor =>
            {
                await EnsureHydratedCoreAsync(admissionToken).ConfigureAwait(false);
                RequireWorkerOperatorCore(callerSessionId);
                var session = RequireSessionCore(sessionId);
                var worker = session.BoundAttemptId is not null;
                if (callerSessionId is not null && !worker)
                    throw KernelException.Forbidden("This tool targets Worker sessions on the current Track.");
                if (worker && content is not null)
                    throw KernelException.InvalidArgument("Worker messages require an attempt target.");

                SessionMessage? message = null;
                if (content is null)
                    message = await Messages.GetAsync(sessionId, worker, inputId, admissionToken).ConfigureAwait(false)
                        ?? throw KernelException.NotFound("Message not found in this session.");
                else if (_store.SessionMessages is not null)
                    message = await Messages.FindAsync(sessionId, worker: false, InstallationAuthor, keyOrId, admissionToken).ConfigureAwait(false);

                if (message is not null)
                {
                    inputId = message.Id;
                    if (content is not null)
                        RequireMatchingHash(message.RequestHash, requestHash!);
                    if (attemptId is not null && attemptId != message.AttemptId)
                        throw KernelException.Conflict("The message belongs to another Worker attempt.");
                    if ((content is not null && message.Kind != "steer") || (content is null && message.Kind is not ("queue" or "worker")))
                        throw KernelException.Conflict("This request was used for another message operation.");
                    var prior = content is null
                        ? await Messages.FindQueuedSteerAsync(message, turnId, admissionToken).ConfigureAwait(false)
                        : message.Delivery;
                    if (prior is not null)
                    {
                        if (content is not null && message.TargetTurnId != turnId)
                            throw KernelException.Conflict("A steering request is bound to its original turn.");
                        if (prior == "dispatching" && _steering.TryGetValue(sessionId, out var runningSteer) && runningSteer.InputId == inputId &&
                            runningSteer.Attempt is Task<SteeringResult> runningResult)
                            return runningResult;
                        return Task.FromResult(ToSteeringResult(message.Id, prior));
                    }
                    if (message.Delivery != "queued" || message.TurnId is not null)
                        throw KernelException.Conflict("This message is already being sent or has been withdrawn.");
                }

                RequireSessionDispatchAvailableCore(sessionId);
                RequireDispatchWorkspace(session);
                if (worker) RequireCurrentWorkerCore(sessionId, message!.AttemptId!);
                if (session.Status == "unknown")
                    throw KernelException.Conflict("Resolve the current turn before sending another message.");

                if (!_activeTurns.TryGetValue(sessionId, out var running) || running.TurnDto.Id != turnId || running.Interrupted)
                {
                    if (content is not null && !await _store.HasSessionTurnAsync(sessionId, turnId, admissionToken).ConfigureAwait(false))
                        throw KernelException.NotFound("The target turn was not found in this session.");
                    throw KernelException.Conflict("The target turn changed. Refresh the session before steering.");
                }
                if (message is not null && !running.CanSteerQueuedMessages)
                    return Task.FromResult(new SteeringResult(inputId, "queued"));

                var prepared = message is null
                    ? new SessionMessage(inputId, sessionId, null, InstallationAuthor, keyOrId, requestHash!, turnId, null,
                        content!, "dispatching", NowMs(), "steer")
                    : message;
                if (_store.SessionMessages is not null)
                {
                    if (message is null)
                        await Messages.InsertAsync(prepared, admissionToken).ConfigureAwait(false);
                    else if (!await Messages.ClaimQueuedSteerAsync(prepared, turnId, admissionToken).ConfigureAwait(false))
                        throw KernelException.Conflict("This message is already being sent or has been withdrawn.");
                    NotifyQueueChangedCore(sessionId);
                }

                _steering.TryGetValue(sessionId, out var previous);
                var next = RunSteeringAsync(previous.Attempt, prepared, turnId, queued: content is null, startedAt);
                _steering[sessionId] = (inputId, next);
                _ = actor.TrackBackgroundJob(() => next);
                return next;
            }, admissionToken).ConfigureAwait(false);
            return await attempt.WaitAsync(admissionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return new(inputId, "unconfirmed");
        }
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
                SteerDelivery.Accepted => "delivered",
                SteerDelivery.Declined when _timeProvider.GetElapsedTime(startedAt) < SteeringRequestTimeout => "queued",
                null => "rejected",
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
                    // Only a still-current Attempt can take back a declined message for a later turn.
                    if (outcome is "queued" or "rejected" && message.AttemptId is { } attemptId &&
                        (!_activeAttempts.TryGetValue(attemptId, out var attempt) || attempt.Settling ||
                         !string.Equals(attempt.AttemptDto.Status, "running", StringComparison.OrdinalIgnoreCase)))
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
                if (_steering.TryGetValue(message.SessionId, out var latest) && latest.InputId == message.Id)
                    _steering.Remove(message.SessionId);
                await SchedulePendingWakeCoreAsync(message.SessionId).ConfigureAwait(false);
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
        return saved ? settled : (await Messages.GetAsync(message.SessionId, message.AttemptId is not null, message.Id, ct)
            .ConfigureAwait(false)) ?? settled;
    }

    private static SteeringResult ToSteeringResult(string messageId, string delivery) => new(messageId, delivery switch
    {
        "delivered" => "accepted",
        "queued" => "queued",
        "rejected" or "withdrawn" or "interrupted" => "rejected",
        _ => "unconfirmed",
    });

    private async Task<SteerDelivery?> AttemptSteeringAsync(SessionMessage message, string turnId, long startedAt, CancellationToken ct)
    {
        var target = await EnqueueAsync(_ =>
        {
            var session = RequireSessionCore(message.SessionId);
            RequireDispatchWorkspace(session);
            if (message.AttemptId is { } attemptId)
            {
                try
                {
                    RequireCurrentWorkerCore(session.Id, attemptId);
                }
                catch (KernelException ex) when (ex.Code == KernelErrorCode.Conflict)
                {
                    return Task.FromResult<ActiveTurnState?>(null);
                }
            }
            _activeTurns.TryGetValue(message.SessionId, out var current);
            return Task.FromResult(current is not null && current.TurnDto.Id == turnId && !current.Interrupted ? current : null);
        }, ct).ConfigureAwait(false);
        var remaining = SteeringRequestTimeout - _timeProvider.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero) return SteerDelivery.Unconfirmed;
        if (target is null) return null;
        if (!target.AgentSession.SupportsNativeSteer) return SteerDelivery.Declined;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(remaining < SteeringTimeout ? remaining : SteeringTimeout);
        try
        {
            return await target.AgentSession.SteerAsync(turnId, message.Content, deadline.Token)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SteerDelivery.Unconfirmed;
        }
    }
}
