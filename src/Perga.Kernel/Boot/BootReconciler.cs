using System.Text.Json;
using Perga.Kernel.Actors;
using Perga.Kernel.Internal;
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
        var discardedLeases = 0;

        // Recover leases before marking the owning attempts interrupted.
        var leases = await _store.ListInFlightAttemptLeasesAsync(ct).ConfigureAwait(false);
        foreach (var trackLeases in leases.GroupBy(lease => lease.TrackId))
        {
            var track = await _store.GetTrackAsync(trackLeases.Key, ct).ConfigureAwait(false);
            if (track is null)
            {
                continue;
            }

            foreach (var entry in trackLeases)
            {
                if (entry.WorkspaceLease.ValueKind == JsonValueKind.Object &&
                    TryParseAttemptLease(entry.WorkspaceLease, track.WorkspaceDriver, entry.AttemptId, track.Cwd) is { } lease)
                {
                    var driver = _workspaceDrivers.GetRequired(lease.DriverId);
                    await driver.SettleAttemptLeaseAsync(lease, AttemptLeaseDisposition.Discard, ct).ConfigureAwait(false);
                    discardedLeases++;
                }
            }
        }

        // 2. Mark all in-flight turns & attempts as interrupted in persistent store.
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
            await _actorRegistry.GetOrActivate(trackId).WarmWorkspaceAsync(ct).ConfigureAwait(false);

        foreach (var (trackId, input) in queuedInputs)
            _actorRegistry.GetOrActivate(trackId).DispatchAcceptedInput(input);

        var leadTracks = new HashSet<string>(pendingWakeTracks, StringComparer.Ordinal);
        foreach (var (trackId, sessionId) in queues)
        {
            var track = await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false);
            if (track?.LeadSessionId == sessionId) leadTracks.Add(trackId);
            else await _actorRegistry.GetOrActivate(trackId).RecoverQueuedMessagesAsync(sessionId, ct).ConfigureAwait(false);
        }
        foreach (var trackId in leadTracks.Order(StringComparer.Ordinal))
        {
            var track = await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false);
            if (track is null || track.ClosedAtMs is not null || track.LeadSessionId is null) continue;
            await _actorRegistry.GetOrActivate(trackId).PokeLeadAsync(ct).ConfigureAwait(false);
            wokenCount++;
        }

        foreach (var trackId in await _store.ListPendingWorkerDispatchTracksAsync(ct).ConfigureAwait(false))
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

        return new BootReconcileResult(orderedAffected, discardedLeases, wokenCount);
    }

    private static AttemptWorkspaceLease? TryParseAttemptLease(
        JsonElement element,
        string fallbackDriverId,
        string fallbackAttemptId,
        string fallbackCwd)
    {
        try
        {
            var parsed = JsonHelpers.Deserialize<AttemptWorkspaceLease>(element);
            if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.DriverId))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            // Fall through to tolerant manual extraction below.
        }

        var driverId = JsonHelpers.TryGetPropertyIgnoreCase(element, ["driverId", "driver_id"], out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? fallbackDriverId
            : fallbackDriverId;
        var attemptId = JsonHelpers.TryGetPropertyIgnoreCase(element, ["attemptId", "attempt_id"], out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() ?? fallbackAttemptId
            : fallbackAttemptId;
        var effectiveCwd = JsonHelpers.TryGetPropertyIgnoreCase(element, ["effectiveCwd", "effective_cwd", "cwd"], out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? fallbackCwd
            : fallbackCwd;
        var isCow = JsonHelpers.TryGetPropertyIgnoreCase(element, ["isIsolatedCopyOnWrite", "is_isolated_copy_on_write"], out var cow) &&
                    cow.ValueKind == JsonValueKind.True;

        return new AttemptWorkspaceLease(
            DriverId: driverId,
            AttemptId: attemptId,
            EffectiveCwd: effectiveCwd,
            IsIsolatedCopyOnWrite: isCow,
            Metadata: element.Clone());
    }
}
