using Perga.Kernel.Actors;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.SystemUpgrade;

public sealed class UpgradeReceiptConsumer(ITrackStore store, ISystemUpgradeReceiptSource? source,
    TrackActorRegistry actors, EventHub? events = null, DispatchAdmission? admission = null)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly HashSet<string> _dispatching = new(StringComparer.Ordinal);

    public async Task ImportAsync(CancellationToken ct)
    {
        if (source is null || store is not ISystemUpgradeStore upgrades)
        {
            return;
        }
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                var receipt = await source.ReadAsync(ct).ConfigureAwait(false);
                if (events is not null)
                {
                    await events.EnsureInitializedFromStoreAsync(store, ct).ConfigureAwait(false);
                }
                if (receipt is not null && await upgrades.ConsumeUpgradeReceiptAsync(receipt, ct).ConfigureAwait(false) && events is not null)
                {
                    await events.PublishSyncedAfterStoreWriteAsync(store, new(null, null, receipt.NotifyTrackId,
                        "system.upgrade_result", JsonHelpers.ToElement(receipt), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
                    events.PublishTransient(null, null, "system.upgrade_status",
                        JsonHelpers.ToElement(new SystemUpgradeStatusPayload(admission?.Draining ?? false, admission?.ActiveDispatches ?? 0, receipt)),
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A malformed or temporarily unreadable receipt must not block older durable wakes.
                System.Diagnostics.Trace.TraceWarning("Upgrade receipt import: {0}", ex.Message);
            }
            var pending = await store.ListPendingMailWakesAsync(ct).ConfigureAwait(false);
            _dispatching.IntersectWith(pending.Select(m => m.Id));
            foreach (var mail in pending)
            {
                if (mail.FromTrackId != "system:upgrade" || !_dispatching.Add(mail.Id))
                {
                    continue;
                }
                // Never await a provider turn from boot or the watcher. Durable wake_pending
                // survives a crash, and the actor acknowledges only after the wake settles.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await actors.GetOrActivate(mail.ToTrackId).WakeForMailAsync(mail, ct, recovering: true, waitForSettlement: true).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning("Upgrade wake {0}: {1}", mail.Id, ex.Message);
                        await upgrades.MarkUpgradeNotificationFailedAsync(mail.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        await _mutex.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            _dispatching.Remove(mail.Id); } finally { _mutex.Release();
                        }
                    }
                }, CancellationToken.None);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }
}
