using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class AcpProviderTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private AgentRuntime CreateRuntime(IReadOnlyDictionary<string, string>? acpEnv = null) =>
        new(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(Path.Combine(_fixture.RootDir, "unavailable-codex")),
                ["claude"] = new(Path.Combine(_fixture.RootDir, "unavailable-claude")),
                ["acp:antigravity"] = new(_fixture.MockAcpPath, Env: acpEnv),
                ["acp:opencode"] = new(_fixture.MockAcpPath, Env: acpEnv)
            }
        });

    private AgentRuntime CreateDiscoveryRuntime(IReadOnlyDictionary<string, string> acpEnv)
    {
        var overrides = _fixture.UnavailableProviders();
        overrides["acp:custom"] = new(_fixture.MockAcpPath, Env: acpEnv);
        return new AgentRuntime(new AgentRuntimeOptions { ProviderOverrides = overrides });
    }

    [Theory]
    [InlineData(OsSandboxMode.ReadOnlyBwrap)]
    [InlineData(OsSandboxMode.WorkspaceWriteBwrap)]
    public async Task PermissionDiscoveryPassesSandboxAndOverlaysToProcessLaunch(OsSandboxMode sandbox)
    {
        if (OperatingSystem.IsWindows()) return;
        var wrapper = Path.Combine(_fixture.RootDir, "record-bwrap");
        var log = Path.Combine(_fixture.RootDir, "sandbox-args");
        File.WriteAllText(wrapper, """
            #!/bin/sh
            printf '%s\n' "$@" > "$SANDBOX_LOG"
            while [ "$1" != "--" ]; do shift; done
            shift
            exec "$@"
            """);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lower = Path.Combine(_fixture.RootDir, "lower");
        Directory.CreateDirectory(lower);
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Sandbox = new OsSandboxOptions
            {
                PlatformOverride = System.Runtime.InteropServices.OSPlatform.Linux,
                BwrapExecutablePath = wrapper,
                BwrapVersionOverride = new Version(0, 10, 0)
            },
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:antigravity"] = new(_fixture.MockAcpPath, Env: new Dictionary<string, string>
                {
                    ["SANDBOX_LOG"] = log,
                    ["MOCK_ACP_MODELS"] = """{"modes":{"currentModeId":"plan","availableModes":[{"id":"plan","name":"Plan"}]}}"""
                })
            }
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", null, _fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>(),
            Sandbox: sandbox, OverlayMounts: [new([lower], null, null, _fixture.WorkspaceDir)]), CancellationToken.None);
        var modes = session.AvailablePermissionModes;
        Assert.Equal("plan", Assert.Single(modes).Id);
        var args = File.ReadAllLines(log);
        Assert.Contains("--overlay-src", args);
        Assert.Contains(lower, args);
        Assert.Contains(_fixture.WorkspaceDir, args);
        var workspaceBind = Enumerable.Range(0, args.Length - 2).Single(index =>
            args[index + 1] == _fixture.WorkspaceDir && args[index + 2] == _fixture.WorkspaceDir);
        Assert.Equal(sandbox == OsSandboxMode.ReadOnlyBwrap ? "--ro-bind" : "--bind", args[workspaceBind]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"success\":false}")]
    [InlineData("{\"configOptions\":null}")]
    [InlineData("{\"configOptions\":[]}")]
    [InlineData("{\"success\":\"false\"}")]
    [InlineData("{\"configOptions\":[{\"id\":\"worker-thought\",\"category\":\"thought_level\",\"type\":\"select\",\"currentValue\":\"normal\",\"options\":[{\"value\":\"normal\"}]}]}")]
    [InlineData("{\"configOptions\":[{\"id\":\"worker-thought\",\"category\":\"thought_level\",\"currentValue\":42}]}")]
    [InlineData("{\"configOptions\":[{\"id\":\"other\",\"category\":\"thought_level\",\"currentValue\":\"deep\"}]}")]
    public async Task ThoughtAcknowledgementRejectionKeepsPartialStateAndBlocksPrompt(string acknowledgement)
    {
        var log = Path.Combine(_fixture.RootDir, "rejected-thought.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_WORKER_OPTIONS"] = "1",
            ["MOCK_ACP_THOUGHT_RESULT"] = acknowledgement
        });
        var spec = new HarnessLaunchSpec("acp:opencode", null, _fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>());
        await using var session = await runtime.OpenSessionAsync(spec, default);
        var result = Assert.IsType<SessionConfigurationOutcome.Rejected>(await session.ConfigureAsync(SessionConfiguration.FromNullable("large", "deep")));
        Assert.Equal("large", result.ObservedState.NativeModel);
        Assert.Equal("normal", result.ObservedState.NativeEffort);
        Assert.False(result.ObservedState.IsUsable);
        Assert.Null(session.ConfigurationState);
        Assert.Null(session.ContextUsageRevision);
        await Assert.ThrowsAsync<SessionConfigurationException>(() => DrainTurnAsync(session, new("blocked", "blocked")));
        Assert.DoesNotContain(File.ReadAllLines(log), line => line.Contains("session/prompt", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(spec with { Model = "large", ReasoningEffort = "deep" }, default));
        Assert.IsType<SessionConfigurationOutcome.Rejected>(error.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThoughtTransportTimeoutOrCancellationInvalidatesCompleteSelection(bool cancelCaller)
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:opencode"] = new(_fixture.MockAcpPath, Env: new Dictionary<string, string>
                    { ["MOCK_ACP_WORKER_OPTIONS"] = "1", ["MOCK_ACP_THOUGHT_DELAY_MS"] = "2000" })
            }
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec("acp:opencode", null,
            _fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>()), default);
        var nativeId = session.NativeThreadId;
        using var cancellation = new CancellationTokenSource();
        if (cancelCaller) cancellation.CancelAfter(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConfigureAsync(
            SessionConfiguration.FromNullable("large", "deep"), cancellation.Token));
        Assert.Null(session.ConfigurationState);
        Assert.Null(session.ContextUsageRevision);
        Assert.Equal(nativeId, session.NativeThreadId);
        await Assert.ThrowsAsync<SessionConfigurationException>(() => DrainTurnAsync(session, new("blocked", "after-timeout")));
    }

    [Fact]
    public async Task PermissionReopenOutcomeRequiresTheCompleteSelectionToBeExpressible()
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = Path.Combine(_fixture.RootDir, "agy");
        File.WriteAllText(executable, $"#!/bin/sh\nexec \"{_fixture.MockAcpPath}\" \"$@\"\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:antigravity"] = new(executable, Env: new Dictionary<string, string>
                    { ["MOCK_ACP_WORKER_OPTIONS"] = "1" })
            }
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec("acp:antigravity", null,
            _fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>()), default);
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "deep", "bypassPermissions")));
        Assert.IsType<SessionConfigurationOutcome.Rejected>(await session.ConfigureAsync(SessionConfiguration.FromNullable("large", "invalid", "bypassPermissions")));
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await session.ConfigureAsync(SessionConfiguration.FromNullable("large", "deep", "bypassPermissions")));
    }

    [Fact]
    public async Task EmptyThoughtAcknowledgementIsCompatibleAndConfirmsCompleteSelection()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_WORKER_OPTIONS"] = "1", ["MOCK_ACP_THOUGHT_RESULT"] = "{}"
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec("acp:opencode", null, _fixture.WorkspaceDir,
            null, null, new Dictionary<string, McpServerConfig>(), ReasoningEffort: "deep"), default);
        Assert.Equal("deep", session.ConfigurationState?.NativeEffort);
        Assert.IsType<SessionSelection.ProviderDefault>(session.ConfigurationState!.Selection.Model);
    }

    [Fact]
    public async Task ResumeCannotInferDefaultModelFromLoadedCurrentSelection()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string> { ["MOCK_ACP_WORKER_OPTIONS"] = "1" });
        var error = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", null, _fixture.WorkspaceDir, "saved-thread", null, new Dictionary<string, McpServerConfig>(), ReasoningEffort: "deep"), default));
        var unsupported = Assert.IsType<SessionConfigurationOutcome.Unsupported>(error.Outcome);
        Assert.Equal(ConfigurationDimension.Model, unsupported.Dimension);
    }

    [Fact]
    public async Task OrdinaryThoughtLevelRejectsContradictoryAcknowledgement()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_WORKER_OPTIONS"] = "1",
            ["MOCK_ACP_REJECT_WORKER_EFFORT"] = "1"
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>()), CancellationToken.None);
        Assert.IsNotType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("large", "deep")));
    }

    [Fact]
    public async Task NullLaunchReadsWorkspaceCatalogueAndKeepsWorkspaceDefault()
    {
        var log = Path.Combine(_fixture.RootDir, "workspace-modes.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_PROJECT_MODES"] = "1"
        });
        var payload = """{"modes":{"currentModeId":"project-plan","availableModes":[{"id":"project-plan","name":"Project plan"}]}}""";
        File.WriteAllText(Path.Combine(_fixture.WorkspaceDir, "permission-modes.json"), payload);
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>()), CancellationToken.None);
        Assert.Equal("project-plan", Assert.Single(session.AvailablePermissionModes).Id);
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var opens = calls.Where(call => call.GetProperty("method").GetString() == "session/new").ToArray();
        Assert.Equal([_fixture.WorkspaceDir],
            opens.Select(call => call.GetProperty("params").GetProperty("cwd").GetString()));
        Assert.DoesNotContain(calls, call => call.GetProperty("method").GetString() is "session/set_mode" or "session/set_config_option");
    }

    [Theory]
    [InlineData(0, null, false)]
    [InlineData(0, "restored", false)]
    [InlineData(0, "restored", true)]
    [InlineData(1, null, false)]
    [InlineData(1, "restored", false)]
    [InlineData(1, "restored", true)]
    [InlineData(2, null, false)]
    [InlineData(2, "restored", false)]
    [InlineData(2, "restored", true)]
    public async Task PermissionModesAreReadAndAppliedOnNewLoadAndResume(int shape, string? resume, bool nativeResume)
    {
        var config = shape != 0;
        var log = Path.Combine(_fixture.RootDir, "mode-calls.jsonl");
        var payload = config
            ? """{"configOptions":[{"id":"unrelated","category":"model","type":"select","options":[{"value":"wrong"}]},{"id":"agent-mode","category":"mode","type":"select","currentValue":"safe","options":[{"value":"safe","name":"Safe","description":"Read first"},{"group":"Other","options":[{"value":"execute","name":"Execute"}]}]}]}"""
            : """{"modes":{"currentModeId":"safe","availableModes":[{"id":"safe","name":"Safe","description":"Read first"},{"id":"execute","name":"Execute"}]}}""";
        if (shape == 2) payload = payload.Replace("agent-mode", "mode").Replace("\"category\":\"mode\",", "");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_MODELS"] = payload,
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_RESUME"] = nativeResume ? "1" : "0"
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", resume is null ? null : "saved-model", _fixture.WorkspaceDir, resume, null,
            new Dictionary<string, McpServerConfig>(), PermissionMode: "execute"), CancellationToken.None);
        var modes = session.AvailablePermissionModes;
        Assert.Equal(["safe", "execute"], modes.Select(mode => mode.Id));
        Assert.Equal("safe", Assert.Single(modes, mode => mode.IsDefault).Id);
        Assert.Equal("Read first", modes[0].Description);
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var selected = Assert.Single(calls, call => call.GetProperty("method").GetString() ==
            (config ? "session/set_config_option" : "session/set_mode"));
        var parameters = selected.GetProperty("params");
        Assert.Equal(resume ?? "acp_sess_001", parameters.GetProperty("sessionId").GetString());
        Assert.Equal("execute", parameters.GetProperty(config ? "value" : "modeId").GetString());
        if (config) Assert.Equal(shape == 2 ? "mode" : "agent-mode", parameters.GetProperty("configId").GetString());
    }

    [Fact]
    public async Task AntigravityExecutableExposesBypassPermissionsAndPassesDangerouslySkipPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        var agyWrapper = Path.Combine(_fixture.RootDir, "agy");
        File.WriteAllText(agyWrapper, $"""
            #!/bin/sh
            exec "{_fixture.MockAcpPath}" "$@"
            """);
        File.SetUnixFileMode(agyWrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var log = Path.Combine(_fixture.RootDir, "antigravity-bypass.jsonl");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:antigravity"] = new(agyWrapper, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_LOG"] = log,
                    ["MOCK_ACP_LOG_ARGV"] = "1",
                    ["MOCK_ACP_MODELS"] = """{"modes":{"currentModeId":"default","availableModes":[{"id":"default","name":"Default"},{"id":"auto","name":"Auto"}]}}"""
                })
            }
        });

        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), PermissionMode: "bypassPermissions"), CancellationToken.None);
        Assert.Contains(session.AvailablePermissionModes, mode => mode.Id == "bypassPermissions" && mode.Label == "Full access");

        var entries = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var argvEntry = Assert.Single(entries, e => e.TryGetProperty("argv", out _));
        var argv = argvEntry.GetProperty("argv").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("--dangerously-skip-permissions", argv);
        Assert.DoesNotContain(entries, e =>
            e.TryGetProperty("method", out var m) &&
            (m.GetString() == "session/set_mode" || m.GetString() == "session/set_config_option"));
    }

    [Theory]
    [InlineData("vendor/model-high", null, "vendor/model-high")]
    [InlineData("vendor/model-high", "high", "vendor/model-high")]
    [InlineData("vendor/model-HIGH", "high", "vendor/model-HIGH")]
    [InlineData("vendor/model-low,vendor/model-high", null, "vendor/model-low")]
    [InlineData("vendor/model-low,vendor/model-high", "high", "vendor/model-high")]
    [InlineData("vendor/model,vendor/model-high", null, "vendor/model")]
    [InlineData("vendor/model,vendor/model-high", "high", "vendor/model-high")]
    [InlineData("vendor/model-medium,vendor/model-high", null, "vendor/model-medium")]
    public async Task AdvertisedGroupDefaultAndEffortUseExactIdsOnOpenAndSwitch(string ids, string? effort, string expected)
    {
        var log = Path.Combine(_fixture.RootDir, "strict-model.jsonl");
        var modelIds = ids.Split(',');
        await using var runtime = CreateDiscoveryRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_STRICT_MODELS"] = "1",
            ["MOCK_ACP_MODELS"] = JsonSerializer.Serialize(new { models = new {
                currentModelId = modelIds[0], availableModels = modelIds.Select(id => new { modelId = id }) } })
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), p => p.Provider == "acp:custom");
        var model = Assert.Single(status.ModelCatalog!);
        Assert.Equal("vendor/model", model.Model);
        if (effort is null && !modelIds.Contains("vendor/model"))
            Assert.Equal(expected[(expected.LastIndexOf('-') + 1)..].ToLowerInvariant(), model.DefaultReasoningEffort);
        if (modelIds.Contains("vendor/model"))
            Assert.Null(model.DefaultReasoningEffort);
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:custom", model.Model, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: effort), CancellationToken.None);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(model.Model, effort)));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.GetProperty("method").GetString() == "session/set_model").ToArray();
        Assert.Single(calls);
        Assert.All(calls, call => Assert.Equal(expected, call.GetProperty("params").GetProperty("modelId").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultModelEffortAppliesOnOpenAndSwitch(bool configFallback)
    {
        var log = Path.Combine(_fixture.RootDir, "default-effort.jsonl");
        await using var runtime = CreateDiscoveryRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_NO_SET_MODEL"] = configFallback ? "1" : "0",
            ["MOCK_ACP_MODELS"] = """{"models":{"currentModelId":"vendor/model-high","availableModels":[{"modelId":"vendor/model-low"},{"modelId":"vendor/model-high"}]}}"""
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), p => p.Provider == "acp:custom");
        Assert.Equal("high", Assert.Single(status.ModelCatalog!).DefaultReasoningEffort);
        File.Delete(log);
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:custom", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "low"), CancellationToken.None);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "high")));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("vendor/model")));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("  ", " low ")));
        Assert.IsNotType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "unsupported")));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, null)));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.GetProperty("method").GetString() == (configFallback ? "session/set_config_option" : "session/set_model")).ToArray();
        Assert.Equal(new[] { "vendor/model-low", "vendor/model-high", "vendor/model-high", "vendor/model-low", "vendor/model-high" },
            calls.Select(call => call.GetProperty("params").GetProperty(configFallback ? "value" : "modelId").GetString()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"models":{"currentModelId":"bare-model","availableModels":[{"modelId":"bare-model"}]}}""")]
    public async Task EffortOnlyWithoutAdvertisedMappingDoesNotInventModelIds(string payload)
    {
        var log = Path.Combine(_fixture.RootDir, "unknown-effort.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_MODELS"] = payload
        });
        var spec = new HarnessLaunchSpec("acp:opencode", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "high");
        var error = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(spec, default));
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(error.Outcome);
        await using var session = await runtime.OpenSessionAsync(spec with { ReasoningEffort = null }, default);
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "high")));
        Assert.DoesNotContain(File.ReadAllLines(log), line => line.Contains("session/set_", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("small")]
    public async Task OrdinarySessionsDiscoverAndApplyThoughtLevel(string? model)
    {
        var log = Path.Combine(_fixture.RootDir, "ordinary-thought.jsonl");
        await using var runtime = CreateDiscoveryRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_WORKER_OPTIONS"] = "1"
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), p => p.Provider == "acp:custom");
        var small = Assert.Single(status.ModelCatalog!, m => m.Model == "small");
        Assert.Equal("normal", small.DefaultReasoningEffort);
        Assert.Equal(new[] { "normal", "deep" }, small.SupportedReasoningEfforts.Select(e => e.ReasoningEffort));
        File.Delete(log);
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:custom", model, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "deep"), CancellationToken.None);
        var deepRevision = session.ContextUsageRevision;
        Assert.NotNull(deepRevision);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "normal")));
        Assert.NotEqual(deepRevision, session.ContextUsageRevision);
        var normalRevision = session.ContextUsageRevision;
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "normal")));
        Assert.Equal(normalRevision, session.ContextUsageRevision);
        Assert.IsNotType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "unsupported")));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, null)));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.GetProperty("method").GetString() == "session/set_config_option" &&
                call.GetProperty("params").GetProperty("configId").GetString() == "worker-thought").ToArray();
        Assert.Equal(new[] { "deep", "normal" }, calls.Select(call => call.GetProperty("params").GetProperty("value").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThoughtLevelInitialDefaultEffortDoesNotSetModel(bool advertisedModel)
    {
        var log = Path.Combine(_fixture.RootDir, "thought-initial-default.jsonl");
        var models = advertisedModel
            ? "\"models\":{\"currentModelId\":\"small\",\"availableModels\":[{\"modelId\":\"small\"}]},"
            : "";
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_MODEL_ERROR"] = "-32602",
            ["MOCK_ACP_MODELS"] = "{" + models + """
                "configOptions":[{"id":"thought","category":"thought_level","type":"select",
                "currentValue":"normal","options":[{"value":"normal"},{"value":"deep"}]}]}
                """
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "deep"), CancellationToken.None);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "normal")));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, null)));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.DoesNotContain(calls, call => call.GetProperty("method").GetString() == "session/set_model");
        Assert.Equal(new[] { "deep", "normal" }, calls
            .Where(call => call.GetProperty("method").GetString() == "session/set_config_option")
            .Select(call => call.GetProperty("params").GetProperty("value").GetString()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ThoughtLevelResumeWithoutExplicitEffortCanSetModel(bool onOpen, bool configFallback)
    {
        var log = Path.Combine(_fixture.RootDir, "thought-resume-model.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_WORKER_OPTIONS"] = "1",
            ["MOCK_ACP_NO_SET_MODEL"] = configFallback ? "1" : "0"
        });
        var spec = new HarnessLaunchSpec("acp:opencode", "large", _fixture.WorkspaceDir, "resumed-session", null,
            new Dictionary<string, McpServerConfig>());
        if (onOpen)
        {
            var error = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(spec, default));
            Assert.Equal(ConfigurationDimension.Effort, Assert.IsType<SessionConfigurationOutcome.Unsupported>(error.Outcome).Dimension);
        }
        else
        {
            await using var session = await runtime.OpenSessionAsync(spec with { ReasoningEffort = "deep" }, default);
            var before = File.ReadAllLines(log).Length;
            Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(SessionConfiguration.FromNullable("large")));
            Assert.Equal(before, File.ReadAllLines(log).Length);
        }
    }

    [Theory]
    [InlineData("normal", false)]
    [InlineData("normal", true)]
    [InlineData("deep", false)]
    [InlineData("deep", true)]
    public async Task ThoughtLevelResumeExplicitEffortPreventsReset(string effort, bool onOpen)
    {
        var log = Path.Combine(_fixture.RootDir, "thought-resume-explicit.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_WORKER_OPTIONS"] = "1"
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", "large", _fixture.WorkspaceDir, "resumed-session", null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: onOpen ? effort : "deep"), CancellationToken.None);
        if (!onOpen) Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("large", effort)));
        var before = File.ReadAllLines(log).Length;
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, effort)));
        Assert.Equal(before, File.ReadAllLines(log).Length);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("small", effort)));
        before = File.ReadAllLines(log).Length;
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(SessionConfiguration.FromNullable("large")));
        Assert.Equal(before, File.ReadAllLines(log).Length);
    }

    [Fact]
    public async Task ThoughtLevelResumeCannotResetToResumedEffort()
    {
        var log = Path.Combine(_fixture.RootDir, "thought-resume.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_WORKER_OPTIONS"] = "1"
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", "large", _fixture.WorkspaceDir, "resumed-session", null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "deep"), CancellationToken.None);
        var before = File.ReadAllLines(log).Length;
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(SessionConfiguration.FromNullable("large")));
        Assert.Equal(before, File.ReadAllLines(log).Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ThoughtLevelModelSwitchReappliesEffort(bool returnsOptions, bool configFallback)
    {
        var log = Path.Combine(_fixture.RootDir, "thought-model-reset.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_WORKER_OPTIONS"] = "1",
            ["MOCK_ACP_RESET_MODEL_EFFORT"] = "1",
            ["MOCK_ACP_MODEL_RESPONSE_OPTIONS"] = returnsOptions ? "1" : "0",
            ["MOCK_ACP_NO_SET_MODEL"] = configFallback ? "1" : "0"
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "deep"), CancellationToken.None);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("large", "deep")));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line));
        Assert.Equal(new[] { "deep", "deep" }, calls
            .Where(call => call.GetProperty("method").GetString() == "session/set_config_option" &&
                call.GetProperty("params").GetProperty("configId").GetString() == "worker-thought")
            .Select(call => call.GetProperty("params").GetProperty("value").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThoughtLevelDefaultModelRestoresAfterExplicitSelection(bool configFallback)
    {
        var log = Path.Combine(_fixture.RootDir, "thought-default-model.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_WORKER_OPTIONS"] = "1",
            ["MOCK_ACP_NO_SET_MODEL"] = configFallback ? "1" : "0"
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", "large", _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "deep"), CancellationToken.None);

        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("  ", "normal")));

        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.GetProperty("method").GetString() ==
                (configFallback ? "session/set_config_option" : "session/set_model"))
            .Select(call => call.GetProperty("params"))
            .Where(parameters => !configFallback || parameters.GetProperty("configId").GetString() == "model");
        Assert.Equal(new[] { "large", "small" },
            calls.Select(parameters => parameters.GetProperty(configFallback ? "value" : "modelId").GetString()));
    }

    [Fact]
    public async Task ThoughtLevelUnknownDefaultModelRequiresReopen()
    {
        var log = Path.Combine(_fixture.RootDir, "thought-unknown-default.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_MODELS"] = """
                {"configOptions":[{"id":"thought","category":"thought_level","type":"select",
                  "currentValue":"normal","options":[{"value":"normal"},{"value":"deep"}]}]}
                """
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", "large", _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "deep"), CancellationToken.None);
        var before = File.ReadAllLines(log).Length;
        Assert.IsNotType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "normal")));
        Assert.Equal(before, File.ReadAllLines(log).Length);
    }

    [Theory]
    [InlineData("small", null)]
    [InlineData("large", "  ")]
    public async Task ThoughtLevelBlankEffortRestoresInitialDefault(string model, string? effort)
    {
        var log = Path.Combine(_fixture.RootDir, "thought-default-effort.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_WORKER_OPTIONS"] = "1"
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", "small", _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>()), CancellationToken.None);
        Assert.DoesNotContain(File.ReadAllLines(log), line => line.Contains("worker-thought", StringComparison.Ordinal));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("small", "deep")));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(model, effort)));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(model, null)));

        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.GetProperty("method").GetString() == "session/set_config_option" &&
                call.GetProperty("params").GetProperty("configId").GetString() == "worker-thought");
        Assert.Equal(new[] { "deep", "normal" },
            calls.Select(call => call.GetProperty("params").GetProperty("value").GetString()));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, true)]
    public async Task DefaultModelEffortKeepsConfirmedIdleUsageRevision(bool thoughtLevel, bool configFallback, bool switchModel, bool modelUnavailable)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_WORKER_OPTIONS"] = thoughtLevel ? "1" : "0",
            ["MOCK_ACP_NO_SET_MODEL"] = configFallback ? "1" : "0",
            ["MOCK_ACP_MODEL_ERROR"] = modelUnavailable ? "-32601" : "0",
            ["MOCK_ACP_POST_SESSION_DELAY_MS"] = "300",
            ["MOCK_ACP_POST_SESSION_USAGE"] = """{"used":100,"size":200000}"""
        });
        if (modelUnavailable && !thoughtLevel)
        {
            await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(new HarnessLaunchSpec(
                "acp:opencode", null, _fixture.WorkspaceDir, null, null,
                new Dictionary<string, McpServerConfig>(), ReasoningEffort: "medium"), default));
            return;
        }
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: thoughtLevel ? "deep" : modelUnavailable ? "medium" : "high"), CancellationToken.None);
        if (switchModel)
        {
            Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(thoughtLevel ? "large" : "gemini-4-carbon", thoughtLevel ? "deep" : "high")));
            Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("  ", thoughtLevel ? "normal" : "medium")));
        }

        var received = new TaskCompletionSource<IdleContextUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = session.AttachIdleContextUsageHandler(usage => received.TrySetResult(usage));
        foreach (var buffered in pending) received.TrySetResult(buffered);
        var usage = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(session.ContextUsageRevision);
        Assert.Equal(session.ContextUsageRevision, usage.ContextUsageRevision);
        Assert.Equal(100, usage.Usage.EffectiveUsedTokens);
    }

    [Fact]
    public async Task ModelSelectionPropagatesErrorsOtherThanMethodNotFound()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:custom"] = new(_fixture.MockAcpPath, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_MODEL_ERROR"] = "-32602"
                })
            }
        });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:custom", "invalid-model", _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>()), CancellationToken.None));
        Assert.Contains("model switch failed", error.Message);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("loaded-session", 0)]
    [InlineData(null, -32601)]
    [InlineData("loaded-session", -32601)]
    public async Task ModelSelectionFollowsSessionBinding(string? resumeId, int error)
    {
        var log = Path.Combine(_fixture.RootDir, "rpc.jsonl");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:custom"] = new(_fixture.MockAcpPath, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_LOG"] = log,
                    ["MOCK_ACP_MODEL_ERROR"] = error.ToString()
                })
            }
        });
        var spec = new HarnessLaunchSpec("acp:custom", "selected-model", _fixture.WorkspaceDir, resumeId, null,
            new Dictionary<string, McpServerConfig>());
        if (error != 0)
        {
            await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(spec, default));
            Assert.DoesNotContain(File.ReadAllLines(log), line => line.Contains("session/prompt", StringComparison.Ordinal));
            return;
        }
        await using var session = await runtime.OpenSessionAsync(spec, default);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("another-model")));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Equal(new[] { "initialize", resumeId is null ? "session/new" : "session/load", "session/set_model", "session/set_model" },
            calls.Select(call => call.GetProperty("method").GetString()));
        Assert.False(calls[1].GetProperty("params").TryGetProperty("model", out _));
        Assert.Equal("selected-model", calls[2].GetProperty("params").GetProperty("modelId").GetString());
        if (resumeId is not null)
            Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerSettingsUseNegotiatedOptionsAndRequireAcknowledgmentBeforePrompt(bool rejectsEffort)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_WORKER_OPTIONS"] = "1",
            ["MOCK_ACP_REJECT_WORKER_EFFORT"] = rejectsEffort ? "1" : "0"
        });
        var spec = new HarnessLaunchSpec("acp:opencode", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers(), ConfigureWorker: true);
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.Equal("small", session.WorkerConfiguration!.DefaultModel);
        Assert.Equal("normal", session.WorkerConfiguration.DefaultReasoningEffort);
        Assert.Equal(["normal", "deep"], session.WorkerConfiguration.GetReasoningEfforts("large"));

        var events = await DrainTurnAsync(session, new TurnInput("process request", "acp-worker-config", "large", "deep"));
        if (rejectsEffort)
        {
            Assert.Contains(events, item => item is UnifiedAgentEvent.TurnError { BeforePrompt: true });
            Assert.DoesNotContain(events, item => item is UnifiedAgentEvent.TextDelta or UnifiedAgentEvent.TurnCompleted);
        }
        else
        {
            var output = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(item => item.Delta));
            Assert.Contains("model=large", output, StringComparison.Ordinal);
            Assert.Contains("effort=deep", output, StringComparison.Ordinal);
            Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
        }
    }

    [Fact]
    public async Task WorkerWithLegacyAcpModelKeepsItsExplicitLaunchModel()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec("acp:opencode", "gemini-4-carbon-high", _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers(), ConfigureWorker: true);
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.Null(session.WorkerConfiguration);

        var events = await DrainTurnAsync(session,
            new TurnInput("use launch model", "acp-worker-launch", spec.Model));
        var output = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(item => item.Delta));
        Assert.Contains("model=gemini-4-carbon-high|via=session/set_model", output, StringComparison.Ordinal);
        Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
    }

    [Fact]
    public async Task WorkerResumeUsesTurnSelectionWhenOriginalLaunchModelIsGone()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string> { ["MOCK_ACP_WORKER_OPTIONS"] = "1" });
        var spec = new HarnessLaunchSpec("acp:opencode", "small", _fixture.WorkspaceDir, "stored-session", null,
            MockCliFixture.CreateSampleMcpServers(), ConfigureWorker: true);
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.Equal("large", Assert.Single(session.WorkerConfiguration!.Models).Id);
        Assert.Null(session.WorkerConfiguration.DefaultModel);

        var events = await DrainTurnAsync(session, new TurnInput("continue Worker", "acp-worker-resume", "large", "deep"));
        var output = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(item => item.Delta));
        Assert.Contains("model=large", output, StringComparison.Ordinal);
        Assert.Contains("effort=deep", output, StringComparison.Ordinal);
        Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
    }

    [Theory]
    [InlineData("acp:antigravity", true)]
    [InlineData("acp:opencode", true)]
    [InlineData("antigravity", true)]
    [InlineData("opencode", true)]
    public async Task AcpBuiltInAndNormalizedSurfaces_SessionNewAndLoad_SetModel_SystemPromptAndUpdates(
        string provider,
        bool expectsAcpSubcommand)
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec(
            Provider: provider,
            Model: "gemini-4-carbon-medium",
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: "ACP system prompt",
            McpServers: MockCliFixture.CreateSampleMcpServers());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.False(session.SupportsNativeSteer);
        Assert.Equal("acp_sess_001", session.NativeThreadId);

        var events = await DrainTurnAsync(session, new TurnInput("hello-acp", "acp_corr_1"));
        Assert.Contains(events, e => e is UnifiedAgentEvent.ThreadBound { NativeThreadId: "acp_sess_001" });
        Assert.Contains(events, e => e is UnifiedAgentEvent.ThinkingDelta { Delta: "acp-thought-1" });

        var toolStarted = Assert.Single(events.OfType<UnifiedAgentEvent.ToolCallStarted>());
        Assert.Equal("vcs_status", toolStarted.Name);

        var toolCompleted = Assert.Single(events.OfType<UnifiedAgentEvent.ToolCallCompleted>());
        Assert.Equal(toolStarted.CallId, toolCompleted.CallId);
        Assert.Equal("clean-working-copy", toolCompleted.Output);
        Assert.False(toolCompleted.IsError);

        var text = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        var expectedPrefix =
            $"acp[session/new|acp_sess_001|sub={expectsAcpSubcommand}|model=gemini-4-carbon-medium|via=session/set_model|mcp=local-tools,perga]:" +
            "<system_instructions>\nACP system prompt\n</system_instructions>\n\nhello-acp";
        Assert.Contains(expectedPrefix, text, StringComparison.Ordinal);

        var done = Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.NotNull(done.Usage);
        Assert.Equal(200, done.Usage!.InputTokens);
        Assert.Equal(75, done.Usage.OutputTokens);
        Assert.Equal(40, done.Usage.CachedInputTokens);
        Assert.Equal(1048576, done.Usage.ContextWindowTokens);

        // Turn 2 on same session does NOT duplicate <system_instructions>
        var turn2Events = await DrainTurnAsync(session, new TurnInput("second-acp-turn", "acp_corr_1b"));
        var turn2Text = string.Concat(turn2Events.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.DoesNotContain("<system_instructions>", turn2Text, StringComparison.Ordinal);
        Assert.EndsWith(":second-acp-turn", turn2Text, StringComparison.Ordinal);

        // Resume via session/load does NOT prepend <system_instructions>
        var resumeSpec = spec with { ResumeNativeThreadId = "acp_loaded_88" };
        await using var loadedSession = await runtime.OpenSessionAsync(resumeSpec, CancellationToken.None);
        Assert.Equal("acp_loaded_88", loadedSession.NativeThreadId);
        var loadedEvents = await DrainTurnAsync(loadedSession, new TurnInput("resumed-prompt", "acp_corr_2"));
        var loadedText = string.Concat(loadedEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("acp[session/load|acp_loaded_88", loadedText, StringComparison.Ordinal);
        Assert.DoesNotContain("<system_instructions>", loadedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcpRestoreUsesNegotiatedResumeAndRejectsUnsupportedProviders(bool supportsResume)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_DISABLE_LOAD"] = "1",
            ["MOCK_ACP_RESUME"] = supportsResume ? "1" : "0",
        });
        var spec = new HarnessLaunchSpec("acp:antigravity", "saved-model", _fixture.WorkspaceDir, "stored-session", null,
            MockCliFixture.CreateSampleMcpServers());
        if (!supportsResume)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => runtime.OpenSessionAsync(spec, CancellationToken.None));
            return;
        }

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.Equal("stored-session", session.NativeThreadId);
        var events = await DrainTurnAsync(session, new TurnInput("next", "resume-turn"));
        Assert.Contains(events, item => item is UnifiedAgentEvent.TextDelta text &&
            text.Delta.Contains("session/resume|stored-session", StringComparison.Ordinal));
        Assert.True(session.SupportsScopedClose);
        Assert.True(await session.TryCloseSessionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcpMalformedRecoveryRepliesDoNotConfirmResumeOrClose()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_RESUME"] = "1",
            ["MOCK_ACP_INVALID_RESUME"] = "1",
            ["MOCK_ACP_INVALID_CLOSE"] = "1",
        });
        var spec = new HarnessLaunchSpec("acp:antigravity", null, _fixture.WorkspaceDir, "stored-session", null,
            MockCliFixture.CreateSampleMcpServers());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.OpenSessionAsync(spec, CancellationToken.None));

        await using var session = await runtime.OpenSessionAsync(spec with { ResumeNativeThreadId = null }, CancellationToken.None);
        Assert.True(session.SupportsScopedClose);
        Assert.False(await session.TryCloseSessionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcpSetModel_FallsBackToSetConfigOptionWhenSetModelReturnsMethodNotFound()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_NO_SET_MODEL"] = "1"
        });

        var spec = new HarnessLaunchSpec(
            Provider: "acp:antigravity",
            Model: "claude-sonnet-5-5-medium",
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: new Dictionary<string, McpServerConfig>());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        var events = await DrainTurnAsync(session, new TurnInput("check-fallback-model", "corr_cfg"));
        var text = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("model=claude-sonnet-5-5-medium|via=session/set_config_option", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcpWithoutHttpMcpCapability_BridgesHttpMcpToLocalEndpointViaStdioBypassingHttpProxy()
    {
        // Spin up a real local Streamable HTTP MCP server on 127.0.0.1:0
        await using var mcpServer = new LocalStreamableHttpMcpTestServer("Bearer pg_live_bridge_token");

        var acpDriverWithMcpExec = _fixture.WriteExecutable(
            "acp_stdio_mcp_consumer.py",
            """
            #!/usr/bin/env python3
            import json, os, subprocess, sys

            def send(obj):
                sys.stdout.write(json.dumps(obj) + "\n")
                sys.stdout.flush()

            mcp_servers = []
            for raw in sys.stdin:
                line = raw.strip()
                if not line:
                    continue
                msg = json.loads(line)
                method = msg.get("method")
                req_id = msg.get("id")
                params = msg.get("params") or {}

                if method == "initialize":
                    http_cap = os.environ.get("TEST_ACP_HTTP_CAP") == "1"
                    mcp_caps = {"http": True} if http_cap else {}
                    send({
                        "jsonrpc": "2.0",
                        "id": req_id,
                        "result": {
                            "protocolVersion": 1,
                            "agentCapabilities": {"loadSession": True, "mcpCapabilities": mcp_caps}
                        }
                    })
                elif method == "session/new":
                    mcp_servers = params.get("mcpServers") or []
                    send({"jsonrpc": "2.0", "id": req_id, "result": {"sessionId": "acp_bridge_sess"}})
                elif method in ("session/set_model", "session/set_config_option"):
                    send({"jsonrpc": "2.0", "id": req_id, "result": {}})
                elif method == "session/prompt":
                    srv = next(s for s in mcp_servers if s.get("name") == "perga")
                    if srv.get("type") == "http":
                        send({
                            "jsonrpc": "2.0",
                            "method": "session/update",
                            "params": {
                                "sessionId": "acp_bridge_sess",
                                "update": {"sessionUpdate": "agent_message_chunk", "content": f"direct-http:{srv.get('url')}"}
                            }
                        })
                        send({"jsonrpc": "2.0", "id": req_id, "result": {"stopReason": "end_turn"}})
                        continue

                    child_env = dict(os.environ)
                    for pair in srv.get("env") or []:
                        child_env[pair["name"]] = pair["value"]

                    proc = subprocess.Popen(
                        [srv["command"]] + list(srv.get("args") or []),
                        stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE,
                        env=child_env,
                        text=True
                    )
                    try:
                        # 1. MCP initialize (JSON response + Mcp-Session-Id)
                        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-03-26"}}) + "\n")
                        proc.stdin.flush()
                        init_resp = json.loads(proc.stdout.readline())

                        # 2. MCP notifications/initialized (202 Accepted empty body -> no stdout line)
                        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
                        proc.stdin.flush()

                        # 3. MCP tools/list (JSON response)
                        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}) + "\n")
                        proc.stdin.flush()
                        list_resp = json.loads(proc.stdout.readline())
                        tool_name = list_resp["result"]["tools"][0]["name"]

                        # 4. MCP tools/call (SSE text/event-stream data: response!)
                        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": tool_name, "arguments": {"whole": True}}}) + "\n")
                        proc.stdin.flush()
                        call_resp = json.loads(proc.stdout.readline())
                        tool_text = call_resp["result"]["content"][0]["text"]
                    finally:
                        proc.stdin.close()
                        proc.wait(timeout=5)

                    summary = f"bridged-ok|srv={init_resp['result']['serverInfo']['name']}|tool={tool_name}|out={tool_text}|hasType={'type' in srv}"
                    send({
                        "jsonrpc": "2.0",
                        "method": "session/update",
                        "params": {
                            "sessionId": "acp_bridge_sess",
                            "update": {"sessionUpdate": "agent_message_chunk", "content": summary}
                        }
                    })
                    send({"jsonrpc": "2.0", "id": req_id, "result": {"stopReason": "end_turn"}})
            """);

        // Deliberately inject a dead http_proxy (simulating agy's http_proxy=http://127.0.0.1:2080)
        // to prove NO_PROXY=127.0.0.1,localhost + direct Node HTTP connects directly to local Perga MCP!
        await using var bridgedRuntime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:antigravity"] = new(
                    acpDriverWithMcpExec,
                    Env: new Dictionary<string, string>
                    {
                        ["http_proxy"] = "http://127.0.0.1:1",
                        ["HTTP_PROXY"] = "http://127.0.0.1:1"
                    }),
                ["acp:opencode"] = new(
                    acpDriverWithMcpExec,
                    Env: new Dictionary<string, string>
                    {
                        ["TEST_ACP_HTTP_CAP"] = "1"
                    })
            }
        });

        var mcpServers = new Dictionary<string, McpServerConfig>
        {
            ["perga"] = new McpServerConfig.HttpMcpServer(
                mcpServer.EndpointUrl,
                new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer pg_live_bridge_token"
                })
        };

        // Case 1: mcpCapabilities: {} -> automatic stdio-to-HTTP bridge
        await using var bridgedSession = await bridgedRuntime.OpenSessionAsync(
            new HarnessLaunchSpec("acp:antigravity", null, _fixture.WorkspaceDir, null, null, mcpServers),
            CancellationToken.None);

        var bridgedEvents = await DrainTurnAsync(bridgedSession, new TurnInput("run-mcp", "corr_mcp_1"));
        var bridgedText = string.Concat(bridgedEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Equal("bridged-ok|srv=perga-kernel-test|tool=perga_report_read|out=sse-report-body-ok|hasType=False", bridgedText);
        Assert.True(mcpServer.SawInitialize);
        Assert.True(mcpServer.SawInitializedNotification);
        Assert.True(mcpServer.SawSessionHeaderOnSubsequentCalls);

        // Case 2: mcpCapabilities: { http: true } -> direct HTTP entry preserved
        await using var directHttpSession = await bridgedRuntime.OpenSessionAsync(
            new HarnessLaunchSpec("acp:opencode", null, _fixture.WorkspaceDir, null, null, mcpServers),
            CancellationToken.None);

        var directEvents = await DrainTurnAsync(directHttpSession, new TurnInput("run-direct", "corr_mcp_2"));
        var directText = string.Concat(directEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Equal($"direct-http:{mcpServer.EndpointUrl}", directText);
    }

    [Fact]
    public async Task AcpGenericExecutableSurface_PermissionRequestCancelAndRpcError()
    {
        await using var runtime = new AgentRuntime();
        var spec = new HarnessLaunchSpec(
            Provider: $"acp:{_fixture.MockAcpPath}",
            Model: null,
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: new Dictionary<string, McpServerConfig>());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        // Permission allow selects allow_once optionId
        var allowEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_PERMISSION", "perm_1"), CancellationToken.None))
        {
            allowEvents.Add(evt);
            if (evt is UnifiedAgentEvent.ApprovalRequested req)
            {
                Assert.StartsWith("permission_", req.RequestId, StringComparison.Ordinal);
                Assert.Equal("write_file", req.ToolName);
                Assert.NotNull(req.Options);
                Assert.Equal(3, req.Options!.Count);
                Assert.Equal(new ApprovalOption("opt_allow_once", "Allow once", true), req.Options[0]);
                Assert.Equal(new ApprovalOption("opt_allow_always", "Always allow", true), req.Options[1]);
                Assert.Equal(new ApprovalOption("opt_reject_once", "Reject once", false), req.Options[2]);
                await session.RespondApprovalAsync(req.RequestId, approved: true, ct: CancellationToken.None);
            }
        }

        Assert.Contains(allowEvents.OfType<UnifiedAgentEvent.TextDelta>(), t => t.Delta == "acp-selected:opt_allow_once");

        // Permission allow_always selects opt_allow_always when explicitly chosen
        var alwaysEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_PERMISSION", "perm_always"), CancellationToken.None))
        {
            alwaysEvents.Add(evt);
            if (evt is UnifiedAgentEvent.ApprovalRequested req)
            {
                await session.RespondApprovalAsync(
                    req.RequestId,
                    approved: true,
                    selectedOptionId: "opt_allow_always",
                    ct: CancellationToken.None);
            }
        }

        Assert.Contains(alwaysEvents.OfType<UnifiedAgentEvent.TextDelta>(), t => t.Delta == "acp-selected:opt_allow_always");

        // Permission reject selects reject_once optionId
        var rejectEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_PERMISSION", "perm_2"), CancellationToken.None))
        {
            rejectEvents.Add(evt);
            if (evt is UnifiedAgentEvent.ApprovalRequested req)
            {
                await session.RespondApprovalAsync(req.RequestId, approved: false, reason: "denied", ct: CancellationToken.None);
            }
        }

        Assert.Contains(rejectEvents.OfType<UnifiedAgentEvent.TextDelta>(), t => t.Delta == "acp-selected:opt_reject_once");

        // session/cancel during pending session/request_permission replies { outcome: { outcome: "cancelled" } }
        var cancelPermEvents = new List<UnifiedAgentEvent>();
        var permCancelSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_PERMISSION_CANCEL", "perm_cancel_1"), CancellationToken.None))
        {
            cancelPermEvents.Add(evt);
            if (!permCancelSent && evt is UnifiedAgentEvent.ApprovalRequested)
            {
                permCancelSent = true;
                await session.CancelTurnAsync(CancellationToken.None);
            }
        }

        Assert.True(permCancelSent);
        Assert.Single(cancelPermEvents.OfType<UnifiedAgentEvent.TurnInterrupted>());

        // session/request_permission arriving after session/cancel auto-replies cancelled without emitting ApprovalRequested
        var postCancelPermEvents = new List<UnifiedAgentEvent>();
        var postCancelSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_PERMISSION_AFTER_CANCEL", "perm_after_cancel_1"), CancellationToken.None))
        {
            postCancelPermEvents.Add(evt);
            if (!postCancelSent && evt is UnifiedAgentEvent.TextDelta)
            {
                postCancelSent = true;
                await session.CancelTurnAsync(CancellationToken.None);
            }
        }

        Assert.True(postCancelSent);
        Assert.Empty(postCancelPermEvents.OfType<UnifiedAgentEvent.ApprovalRequested>());
        Assert.Single(postCancelPermEvents.OfType<UnifiedAgentEvent.TurnInterrupted>());

        // session/cancel stops active prompt cleanly
        var cancelEvents = new List<UnifiedAgentEvent>();
        var cancelSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("WAIT_FOR_CANCEL", "cancel_1"), CancellationToken.None))
        {
            cancelEvents.Add(evt);
            if (!cancelSent && evt is UnifiedAgentEvent.TextDelta)
            {
                cancelSent = true;
                await session.CancelTurnAsync(CancellationToken.None);
            }
        }

        Assert.Single(cancelEvents.OfType<UnifiedAgentEvent.TurnInterrupted>());

        // JSON-RPC error surfaces TurnError with IsRetryable = true on 429
        var rpcErrEvents = await DrainTurnAsync(session, new TurnInput("TRIGGER_RPC_ERROR", "err_1"));
        var rpcErr = Assert.Single(rpcErrEvents.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Equal("rpc_429", rpcErr.Code);
        Assert.True(rpcErr.IsRetryable);
    }

    [Fact]
    public async Task AcpNativeCompact_UsesAvailableCommandsAndUsageUpdate()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["acp:antigravity"] = new ProviderCommandOverride(
                    "python3",
                    PrependArgs: [_fixture.MockAcpPath],
                    Env: new Dictionary<string, string> { ["MOCK_ACP_ADVERTISE_COMPACT"] = "1" })
            }
        });

        var spec = new HarnessLaunchSpec(
            "acp:antigravity",
            null,
            _fixture.WorkspaceDir,
            null,
            null,
            MockCliFixture.CreateSampleMcpServers());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        // Give the background notification reader a brief moment to process available_commands_update.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!session.SupportsNativeCompact && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(session.SupportsNativeCompact);
        var result = await session.CompactAsync(CancellationToken.None);
        Assert.NotNull(result.Usage);
        Assert.Equal(2048, result.Usage!.InputTokens);
        Assert.Equal(1_048_576, result.Usage.ContextWindowTokens);
    }

    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(true, false, "none")]
    [InlineData(true, true, "none")]
    [InlineData(true, false, "notification")]
    [InlineData(true, false, "response")]
    public async Task AcpNativeCompact_OnlyReturnsUsageFromCompaction(bool switchModel, bool configFallback, string usageMode)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_ADVERTISE_COMPACT"] = "1",
            ["MOCK_ACP_NO_SET_MODEL"] = configFallback ? "1" : "0",
            ["MOCK_ACP_COMPACT_USAGE"] = usageMode
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);
        var sampled = await DrainTurnAsync(session, new TurnInput("CONTEXT_ACP_USED", "sample"));
        Assert.NotNull(Assert.Single(sampled.OfType<UnifiedAgentEvent.TurnCompleted>()).Usage);
        if (switchModel)
            Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("next-model")));

        Assert.True(session.SupportsNativeCompact);
        var compacted = await session.CompactAsync(CancellationToken.None);
        if (usageMode == "none")
            Assert.Null(compacted.Usage);
        else
        {
            Assert.NotNull(compacted.Usage);
            Assert.Equal(2048, compacted.Usage.InputTokens);
            Assert.Equal(1_048_576, compacted.Usage.ContextWindowTokens);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("empty_then_compact")]
    public async Task AcpNativeCompact_DelayedAvailableCommandsAndIdleUsageAfterResume_ResolvesAndCaptures(string advertiseMode)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_RESUME"] = "1",
            ["MOCK_ACP_ADVERTISE_COMPACT"] = advertiseMode,
            ["MOCK_ACP_POST_SESSION_DELAY_MS"] = "60",
            ["MOCK_ACP_POST_SESSION_USAGE"] = "{\"used\": 0, \"size\": 1048576}"
        });

        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity",
            "gemini-4-carbon-high",
            _fixture.WorkspaceDir,
            "acp_sess_resumed",
            null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);

        var idleTcs = new TaskCompletionSource<IdleContextUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = session.AttachIdleContextUsageHandler(u => idleTcs.TrySetResult(u));
        foreach (var usage in pending)
            idleTcs.TrySetResult(usage);

        Assert.True(await session.ResolveNativeCompactSupportAsync(CancellationToken.None));
        var idleUsage = await idleTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, idleUsage.Usage.EffectiveUsedTokens);
        Assert.Equal(1_048_576, idleUsage.Usage.ContextWindowTokens);
        Assert.Equal(session.ContextUsageRevision, idleUsage.ContextUsageRevision);
        Assert.Equal("acp_sess_resumed", idleUsage.NativeThreadId);

        var compacted = await session.CompactAsync(CancellationToken.None);
        Assert.NotNull(compacted.Usage);
        Assert.Equal(2048, compacted.Usage!.InputTokens);
        Assert.Equal(1_048_576, compacted.Usage.ContextWindowTokens);
    }

    [Theory]
    [InlineData("no_compact")]
    [InlineData("0")]
    public async Task AcpNativeCompact_WithoutCompactAdvertisement_ReturnsFalseAndThrowsOnCompact(string advertiseMode)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_RESUME"] = "1",
            ["MOCK_ACP_ADVERTISE_COMPACT"] = advertiseMode,
            ["MOCK_ACP_POST_SESSION_DELAY_MS"] = advertiseMode == "no_compact" ? "40" : "0"
        });

        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity",
            "saved-model",
            _fixture.WorkspaceDir,
            "acp_sess_no_compact",
            null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);

        Assert.False(await session.ResolveNativeCompactSupportAsync(CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => session.CompactAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"used\": -1, \"size\": 1048576}")]
    [InlineData("{\"used\": 0, \"size\": 0}")]
    public async Task AcpIdleUsageUpdate_RejectsInvalidPayloadsAndTagsModelSwitches(string invalidPayload)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_POST_SESSION_USAGE"] = invalidPayload,
            ["MOCK_ACP_MODEL_SWITCH_USAGE"] = "{\"used\": 100, \"size\": 200000}"
        });

        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity",
            null,
            _fixture.WorkspaceDir,
            null,
            null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);

        var received = new List<IdleContextUsage>();
        var switchedTcs = new TaskCompletionSource<IdleContextUsage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = session.AttachIdleContextUsageHandler(u =>
        {
            lock (received)
            {
                received.Add(u);
            }

            switchedTcs.TrySetResult(u);
        });
        lock (received)
        {
            received.AddRange(pending);
        }

        // Wait briefly to confirm invalid post-session usage was not recorded.
        await Task.Delay(80);
        lock (received)
        {
            Assert.Empty(received);
        }

        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("gemini-4-carbon-high")));
        var switched = await switchedTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(session.ContextUsageRevision, switched.ContextUsageRevision);
        Assert.Equal(100, switched.Usage.EffectiveUsedTokens);
        Assert.Equal(200_000, switched.Usage.ContextWindowTokens);
    }

    [Theory]
    [InlineData("{\"used\": 0, \"size\": 0}")]
    [InlineData("{\"used\": 0, \"size\": -1}")]
    [InlineData("{\"used\": 0, \"size\": null}")]
    [InlineData("{\"used\": 0, \"size\": \"1000\"}")]
    [InlineData("{\"used\": 0, \"size\": 9223372036854775808}")]
    public async Task AcpIdleUsageUpdate_RejectsInvalidExplicitWindowAfterValidReading(string invalidSecondPayload)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_POST_SESSION_USAGE"] = $"[{{\"used\": 100, \"size\": 1000}}, {invalidSecondPayload}]"
        });

        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity",
            null,
            _fixture.WorkspaceDir,
            null,
            null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);

        var received = new List<IdleContextUsage>();
        var pending = session.AttachIdleContextUsageHandler(u =>
        {
            lock (received)
            {
                received.Add(u);
            }
        });
        lock (received)
        {
            received.AddRange(pending);
        }

        await Task.Delay(80);
        lock (received)
        {
            var only = Assert.Single(received);
            Assert.Equal(100, only.Usage.EffectiveUsedTokens);
            Assert.Equal(1000, only.Usage.ContextWindowTokens);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcpIdleUsageUpdate_PreservesImmediateUsageWhenSessionResponseMatchesRequestedModel(bool resume)
    {
        var modelsJson = "{\"models\":{\"currentModelId\":\"model-a\",\"availableModels\":[{\"modelId\":\"model-a\",\"name\":\"Model A\"},{\"modelId\":\"model-b\",\"name\":\"Model B\"}]}}";
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_RESUME"] = "1",
            ["MOCK_ACP_MODELS"] = modelsJson,
            ["MOCK_ACP_POST_SESSION_DELAY_MS"] = "0",
            ["MOCK_ACP_POST_SESSION_USAGE"] = "{\"used\": 123, \"size\": 1000}"
        });

        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity",
            "model-a",
            _fixture.WorkspaceDir,
            resume ? "acp_sess_model_match" : null,
            null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);

        var received = new List<IdleContextUsage>();
        var pending = session.AttachIdleContextUsageHandler(u =>
        {
            lock (received)
            {
                received.Add(u);
            }
        });
        lock (received)
        {
            received.AddRange(pending);
        }

        var item = Assert.Single(received);
        Assert.Equal(session.ContextUsageRevision, item.ContextUsageRevision);
        Assert.Equal(123, item.Usage.EffectiveUsedTokens);
        Assert.Equal(1000, item.Usage.ContextWindowTokens);
    }

    [Fact]
    public async Task AcpIdleUsageUpdate_DiscardsImmediatePreSwitchUsageWhenSessionResponseReportsDifferentModel()
    {
        var modelsJson = "{\"models\":{\"currentModelId\":\"model-a\",\"availableModels\":[{\"modelId\":\"model-a\",\"name\":\"Model A\"},{\"modelId\":\"model-b\",\"name\":\"Model B\"}]}}";
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_RESUME"] = "1",
            ["MOCK_ACP_MODELS"] = modelsJson,
            ["MOCK_ACP_POST_SESSION_DELAY_MS"] = "0",
            ["MOCK_ACP_POST_SESSION_USAGE"] = "{\"used\": 999, \"size\": 500000}"
        });

        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity",
            "model-b",
            _fixture.WorkspaceDir,
            "acp_sess_model_mismatch",
            null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);

        var received = new List<IdleContextUsage>();
        var pending = session.AttachIdleContextUsageHandler(u =>
        {
            lock (received)
            {
                received.Add(u);
            }
        });
        lock (received)
        {
            received.AddRange(pending);
        }

        Assert.Empty(received);
    }

    [Fact]
    public async Task AcpNativeCompact_GraceWaitHonorsCancellationAndDisconnect()
    {
        await using var cancelRuntime = CreateRuntime(new Dictionary<string, string>());
        await using var cancelSession = await cancelRuntime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity",
            null,
            _fixture.WorkspaceDir,
            null,
            null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelSession.ResolveNativeCompactSupportAsync(cts.Token).AsTask());

        await using var exitRuntime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_EXIT_AFTER_SESSION_MS"] = "40"
        });
        await using var exitSession = await exitRuntime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity",
            null,
            _fixture.WorkspaceDir,
            null,
            null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await exitSession.ResolveNativeCompactSupportAsync(CancellationToken.None));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500), $"Expected disconnect to wake early, took {sw.Elapsed}.");
    }

    [Fact]
    public async Task AcpIdleUsageUpdate_ResumeWithoutCurrentModelId_DiscardsUnconfirmedUsage()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_RESUME"] = "1",
            ["MOCK_ACP_POST_SESSION_USAGE"] = """{"used": 123, "size": 1000}"""
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", "model-a", _fixture.WorkspaceDir, "acp_sess_resume_no_models", null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);

        Assert.Empty(session.AttachIdleContextUsageHandler(_ => { }));
        Assert.NotNull(session.ContextUsageRevision);
    }

    [Fact]
    public async Task AcpTurnUsage_ChangesBindingWhenEffortSelectsAnotherProviderModelId()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_STRICT_MODELS"] = "1",
            ["MOCK_ACP_MODELS"] = """
                {"models":{"currentModelId":"vendor/model-low","availableModels":[{"modelId":"vendor/model-low"},{"modelId":"vendor/model-high"}]}}
                """
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", "vendor/model", _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers(), ReasoningEffort: "low"), CancellationToken.None);

        var first = await DrainTurnAsync(session, new TurnInput("CONTEXT_ACP_USED", "first"));
        var original = Assert.Single(first.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.Equal(1_048_576, original.Usage!.ContextWindowTokens);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable("vendor/model", "high")));
        var second = await DrainTurnAsync(session, new TurnInput("ACP_NO_SIZE_USAGE", "second"));
        var completed = Assert.Single(second.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.Null(completed.Usage!.ContextWindowTokens);
        Assert.NotEqual(original.ContextUsageRevision, completed.ContextUsageRevision);
        Assert.Equal(session.ContextUsageRevision, completed.ContextUsageRevision);
    }

    [Fact]
    public async Task AcpTurnUsage_ReportsCompactResponseWindowWithoutFillingLaterTurn()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_ADVERTISE_COMPACT"] = "1",
            ["MOCK_ACP_COMPACT_USAGE"] = "response",
            ["MOCK_ACP_PROMPT_WINDOW"] = "1000"
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers()), CancellationToken.None);
        var first = await DrainTurnAsync(session, new TurnInput("CONTEXT_ACP_USED", "first"));
        var original = Assert.Single(first.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.Equal(1000, original.Usage!.ContextWindowTokens);

        var compacted = await session.CompactAsync(CancellationToken.None);
        Assert.Equal(1_048_576, compacted.Usage!.ContextWindowTokens);
        var next = await DrainTurnAsync(session, new TurnInput("ACP_NO_SIZE_USAGE", "next"));
        var completed = Assert.Single(next.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.Null(completed.Usage!.ContextWindowTokens);
        Assert.Equal(compacted.ContextUsageRevision, completed.ContextUsageRevision);
    }

    [Fact]
    public async Task AcpTurnUsage_ChangesBindingWhenWorkerAppliesNewModel()
    {
        var log = Path.Combine(_fixture.RootDir, "review-worker.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_WORKER_OPTIONS"] = "1",
            ["MOCK_ACP_LOG"] = log
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers(), ConfigureWorker: true), CancellationToken.None);
        var first = await DrainTurnAsync(session, new TurnInput("CONTEXT_ACP_USED", "first", "small", "normal"));
        var original = Assert.Single(first.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.Equal(1_048_576, original.Usage!.ContextWindowTokens);

        var next = await DrainTurnAsync(session, new TurnInput("ACP_NO_SIZE_USAGE", "next", "large", "deep"));
        var completed = Assert.Single(next.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.Contains(File.ReadLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)), entry =>
            entry.GetProperty("method").GetString() == "session/set_config_option" &&
            entry.GetProperty("params").GetProperty("configId").GetString() == "worker-model" &&
            entry.GetProperty("params").GetProperty("value").GetString() == "large");
        Assert.Null(completed.Usage!.ContextWindowTokens);
        Assert.NotEqual(original.ContextUsageRevision, completed.ContextUsageRevision);
        Assert.Equal(session.ContextUsageRevision, completed.ContextUsageRevision);
    }

    private static async Task<List<UnifiedAgentEvent>> DrainTurnAsync(IAgentSession session, TurnInput input)
    {
        var list = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(input, CancellationToken.None))
        {
            list.Add(evt);
        }

        return list;
    }

    private sealed class LocalStreamableHttpMcpTestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _expectedAuth;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public LocalStreamableHttpMcpTestServer(string expectedAuth)
        {
            _expectedAuth = expectedAuth;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            EndpointUrl = $"http://127.0.0.1:{port}/mcp/kernel";
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string EndpointUrl { get; }
        public volatile bool SawInitialize;
        public volatile bool SawInitializedNotification;
        public volatile bool SawSessionHeaderOnSubsequentCalls;

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // Ignore accept shutdown.
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var headerBytes = new List<byte>();
                var buf = new byte[1];
                while (true)
                {
                    var n = await stream.ReadAsync(buf.AsMemory(0, 1), _cts.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    headerBytes.Add(buf[0]);
                    var c = headerBytes.Count;
                    if (c >= 4 &&
                        headerBytes[c - 4] == '\r' &&
                        headerBytes[c - 3] == '\n' &&
                        headerBytes[c - 2] == '\r' &&
                        headerBytes[c - 1] == '\n')
                    {
                        break;
                    }
                }

                var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                var headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                for (var i = 1; i < headerLines.Length; i++)
                {
                    var colon = headerLines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        headers[headerLines[i][..colon].Trim()] = headerLines[i][(colon + 1)..].Trim();
                    }
                }

                var contentLen = headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var parsedLen)
                    ? parsedLen
                    : 0;
                var bodyBytes = new byte[contentLen];
                var readTotal = 0;
                while (readTotal < contentLen)
                {
                    var r = await stream.ReadAsync(bodyBytes.AsMemory(readTotal, contentLen - readTotal), _cts.Token);
                    if (r == 0)
                    {
                        break;
                    }

                    readTotal += r;
                }

                if (!headers.TryGetValue("Authorization", out var auth) ||
                    !string.Equals(auth, _expectedAuth, StringComparison.Ordinal))
                {
                    await WriteHttpResponseAsync(stream, "401 Unauthorized", "application/json", "{\"error\":\"unauthorized\"}", extraHeader: null);
                    return;
                }

                using var doc = JsonDocument.Parse(bodyBytes);
                var root = doc.RootElement;
                var method = root.TryGetProperty("method", out var mProp) ? mProp.GetString() : null;
                var hasId = root.TryGetProperty("id", out var idProp);

                if (!hasId)
                {
                    if (method == "notifications/initialized")
                    {
                        SawInitializedNotification = true;
                        if (headers.TryGetValue("Mcp-Session-Id", out var sid) && sid == "mcp_sess_bridge_1")
                        {
                            SawSessionHeaderOnSubsequentCalls = true;
                        }
                    }

                    await WriteHttpResponseAsync(stream, "202 Accepted", "text/plain", string.Empty, extraHeader: null);
                    return;
                }

                var idRaw = idProp.GetRawText();
                if (method == "initialize")
                {
                    SawInitialize = true;
                    var body = $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{{\"protocolVersion\":\"2025-03-26\",\"serverInfo\":{{\"name\":\"perga-kernel-test\",\"version\":\"1.0.0\"}}}}}}";
                    await WriteHttpResponseAsync(
                        stream,
                        "200 OK",
                        "application/json",
                        body,
                        extraHeader: "Mcp-Session-Id: mcp_sess_bridge_1");
                    return;
                }

                if (headers.TryGetValue("Mcp-Session-Id", out var callSid) && callSid == "mcp_sess_bridge_1")
                {
                    SawSessionHeaderOnSubsequentCalls = true;
                }

                if (method == "tools/list")
                {
                    var body = $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{{\"tools\":[{{\"name\":\"perga_report_read\"}}]}}}}";
                    await WriteHttpResponseAsync(stream, "200 OK", "application/json", body, extraHeader: null);
                    return;
                }

                if (method == "tools/call")
                {
                    var rpcJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{{\"content\":[{{\"type\":\"text\",\"text\":\"sse-report-body-ok\"}}]}}}}";
                    var sseBody = $": keepalive\n\nevent: message\ndata: {rpcJson}\n\n";
                    await WriteHttpResponseAsync(stream, "200 OK", "text/event-stream", sseBody, extraHeader: null);
                }
            }
        }

        private static async Task WriteHttpResponseAsync(
            NetworkStream stream,
            string status,
            string contentType,
            string body,
            string? extraHeader)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            sb.Append("Connection: close\r\n");
            if (!string.IsNullOrWhiteSpace(extraHeader))
            {
                sb.Append(extraHeader).Append("\r\n");
            }

            sb.Append("\r\n");
            var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headBytes);
            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes);
            }

            await stream.FlushAsync();
        }
    }
}
