using System.Text.Json;

namespace Perga.PolicyVm.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void PolicyVmContracts_ConstructCleanly()
    {
        GuardDecision decision = new GuardDecision.Allow();
        using var doc = JsonDocument.Parse("{}");
        var ctx = new PolicyToolCallContext(
            "trk_1",
            "ses_1",
            true,
            null,
            "perga",
            "perga_report_read",
            doc.RootElement.Clone(),
            doc.RootElement.Clone());

        Assert.IsType<GuardDecision.Allow>(decision);
        Assert.Equal("perga", ctx.McpServer);
    }
}
