using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    public Task<TrackDto> PatchTrackAsync(PatchTrackRequest req, CancellationToken ct = default)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (req.Closed is true)
                RequireTrackCanClose();
            var now = NowMs();
            var nextTrack = _track;
            var changed = false;

            if (req.Title is not null && !string.Equals(req.Title, nextTrack.Title, StringComparison.Ordinal))
            {
                nextTrack = nextTrack with { Title = req.Title };
                changed = true;
            }

            if (req.Closed.HasValue)
            {
                long? nextClosed = req.Closed.Value ? (nextTrack.ClosedAtMs ?? now) : null;
                if (nextClosed != nextTrack.ClosedAtMs)
                {
                    nextTrack = nextTrack with { ClosedAtMs = nextClosed };
                    changed = true;
                }
            }

            if (req.Pinned.HasValue)
            {
                long? nextPinned = req.Pinned.Value ? (nextTrack.PinnedAtMs ?? now) : null;
                if (nextPinned != nextTrack.PinnedAtMs)
                {
                    nextTrack = nextTrack with { PinnedAtMs = nextPinned };
                    changed = true;
                }
            }

            if (req.SortOrder.HasValue && Math.Abs(req.SortOrder.Value - nextTrack.SortOrder) > double.Epsilon)
            {
                nextTrack = nextTrack with { SortOrder = req.SortOrder.Value };
                changed = true;
            }

            if (req.EnabledPlugins is not null)
            {
                nextTrack = nextTrack with { EnabledPlugins = req.EnabledPlugins.ToArray() };
                _mcpBridge.UpdateTrackPlugins(TrackId, nextTrack.EnabledPlugins);
                changed = true;
            }

            if (req.LeadSessionId is not null && !string.Equals(req.LeadSessionId, nextTrack.LeadSessionId, StringComparison.Ordinal))
            {
                await SwitchLeadSessionCoreAsync(req.LeadSessionId, now, ct).ConfigureAwait(false);
                nextTrack = _track with
                {
                    Title = nextTrack.Title,
                    ClosedAtMs = nextTrack.ClosedAtMs,
                    PinnedAtMs = nextTrack.PinnedAtMs,
                    SortOrder = nextTrack.SortOrder,
                    EnabledPlugins = nextTrack.EnabledPlugins
                };
                changed = true;
            }

            if (changed)
            {
                var reopening = _track.ClosedAtMs is not null && nextTrack.ClosedAtMs is null;
                var closing = _track.ClosedAtMs is null && nextTrack.ClosedAtMs is not null;
                ct.ThrowIfCancellationRequested();
                nextTrack = nextTrack with { UpdatedAtMs = now };
                _track = nextTrack;
                SetApprovalAdmissionClosed(nextTrack.ClosedAtMs is not null || _deletingTrack, stopPendingTurns: nextTrack.ClosedAtMs is not null);
                await EmitTrackUpdatedCoreAsync(now, reopening || closing ? _actorShutdownCts.Token : ct).ConfigureAwait(false);
                if (closing) CloseTrackRuntimesCore();
                if (req.LeadSessionId is not null) await _scheduler.PokeLeadAsync().ConfigureAwait(false);
            }

            if (req.Closed is false)
            {
                StartWorkspacePreparationCore(retry: false);
                await ResumeWorkspaceWorkCoreAsync(_actorShutdownCts.Token).ConfigureAwait(false);
            }

            return PresentTrackCore();
        }, ct);
    }

    public Task<TrackDto> RenameTrackBySessionAsync(string callerSessionId, string newTitle, CancellationToken ct = default, InteropPrincipal.Session? authorization = null)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireMcpAuthorizationCore(authorization);
            RequireGovernanceSessionCore(callerSessionId, "track_rename");

            var now = NowMs();
            _track = _track with { Title = newTitle, UpdatedAtMs = now };
            await EmitTrackUpdatedCoreAsync(now, ct).ConfigureAwait(false);
            return PresentTrackCore();
        }, ct);
    }

    public Task<TrackDto> CloseTrackBySessionAsync(string callerSessionId, CancellationToken ct = default, InteropPrincipal.Session? authorization = null) =>
        RunWithMailboxReservationAsync(
            reserve: async () =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                RequireMcpAuthorizationCore(authorization);
                RequireGovernanceSessionCore(callerSessionId, "track_close");
                RequireTrackCanClose();
                if (_closingTrack || _deletingTrack) throw KernelException.Conflict("Track teardown is already in progress.");
                ct.ThrowIfCancellationRequested();
                _closingTrack = true;
                foreach (var sessionId in _sessions.Keys) RevokeSessionRuntime(sessionId);
                return true;
            },
            run: async _ =>
            {
                await _taskCoordinator.CancelAllInFlightTasksAsync("Track closed.", ct).ConfigureAwait(false);
                return await EnqueueAsync(async _ =>
                {
                    var now = NowMs();
                    _track = _track with { ClosedAtMs = _track.ClosedAtMs ?? now, UpdatedAtMs = now };
                    SetApprovalAdmissionClosed(true, stopPendingTurns: true);
                    await EmitTrackUpdatedCoreAsync(now, CancellationToken.None).ConfigureAwait(false);
                    CloseTrackRuntimesCore();
                    return PresentTrackCore();
                }, CancellationToken.None).ConfigureAwait(false);
            },
            release: _ =>
            {
                _closingTrack = false;
                return Task.CompletedTask;
            });

    private void RequireTrackCanClose()
    {
        if (_memoryBinding is not null)
            throw KernelException.Forbidden("Memory Planner Tracks cannot be closed.");
    }

    private void CloseTrackRuntimesCore()
    {
        ScheduleWatchRearmCore(null);
        foreach (var sessionId in _sessions.Keys)
            ScheduleSessionRuntimeCloseCore(sessionId, revokeToken: true);
    }

    public async Task TeardownAndDeleteTrackAsync(CancellationToken ct = default, Func<CancellationToken, Task>? delete = null)
    {
        var reserved = false;
        try
        {
            await DeleteTrackCoreAsync(reserve: () => reserved = true, committed: () => reserved = false, ct, delete ?? (token => _store.DeleteTrackAsync(TrackId, token))).ConfigureAwait(false);
        }
        catch
        {
            if (reserved)
                await EnqueueAsync(_ =>
                {
                    _deletingTrack = false;
                    SetApprovalAdmissionClosed(_track.ClosedAtMs is not null);
                    return Task.CompletedTask;
                }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DeleteTrackCoreAsync(Action reserve, Action committed, CancellationToken ct, Func<CancellationToken, Task> delete)
    {
        await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);

            await RefreshForeignWorkspacePreparationCoreAsync(ct).ConfigureAwait(false);
            if (_workspacePreparationJob is { IsCompleted: false } ||
                _workspacePreparation is { Phase: WorkspacePreparationPhase.Provisioning or WorkspacePreparationPhase.Mounting or WorkspacePreparationPhase.Initializing })
                throw KernelException.Conflict("Wait for workspace setup to finish before deleting this Track.");
            if (_workspacePreparation is { Phase: WorkspacePreparationPhase.Unknown, Spec: null })
                throw KernelException.PreconditionFailed("Confirm the previous workspace allocation before deleting this Track.");

            if (_deletingTrack || _closingTrack)
                throw KernelException.Conflict("Track teardown is already in progress.");
            _taskCoordinator.RequireTasksOutsideVerification(_taskCoordinator.ActiveRuns);
            if (_scheduler.HasRecoveringSessions)
                throw KernelException.Conflict("Wait for provider recovery before deleting this Track.");
            ct.ThrowIfCancellationRequested();
            _deletingTrack = true;
            foreach (var sessionId in _sessions.Keys) RevokeSessionRuntime(sessionId);
            SetApprovalAdmissionClosed(true);
            reserve();

            // 1. Cancel active turns
            foreach (var turn in _scheduler.ActiveTurns.ToArray())
            {
                AbortTurn(turn);
            }

            // 2. Cancel pending ask waiters
            _interactions.CancelWaitersCore(ct);
        }, CancellationToken.None).ConfigureAwait(false);

        await _taskCoordinator.CancelAllInFlightTasksAsync("Track deleted.", ct).ConfigureAwait(false);
        (WorkspaceSpec? Spec, IWorkspaceDriver? Driver, IExecutionEnvironment? Env, string? CloseCmd, string Cwd, string AreaId) teardownInfo = await EnqueueAsync(async _ =>
        {
            // Kill open terminals after Worker shutdown has been confirmed.
            var env = _executionEnvironments.TryGet(_track.ExecutionTargetId);
            await _terminalLifecycle.TeardownCoreAsync(env, ct).ConfigureAwait(false);

            _mcpBridge.RevokeTrack(TrackId);

            var driver = env?.WorkspaceDrivers.GetRequired(_track.WorkspaceDriver);
            var spec = WorkspaceReady ? ExtractWorkspaceSpec(_track) : _workspacePreparation?.Spec;
            var closeCmd = WorkspaceReady ? _policy?.Manifest.WorkspaceCloseCmd : null;
            var cwd = _mountedWorkspace?.MergedCwd ?? _track.Cwd;
            _mountedWorkspace = null;
            return (spec, driver, env, closeCmd, cwd, _track.AreaId);
        }, CancellationToken.None).ConfigureAwait(false);

        await _runtimes.CloseAllAsync(_options.SessionStartupTimeout).ConfigureAwait(false);

        // Run WorkspaceCloseCmd before tearing down workspace
        if (!string.IsNullOrWhiteSpace(teardownInfo.CloseCmd) && teardownInfo.Env is not null)
        {
            try
            {
                var cwd = teardownInfo.Spec is null || teardownInfo.Driver is null
                    ? teardownInfo.Cwd
                    : (await teardownInfo.Driver.EnsureMountedAsync(teardownInfo.Spec, ct).ConfigureAwait(false)).MergedCwd;
                await teardownInfo.Env.Shell.RunAsync(
                    new ShellCommandRequest(
                        teardownInfo.CloseCmd,
                        cwd,
                        TimeSpan.FromSeconds(_options.DefaultHookTimeoutSecs)),
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close command before physical teardown.
            }
        }

        if (teardownInfo.Spec is not null && teardownInfo.Driver is not null)
        {
            try
            {
                await teardownInfo.Driver.TeardownTrackAsync(teardownInfo.Spec, ct).ConfigureAwait(false);
            }
            catch (WorkspaceUnavailableException ex)
            {
                throw KernelException.PreconditionFailed(ex.Message);
            }
        }
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await delete(ct).ConfigureAwait(false);
        committed();

        if (teardownInfo.Spec is not null && teardownInfo.Driver is not null)
        {
            try
            {
                await teardownInfo.Driver.CleanupDeletedTrackAsync(teardownInfo.Spec).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Could not clean up the deleted workspace for Track {0}: {1}", TrackId, ex.Message);
            }
        }

        var delNow = NowMs();
        var delEvt = new EventEnvelopeDto(
            EventId: null,
            AreaId: teardownInfo.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.TrackDeleted,
            Payload: JsonHelpers.ToElement(new { id = TrackId, trackId = TrackId, areaId = teardownInfo.AreaId, deleted = true }),
            CreatedAtMs: delNow);
        await _store.AppendEventAsync(delEvt, CancellationToken.None).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, delEvt, CancellationToken.None, allowFallback: false).ConfigureAwait(false);
    }

    // ------------------- Report Read & Commit -------------------

    private async Task EmitTrackUpdatedCoreAsync(long now, CancellationToken ct)
    {
        var evt = new EventEnvelopeDto(
            EventId: null,
            AreaId: _track.AreaId,
            TrackId: TrackId,
            Kind: KernelEventKinds.TrackUpdated,
            Payload: JsonHelpers.ToElement(PresentTrackCore()),
            CreatedAtMs: now);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpdateTrackAndEmitAsync(_track, evt, ct).ConfigureAwait(false);
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
    }

}
