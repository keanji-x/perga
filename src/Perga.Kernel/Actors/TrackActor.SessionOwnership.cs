using Perga.AgentKit;
using Perga.Kernel.Internal;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private sealed record PendingSessionLaunch(Task<IAgentSession> Launch, Task Cleanup);

    // Tasks and handles stay associated with their session until shutdown is verified.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingSessionLaunch> _pendingSessionLaunches = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sessionShutdownPending = new(StringComparer.Ordinal);

    private async Task DisposeOwnedSessionCoreAsync(string sessionId, IAgentSession agent)
    {
        using var deadline = new CancellationTokenSource(_options.SessionStartupTimeout);
        await ConfirmProviderShutdownAsync(sessionId, agent, deadline.Token).ConfigureAwait(false);
        _openSessionHandles.Remove(sessionId);
        _sessionShutdownPending.TryRemove(sessionId, out _);
    }

    private async Task<IAgentSession> OpenAgentSessionBoundedAsync(string sessionId, HarnessLaunchSpec spec, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_options.SessionStartupTimeout);
        var launchTask = _agentRuntime.OpenSessionAsync(spec, deadline.Token);
        try { return await launchTask.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _sessionShutdownPending.TryAdd(sessionId, 0);
            var cleanup = DisposeLateLaunchAsync(sessionId, launchTask);
            _pendingSessionLaunches[sessionId] = new(launchTask, cleanup);
            ObserveTaskFault(cleanup);
            throw;
        }
    }

    private async Task DisposeLateLaunchAsync(string sessionId, Task<IAgentSession> launch)
    {
        var agent = await launch.ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(_options.SessionStartupTimeout);
        await ConfirmProviderShutdownAsync(sessionId, agent, deadline.Token).ConfigureAwait(false);
        // The launch task remains owned even if disposal fails or shutdown is unconfirmed.
    }

    private readonly Dictionary<string, Task> _sessionDisposals = new(StringComparer.Ordinal);

    private async Task ConfirmProviderShutdownAsync(string sessionId, IAgentSession agent, CancellationToken ct)
    {
        Task disposal;
        lock (_sessionDisposals)
        {
            if (!_sessionDisposals.TryGetValue(sessionId, out disposal!) || disposal.IsCompleted)
            {
                async Task DisposeAsync() => await agent.DisposeAsync().ConfigureAwait(false);
                _sessionDisposals[sessionId] = disposal = DisposeAsync();
                ObserveTaskFault(disposal);
            }
        }
        await disposal.WaitAsync(ct).ConfigureAwait(false);
        if (!agent.ShutdownConfirmed)
            throw KernelException.PreconditionFailed("The previous provider subprocess shutdown was not confirmed.");
    }

}
