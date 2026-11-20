using Perga.AgentKit;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionLivenessTests
{
    [Fact]
    public async Task StopCancelsQueuedInputWhileDispatchAdmissionIsDraining()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        try
        {
            var accepted = await ctx.Kernel.AcceptMessageAsync(id, new("queued", "queued"), default);
            await ctx.Kernel.InterruptSessionAsync(id, turnId: accepted.Receipt!.TurnId);
            await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
            var page = await ctx.Kernel.GetSessionItemsAsync(id);
            Assert.Equal("idle", page.State);
            Assert.Equal("interrupted", page.LastTurn?.Status);
            Assert.Empty(ctx.AgentRuntime.OpenedSessions);
        }
        finally { ctx.Kernel.DispatchAdmission.SetDraining(false); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActorShutdownJoinsOwnedCleanupWithoutConcurrentDisposal(bool lateLaunch)
    {
        await using var ctx = new KernelTestContext(new KernelOptions { SessionStartupTimeout = TimeSpan.FromMilliseconds(100) });
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void HoldDisposal(ScriptableFakeAgentSession agent)
        {
            agent.ShutdownConfirmed = false;
            agent.DisposeHandler = async () =>
            {
                disposing.TrySetResult();
                await release.Task;
                agent.ShutdownConfirmed = true;
            };
        }
        if (lateLaunch)
        {
            ctx.AgentRuntime.BeforeOpenAsync = () => launch.Task;
            ctx.AgentRuntime.OnOpenedSession = HoldDisposal;
            await ctx.Kernel.AcceptMessageAsync(id, new("slow", "slow"), default);
            await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
            HoldDisposal(ctx.AgentRuntime.OpenedSessions.Single());
            await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.UpdateSessionModelAsync(id, new("B")));
        }
        var shutdown = ctx.Kernel.Actors.GetOrActivate(snap.Track.Id).DisposeAsync().AsTask();
        try
        {
            Assert.False(shutdown.IsCompleted);
            launch.TrySetResult();
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(1, ctx.AgentRuntime.OpenedSessions.Single().DisposeCalls);
        }
        finally { launch.TrySetResult(); release.TrySetResult(); }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(ctx.AgentRuntime.OpenedSessions.Single().ShutdownConfirmed);
        Assert.Equal(1, ctx.AgentRuntime.OpenedSessions.Single().DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleEvictionRetainsUnconfirmedOwnership(bool lateLaunch)
    {
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            SessionStartupTimeout = TimeSpan.FromMilliseconds(100),
            ActorIdleTimeout = TimeSpan.FromMinutes(1),
            ActorEvictionSweepInterval = TimeSpan.Zero
        });
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var actor = ctx.Kernel.Actors.GetOrActivate(snap.Track.Id);
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (lateLaunch)
        {
            ctx.AgentRuntime.BeforeOpenAsync = () => launch.Task;
            ctx.AgentRuntime.OnOpenedSession = agent => { agent.ShutdownConfirmed = false; agent.FailDisposal = true; };
            await ctx.Kernel.AcceptMessageAsync(id, new("slow", "slow"), default);
            await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
            var opened = ctx.AgentRuntime.OpenedSessions.Single();
            opened.ShutdownConfirmed = false;
            await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.UpdateSessionModelAsync(id, new("B")));
        }
        try
        {
            ctx.Time.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleActorsAsync());
            Assert.Same(actor, ctx.Kernel.Actors.GetOrActivate(snap.Track.Id));
            if (lateLaunch)
            {
                launch.TrySetResult();
                await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.RecoverSessionAsync(id, new("reset_context"), default));
                ctx.Time.Advance(TimeSpan.FromMinutes(2));
                Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleActorsAsync());
                Assert.Same(actor, ctx.Kernel.Actors.GetOrActivate(snap.Track.Id));
            }
            var agent = ctx.AgentRuntime.OpenedSessions.Single();
            agent.FailDisposal = false;
            agent.ShutdownConfirmed = true;
            await ctx.Kernel.RecoverSessionAsync(id, new("reset_context"), default);
            await Task.Run(async () => { while (actor.HasActiveWork) await Task.Delay(10); }).WaitAsync(TimeSpan.FromSeconds(5));
            ctx.Time.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleActorsAsync());
            Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(snap.Track.Id));
        }
        finally { launch.TrySetResult(); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task FailedLaunchOrReconfigurationRetainsUnconfirmedShutdown(bool mismatch, bool throws, bool configureThrows)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        if (!mismatch)
        {
            ctx.Kernel.DispatchAdmission.SetDraining(false);
            var old = ctx.AgentRuntime.OpenedSessions.Last();
            if (configureThrows) old.ConfigureHandler = _ => throw new IOException("configure failed");
            old.ShutdownConfirmed = false;
            old.FailDisposal = throws;
            await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.UpdateSessionModelAsync(id, new("B", "high")));
        }
        else
        {
            await ctx.Kernel.UpdateSessionModelAsync(id, new("B", "high"));
            ctx.AgentRuntime.OnOpenedSession = agent => {
                agent.NativeThreadId = "wrong";
                agent.ShutdownConfirmed = false;
                agent.FailDisposal = throws;
            };
            ctx.Kernel.DispatchAdmission.SetDraining(false);
            await ctx.Kernel.AcceptMessageAsync(id, new("reopen", "reopen"), default);
            await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
        }
        ctx.Kernel.DispatchAdmission.SetDraining(false);
        var retained = ctx.AgentRuntime.OpenedSessions.Last();
        var epoch = (await ctx.Kernel.GetSessionItemsAsync(id)).Session.Epoch;
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.RecoverSessionAsync(id, new("reset_context"), default));
        Assert.Equal(epoch, (await ctx.Kernel.GetSessionItemsAsync(id)).Session.Epoch);
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.AcceptMessageAsync(id, new("blocked", "blocked"), default));
        retained.ShutdownConfirmed = true;
        retained.FailDisposal = false;
        await ctx.Kernel.RecoverSessionAsync(id, new("reset_context"), default);
        Assert.Equal(epoch + 1, (await ctx.Kernel.GetSessionItemsAsync(id)).Session.Epoch);
    }

    [Fact]
    public async Task ReconfigurationDisposalTimeoutIsJoinedByResetBeforeEpochAdvances()
    {
        await using var ctx = new KernelTestContext(new KernelOptions { SessionStartupTimeout = TimeSpan.FromMilliseconds(100) });
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var agent = ctx.AgentRuntime.OpenedSessions.Single();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.DisposeHandler = () => release.Task;
        agent.ShutdownConfirmed = false;
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.UpdateSessionModelAsync(id, new("B", "high")));
        var reset = ctx.Kernel.RecoverSessionAsync(id, new("reset_context"), default);
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.AcceptMessageAsync(id, new("blocked", "blocked"), default));
        Assert.False(reset.IsCompleted);
        Assert.Equal(1, agent.DisposeCalls);
        Assert.Equal(1, (await ctx.Kernel.GetSessionItemsAsync(id)).Session.Epoch);
        release.SetResult();
        await Assert.ThrowsAnyAsync<Exception>(() => reset);
        agent.ShutdownConfirmed = true;
        await ctx.Kernel.RecoverSessionAsync(id, new("reset_context"), default);
        Assert.Equal(2, (await ctx.Kernel.GetSessionItemsAsync(id)).Session.Epoch);
    }

    [Fact]
    public async Task TimedOutLateLaunchRetainsOwnershipUntilShutdownConfirmed()
    {
        await using var ctx = new KernelTestContext(new KernelOptions { SessionStartupTimeout = TimeSpan.FromMilliseconds(100) });
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.BeforeOpenAsync = () => launch.Task;
        ctx.AgentRuntime.OnOpenedSession = agent => { agent.ShutdownConfirmed = false; agent.FailDisposal = true; };
        await ctx.Kernel.AcceptMessageAsync(id, new("slow", "slow"), default);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
        launch.SetResult();
        while (ctx.AgentRuntime.OpenedSessions.IsEmpty) await Task.Delay(10);
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.RecoverSessionAsync(id, new("reset_context"), default));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Kernel.AcceptMessageAsync(id, new("blocked", "blocked"), default));
        var agent = ctx.AgentRuntime.OpenedSessions.Single();
        agent.FailDisposal = false;
        agent.ShutdownConfirmed = true;
        await ctx.Kernel.RecoverSessionAsync(id, new("reset_context"), default);
        Assert.Equal(2, (await ctx.Kernel.GetSessionItemsAsync(id)).Session.Epoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupIdentityLossFencesSessionEvenWithoutQueue(bool mismatch)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        await ctx.Kernel.UpdateSessionModelAsync(id, new("next-model", "high"));
        // Force an adapter reopen with the persisted native identity.
        ctx.AgentRuntime.OnBeforeOpenSession = _ =>
        {
            if (!mismatch) throw new ProviderSessionNotFoundException("lost original session");
        };
        ctx.AgentRuntime.OnOpenedSession = session => session.NativeThreadId = "different-thread";
        ctx.Kernel.DispatchAdmission.SetDraining(false);
        await ctx.Kernel.AcceptMessageAsync(id, new("reopen", "reopen"), default);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
        var page = await ctx.Kernel.GetSessionItemsAsync(id);
        Assert.Equal("unknown", page.State);
        Assert.Equal("session_lost", page.UnknownDetail?.Reason);
        Assert.False(page.UnknownDetail?.CanReconnect);
        Assert.True(page.UnknownDetail?.CanResetContext);
        Assert.True(page.Session.ReconnectInvalidated);
        Assert.Equal("unknown", page.LastTurn?.Status);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5000000000)]
    public void InvalidTimeoutConfigurationCannotDisableLiveness(long milliseconds)
    {
        var options = new KernelOptions
        {
            SessionStartupTimeout = TimeSpan.FromMilliseconds(milliseconds),
            SessionCompactionTimeout = TimeSpan.FromMilliseconds(milliseconds)
        };
        Assert.Equal(TimeSpan.FromSeconds(30), options.SessionStartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.SessionCompactionTimeout);
    }
}
