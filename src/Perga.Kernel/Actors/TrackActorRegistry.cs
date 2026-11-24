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
    private readonly IFlowTrackSpawner _flowTrackSpawner;
    private readonly IReportDocEngine _reportEngine;
    private readonly IPolicyVm _policyVm;
    private readonly ITemplateCatalog _templateCatalog;
    private readonly IExecutionEnvironmentRegistry _executionEnvironments;
    private readonly IMcpSessionBridge _mcpBridge;
    private readonly EventHub _eventHub;
    private readonly KernelOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IScheduleCronEvaluator? _scheduleCronEvaluator;
    private readonly IMemoryContextProvider _memoryContext;
    private readonly ITimer? _evictionTimer;
    private int _evicting;
    private bool _disposed;

    public TrackActorRegistry(
        ITrackStore store,
        IFlowTrackSpawner flowTrackSpawner,
        IReportDocEngine reportEngine,
        IPolicyVm policyVm,
        ITemplateCatalog templateCatalog,
        IExecutionEnvironmentRegistry executionEnvironments,
        IMcpSessionBridge mcpBridge,
        EventHub eventHub,
        KernelOptions options,
        TimeProvider? timeProvider = null,
        IScheduleCronEvaluator? scheduleCronEvaluator = null,
        IMemoryContextProvider? memoryContext = null)
    {
        _store = store;
        _flowTrackSpawner = flowTrackSpawner;
        _scheduleCronEvaluator = scheduleCronEvaluator;
        _memoryContext = memoryContext ?? NoMemoryContextProvider.Instance;
        _reportEngine = reportEngine;
        _policyVm = policyVm;
        _templateCatalog = templateCatalog;
        _executionEnvironments = executionEnvironments;
        _mcpBridge = mcpBridge;
        _eventHub = eventHub;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.ActorEvictionSweepInterval > TimeSpan.Zero &&
            (_options.ActorIdleTimeout > TimeSpan.Zero || _options.SessionIdleTimeout > TimeSpan.Zero))
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

    internal Task ClearTrackOriginsAsync(string originTrackId) =>
        Task.WhenAll(_actors.Values.Select(actor => actor.ClearOriginAsync(originTrackId)));

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
                await actor.EvictIdleSessionRuntimesAsync(ct).ConfigureAwait(false);

                if (_options.ActorIdleTimeout > TimeSpan.Zero && actor.CanEvictIdleActor &&
                    nowMs - actor.LastActivityAtMs >= idleThresholdMs && _actors.TryRemove(kvp))
                {
                    if (!actor.CanEvictIdleActor)
                    {
                        // Activity raced with removal; re-insert if slot still free.
                        _actors.TryAdd(kvp.Key, actor);
                    }
                    else
                    {
                        await actor.DisposeAsync().ConfigureAwait(false);
                        evicted++;
                    }
                }
            }

            return evicted;
        }
        finally
        {
            Interlocked.Exchange(ref _evicting, 0);
        }
    }

    public async Task<int> EvictIdleSessionRuntimesAsync(CancellationToken ct = default)
    {
        var evicted = 0;
        foreach (var actor in _actors.Values)
        {
            ct.ThrowIfCancellationRequested();
            evicted += await actor.EvictIdleSessionRuntimesAsync(ct).ConfigureAwait(false);
        }
        return evicted;
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
            _flowTrackSpawner,
            _reportEngine,
            _policyVm,
            _templateCatalog,
            _executionEnvironments,
            _mcpBridge,
            _eventHub,
            _options,
            _timeProvider,
            _scheduleCronEvaluator,
            _memoryContext);
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
