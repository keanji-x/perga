using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.Execution;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests;

public sealed class WorkspaceFileIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileAdaptersResolvePathsAndRejectSpecialFiles(bool ssh)
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new FileAdapterFixture(ssh);
        var root = fixture.Workspace;
        var files = fixture.Files;
        var outside = Path.Combine(fixture.Home, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside");
        var canonicalOutside = WorkspaceFileReader.ResolveCanonicalPath(outside);
        var inside = Path.Combine(root, "inside.txt");
        await File.WriteAllTextAsync(inside, "inside");
        Directory.CreateDirectory(Path.Combine(root, "subdir"));
        File.CreateSymbolicLink(Path.Combine(root, "alias.txt"), outside);
        var linkedDirectory = Directory.CreateDirectory(Path.Combine(fixture.Home, "nested")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(root, "alias-dir"), linkedDirectory);

        var home = ssh ? fixture.Home : (await files.ListDirectoryAsync("~")).Path;
        var homePath = "~/" + Path.GetRelativePath(home, outside);

        foreach (var path in new[] { "../home/outside.txt", outside, homePath, "alias.txt", "alias-dir/../outside.txt" })
        {
            var preview = await files.BrowseOrReadWorkspaceFileAsync(root, path, 3);
            Assert.Equal(canonicalOutside, preview.RelativePath);
            Assert.Equal("out", preview.Content);
            Assert.True(preview.Truncated);

            var (rawPath, stream) = await files.OpenWorkspaceRawFileAsync(root, path);
            await using (stream)
            {
                Assert.Equal(canonicalOutside, rawPath);
                using var contents = new StreamReader(stream);
                Assert.Equal("outside", await contents.ReadToEndAsync());
            }
            Assert.Equal(canonicalOutside, await files.ValidateWorkspaceFileAsync(root, path));
        }

        foreach (var path in new[] { "inside.txt", "subdir/../inside.txt", inside })
            Assert.Equal("inside.txt", (await files.BrowseOrReadWorkspaceFileAsync(root, path, 16)).RelativePath);

        foreach (var path in new string?[] { null, "", "." })
        {
            var directory = await files.BrowseOrReadWorkspaceFileAsync(root, path, 16);
            Assert.True(directory.IsDirectory);
            Assert.Empty(directory.RelativePath);
            Assert.Equal(canonicalOutside, Assert.Single(directory.Entries!, e => e.Name == "alias.txt").RelativePath);
            var rawError = await Assert.ThrowsAsync<KernelException>(() => files.OpenWorkspaceRawFileAsync(root, path));
            Assert.Equal(KernelErrorCode.NotFound, rawError.Code);
        }

        var homeDirectory = await files.BrowseOrReadWorkspaceFileAsync(root, "~/", 16);
        Assert.True(homeDirectory.IsDirectory);
        Assert.Equal(WorkspaceFileReader.ResolveCanonicalPath(home), homeDirectory.RelativePath);

        var fifo = Path.Combine(root, "fifo");
        await RunAsync(root, "mkfifo", fifo);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(root, "socket")));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var path in new[] { "fifo", "socket", "/dev/zero", "embedded\0null" })
            await AssertFileErrorAsync(files, root, path, KernelErrorCode.InvalidArgument, timeout.Token);
        await AssertFileErrorAsync(files, root, "../missing.txt", KernelErrorCode.NotFound, timeout.Token);
        var directoryError = await Assert.ThrowsAsync<KernelException>(() => files.ValidateWorkspaceFileAsync(root, "subdir"));
        Assert.Equal(KernelErrorCode.InvalidArgument, directoryError.Code);

        if (Environment.UserName != "root")
        {
            var locked = Path.Combine(root, "subdir", "locked.txt");
            await File.WriteAllTextAsync(locked, "locked");
            File.SetUnixFileMode(locked, UnixFileMode.None);
            await AssertFileErrorAsync(files, root, "subdir/locked.txt", KernelErrorCode.Forbidden, timeout.Token);
            File.SetUnixFileMode(Path.GetDirectoryName(locked)!, UnixFileMode.None);
            try
            {
                await AssertFileErrorAsync(files, root, "subdir/locked.txt", KernelErrorCode.Forbidden, timeout.Token);
            }
            finally
            {
                File.SetUnixFileMode(Path.GetDirectoryName(locked)!,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileAdaptersRoundTripLiteralTildePaths(bool ssh)
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new FileAdapterFixture(ssh);
        var root = fixture.Workspace;
        var files = fixture.Files;
        Directory.CreateDirectory(Path.Combine(root, "~"));
        await File.WriteAllTextAsync(Path.Combine(root, "~", "notes.txt"), "workspace notes");
        await File.WriteAllTextAsync(Path.Combine(fixture.Home, "notes.txt"), "home notes");
        await File.WriteAllTextAsync(Path.Combine(root, @"~\literal.txt"), "literal backslash");

        var listing = await files.BrowseOrReadWorkspaceFileAsync(root, null, 256);
        Assert.Equal("./~", Assert.Single(listing.Entries!, entry => entry.Name == "~").RelativePath);
        var preview = await files.BrowseOrReadWorkspaceFileAsync(root, "./~/notes.txt", 256);
        var search = await files.SearchWorkspaceFilesAsync(root, "notes.txt", 50);
        var opened = await files.ValidateWorkspaceFileAsync(root, "./~/notes.txt");
        foreach (var returnedPath in new[] { preview.RelativePath, Assert.Single(search.Paths), opened })
        {
            Assert.Equal("./~/notes.txt", returnedPath);
            Assert.Equal("workspace notes", (await files.BrowseOrReadWorkspaceFileAsync(root, returnedPath, 256)).Content);
        }
        Assert.Equal("literal backslash", (await files.BrowseOrReadWorkspaceFileAsync(root, @"~\literal.txt", 256)).Content);

        await RunAsync(root, "git", "init", "-q");
        Assert.Equal(preview.RelativePath, Assert.Single((await files.SearchWorkspaceFilesAsync(root, "notes.txt", 50)).Paths));

        await using var cluster = new PergaHostTestCluster();
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        if (fixture.TargetId is { } targetId)
            kernel.ExecutionEnvironments.Register(new FileTestEnvironment(kernel.ExecutionEnvironments.Default,
                new ExecutionTargetDto(targetId, "SSH file test", "ssh", IsDefault: false), root, files));
        var track = await CreateTrackAsync(cluster, root, fixture.TargetId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var lead = await kernel.OpenMcpCallerAsync(track.LeadSessionId!, ct);
        using var events = kernel.EventBus.Subscribe(track.Id);
        var (text, error) = await McpKernelAndVcsIntegrationTests.CallToolAsync(cluster, "/mcp/kernel", lead.Token,
            KernelMcpToolNames.FileOpen, new { path = "./~/notes.txt" }, ct);
        Assert.False(error, text);
        var result = JsonSerializer.Deserialize<WorkspaceFileOpenResultDto>(text, PergaJsonDefaults.ApiOptions)!;
        Assert.Equal("./~/notes.txt", result.Path);
        var request = await events.Reader.ReadAsync(ct);
        Assert.Equal(KernelEventKinds.WorkspaceFileOpenRequested, request.Kind);
        Assert.Equal(result.Path, request.Payload.GetProperty("path").GetString());
        var read = await cluster.AuthenticatedClient.GetFromJsonAsync<WorkspaceFileResultDto>(
            $"/api/v1/tracks/{track.Id}/files?path={Uri.EscapeDataString(result.Path)}", PergaJsonDefaults.ApiOptions, ct);
        Assert.Equal("workspace notes", read!.Content);
    }

    [Fact]
    public async Task HttpReadsResolveAliasesOutsideTheWorkspace()
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new FileAdapterFixture(ssh: false);
        Directory.CreateDirectory(Path.Combine(fixture.Home, "nested"));
        var outside = Path.Combine(fixture.Home, "notes.txt");
        await File.WriteAllTextAsync(outside, "outside");
        await File.WriteAllTextAsync(Path.Combine(fixture.Workspace, "notes.txt"), "workspace");
        Directory.CreateSymbolicLink(Path.Combine(fixture.Workspace, "alias-dir"), "../home/nested");
        File.CreateSymbolicLink(Path.Combine(fixture.Workspace, "alias.txt"), outside);

        await using var cluster = new PergaHostTestCluster();
        var track = await CreateTrackAsync(cluster, fixture.Workspace);
        var client = cluster.AuthenticatedClient;
        foreach (var path in new[] { "alias.txt", "alias-dir/../notes.txt" })
        {
            var route = $"/api/v1/tracks/{track.Id}/files";
            var query = $"?path={Uri.EscapeDataString(path)}";
            var preview = await client.GetFromJsonAsync<WorkspaceFileResultDto>(route + query, PergaJsonDefaults.ApiOptions);
            Assert.Equal(WorkspaceFileReader.ResolveExistingPath(outside), preview!.RelativePath);
            Assert.Equal("outside", preview.Content);
            Assert.Equal("outside", await client.GetStringAsync(route + "/raw" + query));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileSearchAdaptersCapTheIndexAndMatchBeyondIt(bool ssh)
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new FileAdapterFixture(ssh);
        var root = fixture.Workspace;
        var files = fixture.Files;
        await RunAsync(root, "git", "init", "-q", "--object-format=sha1");
        await File.WriteAllTextAsync(Path.Combine(root, "z-marker.txt"), "marker");

        // Missing cached entries still consume the scan budget. The untracked marker is emitted first,
        // so reaching the cap is observable even when the query has only one possible match.
        await AddMissingIndexEntriesAsync(root, start: 0, count: 19_999);
        var exact = await files.SearchWorkspaceFilesAsync(root, "z-marker", 50);
        Assert.Equal(["z-marker.txt"], exact.Paths);
        Assert.False(exact.Truncated);

        await AddMissingIndexEntriesAsync(root, start: 19_999, count: 1);
        var capped = await files.SearchWorkspaceFilesAsync(root, "z-marker", 50);
        Assert.Equal(exact.Paths, capped.Paths);
        Assert.True(capped.Truncated);

        await RunAsync(root, "git", "add", "z-marker.txt");
        var index = await files.SearchWorkspaceFilesAsync(root, null, WorkspaceFileSearchResultDto.MaxLimit);
        Assert.Empty(index.Paths);
        Assert.True(index.Truncated);
        var supplemental = await files.SearchWorkspaceFilesAsync(root, "z marker", 200);
        Assert.Equal(exact.Paths, supplemental.Paths);

        string[] lateFiles = ["café.md", "src-index.ts", "z-ignore-src.ts", "^literal.ts", "!literal", "'literal"];
        foreach (var name in lateFiles)
            await File.WriteAllTextAsync(Path.Combine(root, name), "");
        Directory.CreateDirectory(Path.Combine(root, "quick-open"));
        await File.WriteAllTextAsync(Path.Combine(root, "quick-open", "public.tsx"), "");
        await RunAsync(root, "git", ["add", "--", .. lateFiles, "quick-open/public.tsx"]);

        Assert.Equal(["café.md"], (await files.SearchWorkspaceFilesAsync(root, "cafe", 200)).Paths);
        Assert.Equal(["src-index.ts"], (await files.SearchWorkspaceFilesAsync(root, "^src", 200)).Paths);
        Assert.Equal(["quick-open/public.tsx"], (await files.SearchWorkspaceFilesAsync(root, "quick pub", 200)).Paths);
        Assert.Equal(["src-index.ts"], (await files.SearchWorkspaceFilesAsync(root, "src !ignore", 200)).Paths);
        Assert.DoesNotContain("z-ignore-src.ts", (await files.SearchWorkspaceFilesAsync(root, "!ignore", 200)).Paths);
        Assert.Equal(["src-index.ts", "café.md"], (await files.SearchWorkspaceFilesAsync(root, ".md$ | ^src", 200)).Paths);
        Assert.Equal(["^literal.ts"], (await files.SearchWorkspaceFilesAsync(root, "^literal.ts", 200)).Paths);
        Assert.Contains("!literal", (await files.SearchWorkspaceFilesAsync(root, "!literal", 200)).Paths);
        Assert.Contains("'literal", (await files.SearchWorkspaceFilesAsync(root, "'literal", 200)).Paths);
    }

    private static async Task AddMissingIndexEntriesAsync(string root, int start, int count)
    {
        var command = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true
        };
        command.ArgumentList.Add("update-index");
        command.ArgumentList.Add("--index-info");
        using var git = Process.Start(command)!;
        for (var i = start; i < start + count; i++)
            await git.StandardInput.WriteLineAsync($"100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391\tcached-{i:D5}");
        git.StandardInput.Close();
        await git.WaitForExitAsync();
        Assert.Equal(0, git.ExitCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileSearchAdaptersRankCandidatesAndRespectGitIgnore(bool ssh)
    {
        if (OperatingSystem.IsWindows()) return;

        using var fixture = new FileAdapterFixture(ssh);
        var root = fixture.Workspace;
        var files = fixture.Files;
        foreach (var path in new[] { "node_modules/readme", "docs/readme", "readme.txt", "readme-guides/a", "my/r-e-a-d-m-e.md",
                     "hidden/readme-hidden.txt", "readme-deleted.txt", "quotes/'readme\nquoted" })
        {
            var fullPath = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, path);
        }
        File.CreateSymbolicLink(Path.Combine(root, "readme-link"), Path.Combine(root, "readme.txt"));
        var fallback = await files.SearchWorkspaceFilesAsync(root, "readme", 4);
        Assert.Equal(["docs/readme", "readme.txt", "readme-deleted.txt", "hidden/readme-hidden.txt"], fallback.Paths);
        Assert.True(fallback.Truncated);
        var allFallback = await files.SearchWorkspaceFilesAsync(root, "readme", 200);
        Assert.DoesNotContain("node_modules/readme", allFallback.Paths);
        Assert.DoesNotContain("readme-link", allFallback.Paths);
        Assert.Contains("quotes/'readme\nquoted", allFallback.Paths);
        Assert.Equal("my/r-e-a-d-m-e.md", allFallback.Paths[^1]);

        await RunAsync(root, "git", "init", "-q");
        await RunAsync(root, "git", "add", "readme-deleted.txt", "docs/readme");
        File.Delete(Path.Combine(root, "readme-deleted.txt"));
        var changed = Directory.CreateDirectory(Path.Combine(root, "changed")).FullName;
        var tracked = Path.Combine(changed, "readme-tracked.txt");
        await File.WriteAllTextAsync(tracked, "");
        await File.WriteAllTextAsync(Path.Combine(root, "readme-pipe"), "");
        await RunAsync(root, "git", "add", "changed/readme-tracked.txt", "readme-pipe");
        var moved = Path.Combine(fixture.Home, "moved");
        Directory.Move(changed, moved);
        Directory.CreateSymbolicLink(changed, moved);
        File.Delete(Path.Combine(root, "readme-pipe"));
        await RunAsync(root, "mkfifo", "readme-pipe");
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "hidden/\nnode_modules/\n");
        var git = await files.SearchWorkspaceFilesAsync(root, "README", 200);
        Assert.Equal(["docs/readme", "readme.txt", "readme-guides/a", "quotes/'readme\nquoted", "my/r-e-a-d-m-e.md"], git.Paths);
        Assert.False(git.Truncated);
        Assert.Equal(["docs/readme"], (await files.SearchWorkspaceFilesAsync(root, "docs/read", 1)).Paths);

        // An unreadable Git index must surface an error instead of exposing ignored paths via a plain walk.
        await File.WriteAllTextAsync(Path.Combine(root, ".git", "index"), "corrupt");
        var gitError = await Assert.ThrowsAsync<KernelException>(() => files.SearchWorkspaceFilesAsync(root, "readme", 200));
        Assert.Equal(KernelErrorCode.PreconditionFailed, gitError.Code);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.SearchWorkspaceFilesAsync(root, null, 50, cancelled.Token));
    }

    [Fact]
    public async Task SearchEndpointUsesWorkspaceAndCapsResults()
    {
        await using var cluster = new PergaHostTestCluster();
        var workspace = Directory.CreateDirectory(Path.Combine(cluster.DataDir, "search-workspace")).FullName;
        await RunAsync(workspace, "git", "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(workspace, ".gitignore"), "needle-ignore\n");
        File.WriteAllText(Path.Combine(workspace, "needle-ignore"), "");
        File.WriteAllText(Path.Combine(workspace, "needle"), "");
        for (var i = 0; i < 105; i++)
            File.WriteAllText(Path.Combine(workspace, $"needle-{i:D3}"), "");

        var track = await CreateTrackAsync(cluster, workspace);
        var client = cluster.AuthenticatedClient;
        var route = $"/api/v1/tracks/{track.Id}/files/search?q=needle";
        var defaultResults = await client.GetFromJsonAsync<WorkspaceFileSearchResultDto>(route, PergaJsonDefaults.ApiOptions);
        Assert.Equal(50, defaultResults!.Paths.Count);
        Assert.True(defaultResults.Truncated);
        Assert.Equal("needle", defaultResults.Paths[0]);
        var explicitLimit = await client.GetFromJsonAsync<WorkspaceFileSearchResultDto>(route + "&limit=100", PergaJsonDefaults.ApiOptions);
        Assert.Equal(100, explicitLimit!.Paths.Count);
        Assert.True(explicitLimit.Truncated);
        var index = await client.GetFromJsonAsync<WorkspaceFileSearchResultDto>(
            $"/api/v1/tracks/{track.Id}/files/search?q=&limit=20000", PergaJsonDefaults.ApiOptions);
        Assert.Equal(107, index!.Paths.Count);
        Assert.False(index.Truncated);
        Assert.DoesNotContain("needle-ignore", index.Paths);
        Assert.Equal(WorkspaceFileSearch.ScanLimit, WorkspaceFileSearchResultDto.MaxLimit);
        var capped = WorkspaceFileSearch.Rank(Enumerable.Range(0, WorkspaceFileSearch.ScanLimit + 1)
            .Select(i => $"file-{i:D5}"), false, null, int.MaxValue);
        Assert.Equal(WorkspaceFileSearchResultDto.MaxLimit, capped.Paths.Count);
        Assert.True(capped.Truncated);
        var noResults = await client.GetFromJsonAsync<WorkspaceFileSearchResultDto>(
            $"/api/v1/tracks/{track.Id}/files/search?q=zzz", PergaJsonDefaults.ApiOptions);
        Assert.Empty(noResults!.Paths);
        Assert.False(noResults.Truncated);
        using var missing = await client.GetAsync("/api/v1/tracks/missing/files/search");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task FileOpenMcpPublishesTransientWebSocketEventAndReturnsStructuredErrors()
    {
        await using var cluster = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var root = Directory.CreateDirectory(Path.Combine(cluster.DataDir, "viewer-workspace")).FullName;
        var outside = Path.Combine(cluster.DataDir, "notes.txt");
        await File.WriteAllTextAsync(outside, "lines", ct);
        var track = await CreateTrackAsync(cluster, root);
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        var lead = await kernel.OpenMcpCallerAsync(track.LeadSessionId!, ct);
        var store = cluster.Services.GetRequiredService<ITrackStore>();
        var tip = await store.GetEventTipIdAsync(ct);
        var query = $"?trackId={track.Id}&since={tip}";
        using var socket = await cluster.ConnectEventsWebSocketAsync(query, ct);
        using var hello = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, ct);
        Assert.Equal("_replay_complete", hello.RootElement.GetProperty("kind").GetString());

        foreach (var (args, expectedError) in new (object, string)[]
        {
            (new { path = "." }, "invalid_argument"),
            (new { path = "missing.txt" }, "not_found"),
            (new { path = "../notes.txt", endLine = 2 }, "invalid_argument"),
            (new { path = "../notes.txt", line = 5, endLine = 4 }, "invalid_argument")
        })
        {
            var (text, error) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
                cluster, "/mcp/kernel", lead.Token, KernelMcpToolNames.FileOpen, args, ct);
            Assert.True(error, text);
            using var failure = JsonDocument.Parse(text);
            Assert.Equal(expectedError, failure.RootElement.GetProperty("code").GetString());
        }

        var (result, isError) = await McpKernelAndVcsIntegrationTests.CallToolAsync(
            cluster, "/mcp/kernel", lead.Token, KernelMcpToolNames.FileOpen,
            new { path = "../notes.txt", line = 2, endLine = 4 }, ct);
        Assert.False(isError, result);
        var expectedPath = WorkspaceFileReader.ResolveCanonicalPath(outside);
        Assert.Equal(new WorkspaceFileOpenResultDto(expectedPath, 2, 4, true),
            JsonSerializer.Deserialize<WorkspaceFileOpenResultDto>(result, PergaJsonDefaults.ApiOptions));

        using var frame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, ct);
        var opened = frame.RootElement.Deserialize<EventEnvelopeDto>(PergaJsonDefaults.ApiOptions);
        Assert.Equal(KernelEventKinds.WorkspaceFileOpenRequested, opened!.Kind);
        Assert.Equal(track.AreaId, opened.AreaId);
        Assert.Equal(track.Id, opened.TrackId);
        Assert.Null(opened.EventId);
        Assert.Equal(new WorkspaceFileOpenRequestedDto(track.Id, lead.SessionId, expectedPath, 2, 4),
            opened.Payload.Deserialize<WorkspaceFileOpenRequestedDto>(PergaJsonDefaults.ApiOptions));
        Assert.Equal(tip, await store.GetEventTipIdAsync(ct));
        Assert.Empty(await store.ListEventsSinceAsync(tip, track.Id, ct: ct));

        using var reconnected = await cluster.ConnectEventsWebSocketAsync(query, ct);
        using var replay = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(reconnected, ct);
        Assert.Equal("_replay_complete", replay.RootElement.GetProperty("kind").GetString());
    }

    private static async Task<TrackDto> CreateTrackAsync(PergaHostTestCluster cluster, string workspace, string? targetId = null)
    {
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Workspace files", DefaultCwd: workspace, ExecutionTargetId: targetId));
        var track = (await kernel.CreateTrackAsync(
            new CreateTrackRequest(area.Id, Cwd: workspace, WorkspaceDriver: "direct"))).Track;
        await kernel.WaitForTrackIdleAsync(track.Id);
        return track;
    }

    private static async Task AssertFileErrorAsync(
        IExecutionFileSystem files, string root, string path, KernelErrorCode code, CancellationToken ct)
    {
        Assert.Equal(code, (await Assert.ThrowsAsync<KernelException>(() =>
            files.BrowseOrReadWorkspaceFileAsync(root, path, 16, ct))).Code);
        Assert.Equal(code, (await Assert.ThrowsAsync<KernelException>(() =>
            files.OpenWorkspaceRawFileAsync(root, path, ct))).Code);
        Assert.Equal(code, (await Assert.ThrowsAsync<KernelException>(() =>
            files.ValidateWorkspaceFileAsync(root, path, ct))).Code);
    }

    private static async Task RunAsync(string workingDirectory, string command, params string[] arguments)
    {
        var start = new ProcessStartInfo(command) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    // Exercise the real target filesystem through MCP while keeping agent turns on the test runtime.
    private sealed class FileTestEnvironment(
        IExecutionEnvironment local, ExecutionTargetDto descriptor, string root, IExecutionFileSystem files)
        : IExecutionEnvironment
    {
        public ExecutionTargetDto Descriptor { get; } = descriptor;
        public string WorkspacesRoot { get; } = root;
        public bool IsLocal => false;
        public IAgentRuntime AgentRuntime => local.AgentRuntime;
        public IShellCommandRunner Shell => local.Shell;
        public IVcsService Vcs => local.Vcs;
        public IGitHubForgeService? Forge => local.Forge;
        public IWorkspaceDriverRegistry WorkspaceDrivers => local.WorkspaceDrivers;
        public IExecutionFileSystem Files { get; } = files;
        public ITerminalBridgeClient Terminals => local.Terminals;
        public Task<string> ResolveMcpBaseUriAsync(string hostMcpBaseUri, CancellationToken ct = default) =>
            local.ResolveMcpBaseUriAsync(hostMcpBaseUri, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FileAdapterFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("perga-files-").FullName;
        private readonly SshTargetConfig? _config;
        public string? TargetId => _config?.TargetId;
        public string Workspace { get; }
        public string Home { get; }
        public IExecutionFileSystem Files { get; }

        public FileAdapterFixture(bool ssh)
        {
            Workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
            Home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
            Files = LocalExecutionFileSystem.Instance;
            if (!ssh || OperatingSystem.IsWindows()) return;

            var executable = Path.Combine(_root, "ssh");
            File.WriteAllText(executable, $"""
                #!/bin/sh
                export HOME={SshCommandBuilder.QuotePosix(Home)}
                for arg do last=$arg; done
                exec sh -c "$last"
                """ + "\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _config = new SshTargetConfig("file-test", new ExecutionTargetOptions
            {
                User = "test", WorkspacesRoot = _root, SshExecutablePath = executable
            });
            Files = new SshExecutionFileSystem(_config);
        }

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
            if (_config is not null && Directory.Exists(_config.ControlDir))
                Directory.Delete(_config.ControlDir, recursive: true);
        }
    }
}
