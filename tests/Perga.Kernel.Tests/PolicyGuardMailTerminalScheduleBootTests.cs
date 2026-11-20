using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Tests.Fakes;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Kernel.Tests;

public sealed class PolicyGuardMailTerminalScheduleBootTests
{
    [Fact]
    public async Task GuardMcpToolCall_EnforcesAllowDenyAndAskHumanSuspension()
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate(
            """
            export default defineTemplate({
              id: "sec-tpl",
              title: "Security Policy"
            });
            """);

        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "sec-tpl");
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        // 1. Allow
        ctx.PolicyVm.OnBefore = (_, action, _, state) =>
            new PolicyEvalResult<GuardDecision>(state, new GuardDecision.Allow());
        await ctx.Kernel.GuardMcpToolCallAsync(trackId, leadId, "vcs", "vcs_status", TestJson.Parse("{}"));

        // 2. Disabled plugin rejected immediately
        var disabledEx = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.GuardMcpToolCallAsync(trackId, leadId, "unapproved-plugin", "fetch", TestJson.Parse("{}")));
        Assert.Equal(KernelErrorCode.Forbidden, disabledEx.Code);

        // 3. Policy Deny
        ctx.PolicyVm.OnBefore = (_, action, call, state) =>
            new PolicyEvalResult<GuardDecision>(state, new GuardDecision.Deny($"Blocked tool {call.GetProperty("toolName").GetString()}"));
        var denyEx = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.GuardMcpToolCallAsync(trackId, leadId, "vcs", "vcs_pr_merge", TestJson.Parse("{}")));
        Assert.Equal(KernelErrorCode.PolicyDenied, denyEx.Code);

        // 4. Policy AskHuman -> suspends until human approves -> allows; if human denies -> throws PolicyDenied!
        ctx.PolicyVm.OnBefore = (_, action, _, state) =>
            new PolicyEvalResult<GuardDecision>(state, new GuardDecision.AskHuman("Allow vcs_push?", ["Approve", "Reject"]));

        var guardTask = ctx.Kernel.GuardMcpToolCallAsync(trackId, leadId, "vcs", "vcs_publish_pr", TestJson.Parse("{}"));
        ApprovalDto? openAsk = null;
        for (var i = 0; i < 50 && openAsk is null; i++)
        {
            openAsk = (await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals.FirstOrDefault();
            if (openAsk is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(openAsk);
        Assert.False(guardTask.IsCompleted);

        await ctx.Kernel.DecideApprovalAsync(trackId, openAsk!.ExecutionId, openAsk!.Id, openAsk.Options.Single(o => o.IsApproval).Id);
        await guardTask; // Completes cleanly!

        // Now verify human rejection throws PolicyDenied
        var rejectGuardTask = ctx.Kernel.GuardMcpToolCallAsync(trackId, leadId, "vcs", "vcs_pr_merge", TestJson.Parse("{}"));
        ApprovalDto? rejectAsk = null;
        for (var i = 0; i < 50 && rejectAsk is null; i++)
        {
            rejectAsk = (await ctx.Kernel.GetTrackAggregateAsync(trackId)).PendingApprovals.FirstOrDefault();
            if (rejectAsk is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(rejectAsk);
        await ctx.Kernel.DecideApprovalAsync(trackId, rejectAsk!.ExecutionId, rejectAsk!.Id, rejectAsk.Options.Single(o => !o.IsApproval).Id);
        var humanRejectEx = await Assert.ThrowsAsync<KernelException>(() => rejectGuardTask);
        Assert.Equal(KernelErrorCode.PolicyDenied, humanRejectEx.Code);
    }

    [Fact]
    public async Task CrossTrackMail_PersistsToTarget_WakesTargetLead_AndSupportsListAndCat()
    {
        await using var ctx = new KernelTestContext();
        var (area, senderSnap) = await ctx.CreateDefaultTrackAsync();
        var receiverSnap = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Title: "Receiver Track"));

        var senderTrackId = senderSnap.Track.Id;
        var senderLeadId = senderSnap.Track.LeadSessionId!;
        var receiverTrackId = receiverSnap.Track.Id;
        var receiverLeadId = receiverSnap.Track.LeadSessionId!;

        Assert.Empty(ctx.Store.GetTurnsForSession(receiverLeadId));

        var sentMail = await ctx.Kernel.SendMailAsync(
            senderTrackId,
            senderLeadId,
            receiverTrackId,
            "API Schema Ready",
            "All 18 MCP tools landed.");

        // Receiver track LeadSessionId was automatically woken!
        var receiverTurns = ctx.Store.GetTurnsForSession(receiverLeadId);
        Assert.Single(receiverTurns);
        Assert.Equal("wake", receiverTurns[0].TriggerKind);

        var unread = await ctx.Kernel.ListMailsAsync(receiverTrackId, unreadOnly: true);
        Assert.Single(unread);
        Assert.Equal(sentMail.Id, unread[0].Id);
        Assert.Null(unread[0].ReadAtMs);

        var readMail = await ctx.Kernel.ReadMailAsync(receiverTrackId, sentMail.Id);
        Assert.NotNull(readMail.ReadAtMs);
        Assert.Empty(await ctx.Kernel.ListMailsAsync(receiverTrackId, unreadOnly: true));
    }

    [Fact]
    public async Task Terminals_Schedules_HumanReportEdit_AndMcpCatalog_WorkEndToEnd()
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate("""export default defineTemplate({ id: "ops-tpl", title: "Ops" });""");
        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "ops-tpl");
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        // 1. Terminal IdempotencyKey deduplication + observe exit status update
        var term1 = await ctx.Kernel.OpenTerminalAsync(trackId, leadId, new OpenTerminalRequest(Title: "Dev", IdempotencyKey: "term-k1"));
        var term2 = await ctx.Kernel.OpenTerminalAsync(trackId, leadId, new OpenTerminalRequest(Title: "Dev", IdempotencyKey: "term-k1"));
        Assert.Equal(term1.Id, term2.Id);
        Assert.Single(ctx.TerminalClient.EnsureCalls);

        ctx.TerminalClient.ObserveHandler = _ => new TermObserveResult(
            5,
            new TerminalFrame(120, 40, new TerminalCursor(0, 1, true), false, 0, 0, 0, ["done"]),
            Exited: true,
            ExitCode: 0);

        var obs = await ctx.Kernel.ObserveTerminalAsync(trackId, new TermObserveRequest(term1.Id));
        Assert.True(obs.Exited);
        var terms = await ctx.Kernel.ListTerminalsAsync(trackId);
        Assert.Equal(TerminalStatus.Exited, terms[0].Status);

        // 2. Schedule wake invokes after(schedule.fired) and wakes LeadSessionId
        var turnsBeforeSched = ctx.Store.GetTurnsForSession(leadId).Count;
        await ctx.Kernel.TriggerTrackScheduleAsync(trackId, "daily-check");
        Assert.Equal(turnsBeforeSched + 1, ctx.Store.GetTurnsForSession(leadId).Count);

        // 3. Human single-block update (PUT /api/v1/tracks/{trackId}/report/blocks/{blockId}) wakes LeadSessionId
        var emptyReport = await ctx.Kernel.ReadReportAsync(trackId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new(emptyReport.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "# Overview\n\nEditable content.\n", null)]));
        var reportBefore = await ctx.Kernel.ReadReportAsync(trackId);
        var targetBlock = reportBefore.Blocks[0];
        var turnsBeforeHumanEdit = ctx.Store.GetTurnsForSession(leadId).Count;

        var afterHuman = await ctx.Kernel.UpdateReportBlockByHumanAsync(
            trackId,
            targetBlock.Id,
            new HumanBlockUpdateRequest(
                DocRev: reportBefore.DocRev,
                KnownBlockRev: targetBlock.Rev,
                Markdown: "# Overview\n\nEdited by human in UI."));
        Assert.Equal(reportBefore.DocRev + 1, afterHuman.DocRev);
        Assert.Equal(turnsBeforeHumanEdit + 1, ctx.Store.GetTurnsForSession(leadId).Count);

        // 4. Verify the catalog exposes Worker controls only to Lead.
        Assert.Equal(35, KernelMcpToolNames.All.Count);
        Assert.Contains(KernelMcpCatalog.GetToolsForSession(boundAttemptId: null, isLead: true), tool => tool.Name == KernelMcpToolNames.SessionSend);
        Assert.Equal(23, KernelMcpCatalog.GetToolsForSession(boundAttemptId: null).Count);
        Assert.Equal(13, KernelMcpCatalog.GetToolsForSession(boundAttemptId: "att_1").Count);

        var reg = ctx.Kernel.McpSessionBridge.RegisterSession(trackId, leadId, isLead: true, null, null, ["market"]);
        var statusJson = await ctx.Kernel.InvokeKernelMcpToolAsync(reg.Principal, KernelMcpToolNames.TrackStatus, TestJson.Parse("{}"));
        Assert.Equal(trackId, statusJson.GetProperty("track").GetProperty("id").GetString());
    }

    [Fact]
    public async Task BootReconciler_DiscardsSurvivingAttemptLeases_MarksInterrupted_AndWakesOpenTracksOnly()
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Boot Area"));
        var emptyJson = TestJson.Parse("{}");

        // Open track with an in-flight Running attempt holding a WorkspaceLease
        var openTrack = new TrackDto(
            "trk_boot_open", area.Id, null, null, "Open Interrupted Track", "/repo/open", "direct",
            emptyJson, "ses_open_lead", null, emptyJson, emptyJson, [], ClosedAtMs: null, PinnedAtMs: null, 0, 1000, 1000);
        var openLead = new AgentSessionDto(
            "ses_open_lead", "trk_boot_open", true, null, null, "Lead", "codex", null, null, 1, "idle", null, 1000, 1000);
        var inFlightTask = new TaskDto(
            "tsk_inflight", "trk_boot_open", "b_1", "crashed-task", 1, "agent", null, AccessMode.ReadWrite, true, [],
            "Build", null, null, null, TaskRunStatus.Running, null, SemanticVerdict.None, null, "att_surviving", 1000, 1000);
        var survivingLease = new AttemptWorkspaceLease(
            DriverId: "direct",
            AttemptId: "att_surviving",
            EffectiveCwd: "/repo/open/leases/att_surviving",
            IsIsolatedCopyOnWrite: true,
            Metadata: emptyJson);
        var inFlightAttempt = new TaskAttemptDto(
            "att_surviving", "tsk_inflight", "trk_boot_open", null, "Running", null, emptyJson,
            TestJson.FromObject(survivingLease), null, 1000, null);

        ctx.Store.SeedDirectAggregate(
            openTrack,
            new ReportDocument(0, "", "", []),
            [inFlightTask],
            [inFlightAttempt],
            [openLead]);

        // Closed track with an interrupted turn -> should be reconciled in DB, NOT woken!
        var closedTrack = new TrackDto(
            "trk_boot_closed", area.Id, null, null, "Closed Track", "/repo/closed", "direct",
            emptyJson, "ses_closed_lead", null, emptyJson, emptyJson, [], ClosedAtMs: 2000, PinnedAtMs: null, 0, 1000, 2000);
        var closedLead = new AgentSessionDto(
            "ses_closed_lead", "trk_boot_closed", true, null, null, "Lead", "codex", null, null, 1, "running", null, 1000, 1000);
        ctx.Store.SeedDirectAggregate(
            closedTrack,
            new ReportDocument(0, "", "", []),
            [],
            [],
            [closedLead]);

        var reconcileResult = await ctx.Kernel.ReconcileOnBootAsync();

        Assert.Equal(1, reconcileResult.DiscardedLeases);
        Assert.Equal(1, reconcileResult.WokenTracks);
        Assert.Equal(1, ctx.Store.MarkInterruptedCalls);
        Assert.Equal(["trk_boot_closed", "trk_boot_open"], reconcileResult.AffectedTrackIds);

        // Verify surviving lease was settled with AttemptLeaseDisposition.Discard!
        var discarded = Assert.Single(ctx.DirectDriver.SettleLeaseCalls);
        Assert.Equal("att_surviving", discarded.Lease.AttemptId);
        Assert.Equal(AttemptLeaseDisposition.Discard, discarded.Disposition);

        // Verify open track's LeadSessionId received system wake turn, and closed track's Lead did NOT!
        var openLeadTurns = ctx.Store.GetTurnsForSession("ses_open_lead");
        Assert.Single(openLeadTurns);
        Assert.Equal("system", openLeadTurns[0].TriggerKind);
        Assert.Empty(ctx.Store.GetTurnsForSession("ses_closed_lead"));
    }

    [Fact]
    public async Task AskUserSuspension_And_WorkspaceFileTraversalFence_WorkCorrectly()
    {
        var tempWs = Path.Combine(Path.GetTempPath(), $"perga-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWs);
        await File.WriteAllTextAsync(Path.Combine(tempWs, "hello.txt"), "safe-content");

        try
        {
            await using var ctx = new KernelTestContext();
            var (_, snap) = await ctx.CreateDefaultTrackAsync(cwd: tempWs);
            var trackId = snap.Track.Id;
            var leadId = snap.Track.LeadSessionId!;
            var sub = await ctx.Kernel.CreateSubagentSessionAsync(trackId, new CreateSessionRequest(Title: "Worker"));

            // 1. Subagent calls AskUserAsync -> suspends -> AnswerAskAsync unblocks waiter AND wakes LeadSessionId!
            var askTask = ctx.Kernel.AskUserAsync(
                trackId,
                sub.Id,
                [new AskQuestionDto("Which deployment target?", ["staging", "prod"])]);

            AskDto? pendingAsk = null;
            for (var i = 0; i < 50 && pendingAsk is null; i++)
            {
                pendingAsk = (await ctx.Kernel.GetTrackAggregateAsync(trackId)).OpenAsks.FirstOrDefault();
                if (pendingAsk is null)
                {
                    await Task.Delay(10);
                }
            }

            Assert.NotNull(pendingAsk);
            var leadTurnsBefore = ctx.Store.GetTurnsForSession(leadId).Count;

            await ctx.Kernel.AnswerAskAsync(trackId, pendingAsk!.Id, TestJson.FromObject(new { choice = "staging" }));
            var resolvedAnswer = await askTask;
            Assert.Equal("staging", resolvedAnswer.GetProperty("choice").GetString());
            Assert.Equal(leadTurnsBefore + 1, ctx.Store.GetTurnsForSession(leadId).Count);

            // 2. Safe workspace file browse/read works, path traversal outside workspace root is strictly blocked!
            var fileRes = await ctx.Kernel.BrowseOrReadWorkspaceFileAsync(trackId, "hello.txt");
            Assert.False(fileRes.IsDirectory);
            Assert.Equal("safe-content", fileRes.Content);

            var traversalEx = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.BrowseOrReadWorkspaceFileAsync(trackId, "../../etc/passwd"));
            Assert.Equal(KernelErrorCode.Forbidden, traversalEx.Code);
        }
        finally
        {
            if (Directory.Exists(tempWs))
            {
                Directory.Delete(tempWs, recursive: true);
            }
        }
    }
}
