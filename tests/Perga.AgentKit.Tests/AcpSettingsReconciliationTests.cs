using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class AcpSettingsReconciliationTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();
    private const string Modes = """{"modes":{"currentModeId":"safe","availableModes":[{"id":"safe"},{"id":"act"}]}}""";
    private const string ConfigModes = """{"configOptions":[{"id":"permission-policy","category":"mode","type":"select","currentValue":"safe","options":[{"value":"safe"},{"value":"act"}]}]}""";
    private string Log => Path.Combine(_fixture.RootDir, "settings.jsonl");

    public void Dispose() => _fixture.Dispose();

    private AgentRuntime Runtime(string provider, bool config = false, int error = 0, Dictionary<string, string>? env = null, string? modes = null) =>
        new(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                [provider] = new(_fixture.MockAcpPath, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_MODELS"] = modes ?? (config ? ConfigModes : Modes),
                    ["MOCK_ACP_LOG"] = Log,
                    ["MOCK_ACP_LOG_ARGV"] = "1",
                    ["MOCK_ACP_MODE_ERROR"] = error.ToString()
                }.Concat(env ?? []).ToDictionary(pair => pair.Key, pair => pair.Value))
            }
        });

    private HarnessLaunchSpec Spec(string provider, string? mode = null, string? resume = null) =>
        new(provider, null, _fixture.WorkspaceDir, resume, null, new Dictionary<string, McpServerConfig>(), PermissionMode: mode);
    private JsonElement[] Calls() => File.ReadAllLines(Log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
        .Where(call => call.TryGetProperty("method", out _)).ToArray();

    private const string UpdatedModes = """{"configOptions":[{"id":"new-policy","category":"mode","type":"select","currentValue":"safe","options":[{"value":"safe"},{"value":"extra"}]}]}""";

    [Theory]
    [InlineData("permission", true)]
    [InlineData("permission", false)]
    [InlineData("permission-modes", true)]
    [InlineData("notification-modes", true)]
    [InlineData("model", true)]
    [InlineData("notification", true)]
    [InlineData("notification", false)]
    public async Task DynamicCatalogueAllowsNewModeAndRejectsRemovedMode(string source, bool config)
    {
        var legacyModes = source.EndsWith("-modes", StringComparison.Ordinal);
        var env = new Dictionary<string, string>
        {
            [source.StartsWith("notification", StringComparison.Ordinal) ? "MOCK_ACP_PERMISSION_UPDATE" : "MOCK_ACP_PERMISSION_RESPONSE"] =
                legacyModes ? """{"modes":{"availableModes":[{"id":"safe"},{"id":"extra"}]}}""" : UpdatedModes
        };
        if (source == "model") env["MOCK_ACP_NO_SET_MODEL"] = "1";
        await using var runtime = Runtime("acp:opencode", config, env: env);
        await using var session = await runtime.OpenSessionAsync(Spec("acp:opencode"), default);
        if (source.StartsWith("permission", StringComparison.Ordinal))
            Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, null, "act"), default));
        else
            Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(session.ConfigurationState!.Selection with { Model = new SessionSelection.Explicit("next-model") }));
        // Exercise reconciliation before inspecting the catalogue: the old implementation rejects extra here.
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(session.ConfigurationState!.Selection with { Permission = new SessionSelection.Explicit("extra") }, default));
        Assert.Equal(new[] { "safe", "extra" }, session.AvailablePermissionModes.Select(mode => mode.Id));
        var before = Calls().Length;
        await Assert.ThrowsAsync<ArgumentException>(() => session.ConfigureAsync(SessionConfiguration.FromNullable(null, null, "act"), default));
        Assert.Equal(before, Calls().Length);
        var last = Calls().Last();
        Assert.Equal(legacyModes ? "session/set_mode" : "session/set_config_option", last.GetProperty("method").GetString());
        if (!legacyModes) Assert.Equal("new-policy", last.GetProperty("params").GetProperty("configId").GetString());
        Assert.Single(Calls(), call => call.GetProperty("method").GetString() == "session/new");
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("notification")]
    public async Task ExpandedCatalogueAdvertisesSafeActAndExtra(string source)
    {
        var expanded = ConfigModes.Replace("{\"value\":\"act\"}", "{\"value\":\"act\"},{\"value\":\"extra\"}");
        await using var runtime = Runtime("acp:opencode", true, env: new()
        {
            [source == "permission" ? "MOCK_ACP_PERMISSION_RESPONSE" : "MOCK_ACP_PERMISSION_UPDATE"] = expanded
        });
        await using var session = await runtime.OpenSessionAsync(Spec("acp:opencode"), default);
        if (source == "permission") Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, null, "act"), default));
        else Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(session.ConfigurationState!.Selection with { Model = new SessionSelection.Explicit("next-model") }));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(session.ConfigurationState!.Selection with { Permission = new SessionSelection.Explicit("extra") }, default));
        Assert.Equal(new[] { "safe", "act", "extra" }, session.AvailablePermissionModes.Select(mode => mode.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptySuccessKeepsCatalogueAndTransport(bool config)
    {
        await using var runtime = Runtime("acp:opencode", config);
        await using var session = await runtime.OpenSessionAsync(Spec("acp:opencode", "safe"), default);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, null, "act"), default));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, null, "safe"), default));
        Assert.Equal(new[] { "safe", "act" }, session.AvailablePermissionModes.Select(mode => mode.Id));
        Assert.Equal(config ? "session/set_config_option" : "session/set_mode", Calls().Last().GetProperty("method").GetString());
    }

    [Fact]
    public async Task NotificationForOtherOrBlankSessionDoesNotReplaceCatalogue()
    {
        foreach (var sessionId in new[] { "other-session", "" })
        {
            await using var runtime = Runtime("acp:opencode", true, env: new()
            {
                ["MOCK_ACP_PERMISSION_UPDATE"] = UpdatedModes,
                ["MOCK_ACP_PERMISSION_UPDATE_SESSION"] = sessionId
            });
            await using var session = await runtime.OpenSessionAsync(Spec("acp:opencode"), default);
            Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(session.ConfigurationState!.Selection with { Model = new SessionSelection.Explicit("next-model") }));
            Assert.Equal(new[] { "safe", "act" }, session.AvailablePermissionModes.Select(mode => mode.Id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogueRefreshPreservesInjectedLaunchModeWithoutDuplicates(bool notification)
    {
        await using var runtime = Runtime("acp:antigravity", true, env: new()
        {
            [notification ? "MOCK_ACP_PERMISSION_UPDATE" : "MOCK_ACP_PERMISSION_RESPONSE"] = UpdatedModes
        });
        await using var session = await runtime.OpenSessionAsync(Spec("acp:antigravity", "safe"), default);
        if (notification) Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(session.ConfigurationState!.Selection with { Model = new SessionSelection.Explicit("next-model") }));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(session.ConfigurationState!.Selection with { Permission = new SessionSelection.Explicit("extra") }, default));
        Assert.Single(session.AvailablePermissionModes, mode => mode.Id == "bypassPermissions");
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await session.ConfigureAsync(session.ConfigurationState!.Selection with { Model = new SessionSelection.Explicit("next-model"), Permission = new SessionSelection.Explicit("bypassPermissions") }, default));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProtocolCollisionKeepsBypassPermissionsAsLaunchMode(bool config, bool notification)
    {
        const string provider = "acp:antigravity";
        var modes = config
            ? ConfigModes.Replace("{\"value\":\"act\"}", "{\"value\":\"act\"},{\"value\":\"bypassPermissions\"}")
            : Modes.Replace("{\"id\":\"act\"}", "{\"id\":\"act\"},{\"id\":\"bypassPermissions\"}");
        var refreshed = modes.Replace("act", "extra");
        await using var runtime = Runtime(provider, config, env: new()
        {
            [notification ? "MOCK_ACP_PERMISSION_UPDATE" : "MOCK_ACP_PERMISSION_RESPONSE"] = refreshed
        }, modes: modes);
        await using (var ordinary = await runtime.OpenSessionAsync(Spec(provider), default))
        {
            Assert.Equal("danger", Assert.Single(ordinary.AvailablePermissionModes, mode => mode.Id == "bypassPermissions").Tone);
            if (notification)
                Assert.IsType<SessionConfigurationOutcome.Applied>(await ordinary.ConfigureAsync(ordinary.ConfigurationState!.Selection with { Model = new SessionSelection.Explicit("next-model") }));
            else
                Assert.IsType<SessionConfigurationOutcome.Applied>(await ordinary.ConfigureAsync(SessionConfiguration.FromNullable(null, null, "act")));
            Assert.Contains(ordinary.AvailablePermissionModes, mode => mode.Id == "extra");
            Assert.Equal("danger", Assert.Single(ordinary.AvailablePermissionModes, mode => mode.Id == "bypassPermissions").Tone);
            var before = Calls().Length;
            Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await ordinary.ConfigureAsync(ordinary.ConfigurationState!.Selection with { Model = new SessionSelection.Explicit("next-model"), Permission = new SessionSelection.Explicit("bypassPermissions") }));
            Assert.Equal(before, Calls().Length);
        }
        await using var full = await runtime.OpenSessionAsync(Spec(provider, "bypassPermissions") with { Model = "next-model" }, default);
        Assert.Equal("danger", Assert.Single(full.AvailablePermissionModes, mode => mode.Id == "bypassPermissions").Tone);
        var beforeSelection = Calls().Length;
        Assert.IsType<SessionConfigurationOutcome.Applied>(await full.ConfigureAsync(SessionConfiguration.FromNullable("next-model", null, "bypassPermissions")));
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await full.ConfigureAsync(SessionConfiguration.FromNullable("next-model", null, "safe")));
        Assert.Equal(beforeSelection, Calls().Length);
        Assert.DoesNotContain(Calls(), call => call.GetProperty("method").GetString() is "session/set_mode" or "session/set_config_option" &&
            call.GetProperty("params").ToString().Contains("bypassPermissions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("acp:opencode", false)]
    [InlineData("acp:opencode", true)]
    [InlineData("acp:antigravity", false)]
    [InlineData("acp:antigravity", true)]
    public async Task OrdinaryModesAndModelReconcileInPlace(string provider, bool config)
    {
        await using var runtime = Runtime(provider, config);
        await using var session = await runtime.OpenSessionAsync(Spec(provider, "safe"), default);
        var nativeId = session.NativeThreadId;
        var desired = SessionConfiguration.FromNullable("next-model", null, "act");
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(desired, default));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(desired, default));
        Assert.Equal(nativeId, session.NativeThreadId);
        Assert.Single(Calls(), call => call.GetProperty("method").GetString() == "initialize");
        Assert.Single(Calls(), call => call.GetProperty("method").GetString() == "session/new");
        var switches = Calls().Where(call => call.GetProperty("method").GetString() ==
            (config ? "session/set_config_option" : "session/set_mode")).ToArray();
        Assert.Equal(2, switches.Length);
        var parameters = switches.Last().GetProperty("params");
        Assert.Equal("act", parameters.GetProperty(config ? "value" : "modeId").GetString());
        if (config) Assert.Equal("permission-policy", parameters.GetProperty("configId").GetString());
        Assert.Single(Calls(), call => call.GetProperty("method").GetString() == "session/set_model");
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await session.ConfigureAsync(desired with { Permission = new SessionSelection.ProviderDefault() }, default));
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(desired with { Model = new SessionSelection.ProviderDefault() }, default));
    }

    [Theory]
    [InlineData(false, -32601)]
    [InlineData(true, -32601)]
    [InlineData(false, -32602)]
    [InlineData(true, -32602)]
    public async Task MissingMethodFallsBackButProviderRejectionThrows(bool config, int error)
    {
        await using var runtime = Runtime("acp:opencode", config, error);
        await using var session = await runtime.OpenSessionAsync(Spec("acp:opencode"), default);
        var desired = SessionConfiguration.FromNullable(null, null, "act");
        if (error == -32601) Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await session.ConfigureAsync(desired, default));
        else
        {
            var failure = Assert.IsType<SessionConfigurationOutcome.Rejected>(await session.ConfigureAsync(desired, default));
            Assert.Contains("mode switch failed", failure.Reason);
        }
        Assert.Single(Calls(), call => call.GetProperty("method").GetString() == "session/new");
        Assert.DoesNotContain(Calls(), call => call.GetProperty("method").GetString() == "session/prompt");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidModeRejectedBeforeModeOrPromptRpcAndValidRetrySucceeds(bool config)
    {
        await using var runtime = Runtime("acp:opencode", config);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.OpenSessionAsync(Spec("acp:opencode", "invented"), default));
        await using var session = await runtime.OpenSessionAsync(Spec("acp:opencode", "safe"), default);
        var before = Calls().Length;
        await Assert.ThrowsAsync<ArgumentException>(() => session.ConfigureAsync(SessionConfiguration.FromNullable(null, null, "invented"), default));
        Assert.Equal(before, Calls().Length);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, null, "act"), default));
        Assert.DoesNotContain(Calls(), call => call.GetProperty("method").GetString() == "session/prompt");
    }

    [Fact]
    public async Task WorkerReuseValidatesLaunchSelectionWithoutRepeatingOrdinaryConfiguration()
    {
        await using var runtime = Runtime("acp:opencode");
        var spec = Spec("acp:opencode", "safe") with { ConfigureWorker = true };
        await using var session = await runtime.OpenSessionAsync(spec, default);
        var configuration = SessionConfiguration.FromLaunch(spec);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(configuration));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(configuration));
        Assert.Single(Calls(), call => call.GetProperty("method").GetString() == "session/set_mode");
        Assert.DoesNotContain(Calls(), call => call.GetProperty("method").GetString() == "session/set_model");
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "[]")]
    [InlineData(true, "[]")]
    [InlineData(false, "{\"success\":false}")]
    [InlineData(true, "{\"success\":false}")]
    public async Task RejectedPermissionAcknowledgementCannotConfirmOrDispatch(bool config, string acknowledgement)
    {
        await using var runtime = Runtime("acp:opencode", config, env: new()
        {
            ["MOCK_ACP_PERMISSION_RESPONSE"] = acknowledgement
        });
        await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(Spec("acp:opencode", "act"), default));
        await using var session = await runtime.OpenSessionAsync(Spec("acp:opencode"), default);
        var outcome = Assert.IsType<SessionConfigurationOutcome.Rejected>(await session.ConfigureAsync(
            SessionConfiguration.FromNullable(null, null, "act")));
        Assert.Equal(ConfigurationFailureCode.InvalidAcknowledgement, outcome.Code);
        Assert.False(outcome.ObservedState.IsUsable);
        Assert.Null(session.ConfigurationState);
        Assert.Null(session.ContextUsageRevision);
        await Assert.ThrowsAsync<SessionConfigurationException>(async () =>
        {
            await foreach (var _ in session.StreamTurnAsync(new("blocked", "rejected-permission"), default)) { }
        });
        Assert.DoesNotContain(Calls(), call => call.GetProperty("method").GetString() == "session/prompt");
    }

    [Fact]
    public async Task LaunchArgumentTransitionsRequireRestartAndResumeSameNativeThread()
    {
        const string provider = "acp:antigravity";
        await using var runtime = Runtime(provider);
        string? nativeId;
        await using (var ordinary = await runtime.OpenSessionAsync(Spec(provider, "safe"), default))
        {
            nativeId = ordinary.NativeThreadId;
            Assert.IsType<SessionConfigurationOutcome.Applied>(await ordinary.ConfigureAsync(SessionConfiguration.FromNullable(null, null, "act"), default));
            Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await ordinary.ConfigureAsync(SessionConfiguration.FromNullable("next-model", null, "bypassPermissions"), default));
        }
        await using (var full = await runtime.OpenSessionAsync(Spec(provider, "bypassPermissions", nativeId) with { Model = "next-model" }, default))
        {
            Assert.Equal(nativeId, full.NativeThreadId);
            Assert.IsType<SessionConfigurationOutcome.Applied>(await full.ConfigureAsync(SessionConfiguration.FromNullable("next-model", null, "bypassPermissions"), default));
            Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await full.ConfigureAsync(SessionConfiguration.FromNullable("next-model", null, "safe"), default));
            Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await full.ConfigureAsync(SessionConfiguration.FromNullable("next-model", null, null), default));
        }
        await using var restored = await runtime.OpenSessionAsync(Spec(provider, "safe", nativeId) with { Model = "next-model" }, default);
        Assert.Equal(nativeId, restored.NativeThreadId);
        var argv = File.ReadAllLines(Log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.TryGetProperty("argv", out _)).Select(call => call.GetProperty("argv").ToString()).ToArray();
        Assert.Equal(3, argv.Length);
        Assert.DoesNotContain("--dangerously-skip-permissions", argv[0]);
        Assert.Contains("--dangerously-skip-permissions", argv[1]);
        Assert.DoesNotContain("--dangerously-skip-permissions", argv[2]);
        Assert.Equal(2, Calls().Count(call => call.GetProperty("method").GetString() == "session/load"));
    }
}
