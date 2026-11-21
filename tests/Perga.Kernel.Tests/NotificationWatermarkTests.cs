using Perga.Kernel.Ports;
using Perga.VcsKit;
using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class NotificationWatermarkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlayLookupConcurrentWithIdleEvictionUsesStoreOrNewActor(bool replace)
    {
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            ActorIdleTimeout = TimeSpan.FromMinutes(1), ActorEvictionSweepInterval = TimeSpan.Zero
        });
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var stored = Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks) with
        {
            NotifiableSessions = [new(snap.Track.LeadSessionId!, 123)]
        };
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var lookups = 0;
        var merge = Task.Run(() => PergaKernelService.MergeActiveOverlayAsync(stored, () =>
        {
            ctx.Kernel.Actors.TryGetActive(snap.Track.Id, out var actor);
            if (Interlocked.Increment(ref lookups) == 1)
            {
                selected.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return actor;
        }, default));
        try
        {
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ctx.Time.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleActorsAsync());
            if (replace) await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        }
        finally { release.Set(); }
        var overlay = await merge;
        Assert.Equal(2, lookups);
        if (replace) Assert.Empty(overlay.NotifiableSessions!); // The newly hydrated actor wins over the stale store input.
        else Assert.Equal(stored, overlay);
        Assert.Equal(snap.Track.Id, Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks).TrackId);
    }

    [Theory]
    [InlineData(WorkspacePreparationPhase.Ready, false, false)]
    [InlineData(WorkspacePreparationPhase.Pending, true, false)]
    [InlineData(WorkspacePreparationPhase.Provisioning, true, false)]
    [InlineData(WorkspacePreparationPhase.Mounting, true, false)]
    [InlineData(WorkspacePreparationPhase.Initializing, true, false)]
    [InlineData(WorkspacePreparationPhase.Provisioned, true, false)]
    [InlineData(WorkspacePreparationPhase.Failed, false, true)]
    [InlineData(WorkspacePreparationPhase.Unknown, false, true)]
    public async Task ActorWorkspaceAndClosedOverlayPreserveWatermarks(WorkspacePreparationPhase phase, bool working, bool failed)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.Id;
        // Keep the seeded pending preparation from finishing before the overlay read.
        if (phase == WorkspacePreparationPhase.Pending)
            ctx.DirectDriver.ProvisionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var session = Assert.Single(await ctx.Kernel.ListSessionsAsync(id, new(), new InteropPrincipal.Human(), default)).Session with { LastNotifiableTurnAtMs = 123 };
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(id);
        await ctx.Store.UpsertSessionAsync(session, null, default);
        await ctx.Store.SaveWorkspacePreparationAsync(id,
            new(new WorkspaceProvisionRequest(id, "/tmp/source", "/tmp/target", []), null, phase), null, null, default);
        // Activate without starting preparation: overlay reads hydrate the saved phase themselves.
        ctx.Kernel.Actors.GetOrActivate(id);
        var overlay = Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks);
        Assert.Equal(working, overlay.IsWorking);
        Assert.Equal(failed, overlay.WorkspaceFailed);
        // An active actor is the single source of truth over contradictory stored flags.
        var active = await PergaKernelService.MergeActiveOverlayAsync(
            overlay with { IsWorking = !working, WorkspaceFailed = !failed, HasBlockedSession = true, OpenAskCount = 3 },
            () => ctx.Kernel.Actors.GetOrActivate(id), default);
        Assert.Equal(working, active.IsWorking);
        Assert.Equal(failed, active.WorkspaceFailed);
        Assert.False(active.HasBlockedSession);
        Assert.Equal(0, active.OpenAskCount);
        Assert.Equal(123, Assert.Single(overlay.NotifiableSessions!).LastNotifiableTurnAtMs);
        await ctx.Kernel.PatchTrackAsync(id, new(Closed: true));
        var closed = Assert.Single((await ctx.Kernel.GetOverlaysAsync(includeClosed: true)).Tracks);
        Assert.False(closed.IsWorking);
        Assert.False(closed.WorkspaceFailed);
        Assert.False(closed.HasBlockedSession);
        Assert.Equal(0, closed.OpenAskCount);
        Assert.Equal(0, closed.PendingApprovalCount);
        Assert.Equal(123, Assert.Single(closed.NotifiableSessions!).LastNotifiableTurnAtMs);
        Assert.Empty((await ctx.Kernel.GetOverlaysAsync()).Tracks);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("interrupted")]
    public async Task TerminalTurnsImmediatelyUpdateActorWatermarks(string status)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession agent, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new UnifiedAgentEvent.ThreadBound(agent.NativeThreadId!);
            if (status == "failed") yield return new UnifiedAgentEvent.TurnError("test", "test failure", false, IsConclusive: true);
            else if (status == "interrupted") yield return new UnifiedAgentEvent.TurnInterrupted();
            else yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        ctx.AgentRuntime.TurnScript = Stream;
        Assert.Null(Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(), new InteropPrincipal.Human(), default)).Session.LastNotifiableTurnAtMs);
        var turn = await ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("hello", "notification-test"));
        var session = Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(), new InteropPrincipal.Human(), default)).Session;
        Assert.Equal(status, turn.Status);
        if (status == "interrupted")
        {
            Assert.Null(session.LastNotifiableTurnAtMs);
            Assert.Empty(Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks).NotifiableSessions!);
            return;
        }
        Assert.NotNull(session.LastNotifiableTurnAtMs);
        Assert.Equal(turn.CompletedAtMs, session.LastNotifiableTurnAtMs);
        var snapshot = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        Assert.Equal(session.LastNotifiableTurnAtMs, Assert.Single(snapshot.Sessions).LastNotifiableTurnAtMs);
        var overlay = Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks);
        Assert.Equal(session.LastNotifiableTurnAtMs, Assert.Single(overlay.NotifiableSessions!).LastNotifiableTurnAtMs);
        await ctx.Kernel.UpdateSessionAsync(id, new UpdateSessionRequest(Title: "renamed"), new InteropPrincipal.Human(), default);
        Assert.Equal(session.LastNotifiableTurnAtMs, Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(), new InteropPrincipal.Human(), default)).Session.LastNotifiableTurnAtMs);
    }
}
