using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class TemplateEntryOptimizationTests
{
    [Fact]
    public async Task LeadSystemPrompt_DescribesTrackModelUpfrontTemplateScopingAndPreservesFlowAndGovernanceContracts()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new SendSessionMessageRequest("帮我做小米投研", "lead-prompt-check"));

        var leadPrompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;

        // Track Model + upfront template scoping
        Assert.Contains("## Track Model", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("**Title**", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("**Report**", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("Templates are optional and implicit", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("When no template is active yet", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("dryRun", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("inputs", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("before doing deep work or writing the Report", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("composable rather than all-or-nothing", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("format: \"files\"", leadPrompt, StringComparison.Ordinal);

        // Preserved Flow orchestration contract
        Assert.Contains("## Track Flow Orchestration", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("flow_commit(dryRun: true)", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("flow_read(format: \"spec\")", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("\"canceled\": true", leadPrompt, StringComparison.Ordinal);

        // Preserved Session & Worker governance contract
        Assert.Contains("session_create", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("requestKey", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("task_done", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("provider_list", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("session_respond", leadPrompt, StringComparison.Ordinal);
    }
}
