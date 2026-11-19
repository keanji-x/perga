using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void PolicyVmContracts_ConstructCleanly()
    {
        GuardDecision decision = new GuardDecision.Allow();
        using var doc = JsonDocument.Parse("{}");
        var route = new PolicyTaskRoute("codex", "gpt-5");

        Assert.IsType<GuardDecision.Allow>(decision);
        Assert.Equal("codex", route.Provider);
    }
}
