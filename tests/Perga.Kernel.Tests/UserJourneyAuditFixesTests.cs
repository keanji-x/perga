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
            new SendSessionMessageRequest("Build incremental items", "idem-decoupled"),
            callerCts.Token);

        await midTurnReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 1. Cancel caller HTTP token mid-turn: caller task cancels, but underlying turn MUST stay alive!
        callerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstCallTask);

        // 2. While turn is STILL running, query GetSessionItemsAsync -> user, thinking, tool_call, tool_result are ALREADY persisted!
        var midPage = await ctx.Kernel.GetSessionItemsAsync(leadId, new(), new InteropPrincipal.Human(), default);
        Assert.NotNull(midPage.ActiveTurn);
        Assert.NotNull(midPage.ActiveTurn!.UserItemId);
        Assert.Contains(midPage.Items, i => i.Kind == "user" && i.Id == midPage.ActiveTurn.UserItemId);
        Assert.Contains(midPage.Items, i => i.Kind == "thinking");
        Assert.Contains(midPage.Items, i => i.Kind == "tool_call");
        Assert.Contains(midPage.Items, i => i.Kind == "tool_result");

        // 3. Re-attaching with the same IdempotencyKey joins the running turn without spawning a second turn!
        var reattachedTask = ctx.SendAndWaitForTurnAsync(
            leadId,
            new SendSessionMessageRequest("Build incremental items", "idem-decoupled"));

        allowFinish.TrySetResult();
        var completedTurn = await reattachedTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", completedTurn.Status);
        Assert.Equal(midPage.ActiveTurn.UserItemId, completedTurn.UserItemId);
        Assert.Single(ctx.Store.GetTurnsForSession(leadId));

        var afterCompletePage = await ctx.Kernel.GetSessionItemsAsync(leadId, new(), new InteropPrincipal.Human(), default);
        Assert.Contains(afterCompletePage.Items, i => i.Kind == "assistant_text");

        // 4. Failed turn persists kind: "turn_outcome" with exact error text!
        ctx.AgentRuntime.TurnScript = (_, _, _) => StreamFailingTurn("model_not_found: invalid-model-x");
        var failedTurn = await ctx.SendAndWaitForTurnAsync(
            leadId,
            new SendSessionMessageRequest("Trigger provider failure", "idem-fail-outcome"));
        Assert.Equal("failed", failedTurn.Status);

        var afterFailPage = await ctx.Kernel.GetSessionItemsAsync(leadId, new(), new InteropPrincipal.Human(), default);
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
            new SendSessionMessageRequest("Long turn to interrupt", "idem-interrupt-outcome"));
        await startedInterruptTurn.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await ctx.Kernel.ControlSessionAsync(leadId, new("stop", TurnId: (await ctx.Kernel.GetSessionItemsAsync(leadId, new(), new InteropPrincipal.Human(), default)).ActiveTurn!.Id), new InteropPrincipal.Human(), default);
        var interruptedTurn = await interruptTurnTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("interrupted", interruptedTurn.Status);

        var afterInterruptPage = await ctx.Kernel.GetSessionItemsAsync(leadId, new(), new InteropPrincipal.Human(), default);
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
