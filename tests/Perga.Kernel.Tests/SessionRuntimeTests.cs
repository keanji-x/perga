using Perga.AgentKit;
using Perga.Kernel.Actors;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionRuntimeTests
{
    private static readonly HarnessLaunchSpec Spec = new("codex", null, "/tmp", null, null,
        new Dictionary<string, McpServerConfig>());
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static Task<OpenSessionHandle> Open(SessionRuntime runtime, ScriptableFakeAgentRuntime provider, CancellationToken ct) =>
        runtime.EnsureOpenAsync(provider, SessionConfiguration.FromLaunch(Spec), Spec.Provider,
            () => Task.FromResult(Spec), _ => { }, Timeout, ct);

    [Fact]
    public async Task CancelLaunchRunsCallbacksSynchronouslyOutsideSnapshotLock()
    {
        var runtime = new SessionRuntime();
        var launch = runtime.BeginLaunch(default, "turn");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var registration = launch.Token.Register(() =>
        {
            // A different thread must be able to take the snapshot lock during the callback.
            Assert.True(Task.Run(() => runtime.HasActiveWork).Wait(Timeout));
            entered.TrySetResult();
            release.Wait(Timeout);
        });
        var cancel = Task.Run(() => runtime.CancelLaunch("turn"));
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.False(cancel.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True(await cancel.WaitAsync(Timeout));
        runtime.EndLaunch(launch);
    }

    [Fact]
    public async Task ProviderCasingDoesNotReopenExistingSession()
    {
        var runtime = new SessionRuntime();
        var provider = new ScriptableFakeAgentRuntime();
        var first = await Open(runtime, provider, default);
        var second = await runtime.EnsureOpenAsync(provider, SessionConfiguration.FromLaunch(Spec), "CODEX",
            () => Task.FromResult(Spec), _ => { }, Timeout, default);
        Assert.Same(first.Agent, second.Agent);
        Assert.Single(provider.OpenedSessions);
        await runtime.CloseAsync(Timeout, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelLaunchClosesInPlaceAndRetainsOnlyUnconfirmedHandle(bool confirmed)
    {
        var runtime = new SessionRuntime();
        var provider = new ScriptableFakeAgentRuntime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.BeforeOpenAsync = () => { entered.TrySetResult(); return new TaskCompletionSource().Task; };
        provider.OnOpenedSession = agent => agent.ShutdownConfirmed = confirmed;
        var launch = runtime.BeginLaunch(default, "turn");
        var opening = Open(runtime, provider, launch.Token);
        await entered.Task.WaitAsync(Timeout);
        Assert.False(runtime.CancelLaunch("other"));
        Assert.True(runtime.CancelLaunch("turn"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Timeout));
        runtime.EndLaunch(launch);
        var agent = Assert.Single(provider.OpenedSessions);
        Assert.Equal(1, agent.DisposeCalls);
        Assert.Equal(!confirmed, runtime.IsShutdownUnconfirmed);
        Assert.Equal(!confirmed, runtime.HasActiveWork);
        if (confirmed) Assert.Null(runtime.Snapshot);
        else
        {
            Assert.Same(agent, runtime.Snapshot!.Agent);
            var error = await Assert.ThrowsAsync<KernelException>(() => Open(runtime, provider, default));
            Assert.Equal(KernelErrorCode.SessionRecoveryRequired, error.Code);
            agent.ShutdownConfirmed = true;
            await runtime.CloseAsync(Timeout, default);
            Assert.Equal(2, agent.DisposeCalls);
            Assert.Null(runtime.Snapshot);
            Assert.False(runtime.HasActiveWork);
        }
    }

    [Fact]
    public async Task SnapshotsAndCancellationDoNotWaitForGateAndOperationsSerialize()
    {
        var runtime = new SessionRuntime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = runtime.RunAsync(async _ => { entered.SetResult(); await release.Task; return 1; }, default);
        await entered.Task;
        var secondEntered = false;
        var second = runtime.RunAsync(_ => { secondEntered = true; return Task.FromResult(2); }, default);
        try
        {
            Assert.True(runtime.HasActiveWork);
            Assert.Null(runtime.Snapshot);
            Assert.False(runtime.CancelLaunch());
            Assert.False(secondEntered);
        }
        finally { release.SetResult(); }
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.False(runtime.HasActiveWork);
    }

    [Fact]
    public async Task DisposeAllJoinsStartupCleanupAndDisposesExactlyOnce()
    {
        var registry = new SessionRuntimeRegistry();
        var runtime = registry.Get("session");
        var provider = new ScriptableFakeAgentRuntime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.BeforeOpenAsync = () => { entered.SetResult(); return new TaskCompletionSource().Task; };
        provider.OnOpenedSession = agent =>
        {
            agent.ShutdownConfirmed = false;
            agent.DisposeHandler = async () => { disposing.TrySetResult(); await release.Task; agent.ShutdownConfirmed = true; };
        };
        var launch = runtime.BeginLaunch(default, "turn");
        var opening = Open(runtime, provider, launch.Token);
        await entered.Task;
        var shutdown = registry.DisposeAllAsync(Timeout);
        await disposing.Task.WaitAsync(Timeout);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(1, Assert.Single(provider.OpenedSessions).DisposeCalls);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        await shutdown.WaitAsync(Timeout);
        Assert.Null(runtime.Snapshot);
        Assert.Equal(1, Assert.Single(provider.OpenedSessions).DisposeCalls);
    }
    [Fact]
    public async Task CloseRechecksCallerPredicateAfterWaitingForGate()
    {
        var runtime = new SessionRuntime();
        var provider = new ScriptableFakeAgentRuntime();
        await Open(runtime, provider, default);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = runtime.RunAsync(async _ =>
        {
            await release.Task;
            return true;
        }, default);
        var shouldClose = true;
        var close = runtime.CloseAsync(Timeout, default, shouldClose: () => shouldClose);
        shouldClose = false;
        release.SetResult();
        await operation;
        await close;
        Assert.Equal(0, Assert.Single(provider.OpenedSessions).DisposeCalls);
        Assert.NotNull(runtime.Snapshot);
        await runtime.CloseAsync(Timeout, default, shouldClose: () => true);
        Assert.Null(runtime.Snapshot);
    }

    [Fact]
    public async Task ConditionalCloseKeepsProviderWhileLaunchIsPending()
    {
        var runtime = new SessionRuntime();
        var provider = new ScriptableFakeAgentRuntime();
        await Open(runtime, provider, default);
        var launch = runtime.BeginLaunch(default);
        try
        {
            await runtime.CloseAsync(Timeout, default, shouldClose: () => true);
            Assert.Equal(0, Assert.Single(provider.OpenedSessions).DisposeCalls);
            Assert.NotNull(runtime.Snapshot);
        }
        finally
        {
            runtime.EndLaunch(launch);
            await runtime.CloseAsync(Timeout, default);
        }
    }

    [Fact]
    public async Task DisposeIsBoundedWhenPriorCloseStillHangs()
    {
        var runtime = new SessionRuntime();
        var provider = new ScriptableFakeAgentRuntime();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.OnOpenedSession = agent =>
        {
            agent.ShutdownConfirmed = false;
            agent.DisposeHandler = async () =>
            {
                await release.Task;
                agent.ShutdownConfirmed = true;
                shutdownFinished.TrySetResult();
            };
        };
        await Open(runtime, provider, default);
        var timeout = TimeSpan.FromMilliseconds(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CloseAsync(timeout, default));
        var disposing = runtime.DisposeAsync(timeout);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disposing.WaitAsync(Timeout));
            Assert.True(disposing.IsCompleted);
            Assert.True(runtime.IsShutdownUnconfirmed);
            Assert.Equal(1, Assert.Single(provider.OpenedSessions).DisposeCalls);
        }
        finally
        {
            release.TrySetResult();
            try { await disposing.WaitAsync(Timeout); }
            catch (OperationCanceledException) { }
            await shutdownFinished.Task.WaitAsync(Timeout);
            await runtime.DisposeAsync(timeout).WaitAsync(Timeout);
            Assert.Null(runtime.Snapshot);
            Assert.Equal(1, Assert.Single(provider.OpenedSessions).DisposeCalls);
        }
    }

}
