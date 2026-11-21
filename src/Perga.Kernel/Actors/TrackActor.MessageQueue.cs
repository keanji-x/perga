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

    internal Task ResumeSchedulingAsync(CancellationToken ct = default) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        await ScheduleEligibleTasksCoreAsync(ct).ConfigureAwait(false);
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

    private Task RemoveQueuedMessageAsync(string sessionId, string inputId, CancellationToken ct, string? attemptId, InteropPrincipal principal) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireSessionMutationCore(session, principal, attemptId);
            var worker = session.BoundAttemptId is not null;
            var message = await Messages.GetAsync(sessionId, worker, inputId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound("Message not found in this session.");
            if (attemptId is not null && attemptId != message.AttemptId)
                throw KernelException.Conflict("The message belongs to another Worker attempt.");
            if (message.Delivery == "withdrawn") return;
            if (message.TurnId is not null || message.Delivery is not ("queued" or "unknown") ||
                !await Messages.TransitionAsync(message with { Delivery = "withdrawn" }, message.Delivery, ct).ConfigureAwait(false))
                throw KernelException.Conflict("Only waiting messages can be withdrawn.");

            NotifyQueueChangedCore(sessionId);
            await PokeSessionCoreAsync(sessionId, allowWorker: false).ConfigureAwait(false);
        }, ct);
}
