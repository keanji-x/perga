using System.Text.Json;

namespace Perga.VcsKit;

// -------- 1. Track-scoped workspace storage & mount abstractions --------

public sealed record WorkspaceOverlayLayer(
    IReadOnlyList<string> LowerDirs,
    string? UpperDir,
    string? WorkDir,
    string TargetPath);

public sealed record WorkspaceProvisionRequest(
    string TrackId,
    string SourcePath,
    string TargetRootDir,
    IReadOnlyList<string> LowerCacheDirs,
    string? BaseRevision = null,
    bool NewEmptyWorkspace = false,
    VcsBackendKind? VcsBackend = null);

public sealed record WorkspaceSpec(
    string DriverId,
    string LogicalRoot,
    IReadOnlyList<string> LowerDirs,
    string? UpperDir,
    string? WorkDir,
    string MergedCwd,
    JsonElement Metadata,
    bool ManagedEmptyWorkspace = false);

public sealed record MountedWorkspace(
    string DriverId,
    string MergedCwd,
    IReadOnlyList<WorkspaceOverlayLayer>? ProcessOverlayLayers = null,
    bool ManagedEmptyWorkspace = false);

public interface IWorkspaceDriver
{
    string DriverId { get; }
    bool ProvisionIsRepeatable => false;
    bool VerifyMountOnAccess => false;
    Task<bool> IsAvailableAsync(string? source, CancellationToken ct) => Task.FromResult(true);

    Task<WorkspaceSpec> ProvisionTrackAsync(WorkspaceProvisionRequest req, CancellationToken ct = default);
    Task<MountedWorkspace> EnsureMountedAsync(WorkspaceSpec spec, CancellationToken ct = default);
    Task TeardownTrackAsync(WorkspaceSpec spec, CancellationToken ct = default);
    Task CleanupDeletedTrackAsync(WorkspaceSpec spec) => Task.CompletedTask;
}

public interface IWorkspaceDriverRegistry
{
    IWorkspaceDriver GetRequired(string driverId);
    IReadOnlyList<string> RegisteredDrivers { get; }
}

// -------- 2. 版本控制与 Forge 抽象（JJ 一等支持 + Git 兜底 + gh CLI） --------

public enum VcsBackendKind
{
    JjColocated = 0,
    GitWorktree = 1,
    PlainDirectory = 2
}

public sealed class WorkspaceUnavailableException(string message) : InvalidOperationException(message);

public sealed class WorkspaceBackendUnavailableException(VcsBackendKind backend)
    : InvalidOperationException($"The selected workspace backend '{backend}' is no longer available.");

public sealed record IsolatedWorkspace(
    VcsBackendKind Kind,
    string RootRepoPath,
    string WorkspaceName,
    string WorkspacePath,
    string? BaseRevision);

public sealed record RevisionSnapshot(
    VcsBackendKind Kind,
    string ChangeIdOrBranch,
    string CommitId,
    string? OpId,
    bool HasConflicts,
    IReadOnlyList<string> ChangedFiles);

public sealed record VcsDiffFileDto(
    string Path,
    string Status,
    string Patch,
    string? HeadText,
    string? WorkingText,
    bool IsBinary);

public sealed record DiffSummary(
    string DiffText,
    bool Truncated,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<VcsDiffFileDto>? Files = null)
{
    public IReadOnlyList<VcsDiffFileDto> Files { get; init; } = Files ?? [];
}

public sealed record PushResult(
    string BookmarkOrBranch,
    string RemoteName,
    string HeadCommitId);

public interface IVcsService
{
    Task<VcsBackendKind> DetectAsync(string path, CancellationToken ct);
    IReadOnlyList<VcsBackendKind> ListWorkspaceBackends(string path);
    Task<IReadOnlyList<VcsBackendKind>> ListWorkspaceBackendsAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ListWorkspaceBackends(path));
    }
    Task<IsolatedWorkspace> CreateWorkspaceAsync(string repoPath, string workspaceName, string targetDir, string? baseRev = null,
        CancellationToken ct = default, VcsBackendKind? backend = null);
    Task RemoveWorkspaceAsync(IsolatedWorkspace workspace, CancellationToken ct = default);
    Task<RevisionSnapshot> SnapshotWorkingCopyAsync(string workspacePath, string? commitMessageIfGit = null, CancellationToken ct = default);
    Task RestoreOperationAsync(string workspacePath, string opId, CancellationToken ct = default);
    Task<RevisionSnapshot> RebaseOntoUpstreamAsync(string workspacePath, string upstreamBranch = "main", CancellationToken ct = default);
    Task<DiffSummary> DiffAsync(string workspacePath, string? fromRev = null, string? toRev = null, CancellationToken ct = default);
    Task<DiffSummary> DiffDetailedAsync(string workspacePath, string? fromRev = null, string? toRev = null, string? filterPath = null, CancellationToken ct = default, bool searchAncestors = true) =>
        DiffAsync(workspacePath, fromRev, toRev, ct);
    Task<PushResult> PushBookmarkAsync(string workspacePath, string bookmarkOrBranch, string? targetRev = null, CancellationToken ct = default);
}

public sealed record PrPublishRequest(
    string WorkspacePath,
    string HeadBranch,
    string BaseBranch,
    string Title,
    string Body);

public sealed record PrInfo(int Number, string Url, string State, string HeadSha);
public sealed record CheckRunSummary(string Name, string Status, string? Conclusion, string? DetailsUrl);
public sealed record PrChecksStatus(int PrNumber, string HeadSha, bool AllCompleted, bool AllGreen, IReadOnlyList<CheckRunSummary> Checks);
public sealed record CiFailureEvidence(int PrNumber, string HeadSha, string SummaryText, string LogTail);
public sealed record IssueInfo(int Number, string Url, string State, string Title, string Body, IReadOnlyList<string> Labels);

public interface IGitHubForgeService
{
    Task<PrInfo> PublishPrAsync(PrPublishRequest req, CancellationToken ct);
    Task<PrChecksStatus> GetPrChecksAsync(string repoOrPath, int prNumber, CancellationToken ct);
    Task<CiFailureEvidence> GetCiFailureEvidenceAsync(string repoOrPath, int prNumber, string headSha, CancellationToken ct);
    Task MergePrAsync(string repoOrPath, int prNumber, string expectedHeadSha, CancellationToken ct);
    Task<IssueInfo> ViewIssueAsync(string repoOrPath, int issueNumber, CancellationToken ct);
}
