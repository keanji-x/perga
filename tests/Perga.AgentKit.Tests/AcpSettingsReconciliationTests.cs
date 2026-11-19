using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class AcpSettingsReconciliationTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();
    private const string Modes = """{"modes":{"currentModeId":"safe","availableModes":[{"id":"safe"},{"id":"act"}]}}""";
    private const string ConfigModes = """{"configOptions":[{"id":"permission-policy","category":"mode","type":"select","currentValue":"safe","options":[{"value":"safe"},{"value":"act"}]}]}""";
    private string Log => Path.Combine(_fixture.RootDir, "settings.jsonl");

    public void Dispose() => _fixture.Dispose();

    private AgentRuntime Runtime(string provider, bool config = false, int error = 0, bool launchMode = false, Dictionary<string, string>? env = null)
    {
        var executable = _fixture.MockAcpPath;
        if (launchMode && !OperatingSystem.IsWindows())
        {
            executable = Path.Combine(_fixture.RootDir, "agy");
            File.WriteAllText(executable, $"#!/bin/sh\nexec \"{_fixture.MockAcpPath}\" \"$@\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return new(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                [provider] = new(executable, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_MODELS"] = config ? ConfigModes : Modes,
                    ["MOCK_ACP_LOG"] = Log,
                    ["MOCK_ACP_LOG_ARGV"] = "1",
                    ["MOCK_ACP_MODE_ERROR"] = error.ToString()
                }.Concat(env ?? []).ToDictionary(pair => pair.Key, pair => pair.Value))
            }
        });
    }

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
            Assert.True(await session.TryReconcileSettingsAsync(new(null, null, "act"), default));
        else
            Assert.True(await session.TrySetModelAsync("next-model"));
        // Exercise reconciliation before inspecting the catalogue: the old implementation rejects extra here.
        Assert.True(await session.TryReconcileSettingsAsync(new(null, null, "extra"), default));
        Assert.Equal(new[] { "safe", "extra" }, session.AvailablePermissionModes.Select(mode => mode.Id));
        var before = Calls().Length;
        await Assert.ThrowsAsync<ArgumentException>(() => session.TryReconcileSettingsAsync(new(null, null, "act"), default));
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
        if (source == "permission") Assert.True(await session.TryReconcileSettingsAsync(new(null, null, "act"), default));
        else Assert.True(await session.TrySetModelAsync("next-model"));
        Assert.True(await session.TryReconcileSettingsAsync(new(null, null, "extra"), default));
        Assert.Equal(new[] { "safe", "act", "extra" }, session.AvailablePermissionModes.Select(mode => mode.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptySuccessKeepsCatalogueAndTransport(bool config)
    {
        await using var runtime = Runtime("acp:opencode", config);
        await using var session = await runtime.OpenSessionAsync(Spec("acp:opencode", "safe"), default);
        Assert.True(await session.TryReconcileSettingsAsync(new(null, null, "act"), default));
        Assert.True(await session.TryReconcileSettingsAsync(new(null, null, "safe"), default));
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
            Assert.True(await session.TrySetModelAsync("next-model"));
            Assert.Equal(new[] { "safe", "act" }, session.AvailablePermissionModes.Select(mode => mode.Id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogueRefreshPreservesInjectedLaunchModeWithoutDuplicates(bool notification)
    {
        if (OperatingSystem.IsWindows()) return;
        await using var runtime = Runtime("acp:antigravity", true, launchMode: true, env: new()
        {
            [notification ? "MOCK_ACP_PERMISSION_UPDATE" : "MOCK_ACP_PERMISSION_RESPONSE"] = UpdatedModes
        });
        await using var session = await runtime.OpenSessionAsync(Spec("acp:antigravity", "safe"), default);
        if (notification) Assert.True(await session.TrySetModelAsync("next-model"));
        Assert.True(await session.TryReconcileSettingsAsync(new(null, null, "extra"), default));
        Assert.Single(session.AvailablePermissionModes, mode => mode.Id == "bypassPermissions");
        Assert.False(await session.TryReconcileSettingsAsync(new(null, null, "bypassPermissions"), default));
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
        var desired = new SessionRuntimeSettings("next-model", null, "act");
        Assert.True(await session.TryReconcileSettingsAsync(desired, default));
        Assert.True(await session.TryReconcileSettingsAsync(desired, default));
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
        Assert.False(await session.TryReconcileSettingsAsync(desired with { PermissionMode = null }, default));
        Assert.False(await session.TryReconcileSettingsAsync(desired with { Model = null }, default));
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
        var desired = new SessionRuntimeSettings(null, null, "act");
        if (error == -32601) Assert.False(await session.TryReconcileSettingsAsync(desired, default));
        else
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => session.TryReconcileSettingsAsync(desired, default));
            Assert.Contains("mode switch failed", failure.Message);
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
        await Assert.ThrowsAsync<ArgumentException>(() => session.TryReconcileSettingsAsync(new(null, null, "invented"), default));
        Assert.Equal(before, Calls().Length);
        Assert.True(await session.TryReconcileSettingsAsync(new(null, null, "act"), default));
        Assert.DoesNotContain(Calls(), call => call.GetProperty("method").GetString() == "session/prompt");
    }

    [Fact]
    public async Task LaunchArgumentTransitionsRequireRestartAndResumeSameNativeThread()
    {
        if (OperatingSystem.IsWindows()) return;
        const string provider = "acp:antigravity";
        await using var runtime = Runtime(provider, launchMode: true);
        string? nativeId;
        await using (var ordinary = await runtime.OpenSessionAsync(Spec(provider, "safe"), default))
        {
            nativeId = ordinary.NativeThreadId;
            Assert.True(await ordinary.TryReconcileSettingsAsync(new(null, null, "act"), default));
            Assert.False(await ordinary.TryReconcileSettingsAsync(new(null, null, "bypassPermissions"), default));
        }
        await using (var full = await runtime.OpenSessionAsync(Spec(provider, "bypassPermissions", nativeId), default))
        {
            Assert.Equal(nativeId, full.NativeThreadId);
            Assert.True(await full.TryReconcileSettingsAsync(new(null, null, "bypassPermissions"), default));
            Assert.False(await full.TryReconcileSettingsAsync(new(null, null, "safe"), default));
            Assert.False(await full.TryReconcileSettingsAsync(new(null, null, null), default));
        }
        await using var restored = await runtime.OpenSessionAsync(Spec(provider, "safe", nativeId), default);
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
