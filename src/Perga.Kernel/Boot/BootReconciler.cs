using System.Text.Json;
using Perga.Contracts;
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
        var queuedInputs = await _store.RecoverSessionInputsAsync(ct).ConfigureAwait(false);
        var affectedTrackIds = new HashSet<string>(StringComparer.Ordinal);
        var discardedLeases = 0;

        // 1. Scan surviving in-flight task attempts before DB marks them interrupted so we can discard surviving workspace leases.
        var allTracks = await _store.ListTracksAsync(areaId: null, includeClosed: true, ct).ConfigureAwait(false);
        foreach (var track in allTracks)
        {
            var aggregate = await _store.LoadTrackAggregateAsync(track.Id, ct).ConfigureAwait(false);
            if (aggregate is null)
            {
                continue;
            }

            foreach (var session in aggregate.Sessions)
            {
                if (string.Equals(session.Status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    affectedTrackIds.Add(track.Id);
                }
            }

            foreach (var task in aggregate.Tasks)
            {
                if (task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)
                {
                    affectedTrackIds.Add(track.Id);
                }

                var attempts = await _store.ListAttemptsForTaskAsync(task.Id, ct).ConfigureAwait(false);
                foreach (var attempt in attempts)
                {
                    if (!IsInFlightAttemptStatus(attempt.Status))
                    {
                        continue;
                    }

                    affectedTrackIds.Add(track.Id);
                    if (attempt.WorkspaceLease is { ValueKind: JsonValueKind.Object } leaseElem)
                    {
                        var lease = TryParseAttemptLease(leaseElem, track.WorkspaceDriver, attempt.AttemptId, track.Cwd);
                        if (lease is not null)
                        {
                            var driver = _workspaceDrivers.GetRequired(lease.DriverId);
                            await driver.SettleAttemptLeaseAsync(lease, AttemptLeaseDisposition.Discard, ct)
                                .ConfigureAwait(false);
                            discardedLeases++;
                        }
                    }
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

        foreach (var (trackId, input) in queuedInputs)
            _actorRegistry.GetOrActivate(trackId).DispatchAcceptedInput(input);

        return new BootReconcileResult(orderedAffected, discardedLeases, wokenCount);
    }

    private static bool IsInFlightAttemptStatus(string? status)
    {
        return string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "Verifying", StringComparison.OrdinalIgnoreCase);
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
