using Perga.Contracts;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private enum QueuedMessageState { Waiting, Steering, Sending, Processing, Unconfirmed, Removed, HandedOff }

    private sealed class QueuedMessage(string id, string hash, string text, long queuedAtMs)
    {
        public string Id { get; } = id;
        public string Hash { get; } = hash;
        public string Text { get; private set; } = text;
        public long QueuedAtMs { get; } = queuedAtMs;
        public QueuedMessageState State { get; set; }

        public void Remove(bool stored = false)
        {
            State = stored ? QueuedMessageState.HandedOff : QueuedMessageState.Removed;
            Text = string.Empty;
        }
    }

    private readonly Dictionary<(string SessionId, string RequestKey), QueuedMessage> _queuedMessageRequests = [];

    private QueuedMessage AddQueuedMessageCore(string sessionId, SendMessageRequest request, string hash)
    {
        var message = new QueuedMessage($"queued_{Guid.NewGuid():N}", hash, request.Content, NowMs());
        _queuedMessageRequests.Add((sessionId, request.IdempotencyKey), message);
        EnqueuePendingWakeCore(sessionId, "user", message.Text, inputId: message.Id, message: message);
        return message;
    }

    private QueuedMessage RequireQueuedMessageCore(string sessionId, string inputId) =>
        _queuedMessageRequests.FirstOrDefault(pair => pair.Key.SessionId == sessionId && pair.Value.Id == inputId
            && pair.Value.State is not (QueuedMessageState.Removed or QueuedMessageState.HandedOff)).Value
        ?? throw KernelException.NotFound("This message is no longer in the queue.");

    private bool HasPendingUserMessagesCore(string sessionId) =>
        _pendingWakes.TryGetValue(sessionId, out var pending) &&
        pending.Any(wake => wake.Message?.State is QueuedMessageState.Waiting or QueuedMessageState.Steering);

    private IReadOnlyList<QueuedSessionMessageDto> ReadQueuedMessagesCore(string sessionId) =>
        _queuedMessageRequests.Where(pair => pair.Key.SessionId == sessionId && pair.Value.State is not (QueuedMessageState.Removed or QueuedMessageState.HandedOff))
            .Select(pair => pair.Value).OrderBy(message => message.QueuedAtMs)
            .Select(message => new QueuedSessionMessageDto(message.Id, message.Text, message.QueuedAtMs, message.State switch
            {
                QueuedMessageState.Steering => "steering",
                QueuedMessageState.Sending => "sending",
                QueuedMessageState.Processing => "processing",
                QueuedMessageState.Unconfirmed => "unconfirmed",
                _ => "waiting",
            })).ToArray();

    private IReadOnlyList<string> ReadQueueHandoffsCore(string sessionId) =>
        _queuedMessageRequests.Where(pair => pair.Key.SessionId == sessionId && pair.Value.State == QueuedMessageState.HandedOff)
            .Select(pair => pair.Value).OrderByDescending(message => message.QueuedAtMs).Take(128).Select(message => message.Id).ToArray();

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
        // The notification carries only the existing session snapshot. Queue text stays in memory.
        var ct = _actorShutdownCts.Token;
        _ = TrackBackgroundJob(() => EnqueueAsync(async _ =>
        {
            var evt = SessionUpdatedEvent(RequireSessionCore(sessionId)) with { CreatedAtMs = NowMs() };
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.AppendEventAsync(evt, ct).ConfigureAwait(false);
            await PublishInputEventAsync(evt).ConfigureAwait(false);
        }, ct));
    }

    internal Task<SteeringResult> SteerQueuedMessageAsync(string sessionId, string inputId, string turnId, CancellationToken ct) =>
        AcceptSteeringAsync(sessionId, turnId, null, inputId, ct);

    internal Task RemoveQueuedMessageAsync(string sessionId, string inputId, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var message = RequireQueuedMessageCore(sessionId, inputId);
            if (message.State is QueuedMessageState.Steering or QueuedMessageState.Sending or QueuedMessageState.Processing)
                throw KernelException.Conflict("This message is already being sent.");

            message.Remove();
            SchedulePendingWakeCore(sessionId);
            NotifyQueueChangedCore(sessionId);
        }, ct);
}
