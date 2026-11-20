using System.Diagnostics;
using Perga.AgentKit;
using Perga.AgentKit.Internal;
using Xunit;

namespace Perga.AgentKit.Tests;

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
    public async Task FailedOpenCanRetryShutdownOfTheSameRealSubprocess()
    {
        var node = OarNodeResolver.ResolveNode24Executable(null, null);
        var script = Path.Combine(_tempDir, "hold-open.mjs");
        File.WriteAllText(script, "setInterval(() => {}, 1000);");
        Process? child = null;
        var attempts = 0;
        try
        {
            var error = await Assert.ThrowsAsync<ProviderOpenShutdownUnconfirmedException>(() =>
                OarAgentSession.OpenAsync(new(node, [script], _workspaceDir, new Dictionary<string, string>()),
                    new("codex", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>()),
                    "codex", null, TimeSpan.FromMilliseconds(100), null, CancellationToken.None, process =>
                    {
                        if (Interlocked.Increment(ref attempts) == 1)
                        {
                            child = Process.GetProcessById(process.Id);
                            throw new System.ComponentModel.Win32Exception("Scripted first kill failure");
                        }
                        process.Kill(entireProcessTree: true);
                    }));
            Assert.False(error.Session.ShutdownConfirmed);
            Assert.False(child!.HasExited);

            await Task.WhenAll(error.Session.DisposeAsync().AsTask(), error.Session.DisposeAsync().AsTask());

            Assert.True(error.Session.ShutdownConfirmed);
            Assert.True(child!.HasExited);
            Assert.Equal(2, attempts);
        }
        finally
        {
            if (child is not null)
            {
                using (child)
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task ShutdownLeavesBridgeOutputOpenWhileItHandlesEndOfInput()
    {
        var node = OarNodeResolver.ResolveNode24Executable(null, null);
        var script = Path.Combine(_tempDir, "shutdown-output.mjs");
        var sentinel = Path.Combine(_tempDir, "provider-closed");
        File.WriteAllText(script, """
            import { writeFileSync } from 'node:fs';
            process.stdin.resume();
            process.stdin.on('end', () => {
              process.stdout.write('final response\n', error => {
                if (!error) writeFileSync(process.argv[2], 'closed');
              });
            });
            console.log('ready');
            """);
        await using var child = StdioSubprocess.Start(new(node, [script, sentinel], _workspaceDir, new Dictionary<string, string>()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("ready", await child.ReadStdoutLineAsync(deadline.Token));

        await child.DisposeAsync();

        Assert.True(child.ShutdownConfirmed);
        Assert.True(File.Exists(sentinel));
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

            await using var session = await runtime.OpenSessionAsync(
                new HarnessLaunchSpec(provider, null, _workspaceDir, null, "Track instructions", mcpServers),
                CancellationToken.None);

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
            await using var defaultSession = await runtime.OpenSessionAsync(
                new HarnessLaunchSpec(provider, null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: defaultMode),
                CancellationToken.None);
            Assert.NotNull(defaultSession.ConfigurationState);
        }

        // 2. Unsupported interactive permission modes are rejected instead of falsely acknowledged.
        var askEx = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(
            new HarnessLaunchSpec("codex", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: "ask"),
            CancellationToken.None));
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(askEx.Outcome);

        var claudeDefaultEx = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(
            new HarnessLaunchSpec("claude", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: "default"),
            CancellationToken.None));
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(claudeDefaultEx.Outcome);

        var claudeAutoEx = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(
            new HarnessLaunchSpec("claude", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: "auto"),
            CancellationToken.None));
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(claudeAutoEx.Outcome);

        // 2b. Missing native resume session maps OAR SessionNotFoundError to ProviderSessionNotFoundException.
        await Assert.ThrowsAsync<ProviderSessionNotFoundException>(() => runtime.OpenSessionAsync(
            new HarnessLaunchSpec("codex", null, _workspaceDir, "missing-thread-id", null, new Dictionary<string, McpServerConfig>(), PermissionMode: "never"),
            CancellationToken.None));

        // 3. Claude supported modes propagate --permission-mode or bypassPermissions accurately.
        foreach (var claudeMode in new[] { "dontAsk", "acceptEdits", "plan", "bypassPermissions" })
        {
            await using var claudeSession = await runtime.OpenSessionAsync(
                new HarnessLaunchSpec("claude", null, _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), PermissionMode: claudeMode),
                CancellationToken.None);
            Assert.Equal(claudeMode, claudeSession.ConfigurationState?.Selection.Permission.ValueOrNull);
        }

        // 4. Codex "never" propagates OAR_CODEX_SANDBOX=workspace-write, rejects mismatched per-turn model/effort,
        //    tracks mid-turn model_changed, and poisons on canceled compact while still returning RequiresReopen to ConfigureAsync.
        await using var session = await runtime.OpenSessionAsync(
            new HarnessLaunchSpec("codex", "gpt-5.4", _workspaceDir, null, null, new Dictionary<string, McpServerConfig>(), ReasoningEffort: "high", PermissionMode: "never"),
            CancellationToken.None);

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
  log({ method: req.method, params: req.params });

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
