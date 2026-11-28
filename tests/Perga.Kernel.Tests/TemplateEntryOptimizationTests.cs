using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class TemplateEntryOptimizationTests
{
    [Fact]
    public async Task LeadSystemPrompt_UsesSceneDrivenReportFirstModelWithoutPerTurnReportState()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate("""
            export default {
              id: 'research', title: 'Research', description: 'Compare sources and document findings.',
              inputs: { topic: { type: 'string', default: 'Example' } }
            };
            """);
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new SendSessionMessageRequest("帮我做小米投研", "lead-prompt-check"));

        var session = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var leadPrompt = session.LaunchSpec.SystemPrompt;

        Assert.Contains("## Workspace & Collaboration Model", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("## How to Work", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("Work **Report-first**", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("`report_commit`", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("`track_rename`", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("check `template_list` before writing the first Report", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("resolve any task-specific inputs (`template_read` if needed)", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("`session_create`", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("`flow_commit`", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("## Boundaries", leadPrompt, StringComparison.Ordinal);

        Assert.Contains("## Current Report\nThe Report is empty.", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("""{"docRev":0,"knownBlockRevs":{},"wholeDocumentRead":true}""", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("apply a fitting template with `track_set` first and use its `reportAnchor`", leadPrompt, StringComparison.Ordinal);

        // Keeps static System Prompt concise and leaves per-turn user prompts clean.
        Assert.DoesNotContain("## Installed Templates", leadPrompt, StringComparison.Ordinal);
        Assert.Equal("帮我做小米投研", Assert.Single(session.RecordedInputs).Prompt);
    }

    [Fact]
    public async Task LeadSystemPrompt_SwitchesTemplateGuidanceWhenTemplateIsActive()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate("""
            export default { id: 'delivery', title: 'Delivery', description: 'Deliver changes cleanly.' };
            """);
        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "delivery");
        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new("Review the scope", "lead-bound-template"));

        var leadPrompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        Assert.Contains("**Active template**", leadPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("check `template_list`", leadPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("apply a fitting template with `track_set` first", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("""{"docRev":0,"knownBlockRevs":{},"wholeDocumentRead":true}""", leadPrompt, StringComparison.Ordinal);
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
    public async Task MemoryLeadSystemPrompt_OmitsReportFirstWriteAndTemplateGuidance()
    {
        await using var ctx = new KernelTestContext();
        var (area, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snap.Track.Id);
        ctx.Store.MemoryBinding = new(MemoryTrackBinding.ScopeKeyFor(area.Id), area.Id, snap.Track.Id);

        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new("Review memory", "memory-prompt-check"));

        var prompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        Assert.Contains("This Track's report projects live Memory entries", prompt, StringComparison.Ordinal);
        Assert.Contains("live read-only projection", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("The Report is empty.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Report-first", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`report_commit`", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`track_rename`", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`template_list`", prompt, StringComparison.Ordinal);
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
