using System.Text.Json;

namespace Perga.VcsKit;

public sealed class DirectWorkspaceDriver : IWorkspaceDriver
{
    public const string Id = "direct";

    public string DriverId => Id;

    public bool SupportsParallelIsolatedWriteLeases => false;

    public Task<WorkspaceSpec> ProvisionTrackAsync(WorkspaceProvisionRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.TrackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.SourcePath);
        ct.ThrowIfCancellationRequested();

        var sourcePath = Path.GetFullPath(req.SourcePath);
        var lowerDirs = req.LowerCacheDirs?.ToArray() ?? [];
        var metadata = JsonSerializer.SerializeToElement(new
        {
            v = 1,
            driver = DriverId,
            trackId = req.TrackId,
            baseRevision = req.BaseRevision,
        });

        var spec = new WorkspaceSpec(
            DriverId: DriverId,
            LogicalRoot: sourcePath,
            LowerDirs: lowerDirs,
            UpperDir: null,
            WorkDir: null,
            MergedCwd: sourcePath,
            Metadata: metadata);

        return Task.FromResult(spec);
    }

    public Task<MountedWorkspace> EnsureMountedAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.MergedCwd);
        ct.ThrowIfCancellationRequested();

        var mounted = new MountedWorkspace(
            DriverId: DriverId,
            MergedCwd: Path.GetFullPath(spec.MergedCwd),
            ProcessOverlayLayers: null);

        return Task.FromResult(mounted);
    }

    public Task<AttemptWorkspaceLease> AcquireAttemptLeaseAsync(
        MountedWorkspace workspace,
        string attemptId,
        bool readWrite,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.MergedCwd);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ct.ThrowIfCancellationRequested();

        var metadata = JsonSerializer.SerializeToElement(new
        {
            v = 1,
            readWrite,
        });

        var lease = new AttemptWorkspaceLease(
            DriverId: DriverId,
            AttemptId: attemptId,
            EffectiveCwd: workspace.MergedCwd,
            IsIsolatedCopyOnWrite: false,
            Metadata: metadata,
            ProcessOverlayLayers: workspace.ProcessOverlayLayers);

        return Task.FromResult(lease);
    }

    public Task SettleAttemptLeaseAsync(
        AttemptWorkspaceLease lease,
        AttemptLeaseDisposition disposition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        _ = disposition;
        ct.ThrowIfCancellationRequested();

        // Direct passthrough shares the source directory directly; never delete EffectiveCwd.
        return Task.CompletedTask;
    }

    public Task TeardownTrackAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ct.ThrowIfCancellationRequested();

        // Direct passthrough never deletes LogicalRoot, MergedCwd, or shared LowerDirs.
        return Task.CompletedTask;
    }
}
