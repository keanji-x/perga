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

    public async Task ImportAsync(CancellationToken ct, bool dispatch = true)
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
            var pendingTracks = await upgrades.PrepareUpgradeNotificationRetriesAsync(ct).ConfigureAwait(false);
            if (dispatch)
                foreach (var trackId in pendingTracks)
                    await actors.GetOrActivate(trackId).PokeLeadAsync(ct, allowInline: false).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }
}
