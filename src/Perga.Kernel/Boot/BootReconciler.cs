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
    private readonly ITrackStore _store;
    private readonly IWorkspaceDriverRegistry _workspaceDrivers;
    private readonly TrackActorRegistry _actorRegistry;

    public BootReconciler(
        ITrackStore store,
        IWorkspaceDriverRegistry workspaceDrivers,
        TrackActorRegistry actorRegistry)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspaceDrivers = workspaceDrivers ?? throw new ArgumentNullException(nameof(workspaceDrivers));
        _actorRegistry = actorRegistry ?? throw new ArgumentNullException(nameof(actorRegistry));
    }

    public async Task<BootReconcileResult> ReconcileAsync(CancellationToken ct = default)
    {
        // Snapshot before any recovered session can send new mail through live delivery.
        var pendingMail = await _store.ListPendingMailWakesAsync(ct).ConfigureAwait(false);
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

        // 3. Wake affected open tracks' LeadSessionId so Lead can inspect interrupted work and resume.
        var orderedAffected = affectedTrackIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var wokenCount = 0;

        foreach (var trackId in orderedAffected)
        {
            var track = await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false);
            if (track is null || track.ClosedAtMs is not null || string.IsNullOrEmpty(track.LeadSessionId))
            {
                continue;
            }

            // Ensure any cached actor reloads fresh post-reconcile snapshot from store.
            await _actorRegistry.RemoveAndDisposeAsync(trackId).ConfigureAwait(false);
            var actor = _actorRegistry.GetOrActivate(trackId);
            await actor.WakeLeadAsync(
                triggerKind: "system",
                wakePrompt: "System boot reconciliation completed: previous in-flight session turns and task attempts were marked interrupted and temporary workspace leases were discarded. Review track status and decide next steps.",
                ct: ct).ConfigureAwait(false);
            wokenCount++;
        }

        foreach (var trackId in preparingTracks)
            await _actorRegistry.GetOrActivate(trackId).WarmWorkspaceAsync(ct).ConfigureAwait(false);

        foreach (var (trackId, input) in queuedInputs)
            _actorRegistry.GetOrActivate(trackId).DispatchAcceptedInput(input);

        foreach (var mail in pendingMail)
            await _actorRegistry.GetOrActivate(mail.ToTrackId).WakeForMailAsync(mail, ct, recovering: true).ConfigureAwait(false);

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
