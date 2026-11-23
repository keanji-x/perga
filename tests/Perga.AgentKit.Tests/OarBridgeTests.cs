using System.Text.Json;
using Perga.AgentKit;
using Perga.AgentKit.Internal;
using Xunit;

namespace Perga.AgentKit.Tests;

[Collection("OAR process environment")]
public sealed class OarBridgeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceDir;
    private readonly string _mockBridgePath;
    private readonly string _logPath;

    public OarBridgeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"perga-oar-tests-{Guid.NewGuid():N}");
        _workspaceDir = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceDir);
        _logPath = Path.Combine(_tempDir, "bridge-log.jsonl");
        _mockBridgePath = Path.Combine(_tempDir, "mock-oar-bridge.mjs");
        File.WriteAllText(_mockBridgePath, MockBridgeScript);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RuntimeActivityReplaysBeforeAttachAndForwardsDuringAndAfterTurns()
    {
        File.WriteAllText(_mockBridgePath, MockBridgeScript
            .Replace("if (req.method === 'session/open') {", """
                if (req.method === 'session/open') {
                  send({jsonrpc:'2.0',method:'oar/event',params:{kind:'task_started',taskId:'background',taskType:'shell',background:true}});
                  send({jsonrpc:'2.0',method:'oar/event',params:{kind:'agent_status',status:{kind:'idle',activity:{since:1,lastEventAt:2}}}});
                """)
            .Replace("if (req.method === 'session/prompt') {", """
                if (req.method === 'session/prompt') {
                  send({jsonrpc:'2.0',method:'oar/event',params:{kind:'task_updated',taskId:'background',description:'progress'}});
                  send({jsonrpc:'2.0',method:'oar/event',params:{kind:'agent_status',status:{kind:'running',sinceSeq:3,phase:{tool:'shell',callId:'call',writing:true}}}});
                  send({jsonrpc:'2.0',method:'oar/event',params:{kind:'task_ended',taskId:'background',status:'completed',summary:'done'}});
                  setTimeout(() => send({jsonrpc:'2.0',method:'oar/event',params:{kind:'agent_status',status:{kind:'idle',lastTurnOutcome:{kind:'completed'},activity:{since:4,lastEventAt:5}}}}),100);
                  setTimeout(() => send({jsonrpc:'2.0',method:'oar/event',params:{kind:'exited',code:0}}),200);
                """));
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions { BridgeScriptPath = _mockBridgePath });
        await using var session = runtime.CreateSession(new HarnessLaunchSpec("codex", null, _workspaceDir,
            null, null, new Dictionary<string, McpServerConfig>()));
        await session.InitializeAsync(CancellationToken.None);
        var idle = new TaskCompletionSource<AgentRuntimeStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var live = new System.Collections.Concurrent.ConcurrentQueue<UnifiedAgentEvent>();
        var pending = session.AttachRuntimeActivityHandler(observation =>
        {
            live.Enqueue(observation);
            if (observation is UnifiedAgentEvent.AgentStatusChanged { Status.IdleActivity.Since: 4 } changed)
                idle.TrySetResult(changed.Status);
            if (observation is UnifiedAgentEvent.RuntimeExited) exited.TrySetResult();
        });
        Assert.Contains(pending, observation => observation is UnifiedAgentEvent.TaskStarted { Task.TaskId: "background" });
        Assert.Contains(pending, observation => observation is UnifiedAgentEvent.AgentStatusChanged { Status.IdleActivity.LastEventAt: 2 });
        var streamed = new List<UnifiedAgentEvent>();
        await foreach (var observation in session.StreamTurnAsync(new TurnInput("hello", "runtime-test"), default))
            streamed.Add(observation);
        Assert.Contains(streamed, observation => observation is UnifiedAgentEvent.TaskUpdated);
        Assert.Contains(streamed, observation => observation is UnifiedAgentEvent.TaskEnded { Task.Summary: "done" });
        Assert.Contains(streamed, observation => observation is UnifiedAgentEvent.AgentStatusChanged
            { Status.Phase: "tool", Status.ToolName: "shell", Status.Writing: true });
        Assert.Equal("completed", (await idle.Task.WaitAsync(TimeSpan.FromSeconds(5))).LastTurnOutcome?.Kind);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, live.Count);
        Assert.Empty(session.AttachRuntimeActivityHandler(null));
    }

    [Fact]
    public async Task ExitDuringOpenSuppressesInitialAndSubsequentRuntimeStatus()
    {
        File.WriteAllText(_mockBridgePath, MockBridgeScript
            .Replace("if (req.method === 'session/open') {", """
                if (req.method === 'session/open') {
                  send({jsonrpc:'2.0',method:'oar/event',params:{kind:'exited',code:0}});
                  send({jsonrpc:'2.0',method:'oar/event',params:{kind:'agent_status',status:{kind:'idle'}}});
                """)
            .Replace("supportsNativeSteer: true, supportsNativeCompact: openRuntime", "agentStatus: {kind:'idle'}, supportsNativeSteer: true, supportsNativeCompact: openRuntime"));
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions { BridgeScriptPath = _mockBridgePath });
        await using var session = runtime.CreateSession(new HarnessLaunchSpec("codex", null, _workspaceDir,
            null, null, new Dictionary<string, McpServerConfig>()));
        await session.InitializeAsync(CancellationToken.None);
        Assert.IsType<UnifiedAgentEvent.RuntimeExited>(Assert.Single(session.AttachRuntimeActivityHandler(_ => { })));
    }

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("claude", "claude")]
    [InlineData("acp:antigravity", "antigravity")]
    [InlineData("agy", "antigravity")]
    [InlineData("acp:opencode", "opencode")]
    [InlineData("opencode", "opencode")]
    [InlineData("oar:qwen", "qwen")]
    [InlineData("oar:antigravity", "antigravity")]
    [InlineData("acp:custom-cli", null)]
    public void TryMapProviderToOarRuntimeMapsCanonicalAndOarProviders(string input, string? expected)
    {
        Assert.Equal(expected, OarNodeResolver.TryMapProviderToOarRuntime(input));
    }

    [Theory]
    [InlineData("v24.4.1", 24, 4, 1)]
    [InlineData("v25.0.0-nightly.1", 25, 0, 0)]
    [InlineData("22.22.2", 22, 22, 2)]
    public void ParseNodeVersionParsesSemverStrings(string raw, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), OarNodeResolver.ParseNodeVersion(raw));
    }

    [Fact]
    public async Task AgentRuntimeCoversProbeTurnSteerCompactAndReopenForAllFourProviders()
    {
        var node24 = OarNodeResolver.ResolveNode24Executable(null, null);

        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            NodeExecutablePath = node24,
            BridgeScriptPath = _mockBridgePath,
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new("fake-codex", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["claude"] = new("fake-claude", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["acp:antigravity"] = new("fake-agy", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["acp:opencode"] = new("fake-opencode", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath })
            }
        });

        var statuses = await runtime.ProbeProvidersAsync(forceRefresh: true, CancellationToken.None);
        Assert.Equal(["codex", "claude", "acp:antigravity", "acp:opencode"], statuses.Select(s => s.Provider).ToArray());
        Assert.All(statuses, s => Assert.True(s.Available));
        Assert.True(runtime.CanResumeSession("codex"));
        Assert.False(runtime.CanResumeSession("acp:custom-cli"));

        foreach (var provider in new[] { "codex", "claude", "acp:opencode", "acp:antigravity" })
        {
            var mcpServers = new Dictionary<string, McpServerConfig>
            {
                ["perga"] = new McpServerConfig.HttpMcpServer(
                    "http://127.0.0.1:5000/mcp/kernel",
                    new Dictionary<string, string> { ["Authorization"] = "Bearer pg_test" })
            };

            await using var session = runtime.CreateSession(
                new HarnessLaunchSpec(provider, null, _workspaceDir, null, "Track instructions", mcpServers));
            await session.InitializeAsync(CancellationToken.None);

            Assert.IsType<OarAgentSession>(session);
            Assert.NotNull(session.NativeThreadId);
            Assert.NotNull(session.ConfigurationState);

            Assert.IsType<SessionConfigurationOutcome.Applied>(
                await session.ConfigureAsync(SessionConfiguration.FromNullable(null), CancellationToken.None));
            Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(
                await session.ConfigureAsync(SessionConfiguration.FromNullable("other-model"), CancellationToken.None));

            var events = new List<UnifiedAgentEvent>();
            await foreach (var evt in session.StreamTurnAsync(new TurnInput("hello", "corr-1"), CancellationToken.None))
            {
                events.Add(evt);
            }

            Assert.Contains(events, e => e is UnifiedAgentEvent.ThreadBound);
            Assert.Contains(events, e => e is UnifiedAgentEvent.ThinkingDelta { Delta: "thinking..." });
            Assert.Contains(events, e => e is UnifiedAgentEvent.ToolCallStarted { Name: "perga:status" });
            Assert.Contains(events, e => e is UnifiedAgentEvent.ToolCallCompleted { Output: "tool-done", IsError: false });
            Assert.Contains(events, e => e is UnifiedAgentEvent.TextDelta { Delta: var d } && d.Contains("hello", StringComparison.Ordinal));
            Assert.Contains(events, e => e is UnifiedAgentEvent.TurnCompleted { Usage.ContextWindowTokens: 200000 });

            Assert.Equal(SteerDelivery.Accepted, await session.SteerAsync("corr-1", "focus on tests", CancellationToken.None));
            Assert.True(await session.ResolveNativeCompactSupportAsync(CancellationToken.None));
            var compacted = await session.CompactAsync(CancellationToken.None);
            Assert.Contains("Compacted", compacted.Summary ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(200000, compacted.Usage?.ContextWindowTokens);
        }

        var logLines = await File.ReadAllLinesAsync(_logPath);
        foreach (var rt in new[] { "codex", "claude", "opencode", "antigravity" })
        {
            Assert.Contains(logLines, l => l.Contains($"\"runtime\":\"{rt}\"", StringComparison.Ordinal));
        }
        Assert.Contains(logLines, l => l.Contains("\"method\":\"session/compact\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OarAgentSessionEnforcesPermissionPolicyTurnSelectionAndCompactionPoisoning()
    {
        var node24 = OarNodeResolver.ResolveNode24Executable(null, null);
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            NodeExecutablePath = node24,
            BridgeScriptPath = _mockBridgePath,
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new("fake-codex", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath, ["MOCK_SLOW_COMPACT"] = "1" }),
                ["claude"] = new("fake-claude", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["acp:antigravity"] = new("fake-agy", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["acp:opencode"] = new("fake-opencode", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath })
            }
        });

        // 1. Every provider's catalog default mode opens cleanly through OarAgentSession.
        foreach (var provider in new[] { "codex", "claude", "acp:antigravity", "acp:opencode" })
        {
            var defaultMode = ProviderPermissionModes.GetDefaultMode(provider);
            await using var defaultSession = runtime.CreateSession(
                new HarnessLaunchSpec(provider, null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: defaultMode));
            await defaultSession.InitializeAsync(CancellationToken.None);
            Assert.NotNull(defaultSession.ConfigurationState);
        }

        // 2. Unsupported interactive permission modes are rejected instead of falsely acknowledged.
        var askEx = await Assert.ThrowsAsync<SessionConfigurationException>(async () =>
        {
            await using var rejected = runtime.CreateSession(
                new HarnessLaunchSpec("codex", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: "ask"));
            await rejected.InitializeAsync(CancellationToken.None);
        });
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(askEx.Outcome);

        var claudeDefaultEx = await Assert.ThrowsAsync<SessionConfigurationException>(async () =>
        {
            await using var rejected = runtime.CreateSession(
                new HarnessLaunchSpec("claude", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: "default"));
            await rejected.InitializeAsync(CancellationToken.None);
        });
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(claudeDefaultEx.Outcome);

        var claudeAutoEx = await Assert.ThrowsAsync<SessionConfigurationException>(async () =>
        {
            await using var rejected = runtime.CreateSession(
                new HarnessLaunchSpec("claude", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: "auto"));
            await rejected.InitializeAsync(CancellationToken.None);
        });
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(claudeAutoEx.Outcome);

        // 2b. Missing native resume session maps OAR SessionNotFoundError to ProviderSessionNotFoundException.
        await Assert.ThrowsAsync<ProviderSessionNotFoundException>(async () =>
        {
            await using var rejected = runtime.CreateSession(
                new HarnessLaunchSpec("codex", null, _workspaceDir, "missing-thread-id", null, new Dictionary<string, McpServerConfig>(), PermissionMode: "never"));
            await rejected.InitializeAsync(CancellationToken.None);
        });

        // 3. Claude supported modes propagate --permission-mode or bypassPermissions accurately.
        foreach (var claudeMode in new[] { "dontAsk", "acceptEdits", "plan", "bypassPermissions" })
        {
            await using var claudeSession = runtime.CreateSession(
                new HarnessLaunchSpec("claude", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: claudeMode));
            await claudeSession.InitializeAsync(CancellationToken.None);
            Assert.Equal(claudeMode, claudeSession.ConfigurationState?.Selection.Permission.ValueOrNull);
        }

        // 4. Codex "never" propagates OAR_CODEX_SANDBOX=workspace-write, rejects mismatched per-turn model/effort,
        //    tracks mid-turn model_changed, and poisons on canceled compact while still returning RequiresReopen to ConfigureAsync.
        await using var session = runtime.CreateSession(
            new HarnessLaunchSpec("codex", "gpt-5.4", _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), ReasoningEffort: "high", PermissionMode: "never"));
        await session.InitializeAsync(CancellationToken.None);

        var modelMismatchEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("hi", "c-1", Model: "other-model"), CancellationToken.None))
            modelMismatchEvents.Add(evt);
        var modelErr = Assert.Single(modelMismatchEvents.OfType<UnifiedAgentEvent.TurnError>());
        Assert.True(modelErr.BeforePrompt);
        Assert.Equal("OAR_TURN_MODEL_MISMATCH", modelErr.Code);

        var effortMismatchEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("hi", "c-1b", ReasoningEffort: "low"), CancellationToken.None))
            effortMismatchEvents.Add(evt);
        var effortErr = Assert.Single(effortMismatchEvents.OfType<UnifiedAgentEvent.TurnError>());
        Assert.True(effortErr.BeforePrompt);
        Assert.Equal("OAR_TURN_EFFORT_MISMATCH", effortErr.Code);

        // Mid-turn model_changed bumps ContextUsageRevision on TurnCompleted and causes ConfigureAsync to require reopen.
        var initialRev = session.ContextUsageRevision!.Value;
        var driftEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("trigger-model-change", "c-drift"), CancellationToken.None))
            driftEvents.Add(evt);
        var completedDrift = Assert.Single(driftEvents.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.Equal("gpt-5.4-drifted", session.ConfigurationState?.NativeModel);
        Assert.True(completedDrift.ContextUsageRevision > initialRev);
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(
            await session.ConfigureAsync(SessionConfiguration.FromLaunch(
                new HarnessLaunchSpec("codex", "gpt-5.4", _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), ReasoningEffort: "high", PermissionMode: "never")),
                CancellationToken.None));

        // 5. Canceled compaction disposes/poisons the session so StreamTurnAsync throws ObjectDisposedException,
        //    while ConfigureAsync returns RequiresReopen so TrackActor can reopen transparently on the next turn.
        using var cancelCompact = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.CompactAsync(cancelCompact.Token));
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(
            await session.ConfigureAsync(SessionConfiguration.FromLaunch(
                new HarnessLaunchSpec("codex", "gpt-5.4", _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), ReasoningEffort: "high", PermissionMode: "never")),
                CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in session.StreamTurnAsync(new TurnInput("after-cancel", "c-2"), CancellationToken.None)) { }
        });

        var logLines = await File.ReadAllLinesAsync(_logPath);
        Assert.Contains(logLines, l => l.Contains("\"OAR_CODEX_SANDBOX\":\"workspace-write\"", StringComparison.Ordinal));
        Assert.Contains(logLines, l => l.Contains("\"--permission-mode\",\"dontAsk\"", StringComparison.Ordinal));
        Assert.Contains(logLines, l => l.Contains("\"--permission-mode\",\"acceptEdits\"", StringComparison.Ordinal));
        Assert.Contains(logLines, l => l.Contains("\"--permission-mode\",\"plan\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbientHostSettingsDoNotReachProviderProbeOrSessionWhileDeveloperEnvPassesThrough()
    {
        var previous = new Dictionary<string, string?>();
        var overrides = new Dictionary<string, string?>
        {
            ["Perga__DataDir"] = _tempDir,
            ["PERGA_DB_PATH"] = Path.Combine(_tempDir, "perga.db"),
            ["ASPNETCORE_Perga__DataDir"] = _tempDir,
            ["DOTNET_Perga__DataDir"] = _tempDir,
            ["DOTNET_ENVIRONMENT"] = "Production",
            ["VIRTUAL_ENV"] = "/simulated/venv",
            ["OAR_TEST_CUSTOM_ENV"] = "custom-ok"
        };
        try
        {
            foreach (var (key, value) in overrides)
            {
                previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }

            var mockEnvironment = new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath };
            await using var runtime = new AgentRuntime(new AgentRuntimeOptions
            {
                BridgeScriptPath = _mockBridgePath,
                ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
                {
                    ["codex"] = new("fake-codex", mockEnvironment),
                    ["claude"] = new("fake-claude", mockEnvironment)
                }
            });

            var statuses = await runtime.ProbeProvidersAsync(CancellationToken.None);
            Assert.Contains(statuses, status => status.Provider == "codex" && status.Available);
            await using var session = runtime.CreateSession(
                new HarnessLaunchSpec("codex", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>()));
            await session.InitializeAsync(CancellationToken.None);

            var calls = new List<(string? Method, string? Runtime)>();
            foreach (var line in await File.ReadAllLinesAsync(_logPath))
            {
                using var log = JsonDocument.Parse(line);
                var method = log.RootElement.GetProperty("method").GetString();
                if (method is not ("probe" or "session/open")) continue;
                var runtimeName = log.RootElement.GetProperty("params").GetProperty("runtime").GetString();
                calls.Add((method, runtimeName));
                Assert.True(log.RootElement.GetProperty("hasDeveloperEnv").GetBoolean());
                Assert.False(log.RootElement.GetProperty("hasHostStorage").GetBoolean(),
                    "The bridge inherited Host-reserved settings.");
            }
            Assert.Contains(("probe", "codex"), calls);
            Assert.Contains(("probe", "claude"), calls);
            Assert.Contains(("session/open", "codex"), calls);
        }
        finally
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public async Task OarAgentSessionSupportsImagesForwardsImagesOnPromptAndSteerAndRefusesWhenUnsupported()
    {
        var node24 = OarNodeResolver.ResolveNode24Executable(null, null);
        var imgBridgePath = Path.Combine(_tempDir, "mock-img-bridge.mjs");
        File.WriteAllText(imgBridgePath, MockBridgeScript
            .Replace(
                "supportsManualCompaction: true, models:",
                "supportsManualCompaction: true, supportsImages: rt === 'codex' ? true : rt === 'qwen' ? false : null, models:")
            .Replace(
                "supportsSessionResume: true, contextUsage:",
                "supportsSessionResume: true, supportsImages: openRuntime === 'codex', contextUsage:")
            .Replace(
                "if (req.method === 'session/prompt') {",
                """
                if (req.method === 'session/prompt') {
                  if (openRuntime !== 'codex' && Array.isArray(req.params.images) && req.params.images.length > 0) {
                    send({ jsonrpc: '2.0', id: req.id, result: { status: 'failed', code: 'unsupported', message: 'Images not supported by runtime', isRetryable: false, beforePrompt: true } });
                    return;
                  }
                """));

        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            NodeExecutablePath = node24,
            BridgeScriptPath = imgBridgePath,
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new("fake-codex", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["acp:antigravity"] = new("fake-agy", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath }),
                ["oar:qwen"] = new("fake-qwen", Env: new Dictionary<string, string> { ["MOCK_OAR_LOG"] = _logPath })
            }
        });

        var statuses = await runtime.ProbeProvidersAsync(forceRefresh: true, CancellationToken.None);
        Assert.Equal(true, statuses.Single(s => s.Provider == "codex").SupportsImages);
        Assert.Null(statuses.Single(s => s.Provider == "acp:antigravity").SupportsImages);
        Assert.Equal(false, statuses.Single(s => s.Provider == "oar:qwen").SupportsImages);

        Assert.Equal(true, runtime.SupportsImages("codex"));
        Assert.Null(runtime.SupportsImages("acp:antigravity"));
        Assert.Equal(false, runtime.SupportsImages("oar:qwen"));

        var image = new InputImageRef("/tmp/att_0123456789abcdef0123456789abcdef.png", "image/png");

        // Supported session: codex
        await using (var supported = runtime.CreateSession(
            new HarnessLaunchSpec("codex", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>())))
        {
            await supported.InitializeAsync(CancellationToken.None);
            Assert.True(supported.SupportsImages);

            var events = new List<UnifiedAgentEvent>();
            await foreach (var evt in supported.StreamTurnAsync(new TurnInput("", "corr-img", Images: [image]), CancellationToken.None))
            {
                events.Add(evt);
            }
            Assert.Contains(events, e => e is UnifiedAgentEvent.TurnCompleted);

            Assert.Equal(SteerDelivery.Accepted,
                await supported.SteerAsync("corr-img", "", [image], CancellationToken.None));
        }

        // Unsupported session: antigravity handshake returns supportsImages=false
        await using (var unsupported = runtime.CreateSession(
            new HarnessLaunchSpec("acp:antigravity", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>())))
        {
            await unsupported.InitializeAsync(CancellationToken.None);
            Assert.False(unsupported.SupportsImages);

            // Steer with images is declined fail-closed without issuing RPC
            Assert.Equal(SteerDelivery.Declined,
                await unsupported.SteerAsync("corr-no", "", [image], CancellationToken.None));

            // Prompt with images propagates BeforePrompt=true conclusive TurnError
            var events = new List<UnifiedAgentEvent>();
            await foreach (var evt in unsupported.StreamTurnAsync(new TurnInput("look", "corr-no", Images: [image]), CancellationToken.None))
            {
                events.Add(evt);
            }
            var err = Assert.Single(events.OfType<UnifiedAgentEvent.TurnError>());
            Assert.Equal("unsupported", err.Code);
            Assert.True(err.BeforePrompt);
            Assert.True(err.IsConclusive);
        }

        var logLines = await File.ReadAllLinesAsync(_logPath);
        Assert.Contains(logLines, l => l.Contains("\"method\":\"session/prompt\"", StringComparison.Ordinal) &&
                                       l.Contains("att_0123456789abcdef0123456789abcdef.png", StringComparison.Ordinal));
        Assert.Contains(logLines, l => l.Contains("\"method\":\"session/steer\"", StringComparison.Ordinal) &&
                                       l.Contains("att_0123456789abcdef0123456789abcdef.png", StringComparison.Ordinal));
    }

    private const string MockBridgeScript = """
import fs from 'node:fs';
import readline from 'node:readline';

const logPath = process.env.MOCK_OAR_LOG;
const log = (entry) => { if (logPath) fs.appendFileSync(logPath, JSON.stringify(entry) + '\n'); };
const send = (msg) => process.stdout.write(JSON.stringify(msg) + '\n');

let openRuntime = null;
let lastModel = null;
readline.createInterface({ input: process.stdin, crlfDelay: Infinity }).on('line', (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;
  const req = JSON.parse(trimmed);
  const hasHostStorage =
    Object.hasOwn(process.env, "Perga__DataDir") ||
    Object.hasOwn(process.env, "PERGA_DB_PATH") ||
    Object.hasOwn(process.env, "ASPNETCORE_Perga__DataDir") ||
    Object.hasOwn(process.env, "DOTNET_Perga__DataDir") ||
    Object.hasOwn(process.env, "DOTNET_ENVIRONMENT");
  const hasDeveloperEnv =
    process.env.VIRTUAL_ENV === "/simulated/venv" &&
    process.env.OAR_TEST_CUSTOM_ENV === "custom-ok";
  log({ method: req.method, params: req.params, hasHostStorage, hasDeveloperEnv });

  if (req.method === 'probe') {
    const rt = req.params.runtime;
    const modelId = rt === 'codex' ? 'gpt-5.4' : rt === 'claude' ? 'claude-sonnet-4-6' : rt === 'antigravity' ? 'gemini-4-neon' : 'opencode/default';
    send({ jsonrpc: '2.0', id: req.id, result: { runtime: rt, available: true, version: 'mock-1.0.0', reason: null, supportsNativeSteer: true, supportsManualCompaction: true, models: [{ id: modelId, displayName: modelId, description: '', isDefault: true, defaultEffort: 'high', effortLevels: ['low', 'high'] }] } });
    return;
  }
  if (req.method === 'session/open') {
    if (req.params.resume === 'missing-thread-id') {
      send({ jsonrpc: '2.0', id: req.id, error: { code: -32000, message: 'Thread was not found', data: { errorName: 'SessionNotFoundError' } } });
      return;
    }
    openRuntime = req.params.runtime;
    lastModel = req.params.model ?? null;
    send({ jsonrpc: '2.0', id: req.id, result: { sessionId: `${openRuntime}-sess-1`, model: req.params.model ?? `${openRuntime}-default`, effort: req.params.effort ?? 'high', supportsNativeSteer: true, supportsNativeCompact: openRuntime !== 'antigravity', supportsSessionResume: true, contextUsage: { inputTokens: 0, outputTokens: 0, cachedInputTokens: 0, contextWindowTokens: 200000, usedTokens: 0 } } });
    return;
  }
  if (req.method === 'session/prompt') {
    if (req.params.prompt === 'trigger-model-change') {
      send({ jsonrpc: '2.0', method: 'oar/event', params: { kind: 'model_changed', model: 'gpt-5.4-drifted' } });
    }
    send({ jsonrpc: '2.0', method: 'oar/event', params: { kind: 'thinking_delta', delta: 'thinking...' } });
    send({ jsonrpc: '2.0', method: 'oar/event', params: { kind: 'tool_call_started', callId: 'c1', name: 'perga:status', args: { ok: true } } });
    send({ jsonrpc: '2.0', method: 'oar/event', params: { kind: 'tool_call_completed', callId: 'c1', output: 'tool-done', isError: false } });
    send({ jsonrpc: '2.0', method: 'oar/event', params: { kind: 'text_delta', delta: `Reply from ${openRuntime}: ${req.params.prompt}` } });
    send({ jsonrpc: '2.0', id: req.id, result: { status: 'completed', usage: { inputTokens: 100, outputTokens: 50, cachedInputTokens: 10, contextWindowTokens: 200000, usedTokens: 150 } } });
    return;
  }
  if (req.method === 'session/steer') { send({ jsonrpc: '2.0', id: req.id, result: { delivery: 'accepted' } }); return; }
  if (req.method === 'session/abort') { send({ jsonrpc: '2.0', id: req.id, result: { accepted: true } }); return; }
  if (req.method === 'session/compact_support') { send({ jsonrpc: '2.0', id: req.id, result: { supported: true, mode: openRuntime === 'antigravity' ? 'slash_command' : 'native' } }); return; }
  if (req.method === 'session/compact') {
    if (process.env.MOCK_SLOW_COMPACT === '1') {
      setTimeout(() => send({ jsonrpc: '2.0', id: req.id, result: { completed: true, mode: 'native', summary: 'Late', usage: null } }), 5000);
      return;
    }
    send({ jsonrpc: '2.0', id: req.id, result: { completed: true, mode: openRuntime === 'antigravity' ? 'slash_command' : 'native', summary: `Compacted (${openRuntime})`, usage: { inputTokens: 20, outputTokens: 10, cachedInputTokens: 0, contextWindowTokens: 200000, usedTokens: 30 } } }); return;
  }
  if (req.method === 'session/close') { send({ jsonrpc: '2.0', id: req.id, result: { closed: true } }); return; }
  send({ jsonrpc: '2.0', id: req.id, error: { code: -32601, message: 'unknown method' } });
});
""";
}

[CollectionDefinition("OAR process environment", DisableParallelization = true)]
public sealed class OarProcessEnvironmentCollection;
