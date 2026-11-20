using Perga.Contracts;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    internal Task<TrackOverlayDto> MergeOverlayAsync(TrackOverlayDto stored, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var open = _track.ClosedAtMs is null;
            var sessions = _sessions.Values.Where(s => s.ArchivedAtMs is null).ToArray();
            return stored with
            {
                IsWorking = open && (stored.IsWorking || IsSessionOrTaskRunning || WorkspacePreparationStatus == "pending" ||
                    sessions.Any(s => s.Status is "queued" or "running" or "stopping" or "compacting")),
                OpenAskCount = open ? stored.OpenAskCount : 0,
                PendingApprovalCount = open ? PendingApprovalCount : 0,
                HasBlockedSession = open && (stored.HasBlockedSession || sessions.Any(s => s.Status == "unknown" &&
                    (s.BoundTaskId is null || _activeAttempts.ContainsKey(s.BoundAttemptId ?? "")))),
                WorkspaceFailed = open && (WorkspacePreparationStatus is "failed" or "unknown"),
                UpdatedAtMs = Math.Max(stored.UpdatedAtMs, LastActivityAtMs),
                NotifiableSessions = sessions.Where(s => s.BoundTaskId is null && s.LastNotifiableTurnAtMs is not null)
                    .Select(s => new NotifiableSessionWatermarkDto(s.Id, s.LastNotifiableTurnAtMs!.Value)).ToArray()
            };
        }, ct);
}
