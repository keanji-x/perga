using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

public sealed class SessionStreamingAndCompactionTests
{
    [Fact]
    public async Task EpochHandoverCompaction_ManualAndAutoAt80Percent_SummarizesDisposesBumpsEpochAndSeedsFreshSession()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        // Update report with distinctive content so we can verify fresh Epoch session receives latest Report markdown!
        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "# Architecture\n\nDISTILLATION_MARKER_V1", null)]));

        // Configure turn script: first regular turn hits 85% context usage (>= 0.80), triggering AUTO epoch handover compaction!
        var turnCallNum = 0;
        ctx.AgentRuntime.TurnScript = (session, input, ct) =>
            StreamScriptedTurn(session, input, Interlocked.Increment(ref turnCallNum), ct);

        var turn1 = await ctx.Kernel.SendMessageAsync(leadId, new SendMessageRequest("Analyze repo", "idem-auto-compact"));
        Assert.Equal("completed", turn1.Status);
        Assert.Equal(1, turn1.Epoch);

        // Auto-compaction ran automatically after turn1 because 85_000 / 100_000 == 0.85 >= 0.80!
        var sessionsAfterAuto = await ctx.Kernel.ListSessionsAsync(trackId);
        var leadAfterAuto = sessionsAfterAuto.Single(s => s.Id == leadId);
        Assert.Equal(2, leadAfterAuto.Epoch);
        Assert.Null(leadAfterAuto.NativeThreadId);
        Assert.Equal(0.0, leadAfterAuto.ContextUsageRatio);

        var openedArray = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Equal(2, openedArray.Length);

        // Old Epoch 1 IAgentSession was asked for structured handover summary and then DISPOSED!
        var epoch1Session = openedArray[0];
        Assert.True(epoch1Session.Disposed);
        Assert.Equal(2, epoch1Session.RecordedInputs.Count); // 1 user turn + 1 handover summary turn

        // Fresh Epoch 2 IAgentSession was opened with ResumeNativeThreadId == null and SystemPrompt containing BOTH latest Report + Handover Summary!
        var epoch2Session = openedArray[1];
        Assert.False(epoch2Session.Disposed);
        Assert.Null(epoch2Session.LaunchSpec.ResumeNativeThreadId);
        Assert.Contains("DISTILLATION_MARKER_V1", epoch2Session.LaunchSpec.SystemPrompt);
        Assert.Contains("HANDOVER_SUMMARY_EPOCH_1", epoch2Session.LaunchSpec.SystemPrompt);

        // Now test MANUAL CompactSessionAsync from Epoch 2 -> Epoch 3!
        var manuallyCompacted = await ctx.Kernel.CompactSessionAsync(leadId);
        Assert.Equal(3, manuallyCompacted.Epoch);
        Assert.Null(manuallyCompacted.NativeThreadId);
        Assert.Equal(0.0, manuallyCompacted.ContextUsageRatio);
        Assert.True(epoch2Session.Disposed);

        var allOpened = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Equal(3, allOpened.Length);
        var epoch3Session = allOpened[2];
        Assert.Null(epoch3Session.LaunchSpec.ResumeNativeThreadId);
        Assert.Contains("DISTILLATION_MARKER_V1", epoch3Session.LaunchSpec.SystemPrompt);
        Assert.Contains("HANDOVER_SUMMARY_EPOCH_2", epoch3Session.LaunchSpec.SystemPrompt);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamScriptedTurn(
        ScriptableFakeAgentSession session,
        TurnInput input,
        int callNumber,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        if (callNumber == 1)
        {
            yield return new UnifiedAgentEvent.ThreadBound("thr_epoch_1");
            yield return new UnifiedAgentEvent.TextDelta("Analysis complete.");
            // 85% context usage triggers auto-compaction (>= 0.80)
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(80_000, 5_000, 0, 100_000));
            yield break;
        }

        if (callNumber == 2)
        {
            // Auto-compaction handover summary turn on Epoch 1 session
            yield return new UnifiedAgentEvent.TextDelta("HANDOVER_SUMMARY_EPOCH_1: Verified parser & storage.");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1_000, 200, 0, 100_000));
            yield break;
        }

        // Manual compaction handover summary turn on Epoch 2 session
        yield return new UnifiedAgentEvent.TextDelta("HANDOVER_SUMMARY_EPOCH_2: Ready for final ship.");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1_000, 200, 0, 100_000));
    }

    [Fact]
    public async Task SendMessage_SupportsIdempotencyDedup_SteerIfRunning_Interrupt_AndApprovalBridge()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        // 1. Idempotency-Key deduplication
        var t1 = await ctx.Kernel.SendMessageAsync(leadId, new SendMessageRequest("Hello once", "same-key-1"));
        var t2 = await ctx.Kernel.SendMessageAsync(leadId, new SendMessageRequest("Hello once", "same-key-1"));
        Assert.Equal(t1.Id, t2.Id);
        Assert.Single(ctx.Store.GetTurnsForSession(leadId));

        // 2. ApprovalRequested bridged to AskDto + SteerIfRunning while turn is in-flight!
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => StreamWithApprovalAndGate(releaseTurn.Task, ct);

        var inFlightTurnTask = ctx.Kernel.SendMessageAsync(leadId, new SendMessageRequest("Run guarded shell", "key-approval"));

        // Wait for ApprovalRequested to materialize as an OpenAsk on the track
        AskDto? approvalAsk = null;
        for (var i = 0; i < 50 && approvalAsk is null; i++)
        {
            var agg = await ctx.Kernel.GetTrackAggregateAsync(trackId);
            approvalAsk = agg.OpenAsks.FirstOrDefault();
            if (approvalAsk is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(approvalAsk);

        // Steer while turn is running!
        var steeredTurn = await ctx.Kernel.SendMessageAsync(
            leadId,
            new SendMessageRequest("Use --release flag", "key-steer", SteerIfRunning: true));
        Assert.Equal("running", steeredTurn.Status);

        var activeFakeSession = ctx.AgentRuntime.OpenedSessions.Last();
        Assert.Contains("Use --release flag", activeFakeSession.SteeredMessages);

        // Answer the approval Ask -> unblocks RespondApprovalAsync on IAgentSession without extra duplicate wake turn!
        await ctx.Kernel.AnswerAskAsync(trackId, approvalAsk!.Id, TestJson.FromObject(new { approved = true }));
        releaseTurn.TrySetResult();

        var finishedTurn = await inFlightTurnTask;
        Assert.Equal("completed", finishedTurn.Status);
        Assert.Single(activeFakeSession.Approvals, a => a.RequestId == "req_42" && a.Approved);

        // Verify MCP token registration format (pg_<guid>)
        Assert.True(activeFakeSession.LaunchSpec.McpServers.TryGetValue("perga", out var pergaMcp));
        var httpMcp = Assert.IsType<McpServerConfig.HttpMcpServer>(pergaMcp);
        Assert.StartsWith("Bearer pg_", httpMcp.Headers["Authorization"], StringComparison.Ordinal);

        // 3. InterruptSessionAsync cancels an active running turn and marks it "interrupted"
        var neverEnding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => StreamBlockingUntilCanceled(neverEnding.Task, ct);

        var interruptedTurnTask = ctx.Kernel.SendMessageAsync(leadId, new SendMessageRequest("Long job", "key-interrupt"));
        for (var i = 0; i < 50; i++)
        {
            var page = await ctx.Kernel.GetSessionItemsAsync(leadId);
            if (page.ActiveTurn is not null)
            {
                break;
            }

            await Task.Delay(10);
        }

        await ctx.Kernel.InterruptSessionAsync(leadId);
        var interruptedTurn = await interruptedTurnTask;
        Assert.Equal("interrupted", interruptedTurn.Status);
        Assert.True(activeFakeSession.CancelCalled);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamBlockingUntilCanceled(
        Task blocker,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.TextDelta("Starting long work...");
        await blocker.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamWithApprovalAndGate(
        Task gateTask,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ToolCallStarted("call_1", "bash", TestJson.Parse("""{"cmd":"rm -rf tmp"}"""));
        yield return new UnifiedAgentEvent.ApprovalRequested("req_42", "bash", TestJson.Parse("""{"cmd":"rm -rf tmp"}"""));
        await gateTask.WaitAsync(ct);
        yield return new UnifiedAgentEvent.ToolCallCompleted("call_1", "ok", IsError: false);
        yield return new UnifiedAgentEvent.TextDelta("Completed approved command.");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(200, 50, 0, 100_000));
    }
}
