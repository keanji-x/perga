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

public sealed class VcsService : IVcsService
{
    private readonly IProcessRunner _processRunner;
    private readonly VcsServiceOptions _options;
    private readonly bool _usesDefaultRunner;

    public VcsService()
        : this(new ProcessRunner(), new VcsServiceOptions())
    {
    }

    public VcsService(VcsServiceOptions options)
        : this(new ProcessRunner(), options)
    {
    }

    public VcsService(IProcessRunner processRunner, VcsServiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        _processRunner = processRunner;
        _options = options ?? new VcsServiceOptions();
        _usesDefaultRunner = processRunner is ProcessRunner;

        if (_options.MaxDiffBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDiffBytes must be positive.");
        }
    }

    public async Task<VcsBackendKind> DetectAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        var startDir = Directory.Exists(fullPath)
            ? fullPath
            : File.Exists(fullPath)
                ? Path.GetDirectoryName(fullPath)
                : null;

        if (string.IsNullOrEmpty(startDir) || !Directory.Exists(startDir))
        {
            return VcsBackendKind.PlainDirectory;
        }

        var vcsRoot = FindNearestVcsRoot(startDir);
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

    public async Task<IsolatedWorkspace> CreateWorkspaceAsync(
        string repoPath,
        string workspaceName,
        string targetDir,
        string? baseRev = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDir);

        var fullRepoPath = Path.GetFullPath(repoPath);
        var fullTargetDir = Path.GetFullPath(targetDir);

        if (PathsEqual(fullRepoPath, fullTargetDir))
        {
            throw new InvalidOperationException(
                $"Target workspace path '{fullTargetDir}' must differ from source path '{fullRepoPath}'.");
        }

        var parentDir = Path.GetDirectoryName(fullTargetDir);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        var kind = await DetectAsync(fullRepoPath, ct).ConfigureAwait(false);
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
                if (jjExe is not null && Directory.Exists(rootRepoPath) && !string.IsNullOrWhiteSpace(workspace.WorkspaceName))
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
                if (Directory.Exists(rootRepoPath))
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

    public async Task<DiffSummary> DiffAsync(
        string workspacePath,
        string? fromRev = null,
        string? toRev = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        var fullWorkspacePath = Path.GetFullPath(workspacePath);
        var kind = await DetectAsync(fullWorkspacePath, ct).ConfigureAwait(false);

        switch (kind)
        {
            case VcsBackendKind.JjColocated:
            {
                var jjExe = RequireJjExecutable();
                var revArgs = BuildJjDiffRevisionArgs(fromRev, toRev);

                var diffArgs = new List<string> { "diff", "--git" };
                diffArgs.AddRange(revArgs);
                var diffRes = await RunJjCheckedAsync(jjExe, fullWorkspacePath, diffArgs, ct).ConfigureAwait(false);

                var nameArgs = new List<string> { "diff", "--name-only", "--ignore-working-copy" };
                nameArgs.AddRange(revArgs);
                var nameRes = await RunJjCheckedAsync(jjExe, fullWorkspacePath, nameArgs, ct).ConfigureAwait(false);

                var changedFiles = ParseNonEmptyLines(nameRes.StandardOutput);
                var (truncatedText, truncated) = TruncateUtf8Deterministically(diffRes.StandardOutput, _options.MaxDiffBytes);
                return new DiffSummary(truncatedText, truncated, changedFiles);
            }

            case VcsBackendKind.GitWorktree:
            {
                return await DiffGitAsync(fullWorkspacePath, fromRev, toRev, ct).ConfigureAwait(false);
            }

            case VcsBackendKind.PlainDirectory:
            default:
            {
                if (fromRev is not null || toRev is not null)
                {
                    throw new NotSupportedException(
                        "Revision range DiffAsync is not supported on PlainDirectory workspaces.");
                }

                return new DiffSummary(string.Empty, Truncated: false, ChangedFiles: []);
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

            if (!_usesDefaultRunner)
            {
                return _options.JjExecutablePath;
            }

            if (Path.IsPathRooted(_options.JjExecutablePath))
            {
                return File.Exists(_options.JjExecutablePath) ? _options.JjExecutablePath : null;
            }

            return SearchExecutableOnPath(_options.JjExecutablePath, pathEnvironmentOverride);
        }

        var exeName = OperatingSystem.IsWindows() ? "jj.exe" : "jj";
        var fromPath = SearchExecutableOnPath(exeName, pathEnvironmentOverride);
        if (fromPath is not null)
        {
            return fromPath;
        }

        foreach (var homeDir in EnumerateCandidateHomeDirectories())
        {
            var localBinCandidate = Path.Combine(homeDir, ".local", "bin", exeName);
            if (File.Exists(localBinCandidate))
            {
                return localBinCandidate;
            }
        }

        return null;
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

    private static RevisionSnapshot SnapshotPlainDirectory(string workspacePath)
    {
        if (!Directory.Exists(workspacePath))
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
        CancellationToken ct)
    {
        if (fromRev is null && toRev is null)
        {
            var hasHead = await GitRefExistsAsync(workspacePath, "HEAD", ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            var changedSet = new SortedSet<string>(StringComparer.Ordinal);

            if (hasHead)
            {
                var trackedDiff = await RunGitCheckedAsync(workspacePath, ["--no-pager", "diff", "HEAD"], ct)
                    .ConfigureAwait(false);
                sb.Append(trackedDiff.StandardOutput);

                var trackedNames = await RunGitCheckedAsync(
                        workspacePath,
                        ["--no-pager", "diff", "--name-only", "HEAD"],
                        ct)
                    .ConfigureAwait(false);

                foreach (var file in ParseNonEmptyLines(trackedNames.StandardOutput))
                {
                    changedSet.Add(file);
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
                var untrackedDiff = await RunGitAsync(
                        workspacePath,
                        ["--no-pager", "diff", "--no-index", "--", "/dev/null", relPath],
                        ct)
                    .ConfigureAwait(false);

                // git diff --no-index exits with 1 when differences exist.
                if (untrackedDiff.ExitCode is 0 or 1 && !string.IsNullOrEmpty(untrackedDiff.StandardOutput))
                {
                    sb.Append(untrackedDiff.StandardOutput);
                }
            }

            var (truncatedText, truncated) = TruncateUtf8Deterministically(sb.ToString(), _options.MaxDiffBytes);
            return new DiffSummary(truncatedText, truncated, [.. changedSet]);
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

        var gitNameArgs = new List<string> { "--no-pager", "diff", "--name-only" };
        gitNameArgs.AddRange(rangeArgs);
        var nameOut = await RunGitCheckedAsync(workspacePath, gitNameArgs, ct).ConfigureAwait(false);

        var files = ParseNonEmptyLines(nameOut.StandardOutput);
        var (text, wasTruncated) = TruncateUtf8Deterministically(diffOut.StandardOutput, _options.MaxDiffBytes);
        return new DiffSummary(text, wasTruncated, files);
    }

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

                if (Directory.Exists(resolved) || File.Exists(resolved))
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

    private static (string RootPath, bool HasJj, bool HasGit)? FindNearestVcsRoot(string startDirectory)
    {
        var current = NormalizeDirectoryPath(startDirectory);
        var tempRoot = NormalizeDirectoryPath(Path.GetTempPath());

        while (!string.IsNullOrEmpty(current))
        {
            var parent = Directory.GetParent(current)?.FullName;
            var isFilesystemRoot = string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal);

            // Never treat `/` or the system temp root (`/tmp`) itself as a repository root.
            if (!isFilesystemRoot && !string.Equals(current, tempRoot, PathComparison))
            {
                var hasJj = IsValidJjMarker(Path.Combine(current, ".jj"));
                var hasGit = IsValidGitMarker(Path.Combine(current, ".git"));

                if (hasJj || hasGit)
                {
                    return (current, hasJj, hasGit);
                }
            }

            if (isFilesystemRoot)
            {
                break;
            }

            current = NormalizeDirectoryPath(parent!);
        }

        return null;
    }

    private static bool IsValidJjMarker(string jjPath)
    {
        if (!Directory.Exists(jjPath))
        {
            return false;
        }

        var repoEntry = Path.Combine(jjPath, "repo");
        return Directory.Exists(repoEntry) || File.Exists(repoEntry);
    }

    private static bool IsValidGitMarker(string gitPath)
    {
        if (Directory.Exists(gitPath))
        {
            return File.Exists(Path.Combine(gitPath, "HEAD"));
        }

        if (File.Exists(gitPath))
        {
            try
            {
                var firstLine = File.ReadLines(gitPath).FirstOrDefault();
                return firstLine is not null &&
                       firstLine.TrimStart().StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    private static string? SearchExecutableOnPath(string executableName, string? pathEnvironmentOverride = null)
    {
        var pathVar = pathEnvironmentOverride ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return null;
        }

        foreach (var entry in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(entry.Trim(), executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateHomeDirectories()
    {
        var yielded = new HashSet<string>(StringComparer.Ordinal);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && yielded.Add(userProfile))
        {
            yield return userProfile;
        }

        var homeEnv = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(homeEnv) && yielded.Add(homeEnv))
        {
            yield return homeEnv;
        }

        var userName = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(userName))
        {
            var linuxHome = Path.Combine("/home", userName);
            if (Directory.Exists(linuxHome) && yielded.Add(linuxHome))
            {
                yield return linuxHome;
            }
        }
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

    private static void CopyDirectorySafe(string sourceDir, string targetDir, CancellationToken ct)
    {
        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source directory '{sourceDir}' does not exist.");
        }

        var sourceEntries = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
        var sourceSubdirs = Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories);

        Directory.CreateDirectory(targetDir);

        var normalizedTargetPrefix = NormalizeDirectoryPath(targetDir) + Path.DirectorySeparatorChar;

        foreach (var dir in sourceSubdirs)
        {
            ct.ThrowIfCancellationRequested();
            var fullSubdir = Path.GetFullPath(dir);
            if (PathsEqual(fullSubdir, targetDir) ||
                fullSubdir.StartsWith(normalizedTargetPrefix, PathComparison))
            {
                continue;
            }

            var relDir = Path.GetRelativePath(sourceDir, fullSubdir);
            Directory.CreateDirectory(Path.Combine(targetDir, relDir));
        }

        foreach (var file in sourceEntries)
        {
            ct.ThrowIfCancellationRequested();
            var fullFile = Path.GetFullPath(file);
            if (fullFile.StartsWith(normalizedTargetPrefix, PathComparison))
            {
                continue;
            }

            var relFile = Path.GetRelativePath(sourceDir, fullFile);
            var destFile = Path.Combine(targetDir, relFile);
            var destParent = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destParent))
            {
                Directory.CreateDirectory(destParent);
            }

            File.Copy(fullFile, destFile, overwrite: true);
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizeDirectoryPath(left), NormalizeDirectoryPath(right), PathComparison);

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
