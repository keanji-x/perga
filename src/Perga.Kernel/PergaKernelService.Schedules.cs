using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public async Task<IReadOnlyList<FiredTrackSchedule>> FireDueTrackSchedulesAsync(long nowMs, CancellationToken ct = default)
    {
        if (_store.TrackSchedules is not { } store) return [];
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var fired = await store.ListDueFlowClocksAsync(nowMs, ct).ConfigureAwait(false);
        var committed = new List<FiredTrackSchedule>(fired.Count);
        foreach (var item in fired)
        {
            try
            {
                if (await Actors.GetOrActivate(item.TrackId).FireFlowScheduleAsync(item, nowMs, ct)
                        .ConfigureAwait(false))
                    committed.Add(item);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning("Could not dispatch due Flow clock {0}: {1}", item.ScheduleId, ex.Message);
            }
        }
        foreach (var trackId in committed.Select(s => s.TrackId).Distinct())
            await PublishScheduleEventsAsync(trackId).ConfigureAwait(false);
        return committed;
    }

    private async Task PublishScheduleEventsAsync(string trackId)
    {
        try
        {
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store,
                new EventEnvelopeDto(null, null, trackId, KernelEventKinds.TrackSchedulesUpdated, JsonHelpers.ToElement(new { trackId }), NowMs()),
                CancellationToken.None, allowFallback: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not publish committed schedule events for Track {0}: {1}", trackId, ex);
        }
    }
}
