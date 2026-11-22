using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class ActorIdleEvictionTests
{
    [Fact]
    public async Task OverlayPollingDoesNotExtendActorLifetime()
    {
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            ActorIdleTimeout = TimeSpan.FromMinutes(1), ActorEvictionSweepInterval = TimeSpan.Zero
        });
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var actor = ctx.Kernel.Actors.GetOrActivate(trackId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ctx.WaitForActorQuiescenceAsync(trackId, deadline.Token);
        var lastActivity = actor.LastActivityAtMs;
        var before = await ctx.Kernel.GetOverlaysAsync(ct: deadline.Token);

        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        var after = await ctx.Kernel.GetOverlaysAsync(ct: deadline.Token);
        await ctx.WaitForActorQuiescenceAsync(trackId, deadline.Token);

        Assert.Equal(lastActivity, actor.LastActivityAtMs);
        Assert.Equal(Assert.Single(before.Tracks).UpdatedAtMs, Assert.Single(after.Tracks).UpdatedAtMs);
        Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleActorsAsync(deadline.Token));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));
    }

    [Fact]
    public async Task IdleSweepWaitsForMailboxFinalizationEvenAfterOperationResultIsPublished()
    {
        using var clock = new FinalizationGateTimeProvider();
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            ActorIdleTimeout = TimeSpan.FromMinutes(1), ActorEvictionSweepInterval = TimeSpan.Zero
        }, timeProvider: clock);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var actor = ctx.Kernel.Actors.GetOrActivate(snapshot.Track.Id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ctx.WaitForActorQuiescenceAsync(snapshot.Track.Id, deadline.Token);
        Task quiescence;
        try
        {
            await actor.EnqueueAsync(_ =>
            {
                // The next clock read is TouchActivity in EnqueueAsync's finally.
                // The completed callback and its finally execute in the same synchronous segment.
                clock.ArmOnCurrentThread();
                return Task.FromResult(true);
            }, deadline.Token);
            await clock.Blocked.Task.WaitAsync(deadline.Token);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(actor.HasActiveWork);
            Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleActorsAsync(deadline.Token));

            quiescence = ctx.WaitForActorQuiescenceAsync(snapshot.Track.Id, deadline.Token);
            Assert.False(quiescence.IsCompleted);
        }
        finally { clock.Release.Set(); }

        await quiescence;
        Assert.False(actor.HasActiveWork);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleActorsAsync(deadline.Token));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(snapshot.Track.Id));
    }

    private sealed class FinalizationGateTimeProvider : TimeProvider, IDisposable
    {
        private int _armedThreadId;
        private readonly FakeManualTimeProvider _time = new();
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void ArmOnCurrentThread() => Interlocked.Exchange(ref _armedThreadId, Environment.CurrentManagedThreadId);
        public void Advance(TimeSpan delta) => _time.Advance(delta);

        public override DateTimeOffset GetUtcNow()
        {
            var threadId = Environment.CurrentManagedThreadId;
            if (Interlocked.CompareExchange(ref _armedThreadId, 0, threadId) == threadId)
            {
                Blocked.SetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Mailbox finalization gate was not released.");
            }
            return _time.GetUtcNow();
        }

        public void Dispose() => Release.Dispose();
    }
}
