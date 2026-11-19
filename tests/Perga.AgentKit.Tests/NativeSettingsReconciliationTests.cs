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
        var settings = SessionConfiguration.FromNullable("test-model", "high", permissionMode);
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            provider, settings.Model.ValueOrNull, fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>(),
            ReasoningEffort: settings.Effort.ValueOrNull, PermissionMode: settings.Permission.ValueOrNull), default);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(settings, default));
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await session.ConfigureAsync(settings with { Model = new SessionSelection.Explicit("other") }, default));
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await session.ConfigureAsync(settings with { Effort = new SessionSelection.Explicit("low") }, default));
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await session.ConfigureAsync(settings with { Permission = new SessionSelection.ProviderDefault() }, default));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(settings, default));
    }
}
