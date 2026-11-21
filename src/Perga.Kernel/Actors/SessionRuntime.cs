using System.Collections.Concurrent;
using Perga.AgentKit;
using Perga.Contracts;

namespace Perga.Kernel.Actors;

internal sealed class LiveContextUsage
{
    public long? Revision { get; set; }
    public long? WindowTokens { get; set; }
    public SessionContextUsage? Latest { get; set; }
    public IdleContextUsage? Trailing { get; set; }
    public IdleContextUsage? TrailingWithWindow { get; set; }
}

internal sealed record OpenSessionHandle(IAgentSession Agent, HarnessLaunchSpec LaunchSpec,
    SessionConfiguration Selection, ConfirmedConfiguration? Confirmation, LiveContextUsage ContextUsage, long Generation);

// Provider methods are called only while holding the gate, never the snapshot lock.
internal sealed class SessionRuntime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private IAgentSession? _agent;
    private HarnessLaunchSpec? _spec;
    private SessionConfiguration? _selection;
    private ConfirmedConfiguration? _confirmation;
    private LiveContextUsage _contextUsage = new();
    private long _generation;
    private CancellationTokenSource? _launchCts;
    private string? _launchTurnId;
    private bool _shutdownUnconfirmed;
    private bool _disposing;
    private int _activeOps;
    private Task? _closeTask;

    public OpenSessionHandle? Snapshot
    {
        get
        {
            lock (_sync) return _agent is null ? null : new(_agent, _spec!, _selection!, _confirmation, _contextUsage, _generation);
        }
    }
    public LiveContextUsage ContextUsage
    {
        get
        {
            lock (_sync) return _contextUsage;
        }
    }
    public bool HasInFlightOperation
    {
        get
        {
            lock (_sync) return _activeOps > 0 || _launchCts is not null;
        }
    }
    public bool HasActiveWork
    {
        get
        {
            lock (_sync) return _activeOps > 0 || _launchCts is not null || _shutdownUnconfirmed;
        }
    }
    public bool IsDisposing
    {
        get
        {
            lock (_sync) return _disposing;
        }
    }
    public bool IsShutdownUnconfirmed
    {
        get
        {
            lock (_sync) return _shutdownUnconfirmed;
        }
    }
    public void InvalidateContextUsage()
    {
        lock (_sync) _contextUsage = new();
    }

    public CancellationTokenSource BeginLaunch(CancellationToken shutdown, string? turnId = null)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _launchTurnId ??= turnId;
            return _launchCts ??= CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        }
    }
    public bool CancelLaunch(string? turnId = null)
    {
        CancellationTokenSource? source;
        lock (_sync)
        {
            if (_launchCts is null || turnId is not null && _launchTurnId != turnId) return false;
            source = _launchCts;
        }
        // Cancellation callbacks are external code and must not run under the snapshot lock.
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException) { }
        return true;
    }
    public void ThrowIfStartupCanceled()
    {
        lock (_sync) _launchCts?.Token.ThrowIfCancellationRequested();
    }

    public void EndLaunch(CancellationTokenSource? expected = null)
    {
        lock (_sync)
        {
            if (expected is not null && !ReferenceEquals(_launchCts, expected)) return;
            _launchCts?.Dispose();
            _launchCts = null;
            _launchTurnId = null;
        }
    }

    public async Task<T> RunAsync<T>(Func<SessionRuntime, Task<T>> operation, CancellationToken ct)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            _activeOps++;
        }
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (_sync) ObjectDisposedException.ThrowIf(_disposing, this);
                return await operation(this).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            lock (_sync) _activeOps--;
        }
    }

    public Task CloseAsync(TimeSpan timeout, CancellationToken ct, bool scoped = false, Func<bool>? shouldClose = null) =>
        RunAsync(async runtime =>
        {
            if (shouldClose is not null)
            {
                lock (_sync)
                {
                    if (_launchCts is not null) return false;
                }
                if (!shouldClose()) return false;
            }
            await runtime.CloseLockedAsync(timeout, ct, scoped).ConfigureAwait(false);
            return true;
        }, ct);

    // Used by a gate operation, including the full recovery transaction.
    public async Task CloseLockedAsync(TimeSpan timeout, CancellationToken ct, bool scoped = false)
    {
        var agent = Snapshot?.Agent;
        if (agent is null) return;
        agent.AttachIdleContextUsageHandler(null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            if (scoped && agent.SupportsScopedClose)
                await agent.TryCloseSessionAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                var startedClose = false;
                // Dispose every fresh handle; join in-flight cleanup and retry failed or unconfirmed cleanup.
                if (_closeTask is null || (_closeTask.IsCompleted && (!agent.ShutdownConfirmed || !_closeTask.IsCompletedSuccessfully)))
                {
                    _closeTask = agent.DisposeAsync().AsTask();
                    startedClose = true;
                }
                if (startedClose || !_closeTask.IsCompleted)
                    await _closeTask.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    _shutdownUnconfirmed = !agent.ShutdownConfirmed;
                    if (agent.ShutdownConfirmed)
                    {
                        _agent = null;
                        _spec = null;
                        _selection = null;
                        _confirmation = null;
                        _contextUsage = new();
                        _closeTask = null;
                    }
                }
            }
        }
        if (!agent.ShutdownConfirmed)
            throw new KernelException(KernelErrorCode.SessionRecoveryRequired, "The previous provider subprocess did not confirm shutdown.");
    }

    public async Task<OpenSessionHandle> OpenLockedAsync(IAgentRuntime runtime, HarnessLaunchSpec spec,
        Action<IAgentSession> validate, TimeSpan timeout, CancellationToken ct)
    {
        if (Snapshot is not null) throw new InvalidOperationException("Close the owned provider before opening another.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var agent = runtime.CreateSession(spec);
            lock (_sync)
            {
                _agent = agent;
                _spec = spec;
                _selection = SessionConfiguration.FromLaunch(spec);
                _confirmation = null;
                _contextUsage = new();
                _generation++;
            }
            await agent.InitializeAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            if (spec.ResumeNativeThreadId is not null && agent.NativeThreadId != spec.ResumeNativeThreadId)
                throw new ProviderSessionNotFoundException("The provider did not restore the original session.");
            validate(agent);
            lock (_sync) _confirmation = agent.ConfigurationState;
            return Snapshot!;
        }
        catch
        {
            try
            {
                await CloseLockedAsync(timeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* Preserve startup failure; the retained handle fences dispatch. */ }
            throw;
        }
    }

    public async Task<OpenSessionHandle> EnsureOpenAsync(IAgentRuntime runtime, SessionConfiguration desired,
        string provider, Func<Task<HarnessLaunchSpec>> buildSpec, Action<IAgentSession> validate,
        TimeSpan timeout, CancellationToken ct) => await RunAsync(async owner =>
    {
        if (IsShutdownUnconfirmed)
            throw new KernelException(KernelErrorCode.SessionRecoveryRequired, "Recover the provider session before sending more input.");
        if (Snapshot is { } handle)
        {
            try
            {
                var outcome = string.Equals(handle.LaunchSpec.Provider, provider, StringComparison.OrdinalIgnoreCase)
                    ? await handle.Agent.ConfigureAsync(desired, ct).WaitAsync(ct).ConfigureAwait(false)
                    : new SessionConfigurationOutcome.RequiresReopen("The provider changed.");
                if (outcome is SessionConfigurationOutcome.Applied applied)
                {
                    validate(handle.Agent);
                    lock (_sync)
                    {
                        if (_confirmation != applied.Configuration) _contextUsage = new();
                        _selection = applied.Configuration.Selection;
                        _confirmation = applied.Configuration;
                    }
                    return Snapshot!;
                }
                await CloseLockedAsync(timeout, CancellationToken.None).ConfigureAwait(false);
                if (outcome is SessionConfigurationOutcome.Unsupported unsupported) throw KernelException.InvalidArgument(unsupported.Reason);
                if (outcome is SessionConfigurationOutcome.Rejected rejected) throw KernelException.PreconditionFailed(rejected.Reason);
            }
            catch
            {
                try
                {
                    await CloseLockedAsync(timeout, CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* Preserve configuration failure; retain ownership until shutdown is confirmed. */ }
                throw;
            }
        }
        return await OpenLockedAsync(runtime, await buildSpec().ConfigureAwait(false), validate, timeout, ct).ConfigureAwait(false);
    }, ct).ConfigureAwait(false);

    public async Task DisposeAsync(TimeSpan timeout)
    {
        lock (_sync) _disposing = true;
        CancelLaunch();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseLockedAsync(timeout, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            EndLaunch();
            _gate.Release();
        }
    }
}

internal sealed class SessionRuntimeRegistry
{
    private readonly ConcurrentDictionary<string, SessionRuntime> _sessions = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private bool _disposing;
    public SessionRuntime Get(string id)
    {
        lock (_sync)
        {
            if (_sessions.TryGetValue(id, out var existing)) return existing;
            ObjectDisposedException.ThrowIf(_disposing, this);
            return _sessions.GetOrAdd(id, _ => new());
        }
    }
    public bool TryGetOpen(string id, out OpenSessionHandle handle)
    {
        handle = _sessions.TryGetValue(id, out var runtime) ? runtime.Snapshot! : null!;
        return handle is not null;
    }
    public bool HasActiveWork => _sessions.Values.Any(runtime => runtime.HasActiveWork);
    public bool IsShutdownUnconfirmed(string id) => _sessions.TryGetValue(id, out var runtime) && runtime.IsShutdownUnconfirmed;
    public async Task CloseAllAsync(TimeSpan timeout)
    {
        SessionRuntime[] runtimes;
        lock (_sync) runtimes = _sessions.Values.ToArray();
        foreach (var runtime in runtimes) runtime.CancelLaunch();
        await Task.WhenAll(runtimes.Select(runtime => runtime.CloseAsync(timeout, CancellationToken.None))).ConfigureAwait(false);
    }

    public async Task DisposeAllAsync(TimeSpan timeout)
    {
        SessionRuntime[] runtimes;
        lock (_sync)
        {
            _disposing = true;
            runtimes = _sessions.Values.ToArray();
        }
        foreach (var runtime in runtimes) runtime.CancelLaunch();
        await Task.WhenAll(runtimes.Select(async runtime =>
        {
            try
            {
                await runtime.DisposeAsync(timeout).ConfigureAwait(false);
            }
            catch { /* Retained unconfirmed handles remain available to a subsequent teardown retry. */ }
        })).ConfigureAwait(false);
    }
}
