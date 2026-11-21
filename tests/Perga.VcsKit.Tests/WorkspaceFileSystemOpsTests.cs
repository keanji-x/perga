using Perga.VcsKit;

namespace Perga.VcsKit.Tests;

public sealed class WorkspaceFileSystemOpsTests
{
    [Fact]
    public async Task RemoteFsOps_DirectDriverAndVcsBackendDetection_DoNotTouchLocalDisk()
    {
        var fs = new InMemoryRemoteFsOps();
        fs.Directories.Add("/home/jasper/my-repo");
        fs.Directories.Add("/home/jasper/my-repo/.git");
        fs.Files["/home/jasper/my-repo/.git/HEAD"] = "ref: refs/heads/main\n";
        fs.Executables["git"] = "/usr/bin/git";

        var processRunner = new StubProcessRunner();
        var vcs = new VcsService(processRunner, fsOps: fs);
        IWorkspaceDriver direct = new DirectWorkspaceDriver(fs);

        var backends = await vcs.ListWorkspaceBackendsAsync("/home/jasper/my-repo", CancellationToken.None);
        Assert.Contains(VcsBackendKind.GitWorktree, backends);
        Assert.DoesNotContain(VcsBackendKind.JjColocated, backends);

        var provisioned = await direct.ProvisionTrackAsync(
            new WorkspaceProvisionRequest("trk_remote_1", "/home/jasper/my-repo", "/home/jasper/.perga/workspaces/trk_remote_1", []),
            CancellationToken.None);
        Assert.Equal("/home/jasper/my-repo", provisioned.MergedCwd);

        var created = await direct.ProvisionTrackAsync(
            new WorkspaceProvisionRequest("trk_remote_2", "/home/jasper/new-managed", "/home/jasper/.perga/workspaces/trk_remote_2", [], NewEmptyWorkspace: true),
            CancellationToken.None);
        Assert.Equal("/home/jasper/new-managed", created.MergedCwd);
        Assert.Contains("/home/jasper/new-managed", fs.Directories);
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
    }

    private sealed class InMemoryRemoteFsOps : IWorkspaceFileSystemOps
    {
        public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Executables { get; } = new(StringComparer.Ordinal);

        public bool IsLocal => false;
        public bool DirectoryExists(string path) => Directories.Contains(path.TrimEnd('/'));
        public bool FileExists(string path) => Files.ContainsKey(path);
        public string? ReadFirstLine(string path) => Files.TryGetValue(path, out var v) ? v.Split('\n')[0] : null;
        public byte[]? ReadFileBytes(string path, int maxBytes) =>
            Files.TryGetValue(path, out var v) ? System.Text.Encoding.UTF8.GetBytes(v) : null;
        public void CreateDirectory(string path) => Directories.Add(path.TrimEnd('/'));
        public void CopyDirectory(string sourceDir, string targetDir, CancellationToken ct) => Directories.Add(targetDir.TrimEnd('/'));
        public void DeleteDirectoryIfExists(string path) => Directories.Remove(path.TrimEnd('/'));
        public string? ResolveExecutable(string executableName, string? pathEnvironmentOverride = null) =>
            Executables.TryGetValue(executableName, out var resolved) ? resolved : null;
    }
}
