using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class TrackActorRegistryTests
{
    [Fact]
    public async Task ConcurrentActivationKeepsOneHandshakeSubscriberUntilRegistryDisposal()
    {
        var bridge = new ObservableHandshakeBridge();
        using var clock = new ActivationClock();
        await using var context = new KernelTestContext(timeProvider: clock, mcpSessionBridge: bridge);
        var registry = context.Kernel.Actors;
        clock.SynchronizeNextTwoReads();

        var activations = Enumerable.Range(0, 2).Select(_ => Task.Factory.StartNew(
            () => registry.GetOrActivate("concurrent-track"), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default));
        var actors = await Task.WhenAll(activations);

        Assert.Same(actors[0], actors[1]);
        Assert.Equal(1, registry.ActiveActorCount);
        Assert.Equal(1, bridge.DeliverHandshake(new("concurrent-track", "session", "binding")));
        Assert.Equal(1, bridge.DeliverHandshake(new("inactive-track", "session", "binding")));
        Assert.False(registry.IsTrackActiveInMemory("inactive-track"));

        await registry.DisposeAsync();

        Assert.Equal(0, registry.ActiveActorCount);
        Assert.Equal(0, bridge.DeliverHandshake(new("concurrent-track", "session", "binding")));
    }

    private sealed class ActivationClock : TimeProvider, IDisposable
    {
        private readonly Barrier _activations = new(2);
        private int _readsToSynchronize;

        public void SynchronizeNextTwoReads() => Volatile.Write(ref _readsToSynchronize, 2);

        public override DateTimeOffset GetUtcNow()
        {
            if (Volatile.Read(ref _readsToSynchronize) > 0 && Interlocked.Decrement(ref _readsToSynchronize) >= 0 &&
                !_activations.SignalAndWait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The two actor factories did not run concurrently.");
            return base.GetUtcNow();
        }

        public void Dispose() => _activations.Dispose();
    }

    private sealed class ObservableHandshakeBridge : IMcpSessionBridge
    {
        private readonly InMemoryMcpSessionBridge _inner = new();

        public event Action<KernelMcpHandshake>? KernelMcpHandshakeRecorded;

        public int DeliverHandshake(KernelMcpHandshake handshake)
        {
            var handlers = KernelMcpHandshakeRecorded;
            handlers?.Invoke(handshake);
            return handlers?.GetInvocationList().Length ?? 0;
        }

        public McpSessionRegistration RegisterSession(string trackId, string sessionId, bool isLead,
            string? boundTaskId, int? taskRunCount, IReadOnlyList<string> enabledPlugins,
            string? runtimeBindingId = null, string? baseUriOverride = null) =>
            _inner.RegisterSession(trackId, sessionId, isLead, boundTaskId, taskRunCount, enabledPlugins, runtimeBindingId, baseUriOverride);

        public McpSessionPrincipal? ResolveToken(string token) => _inner.ResolveToken(token);
        public string? GetTokenForSession(string sessionId) => _inner.GetTokenForSession(sessionId);
        public void UpdateLeadStatus(string trackId, string? newLeadSessionId) => _inner.UpdateLeadStatus(trackId, newLeadSessionId);
        public void UpdateTrackPlugins(string trackId, IReadOnlyList<string> enabledPlugins) => _inner.UpdateTrackPlugins(trackId, enabledPlugins);
        public void RevokeSession(string sessionId, string? runtimeBindingId = null) => _inner.RevokeSession(sessionId, runtimeBindingId);
        public void RevokeTrack(string trackId) => _inner.RevokeTrack(trackId);
    }
}
