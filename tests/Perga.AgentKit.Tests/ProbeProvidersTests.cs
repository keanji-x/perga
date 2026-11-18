namespace Perga.AgentKit.Tests;

public sealed class ProbeProvidersTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("acp:antigravity")]
    [InlineData("acp:opencode")]
    [InlineData("acp:custom")]
    public async Task AcpDiscoveryReturnsAdvertisedModels(string provider)
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                [provider] = new(_fixture.MockAcpPath)
            }
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), s => s.Provider == provider);
        Assert.True(status.Available);
        Assert.Equal(["mock-model-a", "mock-model-b"], status.Models);
    }

    [Theory]
    [InlineData("{\"configOptions\":[{\"category\":\"model\",\"id\":\"choice\",\"options\":[{\"value\":\"model-1\"},{\"group\":\"Other\",\"options\":[{\"value\":\"model-2\"}]}]}]}", false)]
    [InlineData("{}", true)]
    public async Task AcpDiscoveryReadsConfigOptionsAndHonorsOverrides(string advertised, bool explicitOverride)
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:custom"] = new(_fixture.MockAcpPath,
                    Env: new Dictionary<string, string> { ["MOCK_ACP_MODELS"] = advertised },
                    Models: explicitOverride ? ["override"] : null)
            }
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), s => s.Provider == "acp:custom");
        Assert.True(status.Available);
        Assert.Equal(explicitOverride ? ["override"] : new[] { "model-1", "model-2" }, status.Models);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitModelsIncludingEmptyBypassAcpProbe(bool empty)
    {
        var log = Path.Combine(_fixture.RootDir, "discovery.jsonl");
        string[] models = empty ? [] : ["override"];
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:custom"] = new(_fixture.MockAcpPath,
                    Env: new Dictionary<string, string> { ["MOCK_ACP_LOG"] = log }, Models: models)
            }
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), s => s.Provider == "acp:custom");
        Assert.Equal(models, status.Models);
        Assert.False(File.Exists(log));
    }

    [Fact]
    public async Task FailedAcpHandshakeKeepsProviderAvailableWithoutInventingModels()
    {
        var path = _fixture.WriteExecutable("version-only.py", "#!/usr/bin/env python3\nprint('version-only')\n");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(300),
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride> { ["acp:antigravity"] = new(path) }
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), s => s.Provider == "acp:antigravity");
        Assert.True(status.Available);
        Assert.Empty(status.Models);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenCodeFallbackHasItsOwnTimeoutAndDrainsStderr(bool modelsTimeout)
    {
        var path = _fixture.WriteExecutable("opencode-fallback.py", """
            #!/usr/bin/env python3
            import os, sys, time
            if '--version' in sys.argv:
                print('opencode mock')
            elif 'models' in sys.argv:
                if os.environ['MODELS_TIMEOUT'] == 'true':
                    time.sleep(30)
                else:
                    sys.stderr.write('x' * 262144)
                    sys.stderr.flush()
                    print('provider/model')
            else:
                time.sleep(30)
            """);
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(500),
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:opencode"] = new(path, Env: new Dictionary<string, string>
                {
                    ["MODELS_TIMEOUT"] = modelsTimeout ? "true" : "false"
                })
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var status = Assert.Single(await runtime.ProbeProvidersAsync(deadline.Token), s => s.Provider == "acp:opencode");
        Assert.True(status.Available);
        Assert.Null(status.Reason);
        Assert.Equal(modelsTimeout ? Array.Empty<string>() : ["provider/model"], status.Models);
    }

    [Fact]
    public async Task ProbeProvidersAsync_ReportsAvailableMissingFailingAndTimeoutCleanly()
    {
        var options = new AgentRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(350),
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(_fixture.MockCodexPath),
                ["claude"] = new(_fixture.MockClaudePath, Models: ["claude-sonnet-4-6-custom"]),
                ["acp:antigravity"] = new("/nonexistent/perga/antigravity-missing"),
                ["acp:opencode"] = new(_fixture.MockFailingVersionPath),
                ["acp:slow-hang"] = new(_fixture.MockSlowVersionPath)
            }
        };

        await using var runtime = new AgentRuntime(options);
        var statuses = await runtime.ProbeProvidersAsync(CancellationToken.None);

        var codex = Assert.Single(statuses, s => s.Provider == "codex");
        Assert.True(codex.Available);
        Assert.Equal("codex-cli 0.99.0-mock", codex.Version);
        Assert.Null(codex.Reason);
        Assert.NotEmpty(codex.Models);

        var claude = Assert.Single(statuses, s => s.Provider == "claude");
        Assert.True(claude.Available);
        Assert.Equal("2.1.0 (Claude Code Mock)", claude.Version);
        Assert.Equal(["claude-sonnet-4-6-custom"], claude.Models);

        var agy = Assert.Single(statuses, s => s.Provider == "acp:antigravity");
        Assert.False(agy.Available);
        Assert.Null(agy.Version);
        Assert.Contains("not found", agy.Reason, StringComparison.OrdinalIgnoreCase);

        var opencode = Assert.Single(statuses, s => s.Provider == "acp:opencode");
        Assert.False(opencode.Available);
        Assert.Contains("fatal configuration error", opencode.Reason, StringComparison.OrdinalIgnoreCase);

        var slow = Assert.Single(statuses, s => s.Provider == "acp:slow-hang");
        Assert.False(slow.Available);
        Assert.Contains("Timed out", slow.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
