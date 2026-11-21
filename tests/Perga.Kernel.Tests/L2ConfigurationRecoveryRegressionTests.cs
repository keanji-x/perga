using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class L2ConfigurationRecoveryRegressionTests
{
    [Theory]
    [InlineData("rejected")]
    [InlineData("unsupported")]
    [InlineData("exception")]
    public async Task CompactedIdentitySurvivesEveryConfigurationEviction(string failure)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
        var original = ctx.AgentRuntime.OpenedSessions.Last();
        var nativeId = original.NativeThreadId;
        original.ConfigureHandler = _ => failure switch
        {
            "rejected" => new SessionConfigurationOutcome.Rejected(ConfigurationFailureCode.InvalidAcknowledgement, "partial", new("B", null, false)),
            "unsupported" => new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Effort, "unsupported"),
            _ => throw new IOException("transport")
        };
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("B"), ReasoningEffort: new("high")), new InteropPrincipal.Human(), default);
        Assert.Equal("failed", (await ctx.SendAndWaitForTurnAsync(id, new("rebind", "rebind"))).Status);
        Assert.True(original.Disposed);
        Assert.Equal(nativeId, ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single().NativeThreadId);
        Assert.Equal(nativeId, (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!.Sessions.Single().NativeThreadId);
        await ctx.SendAndWaitForTurnAsync(id, new("retry", "retry"));
        Assert.Equal(nativeId, ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.ResumeNativeThreadId);
    }

    [Fact]
    public async Task FailedDispatchReopenSettlementRetainsLatestDurableIdentity()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
        var nativeId = ctx.AgentRuntime.OpenedSessions.Last().NativeThreadId;
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("B"), ReasoningEffort: new("high")), new InteropPrincipal.Human(), default);
        ctx.AgentRuntime.OnBeforeOpenSession = spec =>
        {
            Assert.Equal(nativeId, spec.ResumeNativeThreadId);
            throw new IOException("unavailable");
        };
        ctx.Kernel.DispatchAdmission.SetDraining(false);
        await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "fails", RequestKey: "fails"), new InteropPrincipal.Human(), default);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("failed", ctx.Store.GetTurnsForSession(id).Last().Status);
        Assert.Equal(nativeId, (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!.Sessions.Single().NativeThreadId);
        Assert.Equal(nativeId, ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single().NativeThreadId);
        ctx.AgentRuntime.OnBeforeOpenSession = null;
        await ctx.SendAndWaitForTurnAsync(id, new("retry", "retry"));
        Assert.Equal(nativeId, ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.ResumeNativeThreadId);
    }

    [Fact]
    public async Task FreshReopenRejectsContradictoryResumedIdentity()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var nativeId = ctx.AgentRuntime.OpenedSessions.Last().NativeThreadId;
        ctx.AgentRuntime.OnOpenedSession = session => session.NativeThreadId = "contradictory";
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("B"), ReasoningEffort: new("high")), new InteropPrincipal.Human(), default);
        Assert.Equal("unknown", (await ctx.SendAndWaitForTurnAsync(id, new("rebind", "rebind"))).Status);
        var replacement = ctx.AgentRuntime.OpenedSessions.Last();
        Assert.Equal(nativeId, replacement.LaunchSpec.ResumeNativeThreadId);
        Assert.True(replacement.Disposed);
        Assert.Empty(replacement.RecordedInputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumePermissionRaceValidatesLaunchTupleBeforeCompletingRecovery(bool unconfirmed)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var end = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        ctx.AgentRuntime.OnOpenedSession = session =>
        {
            session.SupportsSessionResume = true;
            session.SupportsScopedClose = true;
            session.OnCancelTurn = () => end.TrySetResult();
        };
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (input.Prompt == "old")
            {
                yield return new UnifiedAgentEvent.TextDelta("accepted");
                started.TrySetResult();
                await end.Task.WaitAsync(ct);
                yield return new UnifiedAgentEvent.TurnError("stream_lost", "Disconnected", false);
            }
            else yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "old", RequestKey: "old"), new InteropPrincipal.Human(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        end.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id);
        var turnId = (await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).LastTurn!.Id;
        ctx.AgentRuntime.OnBeforeOpenSession = _ =>
        {
            launched.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        ctx.AgentRuntime.OnOpenedSession = session =>
        {
            session.SupportsSessionResume = true;
            session.SupportsScopedClose = true;
            if (unconfirmed) session.ConfigurationState = null;
        };
        var resume = ctx.Kernel.ControlSessionAsync(id, new("reconnect"), new InteropPrincipal.Human(), default);
        try
        {
            await launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await ctx.Kernel.UpdateSessionAsync(id, new(PermissionMode: new("full")), new InteropPrincipal.Human(), default);
        }
        finally { release.Set(); }
        if (unconfirmed)
        {
            await Assert.ThrowsAsync<KernelException>(() => resume);
            Assert.Equal("unknown", (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!.Sessions.Single().Status);
            Assert.Equal("unknown", (await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).State);
        }
        else
        {
            await resume;
            Assert.Equal("idle", (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!.Sessions.Single().Status);
            Assert.Equal("full", ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single().PermissionMode);
            ctx.AgentRuntime.OnBeforeOpenSession = null;
            await ctx.SendAndWaitForTurnAsync(id, new("next", "next"));
            Assert.Equal("full", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.PermissionMode);
        }
    }

}
