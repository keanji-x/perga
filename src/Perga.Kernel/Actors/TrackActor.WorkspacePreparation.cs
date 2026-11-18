using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.VcsKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private WorkspacePreparation? _workspacePreparation;
    private Task? _workspacePreparationJob;
    private bool _deletingTrack;

    private bool WorkspaceReady => _workspacePreparation is null or { Phase: WorkspacePreparationPhase.Ready };

    private string? WorkspacePreparationStatus => _workspacePreparation?.Phase switch
    {
        null or WorkspacePreparationPhase.Ready => null,
        WorkspacePreparationPhase.Failed => "failed",
        WorkspacePreparationPhase.Unknown => "unknown",
        _ => "pending"
    };

    internal Task WarmWorkspaceAsync(CancellationToken ct = default) =>
        EnqueueAsync(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            StartWorkspacePreparationCore(retry: false);
            await ResumeWorkspaceWorkCoreAsync(_actorShutdownCts.Token).ConfigureAwait(false);
        }, ct);

    internal Task RetryWorkspaceAsync(CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Reopen this Track before retrying workspace setup.");
            if (_workspacePreparation?.Phase == WorkspacePreparationPhase.Unknown)
                throw KernelException.PreconditionFailed("The previous workspace setup result needs to be confirmed.");
            StartWorkspacePreparationCore(retry: true);
        }, ct);

    private async Task WaitForWorkspacePreparationAsync(CancellationToken ct)
    {
        var job = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await RefreshForeignWorkspacePreparationCoreAsync(ct).ConfigureAwait(false);
            StartWorkspacePreparationCore(retry: false);
            return _workspacePreparationJob;
        }, ct).ConfigureAwait(false);

        if (job is not null)
            await job.WaitAsync(ct).ConfigureAwait(false);

        await EnqueueAsync(_ =>
        {
            RequireWorkspaceReadyCore();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    private void RequireWorkspaceReadyCore()
    {
        if (_deletingTrack)
            throw KernelException.Conflict("This Track is being deleted.");
        if (!WorkspaceReady)
            throw KernelException.PreconditionFailed(WorkspacePreparationStatus switch
            {
                "failed" => "Workspace setup failed. Retry it in this Track.",
                "unknown" => "The previous workspace setup result needs to be confirmed.",
                _ => "The workspace is still being prepared."
            });
    }

    private void StartWorkspacePreparationCore(bool retry)
    {
        if (_deletingTrack || _workspacePreparation is not { } preparation || _track.ClosedAtMs is not null ||
            _workspacePreparationJob is { IsCompleted: false })
            return;
        if (preparation.Phase is WorkspacePreparationPhase.Pending or WorkspacePreparationPhase.Provisioned ||
            retry && preparation.Phase == WorkspacePreparationPhase.Failed)
        {
            var driver = _workspaceDrivers.GetRequired(_track.WorkspaceDriver);
            _workspacePreparation = preparation with { Phase = WorkspacePreparationPhase.Pending };
            _workspacePreparationJob = TrackBackgroundJob(() => PrepareWorkspaceAsync(preparation, driver, _actorShutdownCts.Token));
        }
    }

    private async Task PrepareWorkspaceAsync(WorkspacePreparation initial, IWorkspaceDriver driver, CancellationToken ct)
    {
        var preparation = initial with { Phase = initial.Spec is null
            ? WorkspacePreparationPhase.Provisioning : WorkspacePreparationPhase.Mounting };
        if (!await _store.TryClaimWorkspacePreparationAsync(TrackId, initial.Phase, preparation, ct).ConfigureAwait(false))
        {
            await EnqueueAsync(_ => RefreshWorkspacePreparationCoreAsync(ct), ct).ConfigureAwait(false);
            return;
        }

        MountedWorkspace? mounted = null;
        try
        {
            if (preparation.Spec is null)
            {
                var spec = await driver.ProvisionTrackAsync(preparation.Request, ct).ConfigureAwait(false);
                preparation = preparation with { Spec = spec, Phase = WorkspacePreparationPhase.Mounting };
                await _store.SaveWorkspacePreparationAsync(TrackId, preparation, null, null, ct).ConfigureAwait(false);
            }

            mounted = await driver.EnsureMountedAsync(preparation.Spec!, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(preparation.Command))
            {
                preparation = preparation with { Phase = WorkspacePreparationPhase.Initializing };
                await _store.SaveWorkspacePreparationAsync(TrackId, preparation, null, null, ct).ConfigureAwait(false);
                var result = await _commandRunner.RunAsync(new ShellCommandRequest(
                    preparation.Command, mounted.MergedCwd, TimeSpan.FromSeconds(_options.DefaultHookTimeoutSecs)), ct).ConfigureAwait(false);
                preparation = preparation with
                {
                    Phase = result.TimedOut ? WorkspacePreparationPhase.Unknown
                        : result.ExitCode == 0 ? WorkspacePreparationPhase.Ready
                        : WorkspacePreparationPhase.Failed
                };
            }
            else
                preparation = preparation with { Phase = WorkspacePreparationPhase.Ready };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            preparation = preparation with
            {
                Phase = preparation.Phase == WorkspacePreparationPhase.Mounting
                    ? WorkspacePreparationPhase.Failed : WorkspacePreparationPhase.Unknown
            };
        }

        await EnqueueAsync(async actor =>
        {
            var readyTrack = preparation.Phase == WorkspacePreparationPhase.Ready
                ? _track with { Cwd = mounted!.MergedCwd, WorkspaceSpec = JsonHelpers.ToElement(preparation.Spec) }
                : null;
            var evt = readyTrack is null ? null : new EventEnvelopeDto(
                null, _track.AreaId, TrackId, KernelEventKinds.TrackUpdated, JsonHelpers.ToElement(readyTrack), NowMs());
            try
            {
                if (evt is not null)
                    await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
                await _store.SaveWorkspacePreparationAsync(TrackId, preparation, readyTrack, evt, ct).ConfigureAwait(false);
            }
            catch
            {
                // An external effect may have completed even if its final write failed.
                _workspacePreparation = preparation with { Phase = WorkspacePreparationPhase.Unknown };
                throw;
            }
            finally
            {
                _workspacePreparationJob = null;
            }

            _workspacePreparation = preparation;
            if (readyTrack is null)
                return;

            _track = readyTrack;
            _mountedWorkspace = mounted;
            try
            {
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt!, ct, allowFallback: false).ConfigureAwait(false);
            }
            finally
            {
                await ResumeWorkspaceWorkCoreAsync(ct).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task ResumeWorkspaceWorkCoreAsync(CancellationToken ct)
    {
        if (_track.ClosedAtMs is not null || _deletingTrack || !WorkspaceReady)
            return;

        var inputs = await _store.ListAcceptedTrackInputsAsync(TrackId, ct).ConfigureAwait(false);
        foreach (var input in inputs)
            DispatchAcceptedInput(input);
        foreach (var sessionId in _sessions.Keys)
            SchedulePendingWakeCore(sessionId);
        if (_tasks.Count > 0)
            _ = TrackBackgroundJob(() => TriggerSchedulerAsync(_actorShutdownCts.Token));
    }

    private Task RefreshForeignWorkspacePreparationCoreAsync(CancellationToken ct) =>
        _workspacePreparationJob is not { IsCompleted: false } &&
        _workspacePreparation is { Phase: WorkspacePreparationPhase.Provisioning or WorkspacePreparationPhase.Mounting or WorkspacePreparationPhase.Initializing }
            ? RefreshWorkspacePreparationCoreAsync(ct)
            : Task.CompletedTask;

    private async Task RefreshWorkspacePreparationCoreAsync(CancellationToken ct)
    {
        _workspacePreparation = await _store.GetWorkspacePreparationAsync(TrackId, ct).ConfigureAwait(false);
        if (_workspacePreparation is { Phase: WorkspacePreparationPhase.Ready, Spec: { } spec })
        {
            var storedTrack = await _store.GetTrackAsync(TrackId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound($"Track '{TrackId}' not found.");
            _track = _track with { Cwd = storedTrack.Cwd, WorkspaceSpec = JsonHelpers.ToElement(spec) };
        }
    }
}
