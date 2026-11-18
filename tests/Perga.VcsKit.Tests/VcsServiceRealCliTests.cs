using System.Text;

namespace Perga.VcsKit.Tests;

public sealed class VcsServiceRealCliTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly ProcessRunner _runner = new();

    public VcsServiceRealCliTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"perga-vcskit-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        TryDeleteDirectory(_tempRoot);
    }

    [Fact]
    public void ResolveJjExecutable_FindsUserLocalBinEvenWhenNotInPath()
    {
        var vcs = new VcsService();
        var resolved = vcs.ResolveJjExecutable(pathEnvironmentOverride: "/usr/bin:/bin");

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
        Assert.EndsWith("/.local/bin/jj", resolved.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetectAsync_HandlesPlainGitAutoColocateAndSubdirectories()
    {
        var plainDir = Path.Combine(_tempRoot, "plain");
        Directory.CreateDirectory(plainDir);

        var vcsDefault = new VcsService(new VcsServiceOptions { AutoColocateJj = false });
        Assert.Equal(VcsBackendKind.PlainDirectory, await vcsDefault.DetectAsync(plainDir, CancellationToken.None));

        var gitDir = await InitGitRepoAsync(Path.Combine(_tempRoot, "git-only"));
        Assert.Equal(VcsBackendKind.GitWorktree, await vcsDefault.DetectAsync(gitDir, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(gitDir, ".jj")));

        var vcsAutoColocate = new VcsService(new VcsServiceOptions { AutoColocateJj = true });
        Assert.Equal(VcsBackendKind.JjColocated, await vcsAutoColocate.DetectAsync(gitDir, CancellationToken.None));
        Assert.True(Directory.Exists(Path.Combine(gitDir, ".jj")));

        var nestedSubdir = Path.Combine(gitDir, "src", "module");
        Directory.CreateDirectory(nestedSubdir);
        Assert.Equal(VcsBackendKind.JjColocated, await vcsDefault.DetectAsync(nestedSubdir, CancellationToken.None));

        var vcsGitFallback = new VcsService(new VcsServiceOptions { DisableJj = true });
        Assert.Equal(VcsBackendKind.GitWorktree, await vcsGitFallback.DetectAsync(gitDir, CancellationToken.None));
    }

    [Fact]
    public async Task JjColocated_SnapshotAndRestoreOperation_TimeMachineRollbackWithoutGitCommit()
    {
        var repoDir = await InitColocatedRepoAsync(Path.Combine(_tempRoot, "jj-timemachine"));
        var gitHeadBefore = await RunGitStdoutAsync(repoDir, "rev-parse", "HEAD");

        var vcs = new VcsService();
        var initialSnap = await vcs.SnapshotWorkingCopyAsync(repoDir);
        Assert.Equal(VcsBackendKind.JjColocated, initialSnap.Kind);
        Assert.NotEmpty(initialSnap.ChangeIdOrBranch);
        Assert.NotEmpty(initialSnap.CommitId);
        Assert.NotNull(initialSnap.OpId);
        Assert.False(initialSnap.HasConflicts);
        Assert.Empty(initialSnap.ChangedFiles);

        // Modify working tree and take a second snapshot (must NOT run `git commit` on HEAD).
        await File.WriteAllTextAsync(Path.Combine(repoDir, "hello.txt"), "modified line\n");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "added.txt"), "brand new\n");

        var editedSnap = await vcs.SnapshotWorkingCopyAsync(repoDir, commitMessageIfGit: "ignored-in-jj");
        var gitHeadAfter = await RunGitStdoutAsync(repoDir, "rev-parse", "HEAD");
        Assert.Equal(gitHeadBefore, gitHeadAfter);

        Assert.Equal(initialSnap.ChangeIdOrBranch, editedSnap.ChangeIdOrBranch);
        Assert.NotEqual(initialSnap.CommitId, editedSnap.CommitId);
        Assert.NotEqual(initialSnap.OpId, editedSnap.OpId);
        Assert.False(editedSnap.HasConflicts);
        Assert.Equal(["added.txt", "hello.txt"], editedSnap.ChangedFiles.OrderBy(x => x).ToArray());

        // Time-machine rollback via `jj op restore`!
        await vcs.RestoreOperationAsync(repoDir, initialSnap.OpId!);

        Assert.Equal("initial\n", await File.ReadAllTextAsync(Path.Combine(repoDir, "hello.txt")));
        Assert.False(File.Exists(Path.Combine(repoDir, "added.txt")));

        var restoredSnap = await vcs.SnapshotWorkingCopyAsync(repoDir);
        Assert.Equal(initialSnap.CommitId, restoredSnap.CommitId);
        Assert.Empty(restoredSnap.ChangedFiles);
    }

    [Fact]
    public async Task RestoreOperationAsync_ThrowsNotSupportedOnNonJjBackends()
    {
        var gitOnlyDir = await InitGitRepoAsync(Path.Combine(_tempRoot, "git-no-jj"));
        var vcs = new VcsService(new VcsServiceOptions { AutoColocateJj = false });

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            vcs.RestoreOperationAsync(gitOnlyDir, "000000000000"));
    }

    [Fact]
    public async Task JjColocated_RebaseOntoUpstream_HandlesCleanAndConflictingRebaseWithoutHang()
    {
        var remoteDir = await InitBareRemoteAsync(Path.Combine(_tempRoot, "remote-jj.git"));
        var repoDir = await InitColocatedRepoAsync(Path.Combine(_tempRoot, "jj-rebase"), remoteDir);
        var upstreamClone = await CloneGitRepoAsync(remoteDir, Path.Combine(_tempRoot, "upstream-writer"));

        // 1. Clean rebase onto origin/main
        await File.WriteAllTextAsync(Path.Combine(upstreamClone, "upstream-only.txt"), "from-upstream\n");
        await CommitAndPushGitAsync(upstreamClone, "add upstream-only");

        await File.WriteAllTextAsync(Path.Combine(repoDir, "local-only.txt"), "from-local\n");

        var vcs = new VcsService();
        using var cleanCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cleanRebased = await vcs.RebaseOntoUpstreamAsync(repoDir, "main", cleanCts.Token);

        Assert.False(cleanRebased.HasConflicts);
        Assert.True(File.Exists(Path.Combine(repoDir, "upstream-only.txt")));
        Assert.True(File.Exists(Path.Combine(repoDir, "local-only.txt")));

        // 2. Conflicting rebase onto origin/main — must complete rapidly with HasConflicts == true and 0 hang!
        var preConflictOpId = cleanRebased.OpId!;
        await File.WriteAllTextAsync(Path.Combine(upstreamClone, "hello.txt"), "upstream conflicting line\n");
        await CommitAndPushGitAsync(upstreamClone, "upstream conflict edit");

        await File.WriteAllTextAsync(Path.Combine(repoDir, "hello.txt"), "local conflicting line\n");

        using var conflictCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var conflictRebased = await vcs.RebaseOntoUpstreamAsync(repoDir, "main", conflictCts.Token);

        Assert.Equal(VcsBackendKind.JjColocated, conflictRebased.Kind);
        Assert.True(conflictRebased.HasConflicts);

        // Verify workspace can still roll back cleanly after conflict!
        await vcs.RestoreOperationAsync(repoDir, preConflictOpId, conflictCts.Token);
        var afterRestore = await vcs.SnapshotWorkingCopyAsync(repoDir, ct: conflictCts.Token);
        Assert.False(afterRestore.HasConflicts);
    }

    [Fact]
    public async Task GitWorktree_WorkspaceSnapshotRebaseAndConflictAbort_NeverLeavesRepoWedged()
    {
        var remoteDir = await InitBareRemoteAsync(Path.Combine(_tempRoot, "remote-git.git"));
        var rootGitDir = await InitGitRepoAsync(Path.Combine(_tempRoot, "git-root"), remoteDir);
        var upstreamClone = await CloneGitRepoAsync(remoteDir, Path.Combine(_tempRoot, "git-upstream"));

        var vcs = new VcsService(new VcsServiceOptions { DisableJj = true });
        var wsDir = Path.Combine(_tempRoot, "git-ws-1");
        var ws = await vcs.CreateWorkspaceAsync(rootGitDir, "track-git-1", wsDir);

        Assert.Equal(VcsBackendKind.GitWorktree, ws.Kind);
        Assert.True(Directory.Exists(ws.WorkspacePath));

        // Modify file in git worktree and snapshot with commit message.
        await File.WriteAllTextAsync(Path.Combine(wsDir, "hello.txt"), "worktree conflicting edit\n");
        var committedSnap = await vcs.SnapshotWorkingCopyAsync(wsDir, commitMessageIfGit: "feat: edit hello");
        Assert.Equal("track-git-1", committedSnap.ChangeIdOrBranch);
        Assert.Contains("hello.txt", committedSnap.ChangedFiles);

        // Push conflicting edit to origin/main from upstream clone.
        await File.WriteAllTextAsync(Path.Combine(upstreamClone, "hello.txt"), "upstream conflicting edit\n");
        await CommitAndPushGitAsync(upstreamClone, "upstream edit hello");

        // Rebase worktree onto main -> conflict detected, cleanly aborted without wedging!
        var rebaseSnap = await vcs.RebaseOntoUpstreamAsync(wsDir, "main");
        Assert.True(rebaseSnap.HasConflicts);
        Assert.Contains("hello.txt", rebaseSnap.ChangedFiles);

        // Verify git status is clean and not stuck in interactive rebase state.
        var statusOut = await RunGitStdoutAsync(wsDir, "status", "--porcelain=v1");
        Assert.Equal(string.Empty, statusOut.Trim());

        await vcs.RemoveWorkspaceAsync(ws);
        Assert.False(Directory.Exists(wsDir));
        Assert.True(Directory.Exists(rootGitDir));
    }

    [Fact]
    public async Task PlainDirectory_WorkspaceCreateSnapshotAndRemove_WorksCleanly()
    {
        var sourceDir = Path.Combine(_tempRoot, "plain-src");
        Directory.CreateDirectory(Path.Combine(sourceDir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "a.txt"), "alpha");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "sub", "b.txt"), "beta");

        var vcs = new VcsService();
        var targetDir = Path.Combine(_tempRoot, "plain-ws");
        var ws = await vcs.CreateWorkspaceAsync(sourceDir, "track-plain", targetDir);

        Assert.Equal(VcsBackendKind.PlainDirectory, ws.Kind);
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(targetDir, "a.txt")));
        Assert.Equal("beta", await File.ReadAllTextAsync(Path.Combine(targetDir, "sub", "b.txt")));

        var snap = await vcs.SnapshotWorkingCopyAsync(targetDir);
        Assert.Equal(VcsBackendKind.PlainDirectory, snap.Kind);
        Assert.Equal(["a.txt", "sub/b.txt"], snap.ChangedFiles);

        await vcs.RemoveWorkspaceAsync(ws);
        Assert.False(Directory.Exists(targetDir));
        Assert.True(File.Exists(Path.Combine(sourceDir, "a.txt")));
    }

    [Fact]
    public async Task DiffAsync_JjAndGit_SupportsTruncationAndMultiByteUtf8Safety()
    {
        var repoDir = await InitColocatedRepoAsync(Path.Combine(_tempRoot, "jj-diff"));
        var multiByteContent = string.Concat(Enumerable.Repeat("Perga架构测试🚀\n", 40));
        await File.WriteAllTextAsync(Path.Combine(repoDir, "hello.txt"), multiByteContent, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "extra.txt"), "extra\n", Encoding.UTF8);

        var fullVcs = new VcsService();
        var fullDiff = await fullVcs.DiffAsync(repoDir);
        Assert.False(fullDiff.Truncated);
        Assert.Equal(["extra.txt", "hello.txt"], fullDiff.ChangedFiles.OrderBy(x => x).ToArray());
        Assert.Contains("Perga架构测试🚀", fullDiff.DiffText);

        var cappedVcs = new VcsService(new VcsServiceOptions { MaxDiffBytes = 128 });
        var cappedDiff = await cappedVcs.DiffAsync(repoDir);
        Assert.True(cappedDiff.Truncated);
        Assert.True(Encoding.UTF8.GetByteCount(cappedDiff.DiffText) <= 128);
        Assert.DoesNotContain('\uFFFD', cappedDiff.DiffText);
        Assert.Equal(["extra.txt", "hello.txt"], cappedDiff.ChangedFiles.OrderBy(x => x).ToArray());

        // Also verify Git fallback diff including untracked files.
        var gitDir = await InitGitRepoAsync(Path.Combine(_tempRoot, "git-diff"));
        await File.WriteAllTextAsync(Path.Combine(gitDir, "hello.txt"), "updated\n");
        await File.WriteAllTextAsync(Path.Combine(gitDir, "untracked.txt"), "new untracked\n");

        var gitVcs = new VcsService(new VcsServiceOptions { DisableJj = true });
        var gitDiff = await gitVcs.DiffAsync(gitDir);
        Assert.False(gitDiff.Truncated);
        Assert.Equal(["hello.txt", "untracked.txt"], gitDiff.ChangedFiles.OrderBy(x => x).ToArray());
        Assert.Contains("new untracked", gitDiff.DiffText);
    }

    [Fact]
    public async Task PushBookmarkAsync_JjAndGit_PushesToLocalBareRemote()
    {
        // 1. JJ bookmark push to bare remote
        var jjRemote = await InitBareRemoteAsync(Path.Combine(_tempRoot, "push-jj-remote.git"));
        var jjRepo = await InitColocatedRepoAsync(Path.Combine(_tempRoot, "push-jj-repo"), jjRemote);
        await File.WriteAllTextAsync(Path.Combine(jjRepo, "feature.txt"), "shipped via jj\n");

        var jjVcs = new VcsService();
        var jjPush = await jjVcs.PushBookmarkAsync(jjRepo, "feat/jj-bookmark");

        Assert.Equal("feat/jj-bookmark", jjPush.BookmarkOrBranch);
        Assert.Equal("origin", jjPush.RemoteName);
        Assert.NotEmpty(jjPush.HeadCommitId);

        var remoteJjSha = await RunGitStdoutAsync(jjRemote, "rev-parse", "refs/heads/feat/jj-bookmark");
        Assert.Equal(jjPush.HeadCommitId, remoteJjSha.Trim());

        // 2. Git branch push to bare remote
        var gitRemote = await InitBareRemoteAsync(Path.Combine(_tempRoot, "push-git-remote.git"));
        var gitRepo = await InitGitRepoAsync(Path.Combine(_tempRoot, "push-git-repo"), gitRemote);
        await File.WriteAllTextAsync(Path.Combine(gitRepo, "feature.txt"), "shipped via git\n");

        var gitVcs = new VcsService(new VcsServiceOptions { DisableJj = true });
        var gitSnap = await gitVcs.SnapshotWorkingCopyAsync(gitRepo, commitMessageIfGit: "feat: git push test");
        var gitPush = await gitVcs.PushBookmarkAsync(gitRepo, "feat/git-branch");

        Assert.Equal(gitSnap.CommitId, gitPush.HeadCommitId);
        var remoteGitSha = await RunGitStdoutAsync(gitRemote, "rev-parse", "refs/heads/feat/git-branch");
        Assert.Equal(gitPush.HeadCommitId, remoteGitSha.Trim());
    }

    [Fact]
    public async Task JjColocated_DiffDetailedAsync_PopulatesPerFilePatchHeadWorkingStatusAndFilterPath()
    {
        var repoDir = await InitColocatedRepoAsync(Path.Combine(_tempRoot, "jj-detailed-diff"));
        await File.WriteAllTextAsync(Path.Combine(repoDir, "hello.txt"), "modified via jj\n");
        Directory.CreateDirectory(Path.Combine(repoDir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(repoDir, "nested", "added.txt"), "nested new\n");

        var vcs = new VcsService();
        var detailed = await vcs.DiffDetailedAsync(repoDir);
        var byPath = detailed.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);

        Assert.Equal("modified", byPath["hello.txt"].Status);
        Assert.Equal("initial\n", byPath["hello.txt"].HeadText);
        Assert.Equal("modified via jj\n", byPath["hello.txt"].WorkingText);
        Assert.Contains("-initial", byPath["hello.txt"].Patch, StringComparison.Ordinal);
        Assert.Contains("+modified via jj", byPath["hello.txt"].Patch, StringComparison.Ordinal);
        Assert.False(byPath["hello.txt"].IsBinary);

        Assert.Equal("added", byPath["nested/added.txt"].Status);
        Assert.Null(byPath["nested/added.txt"].HeadText);
        Assert.Equal("nested new\n", byPath["nested/added.txt"].WorkingText);

        var filtered = await vcs.DiffDetailedAsync(repoDir, filterPath: "nested/added.txt");
        var single = Assert.Single(filtered.Files);
        Assert.Equal("nested/added.txt", single.Path);
    }

    private async Task<string> InitBareRemoteAsync(string bareDir)
    {
        Directory.CreateDirectory(bareDir);
        await RunCheckedAsync("git", ["init", "--bare", "-b", "main"], bareDir);
        return bareDir;
    }

    private async Task<string> InitGitRepoAsync(string repoDir, string? bareRemoteDir = null)
    {
        Directory.CreateDirectory(repoDir);
        await RunCheckedAsync("git", ["init", "-b", "main"], repoDir);
        await RunCheckedAsync("git", ["config", "user.name", "Perga Test"], repoDir);
        await RunCheckedAsync("git", ["config", "user.email", "test@perga.local"], repoDir);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "hello.txt"), "initial\n");
        await RunCheckedAsync("git", ["add", "."], repoDir);
        await RunCheckedAsync("git", ["commit", "-m", "initial commit"], repoDir);

        if (bareRemoteDir is not null)
        {
            await RunCheckedAsync("git", ["remote", "add", "origin", bareRemoteDir], repoDir);
            await RunCheckedAsync("git", ["push", "-u", "origin", "main"], repoDir);
        }

        return repoDir;
    }

    private async Task<string> InitColocatedRepoAsync(string repoDir, string? bareRemoteDir = null)
    {
        await InitGitRepoAsync(repoDir, bareRemoteDir);
        var jjExe = new VcsService().ResolveJjExecutable()
                    ?? throw new InvalidOperationException("jj executable is required for real CLI tests.");
        await RunCheckedAsync(jjExe, ["git", "init", "--colocate"], repoDir);
        return repoDir;
    }

    private async Task<string> CloneGitRepoAsync(string remoteDir, string cloneDir)
    {
        await RunCheckedAsync("git", ["clone", remoteDir, cloneDir], _tempRoot);
        await RunCheckedAsync("git", ["config", "user.name", "Upstream Writer"], cloneDir);
        await RunCheckedAsync("git", ["config", "user.email", "upstream@perga.local"], cloneDir);
        return cloneDir;
    }

    private async Task CommitAndPushGitAsync(string repoDir, string message)
    {
        await RunCheckedAsync("git", ["add", "-A"], repoDir);
        await RunCheckedAsync("git", ["commit", "-m", message], repoDir);
        await RunCheckedAsync("git", ["push", "origin", "main"], repoDir);
    }

    private async Task<string> RunGitStdoutAsync(string cwd, params string[] args)
    {
        var res = await RunCheckedAsync("git", args, cwd);
        return res.StandardOutput.Trim();
    }

    private async Task<ProcessRunResult> RunCheckedAsync(string file, IReadOnlyList<string> args, string cwd)
    {
        var res = await _runner.RunAsync(new ProcessRunRequest(file, args, cwd));
        if (res.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command '{file} {string.Join(' ', args)}' failed ({res.ExitCode}): {res.StandardError}");
        }

        return res;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Ignore best-effort temp cleanup errors.
        }
    }
}
