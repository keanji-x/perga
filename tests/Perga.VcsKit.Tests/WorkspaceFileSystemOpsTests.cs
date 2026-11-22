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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationRechecksAuthorizationAfterBackendDetection(bool rebase)
    {
        const string path = "/remote/workspace";
        var fs = new InMemoryRemoteFsOps();
        fs.Directories.Add(path);
        fs.Directories.Add(path + "/.git");
        fs.Files[path + "/.git/HEAD"] = "ref: refs/heads/main\n";
        fs.Executables["jj"] = "/usr/bin/jj";
        var detecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDetection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<string>();
        var runner = new StubProcessRunner(async (request, ct) =>
        {
            var command = string.Join(' ', request.Arguments.Skip(3));
            commands.Add(command);
            if (command == "git init --colocate")
            {
                detecting.TrySetResult();
                await finishDetection.Task.WaitAsync(ct);
            }
            return new(0, "", "");
        });
        var vcs = new VcsService(runner, new() { AutoColocateJj = true }, fs);
        var valid = true;
        Task AuthorizeAsync(CancellationToken _) => valid
            ? Task.CompletedTask : Task.FromException(new InvalidOperationException("Runtime was revoked."));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ct = deadline.Token;
        Task pending = rebase
            ? vcs.RebaseOntoUpstreamAsync(path, ct: ct, authorizeMutation: AuthorizeAsync)
            : vcs.RestoreOperationAsync(path, "operation", ct, AuthorizeAsync);
        try
        {
            Assert.Same(detecting.Task, await Task.WhenAny(detecting.Task, pending).WaitAsync(ct));
            valid = false;
        }
        finally { finishDetection.TrySetResult(); }

        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(ct));
        Assert.Equal("Runtime was revoked.", denied.Message);
        Assert.DoesNotContain(commands, command => command.StartsWith(rebase ? "rebase " : "op restore ", StringComparison.Ordinal));
    }

    private sealed class StubProcessRunner(Func<ProcessRunRequest, CancellationToken, Task<ProcessRunResult>>? handler = null) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default) =>
            handler?.Invoke(request, ct) ?? Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
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
        public byte[]? ReadContainedFileBytes(string workspaceRoot, string path, int maxBytes) =>
            Files.TryGetValue(path, out var v) ? System.Text.Encoding.UTF8.GetBytes(v) : null;
        public void CreateDirectory(string path) => Directories.Add(path.TrimEnd('/'));
        public void CopyDirectory(string sourceDir, string targetDir, CancellationToken ct) => Directories.Add(targetDir.TrimEnd('/'));
        public void DeleteDirectoryIfExists(string path) => Directories.Remove(path.TrimEnd('/'));
        public string? ResolveExecutable(string executableName, string? pathEnvironmentOverride = null) =>
            Executables.TryGetValue(executableName, out var resolved) ? resolved : null;
    }
}
