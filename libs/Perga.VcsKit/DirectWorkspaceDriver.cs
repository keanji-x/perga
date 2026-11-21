using System.Text.Json;

namespace Perga.VcsKit;

public sealed class DirectWorkspaceDriver : IWorkspaceDriver
{
    public const string Id = "direct";

    private readonly IWorkspaceFileSystemOps _fs;

    public DirectWorkspaceDriver(IWorkspaceFileSystemOps? fsOps = null)
    {
        _fs = fsOps ?? LocalWorkspaceFileSystemOps.Instance;
    }

    public string DriverId => Id;

    public Task<WorkspaceSpec> ProvisionTrackAsync(WorkspaceProvisionRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.TrackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.SourcePath);
        ct.ThrowIfCancellationRequested();

        var sourcePath = Path.GetFullPath(req.SourcePath);
        if (req.NewEmptyWorkspace)
            _fs.CreateDirectory(sourcePath);
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

        if (spec.ManagedEmptyWorkspace && _fs.DirectoryExists(spec.LogicalRoot))
            _fs.DeleteDirectoryIfExists(spec.LogicalRoot);
        return Task.CompletedTask;
    }
}
