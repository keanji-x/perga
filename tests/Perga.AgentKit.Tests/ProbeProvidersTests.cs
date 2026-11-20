namespace Perga.AgentKit.Tests;

public sealed class ProbeProvidersTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("acp:antigravity", false)]
    [InlineData("acp:antigravity", true)]
    [InlineData("acp:opencode", false)]
    [InlineData("acp:opencode", true)]
    public async Task BuiltInAcpCatalogPreservesNativeSelectionWithoutStartingSession(string provider, bool defaultFails)
    {
        var log = Path.Combine(_fixture.RootDir, "provider-commands.txt");
        var cli = _fixture.WriteExecutable("model-catalog.py", """
            #!/usr/bin/env python3
            import os, sys
            with open(os.environ['COMMAND_LOG'], 'a') as log:
                log.write(' '.join(sys.argv[1:]) + '\n')
            if sys.argv[1:] == ['wrapper-arg', '--version']:
                print('mock 1.0.0')
            elif sys.argv[1:] == ['wrapper-arg', '--output-format', 'json', 'models']:
                print('{"status":"SUCCESS","command":{"name":"models","data":{"models":[{"id":"gemini-4-neon-high","label":"Gemini 4 Neon (High)"},{"id":"gemini-4-neon-low","label":"Gemini 4 Neon (Low)"}]}}}')
            elif sys.argv[1:] == ['wrapper-arg', '--settings', '{"disableSlashCommands":false}', '--disable-slash-commands=false', '--output-format', 'json', '-p', '/model']:
                if os.environ['DEFAULT_FAILS'] == 'true':
                    sys.exit(1)
                print('{"status":"SUCCESS","command":{"name":"model","data":{"id":"gemini-4-neon-low","label":"Gemini 4 Neon (Low)","effort":"low","is_default":false}}}')
            elif sys.argv[1:6] == ['wrapper-arg', 'api', 'model.list', '--standalone', '--param'] and sys.argv[6] == 'location[directory]=' + os.getcwd():
                print('{"data":[{"providerID":"vendor","id":"model-high","name":"Other","variants":[{"id":"low"}]},{"providerID":"other","id":"model-high","name":"Chosen","variants":[{"id":"none"},{"id":"xhigh"},{"id":"xhigh"}]},{"enabled":false,"providerID":"vendor","id":"disabled"}]}')
            elif sys.argv[1:6] == ['wrapper-arg', 'api', 'model.default', '--standalone', '--param'] and sys.argv[6] == 'location[directory]=' + os.getcwd():
                if os.environ['DEFAULT_FAILS'] == 'true':
                    print('invalid JSON')
                else:
                    print('{"data":{"providerID":"other","id":"model-high"}}')
            else:
                sys.exit(1)
            """);
        var overrides = _fixture.UnavailableProviders();
        overrides[provider] = new(cli, PrependArgs: ["wrapper-arg"], Env: new Dictionary<string, string>
        {
            ["COMMAND_LOG"] = log,
            ["DEFAULT_FAILS"] = defaultFails ? "true" : "false"
        });
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions { ProviderOverrides = overrides });

        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), s => s.Provider == provider);
        var location = $"location[directory]={Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))}";
        var commands = File.ReadAllLines(log);
        Assert.Equal("wrapper-arg --version", commands[0]);
        Assert.Equal(provider == "acp:antigravity"
                ? ["wrapper-arg --output-format json models", """wrapper-arg --settings {"disableSlashCommands":false} --disable-slash-commands=false --output-format json -p /model"""]
                : new[] { $"wrapper-arg api model.default --standalone --param {location}",
                    $"wrapper-arg api model.list --standalone --param {location}" },
            commands.Skip(1).Order(StringComparer.Ordinal));
        Assert.True(status.Available);
        var entries = status.ModelCatalog!;
        Assert.Equal(provider == "acp:antigravity"
            ? ["gemini-4-neon-high", "gemini-4-neon-low"] : new[] { "vendor/model-high", "other/model-high" }, status.Models);
        Assert.False(entries[0].IsDefault);
        Assert.Equal(!defaultFails, entries[1].IsDefault);
        Assert.Equal(provider == "acp:antigravity" ? "Gemini 4 Neon (Low)" : "Chosen", entries[1].DisplayName);
        Assert.Equal(provider == "acp:antigravity" && !defaultFails ? "low" : null, entries[1].DefaultReasoningEffort);
        Assert.Equal(provider == "acp:antigravity" ? [] : new[] { "none", "xhigh" },
            entries[1].SupportedReasoningEfforts.Select(option => option.ReasoningEffort));
    }

    [Fact]
    public async Task CustomAcpDiscoveryReturnsAdvertisedModels()
    {
        const string provider = "acp:custom";
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(_fixture.UnavailableProviders(), StringComparer.OrdinalIgnoreCase)
            {
                [provider] = new(_fixture.MockAcpPath, Env: new Dictionary<string, string>
                { ["MOCK_ACP_MODELS"] = "{\"models\":{\"availableModels\":[{\"modelId\":\"mock-model-a\"},{\"modelId\":\"mock-model-b\"}]}}" })
            }
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), s => s.Provider == provider);
        Assert.True(status.Available, status.Reason);
        Assert.Equal(["mock-model-a", "mock-model-b"], status.Models);
    }

    [Theory]
    [InlineData("{\"configOptions\":[{\"category\":\"model\",\"id\":\"choice\",\"options\":[{\"value\":\"model-1\"},{\"group\":\"Other\",\"options\":[{\"value\":\"model-2\"}]}]}]}", false)]
    [InlineData("{}", true)]
    public async Task AcpDiscoveryReadsConfigOptionsAndHonorsOverrides(string advertised, bool explicitOverride)
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(_fixture.UnavailableProviders(), StringComparer.OrdinalIgnoreCase)
            {
                ["acp:custom"] = new(_fixture.MockAcpPath,
                    Env: new Dictionary<string, string> { ["MOCK_ACP_MODELS"] = advertised },
                    Models: explicitOverride ? ["override"] : null)
            }
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), s => s.Provider == "acp:custom");
        Assert.True(status.Available, status.Reason);
        Assert.Equal(explicitOverride ? ["override"] : new[] { "model-1", "model-2" }, status.Models);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitModelsIncludingEmptyArePreservedWhileProbingPermissionModes(bool empty)
    {
        var log = Path.Combine(_fixture.RootDir, "discovery.jsonl");
        string[] models = empty ? [] : ["override"];
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(_fixture.UnavailableProviders(), StringComparer.OrdinalIgnoreCase)
            {
                ["acp:custom"] = new(_fixture.MockAcpPath,
                    Env: new Dictionary<string, string>
                    {
                        ["MOCK_ACP_LOG"] = log,
                        ["MOCK_ACP_MODELS"] = """{"modes":{"currentModeId":"plan","availableModes":[{"id":"plan","name":"Plan"}]}}"""
                    }, Models: models)
            }
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), s => s.Provider == "acp:custom");
        Assert.Equal(models, status.Models);
        Assert.True(File.Exists(log));
        Assert.Equal("plan", Assert.Single(status.PermissionModes!).Id);
    }

    [Theory]
    [InlineData("acp:antigravity")]
    [InlineData("acp:opencode")]
    public async Task FailedModelsCommandKeepsProviderAvailableWithoutStartingSession(string provider)
    {
        var log = Path.Combine(_fixture.RootDir, "provider-commands.txt");
        var path = _fixture.WriteExecutable("version-only.py", """
            #!/usr/bin/env python3
            import os, sys
            with open(os.environ['COMMAND_LOG'], 'a') as log:
                log.write(' '.join(sys.argv[1:]) + '\n')
            if sys.argv[1:] == ['--version']:
                print('version-only')
            else:
                sys.exit(1)
            """);
        var overrides = _fixture.UnavailableProviders();
        overrides[provider] = new(path, Env: new Dictionary<string, string> { ["COMMAND_LOG"] = log });
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = overrides
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), s => s.Provider == provider);
        Assert.True(status.Available, status.Reason);
        Assert.Empty(status.Models);
        var launchLog = File.ReadAllLines(log);
        Assert.Equal(provider == "acp:antigravity" ? 2 : 3, launchLog.Length);
        Assert.Equal("--version", launchLog[0]);
        Assert.DoesNotContain(launchLog, command => command == "acp");

        File.WriteAllText(log, "");
        overrides[provider] = overrides[provider] with { Models = ["configured-model"] };
        var configured = Assert.Single(await runtime.ProbeProvidersAsync(forceRefresh: true, CancellationToken.None),
            s => s.Provider == provider);
        Assert.Equal(["configured-model"], configured.Models);
        Assert.Equal(["--version"], File.ReadAllLines(log));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenCodeModelsIsBoundedAndDrainsStderr(bool modelsTimeout)
    {
        var path = _fixture.WriteExecutable("opencode-models.py", """
            #!/usr/bin/env python3
            import os, sys, time
            if '--version' in sys.argv:
                print('opencode mock')
            elif 'model.list' in sys.argv:
                if os.environ['MODELS_TIMEOUT'] == 'true':
                    time.sleep(30)
                else:
                    sys.stderr.write('x' * 262144)
                    sys.stderr.flush()
                    print('{"data":[{"providerID":"provider","id":"model","name":"Model"}]}')
            elif 'model.default' in sys.argv:
                print('{"data":null}')
            else:
                time.sleep(30)
            """);
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(500),
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(_fixture.UnavailableProviders(), StringComparer.OrdinalIgnoreCase)
            {
                ["acp:opencode"] = new(path, Env: new Dictionary<string, string>
                {
                    ["MODELS_TIMEOUT"] = modelsTimeout ? "true" : "false"
                })
            }
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var status = Assert.Single(await runtime.ProbeProvidersAsync(deadline.Token), s => s.Provider == "acp:opencode");
        Assert.True(status.Available, status.Reason);
        Assert.Null(status.Reason);
        Assert.Equal(modelsTimeout ? Array.Empty<string>() : ["provider/model"], status.Models);
    }

    [Fact]
    public async Task ProbeProvidersAsync_ReportsLiveModelsMissingFailingAndTimeoutCleanly()
    {
        var options = new AgentRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(800),
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(_fixture.UnavailableProviders(), StringComparer.OrdinalIgnoreCase)
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
        Assert.True(codex.Available, codex.Reason);
        Assert.Equal("codex-cli 0.99.0-mock", codex.Version);
        Assert.Null(codex.Reason);
        Assert.Equal(["gpt-6.2-sol", "gpt-5.4", "gpt-6.1-sol"], codex.Models);

        var claude = Assert.Single(statuses, s => s.Provider == "claude");
        Assert.True(claude.Available, claude.Reason);
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
    public async Task ProbeProvidersAsync_FallsBackToStaticModelsWhenCodexModelListFails()
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

        await using var fallbackRuntime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(_fixture.UnavailableProviders(), StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new(versionOnlyCli)
            }
        });

        var fallbackStatuses = await fallbackRuntime.ProbeProvidersAsync(CancellationToken.None);
        var codexFallback = Assert.Single(fallbackStatuses, s => s.Provider == "codex");
        Assert.Equal(["gpt-5.4", "gpt-5.3-codex", "o4-mini"], codexFallback.Models);
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
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(_fixture.UnavailableProviders(), StringComparer.OrdinalIgnoreCase)
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
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(_fixture.UnavailableProviders(), StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new("codex", Env: new Dictionary<string, string> { ["HOME"] = fakeHome, ["PATH"] = poisonedPath }, Models: ["gpt-5.4"]),
                ["acp:antigravity"] = new("agy", Env: new Dictionary<string, string> { ["HOME"] = fakeHome, ["PATH"] = poisonedPath }, Models: ["gemini-4-carbon-medium"])
            }
        });

        var statuses = await runtime.ProbeProvidersAsync(CancellationToken.None);
        var codex = Assert.Single(statuses, s => s.Provider == "codex");
        Assert.True(codex.Available, codex.Reason);
        Assert.Equal("codex-cli 0.169.1", codex.Version);

        var agy = Assert.Single(statuses, s => s.Provider == "acp:antigravity");
        Assert.True(agy.Available, agy.Reason);
        Assert.Equal("antigravity 1.5.10", agy.Version);
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
