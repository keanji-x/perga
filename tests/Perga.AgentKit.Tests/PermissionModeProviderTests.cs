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
        Assert.Equal(["dontAsk", "default", "acceptEdits", "bypassPermissions"], claudeModes.Select(m => m.Id).ToArray());
        Assert.Equal("dontAsk", ProviderPermissionModes.GetDefaultMode("claude"));

        var acpModes = ProviderPermissionModes.GetModesForProvider("acp:opencode");
        Assert.Empty(acpModes);
        Assert.Null(ProviderPermissionModes.GetDefaultMode("acp:opencode"));

        Assert.True(ProviderPermissionModes.IsSupported("codex", "ask"));
        Assert.False(ProviderPermissionModes.IsSupported("codex", "bypassPermissions"));
        Assert.True(ProviderPermissionModes.IsSupported("claude", "acceptEdits"));
        Assert.False(ProviderPermissionModes.IsSupported("acp:opencode", "ask"));
    }

    [Fact]
    public async Task Codex_OpenWithExplicitPermissionMode_Succeeds()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(_fixture.MockCodexPath)
            }
        });

        var codexSpec = new HarnessLaunchSpec(
            Provider: "codex",
            Model: "gpt-5.4",
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: new Dictionary<string, McpServerConfig>(),
            PermissionMode: "ask");

        await using var codexSession = await runtime.OpenSessionAsync(codexSpec, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(codexSession.NativeThreadId));
    }
}
