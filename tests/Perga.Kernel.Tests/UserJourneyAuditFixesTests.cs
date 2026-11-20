using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

public sealed class UserJourneyAuditFixesTests
{
    [Fact]
    public async Task TaskBlockSynchronizer_NormalizesProviderAliasesInKindWhenProviderIsNull()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [
                new BlockOp.UpsertBlockOp("b_codex", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    taskKey = "t-codex",
                    kind = "codex",
                    ready = false,
                    goalOrCmd = "Run on codex"
                })),
                new BlockOp.UpsertBlockOp("b_claude", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    taskKey = "t-claude",
                    kind = "claude",
                    ready = false,
                    goalOrCmd = "Run on claude"
                })),
                new BlockOp.UpsertBlockOp("b_agy", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    taskKey = "t-agy",
                    kind = "antigravity",
                    ready = false,
                    goalOrCmd = "Run on antigravity"
                })),
                new BlockOp.UpsertBlockOp("b_oc", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    taskKey = "t-opencode",
                    kind = "opencode",
                    ready = false,
                    goalOrCmd = "Run on opencode"
                })),
                new BlockOp.UpsertBlockOp("b_acp", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    taskKey = "t-acp-custom",
                    kind = "acp:custom-agent",
                    ready = false,
                    goalOrCmd = "Run on custom ACP"
                }))
            ]));

        var tasks = (await ctx.Kernel.ListTasksAsync(trackId)).ToDictionary(t => t.TaskKey);
        Assert.All(tasks.Values, t => Assert.Equal("agent", t.Kind));
        Assert.Equal("codex", tasks["t-codex"].Provider);
        Assert.Equal("claude", tasks["t-claude"].Provider);
        Assert.Equal("acp:antigravity", tasks["t-agy"].Provider);
        Assert.Equal("acp:opencode", tasks["t-opencode"].Provider);
        Assert.Equal("acp:custom-agent", tasks["t-acp-custom"].Provider);
    }

    [Fact]
    public async Task TerminalKindTask_RunsAsCommand_LowercasesAttemptStatus_IncludesGateStdoutStderr_AndSupportsHumanGovernance()
    {
        await using var ctx = new KernelTestContext();
        ctx.CommandRunner.Handler = (req, _) =>
        {
            if (req.Command == "echo build-ok")
            {
                return Task.FromResult(new ShellCommandResult(0, "build-ok\n", "", TimedOut: false, DurationMs: 5));
            }

            if (req.Command == "echo gate-pass")
            {
                return Task.FromResult(new ShellCommandResult(0, "gate-stdout-line\n", "gate-stderr-warn\n", TimedOut: false, DurationMs: 7));
            }

            if (req.Command == "exit 1")
            {
                return Task.FromResult(new ShellCommandResult(1, "", "fatal compile error\n", TimedOut: false, DurationMs: 4));
            }

            return Task.FromResult(new ShellCommandResult(0, "", "", TimedOut: false, DurationMs: 1));
        };

        var (_, snap) = await ctx.CreateDefaultTrackAsync(cwd: "/repo/term-task");
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;
        var sessionsBefore = ctx.AgentRuntime.OpenedSessions.Count;

        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [
                new BlockOp.UpsertBlockOp("b_term_1", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    taskKey = "term-build",
                    kind = "terminal",
                    access = "read_write",
                    ready = true,
                    command = "echo build-ok",
                    gate = new
                    {
                        steps = new[] { new { name = "smoke-gate", cmd = "echo gate-pass" } }
                    }
                })),
                new BlockOp.UpsertBlockOp("b_fail_1", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    taskKey = "term-fail",
                    kind = "terminal",
                    access = "read_only",
                    ready = true,
                    command = "exit 1",
                    maxRetries = 1
                })),
                new BlockOp.UpsertBlockOp("b_pending_1", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    taskKey = "human-cancel-target",
                    kind = "terminal",
                    ready = false,
                    command = "echo never-run"
                }))
            ]));

        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        // Verify kind: "terminal" ran via IShellCommandRunner (not subagent session; only Lead wake session opened)
        Assert.All(ctx.AgentRuntime.OpenedSessions, s => Assert.DoesNotContain("/leases/", s.LaunchSpec.Cwd, StringComparison.Ordinal));
        _ = sessionsBefore;

        var okAttempts = await ctx.Kernel.ListTaskAttemptsAsync(trackId, "term-build");
        var okAttempt = Assert.Single(okAttempts);
        Assert.Equal("done", okAttempt.Status);
        Assert.NotNull(okAttempt.GateVerdict);
        var step0 = okAttempt.GateVerdict!.Value.GetProperty("steps")[0];
        Assert.True(step0.GetProperty("passed").GetBoolean());
        Assert.Equal("gate-stdout-line\n", step0.GetProperty("stdout").GetString());
        Assert.Equal("gate-stderr-warn\n", step0.GetProperty("stderr").GetString());
        Assert.Contains("gate-stdout-line", step0.GetProperty("outputTail").GetString());
        Assert.Contains("gate-stderr-warn", step0.GetProperty("outputTail").GetString());

        var failAttempts = await ctx.Kernel.ListTaskAttemptsAsync(trackId, "term-fail");
        var failAttempt = Assert.Single(failAttempts);
        Assert.Equal("failed", failAttempt.Status);

        // Human governance (callerSessionId: null) for Cancel, Accept, and Reject!
        var canceled = await ctx.Kernel.CancelTaskAsync(trackId, callerSessionId: null, "human-cancel-target", "Not needed");
        Assert.Equal(TaskRunStatus.Canceled, canceled.Status);

        var accepted = await ctx.Kernel.AcceptTaskAsync(trackId, callerSessionId: null, "term-build", "Verified by human");
        Assert.Equal(TaskRunStatus.Done, accepted.Status);

        var rejected = await ctx.Kernel.RejectTaskAsync(trackId, callerSessionId: null, "term-fail", "Fix compile error", reQueue: false);
        Assert.Equal(TaskRunStatus.Failed, rejected.Status);
    }

    [Fact]
    public async Task TurnPipeline_IgnoresCallerCancellation_PersistsItemsIncrementally_AndPersistsTurnOutcomeOnFailureAndInterrupt()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        var midTurnReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ctx.AgentRuntime.TurnScript = (_, _, ct) =>
            StreamMidTurnCheckpoint(midTurnReached, allowFinish.Task, ct);

        using var callerCts = new CancellationTokenSource();
        var firstCallTask = ctx.SendAndWaitForTurnAsync(
            leadId,
            new SendMessageRequest("Build incremental items", "idem-decoupled"),
            callerCts.Token);

        await midTurnReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 1. Cancel caller HTTP token mid-turn: caller task cancels, but underlying turn MUST stay alive!
        callerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCallTask);

        // 2. While turn is STILL running, query GetSessionItemsAsync -> user, thinking, tool_call, tool_result are ALREADY persisted!
        var midPage = await ctx.Kernel.GetSessionItemsAsync(leadId);
        Assert.NotNull(midPage.ActiveTurn);
        Assert.NotNull(midPage.ActiveTurn!.UserItemId);
        Assert.Contains(midPage.Items, i => i.Kind == "user" && i.Id == midPage.ActiveTurn.UserItemId);
        Assert.Contains(midPage.Items, i => i.Kind == "thinking");
        Assert.Contains(midPage.Items, i => i.Kind == "tool_call");
        Assert.Contains(midPage.Items, i => i.Kind == "tool_result");

        // 3. Re-attaching with the same IdempotencyKey joins the running turn without spawning a second turn!
        var reattachedTask = ctx.SendAndWaitForTurnAsync(
            leadId,
            new SendMessageRequest("Build incremental items", "idem-decoupled"));

        allowFinish.TrySetResult();
        var completedTurn = await reattachedTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", completedTurn.Status);
        Assert.Equal(midPage.ActiveTurn.UserItemId, completedTurn.UserItemId);
        Assert.Single(ctx.Store.GetTurnsForSession(leadId));

        var afterCompletePage = await ctx.Kernel.GetSessionItemsAsync(leadId);
        Assert.Contains(afterCompletePage.Items, i => i.Kind == "assistant_text");

        // 4. Failed turn persists kind: "turn_outcome" with exact error text!
        ctx.AgentRuntime.TurnScript = (_, _, _) => StreamFailingTurn("model_not_found: invalid-model-x");
        var failedTurn = await ctx.SendAndWaitForTurnAsync(
            leadId,
            new SendMessageRequest("Trigger provider failure", "idem-fail-outcome"));
        Assert.Equal("failed", failedTurn.Status);

        var afterFailPage = await ctx.Kernel.GetSessionItemsAsync(leadId);
        var failOutcome = Assert.Single(
            afterFailPage.Items,
            i => i.Kind == "turn_outcome" && i.TurnId == failedTurn.Id);
        Assert.Equal("failed", failOutcome.Payload.GetProperty("status").GetString());
        Assert.Contains("model_not_found: invalid-model-x", failOutcome.Payload.GetProperty("error").GetString());

        // 5. Interrupted turn also persists kind: "turn_outcome" with status: "interrupted"!
        var blockForever = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedInterruptTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => StreamBlockingTurn(startedInterruptTurn, blockForever.Task, ct);

        var interruptTurnTask = ctx.SendAndWaitForTurnAsync(
            leadId,
            new SendMessageRequest("Long turn to interrupt", "idem-interrupt-outcome"));
        await startedInterruptTurn.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await ctx.Kernel.InterruptSessionAsync(leadId);
        var interruptedTurn = await interruptTurnTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("interrupted", interruptedTurn.Status);

        var afterInterruptPage = await ctx.Kernel.GetSessionItemsAsync(leadId);
        var interruptOutcome = Assert.Single(
            afterInterruptPage.Items,
            i => i.Kind == "turn_outcome" && i.TurnId == interruptedTurn.Id);
        Assert.Equal("interrupted", interruptOutcome.Payload.GetProperty("status").GetString());
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamMidTurnCheckpoint(
        TaskCompletionSource midTurnReached,
        Task allowFinish,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ThreadBound("thr_inc_1");
        yield return new UnifiedAgentEvent.ThinkingDelta("Step 1 reasoning...");
        yield return new UnifiedAgentEvent.ToolCallStarted("call_inc_1", "read_file", TestJson.Parse("""{"path":"README.md"}"""));
        yield return new UnifiedAgentEvent.ToolCallCompleted("call_inc_1", "# Perga", IsError: false);
        midTurnReached.TrySetResult();
        await allowFinish.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TextDelta("All incremental items persisted.");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(120, 40, 0, 100_000));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamFailingTurn(string errorMessage)
    {
        await Task.Yield();
        yield return new UnifiedAgentEvent.TurnError("model_not_found", errorMessage, IsRetryable: false, IsConclusive: true);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamBlockingTurn(
        TaskCompletionSource started,
        Task blocker,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ThinkingDelta("Working...");
        started.TrySetResult();
        try { await blocker.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        if (ct.IsCancellationRequested)
        {
            yield return new UnifiedAgentEvent.TurnInterrupted();
            yield break;
        }
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }
}
