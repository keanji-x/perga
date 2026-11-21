using System.Text.Json;

namespace Perga.VcsKit.Tests;

public sealed class WorkspaceDriversTests : IDisposable
{
    private readonly string _tempRoot;

    public WorkspaceDriversTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"perga-vcskit-drv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        TryDeleteDirectory(_tempRoot);
    }

    [Fact]
    public void WorkspaceDriverRegistry_CreateDefault_RegistersDirectAndVcsWorkspace()
    {
        var vcs = new VcsService();
        var registry = WorkspaceDriverRegistry.CreateDefault(vcs);

        Assert.Equal(["direct", "vcs-workspace"], registry.RegisteredDrivers);
        Assert.IsType<DirectWorkspaceDriver>(registry.GetRequired("direct"));
        Assert.IsType<VcsWorkspaceDriver>(registry.GetRequired("vcs-workspace"));

        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("fuse-overlayfs"));
    }

    [Fact]
    public void WorkspaceDriverRegistry_RejectsDuplicateDrivers()
    {
        Assert.Throws<ArgumentException>(() =>
            new WorkspaceDriverRegistry([new DirectWorkspaceDriver(), new DirectWorkspaceDriver()]));
    }

    [Fact]
    public async Task DirectWorkspaceDriver_PassthroughLifecycle_NeverDeletesSourceOrLowerDirs()
    {
        var sourceDir = Path.Combine(_tempRoot, "source-repo");
        var cacheTarget = Path.Combine(sourceDir, "target");
        var cacheNodeModules = Path.Combine(sourceDir, "node_modules");
        Directory.CreateDirectory(cacheTarget);
        Directory.CreateDirectory(cacheNodeModules);

        var sourceFile = Path.Combine(sourceDir, "README.md");
        var cachedArtifact = Path.Combine(cacheTarget, "cached.bin");
        await File.WriteAllTextAsync(sourceFile, "# Perga\n");
        await File.WriteAllTextAsync(cachedArtifact, "binary-cache");

        var driver = new DirectWorkspaceDriver();
        Assert.Equal("direct", driver.DriverId);

        var req = new WorkspaceProvisionRequest(
            TrackId: "trk_direct_1",
            SourcePath: sourceDir,
            TargetRootDir: Path.Combine(_tempRoot, "workspaces"),
            LowerCacheDirs: ["target", cacheNodeModules]);

        var spec = await driver.ProvisionTrackAsync(req);
        Assert.Equal("direct", spec.DriverId);
        Assert.Equal(sourceDir, spec.LogicalRoot);
        Assert.Equal(sourceDir, spec.MergedCwd);

        var mounted = await driver.EnsureMountedAsync(spec);
        Assert.Equal(sourceDir, mounted.MergedCwd);

        await driver.TeardownTrackAsync(spec);

        Assert.True(File.Exists(sourceFile));
        Assert.True(File.Exists(cachedArtifact));
        Assert.True(Directory.Exists(cacheNodeModules));
    }

    [Fact]
    public async Task VcsWorkspaceDriver_JjColocatedLifecycle_ProvisionsIsolatesAndTearsDownSafely()
    {
        var repoDir = await CreateInitializedColocatedJjRepoAsync(Path.Combine(_tempRoot, "jj-root"));
        var lowerCacheDir = Path.Combine(_tempRoot, "shared-cargo-target");
        Directory.CreateDirectory(lowerCacheDir);
        var lowerSentinel = Path.Combine(lowerCacheDir, "keep.txt");
        await File.WriteAllTextAsync(lowerSentinel, "keep");

        var vcs = new VcsService();
        var driver = new VcsWorkspaceDriver(vcs);
        Assert.Equal("vcs-workspace", driver.DriverId);

        var workspacesRoot = Path.Combine(_tempRoot, "track-workspaces");
        var req = new WorkspaceProvisionRequest(
            TrackId: "alpha",
            SourcePath: repoDir,
            TargetRootDir: workspacesRoot,
            LowerCacheDirs: [lowerCacheDir]);

        var spec = await driver.ProvisionTrackAsync(req);
        var expectedWsDir = Path.Combine(workspacesRoot, "alpha");
        Assert.Equal("vcs-workspace", spec.DriverId);
        Assert.Equal(repoDir, spec.LogicalRoot);
        Assert.Equal(expectedWsDir, spec.MergedCwd);
        Assert.True(Directory.Exists(expectedWsDir));
        Assert.True(File.Exists(Path.Combine(expectedWsDir, "app.txt")));

        // Simulate SQLite persistence round-trip of WorkspaceSpec JSON.
        var serializedSpec = JsonSerializer.Serialize(spec);
        var hydratedSpec = JsonSerializer.Deserialize<WorkspaceSpec>(serializedSpec)!;

        var mounted = await driver.EnsureMountedAsync(hydratedSpec);
        Assert.Equal(expectedWsDir, mounted.MergedCwd);

        await File.WriteAllTextAsync(Path.Combine(mounted.MergedCwd, "isolated.txt"), "only-in-track");
        Assert.False(File.Exists(Path.Combine(repoDir, "isolated.txt")));

        await driver.TeardownTrackAsync(hydratedSpec);

        Assert.False(Directory.Exists(expectedWsDir));
        Assert.True(Directory.Exists(repoDir));
        Assert.True(File.Exists(Path.Combine(repoDir, "app.txt")));
        Assert.True(File.Exists(lowerSentinel));

        // Verify `jj workspace list` in root repo no longer lists `track-alpha`.
        var runner = new ProcessRunner();
        var jjExe = vcs.ResolveJjExecutable()!;
        var listRes = await runner.RunAsync(new ProcessRunRequest(jjExe, ["workspace", "list"], repoDir));
        Assert.Equal(0, listRes.ExitCode);
        Assert.DoesNotContain("track-alpha", listRes.StandardOutput);
    }

    [Fact]
    public async Task VcsWorkspaceDriver_RefusesTargetCollidingWithLowerDirs()
    {
        var repoDir = await CreateInitializedColocatedJjRepoAsync(Path.Combine(_tempRoot, "jj-guard"));
        var targetRoot = Path.Combine(_tempRoot, "ws-root");
        var collidingLowerDir = Path.Combine(targetRoot, "trk_collide");
        Directory.CreateDirectory(collidingLowerDir);

        var driver = new VcsWorkspaceDriver(new VcsService());
        var req = new WorkspaceProvisionRequest(
            TrackId: "trk_collide",
            SourcePath: repoDir,
            TargetRootDir: targetRoot,
            LowerCacheDirs: [collidingLowerDir]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.ProvisionTrackAsync(req));
    }

    private static async Task<string> CreateInitializedColocatedJjRepoAsync(string repoDir)
    {
        Directory.CreateDirectory(repoDir);
        var runner = new ProcessRunner();

        await RunCheckedAsync(runner, "git", ["init", "-b", "main"], repoDir);
        await RunCheckedAsync(runner, "git", ["config", "user.name", "Perga Test"], repoDir);
        await RunCheckedAsync(runner, "git", ["config", "user.email", "test@perga.local"], repoDir);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "app.txt"), "v1\n");
        await RunCheckedAsync(runner, "git", ["add", "."], repoDir);
        await RunCheckedAsync(runner, "git", ["commit", "-m", "initial"], repoDir);

        var jjExe = new VcsService().ResolveJjExecutable()
                    ?? throw new InvalidOperationException("Expected jj binary in ~/.local/bin/jj or PATH.");
        await RunCheckedAsync(runner, jjExe, ["git", "init", "--colocate"], repoDir);

        return repoDir;
    }

    private static async Task RunCheckedAsync(
        IProcessRunner runner,
        string file,
        IReadOnlyList<string> args,
        string cwd)
    {
        var res = await runner.RunAsync(new ProcessRunRequest(file, args, cwd));
        if (res.ExitCode != 0)
        {
            throw new InvalidOperationException($"Setup command '{file} {string.Join(' ', args)}' failed: {res.StandardError}");
        }
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
