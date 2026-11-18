namespace Perga.AgentKit.Tests;

public sealed class ProbeProvidersTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ProbeProvidersAsync_ReportsLiveModelsMissingFailingAndTimeoutCleanly()
    {
        var options = new AgentRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(800),
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
        Assert.Equal(["gpt-6.2-sol", "gpt-5.4", "gpt-6.1-sol"], codex.Models);

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

    [Fact]
    public async Task ProbeProvidersAsync_DiscoversAcpAvailableModelsAndConfigOptions_WithUpdatedFallbacks()
    {
        var versionOnlyCli = _fixture.WriteExecutable(
            "version_only_cli.py",
            """
            #!/usr/bin/env python3
            import sys
            if "--version" in sys.argv:
                sys.stdout.write("cli-ver 1.5.10\n")
                sys.exit(0)
            sys.exit(1)
            """);

        await using var liveRuntime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:antigravity"] = new(_fixture.MockAcpPath),
                ["acp:opencode"] = new(
                    _fixture.MockAcpPath,
                    Env: new Dictionary<string, string> { ["MOCK_ACP_CONFIG_OPTIONS_MODELS"] = "1" })
            }
        });

        var liveStatuses = await liveRuntime.ProbeProvidersAsync(CancellationToken.None);
        var agyLive = Assert.Single(liveStatuses, s => s.Provider == "acp:antigravity");
        Assert.Equal(["gemini-4-carbon-medium", "gemini-4-carbon-high", "claude-sonnet-5-5-medium"], agyLive.Models);

        var opencodeLive = Assert.Single(liveStatuses, s => s.Provider == "acp:opencode");
        Assert.Equal(["opencode-sonnet-4-6", "opencode-gpt-5.4"], opencodeLive.Models);

        // When model/list or session/new fails, fall back to updated DefaultCodexModels & DefaultAntigravityModels
        await using var fallbackRuntime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(versionOnlyCli),
                ["acp:antigravity"] = new(versionOnlyCli)
            }
        });

        var fallbackStatuses = await fallbackRuntime.ProbeProvidersAsync(CancellationToken.None);
        var codexFallback = Assert.Single(fallbackStatuses, s => s.Provider == "codex");
        Assert.Equal(["gpt-5.4", "gpt-5.3-codex", "o4-mini"], codexFallback.Models);

        var agyFallback = Assert.Single(fallbackStatuses, s => s.Provider == "acp:antigravity");
        Assert.Equal(
            ["gemini-4-carbon-medium", "gemini-4-carbon-high", "gemini-3.8-flash-high", "claude-sonnet-5-5-medium"],
            agyFallback.Models);
    }

    [Fact]
    public async Task ProbeProvidersAsync_CachesResultsWithinTtlAndSupportsForceRefresh()
    {
        var counterFile = Path.Combine(_fixture.RootDir, "probe_count.txt");
        var countingCli = _fixture.WriteExecutable(
            "counting_cli.py",
            $$"""
            #!/usr/bin/env python3
            import os, sys
            p = {{System.Text.Json.JsonSerializer.Serialize(counterFile)}}
            n = int(open(p).read().strip()) + 1 if os.path.exists(p) else 1
            with open(p, "w") as f:
                f.write(str(n))
            if "--version" in sys.argv:
                sys.stdout.write(f"counting-cli 1.0.{n}\n")
                sys.exit(0)
            sys.exit(1)
            """);

        var fakeClock = new AdjustableTimeProvider();
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProbeCacheTtl = TimeSpan.FromSeconds(60),
            TimeProvider = fakeClock,
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(countingCli, Models: ["gpt-5.4"]),
                ["claude"] = new("/nonexistent/claude"),
                ["acp:antigravity"] = new("/nonexistent/agy"),
                ["acp:opencode"] = new("/nonexistent/opencode")
            }
        });

        var first = await runtime.ProbeProvidersAsync(CancellationToken.None);
        Assert.Equal("counting-cli 1.0.1", Assert.Single(first, s => s.Provider == "codex").Version);

        // Within 60s TTL -> cached, subprocess NOT invoked again
        fakeClock.Advance(TimeSpan.FromSeconds(30));
        var second = await runtime.ProbeProvidersAsync(CancellationToken.None);
        Assert.Equal("counting-cli 1.0.1", Assert.Single(second, s => s.Provider == "codex").Version);
        Assert.Equal("1", (await File.ReadAllTextAsync(counterFile)).Trim());

        // Force refresh -> bypasses cache immediately
        var forced = await runtime.ProbeProvidersAsync(forceRefresh: true, CancellationToken.None);
        Assert.Equal("counting-cli 1.0.2", Assert.Single(forced, s => s.Provider == "codex").Version);
        Assert.Equal("2", (await File.ReadAllTextAsync(counterFile)).Trim());

        // After 61s past last refresh -> cache expires naturally
        fakeClock.Advance(TimeSpan.FromSeconds(61));
        var expired = await runtime.ProbeProvidersAsync(CancellationToken.None);
        Assert.Equal("counting-cli 1.0.3", Assert.Single(expired, s => s.Provider == "codex").Version);
        Assert.Equal("3", (await File.ReadAllTextAsync(counterFile)).Trim());
    }

    [Fact]
    public async Task PathPrecedence_UserLocalBinBeatsLegacyNeigeCalmLocalBin()
    {
        var fakeHome = Path.Combine(_fixture.RootDir, "fake_home");
        var userLocalBin = Path.Combine(fakeHome, ".local", "bin");
        var legacyNeigeBin = Path.Combine(fakeHome, "neige-calm", ".local-bin");
        Directory.CreateDirectory(userLocalBin);
        Directory.CreateDirectory(legacyNeigeBin);

        var modernCodex = Path.Combine(userLocalBin, "codex");
        File.WriteAllText(modernCodex, "#!/usr/bin/env python3\nimport sys\nif '--version' in sys.argv: print('codex-cli 0.169.1'); sys.exit(0)\nsys.exit(1)\n");
        var staleCodex = Path.Combine(legacyNeigeBin, "codex");
        File.WriteAllText(staleCodex, "#!/usr/bin/env python3\nimport sys\nif '--version' in sys.argv: print('codex-cli 0.144.1'); sys.exit(0)\nsys.exit(1)\n");
        var agyBin = Path.Combine(userLocalBin, "agy");
        File.WriteAllText(agyBin, "#!/usr/bin/env python3\nimport sys\nif '--version' in sys.argv: print('antigravity 1.5.10'); sys.exit(0)\nsys.exit(1)\n");

        if (!OperatingSystem.IsWindows())
        {
            var execMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(modernCodex, execMode);
            File.SetUnixFileMode(staleCodex, execMode);
            File.SetUnixFileMode(agyBin, execMode);
        }

        // Legacy neige-calm/.local-bin is deliberately placed FIRST in PATH!
        var poisonedPath = string.Join(Path.PathSeparator, legacyNeigeBin, "/usr/bin", "/bin", userLocalBin);
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Sandbox = new OsSandboxOptions { HomeDirectoryOverride = fakeHome },
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new("codex", Env: new Dictionary<string, string> { ["HOME"] = fakeHome, ["PATH"] = poisonedPath }, Models: ["gpt-5.4"]),
                ["acp:antigravity"] = new("agy", Env: new Dictionary<string, string> { ["HOME"] = fakeHome, ["PATH"] = poisonedPath }, Models: ["gemini-4-carbon-medium"])
            }
        });

        var statuses = await runtime.ProbeProvidersAsync(CancellationToken.None);
        var codex = Assert.Single(statuses, s => s.Provider == "codex");
        Assert.True(codex.Available);
        Assert.Equal("codex-cli 0.169.1", codex.Version);

        var agy = Assert.Single(statuses, s => s.Provider == "acp:antigravity");
        Assert.True(agy.Available);
        Assert.Equal("antigravity 1.5.10", agy.Version);
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
