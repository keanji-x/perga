namespace Perga.AgentKit.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void AgentKitContracts_ConstructCleanly()
    {
        var spec = new HarnessLaunchSpec(
            "codex",
            null,
            "/tmp",
            null,
            null,
            new Dictionary<string, McpServerConfig>(),
            OsSandboxMode.WorkspaceWriteBwrap);
        UnifiedAgentEvent evt = new UnifiedAgentEvent.TextDelta("hello");

        Assert.Equal(OsSandboxMode.WorkspaceWriteBwrap, spec.Sandbox);
        Assert.IsType<UnifiedAgentEvent.TextDelta>(evt);
    }
}
