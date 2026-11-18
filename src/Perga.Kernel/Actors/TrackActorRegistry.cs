using System.Collections.Concurrent;
using Perga.AgentKit;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Kernel.Actors;

public sealed class TrackActorRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TrackActor> _actors = new(StringComparer.Ordinal);
    private readonly ITrackStore _store;
    private readonly IReportDocEngine _reportEngine;
    private readonly IPolicyVm _policyVm;
    private readonly ITemplateCatalog _templateCatalog;
    private readonly IAgentRuntime _agentRuntime;
    private readonly IWorkspaceDriverRegistry _workspaceDrivers;
    private readonly ITerminalBridgeClient _terminalClient;
    private readonly IMcpSessionBridge _mcpBridge;
    private readonly IShellCommandRunner _commandRunner;
    private readonly EventHub _eventHub;
    private readonly KernelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer? _evictionTimer;
    private int _evicting;
    private bool _disposed;

    public TrackActorRegistry(
        ITrackStore store,
        IReportDocEngine reportEngine,
        IPolicyVm policyVm,
        ITemplateCatalog templateCatalog,
        IAgentRuntime agentRuntime,
        IWorkspaceDriverRegistry workspaceDrivers,
        ITerminalBridgeClient terminalClient,
        IMcpSessionBridge mcpBridge,
        IShellCommandRunner commandRunner,
        EventHub eventHub,
        KernelOptions options,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _reportEngine = reportEngine;
        _policyVm = policyVm;
        _templateCatalog = templateCatalog;
        _agentRuntime = agentRuntime;
        _workspaceDrivers = workspaceDrivers;
        _terminalClient = terminalClient;
        _mcpBridge = mcpBridge;
        _commandRunner = commandRunner;
        _eventHub = eventHub;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.ActorEvictionSweepInterval > TimeSpan.Zero &&
            _options.ActorIdleTimeout > TimeSpan.Zero)
        {
            _evictionTimer = _timeProvider.CreateTimer(
                static state => _ = ((TrackActorRegistry)state!).SweepFireAndForgetAsync(),
                this,
                _options.ActorEvictionSweepInterval,
                _options.ActorEvictionSweepInterval);
        }
    }

    public int ActiveActorCount => _actors.Count;

    public bool IsTrackActiveInMemory(string trackId) => _actors.ContainsKey(trackId);

    public bool TryGetActive(string trackId, out TrackActor? actor) =>
        _actors.TryGetValue(trackId, out actor);

    public TrackActor GetOrActivate(string trackId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(trackId))
        {
            throw KernelException.InvalidArgument("TrackId cannot be empty.");
        }

        return _actors.GetOrAdd(trackId, CreateActorInstance);
    }

    public async Task RemoveAndDisposeAsync(string trackId)
    {
        if (_actors.TryRemove(trackId, out var actor))
        {
            await actor.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<int> EvictIdleActorsAsync(CancellationToken ct = default)
    {
        if (_disposed || Interlocked.Exchange(ref _evicting, 1) != 0)
        {
            return 0;
        }

        try
        {
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var idleThresholdMs = (long)_options.ActorIdleTimeout.TotalMilliseconds;
            var evicted = 0;

            foreach (var kvp in _actors.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var actor = kvp.Value;
                if (actor.HasActiveWork)
                {
                    continue;
                }

                var idleDurationMs = nowMs - actor.LastActivityAtMs;
                if (idleDurationMs < idleThresholdMs)
                {
                    continue;
                }

                if (_actors.TryRemove(kvp.Key, out var removed))
                {
                    if (removed.HasActiveWork)
                    {
                        // Activity raced with removal; re-insert if slot still free.
                        _actors.TryAdd(kvp.Key, removed);
                        continue;
                    }

                    await removed.DisposeAsync().ConfigureAwait(false);
                    evicted++;
                }
            }

            return evicted;
        }
        finally
        {
            Interlocked.Exchange(ref _evicting, 0);
        }
    }

    private async Task SweepFireAndForgetAsync()
    {
        try
        {
            await EvictIdleActorsAsync().ConfigureAwait(false);
        }
        catch
        {
            // Swallow background sweep exceptions.
        }
    }

    private TrackActor CreateActorInstance(string trackId)
    {
        return new TrackActor(
            trackId,
            _store,
            _reportEngine,
            _policyVm,
            _templateCatalog,
            _agentRuntime,
            _workspaceDrivers,
            _terminalClient,
            _mcpBridge,
            _commandRunner,
            _eventHub,
            _options,
            _timeProvider);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_evictionTimer is not null)
        {
            await _evictionTimer.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var kvp in _actors.ToArray())
        {
            if (_actors.TryRemove(kvp.Key, out var actor))
            {
                await actor.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
