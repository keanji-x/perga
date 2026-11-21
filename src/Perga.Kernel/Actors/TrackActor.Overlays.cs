using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static bool IsWorkingSessionStatus(string status) =>
        status is "queued" or "running" or "stopping" or "compacting";

    private bool IsSessionBlockingTrack(AgentSessionDto session) =>
        session.BoundTaskId is null ||
        (session.BoundAttemptId is { } attemptId && _activeAttempts.ContainsKey(attemptId));

    internal Task<TrackOverlayDto> MergeOverlayAsync(TrackOverlayDto stored, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var open = _track.ClosedAtMs is null;
            var sessions = _sessions.Values.Where(s => s.ArchivedAtMs is null).ToArray();
            return stored with
            {
                IsWorking = open && (
                    IsSessionOrTaskRunning ||
                    _workspacePreparation?.Phase is WorkspacePreparationPhase.Pending or
                        WorkspacePreparationPhase.Provisioning or WorkspacePreparationPhase.Mounting or
                        WorkspacePreparationPhase.Initializing or WorkspacePreparationPhase.Provisioned ||
                    sessions.Any(s => IsWorkingSessionStatus(s.Status)) ||
                    _tasks.Any(t => t.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)),
                OpenAskCount = open ? _asks.Values.Count(a => a.Status == AskStatus.Open) : 0,
                PendingApprovalCount = open ? PendingApprovalCount : 0,
                HasBlockedSession = open && sessions.Any(s =>
                    string.Equals(s.Status, "unknown", StringComparison.OrdinalIgnoreCase) &&
                    IsSessionBlockingTrack(s)),
                WorkspaceFailed = open && _workspacePreparation?.Phase is
                    WorkspacePreparationPhase.Failed or WorkspacePreparationPhase.Unknown,
                UpdatedAtMs = Math.Max(stored.UpdatedAtMs, LastActivityAtMs),
                NotifiableSessions = sessions
                    .Where(s => s.BoundTaskId is null && s.LastNotifiableTurnAtMs is not null)
                    .OrderBy(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new NotifiableSessionWatermarkDto(s.Id, s.LastNotifiableTurnAtMs!.Value))
                    .ToArray()
            };
        }, ct);
}
