using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perga.VcsKit;

public sealed class VcsWorkspaceDriver : IWorkspaceDriver
{
    public const string Id = "vcs-workspace";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IVcsService _vcsService;

    public VcsWorkspaceDriver(IVcsService vcsService)
    {
        ArgumentNullException.ThrowIfNull(vcsService);
        _vcsService = vcsService;
    }

    public string DriverId => Id;

    public bool SupportsParallelIsolatedWriteLeases => false;

    public async Task<WorkspaceSpec> ProvisionTrackAsync(WorkspaceProvisionRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.TrackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(req.TargetRootDir);

        var sourcePath = Path.GetFullPath(req.SourcePath);
        var workspaceName = $"track-{req.TrackId}";
        var targetDir = Path.GetFullPath(Path.Combine(req.TargetRootDir, req.TrackId));

        if (PathsEqual(sourcePath, targetDir))
        {
            throw new InvalidOperationException(
                $"Target workspace directory '{targetDir}' must differ from source repository '{sourcePath}'.");
        }

        var lowerDirs = req.LowerCacheDirs?.ToArray() ?? [];
        EnsureNotProtectedLowerDir(sourcePath, targetDir, lowerDirs);

        var isolated = await _vcsService
            .CreateWorkspaceAsync(sourcePath, workspaceName, targetDir, req.BaseRevision, ct)
            .ConfigureAwait(false);

        var metadata = JsonSerializer.SerializeToElement(isolated, MetadataJsonOptions);

        return new WorkspaceSpec(
            DriverId: DriverId,
            LogicalRoot: isolated.RootRepoPath,
            LowerDirs: lowerDirs,
            UpperDir: null,
            WorkDir: null,
            MergedCwd: isolated.WorkspacePath,
            Metadata: metadata);
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

        // Phase-1 vcs-workspace uses track-level VCS isolation without per-attempt CoW upperdirs.
        return Task.CompletedTask;
    }

    public async Task TeardownTrackAsync(WorkspaceSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ct.ThrowIfCancellationRequested();

        var isolated = ExtractIsolatedWorkspace(spec);

        if (PathsEqual(isolated.RootRepoPath, isolated.WorkspacePath))
        {
            throw new InvalidOperationException(
                $"Refusing to tear down workspace '{isolated.WorkspacePath}' because it matches RootRepoPath.");
        }

        EnsureNotProtectedLowerDir(isolated.RootRepoPath, isolated.WorkspacePath, spec.LowerDirs);

        await _vcsService.RemoveWorkspaceAsync(isolated, ct).ConfigureAwait(false);
    }

    private static IsolatedWorkspace ExtractIsolatedWorkspace(WorkspaceSpec spec)
    {
        if (spec.Metadata.ValueKind == JsonValueKind.Object)
        {
            if (spec.Metadata.TryGetProperty("isolatedWorkspace", out var nested) &&
                nested.ValueKind == JsonValueKind.Object)
            {
                var parsedNested = JsonSerializer.Deserialize<IsolatedWorkspace>(nested, MetadataJsonOptions);
                if (parsedNested is not null && !string.IsNullOrWhiteSpace(parsedNested.WorkspacePath))
                {
                    return parsedNested;
                }
            }

            var parsed = JsonSerializer.Deserialize<IsolatedWorkspace>(spec.Metadata, MetadataJsonOptions);
            if (parsed is not null &&
                !string.IsNullOrWhiteSpace(parsed.WorkspacePath) &&
                !string.IsNullOrWhiteSpace(parsed.RootRepoPath) &&
                !string.IsNullOrWhiteSpace(parsed.WorkspaceName))
            {
                return parsed;
            }
        }

        throw new InvalidOperationException(
            "WorkspaceSpec.Metadata does not contain valid IsolatedWorkspace metadata.");
    }

    private static void EnsureNotProtectedLowerDir(
        string logicalRoot,
        string candidateTargetDir,
        IReadOnlyList<string>? lowerDirs)
    {
        if (lowerDirs is null || lowerDirs.Count == 0)
        {
            return;
        }

        var normalizedCandidate = NormalizeDirectoryPath(candidateTargetDir);
        foreach (var lower in lowerDirs)
        {
            if (string.IsNullOrWhiteSpace(lower))
            {
                continue;
            }

            var resolvedLower = Path.IsPathRooted(lower)
                ? NormalizeDirectoryPath(lower)
                : NormalizeDirectoryPath(Path.Combine(logicalRoot, lower));

            if (string.Equals(normalizedCandidate, resolvedLower, PathComparison))
            {
                throw new InvalidOperationException(
                    $"Workspace directory '{candidateTargetDir}' collides with protected LowerDir '{resolvedLower}'.");
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizeDirectoryPath(left), NormalizeDirectoryPath(right), PathComparison);

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
