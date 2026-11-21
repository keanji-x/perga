using System.Text;

namespace Perga.VcsKit;

public sealed record VcsServiceOptions
{
    public bool AutoColocateJj { get; init; }
    public bool DisableJj { get; init; }
    public int MaxDiffBytes { get; init; } = 256 * 1024;
    public string DefaultRemoteName { get; init; } = "origin";
    public string? JjExecutablePath { get; init; }
    public string GitExecutablePath { get; init; } = "git";
    public string FallbackAuthorName { get; init; } = "Perga Automation";
    public string FallbackAuthorEmail { get; init; } = "perga@localhost";
}

public sealed partial class VcsService : IVcsService
{
    private readonly IProcessRunner _processRunner;
    private readonly VcsServiceOptions _options;
    private readonly IWorkspaceFileSystemOps _fs;
    private readonly bool _usesDefaultRunner;

    public VcsService()
        : this(new ProcessRunner(), new VcsServiceOptions(), LocalWorkspaceFileSystemOps.Instance)
    {
    }

    public VcsService(VcsServiceOptions options)
        : this(new ProcessRunner(), options, LocalWorkspaceFileSystemOps.Instance)
    {
    }

    public VcsService(IProcessRunner processRunner, VcsServiceOptions? options = null, IWorkspaceFileSystemOps? fsOps = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
        _options = options ?? new VcsServiceOptions();
        _fs = fsOps ?? LocalWorkspaceFileSystemOps.Instance;
        _usesDefaultRunner = processRunner is ProcessRunner;

        if (_options.MaxDiffBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDiffBytes must be positive.");
        }
    }

    public Task<VcsBackendKind> DetectAsync(string path, CancellationToken ct) =>
        DetectAsync(path, ct, searchAncestors: true);

    private async Task<VcsBackendKind> DetectAsync(string path, CancellationToken ct, bool searchAncestors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        var startDir = _fs.DirectoryExists(fullPath)
            ? fullPath
            : _fs.FileExists(fullPath)
                ? Path.GetDirectoryName(fullPath)
                : null;

        if (string.IsNullOrEmpty(startDir) || !_fs.DirectoryExists(startDir))
        {
            return VcsBackendKind.PlainDirectory;
        }

        var vcsRoot = FindNearestVcsRoot(startDir, searchAncestors);
        if (vcsRoot is null)
        {
            return VcsBackendKind.PlainDirectory;
        }

        var jjExe = ResolveJjExecutable();

        if (vcsRoot.Value.HasJj && jjExe is not null)
        {
            return VcsBackendKind.JjColocated;
        }

        if (vcsRoot.Value.HasGit)
        {
            if (_options.AutoColocateJj && jjExe is not null)
            {
                await RunJjCheckedAsync(jjExe, vcsRoot.Value.RootPath, ["git", "init", "--colocate"], ct)
                    .ConfigureAwait(false);
                return VcsBackendKind.JjColocated;
            }

            return VcsBackendKind.GitWorktree;
        }

        return VcsBackendKind.PlainDirectory;
    }

    public IReadOnlyList<VcsBackendKind> ListWorkspaceBackends(string path)
    {
        if (!_fs.DirectoryExists(path))
            return [];

        var root = FindNearestVcsRoot(Path.GetFullPath(path));
        if (root is null)
            return [VcsBackendKind.PlainDirectory];

        var candidates = new List<VcsBackendKind>(2);
        if (root.Value.HasJj && ResolveJjExecutable() is not null)
            candidates.Add(VcsBackendKind.JjColocated);
        var git = _options.GitExecutablePath;
        if (OperatingSystem.IsWindows() && _fs.IsLocal && !Path.HasExtension(git))
            git += ".exe";
        var gitAvailable = !_usesDefaultRunner && _fs.IsLocal
            ? true
            : _fs.ResolveExecutable(git) is not null;
        if (root.Value.HasGit && gitAvailable)
            candidates.Add(VcsBackendKind.GitWorktree);
        return candidates;
    }

    public Task<IReadOnlyList<VcsBackendKind>> ListWorkspaceBackendsAsync(string path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() => ListWorkspaceBackends(path), ct);
    }

    public async Task<IsolatedWorkspace> CreateWorkspaceAsync(
        string repoPath,
        string workspaceName,
        string targetDir,
        string? baseRev = null,
        CancellationToken ct = default,
        VcsBackendKind? backend = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);

        var fullRepoPath = Path.GetFullPath(repoPath);
        var fullTargetDir = Path.GetFullPath(targetDir);
        ct.ThrowIfCancellationRequested();
        if (backend is { } requested && !ListWorkspaceBackends(fullRepoPath).Contains(requested))
            throw new WorkspaceBackendUnavailableException(requested);

        if (PathsEqual(fullRepoPath, fullTargetDir))
        {
            throw new InvalidOperationException(
                $"Target workspace path '{fullTargetDir}' must differ from source path '{fullRepoPath}'.");
        }

        var parentDir = Path.GetDirectoryName(fullTargetDir);
        if (!string.IsNullOrEmpty(parentDir))
        {
            _fs.CreateDirectory(parentDir);
        }

        var kind = backend ?? await DetectAsync(fullRepoPath, ct).ConfigureAwait(false);
        var rootRepoPath = FindNearestVcsRoot(fullRepoPath)?.RootPath ?? fullRepoPath;

        if (PathsEqual(rootRepoPath, fullTargetDir))
        {
            throw new InvalidOperationException(
                $"Target workspace path '{fullTargetDir}' must differ from repository root '{rootRepoPath}'.");
        }

        switch (kind)
        {
            case VcsBackendKind.JjColocated:
            {
                var jjExe = RequireJjExecutable();
                var args = new List<string> { "workspace", "add", "--name", workspaceName };
                if (!string.IsNullOrWhiteSpace(baseRev))
                {
                    args.Add("-r");
                    args.Add(baseRev);
                }

                args.Add(fullTargetDir);
                await RunJjCheckedAsync(jjExe, rootRepoPath, args, ct).ConfigureAwait(false);

                return new IsolatedWorkspace(
                    VcsBackendKind.JjColocated,
                    rootRepoPath,
                    workspaceName,
                    fullTargetDir,
                    baseRev);
            }

            case VcsBackendKind.GitWorktree:
            {
                var branchExists = await GitRefExistsAsync(rootRepoPath, $"refs/heads/{workspaceName}", ct)
                    .ConfigureAwait(false);

                var args = new List<string> { "worktree", "add" };
                if (!branchExists)
                {
                    args.Add("-b");
                    args.Add(workspaceName);
                }
                else
                {
                    args.Add("--detach");
                }

                args.Add(fullTargetDir);
                if (!string.IsNullOrWhiteSpace(baseRev))
                {
                    args.Add(baseRev);
                }

                await RunGitCheckedAsync(rootRepoPath, args, ct).ConfigureAwait(false);

                return new IsolatedWorkspace(
                    VcsBackendKind.GitWorktree,
                    rootRepoPath,
                    workspaceName,
                    fullTargetDir,
                    baseRev);
            }

            case VcsBackendKind.PlainDirectory:
            default:
            {
                CopyDirectorySafe(fullRepoPath, fullTargetDir, ct);

                return new IsolatedWorkspace(
                    VcsBackendKind.PlainDirectory,
                    fullRepoPath,
                    workspaceName,
                    fullTargetDir,
                    baseRev);
            }
        }
    }

    public async Task RemoveWorkspaceAsync(IsolatedWorkspace workspace, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.RootRepoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace.WorkspacePath);
        ct.ThrowIfCancellationRequested();

        var rootRepoPath = Path.GetFullPath(workspace.RootRepoPath);
        var workspacePath = Path.GetFullPath(workspace.WorkspacePath);

        if (PathsEqual(rootRepoPath, workspacePath))
        {
            throw new InvalidOperationException(
                $"Refusing to remove workspace '{workspacePath}' because it matches RootRepoPath.");
        }

        switch (workspace.Kind)
        {
            case VcsBackendKind.JjColocated:
            {
                var jjExe = ResolveJjExecutable();
                if (jjExe is not null && _fs.DirectoryExists(rootRepoPath) && !string.IsNullOrWhiteSpace(workspace.WorkspaceName))
                {
                    // Ignore non-zero if workspace was already forgotten.
                    _ = await RunJjAsync(jjExe, rootRepoPath, ["workspace", "forget", workspace.WorkspaceName], ct)
                        .ConfigureAwait(false);
                }

                DeleteDirectoryIfExists(workspacePath);
                break;
            }

            case VcsBackendKind.GitWorktree:
            {
                if (_fs.DirectoryExists(rootRepoPath))
                {
                    _ = await RunGitAsync(rootRepoPath, ["worktree", "remove", "--force", workspacePath], ct)
                        .ConfigureAwait(false);
                    _ = await RunGitAsync(rootRepoPath, ["worktree", "prune"], ct).ConfigureAwait(false);
                }

                DeleteDirectoryIfExists(workspacePath);
                break;
            }

            case VcsBackendKind.PlainDirectory:
            default:
            {
                DeleteDirectoryIfExists(workspacePath);
                break;
            }
        }
    }

    public async Task<RevisionSnapshot> SnapshotWorkingCopyAsync(
        string workspacePath,
        string? commitMessageIfGit = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);

        var kind = await DetectAsync(fullWorkspacePath, ct).ConfigureAwait(false);
        switch (kind)
        {
            case VcsBackendKind.JjColocated:
                return await SnapshotJjAsync(fullWorkspacePath, ct).ConfigureAwait(false);

            case VcsBackendKind.GitWorktree:
                return await SnapshotGitAsync(fullWorkspacePath, commitMessageIfGit, ct).ConfigureAwait(false);

            case VcsBackendKind.PlainDirectory:
            default:
                return SnapshotPlainDirectory(fullWorkspacePath);
        }
    }

    public async Task RestoreOperationAsync(string workspacePath, string opId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(opId);

        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var kind = await DetectAsync(fullWorkspacePath, ct).ConfigureAwait(false);
        if (kind != VcsBackendKind.JjColocated)
        {
            throw new NotSupportedException(
                $"RestoreOperationAsync is only supported on JjColocated workspaces (detected '{kind}').");
        }

        var jjExe = RequireJjExecutable();
        await RunJjCheckedAsync(jjExe, fullWorkspacePath, ["op", "restore", opId], ct).ConfigureAwait(false);
    }

    public async Task<RevisionSnapshot> RebaseOntoUpstreamAsync(
        string workspacePath,
        string upstreamBranch = "main",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamBranch);

        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var kind = await DetectAsync(fullWorkspacePath, ct).ConfigureAwait(false);

        switch (kind)
        {
            case VcsBackendKind.JjColocated:
            {
                var jjExe = RequireJjExecutable();
                var remoteName = _options.DefaultRemoteName;
                var hasRemote = await JjHasRemoteAsync(jjExe, fullWorkspacePath, remoteName, ct).ConfigureAwait(false);

                if (hasRemote)
                {
                    await RunJjCheckedAsync(jjExe, fullWorkspacePath, ["git", "fetch", "--remote", remoteName], ct)
                        .ConfigureAwait(false);
                }

                var destinationRev = await ResolveJjUpstreamRevsetAsync(
                        jjExe,
                        fullWorkspacePath,
                        upstreamBranch,
                        hasRemote ? remoteName : null,
                        ct)
                    .ConfigureAwait(false);

                // In JJ, conflicts are first-class tree states and exit 0 without wedging the workspace.
                await RunJjCheckedAsync(jjExe, fullWorkspacePath, ["rebase", "-s", "@", "-d", destinationRev], ct)
                    .ConfigureAwait(false);

                return await SnapshotJjAsync(fullWorkspacePath, ct).ConfigureAwait(false);
            }

            case VcsBackendKind.GitWorktree:
            {
                var remoteName = _options.DefaultRemoteName;
                var hasRemote = await GitHasRemoteAsync(fullWorkspacePath, remoteName, ct).ConfigureAwait(false);
                if (hasRemote)
                {
                    await RunGitCheckedAsync(fullWorkspacePath, ["fetch", remoteName], ct).ConfigureAwait(false);
                }

                var targetRef = upstreamBranch;
                if (hasRemote && !upstreamBranch.StartsWith($"{remoteName}/", StringComparison.Ordinal))
                {
                    var remoteTrackingRef = $"refs/remotes/{remoteName}/{upstreamBranch}";
                    if (await GitRefExistsAsync(fullWorkspacePath, remoteTrackingRef, ct).ConfigureAwait(false))
                    {
                        targetRef = $"{remoteName}/{upstreamBranch}";
                    }
                }

                var rebaseResult = await RunGitAsync(fullWorkspacePath, ["rebase", targetRef], ct).ConfigureAwait(false);
                if (rebaseResult.ExitCode == 0)
                {
                    return await SnapshotGitAsync(fullWorkspacePath, commitMessageIfGit: null, ct).ConfigureAwait(false);
                }

                if (await IsGitRebaseInProgressAsync(fullWorkspacePath, ct).ConfigureAwait(false))
                {
                    var unmerged = await GetGitUnmergedFilesAsync(fullWorkspacePath, ct).ConfigureAwait(false);
                    _ = await RunGitAsync(fullWorkspacePath, ["rebase", "--abort"], ct).ConfigureAwait(false);

                    var (branch, headCommitId) = await GetGitHeadInfoAsync(fullWorkspacePath, ct).ConfigureAwait(false);
                    return new RevisionSnapshot(
                        VcsBackendKind.GitWorktree,
                        branch,
                        headCommitId,
                        OpId: null,
                        HasConflicts: true,
                        ChangedFiles: unmerged);
                }

                throw new VcsCommandException(
                    $"{_options.GitExecutablePath} rebase {targetRef}",
                    rebaseResult.ExitCode,
                    rebaseResult.StandardOutput,
                    rebaseResult.StandardError);
            }

            case VcsBackendKind.PlainDirectory:
            default:
                throw new NotSupportedException(
                    "RebaseOntoUpstreamAsync is not supported on PlainDirectory workspaces.");
        }
    }

    private const int MaxFileContentBytes = 256 * 1024;
    private const int BinarySniffBytes = 8 * 1024;

    public Task<DiffSummary> DiffAsync(
        string workspacePath,
        string? fromRev = null,
        string? toRev = null,
        CancellationToken ct = default) =>
        DiffDetailedAsync(workspacePath, fromRev, toRev, filterPath: null, ct);

    public async Task<DiffSummary> DiffDetailedAsync(
        string workspacePath,
        string? fromRev = null,
        string? toRev = null,
        string? filterPath = null,
        CancellationToken ct = default,
        bool searchAncestors = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var normalizedFilter = NormalizeOptionalRelativePath(fullWorkspacePath, filterPath);
        var kind = await DetectAsync(fullWorkspacePath, ct, searchAncestors).ConfigureAwait(false);

        switch (kind)
        {
            case VcsBackendKind.JjColocated:
            {
                var jjExe = RequireJjExecutable();
                var revArgs = BuildJjDiffRevisionArgs(fromRev, toRev);

                var diffArgs = new List<string> { "diff", "--git" };
                diffArgs.AddRange(revArgs);
                var diffRes = await RunJjCheckedAsync(jjExe, fullWorkspacePath, diffArgs, ct).ConfigureAwait(false);

                var summaryArgs = new List<string> { "diff", "--summary", "--ignore-working-copy" };
                summaryArgs.AddRange(revArgs);
                var summaryRes = await RunJjCheckedAsync(jjExe, fullWorkspacePath, summaryArgs, ct).ConfigureAwait(false);

                var nameArgs = new List<string> { "diff", "--name-only", "--ignore-working-copy" };
                nameArgs.AddRange(revArgs);
                var nameRes = await RunJjCheckedAsync(jjExe, fullWorkspacePath, nameArgs, ct).ConfigureAwait(false);

                var changedFiles = ParseNonEmptyLines(nameRes.StandardOutput);
                var statusByPath = ParseJjSummaryStatuses(summaryRes.StandardOutput);
                var patchesByPath = SplitUnifiedGitPatches(diffRes.StandardOutput);

                if (normalizedFilter is not null)
                {
                    changedFiles = changedFiles.Where(f => string.Equals(f, normalizedFilter, StringComparison.Ordinal)).ToArray();
                }

                var baseRev = !string.IsNullOrWhiteSpace(fromRev)
                    ? fromRev!
                    : !string.IsNullOrWhiteSpace(toRev)
                        ? $"({toRev})-"
                        : "@-";

                var fileDtos = new List<VcsDiffFileDto>(changedFiles.Count);
                foreach (var relPath in changedFiles)
                {
                    patchesByPath.TryGetValue(relPath, out var rawPatch);
                    rawPatch ??= string.Empty;
                    var status = statusByPath.TryGetValue(relPath, out var s)
                        ? s
                        : InferStatusFromPatch(rawPatch);
                    var (patchText, _) = TruncateUtf8Deterministically(rawPatch, _options.MaxDiffBytes);

                    var fileDto = await BuildJjDiffFileDtoAsync(
                            jjExe,
                            fullWorkspacePath,
                            relPath,
                            status,
                            patchText,
                            baseRev,
                            toRev,
                            ct)
                        .ConfigureAwait(false);
                    fileDtos.Add(fileDto);
                }

                var rawOverallDiff = normalizedFilter is not null
                    ? string.Join('\n', fileDtos.Select(static f => f.Patch).Where(static p => p.Length > 0))
                    : diffRes.StandardOutput;
                var (truncatedText, truncated) = TruncateUtf8Deterministically(rawOverallDiff, _options.MaxDiffBytes);
                return new DiffSummary(truncatedText, truncated, changedFiles, fileDtos);
            }

            case VcsBackendKind.GitWorktree:
            {
                return await DiffGitAsync(fullWorkspacePath, fromRev, toRev, normalizedFilter, ct).ConfigureAwait(false);
            }

            case VcsBackendKind.PlainDirectory:
            default:
            {
                if (fromRev is not null || toRev is not null)
                {
                    throw new NotSupportedException(
                        "Revision range DiffAsync is not supported on PlainDirectory workspaces.");
                }

                return new DiffSummary(string.Empty, Truncated: false, ChangedFiles: [], Files: []);
            }
        }
    }

    public async Task<PushResult> PushBookmarkAsync(
        string workspacePath,
        string bookmarkOrBranch,
        string? targetRev = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bookmarkOrBranch);

        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var remoteName = _options.DefaultRemoteName;
        var kind = await DetectAsync(fullWorkspacePath, ct).ConfigureAwait(false);

        switch (kind)
        {
            case VcsBackendKind.JjColocated:
            {
                var jjExe = RequireJjExecutable();
                var effectiveRev = await ResolveJjPushTargetRevAsync(jjExe, fullWorkspacePath, targetRev, ct)
                    .ConfigureAwait(false);

                var authorCheck = await RunJjCheckedAsync(
                        jjExe,
                        fullWorkspacePath,
                        ["log", "-r", effectiveRev, "--no-graph", "--ignore-working-copy", "-T", "author.email()"],
                        ct)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(authorCheck.StandardOutput))
                {
                    await RunJjCheckedAsync(
                            jjExe,
                            fullWorkspacePath,
                            ["describe", "--no-edit", "--reset-author", effectiveRev],
                            ct)
                        .ConfigureAwait(false);
                }

                await RunJjCheckedAsync(
                        jjExe,
                        fullWorkspacePath,
                        ["bookmark", "set", bookmarkOrBranch, "-r", effectiveRev, "--allow-backwards"],
                        ct)
                    .ConfigureAwait(false);

                var commitRes = await RunJjCheckedAsync(
                        jjExe,
                        fullWorkspacePath,
                        ["log", "-r", bookmarkOrBranch, "--no-graph", "--ignore-working-copy", "-T", "commit_id"],
                        ct)
                    .ConfigureAwait(false);

                await RunJjCheckedAsync(
                        jjExe,
                        fullWorkspacePath,
                        [
                            "git",
                            "push",
                            "--remote",
                            remoteName,
                            "--bookmark",
                            bookmarkOrBranch,
                            "--allow-new",
                            "--allow-empty-description",
                        ],
                        ct)
                    .ConfigureAwait(false);

                return new PushResult(bookmarkOrBranch, remoteName, commitRes.StandardOutput.Trim());
            }

            case VcsBackendKind.GitWorktree:
            {
                var effectiveTarget = string.IsNullOrWhiteSpace(targetRev) ? "HEAD" : targetRev;
                var revRes = await RunGitCheckedAsync(fullWorkspacePath, ["rev-parse", effectiveTarget], ct)
                    .ConfigureAwait(false);
                var headCommitId = revRes.StandardOutput.Trim();

                await RunGitCheckedAsync(
                        fullWorkspacePath,
                        ["push", remoteName, $"{headCommitId}:refs/heads/{bookmarkOrBranch}"],
                        ct)
                    .ConfigureAwait(false);

                return new PushResult(bookmarkOrBranch, remoteName, headCommitId);
            }

            case VcsBackendKind.PlainDirectory:
            default:
                throw new NotSupportedException(
                    "PushBookmarkAsync is not supported on PlainDirectory workspaces.");
        }
    }

    public string? ResolveJjExecutable(string? pathEnvironmentOverride = null)
    {
        if (_options.DisableJj)
        {
            return null;
        }

        if (_options.JjExecutablePath is not null)
        {
            if (string.IsNullOrWhiteSpace(_options.JjExecutablePath))
            {
                return null;
            }

            if (!_usesDefaultRunner && _fs.IsLocal)
            {
                return _options.JjExecutablePath;
            }

            return _fs.ResolveExecutable(_options.JjExecutablePath, pathEnvironmentOverride);
        }

        var exeName = OperatingSystem.IsWindows() && _fs.IsLocal ? "jj.exe" : "jj";
        return _fs.ResolveExecutable(exeName, pathEnvironmentOverride);
    }

    internal static (string Text, bool Truncated) TruncateUtf8Deterministically(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, false);
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
        {
            return (text, false);
        }

        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0b1100_0000) == 0b1000_0000)
        {
            length--;
        }

        return (Encoding.UTF8.GetString(bytes, 0, length), true);
    }

    private string RequireJjExecutable() =>
        ResolveJjExecutable()
        ?? throw new InvalidOperationException(
            "Jujutsu ('jj') CLI executable was not found on PATH or in '~/.local/bin/jj'.");

    private async Task<RevisionSnapshot> SnapshotJjAsync(string workspacePath, CancellationToken ct)
    {
        var jjExe = RequireJjExecutable();

        // First call runs WITHOUT --ignore-working-copy so JJ snapshots working-copy changes into @.
        var logRes = await RunJjCheckedAsync(
                jjExe,
                workspacePath,
                [
                    "log",
                    "-r",
                    "@",
                    "--no-graph",
                    "-T",
                    "change_id ++ \"\\t\" ++ commit_id ++ \"\\t\" ++ conflict ++ \"\\n\"",
                ],
                ct)
            .ConfigureAwait(false);

        var line = logRes.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Failed to parse working-copy revision metadata from 'jj log'.");

        var parts = line.Split('\t');
        if (parts.Length < 3)
        {
            throw new InvalidOperationException($"Unexpected 'jj log' template output: '{line}'.");
        }

        var changeId = parts[0].Trim();
        var commitId = parts[1].Trim();
        var hasConflicts = bool.TryParse(parts[2].Trim(), out var conflictFlag) && conflictFlag;

        var opRes = await RunJjCheckedAsync(
                jjExe,
                workspacePath,
                ["op", "log", "-n", "1", "--no-graph", "--ignore-working-copy", "-T", "self.id() ++ \"\\n\""],
                ct)
            .ConfigureAwait(false);

        var opId = opRes.StandardOutput.Trim();

        var diffRes = await RunJjCheckedAsync(
                jjExe,
                workspacePath,
                ["diff", "-r", "@", "--name-only", "--ignore-working-copy"],
                ct)
            .ConfigureAwait(false);

        var changedFiles = ParseNonEmptyLines(diffRes.StandardOutput);

        return new RevisionSnapshot(
            Kind: VcsBackendKind.JjColocated,
            ChangeIdOrBranch: changeId,
            CommitId: commitId,
            OpId: string.IsNullOrEmpty(opId) ? null : opId,
            HasConflicts: hasConflicts,
            ChangedFiles: changedFiles);
    }

    private async Task<RevisionSnapshot> SnapshotGitAsync(
        string workspacePath,
        string? commitMessageIfGit,
        CancellationToken ct)
    {
        var statusRes = await RunGitCheckedAsync(workspacePath, ["status", "--porcelain=v1", "-uall"], ct)
            .ConfigureAwait(false);

        var (hasConflicts, changedFiles) = ParseGitPorcelainStatus(statusRes.StandardOutput);

        if (!string.IsNullOrWhiteSpace(commitMessageIfGit) && changedFiles.Count > 0 && !hasConflicts)
        {
            await RunGitCheckedAsync(workspacePath, ["add", "-A"], ct).ConfigureAwait(false);

            var hasGitEmail = await HasConfiguredGitIdentityAsync(workspacePath, ct).ConfigureAwait(false);
            var commitArgs = new List<string>();
            if (!hasGitEmail)
            {
                commitArgs.Add("-c");
                commitArgs.Add($"user.name={_options.FallbackAuthorName}");
                commitArgs.Add("-c");
                commitArgs.Add($"user.email={_options.FallbackAuthorEmail}");
            }

            commitArgs.Add("commit");
            commitArgs.Add("-m");
            commitArgs.Add(commitMessageIfGit);

            await RunGitCheckedAsync(workspacePath, commitArgs, ct).ConfigureAwait(false);
        }

        var (branch, commitId) = await GetGitHeadInfoAsync(workspacePath, ct).ConfigureAwait(false);

        return new RevisionSnapshot(
            Kind: VcsBackendKind.GitWorktree,
            ChangeIdOrBranch: branch,
            CommitId: commitId,
            OpId: null,
            HasConflicts: hasConflicts,
            ChangedFiles: changedFiles);
    }

    private RevisionSnapshot SnapshotPlainDirectory(string workspacePath)
    {
        if (!_fs.IsLocal || !Directory.Exists(workspacePath))
        {
            return new RevisionSnapshot(VcsBackendKind.PlainDirectory, "plain", "none", null, false, []);
        }

        var files = Directory
            .EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(workspacePath, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        return new RevisionSnapshot(
            Kind: VcsBackendKind.PlainDirectory,
            ChangeIdOrBranch: "plain",
            CommitId: "none",
            OpId: null,
            HasConflicts: false,
            ChangedFiles: files);
    }

    private async Task<DiffSummary> DiffGitAsync(
        string workspacePath,
        string? fromRev,
        string? toRev,
        string? normalizedFilter,
        CancellationToken ct)
    {
        if (fromRev is null && toRev is null)
        {
            var hasHead = await GitRefExistsAsync(workspacePath, "HEAD", ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            var changedSet = new SortedSet<string>(StringComparer.Ordinal);
            var statusByPath = new Dictionary<string, string>(StringComparer.Ordinal);
            var patchesByPath = new Dictionary<string, string>(StringComparer.Ordinal);

            if (hasHead)
            {
                var trackedDiff = await RunGitCheckedAsync(workspacePath, ["--no-pager", "diff", "HEAD"], ct)
                    .ConfigureAwait(false);
                sb.Append(trackedDiff.StandardOutput);
                foreach (var kvp in SplitUnifiedGitPatches(trackedDiff.StandardOutput))
                {
                    patchesByPath[kvp.Key] = kvp.Value;
                }

                var nameStatusRes = await RunGitCheckedAsync(
                        workspacePath,
                        ["--no-pager", "diff", "--name-status", "HEAD"],
                        ct)
                    .ConfigureAwait(false);

                foreach (var (file, status) in ParseGitNameStatus(nameStatusRes.StandardOutput))
                {
                    changedSet.Add(file);
                    statusByPath[file] = status;
                }
            }
            else
            {
                // Unborn HEAD: staged files in index show up via `git diff --cached`
                var cachedDiff = await RunGitAsync(workspacePath, ["--no-pager", "diff", "--cached"], ct)
                    .ConfigureAwait(false);
                if (cachedDiff.ExitCode == 0 && !string.IsNullOrEmpty(cachedDiff.StandardOutput))
                {
                    sb.Append(cachedDiff.StandardOutput);
                    foreach (var kvp in SplitUnifiedGitPatches(cachedDiff.StandardOutput))
                    {
                        patchesByPath[kvp.Key] = kvp.Value;
                        changedSet.Add(kvp.Key);
                        statusByPath[kvp.Key] = "added";
                    }
                }
            }

            var untrackedRes = await RunGitCheckedAsync(
                    workspacePath,
                    ["ls-files", "--others", "--exclude-standard"],
                    ct)
                .ConfigureAwait(false);

            foreach (var relPath in ParseNonEmptyLines(untrackedRes.StandardOutput))
            {
                changedSet.Add(relPath);
                statusByPath.TryAdd(relPath, "untracked");

                if (normalizedFilter is not null && !string.Equals(relPath, normalizedFilter, StringComparison.Ordinal))
                {
                    continue;
                }

                var untrackedDiff = await RunGitAsync(
                        workspacePath,
                        ["--no-pager", "diff", "--no-index", "--", "/dev/null", relPath],
                        ct)
                    .ConfigureAwait(false);

                // git diff --no-index exits with 1 when differences exist.
                if (untrackedDiff.ExitCode is 0 or 1 && !string.IsNullOrEmpty(untrackedDiff.StandardOutput))
                {
                    sb.Append(untrackedDiff.StandardOutput);
                    patchesByPath[relPath] = untrackedDiff.StandardOutput;
                }
            }

            IReadOnlyList<string> changedList = normalizedFilter is not null
                ? changedSet.Where(f => string.Equals(f, normalizedFilter, StringComparison.Ordinal)).ToArray()
                : [.. changedSet];

            var fileDtos = new List<VcsDiffFileDto>(changedList.Count);
            foreach (var relPath in changedList)
            {
                patchesByPath.TryGetValue(relPath, out var rawPatch);
                rawPatch ??= string.Empty;
                var status = statusByPath.TryGetValue(relPath, out var s) ? s : InferStatusFromPatch(rawPatch);
                var (patchText, _) = TruncateUtf8Deterministically(rawPatch, _options.MaxDiffBytes);

                var dto = await BuildGitDiffFileDtoAsync(
                        workspacePath,
                        relPath,
                        status,
                        patchText,
                        baseRev: hasHead ? "HEAD" : null,
                        targetRev: null,
                        ct)
                    .ConfigureAwait(false);
                fileDtos.Add(dto);
            }

            var overallRaw = normalizedFilter is not null
                ? string.Join('\n', fileDtos.Select(static f => f.Patch).Where(static p => p.Length > 0))
                : sb.ToString();
            var (truncatedText, truncated) = TruncateUtf8Deterministically(overallRaw, _options.MaxDiffBytes);
            return new DiffSummary(truncatedText, truncated, changedList, fileDtos);
        }

        List<string> rangeArgs = (fromRev, toRev) switch
        {
            (not null, not null) => [fromRev, toRev],
            (not null, null) => [fromRev],
            (null, not null) => [$"{toRev}^!"],
            _ => [],
        };

        var gitDiffArgs = new List<string> { "--no-pager", "diff" };
        gitDiffArgs.AddRange(rangeArgs);
        var diffOut = await RunGitCheckedAsync(workspacePath, gitDiffArgs, ct).ConfigureAwait(false);

        var gitStatusArgs = new List<string> { "--no-pager", "diff", "--name-status" };
        gitStatusArgs.AddRange(rangeArgs);
        var statusOut = await RunGitCheckedAsync(workspacePath, gitStatusArgs, ct).ConfigureAwait(false);

        var rangeStatuses = ParseGitNameStatus(statusOut.StandardOutput);
        var rangePatches = SplitUnifiedGitPatches(diffOut.StandardOutput);
        var files = rangeStatuses.Select(static x => x.Path).ToArray();
        if (normalizedFilter is not null)
        {
            files = files.Where(f => string.Equals(f, normalizedFilter, StringComparison.Ordinal)).ToArray();
        }

        var statusLookup = rangeStatuses.ToDictionary(static x => x.Path, static x => x.Status, StringComparer.Ordinal);
        var effectiveBaseRev = fromRev ?? (toRev is not null ? $"{toRev}^" : "HEAD");
        var rangeFileDtos = new List<VcsDiffFileDto>(files.Length);
        foreach (var relPath in files)
        {
            rangePatches.TryGetValue(relPath, out var rawPatch);
            rawPatch ??= string.Empty;
            var status = statusLookup.TryGetValue(relPath, out var s) ? s : InferStatusFromPatch(rawPatch);
            var (patchText, _) = TruncateUtf8Deterministically(rawPatch, _options.MaxDiffBytes);
            var dto = await BuildGitDiffFileDtoAsync(
                    workspacePath,
                    relPath,
                    status,
                    patchText,
                    effectiveBaseRev,
                    toRev,
                    ct)
                .ConfigureAwait(false);
            rangeFileDtos.Add(dto);
        }

        var rawRangeDiff = normalizedFilter is not null
            ? string.Join('\n', rangeFileDtos.Select(static f => f.Patch).Where(static p => p.Length > 0))
            : diffOut.StandardOutput;
        var (text, wasTruncated) = TruncateUtf8Deterministically(rawRangeDiff, _options.MaxDiffBytes);
        return new DiffSummary(text, wasTruncated, files, rangeFileDtos);
    }

    private async Task<VcsDiffFileDto> BuildJjDiffFileDtoAsync(
        string jjExe,
        string workspacePath,
        string relPath,
        string status,
        string patch,
        string baseRev,
        string? toRev,
        CancellationToken ct)
    {
        var patchBinary = IsPatchMarkedBinary(patch);
        string? workingText = null;
        var workingBinary = false;

        if (!string.Equals(status, "deleted", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(toRev))
            {
                (workingText, workingBinary) = await ReadWorkspaceFileSafeAsync(workspacePath, relPath, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var escapedRel = EscapeJjFilesetPath(relPath);
                var showTarget = await RunJjAsync(
                        jjExe,
                        workspacePath,
                        ["file", "show", "-r", toRev, "--ignore-working-copy", $"file:\"{escapedRel}\""],
                        ct)
                    .ConfigureAwait(false);
                if (showTarget.ExitCode == 0)
                {
                    if (ContainsNullChar(showTarget.StandardOutput))
                    {
                        workingBinary = true;
                    }
                    else
                    {
                        workingText = TruncateUtf8Deterministically(showTarget.StandardOutput, MaxFileContentBytes).Text;
                    }
                }
            }
        }

        var isBinary = patchBinary || workingBinary;
        if (isBinary)
        {
            return new VcsDiffFileDto(relPath, status, patch, HeadText: null, WorkingText: null, IsBinary: true);
        }

        string? headText = null;
        if (!string.Equals(status, "added", StringComparison.Ordinal) &&
            !string.Equals(status, "untracked", StringComparison.Ordinal))
        {
            var escapedRel = EscapeJjFilesetPath(relPath);
            var showBase = await RunJjAsync(
                    jjExe,
                    workspacePath,
                    ["file", "show", "-r", baseRev, "--ignore-working-copy", $"file:\"{escapedRel}\""],
                    ct)
                .ConfigureAwait(false);
            if (showBase.ExitCode == 0)
            {
                if (ContainsNullChar(showBase.StandardOutput))
                {
                    return new VcsDiffFileDto(relPath, status, patch, HeadText: null, WorkingText: null, IsBinary: true);
                }

                headText = TruncateUtf8Deterministically(showBase.StandardOutput, MaxFileContentBytes).Text;
            }
        }

        return new VcsDiffFileDto(relPath, status, patch, headText, workingText, IsBinary: false);
    }

    private async Task<VcsDiffFileDto> BuildGitDiffFileDtoAsync(
        string workspacePath,
        string relPath,
        string status,
        string patch,
        string? baseRev,
        string? targetRev,
        CancellationToken ct)
    {
        var patchBinary = IsPatchMarkedBinary(patch);
        string? workingText = null;
        var workingBinary = false;

        if (!string.Equals(status, "deleted", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(targetRev))
            {
                (workingText, workingBinary) = await ReadWorkspaceFileSafeAsync(workspacePath, relPath, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var showTarget = await RunGitAsync(workspacePath, ["--no-pager", "show", $"{targetRev}:{relPath}"], ct)
                    .ConfigureAwait(false);
                if (showTarget.ExitCode == 0)
                {
                    if (ContainsNullChar(showTarget.StandardOutput))
                    {
                        workingBinary = true;
                    }
                    else
                    {
                        workingText = TruncateUtf8Deterministically(showTarget.StandardOutput, MaxFileContentBytes).Text;
                    }
                }
            }
        }

        var isBinary = patchBinary || workingBinary;
        if (isBinary)
        {
            return new VcsDiffFileDto(relPath, status, patch, HeadText: null, WorkingText: null, IsBinary: true);
        }

        string? headText = null;
        if (!string.IsNullOrWhiteSpace(baseRev) &&
            !string.Equals(status, "added", StringComparison.Ordinal) &&
            !string.Equals(status, "untracked", StringComparison.Ordinal))
        {
            var showBase = await RunGitAsync(workspacePath, ["--no-pager", "show", $"{baseRev}:{relPath}"], ct)
                .ConfigureAwait(false);
            if (showBase.ExitCode == 0)
            {
                if (ContainsNullChar(showBase.StandardOutput))
                {
                    return new VcsDiffFileDto(relPath, status, patch, HeadText: null, WorkingText: null, IsBinary: true);
                }

                headText = TruncateUtf8Deterministically(showBase.StandardOutput, MaxFileContentBytes).Text;
            }
        }

        return new VcsDiffFileDto(relPath, status, patch, headText, workingText, IsBinary: false);
    }

    private async Task<(string? Text, bool IsBinary)> ReadWorkspaceFileSafeAsync(
        string fullWorkspacePath,
        string relPath,
        CancellationToken ct)
    {
        var rootFull = Path.GetFullPath(fullWorkspacePath);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(rootFull, relPath.TrimStart('/', '\\')));
        if (!candidate.StartsWith(rootWithSep, PathComparison) || !_fs.FileExists(candidate))
        {
            return (null, false);
        }

        if (!_fs.IsLocal)
        {
            var remoteBytes = _fs.ReadFileBytes(candidate, MaxFileContentBytes);
            if (remoteBytes is null)
            {
                return (null, false);
            }

            var remoteSniff = Math.Min(remoteBytes.Length, BinarySniffBytes);
            if (remoteBytes.AsSpan(0, remoteSniff).IndexOf((byte)0) >= 0)
            {
                return (null, true);
            }

            var remoteValid = TrimTrailingIncompleteUtf8Bytes(remoteBytes);
            return (Encoding.UTF8.GetString(remoteBytes, 0, remoteValid), false);
        }

        try
        {
            var canonical = WorkspaceFileReader.ResolveContainedPath(rootFull, candidate);
            await using var fs = WorkspaceFileReader.Open(canonical);
            var maxToRead = (int)Math.Min(fs.Length, MaxFileContentBytes);
            var buffer = new byte[maxToRead];
            var read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)
                .ConfigureAwait(false);

            var sniffLen = Math.Min(read, BinarySniffBytes);
            if (buffer.AsSpan(0, sniffLen).IndexOf((byte)0) >= 0)
            {
                return (null, true);
            }

            // If truncated at MaxFileContentBytes, trim trailing incomplete UTF-8 continuation bytes safely.
            var validLen = read;
            if (fs.Length > read)
            {
                validLen = TrimTrailingIncompleteUtf8Bytes(buffer.AsSpan(0, read));
            }

            return (Encoding.UTF8.GetString(buffer, 0, validLen), false);
        }
        catch (IOException)
        {
            return (null, false);
        }
        catch (UnauthorizedAccessException)
        {
            return (null, false);
        }
    }

    private static int TrimTrailingIncompleteUtf8Bytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0;
        }

        var i = bytes.Length - 1;
        var contCount = 0;
        while (i >= 0 && contCount < 4 && (bytes[i] & 0b1100_0000) == 0b1000_0000)
        {
            i--;
            contCount++;
        }

        if (i < 0)
        {
            return bytes.Length;
        }

        var lead = bytes[i];
        var expectedSeqLen = lead switch
        {
            < 0b1000_0000 => 1,
            >= 0b1111_0000 => 4,
            >= 0b1110_0000 => 3,
            >= 0b1100_0000 => 2,
            _ => 1
        };

        var actualSeqLen = bytes.Length - i;
        return actualSeqLen < expectedSeqLen ? i : bytes.Length;
    }

    private static string? NormalizeOptionalRelativePath(string fullWorkspacePath, string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        var trimmed = rawPath.Trim();
        var rootFull = Path.GetFullPath(fullWorkspacePath);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        string candidateFull;
        if (Path.IsPathRooted(trimmed))
        {
            var rootedFull = Path.GetFullPath(trimmed);
            if (string.Equals(rootedFull, rootFull, PathComparison))
            {
                return null;
            }

            candidateFull = rootedFull.StartsWith(rootWithSep, PathComparison)
                ? rootedFull
                : Path.GetFullPath(Path.Combine(rootFull, trimmed.TrimStart('/', '\\')));
        }
        else
        {
            candidateFull = Path.GetFullPath(Path.Combine(rootFull, trimmed));
        }

        if (string.Equals(candidateFull, rootFull, PathComparison))
        {
            return null;
        }

        if (!candidateFull.StartsWith(rootWithSep, PathComparison))
        {
            throw new InvalidOperationException($"Path '{rawPath}' escapes workspace root.");
        }

        return Path.GetRelativePath(rootFull, candidateFull).Replace('\\', '/');
    }

    private static Dictionary<string, string> ParseJjSummaryStatuses(string summaryOutput)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in ParseNonEmptyLines(summaryOutput))
        {
            if (rawLine.Length < 3 || rawLine[1] != ' ')
            {
                continue;
            }

            var code = rawLine[0];
            var pathPart = rawLine[2..].Trim();
            pathPart = ExpandBraceRenameTarget(pathPart);
            var status = code switch
            {
                'A' => "added",
                'D' => "deleted",
                _ => "modified"
            };
            if (pathPart.Length > 0)
            {
                map[pathPart] = status;
            }
        }

        return map;
    }

    private static List<(string Path, string Status)> ParseGitNameStatus(string nameStatusOutput)
    {
        var result = new List<(string Path, string Status)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in nameStatusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var statusCode = parts[0].Trim();
            var rawPath = parts[^1].Trim();
            var unquoted = UnquoteGitPath(rawPath);
            if (unquoted.Length == 0 || !seen.Add(unquoted))
            {
                continue;
            }

            var status = statusCode[0] switch
            {
                'A' => "added",
                'D' => "deleted",
                '?' => "untracked",
                _ => "modified"
            };
            result.Add((unquoted, status));
        }

        return result;
    }

    private static Dictionary<string, string> SplitUnifiedGitPatches(string diffText)
    {
        var patches = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(diffText))
        {
            return patches;
        }

        var lines = diffText.Split('\n');
        var chunkBuilder = new StringBuilder();
        string? currentPath = null;
        string? fallbackHeaderPath = null;

        void FlushChunk()
        {
            var resolved = currentPath ?? fallbackHeaderPath;
            if (!string.IsNullOrEmpty(resolved) && chunkBuilder.Length > 0)
            {
                patches[resolved] = chunkBuilder.ToString();
            }

            chunkBuilder.Clear();
            currentPath = null;
            fallbackHeaderPath = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmedCr = rawLine.TrimEnd('\r');
            if (trimmedCr.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushChunk();
                fallbackHeaderPath = ExtractPathFromDiffGitHeader(trimmedCr);
            }

            if (fallbackHeaderPath is not null || currentPath is not null)
            {
                if (trimmedCr.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    var plusPath = ParseUnifiedFileHeaderPath(trimmedCr[4..]);
                    if (plusPath is not null)
                    {
                        currentPath = plusPath;
                    }
                }
                else if (currentPath is null && trimmedCr.StartsWith("--- ", StringComparison.Ordinal))
                {
                    var minusPath = ParseUnifiedFileHeaderPath(trimmedCr[4..]);
                    if (minusPath is not null)
                    {
                        currentPath = minusPath;
                    }
                }

                chunkBuilder.Append(rawLine);
                if (i < lines.Length - 1)
                {
                    chunkBuilder.Append('\n');
                }
            }
        }

        FlushChunk();
        return patches;
    }

    private static string? ExtractPathFromDiffGitHeader(string headerLine)
    {
        // Format: `diff --git a/foo.txt b/foo.txt` or `diff --git a/dev/null b/foo.txt`
        var rest = headerLine["diff --git ".Length..].Trim();
        var bMarkerIdx = rest.LastIndexOf(" b/", StringComparison.Ordinal);
        if (bMarkerIdx >= 0)
        {
            return UnquoteGitPath(rest[(bMarkerIdx + 3)..]);
        }

        var bQuotedIdx = rest.LastIndexOf(" \"b/", StringComparison.Ordinal);
        if (bQuotedIdx >= 0)
        {
            var unquoted = UnquoteGitPath(rest[(bQuotedIdx + 1)..]);
            return unquoted.StartsWith("b/", StringComparison.Ordinal) ? unquoted[2..] : unquoted;
        }

        return null;
    }

    private static string? ParseUnifiedFileHeaderPath(string rawTarget)
    {
        var tabIdx = rawTarget.IndexOf('\t');
        var clean = (tabIdx >= 0 ? rawTarget[..tabIdx] : rawTarget).Trim();
        var unquoted = UnquoteGitPath(clean);
        if (string.Equals(unquoted, "/dev/null", StringComparison.Ordinal) ||
            string.Equals(unquoted, "dev/null", StringComparison.Ordinal))
        {
            return null;
        }

        if (unquoted.StartsWith("a/", StringComparison.Ordinal) ||
            unquoted.StartsWith("b/", StringComparison.Ordinal))
        {
            return unquoted[2..];
        }

        return unquoted.TrimStart('/');
    }

    private static string UnquoteGitPath(string input)
    {
        if (input.Length < 2 || input[0] != '"' || input[^1] != '"')
        {
            return input;
        }

        var inner = input[1..^1];
        var bytes = new List<byte>(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '\\' && i + 1 < inner.Length)
            {
                var next = inner[i + 1];
                if (next is >= '0' and <= '7' && i + 3 < inner.Length &&
                    inner[i + 2] is >= '0' and <= '7' &&
                    inner[i + 3] is >= '0' and <= '7')
                {
                    var oct = ((next - '0') << 6) | ((inner[i + 2] - '0') << 3) | (inner[i + 3] - '0');
                    bytes.Add((byte)oct);
                    i += 3;
                    continue;
                }

                bytes.Add(next switch
                {
                    'n' => (byte)'\n',
                    'r' => (byte)'\r',
                    't' => (byte)'\t',
                    '\\' => (byte)'\\',
                    '"' => (byte)'"',
                    _ => (byte)next
                });
                i++;
                continue;
            }

            if (c < 128)
            {
                bytes.Add((byte)c);
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes([c]));
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string ExpandBraceRenameTarget(string jjSummaryPath)
    {
        // Handles `dir/{old.txt => new.txt}` or `{old => new}/file.txt`
        var openIdx = jjSummaryPath.IndexOf('{');
        var arrowIdx = jjSummaryPath.IndexOf("=>", StringComparison.Ordinal);
        var closeIdx = jjSummaryPath.IndexOf('}');
        if (openIdx >= 0 && arrowIdx > openIdx && closeIdx > arrowIdx)
        {
            var prefix = jjSummaryPath[..openIdx];
            var targetPart = jjSummaryPath[(arrowIdx + 2)..closeIdx].Trim();
            var suffix = jjSummaryPath[(closeIdx + 1)..];
            return (prefix + targetPart + suffix).Replace("//", "/", StringComparison.Ordinal).Trim('/');
        }

        return jjSummaryPath;
    }

    private static string InferStatusFromPatch(string patch)
    {
        if (patch.Contains("\nnew file mode ", StringComparison.Ordinal) ||
            patch.Contains("\n--- /dev/null", StringComparison.Ordinal))
        {
            return "added";
        }

        if (patch.Contains("\ndeleted file mode ", StringComparison.Ordinal) ||
            patch.Contains("\n+++ /dev/null", StringComparison.Ordinal))
        {
            return "deleted";
        }

        return "modified";
    }

    private static bool IsPatchMarkedBinary(string patch) =>
        patch.Contains("\nBinary files ", StringComparison.Ordinal) ||
        patch.StartsWith("Binary files ", StringComparison.Ordinal) ||
        patch.Contains("\nGIT binary patch", StringComparison.Ordinal);

    private static bool ContainsNullChar(string text) =>
        text.AsSpan(0, Math.Min(text.Length, BinarySniffBytes)).IndexOf('\0') >= 0;

    private static string EscapeJjFilesetPath(string relPath) =>
        relPath.Replace("\\", "\\\\", StringComparison.Ordinal)
               .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static List<string> BuildJjDiffRevisionArgs(string? fromRev, string? toRev) =>
        (fromRev, toRev) switch
        {
            (null, null) => [],
            (not null, not null) => ["--from", fromRev, "--to", toRev],
            (not null, null) => ["--from", fromRev, "--to", "@"],
            (null, not null) => ["-r", toRev],
        };

    private async Task<string> ResolveJjUpstreamRevsetAsync(
        string jjExe,
        string workspacePath,
        string upstreamBranch,
        string? remoteName,
        CancellationToken ct)
    {
        if (upstreamBranch.Contains('@', StringComparison.Ordinal))
        {
            return upstreamBranch;
        }

        if (!string.IsNullOrEmpty(remoteName))
        {
            var remoteBookmark = $"{upstreamBranch}@{remoteName}";
            var probe = await RunJjAsync(
                    jjExe,
                    workspacePath,
                    ["log", "-r", remoteBookmark, "--no-graph", "--ignore-working-copy", "-T", "commit_id"],
                    ct)
                .ConfigureAwait(false);

            if (probe.ExitCode == 0 && !string.IsNullOrWhiteSpace(probe.StandardOutput))
            {
                return remoteBookmark;
            }
        }

        return upstreamBranch;
    }

    private async Task<string> ResolveJjPushTargetRevAsync(
        string jjExe,
        string workspacePath,
        string? targetRev,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(targetRev))
        {
            return targetRev;
        }

        // Snapshot working copy and inspect whether @ is an untouched empty working-copy commit above @-.
        var inspect = await RunJjCheckedAsync(
                jjExe,
                workspacePath,
                [
                    "log",
                    "-r",
                    "@",
                    "--no-graph",
                    "-T",
                    "empty ++ \"\\t\" ++ if(description, \"1\", \"0\") ++ \"\\n\"",
                ],
                ct)
            .ConfigureAwait(false);

        var fields = inspect.StandardOutput.Trim().Split('\t');
        var isEmpty = fields.Length > 0 && bool.TryParse(fields[0], out var emptyFlag) && emptyFlag;
        var hasDescription = fields.Length > 1 && fields[1] == "1";

        if (isEmpty && !hasDescription)
        {
            var parentProbe = await RunJjAsync(
                    jjExe,
                    workspacePath,
                    ["log", "-r", "@- ~ root()", "--no-graph", "--ignore-working-copy", "-T", "commit_id"],
                    ct)
                .ConfigureAwait(false);

            if (parentProbe.ExitCode == 0 && !string.IsNullOrWhiteSpace(parentProbe.StandardOutput))
            {
                return "@-";
            }
        }

        return "@";
    }

    private async Task<bool> JjHasRemoteAsync(
        string jjExe,
        string workspacePath,
        string remoteName,
        CancellationToken ct)
    {
        var res = await RunJjAsync(jjExe, workspacePath, ["git", "remote", "list"], ct).ConfigureAwait(false);
        if (res.ExitCode != 0)
        {
            return false;
        }

        foreach (var line in ParseNonEmptyLines(res.StandardOutput))
        {
            var spaceIdx = line.IndexOf(' ', StringComparison.Ordinal);
            var name = spaceIdx > 0 ? line[..spaceIdx] : line;
            if (string.Equals(name, remoteName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> GitHasRemoteAsync(string workspacePath, string remoteName, CancellationToken ct)
    {
        var res = await RunGitAsync(workspacePath, ["remote"], ct).ConfigureAwait(false);
        return res.ExitCode == 0 &&
               ParseNonEmptyLines(res.StandardOutput).Any(r => string.Equals(r, remoteName, StringComparison.Ordinal));
    }

    private async Task<bool> GitRefExistsAsync(string workspacePath, string fullRefOrRev, CancellationToken ct)
    {
        var args = fullRefOrRev.StartsWith("refs/", StringComparison.Ordinal)
            ? new[] { "show-ref", "--verify", "--quiet", fullRefOrRev }
            : new[] { "rev-parse", "--verify", "--quiet", fullRefOrRev };

        var res = await RunGitAsync(workspacePath, args, ct).ConfigureAwait(false);
        return res.ExitCode == 0;
    }

    private async Task<(string Branch, string CommitId)> GetGitHeadInfoAsync(
        string workspacePath,
        CancellationToken ct)
    {
        var branchRes = await RunGitAsync(workspacePath, ["symbolic-ref", "--quiet", "--short", "HEAD"], ct)
            .ConfigureAwait(false);
        var branch = branchRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(branchRes.StandardOutput)
            ? branchRes.StandardOutput.Trim()
            : "HEAD";

        var commitRes = await RunGitAsync(workspacePath, ["rev-parse", "HEAD"], ct).ConfigureAwait(false);
        var commitId = commitRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(commitRes.StandardOutput)
            ? commitRes.StandardOutput.Trim()
            : "unborn";

        return (branch, commitId);
    }

    private async Task<bool> HasConfiguredGitIdentityAsync(string workspacePath, CancellationToken ct)
    {
        var emailRes = await RunGitAsync(workspacePath, ["config", "--get", "user.email"], ct).ConfigureAwait(false);
        var nameRes = await RunGitAsync(workspacePath, ["config", "--get", "user.name"], ct).ConfigureAwait(false);
        return emailRes.ExitCode == 0 &&
               !string.IsNullOrWhiteSpace(emailRes.StandardOutput) &&
               nameRes.ExitCode == 0 &&
               !string.IsNullOrWhiteSpace(nameRes.StandardOutput);
    }

    private async Task<bool> IsGitRebaseInProgressAsync(string workspacePath, CancellationToken ct)
    {
        foreach (var marker in new[] { "rebase-merge", "rebase-apply" })
        {
            var res = await RunGitAsync(workspacePath, ["rev-parse", "--git-path", marker], ct).ConfigureAwait(false);
            if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StandardOutput))
            {
                var rawPath = res.StandardOutput.Trim();
                var resolved = Path.IsPathRooted(rawPath)
                    ? rawPath
                    : Path.GetFullPath(Path.Combine(workspacePath, rawPath));

                if (_fs.DirectoryExists(resolved) || _fs.FileExists(resolved))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<string>> GetGitUnmergedFilesAsync(string workspacePath, CancellationToken ct)
    {
        var res = await RunGitAsync(workspacePath, ["diff", "--name-only", "--diff-filter=U"], ct)
            .ConfigureAwait(false);

        return res.ExitCode == 0 ? ParseNonEmptyLines(res.StandardOutput) : [];
    }

    private static (bool HasConflicts, IReadOnlyList<string> ChangedFiles) ParseGitPorcelainStatus(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (false, []);
        }

        var hasConflicts = false;
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 4)
            {
                continue;
            }

            var xy = line[..2];
            if (xy is "UU" or "AA" or "DD" or "AU" or "UA" or "DU" or "UD")
            {
                hasConflicts = true;
            }

            var pathPart = line[3..];
            var arrowIdx = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIdx >= 0)
            {
                pathPart = pathPart[(arrowIdx + 4)..];
            }

            pathPart = pathPart.Trim().Trim('"');
            if (!string.IsNullOrEmpty(pathPart) && seen.Add(pathPart))
            {
                files.Add(pathPart);
            }
        }

        return (hasConflicts, files);
    }

    private Task<ProcessRunResult> RunJjAsync(
        string jjExe,
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var fullArgs = new List<string>(args.Count + 3)
        {
            "--no-pager",
            "--color",
            "never",
        };
        fullArgs.AddRange(args);

        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JJ_USER")))
        {
            env["JJ_USER"] = _options.FallbackAuthorName;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JJ_EMAIL")))
        {
            env["JJ_EMAIL"] = _options.FallbackAuthorEmail;
        }

        return _processRunner.RunAsync(
            new ProcessRunRequest(jjExe, fullArgs, workingDirectory, env),
            ct);
    }

    private async Task<ProcessRunResult> RunJjCheckedAsync(
        string jjExe,
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var result = await RunJjAsync(jjExe, workingDirectory, args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new VcsCommandException(
                $"{jjExe} {string.Join(' ', args)}",
                result.ExitCode,
                result.StandardOutput,
                result.StandardError);
        }

        return result;
    }

    private Task<ProcessRunResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct) =>
        _processRunner.RunAsync(
            new ProcessRunRequest(_options.GitExecutablePath, args, workingDirectory),
            ct);

    private async Task<ProcessRunResult> RunGitCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        var result = await RunGitAsync(workingDirectory, args, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new VcsCommandException(
                $"{_options.GitExecutablePath} {string.Join(' ', args)}",
                result.ExitCode,
                result.StandardOutput,
                result.StandardError);
        }

        return result;
    }

    private (string RootPath, bool HasJj, bool HasGit)? FindNearestVcsRoot(
        string startDirectory, bool searchAncestors = true, bool ignoreAmbientRoots = true) =>
        FindNearestVcsRoot(_fs, startDirectory, searchAncestors, ignoreAmbientRoots);

    private static (string RootPath, bool HasJj, bool HasGit)? FindNearestVcsRoot(
        IWorkspaceFileSystemOps fs, string startDirectory, bool searchAncestors = true, bool ignoreAmbientRoots = true)
    {
        var current = NormalizeDirectoryPath(startDirectory);
        var tempRoot = NormalizeDirectoryPath(fs.TempRootPath);

        while (!string.IsNullOrEmpty(current))
        {
            var parent = Path.GetDirectoryName(current);
            var isFilesystemRoot = string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal);

            // Never treat `/` or the system temp root (`/tmp`) itself as a repository root.
            if (!ignoreAmbientRoots || (!isFilesystemRoot && !string.Equals(current, tempRoot, PathComparison)))
            {
                var hasJj = IsValidJjMarker(fs, Path.Combine(current, ".jj"));
                var hasGit = IsValidGitMarker(fs, Path.Combine(current, ".git"));

                if (hasJj || hasGit)
                {
                    return (current, hasJj, hasGit);
                }
            }

            if (isFilesystemRoot || !searchAncestors)
            {
                break;
            }

            current = NormalizeDirectoryPath(parent!);
        }

        return null;
    }

    private static bool IsValidJjMarker(IWorkspaceFileSystemOps fs, string jjPath)
    {
        if (!fs.DirectoryExists(jjPath))
        {
            return false;
        }

        var repoEntry = Path.Combine(jjPath, "repo");
        return fs.DirectoryExists(repoEntry) || fs.FileExists(repoEntry);
    }

    private static bool IsValidGitMarker(IWorkspaceFileSystemOps fs, string gitPath)
    {
        if (fs.DirectoryExists(gitPath))
        {
            return fs.FileExists(Path.Combine(gitPath, "HEAD"));
        }

        if (fs.FileExists(gitPath))
        {
            var firstLine = fs.ReadFirstLine(gitPath);
            return firstLine is not null &&
                   firstLine.TrimStart().StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static IReadOnlyList<string> ParseNonEmptyLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim('\r').Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private void CopyDirectorySafe(string sourceDir, string targetDir, CancellationToken ct) =>
        _fs.CopyDirectory(sourceDir, targetDir, ct);

    private void DeleteDirectoryIfExists(string path) =>
        _fs.DeleteDirectoryIfExists(path);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizeDirectoryPath(left), NormalizeDirectoryPath(right), PathComparison);

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
