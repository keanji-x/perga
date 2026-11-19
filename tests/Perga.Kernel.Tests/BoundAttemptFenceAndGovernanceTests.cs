using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

public sealed class BoundAttemptFenceAndGovernanceTests
{
    [Fact]
    public async Task BoundAttemptIdFence_StrictlyBlocksAllTaskBlockMutations_WhileAllowingProseCommits()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        // Lead adds a ready Task block + a Prose block -> spawns contract Subagent with BoundAttemptId != null
        var read0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        var taskPayload = TestJson.FromObject(new
        {
            task_key = "impl-feature",
            access = "read_write",
            ready = true,
            goal_or_cmd = "Implement feature X"
        });

        var committed = await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            Anchor: read0.Anchor,
            Ops:
            [
                new BlockOp.UpsertBlockOp("b_task1", ReportBlockKind.Task, null, taskPayload),
                new BlockOp.UpsertBlockOp("b_notes", ReportBlockKind.Prose, "# Findings\n\nInitial notes.", null)
            ]));

        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var sessions = await ctx.Kernel.ListSessionsAsync(trackId);
        var contractSubagent = Assert.Single(sessions, s => s.BoundAttemptId is not null);

        // 1. Contract Subagent CAN append/edit Prose blocks
        var subRead1 = await ctx.Kernel.ReadReportAsync(trackId, contractSubagent.Id);
        var proseResult = await ctx.Kernel.CommitReportAsync(trackId, contractSubagent.Id, new ReportCommitRequest(
            Anchor: subRead1.Anchor,
            Ops: [new BlockOp.UpsertBlockOp("b_notes", ReportBlockKind.Prose, "# Findings\n\nWorker progress notes.", null)]));
        Assert.Equal(committed.DocRev + 1, proseResult.DocRev);

        var freshRead = await ctx.Kernel.ReadReportAsync(trackId, contractSubagent.Id);

        // 2. Rejects creating a new Task block
        var exCreate = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(trackId, contractSubagent.Id, new ReportCommitRequest(
                Anchor: freshRead.Anchor,
                Ops: [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Task, null, TestJson.Parse("""{"task_key":"rogue","goal_or_cmd":"x"}"""))])));
        Assert.Equal(KernelErrorCode.Forbidden, exCreate.Code);

        // 3. Rejects editing existing Task block (even disguised as Prose kind!)
        var exEdit = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(trackId, contractSubagent.Id, new ReportCommitRequest(
                Anchor: freshRead.Anchor,
                Ops: [new BlockOp.UpsertBlockOp("b_task1", ReportBlockKind.Prose, "hijacked", null)])));
        Assert.Equal(KernelErrorCode.Forbidden, exEdit.Code);

        // 4. Rejects moving existing Task block
        var exMove = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(trackId, contractSubagent.Id, new ReportCommitRequest(
                Anchor: freshRead.Anchor,
                Ops: [new BlockOp.MoveBlockOp("b_task1", 0)])));
        Assert.Equal(KernelErrorCode.Forbidden, exMove.Code);

        // 5. Rejects deleting existing Task block
        var exDelete = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(trackId, contractSubagent.Id, new ReportCommitRequest(
                Anchor: freshRead.Anchor,
                Ops: [new BlockOp.DeleteBlockOp("b_task1")])));
        Assert.Equal(KernelErrorCode.Forbidden, exDelete.Code);

        // 6. Rejects ReplaceSectionOp that smuggles a Task block
        var exSmuggle = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(trackId, contractSubagent.Id, new ReportCommitRequest(
                Anchor: freshRead.Anchor,
                Ops: [new BlockOp.ReplaceSectionOp("Findings", "```perga-block task\n{\"task_key\":\"sneaky\",\"goal_or_cmd\":\"y\"}\n```")])));
        Assert.Equal(KernelErrorCode.Forbidden, exSmuggle.Code);
    }

    [Fact]
    public async Task GovernanceVsCompletionFences_StrictlyEnforceBoundAttemptId()
    {
        await using var ctx = new KernelTestContext();
        var (area, snap1) = await ctx.CreateDefaultTrackAsync();
        var track2 = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Target Track"));

        var trackId = snap1.Track.Id;
        var leadId = snap1.Track.LeadSessionId!;
        var freeSubagent = await ctx.Kernel.CreateSubagentSessionAsync(trackId, new CreateSessionRequest(Title: "Free Helper"));

        // Add a task so a bound contract subagent is spawned
        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [new BlockOp.UpsertBlockOp("b_t1", ReportBlockKind.Task, null, TestJson.Parse("""{"task_key":"t1","ready":true,"goal_or_cmd":"work"}"""))]));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var boundSubagent = Assert.Single(await ctx.Kernel.ListSessionsAsync(trackId), s => s.BoundAttemptId is not null);

        // A. Unbound sessions (Lead & free Subagent) CANNOT call perga_task_done or perga_task_fail
        foreach (var unboundId in new[] { leadId, freeSubagent.Id })
        {
            var doneEx = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.CompleteAttemptDoneAsync(trackId, unboundId));
            Assert.Equal(KernelErrorCode.Forbidden, doneEx.Code);

            var failEx = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.CompleteAttemptFailAsync(trackId, unboundId, "nope"));
            Assert.Equal(KernelErrorCode.Forbidden, failEx.Code);
        }

        // B. Bound contract Subagent CANNOT call ANY of the 6 governance tools
        Assert.Equal(KernelErrorCode.Forbidden,
            (await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.CancelTaskAsync(trackId, boundSubagent.Id, "t1"))).Code);
        Assert.Equal(KernelErrorCode.Forbidden,
            (await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.AcceptTaskAsync(trackId, boundSubagent.Id, "t1"))).Code);
        Assert.Equal(KernelErrorCode.Forbidden,
            (await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.RejectTaskAsync(trackId, boundSubagent.Id, "t1", "self-reject"))).Code);
        Assert.Equal(KernelErrorCode.Forbidden,
            (await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.SendMailAsync(trackId, boundSubagent.Id, track2.Track.Id, "hi", "body"))).Code);
        Assert.Equal(KernelErrorCode.Forbidden,
            (await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.RenameTrackAsync(trackId, boundSubagent.Id, "Hacked"))).Code);
        Assert.Equal(KernelErrorCode.Forbidden,
            (await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.CloseTrackAsync(trackId, boundSubagent.Id))).Code);

        // C. Unbound free Subagent & Lead CAN execute governance actions
        var renamed = await ctx.Kernel.RenameTrackAsync(trackId, freeSubagent.Id, "Collaboratively Renamed");
        Assert.Equal("Collaboratively Renamed", renamed.Title);

        var accepted = await ctx.Kernel.AcceptTaskAsync(trackId, leadId, "t1", "Looks solid so far");
        Assert.Equal(SemanticVerdict.Accepted, accepted.SemanticVerdict);

        var canceled = await ctx.Kernel.CancelTaskAsync(trackId, leadId, "t1", "Aborting test task");
        Assert.Equal(TaskRunStatus.Canceled, canceled.Status);
    }

    [Fact]
    public async Task TaskSync_PreservesUnchangedTaskGeneration_AndBumpsGenerationOnContractChange()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        var r1 = await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [
                new BlockOp.UpsertBlockOp("b_t1", ReportBlockKind.Task, null,
                    TestJson.Parse("""{"task_key":"build-api","ready":false,"goal_or_cmd":"v1","acceptance":"pass v1"}"""))
            ]));

        var tGen1 = Assert.Single(r1.Tasks);
        Assert.Equal(1, tGen1.Generation);
        Assert.Equal(TaskRunStatus.Draft, tGen1.Status);

        // Editing prose only preserves Generation == 1 and Task Id
        var r2 = await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r1.Anchor,
            [new BlockOp.UpsertBlockOp("b_prose", ReportBlockKind.Prose, "Extra context", null)]));
        var tStillGen1 = Assert.Single(r2.Tasks);
        Assert.Equal(tGen1.Id, tStillGen1.Id);
        Assert.Equal(1, tStillGen1.Generation);

        // Updating goal_or_cmd / acceptance / gate bumps Generation to 2!
        var r3 = await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r2.Anchor,
            [
                new BlockOp.UpsertBlockOp("b_t1", ReportBlockKind.Task, null,
                    TestJson.Parse("""{"task_key":"build-api","ready":false,"goal_or_cmd":"v2","acceptance":"pass v2","gate":{"steps":[{"name":"test","cmd":"dotnet test"}]}}"""))
            ]));
        var tGen2 = Assert.Single(r3.Tasks);
        Assert.Equal(tGen1.Id, tGen2.Id);
        Assert.Equal(2, tGen2.Generation);
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
        ctx.PolicyVm.OnBefore = (_, action, _, state) =>
            new PolicyEvalResult<GuardDecision>(state, new GuardDecision.Deny("Prose freeze active"));

        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        var denyEx = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
                r0.Anchor,
                [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "Blocked text", null)])));
        Assert.Equal(KernelErrorCode.PolicyDenied, denyEx.Code);

        // 2. AskHuman -> suspends without blocking actor -> human approves -> commit succeeds!
        ctx.PolicyVm.OnBefore = (_, action, _, state) =>
            new PolicyEvalResult<GuardDecision>(state, new GuardDecision.AskHuman("Approve report edit?", ["Approve", "Deny"]));

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

        await ctx.Kernel.DecideApprovalAsync(trackId, openAsk!.ExecutionId, openAsk!.Id, TestJson.FromObject(new { answers = new[] { "Approve" } }));
        var approvedCommit = await suspendedCommitTask;
        Assert.Equal(1, approvedCommit.DocRev);
    }
}
