using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.VcsKit;

namespace Perga.Kernel.Tests;

public sealed class MemoryTrackEnsureTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConcurrentEnsuresForOneScopeCreateAndCompileOnce()
    {
        await using var ctx = CreateContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Project"));
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createCalls = 0;
        ctx.Store.BeforeMemoryTrackCreateAsync = ct =>
        {
            Interlocked.Increment(ref createCalls);
            creating.TrySetResult();
            return release.Task.WaitAsync(ct);
        };

        var first = ctx.Kernel.GetOrEnsureMemoryTrackAsync(area.Id);
        await creating.Task.WaitAsync(TestTimeout);
        var siblings = Enumerable.Range(0, 7).Select(_ => ctx.Kernel.GetOrEnsureMemoryTrackAsync(area.Id)).ToArray();
        release.SetResult();

        var planners = await Task.WhenAll(siblings.Append(first)).WaitAsync(TestTimeout);
        var planner = Assert.Single(planners.Distinct());
        Assert.Equal(MemoryTrackBinding.ScopeKeyFor(area.Id), planner.ScopeKey);
        Assert.Equal(area.Id, planner.AreaId);
        Assert.Equal(planner.TrackId, await ctx.Store.GetMemoryTrackIdAsync(planner.ScopeKey, default));
        Assert.Equal(planner, await ctx.Kernel.GetOrEnsureMemoryTrackAsync(area.Id));
        Assert.Equal(1, createCalls);
        Assert.Equal(1, ctx.PolicyVm.CompileCallCount);
    }

    [Fact]
    public async Task CancelingWaitingSiblingLeavesCreationActive()
    {
        await using var ctx = CreateContext();
        using var siblingCts = new CancellationTokenSource();
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createCalls = 0;
        ctx.Store.BeforeMemoryTrackCreateAsync = ct =>
        {
            Interlocked.Increment(ref createCalls);
            creating.TrySetResult();
            return release.Task.WaitAsync(ct);
        };

        var first = ctx.Kernel.GetOrEnsureMemoryTrackAsync(null);
        await creating.Task.WaitAsync(TestTimeout);
        var sibling = ctx.Kernel.GetOrEnsureMemoryTrackAsync(null, siblingCts.Token);
        try
        {
            await siblingCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sibling.WaitAsync(TestTimeout));
            Assert.False(first.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        var planner = await first.WaitAsync(TestTimeout);
        Assert.Equal(planner.TrackId, await ctx.Store.GetMemoryTrackIdAsync(planner.ScopeKey, default));
        Assert.Equal(1, createCalls);
    }

    [Fact]
    public async Task CancelingFirstCallerAllowsWaitingSiblingToComplete()
    {
        await using var ctx = CreateContext();
        using var firstCts = new CancellationTokenSource();
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createCalls = 0;
        ctx.Store.BeforeMemoryTrackCreateAsync = ct =>
        {
            if (Interlocked.Increment(ref createCalls) == 1)
            {
                creating.TrySetResult();
                return release.Task.WaitAsync(ct);
            }

            return Task.CompletedTask;
        };

        var first = ctx.Kernel.GetOrEnsureMemoryTrackAsync(null, firstCts.Token);
        await creating.Task.WaitAsync(TestTimeout);
        var sibling = ctx.Kernel.GetOrEnsureMemoryTrackAsync(null);
        try
        {
            await firstCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TestTimeout));
        }
        finally
        {
            release.TrySetResult();
        }

        var planner = await sibling.WaitAsync(TestTimeout);
        Assert.Equal(planner.TrackId, await ctx.Store.GetMemoryTrackIdAsync(planner.ScopeKey, default));
        Assert.Equal(2, createCalls);
    }

    [Fact]
    public async Task FailedCreationCanBeRetried()
    {
        await using var ctx = CreateContext();
        var failure = new InvalidOperationException("Track creation failed.");
        var createCalls = 0;
        ctx.Store.BeforeMemoryTrackCreateAsync = _ =>
            Interlocked.Increment(ref createCalls) == 1 ? Task.FromException(failure) : Task.CompletedTask;

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.Kernel.GetOrEnsureMemoryTrackAsync(null).WaitAsync(TestTimeout)));
        var planner = await ctx.Kernel.GetOrEnsureMemoryTrackAsync(null).WaitAsync(TestTimeout);

        Assert.Equal(planner.TrackId, await ctx.Store.GetMemoryTrackIdAsync(planner.ScopeKey, default));
        Assert.Equal(2, createCalls);
    }

    private static KernelTestContext CreateContext()
    {
        var ctx = new KernelTestContext();
        ctx.DriverRegistry.Register(new SpyWorkspaceDriver(VcsWorkspaceDriver.Id));
        return ctx;
    }
}
