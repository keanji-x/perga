using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

public sealed class BoundAttemptFenceAndGovernanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeadAndWorkerCanEditProseButCannotAddTaskBlocks(bool worker)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var session = worker
            ? (await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("worker", "Inspect"))).Session.Id
            : snap.Track.LeadSessionId!;
        var initial = await ctx.Kernel.ReadReportAsync(trackId, session);
        var prose = await ctx.Kernel.CommitReportAsync(trackId, session, new(initial.Anchor,
            [new BlockOp.UpsertBlockOp("notes", ReportBlockKind.Prose, "# Findings\n\nNotes.", null)]));
        Assert.Equal(initial.DocRev + 1, prose.DocRev);

        BlockOp[] edits =
        [
            new BlockOp.UpsertBlockOp(null, ReportBlockKind.Task, null, TestJson.Parse("""{"task_key":"added","goal_or_cmd":"x"}""")),
            new BlockOp.ReplaceSectionOp("Findings", "```perga-block task\n{\"task_key\":\"added\",\"goal_or_cmd\":\"x\"}\n```")
        ];
        foreach (var edit in edits)
        {
            var error = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.CommitReportAsync(trackId, session, new(prose.Anchor, [edit])));
            Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
            Assert.Contains("flow_commit", error.Message);
        }
        Assert.Equal(prose.DocRev, (await ctx.Kernel.ReadReportAsync(trackId, session)).DocRev);
        Assert.Equal(worker ? 1 : 0, (await ctx.Kernel.ListTasksAsync(trackId)).Count);
    }

    [Fact]
    public async Task HistoricalReportTaskBlocksRemainReadableButCannotBeRewritten()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var files = new Dictionary<string, string>
        {
            ["template.perga.js"] = """
                export default defineTemplate({id:'historical', title:'Historical', report:{template:'./report.md'}});
                """,
            ["report.md"] = """
                # History

                ```perga-block task
                {"task_key":"legacy","goal_or_cmd":"historical"}
                ```
                """
        };
        ctx.Kernel.Templates.RegisterTemplate(TemplateBundle.BundleHeader +
            System.Text.Json.JsonSerializer.Serialize(new { entry = "template.perga.js", files }));
        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "historical");
        var trackId = snap.Track.Id;
        var lead = snap.Track.LeadSessionId!;
        var read = await ctx.Kernel.ReadReportAsync(trackId, lead);
        var legacy = Assert.Single(read.Blocks, block => block.Kind == ReportBlockKind.Task);
        Assert.Empty(read.Tasks);
        var failure = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.CommitReportAsync(trackId, lead,
            new(read.Anchor, [new BlockOp.UpsertBlockOp(legacy.Id, ReportBlockKind.Task, null,
                TestJson.Parse("""{"task_key":"legacy","goal_or_cmd":"replacement"}"""))])));
        Assert.Equal(KernelErrorCode.InvalidArgument, failure.Code);

        var after = await ctx.Kernel.CommitReportAsync(trackId, lead,
            new(read.Anchor, [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "Progress.", null)]));
        Assert.Equal(legacy.Rev, Assert.Single(after.Blocks, block => block.Id == legacy.Id).Rev);
        Assert.Empty(after.Tasks);
    }

    [Fact]
    public async Task GuardReportCommit_SupportsAllowDenyAndAskHumanSuspension()
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate("""export default defineTemplate({ id: "guarded", title: "Guarded" });""");
        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "guarded");
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        // 1. Deny
        ctx.PolicyVm.OnBefore = (_, action, _, _) =>
            new GuardDecision.Deny("Prose freeze active");

        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        var denyEx = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
                r0.Anchor,
                [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "Blocked text", null)])));
        Assert.Equal(KernelErrorCode.PolicyDenied, denyEx.Code);

        // 2. AskHuman -> suspends without blocking actor -> human approves -> commit succeeds!
        ctx.PolicyVm.OnBefore = (_, action, _, _) =>
            new GuardDecision.AskHuman("Approve report edit?");

        var expired = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.CommitReportAsync(trackId, leadId,
            new ReportCommitRequest(r0.Anchor, [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "Stale request", null)]),
            origin: new McpExecutionOrigin("expired-turn")));
        Assert.Equal(KernelErrorCode.Conflict, expired.Code);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals);

        var suspendedCommitTask = ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "Human-gated text", null)]));

        // Wait until OpenAsk appears on the track
        ApprovalDto? openAsk = null;
        for (var i = 0; i < 50 && openAsk is null; i++)
        {
            var agg = await ctx.Kernel.GetTrackAggregateAsync(trackId);
            openAsk = agg.PendingApprovals.FirstOrDefault();
            if (openAsk is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(openAsk);
        Assert.False(suspendedCommitTask.IsCompleted);

        await ctx.Kernel.RespondInteractionAsync(openAsk!.SessionId, openAsk!.Id, new(ExecutionId: openAsk!.ExecutionId, OptionId: openAsk.Options[0].Id), new InteropPrincipal.Human(), default);
        var approvedCommit = await suspendedCommitTask;
        Assert.Equal(1, approvedCommit.DocRev);
    }
}
