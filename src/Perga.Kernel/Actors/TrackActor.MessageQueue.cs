using Perga.Kernel.Events;
using Perga.Kernel.Internal;
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
