using System.Runtime.InteropServices;

namespace Perga.VcsKit.Tests;

public sealed class OverlayWorkspaceDriverTests
{
    [Fact]
    public async Task DirectorySnapshotsAreSharedAndPinnedAcrossSourceChangesAndTrackDeletion()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Directory.CreateTempSubdirectory("perga-overlay-test-").FullName;
        var specs = new List<WorkspaceSpec>();
        OverlayWorkspaceDriver? driver = null;
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var workspaces = Directory.CreateDirectory(Path.Combine(root, "workspaces")).FullName;
            var file = Path.Combine(source, "source.txt");
            await File.WriteAllTextAsync(file, "first");
            File.CreateSymbolicLink(Path.Combine(source, "relative.txt"), "source.txt");
            var mounts = new Mounts();
            driver = new OverlayWorkspaceDriver(new VcsService(), mounts,
                new OverlayWorkspaceOptions(workspaces, "mkdir -p target && cp source.txt target/seed"));

            WorkspaceProvisionRequest Request() => new(
                $"trk_{Guid.NewGuid():N}", source, workspaces, []);

            var provisioned = await Task.WhenAll(driver.ProvisionTrackAsync(Request()), driver.ProvisionTrackAsync(Request()));
            specs.AddRange(provisioned);
            var first = provisioned[0];
            var second = provisioned[1];
            var oldSnapshot = Assert.Single(first.LowerDirs);
            Assert.Equal(oldSnapshot, Assert.Single(second.LowerDirs));
            Assert.NotEqual(first.UpperDir, second.UpperDir);
            Assert.Equal("source.txt", new FileInfo(Path.Combine(oldSnapshot, "relative.txt")).LinkTarget);
            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(oldSnapshot, "target/seed")));

            await File.WriteAllTextAsync(file, "second");
            var changed = await driver.ProvisionTrackAsync(Request());
            specs.Add(changed);
            var newSnapshot = Assert.Single(changed.LowerDirs);
            Assert.NotEqual(oldSnapshot, newSnapshot);
            Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(oldSnapshot, "source.txt")));
            Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(newSnapshot, "source.txt")));

            var mounted = await driver.EnsureMountedAsync(first);
            Assert.Equal(first.MergedCwd, mounted.MergedCwd);

            var locked = Directory.CreateDirectory(Path.Combine(first.UpperDir!, "locked")).FullName;
            var upperEdit = Path.Combine(locked, "edit");
            await File.WriteAllTextAsync(upperEdit, "keep");
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(first.MergedCwd));
            mounts.Busy = true;
            await Assert.ThrowsAsync<WorkspaceUnavailableException>(() => driver.TeardownTrackAsync(first));
            Assert.Equal("keep", await File.ReadAllTextAsync(upperEdit));
            mounts.Busy = false;
            File.SetUnixFileMode(locked, UnixFileMode.None);
            await driver.TeardownTrackAsync(first);
            Assert.True(Directory.Exists(locked));
            await driver.CleanupDeletedTrackAsync(first);
            await driver.CleanupDeletedTrackAsync(first);
            Assert.False(Directory.Exists(first.UpperDir));
            Assert.True(Directory.Exists(second.UpperDir));
            Assert.True(Directory.Exists(oldSnapshot));
        }
        finally
        {
            foreach (var spec in specs)
                await driver!.CleanupDeletedTrackAsync(spec);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OrdinarySnapshotsIncludeExtendedAttributesAndRejectOverlayAttributes()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = Directory.CreateTempSubdirectory("perga-overlay-attrs-").FullName;
        var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        var workspaces = Directory.CreateDirectory(Path.Combine(root, "workspaces")).FullName;
        var file = Path.Combine(source, "file");
        await File.WriteAllTextAsync(file, "content");
        var driver = new OverlayWorkspaceDriver(new VcsService(), new Mounts(), new OverlayWorkspaceOptions(workspaces));
        WorkspaceProvisionRequest Request() => new($"trk_{Guid.NewGuid():N}", source, workspaces, []);
        var specs = new List<WorkspaceSpec>();
        try
        {
            var result = SetAttribute(file, "user.perga", [1], 1, 0);
            if (result < 0 && Marshal.GetLastPInvokeError() == 95) // EOPNOTSUPP
                return;
            Assert.Equal(0, result);
            specs.Add(await driver.ProvisionTrackAsync(Request()));
            Assert.Equal(0, SetAttribute(file, "user.perga", [2], 1, 0));
            specs.Add(await driver.ProvisionTrackAsync(Request()));
            Assert.NotEqual(specs[0].LowerDirs[0], specs[1].LowerDirs[0]);
            Assert.Equal(0, SetAttribute(file, "user.overlay.metacopy", [(byte)'y'], 1, 0));
            await Assert.ThrowsAsync<WorkspaceUnavailableException>(() => driver.ProvisionTrackAsync(Request()));
        }
        finally
        {
            foreach (var spec in specs)
                await driver.CleanupDeletedTrackAsync(spec);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OverlayRejectsJujutsuSourcesAndVcsAncestorsOfWorkspaceStorage()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = Directory.CreateTempSubdirectory("perga-overlay-boundary-").FullName;
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            Directory.CreateDirectory(Path.Combine(repo, ".jj", "repo"));
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var workspaces = Path.Combine(root, "workspaces");
            var nestedStorage = new OverlayWorkspaceDriver(new VcsService(), new Mounts(), new OverlayWorkspaceOptions(Path.Combine(repo, "storage")));
            Assert.False(await nestedStorage.IsAvailableAsync(source, CancellationToken.None));
            var driver = new OverlayWorkspaceDriver(new VcsService(), new Mounts(), new OverlayWorkspaceOptions(workspaces));
            Assert.False(await driver.IsAvailableAsync(repo, CancellationToken.None));
            var nested = Directory.CreateDirectory(Path.Combine(source, "nested", ".git")).FullName;
            await File.WriteAllTextAsync(Path.Combine(nested, "HEAD"), "ref: refs/heads/main");
            File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                await Assert.ThrowsAsync<WorkspaceUnavailableException>(() => driver.ProvisionTrackAsync(
                    new WorkspaceProvisionRequest($"trk_{Guid.NewGuid():N}", source, workspaces, [])));
                Assert.Empty(Directory.EnumerateDirectories(Path.Combine(workspaces, ".overlayfs", "snapshots")));
            }
            finally
            {
                File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GitSnapshotPreservesSeparateFetchAndPushDestinations()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Directory.CreateTempSubdirectory("perga-overlay-git-").FullName;
        var workspaces = Path.Combine(root, "workspaces");
        var mounts = new Mounts { GitStagingRoot = workspaces, InterruptNextGitSetup = true };
        var driver = new OverlayWorkspaceDriver(new VcsService(), mounts, new OverlayWorkspaceOptions(workspaces));
        WorkspaceSpec? spec = null;
        var runner = new ProcessRunner();
        Task<ProcessRunResult> Git(string cwd, params string[] args) =>
            runner.RunAsync(new ProcessRunRequest("git", args, cwd));
        async Task RunGit(string cwd, params string[] args)
        {
            var result = await Git(cwd, args);
            Assert.True(result.ExitCode == 0, result.StandardError);
        }

        try
        {
            var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
            var upstream = Path.Combine(root, "upstream.git");
            var fork = Path.Combine(root, "fork:local.git");
            await RunGit(root, "init", "--bare", "--quiet", upstream);
            await RunGit(root, "init", "--bare", "--quiet", fork);
            await RunGit(source, "init", "--quiet", "--initial-branch=main");
            await File.WriteAllTextAsync(Path.Combine(source, "base"), "base");
            await RunGit(source, "add", "base");
            await RunGit(source, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-qm", "Baseline");
            await RunGit(source, "config", $"url.{root}/.insteadOf", "company/");
            await RunGit(source, "remote", "add", "origin", "company/upstream.git");
            await RunGit(source, "remote", "set-url", "--push", "origin", "../fork:local.git");

            var request = new WorkspaceProvisionRequest($"trk_{Guid.NewGuid():N}", source, workspaces, []);
            await Assert.ThrowsAsync<WorkspaceUnavailableException>(() => driver.ProvisionTrackAsync(request));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(workspaces, ".overlayfs", "tracks")));
            spec = await driver.ProvisionTrackAsync(request);
            var snapshot = Assert.Single(spec.LowerDirs);
            Assert.StartsWith("stage_", mounts.GitMounts[0]);
            Assert.StartsWith("stage_", mounts.GitMounts[1]);
            Assert.NotEqual(mounts.GitMounts[0], mounts.GitMounts[1]);
            Assert.Equal($"track-{request.TrackId}", (await Git(spec.MergedCwd, "branch", "--show-current")).StandardOutput.Trim());
            await RunGit(source, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-qm", "Advance");
            var moved = Path.Combine(root, "source-moved");
            Directory.Move(source, moved);
            Assert.Equal(snapshot, Assert.Single((await driver.ProvisionTrackAsync(request)).LowerDirs));
            Assert.Equal(2, mounts.GitMounts.Count);
            Assert.Equal(upstream, (await Git(snapshot, "remote", "get-url", "origin")).StandardOutput.Trim());
            await RunGit(snapshot, "push", "origin", "HEAD:refs/heads/track");
            Assert.Equal(0, (await Git(fork, "show-ref", "--verify", "refs/heads/track")).ExitCode);
            Assert.NotEqual(0, (await Git(upstream, "show-ref", "--verify", "refs/heads/track")).ExitCode);

            await RunGit(moved, "config", "remote.origin.url", $"{upstream}\n{fork}");
            var failure = await Assert.ThrowsAsync<WorkspaceUnavailableException>(() => driver.ProvisionTrackAsync(
                request with { TrackId = $"trk_{Guid.NewGuid():N}", SourcePath = moved }));
            Assert.Contains("multiline origin URLs", failure.Message);
            Assert.Equal(2, mounts.GitMounts.Count);
        }
        finally
        {
            if (spec is not null)
                await driver.CleanupDeletedTrackAsync(spec);
            Directory.Delete(root, recursive: true);
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "setxattr")]
    private static extern int SetAttribute(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, string name, byte[] bytes, nuint size, int flags);

    private sealed class Mounts : IOverlayMounts
    {
        public bool Busy { get; set; }
        public string? GitStagingRoot { get; init; }
        public bool InterruptNextGitSetup { get; set; }
        public List<string> GitMounts { get; } = [];

        public Task<bool> ProbeAsync(CancellationToken ct) => Task.FromResult(true);

        public async Task MountAsync(string trackId, string snapshotId, CancellationToken ct)
        {
            if (!OperatingSystem.IsLinux() || GitStagingRoot is null || !trackId.StartsWith("stage_", StringComparison.Ordinal))
                return;

            var root = Path.Combine(GitStagingRoot, ".overlayfs");
            var lower = Path.Combine(root, "snapshots", snapshotId);
            var merged = Path.Combine(root, "tracks", trackId, "merged");
            File.SetUnixFileMode(merged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var copy = await new ProcessRunner().RunAsync(new ProcessRunRequest(
                "/usr/bin/cp", ["-a", Path.Combine(lower, "."), merged], lower), ct);
            Assert.Equal(0, copy.ExitCode);
            GitMounts.Add(trackId);
            if (InterruptNextGitSetup)
            {
                InterruptNextGitSetup = false;
                await File.WriteAllTextAsync(Path.Combine(merged, ".git", "HEAD.lock"), "", ct);
            }
        }

        public Task UnmountAsync(string trackId, string snapshotId, CancellationToken ct) =>
            Busy ? Task.FromException(new WorkspaceUnavailableException("Workspace is busy.")) : Task.CompletedTask;
    }
}
