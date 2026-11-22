using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class ActorIdleEvictionTests
{
    [Fact]
    public async Task IdleSweepWaitsForMailboxFinalizationEvenAfterOperationResultIsPublished()
    {
        using var clock = new FinalizationGateTimeProvider();
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            ActorIdleTimeout = TimeSpan.Zero, ActorEvictionSweepInterval = TimeSpan.Zero
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
            Assert.True(actor.HasActiveWork);
            Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleActorsAsync(deadline.Token));

            quiescence = ctx.WaitForActorQuiescenceAsync(snapshot.Track.Id, deadline.Token);
            Assert.False(quiescence.IsCompleted);
        }
        finally { clock.Release.Set(); }

        await quiescence;
        Assert.False(actor.HasActiveWork);
        Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleActorsAsync(deadline.Token));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(snapshot.Track.Id));
    }

    private sealed class FinalizationGateTimeProvider : TimeProvider, IDisposable
    {
        private int _armedThreadId;
        private readonly DateTimeOffset _now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void ArmOnCurrentThread() => Interlocked.Exchange(ref _armedThreadId, Environment.CurrentManagedThreadId);

        public override DateTimeOffset GetUtcNow()
        {
            var threadId = Environment.CurrentManagedThreadId;
            if (Interlocked.CompareExchange(ref _armedThreadId, 0, threadId) == threadId)
            {
                Blocked.SetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Mailbox finalization gate was not released.");
            }
            return _now;
        }

        public void Dispose() => Release.Dispose();
    }
}
