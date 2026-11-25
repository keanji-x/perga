using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

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

        Assert.Contains("## Current Report\nThe Report is empty.", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("""{"docRev":0,"knownBlockRevs":{},"wholeDocumentRead":true}""", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("Use this ReadAnchor to call `report_commit` directly without calling `report_read` first", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("If you apply a template with `track_set` first, call `report_read` to get the seeded Report's anchor", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("The user sees the Report alongside your reply", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("clear goal, plan, analysis, or deliverable", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("commit an initial structured Report with `report_commit` before ending the turn", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("Use `track_rename` if the title is still untitled or a placeholder", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("keep your reply concise and point to the Report", leadPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("quick one-off question or asking for clarification", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("you can leave the Report empty for now", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("Briefly say you'll fill it in once the direction is confirmed", leadPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeadSystemPrompt_ReflectsTheCurrentReportAfterEarlierCommits(bool emptyAfterDeletion)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var leadId = snap.Track.LeadSessionId!;
        var committed = await ctx.Kernel.CommitReportAsync(snap.Track.Id, leadId,
            new(new ReadAnchor(0, new Dictionary<string, int>(), WholeDocumentRead: true),
                [new BlockOp.ReplaceSectionOp("Overview", "An existing plan.")]));
        if (emptyAfterDeletion)
        {
            await ctx.Kernel.CommitReportAsync(snap.Track.Id, leadId,
                new(committed.Anchor, [new BlockOp.DeleteBlockOp(Assert.Single(committed.Blocks).Id)]));
        }

        await ctx.SendAndWaitForTurnAsync(leadId, new("Review the plan", "lead-current-report"));

        var prompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        if (emptyAfterDeletion)
        {
            Assert.Contains("""{"docRev":2,"knownBlockRevs":{},"wholeDocumentRead":true}""", prompt, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"## Current Report\n{committed.RenderedMarkdown}", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("The Report is empty.", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LeadSystemPrompt_PreservesLeadingCommentOnlyReport()
    {
        var reportEngine = new ReportDocEngine();
        await using var ctx = new KernelTestContext(reportEngine: reportEngine);
        var (area, snap) = await ctx.CreateDefaultTrackAsync();
        var leadId = snap.Track.LeadSessionId!;
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snap.Track.Id);

        var commentOnly = new ReportDocument(0, string.Empty, "<!-- track header -->", []);
        await ctx.Store.SaveReportAsync(
            snap.Track.Id,
            commentOnly,
            reportEngine.CompressZstd(commentOnly.Blocks),
            leadId,
            "seed comment-only report",
            new EventEnvelopeDto(null, area.Id, snap.Track.Id, "report.updated", TestJson.FromObject(new { }), 0),
            default);

        await ctx.SendAndWaitForTurnAsync(leadId, new("Review header", "lead-comment-only-report"));

        var prompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        Assert.Contains("## Current Report\n<!-- track header -->", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("The Report is empty.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryLeadSystemPrompt_OmitsEmptyReportWriteGuidance()
    {
        await using var ctx = new KernelTestContext();
        var (area, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snap.Track.Id);
        ctx.Store.MemoryBinding = new(MemoryTrackBinding.ScopeKeyFor(area.Id), area.Id, snap.Track.Id);

        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new("Review memory", "memory-prompt-check"));

        var prompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        Assert.Contains("This Track's report projects live Memory entries", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("The Report is empty.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("commit an initial structured Report", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Use `track_rename` if the title", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubagentSystemPrompt_OmitsEmptyReportWriteGuidance()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.Kernel.CreateSessionAsync(snap.Track.Id, new CreateSessionRequest(Title: "Helper", Prompt: "Inspect the workspace"),
            requestKey: "subagent-prompt-check", caller: new InteropPrincipal.Human(), ct: default);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);

        var prompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        Assert.Contains("Collaborative Subagent", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("The Report is empty.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`report_commit`", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`track_rename`", prompt, StringComparison.Ordinal);
    }
}
