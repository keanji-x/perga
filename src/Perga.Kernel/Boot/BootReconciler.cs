using Perga.Kernel.Actors;
using Perga.Kernel.Ports;
using Perga.VcsKit;

namespace Perga.Kernel.Boot;

public interface IBootReconciler
{
    Task<BootReconcileResult> ReconcileAsync(CancellationToken ct = default);
}

public sealed class BootReconciler : IBootReconciler
{
    private readonly SystemUpgrade.UpgradeReceiptConsumer? _upgrades;
    private readonly ITrackStore _store;
    private readonly IWorkspaceDriverRegistry _workspaceDrivers;
    private readonly TrackActorRegistry _actorRegistry;

    public BootReconciler(
        ITrackStore store,
        IWorkspaceDriverRegistry workspaceDrivers,
        TrackActorRegistry actorRegistry,
        SystemUpgrade.UpgradeReceiptConsumer? upgrades = null)
    {
        _upgrades = upgrades;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceDrivers = workspaceDrivers ?? throw new ArgumentNullException(nameof(workspaceDrivers));
        _actorRegistry = actorRegistry ?? throw new ArgumentNullException(nameof(actorRegistry));
    }

    public async Task<BootReconcileResult> ReconcileAsync(CancellationToken ct = default)
    {
        // Phase 1 converges persistence before activating any actor.
        var preparingTracks = await _store.RecoverWorkspacePreparationsAsync(ct).ConfigureAwait(false);
        var queuedInputs = await _store.RecoverSessionInputsAsync(ct).ConfigureAwait(false);
        var affectedTrackIds = new HashSet<string>(StringComparer.Ordinal);

        // Mark all in-flight turns & tasks as interrupted in persistent store.
        var storeAffected = await _store.MarkInFlightInterruptedOnBootAsync(ct).ConfigureAwait(false);
        foreach (var trackId in storeAffected)
        {
            affectedTrackIds.Add(trackId);
        }

        if (_upgrades is not null)
        {
            try { await _upgrades.ImportAsync(ct, dispatch: false).ConfigureAwait(false); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { System.Diagnostics.Trace.TraceWarning("Upgrade receipt import will retry in background: {0}", ex.Message); }
        }

        var recoveredWakeTracks = await _store.TrackWakes.RecoverOnBootAsync(ct).ConfigureAwait(false);
        var queues = _store.SessionMessages is { } messages
            ? await messages.ListRecoverableQueuesAsync(ct).ConfigureAwait(false) : [];
        var pendingWakeTracks = await _store.TrackWakes.ListPendingTracksAsync(ct).ConfigureAwait(false);
        var orderedAffected = affectedTrackIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var modifiedTracks = affectedTrackIds.Concat(preparingTracks).Concat(recoveredWakeTracks)
            .Concat(queuedInputs.Select(q => q.TrackId)).Concat(queues.Select(q => q.TrackId)).Concat(pendingWakeTracks).Distinct();
        foreach (var trackId in modifiedTracks)
            await _actorRegistry.RemoveAndDisposeAsync(trackId).ConfigureAwait(false);

        // Phase 2 activates consumers after every durable recovery write has completed.
        var wokenCount = 0;
        foreach (var trackId in preparingTracks)
        {
            try
            {
                await _actorRegistry.GetOrActivate(trackId).WarmWorkspaceAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning("Could not warm workspace for Track {0}: {1}", trackId, ex.Message);
            }
        }

        foreach (var (trackId, input) in queuedInputs)
            _actorRegistry.GetOrActivate(trackId).DispatchAcceptedInput(input);

        var leadTracks = new HashSet<string>(pendingWakeTracks, StringComparer.Ordinal);
        foreach (var (trackId, sessionId) in queues)
        {
            var track = await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false);
            if (track?.LeadSessionId == sessionId) leadTracks.Add(trackId);
            else
            {
                try
                {
                    await _actorRegistry.GetOrActivate(trackId).RecoverQueuedMessagesAsync(sessionId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    System.Diagnostics.Trace.TraceWarning("Could not recover queued messages for Track {0}/{1}: {2}", trackId, sessionId, ex.Message);
                }
            }
        }
        foreach (var trackId in leadTracks.Order(StringComparer.Ordinal))
        {
            var track = await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false);
            if (track is null || track.ClosedAtMs is not null || track.LeadSessionId is null) continue;
            try
            {
                await _actorRegistry.GetOrActivate(trackId).PokePendingWakesAsync(ct).ConfigureAwait(false);
                wokenCount++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning("Could not poke pending wakes for Track {0}: {1}", trackId, ex.Message);
            }
        }

        var pendingDispatch = await _store.ListPendingWorkerDispatchTracksAsync(ct).ConfigureAwait(false);
        var flowTracks = (await _store.ListTracksAsync(ct: ct).ConfigureAwait(false))
            .Where(track => track.FlowRev > 0).Select(track => track.Id);
        foreach (var trackId in pendingDispatch.Concat(flowTracks).Distinct(StringComparer.Ordinal))
        {
            try
            {
                await _actorRegistry.GetOrActivate(trackId).TriggerSchedulerAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning("Could not recover Worker dispatch for Track {0}: {1}", trackId, ex.Message);
            }
        }

        return new BootReconcileResult(orderedAffected, wokenCount);
    }
}
