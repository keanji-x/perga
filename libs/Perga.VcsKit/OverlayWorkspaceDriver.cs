using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perga.VcsKit;

public interface IOverlayMounts
{
    Task<bool> ProbeAsync(CancellationToken ct);
    Task MountAsync(string trackId, string snapshotId, CancellationToken ct);
    Task UnmountAsync(string trackId, string snapshotId, CancellationToken ct);
}

public sealed record OverlayWorkspaceOptions(
    string WorkspacesRoot,
    string? WarmCommand = null,
    string? CacheVersion = null);

public sealed partial class OverlayWorkspaceDriver(
    VcsService vcs,
    IOverlayMounts mounts,
    OverlayWorkspaceOptions options) : IWorkspaceDriver
{
    public const string Id = "kernel-overlayfs";

    private readonly string _root = Path.Combine(
        WorkspaceFileReader.ResolveCanonicalPath(options.WorkspacesRoot), ".overlayfs");
    private readonly OverlayWorkspaceSnapshots _snapshots = new(vcs, options);
    private readonly SemaphoreSlim _mountLock = new(1, 1);

    public string DriverId => Id;
    public bool SupportsParallelIsolatedWriteLeases => false;
    public bool ProvisionIsRepeatable => true;
    public bool VerifyMountOnAccess => true;

    public Task<bool> IsAvailableAsync(string? source, CancellationToken ct) =>
        !OperatingSystem.IsLinux() || VcsService.IsInsideRepository(_root) ||
            (source is not null && (!CanUseSource(source) || VcsService.IsJjOnlySource(source)))
            ? Task.FromResult(false)
            : mounts.ProbeAsync(ct);

    public async Task<WorkspaceSpec> ProvisionTrackAsync(
        WorkspaceProvisionRequest request, CancellationToken ct = default)
    {
        ValidateTrackId(request.TrackId);
        var track = ManagedPath("tracks", request.TrackId);
        var existing = Path.Combine(track, "workspace.json");
        RequireCanonical(existing);
        if (Directory.Exists(track))
            return ReadPublishedSpec(existing, request.TrackId);

        var source = WorkspaceFileReader.ResolveCanonicalPath(request.SourcePath);
        if (!await IsAvailableAsync(request.NewEmptyWorkspace ? null : source, ct).ConfigureAwait(false))
            throw new WorkspaceUnavailableException("OverlayFS is unavailable for this source.");

        var snapshots = ManagedPath("snapshots");
        Directory.CreateDirectory(snapshots);
        var snapshot = await _snapshots.PrepareAsync(snapshots, source, request, ct).ConfigureAwait(false);
        var spec = new WorkspaceSpec(
            Id, source, [ManagedPath("snapshots", snapshot.Id)],
            Path.Combine(track, "upper"), Path.Combine(track, "work"), Path.Combine(track, "merged"),
            JsonSerializer.SerializeToElement(new SpecData(request.TrackId, snapshot.Id)),
            request.NewEmptyWorkspace);

        var stagingId = $"stage_{Guid.NewGuid():N}";
        var staging = ManagedPath("tracks", stagingId);
        Directory.CreateDirectory(Path.Combine(staging, "upper"));
        Directory.CreateDirectory(Path.Combine(staging, "work"));
        var stagedMerged = Path.Combine(staging, "merged");
        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(stagedMerged, UnixFileMode.None);
        else
            throw new WorkspaceUnavailableException("OverlayFS requires Linux.");

        var safeToRemove = true;
        try
        {
            if (snapshot.Commit is not null)
            {
                await _mountLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    safeToRemove = false;
                    await mounts.MountAsync(stagingId, snapshot.Id, ct).ConfigureAwait(false);
                    await vcs.InitializeOverlayBranchAsync(stagedMerged, $"track-{request.TrackId}", ct).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await mounts.UnmountAsync(stagingId, snapshot.Id, CancellationToken.None).ConfigureAwait(false);
                        safeToRemove = true;
                    }
                    finally
                    {
                        _mountLock.Release();
                    }
                }
            }

            File.WriteAllText(Path.Combine(staging, "workspace.json"), JsonSerializer.Serialize(spec));
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.Move(staging, track);
                return spec;
            }
            catch (IOException) when (File.Exists(existing))
            {
                return ReadPublishedSpec(existing, request.TrackId);
            }
        }
        finally
        {
            if (safeToRemove)
                OverlayWorkspaceSnapshots.DeletePrivateTree(staging);
        }
    }

    private WorkspaceSpec ReadPublishedSpec(string path, string trackId)
    {
        RequireCanonical(path);
        var spec = JsonSerializer.Deserialize<WorkspaceSpec>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The published OverlayFS workspace specification is missing.");
        var (data, _) = ValidateSpec(spec);
        if (data.TrackId != trackId)
            throw new InvalidDataException("The published OverlayFS workspace belongs to a different Track.");
        return spec;
    }

    public async Task<MountedWorkspace> EnsureMountedAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        var (data, _) = ValidateSpec(spec);
        if (VcsService.IsInsideRepository(_root))
            throw new WorkspaceUnavailableException("The OverlayFS workspace root must be outside Git and Jujutsu repositories.");
        await _mountLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await mounts.MountAsync(data.TrackId, data.SnapshotId, ct).ConfigureAwait(false);
            return new MountedWorkspace(Id, spec.MergedCwd, ManagedEmptyWorkspace: spec.ManagedEmptyWorkspace);
        }
        finally
        {
            _mountLock.Release();
        }
    }

    public Task<AttemptWorkspaceLease> AcquireAttemptLeaseAsync(
        MountedWorkspace workspace, string attemptId, bool readWrite, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AttemptWorkspaceLease(
            Id, attemptId, workspace.MergedCwd, false, JsonSerializer.SerializeToElement(new { readWrite })));
    }

    public Task SettleAttemptLeaseAsync(
        AttemptWorkspaceLease lease, AttemptLeaseDisposition disposition, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task TeardownTrackAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        var (data, _) = ValidateSpec(spec);
        await _mountLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await mounts.UnmountAsync(data.TrackId, data.SnapshotId, ct).ConfigureAwait(false);
        }
        finally
        {
            _mountLock.Release();
        }
    }

    public async Task CleanupDeletedTrackAsync(WorkspaceSpec spec)
    {
        var (data, track) = ValidateSpec(spec);
        await _mountLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await mounts.UnmountAsync(data.TrackId, data.SnapshotId, CancellationToken.None).ConfigureAwait(false);
            OverlayWorkspaceSnapshots.DeletePrivateTree(track);
        }
        finally
        {
            _mountLock.Release();
        }
    }

    private (SpecData Data, string Track) ValidateSpec(WorkspaceSpec spec)
    {
        if (spec.DriverId != Id || spec.Metadata.ValueKind != JsonValueKind.Object ||
            spec.Metadata.Deserialize<SpecData>() is not { } data)
            throw new InvalidDataException("Invalid OverlayFS workspace specification.");
        ValidateTrackId(data.TrackId);
        if (!SnapshotIdPattern().IsMatch(data.SnapshotId))
            throw new InvalidDataException("Invalid OverlayFS snapshot ID.");

        var track = ManagedPath("tracks", data.TrackId);
        if (spec.UpperDir != Path.Combine(track, "upper") ||
            spec.WorkDir != Path.Combine(track, "work") ||
            spec.MergedCwd != Path.Combine(track, "merged") ||
            spec.LowerDirs.Count != 1 || spec.LowerDirs[0] != ManagedPath("snapshots", data.SnapshotId))
            throw new InvalidDataException("The OverlayFS paths do not match the saved workspace.");
        RequireCanonical(spec.UpperDir);
        RequireCanonical(spec.WorkDir);
        RequireCanonical(spec.MergedCwd);
        return (data, track);
    }

    private bool CanUseSource(string source)
    {
        var canonical = WorkspaceFileReader.ResolveCanonicalPath(source);
        return !IsWithin(_root, canonical) && !IsWithin(canonical, _root);
    }

    private static bool IsWithin(string path, string directory) =>
        path == directory || path.StartsWith(Path.EndsInDirectorySeparator(directory)
            ? directory : directory + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private string ManagedPath(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        RequireCanonical(path);
        return path;
    }

    private static void RequireCanonical(string path)
    {
        if (WorkspaceFileReader.ResolveCanonicalPath(path) != path || new DirectoryInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Managed OverlayFS paths must not contain symbolic links.");
    }

    private static void ValidateTrackId(string id)
    {
        if (!TrackIdPattern().IsMatch(id))
            throw new InvalidDataException("Invalid OverlayFS Track ID.");
    }

    private sealed record SpecData(string TrackId, string SnapshotId);

    [GeneratedRegex(@"\Atrk_[0-9a-f]{32}\z")]
    private static partial Regex TrackIdPattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex SnapshotIdPattern();
}
