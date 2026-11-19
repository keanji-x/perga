namespace Perga.AgentKit.Tests;

public sealed class NativeSettingsReconciliationTests
{
    [Theory]
    [InlineData("codex", "ask")]
    [InlineData("claude", "plan")]
    public async Task OnlyUnchangedSettingsCanReuseNativeSession(string provider, string permissionMode)
    {
        using var fixture = new MockCliFixture();
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                [provider] = new(provider == "codex" ? fixture.MockCodexPath : fixture.MockClaudePath)
            }
        });
        var settings = new SessionRuntimeSettings("test-model", "high", permissionMode);
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            provider, settings.Model, fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>(),
            ReasoningEffort: settings.ReasoningEffort, PermissionMode: settings.PermissionMode), default);
        Assert.True(await session.TryReconcileSettingsAsync(settings, default));
        Assert.False(await session.TryReconcileSettingsAsync(settings with { Model = "other" }, default));
        Assert.False(await session.TryReconcileSettingsAsync(settings with { ReasoningEffort = "low" }, default));
        Assert.False(await session.TryReconcileSettingsAsync(settings with { PermissionMode = null }, default));
        Assert.True(await session.TryReconcileSettingsAsync(settings, default));
    }
}
