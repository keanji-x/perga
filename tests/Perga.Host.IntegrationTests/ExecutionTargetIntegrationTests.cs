using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.Execution;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests;

public sealed class ExecutionTargetIntegrationTests
{
    [Fact]
    public void SshCommandBuilder_AndTargetConfig_EnforceQuotingAllowlistAndValidation()
    {
        Assert.Equal("''", SshCommandBuilder.QuotePosix(""));
        Assert.Equal("'hello world'", SshCommandBuilder.QuotePosix("hello world"));
        Assert.Equal("'a'\\''b$(rm -rf /)'", SshCommandBuilder.QuotePosix("a'b$(rm -rf /)"));

        Assert.Throws<ArgumentException>(() => new SshTargetConfig("local", new ExecutionTargetOptions { User = "jasper" }));
        Assert.Throws<ArgumentException>(() => new SshTargetConfig("jasper", new ExecutionTargetOptions { User = "" }));

        var cfg = new SshTargetConfig("jasper", new ExecutionTargetOptions
        {
            Host = "127.0.0.1",
            Port = 2222,
            User = "jasper",
            IdentityFile = "/home/kenji/.ssh/id_jasper",
            WorkspacesRoot = "/home/jasper/.perga/workspaces"
        });

        Assert.True(cfg.IsLoopbackHost);
        Assert.Equal("jasper@127.0.0.1", cfg.Destination);
        Assert.True(cfg.ControlPath.Length < 100, $"ControlPath too long for Unix socket sun_path: {cfg.ControlPath}");

        var args = SshCommandBuilder.BuildSshExecArguments(cfg, "id");
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("IdentitiesOnly=yes", args);
        Assert.Contains("-i", args);
        Assert.Contains("/home/kenji/.ssh/id_jasper", args);
        Assert.Contains("-p", args);
        Assert.Contains("2222", args);
        Assert.Contains("jasper@127.0.0.1", args);

        Environment.SetEnvironmentVariable("PERGA_TEST_SECRET_DO_NOT_LEAK", "top-secret-value");
        try
        {
            var psi = SshCommandBuilder.CreateSshStartInfo(cfg, args);
            Assert.False(psi.Environment.ContainsKey("PERGA_TEST_SECRET_DO_NOT_LEAK"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERGA_TEST_SECRET_DO_NOT_LEAK", null);
        }
    }

    [Fact]
    public async Task AreaBoundExecutionTarget_RoutesFullPipeline_AndNeverFallsBackWhenUnconfigured()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-exec-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var jasperWorkspacesRoot = Path.Combine(tempDir, "jasper-workspaces");
        var jasperRepoDir = Path.Combine(tempDir, "jasper-repo");
        Directory.CreateDirectory(jasperWorkspacesRoot);
        Directory.CreateDirectory(jasperRepoDir);
        await File.WriteAllTextAsync(Path.Combine(jasperRepoDir, "hello.txt"), "hello from jasper");

        var localRuntime = new ScriptableAgentRuntime
        {
            ProviderStatuses =
            [
                new("codex", Available: true, Version: "local-1.0", Reason: null, Models: ["local-gpt"])
            ]
        };
        var jasperRuntime = new ScriptableAgentRuntime
        {
            ProviderStatuses =
            [
                new("claude", Available: true, Version: "jasper-2.0", Reason: null, Models: ["jasper-claude"])
            ]
        };
        var jasperVcs = new RecordingVcsService();
        var jasperTerminals = new RecordingTerminalBridgeClient();
        var jasperFs = new ScopedFakeFileSystem(jasperRepoDir);
        var jasperEnv = new FakeExecutionEnvironment(
            id: "jasper",
            label: "jasper (localhost)",
            workspacesRoot: jasperWorkspacesRoot,
            agentRuntime: jasperRuntime,
            vcs: jasperVcs,
            files: jasperFs,
            terminals: jasperTerminals);

        string areaId;
        string trackId;

        try
        {
            await using (var factory = new IsolatedPergaHostFactory(
                tempDir,
                configureServices: services =>
                {
                    services.RemoveAll<IAgentRuntime>();
                    services.AddSingleton<IAgentRuntime>(localRuntime);
                    services.RemoveAll<IExecutionEnvironmentRegistry>();
                    services.AddSingleton<IExecutionEnvironmentRegistry>(sp =>
                    {
                        var paths = sp.GetRequiredService<ResolvedHostStoragePaths>();
                        var localEnv = new LocalExecutionEnvironment(
                            workspacesRoot: paths.WorkspacesRootDir,
                            agentRuntime: localRuntime,
                            shell: new ProcessShellCommandRunner(),
                            vcs: sp.GetRequiredService<IVcsService>(),
                            workspaceDrivers: sp.GetRequiredService<IWorkspaceDriverRegistry>(),
                            terminals: sp.GetRequiredService<ITerminalBridgeClient>(),
                            forge: sp.GetService<IGitHubForgeService>());
                        return new ExecutionEnvironmentRegistry([localEnv, jasperEnv]);
                    });
                }))
            {
                using var client = factory.CreateClient();

                // 1. Bootstrap reports both local and jasper execution targets.
                var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/v1/bootstrap", PergaJsonDefaults.ApiOptions);
                var targets = bootstrap.GetProperty("executionTargets").EnumerateArray().ToArray();
                Assert.Contains(targets, t => t.GetProperty("id").GetString() == "local");
                Assert.Contains(targets, t => t.GetProperty("id").GetString() == "jasper" && t.GetProperty("kind").GetString() == "ssh");

                // 2. Reject unknown target IDs (strict whitelist).
                var badAreaResp = await client.PostAsJsonAsync("/api/v1/areas", new
                {
                    name = "Invalid Target Area",
                    executionTargetId = "unconfigured-host"
                });
                Assert.Equal(HttpStatusCode.BadRequest, badAreaResp.StatusCode);

                // 3. Create Area bound to jasper.
                var createAreaResp = await client.PostAsJsonAsync("/api/v1/areas", new
                {
                    name = "Jasper Area",
                    defaultCwd = jasperRepoDir,
                    executionTargetId = "jasper"
                });
                Assert.Equal(HttpStatusCode.Created, createAreaResp.StatusCode);
                var createdArea = await createAreaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions);
                Assert.NotNull(createdArea);
                Assert.Equal("jasper", createdArea!.ExecutionTargetId);
                areaId = createdArea.Id;

                // 4. Target-scoped directory listing and provider discovery route to jasper.
                var listDir = await client.GetFromJsonAsync<HostListDirResultDto>(
                    $"/api/v1/fs/listdir?targetId=jasper&path={Uri.EscapeDataString(jasperRepoDir)}",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(listDir);
                Assert.Equal(jasperRepoDir, listDir!.Path);
                Assert.Equal(1, jasperFs.ListDirectoryCalls);

                var jasperProviders = await client.GetFromJsonAsync<ProviderStatus[]>(
                    "/api/v1/targets/jasper/providers",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(jasperProviders);
                Assert.Single(jasperProviders!);
                Assert.Equal("claude", jasperProviders[0].Provider);
                Assert.Equal("jasper-2.0", jasperProviders[0].Version);

                // 5. Workspace options for the jasper Area probe jasper's environment.
                var wsOpts = await client.GetFromJsonAsync<TrackWorkspaceOptionsDto>(
                    $"/api/v1/areas/{areaId}/workspace-options?cwd={Uri.EscapeDataString(jasperRepoDir)}",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(wsOpts);
                Assert.Contains(wsOpts!.Options, o => o.Id == "direct");
                Assert.DoesNotContain(wsOpts.Options, o => o.Id == OverlayWorkspaceDriver.Id);

                // 6. Create Track in the jasper Area: inherits executionTargetId = jasper.
                var createTrackResp = await client.PostAsJsonAsync("/api/v1/tracks", new
                {
                    areaId,
                    title = "Jasper Feature",
                    cwd = jasperRepoDir,
                    workspaceOptionId = "direct",
                    provider = "claude"
                });
                Assert.Equal(HttpStatusCode.Created, createTrackResp.StatusCode);
                var snapshot = await createTrackResp.Content.ReadFromJsonAsync<TrackAggregateSnapshot>(PergaJsonDefaults.ApiOptions);
                Assert.NotNull(snapshot);
                Assert.Equal("jasper", snapshot!.Track.ExecutionTargetId);
                trackId = snapshot.Track.Id;

                // 7. Workspace files & raw files route to jasper's IExecutionFileSystem.
                var fileRes = await client.GetFromJsonAsync<WorkspaceFileResultDto>(
                    $"/api/v1/tracks/{trackId}/files?path=hello.txt",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(fileRes);
                Assert.Equal("hello from jasper", fileRes!.Content);
                Assert.True(jasperFs.BrowseCalls > 0);

                var rawResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files/raw?path=hello.txt");
                Assert.Equal(HttpStatusCode.OK, rawResp.StatusCode);
                Assert.Equal("hello from jasper", await rawResp.Content.ReadAsStringAsync());

                // 8. VCS diff routes to jasper's IVcsService.
                var diffRes = await client.GetFromJsonAsync<DiffSummary>(
                    $"/api/v1/tracks/{trackId}/vcs-diff",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(diffRes);
                Assert.Equal("jasper-diff", diffRes!.DiffText);
                Assert.True(jasperVcs.DiffCalls > 0);

                // 9. Lead session turn executes on jasperRuntime, NOT localRuntime.
                var leadSessionId = snapshot.Track.LeadSessionId!;
                using var sendReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
                {
                    Content = JsonContent.Create(new { content = "hello jasper agent" })
                };
                sendReq.Headers.Add("Idempotency-Key", "msg-jasper-1");
                var sendResp = await client.SendAsync(sendReq);
                Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);

                var kernel = factory.Services.GetRequiredService<IPergaKernel>();
                await kernel.WaitForTrackIdleAsync(trackId);
                Assert.NotEmpty(jasperRuntime.ExecutedTurns);
                Assert.Empty(localRuntime.ExecutedTurns);

                // 10. Terminal open & WebSocket client resolution route to jasper's ITerminalBridgeClient.
                var termResp = await client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/terminals", new { title = "jasper-sh" });
                Assert.Equal(HttpStatusCode.Created, termResp.StatusCode);
                var termDto = await termResp.Content.ReadFromJsonAsync<TerminalDto>(PergaJsonDefaults.ApiOptions);
                Assert.NotNull(termDto);
                Assert.Equal(1, jasperTerminals.EnsureCalls);
                var resolvedTermClient = await kernel.ResolveTerminalClientAsync(termDto!.Id);
                Assert.Same(jasperTerminals, resolvedTermClient);
            }

            // 11. Restart WITHOUT jasper configured: MUST fail with precondition_failed (409), NEVER silently fall back to local!
            await using (var restartedFactory = new IsolatedPergaHostFactory(
                tempDir,
                configureServices: services =>
                {
                    services.RemoveAll<IAgentRuntime>();
                    services.AddSingleton<IAgentRuntime>(localRuntime);
                }))
            {
                using var restartedClient = restartedFactory.CreateClient();

                var filesWhenMissing = await restartedClient.GetAsync($"/api/v1/tracks/{trackId}/files");
                Assert.Equal(HttpStatusCode.Conflict, filesWhenMissing.StatusCode);
                var filesErr = await filesWhenMissing.Content.ReadFromJsonAsync<JsonElement>(PergaJsonDefaults.ApiOptions);
                Assert.Equal("precondition_failed", filesErr.GetProperty("code").GetString());

                var wsOptsWhenMissing = await restartedClient.GetAsync($"/api/v1/areas/{areaId}/workspace-options");
                Assert.Equal(HttpStatusCode.Conflict, wsOptsWhenMissing.StatusCode);
                var wsErr = await wsOptsWhenMissing.Content.ReadFromJsonAsync<JsonElement>(PergaJsonDefaults.ApiOptions);
                Assert.Equal("precondition_failed", wsErr.GetProperty("code").GetString());
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class FakeExecutionEnvironment : IExecutionEnvironment
    {
        public FakeExecutionEnvironment(
            string id,
            string label,
            string workspacesRoot,
            IAgentRuntime agentRuntime,
            IVcsService vcs,
            IExecutionFileSystem files,
            ITerminalBridgeClient terminals)
        {
            Descriptor = new ExecutionTargetDto(id, label, "ssh", IsDefault: false);
            WorkspacesRoot = workspacesRoot;
            AgentRuntime = agentRuntime;
            Shell = new ProcessShellCommandRunner();
            Vcs = vcs;
            Files = files;
            Terminals = terminals;
            WorkspaceDrivers = new WorkspaceDriverRegistry([new DirectWorkspaceDriver()]);
        }

        public ExecutionTargetDto Descriptor { get; }
        public string WorkspacesRoot { get; }
        public bool IsLocal => false;
        public IAgentRuntime AgentRuntime { get; }
        public IShellCommandRunner Shell { get; }
        public IVcsService Vcs { get; }
        public IGitHubForgeService? Forge => null;
        public IWorkspaceDriverRegistry WorkspaceDrivers { get; }
        public IExecutionFileSystem Files { get; }
        public ITerminalBridgeClient Terminals { get; }

        public Task<string> ResolveMcpBaseUriAsync(string hostMcpBaseUri, CancellationToken ct = default) =>
            Task.FromResult(hostMcpBaseUri);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScopedFakeFileSystem(string rootDir) : IExecutionFileSystem
    {
        public int ListDirectoryCalls { get; private set; }
        public int BrowseCalls { get; private set; }

        public Task<HostListDirResultDto> ListDirectoryAsync(string? path, CancellationToken ct = default)
        {
            ListDirectoryCalls++;
            return LocalExecutionFileSystem.Instance.ListDirectoryAsync(string.IsNullOrWhiteSpace(path) ? rootDir : path, ct);
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.DirectoryExistsAsync(path, ct);

        public Task<string> ResolveCanonicalPathAsync(string path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.ResolveCanonicalPathAsync(path, ct);

        public Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(
            string workspaceRoot, string? relativePath, int maxBytes, CancellationToken ct = default)
        {
            BrowseCalls++;
            return LocalExecutionFileSystem.Instance.BrowseOrReadWorkspaceFileAsync(workspaceRoot, relativePath, maxBytes, ct);
        }

        public Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(
            string workspaceRoot, string relativePath, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.OpenWorkspaceRawFileAsync(workspaceRoot, relativePath, ct);
    }

    private sealed class RecordingVcsService : IVcsService
    {
        public int DiffCalls { get; private set; }

        public Task<VcsBackendKind> DetectAsync(string path, CancellationToken ct) =>
            Task.FromResult(VcsBackendKind.PlainDirectory);

        public IReadOnlyList<VcsBackendKind> ListWorkspaceBackends(string path) =>
            [VcsBackendKind.PlainDirectory];

        public Task<IsolatedWorkspace> CreateWorkspaceAsync(string repoPath, string workspaceName, string targetDir, string? baseRev = null, CancellationToken ct = default, VcsBackendKind? backend = null) =>
            Task.FromResult(new IsolatedWorkspace(VcsBackendKind.PlainDirectory, repoPath, workspaceName, targetDir, baseRev));

        public Task RemoveWorkspaceAsync(IsolatedWorkspace workspace, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RevisionSnapshot> SnapshotWorkingCopyAsync(string workspacePath, string? commitMessageIfGit = null, CancellationToken ct = default) =>
            Task.FromResult(new RevisionSnapshot(VcsBackendKind.PlainDirectory, "main", "0000000", null, false, []));

        public Task<DiffSummary> DiffAsync(string workspacePath, string? fromRev = null, string? toRev = null, CancellationToken ct = default)
        {
            DiffCalls++;
            return Task.FromResult(new DiffSummary("jasper-diff", false, ["hello.txt"]));
        }

        public Task<DiffSummary> DiffDetailedAsync(string workspacePath, string? fromRev = null, string? toRev = null, string? filterPath = null, CancellationToken ct = default, bool searchAncestors = true)
        {
            DiffCalls++;
            return Task.FromResult(new DiffSummary("jasper-diff", false, ["hello.txt"]));
        }

        public Task RestoreOperationAsync(string workspacePath, string opId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RevisionSnapshot> RebaseOntoUpstreamAsync(string workspacePath, string upstreamBranch = "main", CancellationToken ct = default) =>
            SnapshotWorkingCopyAsync(workspacePath, null, ct);

        public Task<PushResult> PushBookmarkAsync(string workspacePath, string bookmarkOrBranch, string? targetRev = null, CancellationToken ct = default) =>
            Task.FromResult(new PushResult(bookmarkOrBranch, "origin", "0000000"));
    }

    private sealed class RecordingTerminalBridgeClient : ITerminalBridgeClient
    {
        public int EnsureCalls { get; private set; }

        public Task<TermEnsureResult> EnsureAsync(TermEnsureRequest req, CancellationToken ct)
        {
            EnsureCalls++;
            return Task.FromResult(new TermEnsureResult(req.TerminalId, Pid: 9001, Created: true));
        }

        public Task<TermObserveResult> ObserveAsync(TermObserveRequest req, CancellationToken ct) =>
            Task.FromResult(new TermObserveResult(
                Revision: 1,
                Frame: new TerminalFrame(80, 24, new TerminalCursor(0, 0, true), false, 0, 0, 0, ["jasper$"]),
                Exited: false,
                ExitCode: null));

        public Task<TermActionResult> SendActionAsync(TermActionRequest req, CancellationToken ct) =>
            Task.FromResult(new TermActionResult(AckRevision: 2));

        public Task ResizeAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct) => Task.CompletedTask;
        public Task KillAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default) => Task.CompletedTask;
        public Uri GetWebSocketRelayUri(string terminalId) => new($"ws://jasper-uds/ws/{terminalId}");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
