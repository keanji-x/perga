using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionRuntimeActivityTests
{
    [Fact]
    public async Task RuntimeTasksMergeDuringAndAfterTurnsWithoutHoldingTurnAdmission()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        ctx.AgentRuntime.OnOpenedSession = session => session.PendingRuntimeActivity =
            [new UnifiedAgentEvent.TaskStarted(new("background", "shell", Background: true))];
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            session.EmitRuntimeActivity(new UnifiedAgentEvent.TaskUpdated(new("background", Description: "progress")));
            session.EmitRuntimeActivity(new UnifiedAgentEvent.AgentStatusChanged(new("running", Phase: "thinking", SinceSeq: 3)));
            await Task.Yield();
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "runtime-first"));
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.AgentStatusChanged(new("idle", IdleActivity: new(10, 20))));
        var page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("idle", page.Session.Status);
        Assert.Equal(20, page.RuntimeStatus?.IdleActivity?.LastEventAt);
        var task = Assert.Single(page.RuntimeTasks!);
        Assert.Equal("progress", task.Description);
        Assert.Equal("shell", task.TaskType);
        Assert.True(task.Background);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskEnded(new("background", Status: "completed", Summary: "done", OutputFile: "output")));
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskUpdated(new("background", Status: "running")));
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Null(Assert.Single(page.RuntimeTasks!).Summary);
        Assert.Null(Assert.Single(page.RuntimeTasks!).OutputFile);
        for (var index = 0; index < 70; index++)
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskEnded(new($"ended-{index}", Status: "completed")));
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Equal(65, page.RuntimeTasks!.Count);
        await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: true), new InteropPrincipal.Human(), default);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskEnded(new("background", Status: "completed", Summary: "finished while archived")));
        await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: false), new InteropPrincipal.Human(), default);
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.True(provider.Disposed);
        Assert.Null(page.RuntimeTasks);
        await ctx.SendAndWaitForTurnAsync(sessionId, new("second", "runtime-second"));
        provider = ctx.AgentRuntime.OpenedSessions.Last();
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.AgentStatusChanged(new("idle", Released: true)));
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Empty(page.RuntimeTasks!);
        await ctx.Kernel.ControlSessionAsync(sessionId, new("reset_context"), new InteropPrincipal.Human(), default);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskStarted(new("stale", "shell")));
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Null(page.RuntimeStatus);
        Assert.Null(page.RuntimeTasks);
    }
}
