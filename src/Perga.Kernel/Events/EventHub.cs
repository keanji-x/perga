using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Events;

public sealed class EventHub : IKernelEventBus, IDisposable
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscribers = new();
    private readonly SemaphoreSlim _storeSyncLock = new(1, 1);
    private long _fallbackEventSeq;
    private long _storeSyncedTipId;
    private bool _storeTipInitialized;
    private bool _disposed;

    public IKernelEventSubscription Subscribe(string? trackId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Guid.NewGuid();
        var sub = new Subscription(this, id, trackId);
        _subscribers[id] = sub;
        return sub;
    }

    public EventEnvelopeDto PublishPersistent(EventEnvelopeDto envelope)
    {
        if (KernelEventKinds.IsTransient(envelope.Kind))
        {
            throw new ArgumentException($"Transient event kind '{envelope.Kind}' cannot be published as persistent.", nameof(envelope));
        }

        var assignedId = envelope.EventId ?? Interlocked.Increment(ref _fallbackEventSeq);
        if (assignedId > Volatile.Read(ref _fallbackEventSeq))
        {
            Interlocked.Exchange(ref _fallbackEventSeq, assignedId);
        }

        var finalEnvelope = envelope.EventId.HasValue
            ? envelope
            : envelope with { EventId = assignedId };

        Broadcast(finalEnvelope);
        return finalEnvelope;
    }

    public EventEnvelopeDto PublishTransient(
        string? areaId,
        string? trackId,
        string kind,
        JsonElement payload,
        long createdAtMs)
    {
        if (!KernelEventKinds.IsTransient(kind))
        {
            throw new ArgumentException($"Event kind '{kind}' is not a registered transient streaming event.", nameof(kind));
        }

        var envelope = new EventEnvelopeDto(
            EventId: null,
            AreaId: areaId,
            TrackId: trackId,
            Kind: kind,
            Payload: payload.Clone(),
            CreatedAtMs: createdAtMs);

        Broadcast(envelope);
        return envelope;
    }

    /// <summary>
    /// Ensures cursor is aligned with existing database events before first live write so pre-existing rows are not re-broadcast.
    /// </summary>
    internal async Task EnsureInitializedFromStoreAsync(ITrackReadStore store, CancellationToken ct)
    {
        if (Volatile.Read(ref _storeTipInitialized))
        {
            return;
        }

        await _storeSyncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_storeTipInitialized)
            {
                return;
            }

            long cursor = 0;
            while (true)
            {
                var batch = await store.ListEventsSinceAsync(cursor, trackId: null, limit: 500, ct).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    break;
                }

                var maxBatchId = cursor;
                foreach (var evt in batch)
                {
                    if (evt.EventId is { } id && id > maxBatchId)
                    {
                        maxBatchId = id;
                    }
                }

                if (maxBatchId <= cursor)
                {
                    break;
                }

                cursor = maxBatchId;
                if (batch.Count < 500)
                {
                    break;
                }
            }

            _storeSyncedTipId = cursor;
            _fallbackEventSeq = cursor;
            Volatile.Write(ref _storeTipInitialized, true);
        }
        finally
        {
            _storeSyncLock.Release();
        }
    }

    /// <summary>
    /// Syncs newly written persistent events from <paramref name="store"/> so broadcast <see cref="EventEnvelopeDto.EventId"/>
    /// matches SQLite <c>events.id</c>, falling back to monotonic in-memory IDs if the store does not index events.
    /// </summary>
    internal async Task PublishSyncedAfterStoreWriteAsync(
        ITrackReadStore store,
        EventEnvelopeDto fallbackTemplate,
        CancellationToken ct)
    {
        await _storeSyncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batch = await store.ListEventsSinceAsync(_storeSyncedTipId, trackId: null, limit: 100, ct).ConfigureAwait(false);
            var publishedAny = false;

            foreach (var evt in batch)
            {
                if (evt.EventId is { } id && id > _storeSyncedTipId)
                {
                    _storeSyncedTipId = id;
                    if (id > _fallbackEventSeq)
                    {
                        _fallbackEventSeq = id;
                    }

                    Broadcast(evt);
                    publishedAny = true;
                }
            }

            if (!publishedAny)
            {
                PublishPersistent(fallbackTemplate);
            }
        }
        finally
        {
            _storeSyncLock.Release();
        }
    }

    private void Broadcast(EventEnvelopeDto envelope)
    {
        foreach (var kvp in _subscribers)
        {
            var sub = kvp.Value;
            if (sub.TrackIdFilter is null ||
                string.Equals(sub.TrackIdFilter, envelope.TrackId, StringComparison.Ordinal))
            {
                sub.TryEnqueue(envelope);
            }
        }
    }

    private void Unsubscribe(Guid id)
    {
        _subscribers.TryRemove(id, out _);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var kvp in _subscribers.ToArray())
        {
            kvp.Value.Dispose();
        }

        _subscribers.Clear();
        _storeSyncLock.Dispose();
    }

    private sealed class Subscription : IKernelEventSubscription
    {
        private readonly EventHub _hub;
        private readonly Guid _id;
        private readonly Channel<EventEnvelopeDto> _channel;
        private int _disposed;

        public Subscription(EventHub hub, Guid id, string? trackIdFilter)
        {
            _hub = hub;
            _id = id;
            TrackIdFilter = trackIdFilter;
            _channel = Channel.CreateUnbounded<EventEnvelopeDto>(new UnboundedChannelOptions
            {
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public string? TrackIdFilter { get; }
        public ChannelReader<EventEnvelopeDto> Reader => _channel.Reader;

        public void TryEnqueue(EventEnvelopeDto envelope)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _channel.Writer.TryWrite(envelope);
            }
        }

        public async IAsyncEnumerable<EventEnvelopeDto> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    yield return item;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _hub.Unsubscribe(_id);
                _channel.Writer.TryComplete();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
