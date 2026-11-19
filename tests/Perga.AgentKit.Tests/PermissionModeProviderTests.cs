using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class PermissionModeProviderTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ProviderPermissionModes_DeclaresExpectedModesAndDefaults()
    {
        var codexModes = ProviderPermissionModes.GetModesForProvider("codex");
        Assert.Equal(["never", "ask", "full"], codexModes.Select(m => m.Id).ToArray());
        Assert.Equal("never", ProviderPermissionModes.GetDefaultMode("codex"));

        var claudeModes = ProviderPermissionModes.GetModesForProvider("claude");
        Assert.Equal(["dontAsk", "default", "acceptEdits", "plan", "auto", "manual", "bypassPermissions"], claudeModes.Select(m => m.Id).ToArray());
        Assert.Equal("dontAsk", ProviderPermissionModes.GetDefaultMode("claude"));

        var acpModes = ProviderPermissionModes.GetModesForProvider("acp:opencode");
        Assert.Empty(acpModes);
        Assert.Null(ProviderPermissionModes.GetDefaultMode("acp:opencode"));

        Assert.True(ProviderPermissionModes.IsSupported("codex", "ask"));
        Assert.False(ProviderPermissionModes.IsSupported("codex", "bypassPermissions"));
        Assert.True(ProviderPermissionModes.IsSupported("claude", "acceptEdits"));
        Assert.False(ProviderPermissionModes.IsSupported("acp:opencode", "ask"));
    }

    [Theory]
    [InlineData("never", "never", "workspace-write")]
    [InlineData("ask", "on-request", "workspace-write")]
    [InlineData("full", "never", "danger-full-access")]
    public async Task Codex_OpenWithExplicitPermissionMode_Succeeds(string mode, string approval, string sandbox)
    {
        var log = Path.Combine(_fixture.RootDir, "native-modes.jsonl");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(_fixture.MockCodexPath, Env: new Dictionary<string, string> { ["MOCK_CODEX_LOG"] = log })
            }
        });

        var codexSpec = new HarnessLaunchSpec(
            Provider: "codex",
            Model: "gpt-5.4",
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: new Dictionary<string, McpServerConfig>(),
            PermissionMode: mode);

        await using var codexSession = await runtime.OpenSessionAsync(codexSpec, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(codexSession.NativeThreadId));
        var call = Assert.Single(File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)),
            call => call.GetProperty("method").GetString() == "thread/start");
        Assert.Equal(approval, call.GetProperty("params").GetProperty("approvalPolicy").GetString());
        Assert.Equal(sandbox, call.GetProperty("params").GetProperty("sandbox").GetString());
    }
    [Theory]
    [InlineData("dontAsk")]
    [InlineData("default")]
    [InlineData("acceptEdits")]
    [InlineData("plan")]
    [InlineData("auto")]
    [InlineData("manual")]
    [InlineData("bypassPermissions")]
    public async Task ClaudeModesPassThroughUnchanged(string mode)
    {
        var log = Path.Combine(_fixture.RootDir, "claude-args.json");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            { ["claude"] = new(_fixture.MockClaudePath, Env: new Dictionary<string, string> { ["MOCK_CLAUDE_ARGS_LOG"] = log }) }
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "claude", null, _fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>(),
            PermissionMode: mode), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("mode check", "mode-check"), timeout.Token))
            Assert.IsNotType<UnifiedAgentEvent.TurnError>(evt);
        var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(log))!;
        Assert.Equal(mode, args[Array.IndexOf(args, "--permission-mode") + 1]);
    }
}
