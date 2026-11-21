using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    // The pending approval itself owns its notification. Detaching it on decision,
    // expiry or shutdown also removes all undispatched notification work.
    private bool TryScheduleApprovalNotificationCore(string sessionId)
    {
        var pending = _approvals.Values.Where(a => a.Dto.DelegateSessionId == sessionId && !a.NotificationScheduled).ToArray();
        if (pending.Length == 0) return false;
        foreach (var approval in pending) approval.NotificationScheduled = true;
        var reservation = Guid.NewGuid();
        _scheduler.Reserve(sessionId, reservation);
        _ = TrackBackgroundJob(async () =>
        {
            try { await _turnRunner.RunAsync(new ApprovalNotificationWorkItem(this, sessionId, pending, reservation), _actorShutdownCts.Token).ConfigureAwait(false); }
            finally
            {
                if (!_actorShutdownCts.IsCancellationRequested)
                    await EnqueueAsync(async _ =>
                    {
                        if (_scheduler.TryGetReservation(sessionId, out var held) && held == reservation)
                            _scheduler.ReleaseReservation(sessionId);
                        await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
            }
        });
        return true;
    }

    private sealed class ApprovalNotificationWorkItem(TrackActor actor, string sessionId,
        PendingApproval[] pending, Guid reservation) : ReservedTurnWorkItem(actor, sessionId, reservation)
    {
        public override string TriggerKind => "system";
        public override Task<string?> PreparePromptAsync(CancellationToken ct) => Actor.EnqueueAsync(_ =>
            Task.FromResult(FinalizePromptCore(string.Empty)), ct);
        public override string? FinalizePromptCore(string prompt)
        {
            var live = pending.Where(a => Actor._approvals.ContainsKey(a.Dto.Id)).Select(a => a.Dto).ToArray();
            return live.Length == 0 ? null :
                "Pending approvals delegated to you. Use perga_session_respond to decide, or leave them for the human:\n" +
                JsonHelpers.ToElement(live).GetRawText();
        }
        public override SessionTurnDto? CreateUnstartedTurn() => null;
        public override Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId) => Task.CompletedTask;
        public override async Task OnTurnSettledAsync(TurnSettlement settlement) => await settlement.SettleAsync().ConfigureAwait(false);
    }
}
