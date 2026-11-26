using System.Text.Json;
using Perga.Kernel.Events;
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
        Assert.Equal(9, ctx.Store.MemoryTrackLookupCalls);
        var planner = Assert.Single(planners.Distinct());
        Assert.Equal(MemoryTrackBinding.ScopeKeyFor(area.Id), planner.ScopeKey);
        Assert.Equal(area.Id, planner.AreaId);
        Assert.Equal(planner.TrackId, await ctx.Store.GetMemoryTrackIdAsync(planner.ScopeKey, default));
        Assert.Equal(planner, await ctx.Kernel.GetOrEnsureMemoryTrackAsync(area.Id));
        Assert.Equal(1, createCalls);
        Assert.Equal(1, ctx.PolicyVm.CompileCallCount);
    }

    [Fact]
    public async Task CancelingFirstCallerLeavesSiblingAndCreationActive()
    {
        await using var ctx = CreateContext();
        using var callerCts = new CancellationTokenSource();
        var creating = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var createCalls = 0;
        ctx.Store.BeforeMemoryTrackCreateAsync = ct =>
        {
            Interlocked.Increment(ref createCalls);
            creating.TrySetResult(ct);
            return release.Task.WaitAsync(ct);
        };

        var first = ctx.Kernel.GetOrEnsureMemoryTrackAsync(null, callerCts.Token);
        var createToken = await creating.Task.WaitAsync(TestTimeout);
        var sibling = ctx.Kernel.GetOrEnsureMemoryTrackAsync(null);
        try
        {
            await callerCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TestTimeout));
            Assert.False(createToken.IsCancellationRequested);
        }
        finally
        {
            release.SetResult();
        }

        var planner = await sibling.WaitAsync(TestTimeout);
        Assert.Equal(planner.TrackId, await ctx.Store.GetMemoryTrackIdAsync(planner.ScopeKey, default));
        Assert.Equal(1, createCalls);
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

    [Fact]
    public async Task SiblingCanCancelWhileFlightIsCompilingSynchronously()
    {
        await using var ctx = CreateContext();
        using var callerCts = new CancellationTokenSource();
        var release = new ManualResetEventSlim();
        var compiling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.PolicyVm.CustomManifestFactory = source =>
        {
            compiling.SetResult();
            release.Wait();
            return new ConfigurableFakePolicyVm().Compile(source).Manifest;
        };

        // Capture the outer tasks so a blocked call cannot also block the test's timeout.
        var firstCall = Task.Factory.StartNew(() => ctx.Kernel.GetOrEnsureMemoryTrackAsync(null),
            CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        var first = firstCall.Unwrap();
        Task? siblingWorker = null;
        try
        {
            await compiling.Task.WaitAsync(TestTimeout);
            var siblingCall = Task.Factory.StartNew(() => ctx.Kernel.GetOrEnsureMemoryTrackAsync(null, callerCts.Token),
                CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            siblingWorker = siblingCall.Unwrap();
            var sibling = await siblingCall.WaitAsync(TestTimeout);
            await callerCts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sibling.WaitAsync(TestTimeout));
            Assert.False((await firstCall.WaitAsync(TestTimeout)).IsCompleted);
        }
        finally
        {
            release.Set();
            var workers = siblingWorker is null ? (Task)first : Task.WhenAll(first, siblingWorker);
            await workers.WaitAsync(TestTimeout).ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            if (workers.IsCompleted)
                release.Dispose();
        }

        var planner = await first.WaitAsync(TestTimeout);
        Assert.Equal(planner.TrackId, await ctx.Store.GetMemoryTrackIdAsync(planner.ScopeKey, default));
        Assert.Equal(1, ctx.PolicyVm.CompileCallCount);
    }

    [Fact]
    public async Task DisposalCancelsAndDrainsPendingCreation()
    {
        await using var ctx = CreateContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Project"));
        var areaPlanner = await ctx.Kernel.GetOrEnsureMemoryTrackAsync(area.Id).WaitAsync(TestTimeout);
        var creating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Store.BeforeMemoryTrackCreateAsync = async ct =>
        {
            using var registration = ct.Register(() => cancelRequested.TrySetResult());
            creating.SetResult();
            await finish.Task;
            ct.ThrowIfCancellationRequested();
        };

        var ensure = ctx.Kernel.GetOrEnsureMemoryTrackAsync(null);
        Task? disposal = null;
        try
        {
            await creating.Task.WaitAsync(TestTimeout);
            disposal = ctx.Kernel.DisposeAsync().AsTask();
            await cancelRequested.Task.WaitAsync(TestTimeout);
            await Task.Delay(50);
            Assert.False(disposal.IsCompleted);
            Assert.True(ctx.Kernel.Actors.IsTrackActiveInMemory(areaPlanner.TrackId));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => ctx.Kernel.GetOrEnsureMemoryTrackAsync(null).WaitAsync(TestTimeout));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => ctx.Kernel.GetOrEnsureMemoryTrackAsync(area.Id).WaitAsync(TestTimeout));
        }
        finally
        {
            finish.SetResult();
            if (disposal is not null)
                await disposal.WaitAsync(TestTimeout);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ensure.WaitAsync(TestTimeout));
        Assert.Null(await ctx.Store.GetMemoryTrackIdAsync(MemoryTrackBinding.ScopeKeyFor(null), default));
    }

    [Fact]
    public async Task DisposalCancelsPostCommitProjectionAndPreservesCreationEvents()
    {
        var projection = new BlockingMemoryProjection();
        var provision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = CreateContext(projection, provision.Task);
        using var events = ctx.EventHub.Subscribe();
        var ensure = ctx.Kernel.GetOrEnsureMemoryTrackAsync(null);
        Task? disposal = null;
        try
        {
            var projectionToken = await projection.Started.Task.WaitAsync(TestTimeout);
            var trackId = await ctx.Store.GetMemoryTrackIdAsync(MemoryTrackBinding.ScopeKeyFor(null), default);
            Assert.NotNull(trackId);

            disposal = ctx.Kernel.DisposeAsync().AsTask();
            await disposal.WaitAsync(TestTimeout);
            Assert.True(projectionToken.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ensure.WaitAsync(TestTimeout));
            Assert.Equal(trackId, await ctx.Store.GetMemoryTrackIdAsync(MemoryTrackBinding.ScopeKeyFor(null), default));
            Assert.True(events.Reader.TryRead(out var trackCreated));
            Assert.Equal(trackId, trackCreated.TrackId);
            Assert.Equal(KernelEventKinds.TrackUpdated, trackCreated.Kind);
            Assert.True(events.Reader.TryRead(out var sessionCreated));
            Assert.Equal(trackId, sessionCreated.TrackId);
            Assert.Equal(KernelEventKinds.SessionUpdated, sessionCreated.Kind);
        }
        finally
        {
            projection.Release.SetResult();
            provision.SetResult();
            if (disposal is not null)
                await disposal.WaitAsync(TestTimeout);
        }
    }

    private static KernelTestContext CreateContext(IMemoryContextProvider? memoryContext = null, Task? provisionGate = null)
    {
        var ctx = new KernelTestContext(memoryContext: memoryContext);
        ctx.DriverRegistry.Register(new SpyWorkspaceDriver(VcsWorkspaceDriver.Id) { ProvisionGate = provisionGate });
        return ctx;
    }

    private sealed class BlockingMemoryProjection : IMemoryContextProvider
    {
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<JsonElement?> GetMemoryTemplateDataAsync(string? areaId, CancellationToken ct)
        {
            Started.SetResult(ct);
            await Release.Task.WaitAsync(ct);
            return null;
        }

        public Task<MemoryTurnSnapshot?> GetContextSnapshotAsync(string? areaId, CancellationToken ct) => Task.FromResult<MemoryTurnSnapshot?>(null);
        public Task<string> AutoRecallAsync(string? areaId, string ownerContent, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<string> HydrateMentionsAsync(string? areaId, string ownerContent, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task DeleteAreaDirectoryAsync(string areaId, CancellationToken ct) => Task.CompletedTask;
        public Task CleanupOrphanAreaDirectoriesAsync(IReadOnlySet<string> validUserAreaIds, CancellationToken ct) => Task.CompletedTask;
    }
}
