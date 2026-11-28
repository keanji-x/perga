using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class TemplateEntryOptimizationTests
{
    private const string TemplateSource = """
        export default {
          id: 'research', title: 'Research', description: 'Compare sources and document findings.',
          inputs: { topic: { type: 'string', default: 'Example' } }
        };
        """;

    [Fact]
    public async Task LeadSystemPrompt_DescribesReportFirstAndListsTemplatesUpfront()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate(TemplateSource);
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new SendSessionMessageRequest("帮我做小米投研", "lead-prompt-check"));

        var leadPrompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;

        // Track Model + upfront template scoping
        Assert.Contains("## Track Model", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("**Title**", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("default to Report-first", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("before ending the turn", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("Keep your Conversation reply concise and point to the Report", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("brief one-off Q&A, small talk, or clarifying questions", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("Templates are optional and implicit", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("## Installed Templates", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("- `research` | Research | Compare sources and document findings. | inputKeys: [topic]", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("apply it directly with `track_set`", leadPrompt, StringComparison.Ordinal);
        Assert.Contains("`reportAnchor`", leadPrompt, StringComparison.Ordinal);
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

        Assert.Contains("## Current Report\n(empty)", leadPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("wholeDocumentRead", leadPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 10, true)]
    [InlineData(false, 11, false)]
    [InlineData(true, 1, false)]
    public async Task LeadSystemPrompt_InlinesOnlySmallUnboundCatalogs(bool bound, int templateCount, bool inlined)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate(TemplateSource);
        for (var i = 1; i < templateCount; i++)
            ctx.Kernel.Templates.RegisterTemplate($$"""export default { id: 'fixture-{{i}}', title: 'Fixture {{i}}' };""");
        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: bound ? "research" : null);
        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new("Review the scope", "lead-template-catalog"));

        var prompt = Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.SystemPrompt;
        Assert.Equal(inlined, prompt!.Contains("## Installed Templates", StringComparison.Ordinal));
        if (templateCount > 10)
            Assert.Contains("check `template_list`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeadTurnContext_UsesLiveEmptyAnchorUntilReportHasContent()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var leadId = snap.Track.LeadSessionId!;
        var empty = new ReadAnchor(0, new Dictionary<string, int>(), WholeDocumentRead: true);

        await ctx.SendAndWaitForTurnAsync(leadId, new("Clarify the goal", "lead-turn-1"));
        var revised = await ctx.Kernel.CommitReportAsync(snap.Track.Id, leadId, new(empty, [], Summary: "Scope clarified"));
        await ctx.SendAndWaitForTurnAsync(leadId, new("Draft the plan", "lead-turn-2"));
        var committed = await ctx.Kernel.CommitReportAsync(snap.Track.Id, leadId,
            new(revised.Anchor, [new BlockOp.ReplaceSectionOp("Overview", "An existing plan."), new BlockOp.ReplaceSectionOp("Next steps", "Confirm owners.")]));
        await ctx.SendAndWaitForTurnAsync(leadId, new("Update the plan", "lead-turn-3"));

        var session = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal(3, session.RecordedInputs.Count);
        var inputs = session.RecordedInputs.ToArray();
        Assert.Contains("""{"docRev":0,"knownBlockRevs":{},"wholeDocumentRead":true}""", inputs[0].Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`track_set`", inputs[0].Prompt, StringComparison.Ordinal);
        Assert.Contains($"\"docRev\":{revised.DocRev},\"knownBlockRevs\":{{}},\"wholeDocumentRead\":true", inputs[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("`report_commit`", inputs[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("`track_rename`", inputs[1].Prompt, StringComparison.Ordinal);
        Assert.EndsWith("</report_state>\n\nDraft the plan", inputs[1].Prompt, StringComparison.Ordinal);

        var outline = inputs[2].Prompt;
        Assert.StartsWith($"<report_state>docRev={committed.DocRev}; H1 sections=[\"Overview\",\"Next steps\"]", outline, StringComparison.Ordinal);
        Assert.Contains("`replace_section` or `edit_block`", outline, StringComparison.Ordinal);
        Assert.EndsWith("</report_state>\n\nUpdate the plan", outline, StringComparison.Ordinal);
        Assert.DoesNotContain("wholeDocumentRead", outline, StringComparison.Ordinal);
        Assert.DoesNotContain("An existing plan.", outline, StringComparison.Ordinal);
        Assert.DoesNotContain("<report_state>", session.LaunchSpec.SystemPrompt, StringComparison.Ordinal);
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

        var session = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var prompt = session.LaunchSpec.SystemPrompt;
        if (emptyAfterDeletion)
        {
            Assert.Contains("## Current Report\n(empty)", prompt, StringComparison.Ordinal);
            Assert.Contains("""{"docRev":2,"knownBlockRevs":{},"wholeDocumentRead":true}""",
                Assert.Single(session.RecordedInputs).Prompt, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"## Current Report\n{committed.RenderedMarkdown}", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("(empty)", prompt, StringComparison.Ordinal);
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

        var session = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var prompt = session.LaunchSpec.SystemPrompt;
        Assert.Contains("## Current Report\n<!-- track header -->", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Current Report\n(empty)", prompt, StringComparison.Ordinal);
        Assert.Contains("<report_state>docRev=0", Assert.Single(session.RecordedInputs).Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("wholeDocumentRead", Assert.Single(session.RecordedInputs).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryLeadSystemPrompt_OmitsEmptyReportWriteGuidance()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate(TemplateSource);
        var (area, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snap.Track.Id);
        ctx.Store.MemoryBinding = new(MemoryTrackBinding.ScopeKeyFor(area.Id), area.Id, snap.Track.Id);

        await ctx.SendAndWaitForTurnAsync(snap.Track.LeadSessionId!, new("Review memory", "memory-prompt-check"));

        var session = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var prompt = session.LaunchSpec.SystemPrompt;
        Assert.Contains("This Track's report projects live Memory entries", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Current Report\n(empty)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Report-first", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Installed Templates", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<report_state>", Assert.Single(session.RecordedInputs).Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubagentSystemPrompt_OmitsEmptyReportWriteGuidance()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate(TemplateSource);
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        await ctx.Kernel.CreateSessionAsync(snap.Track.Id, new CreateSessionRequest(Title: "Helper", Prompt: "Inspect the workspace"),
            requestKey: "subagent-prompt-check", caller: new InteropPrincipal.Human(), ct: default);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);

        var session = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var prompt = session.LaunchSpec.SystemPrompt;
        Assert.Contains("Collaborative Subagent", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Current Report\n(empty)", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Installed Templates", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`report_commit`", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("`track_rename`", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<report_state>", Assert.Single(session.RecordedInputs).Prompt, StringComparison.Ordinal);
    }
}
