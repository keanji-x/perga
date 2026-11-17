namespace Perga.AgentKit.Tests;

public sealed class RuntimeLifecycleAndResilienceTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task DefaultAgentRuntime_ProbeProvidersAsync_ReturnsAllFourCanonicalSurfacesWithoutThrowing()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromSeconds(2)
        });

        var statuses = await runtime.ProbeProvidersAsync(CancellationToken.None);
        Assert.Equal(4, statuses.Count);
        Assert.Equal(["codex", "claude", "acp:antigravity", "acp:opencode"], statuses.Select(s => s.Provider).ToArray());
    }

    [Fact]
    public async Task OpenSessionAsync_ValidatesUnsupportedProviderAndMissingCwd()
    {
        await using var runtime = new AgentRuntime();
        var emptyMcp = new Dictionary<string, McpServerConfig>();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            runtime.OpenSessionAsync(
                new HarnessLaunchSpec("unsupported-provider", null, _fixture.WorkspaceDir, null, null, emptyMcp),
                CancellationToken.None));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            runtime.OpenSessionAsync(
                new HarnessLaunchSpec("codex", null, Path.Combine(_fixture.RootDir, "does-not-exist"), null, null, emptyMcp),
                CancellationToken.None));
    }

    [Fact]
    public async Task AgentRuntimeDisposeAsync_CleansUpTrackedSessionsAndClaudeTempMcpConfig()
    {
        string tempMcpPath;
        await using (var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["claude"] = new(_fixture.MockClaudePath)
            }
        }))
        {
            var session = await runtime.OpenSessionAsync(
                new HarnessLaunchSpec("claude", null, _fixture.WorkspaceDir, null, null, MockCliFixture.CreateSampleMcpServers()),
                CancellationToken.None);

            var claudeSession = Assert.IsType<ClaudeAgentSession>(session);
            tempMcpPath = claudeSession.McpConfigFilePath;
            Assert.True(File.Exists(tempMcpPath));
            // Intentionally do not dispose session directly; AgentRuntime.DisposeAsync must clean it up.
        }

        Assert.False(File.Exists(tempMcpPath));
    }

    [Fact]
    public async Task AbruptSubprocessCrashMidTurn_EmitsTurnErrorWithStderrWithoutHanging()
    {
        var crashClaudePath = _fixture.WriteExecutable(
            "crash_claude.py",
            """
            #!/usr/bin/env python3
            import json, sys
            for raw in sys.stdin:
                if not raw.strip():
                    continue
                sys.stdout.write(json.dumps({"type": "system", "subtype": "init", "session_id": "crash_sess"}) + "\n")
                sys.stdout.flush()
                sys.stderr.write("boom: simulated segmentation/crash in claude child\n")
                sys.stderr.flush()
                sys.exit(139)
            """);

        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["claude"] = new(crashClaudePath)
            }
        });

        await using var session = await runtime.OpenSessionAsync(
            new HarnessLaunchSpec("claude", null, _fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>()),
            CancellationToken.None);

        var events = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("crash-now", "corr_crash"), CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e is UnifiedAgentEvent.ThreadBound { NativeThreadId: "crash_sess" });
        var turnErr = Assert.Single(events.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Equal("claude_disconnected", turnErr.Code);
    }
}
