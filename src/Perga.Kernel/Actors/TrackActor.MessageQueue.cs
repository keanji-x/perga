using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private const string InstallationAuthor = "owner:installation";
    private ISessionMessageStore Messages => _store.SessionMessages ?? throw new InvalidOperationException("Session message storage is unavailable.");

    private async Task<(IReadOnlyList<QueuedSessionMessageDto> Queue, IReadOnlyList<string> Handoffs)> ReadMessageQueueCoreAsync(
        string sessionId, CancellationToken ct)
    {
        if (_store.SessionMessages is null) return ([], []);
        var messages = await Messages.ListQueueAsync(sessionId, ct).ConfigureAwait(false);
        var queue = messages.Select(message => new QueuedSessionMessageDto(message.Id, message.Content, message.CreatedAtMs,
            message.Delivery switch { "dispatching" => "steering", "unknown" => "unconfirmed", _ => "waiting" })).ToArray();
        return (queue, await Messages.ListHandoffsAsync(sessionId, ct).ConfigureAwait(false));
    }

    private async Task SchedulePendingWakeCoreAsync(string sessionId, bool allowWorker = true)
    {
        if (!WorkspaceReady || _track.ClosedAtMs is not null || _sessionResumeHolds.ContainsKey(sessionId) ||
            _activeTurns.ContainsKey(sessionId) || _steering.ContainsKey(sessionId) || _compactingSessions.Contains(sessionId) || _reservedWakeSessions.ContainsKey(sessionId) ||
            !_sessions.TryGetValue(sessionId, out var session) || HoldsDurableSlot(session))
            return;

        ActiveAttemptState? attempt = null;
        if (session.BoundAttemptId is { } attemptId &&
            (!allowWorker || !_activeAttempts.TryGetValue(attemptId, out attempt) || attempt.Paused || attempt.Settling ||
                attempt.InterruptionUnconfirmed || !string.Equals(attempt.AttemptDto.Status, "running", StringComparison.OrdinalIgnoreCase) ||
                RequireTaskCore(attempt.TaskId).LatestAttemptId != attemptId))
            return;

        if (_store.SessionMessages is not null &&
            await Messages.NextAsync(sessionId, session.BoundAttemptId, CancellationToken.None).ConfigureAwait(false) is { } next)
        {
            if (attempt is not null)
                await ScheduleWorkerMessageCoreAsync(next, attempt).ConfigureAwait(false);
            else
            {
                var queued = session with { Status = "queued", UpdatedAtMs = NowMs() };
                var evt = SessionUpdatedEvent(queued);
                await _eventHub.EnsureInitializedFromStoreAsync(_store, CancellationToken.None).ConfigureAwait(false);
                var input = await _store.SessionInputs.PromoteQueuedAsync(queued, next, evt, CancellationToken.None).ConfigureAwait(false);
                if (input is not null)
                {
                    RegisterAcceptedInput(queued, input);
                    await PublishInputEventAsync(evt).ConfigureAwait(false);
                }
            }
            return;
        }

        if (!_pendingWakes.TryGetValue(sessionId, out var wakes) || !wakes.TryDequeue(out var wake)) return;
        if (wake.MailId is not null) Interlocked.Decrement(ref _queuedMailWakes);
        var reservation = Guid.NewGuid();
        _reservedWakeSessions.Add(sessionId, reservation);
        _ = TrackBackgroundJob(() => RunReservedWakeAsync(sessionId, wake.TriggerKind, wake.Prompt, reservation,
            _actorShutdownCts.Token, wake.MailId, wake.Completion));
    }

    internal Task RecoverQueuedMessagesAsync(string sessionId, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await SchedulePendingWakeCoreAsync(sessionId).ConfigureAwait(false);
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
                           (running?.TurnDto.Id == item.TurnId && running.ProviderAccepted) => "processing",
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
        // Queue changes invalidate the existing session projection without publishing message content.
        var ct = _actorShutdownCts.Token;
        _ = TrackBackgroundJob(() => EnqueueAsync(async _ =>
        {
            var evt = SessionUpdatedEvent(RequireSessionCore(sessionId)) with { CreatedAtMs = NowMs() };
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.AppendEventAsync(evt, ct).ConfigureAwait(false);
            await PublishInputEventAsync(evt).ConfigureAwait(false);
        }, ct));
    }

    internal Task<SteeringResult> SteerQueuedMessageAsync(string sessionId, string inputId, string turnId, CancellationToken ct,
        string? callerSessionId = null, string? attemptId = null) =>
        AcceptSteeringAsync(sessionId, turnId, null, inputId, ct, callerSessionId: callerSessionId, attemptId: attemptId);

    internal Task RemoveQueuedMessageAsync(string sessionId, string inputId, CancellationToken ct, string? callerSessionId = null, string? attemptId = null) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireWorkerOperatorCore(callerSessionId);
            var session = RequireSessionCore(sessionId);
            if (callerSessionId is not null && session.BoundAttemptId is null)
                throw KernelException.Forbidden("This tool withdraws Worker messages on the current Track.");
            var worker = session.BoundAttemptId is not null;
            var message = await Messages.GetAsync(sessionId, worker, inputId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound("Message not found in this session.");
            if (attemptId is not null && attemptId != message.AttemptId)
                throw KernelException.Conflict("The message belongs to another Worker attempt.");
            if (message.Delivery == "withdrawn") return;
            if (message.TurnId is not null || !await Messages.TransitionAsync(message with { Delivery = "withdrawn" }, "queued", ct).ConfigureAwait(false))
                throw KernelException.Conflict("Only waiting messages can be withdrawn.");

            NotifyQueueChangedCore(sessionId);
            await SchedulePendingWakeCoreAsync(sessionId, allowWorker: false).ConfigureAwait(false);
        }, ct);
}
