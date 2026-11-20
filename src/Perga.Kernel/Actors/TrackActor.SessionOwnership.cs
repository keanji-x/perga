using Perga.AgentKit;
using Perga.Kernel.Internal;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private readonly object _disposeSync = new();
    private Task? _disposeTask;

    private sealed record PendingSessionLaunch(Task<IAgentSession> Launch, Task Cleanup);

    // Tasks and handles stay associated with their session until shutdown is verified.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingSessionLaunch> _pendingSessionLaunches = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sessionShutdownPending = new(StringComparer.Ordinal);

    private bool HasPendingSessionOwnership
    {
        get
        {
            if (!_sessionShutdownPending.IsEmpty || !_pendingSessionLaunches.IsEmpty) return true;
            lock (_sessionDisposals) return _sessionDisposals.Values.Any(disposal => !disposal.Task.IsCompleted);
        }
    }

    private async Task DisposeSessionOwnershipAsync()
    {
        foreach (var (sessionId, pending) in _pendingSessionLaunches.ToArray())
        {
            try
            {
                // Cleanup may already be disposing the late handle. Join it before retrying.
                try { await pending.Cleanup.ConfigureAwait(false); }
                catch when (pending.Cleanup.IsCompleted) { }
                if (pending.Launch.IsCompletedSuccessfully)
                    await ConfirmProviderShutdownAsync(sessionId, pending.Launch.Result, CancellationToken.None).ConfigureAwait(false);
                _pendingSessionLaunches.TryRemove(sessionId, out _);
                _sessionShutdownPending.TryRemove(sessionId, out _);
            }
            catch { /* Keep unconfirmed ownership registered on shutdown failure. */ }
        }
        foreach (var (sessionId, handle) in _openSessionHandles.ToArray())
        {
            try
            {
                await ConfirmProviderShutdownAsync(sessionId, handle.Agent, CancellationToken.None).ConfigureAwait(false);
                _openSessionHandles.Remove(sessionId);
                _sessionShutdownPending.TryRemove(sessionId, out _);
            }
            catch { /* Keep unconfirmed ownership registered on shutdown failure. */ }
        }
    }

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
        var launched = new TaskCompletionSource<IAgentSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var needsCleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task CleanupAsync()
        {
            if (await needsCleanup.Task.ConfigureAwait(false))
                await DisposeLateLaunchAsync(sessionId, launched.Task).ConfigureAwait(false);
        }
        var cleanup = CleanupAsync();
        // Own both the launch and its cleanup before invoking any external provider code,
        // including providers that block synchronously before returning their Task.
        _pendingSessionLaunches[sessionId] = new(launched.Task, cleanup);
        ObserveTaskFault(cleanup);
        async Task LaunchAsync()
        {
            try { launched.TrySetResult(await _agentRuntime.OpenSessionAsync(spec, deadline.Token).ConfigureAwait(false)); }
            catch (Exception error) { launched.TrySetException(error); }
        }
        _ = LaunchAsync();
        try
        {
            var agent = await launched.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            needsCleanup.TrySetResult(false);
            // The caller removes the pending entry only after installing the open handle.
            return agent;
        }
        catch (OperationCanceledException)
        {
            _sessionShutdownPending.TryAdd(sessionId, 0);
            needsCleanup.TrySetResult(true);
            throw;
        }
        catch
        {
            needsCleanup.TrySetResult(false);
            _pendingSessionLaunches.TryRemove(sessionId, out _);
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

    private sealed record SessionDisposal(IAgentSession Agent, Task Task);

    private readonly Dictionary<string, SessionDisposal> _sessionDisposals = new(StringComparer.Ordinal);

    private async Task ConfirmProviderShutdownAsync(string sessionId, IAgentSession agent, CancellationToken ct)
    {
        Task disposal;
        lock (_sessionDisposals)
        {
            if (_sessionDisposals.TryGetValue(sessionId, out var tracked) &&
                (!tracked.Task.IsCompleted || (ReferenceEquals(tracked.Agent, agent) &&
                    tracked.Task.IsCompletedSuccessfully && agent.ShutdownConfirmed)))
                disposal = tracked.Task;
            else
            {
                async Task DisposeAsync() => await agent.DisposeAsync().ConfigureAwait(false);
                disposal = DisposeAsync();
                _sessionDisposals[sessionId] = new(agent, disposal);
                ObserveTaskFault(disposal);
            }
        }
        await disposal.WaitAsync(ct).ConfigureAwait(false);
        if (!agent.ShutdownConfirmed)
            throw KernelException.PreconditionFailed("The previous provider subprocess shutdown was not confirmed.");
    }

}
