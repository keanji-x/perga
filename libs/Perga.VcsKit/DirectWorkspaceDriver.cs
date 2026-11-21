using System.Text.Json;

namespace Perga.VcsKit;

public sealed class DirectWorkspaceDriver : IWorkspaceDriver
{
    public const string Id = "direct";

    public string DriverId => Id;

    public Task<WorkspaceSpec> ProvisionTrackAsync(WorkspaceProvisionRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.TrackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.SourcePath);
        ct.ThrowIfCancellationRequested();

        var sourcePath = Path.GetFullPath(req.SourcePath);
        if (req.NewEmptyWorkspace)
            Directory.CreateDirectory(sourcePath);
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
            Metadata: metadata,
            ManagedEmptyWorkspace: req.NewEmptyWorkspace);

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
            ProcessOverlayLayers: null,
            ManagedEmptyWorkspace: spec.ManagedEmptyWorkspace);

        return Task.FromResult(mounted);
    }

    public Task TeardownTrackAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ct.ThrowIfCancellationRequested();

        if (spec.ManagedEmptyWorkspace && Directory.Exists(spec.LogicalRoot))
            Directory.Delete(spec.LogicalRoot, recursive: true);
        return Task.CompletedTask;
    }
}
