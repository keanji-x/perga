using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Perga.Host.Auth;

public sealed class LoginRateLimitOptions
{
    public int IpLimit { get; set; } = 20;
    public int PairLimit { get; set; } = 5;
    public int MaxIpPartitions { get; set; } = 4_096;
    public int MaxPairPartitions { get; set; } = 16_384;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);
    public int SegmentsPerWindow { get; set; } = 15;
    public int KdfConcurrency { get; set; } = 4;
    public int KdfQueueLimit { get; set; } = 32;
    public TimeSpan KdfWaitTimeout { get; set; } = TimeSpan.FromSeconds(2);
}

public sealed class LoginRateLimiter : IAsyncDisposable
{
    private readonly LoginRateLimitOptions _options;
    private readonly ConcurrencyLimiter _kdf;
    private readonly FailureWindow<string> _ips;
    private readonly FailureWindow<(string Ip, string Username)> _pairs;
    private readonly object _gate = new();
    private readonly TimeSpan _retryFallback;
    private bool _disposed;

    public LoginRateLimiter(IOptions<LoginRateLimitOptions> options)
    {
        _options = options.Value;
        _ips = new(_options, _options.IpLimit, _options.MaxIpPartitions);
        _pairs = new(_options, _options.PairLimit, _options.MaxPairPartitions);
        _retryFallback = _options.Window / _options.SegmentsPerWindow;
        _kdf = new(new()
        {
            PermitLimit = _options.KdfConcurrency,
            QueueLimit = _options.KdfQueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    public int KdfRetrySeconds => RetrySeconds(_options.KdfWaitTimeout);

    public Attempt? TryReserve(string ip, string username, out int retrySeconds)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                retrySeconds = KdfRetrySeconds;
                return null;
            }
            var ipPartition = _ips.GetOrCreate(ip);
            if (GetRetryAfter(ipPartition) is { } ipRetry)
            {
                retrySeconds = RetrySeconds(ipRetry);
                return null;
            }

            var pair = (ip, username);
            var pairPartition = _pairs.GetOrCreate(pair);
            if (GetRetryAfter(pairPartition) is { } pairRetry)
            {
                _ips.RemoveIfIdle(ip, ipPartition!);
                retrySeconds = RetrySeconds(pairRetry);
                return null;
            }

            ipPartition!.InFlight++;
            pairPartition!.InFlight++;
            retrySeconds = 0;
            return new(this, pair);
        }
    }

    private TimeSpan? GetRetryAfter(FailurePartition? partition)
    {
        if (partition is null)
            return _retryFallback;

        var available = partition.Limiter.GetStatistics()!.CurrentAvailablePermits;
        if (available == 0)
        {
            // A zero-permit probe carries RetryAfter when exhausted and cannot consume a newly replenished permit.
            using var lease = partition.Limiter.AttemptAcquire(0);
            if (!lease.IsAcquired)
                return lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? retry : _retryFallback;
            available = partition.Limiter.GetStatistics()!.CurrentAvailablePermits;
        }

        return partition.InFlight >= available ? _options.KdfWaitTimeout : null;
    }

    private static int RetrySeconds(TimeSpan delay) => Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));

    public async ValueTask<RateLimitLease?> EnterKdfAsync(CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(_options.KdfWaitTimeout);
        try
        {
            var lease = await _kdf.AcquireAsync(1, wait.Token).ConfigureAwait(false);
            if (lease.IsAcquired)
                return lease;
            lease.Dispose();
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private void Complete((string Ip, string Username) pair, bool failed)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _ips.Complete(pair.Ip, failed);
            _pairs.Complete(pair, failed);
        }
    }

    private sealed class FailurePartition(SlidingWindowRateLimiter limiter)
    {
        public SlidingWindowRateLimiter Limiter { get; } = limiter;
        public int InFlight { get; set; }
    }

    private sealed class FailureWindow<TKey>(LoginRateLimitOptions options, int permits, int capacity) : IDisposable
        where TKey : notnull
    {
        private readonly Dictionary<TKey, FailurePartition> _partitions = new();
        private long _nextSweep;

        public FailurePartition? GetOrCreate(TKey key)
        {
            if (_partitions.TryGetValue(key, out var existing))
                return existing;

            if (_partitions.Count >= capacity)
            {
                var now = Environment.TickCount64;
                if (now >= _nextSweep)
                {
                    // Keep the cap under spray while retaining every partition with failures or pending work.
                    _nextSweep = now + 1_000;
                    foreach (var (partitionKey, partition) in _partitions)
                        RemoveIfIdle(partitionKey, partition);
                }
                if (_partitions.Count >= capacity)
                    return null;
            }

            var created = new FailurePartition(new(new()
            {
                PermitLimit = permits,
                Window = options.Window,
                SegmentsPerWindow = options.SegmentsPerWindow,
                AutoReplenishment = true,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
            _partitions.Add(key, created);
            return created;
        }

        public void Complete(TKey key, bool failed)
        {
            var partition = _partitions[key];
            if (failed)
            {
                using var failure = partition.Limiter.AttemptAcquire();
            }
            partition.InFlight--;
            RemoveIfIdle(key, partition);
        }

        public void RemoveIfIdle(TKey key, FailurePartition partition)
        {
            if (partition.InFlight != 0 || partition.Limiter.IdleDuration is null)
                return;

            _partitions.Remove(key);
            partition.Limiter.Dispose();
        }

        public void Dispose()
        {
            foreach (var partition in _partitions.Values)
                partition.Limiter.Dispose();
            _partitions.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _kdf.DisposeAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _ips.Dispose();
            _pairs.Dispose();
        }
    }

    public sealed class Attempt : IDisposable
    {
        private LoginRateLimiter? _owner;
        private readonly (string Ip, string Username) _pair;
        private bool _enteredKdf;
        private bool _succeeded;

        internal Attempt(LoginRateLimiter owner, (string, string) pair) => (_owner, _pair) = (owner, pair);

        public void StartKdf() => _enteredKdf = true;
        public void Succeed() => _succeeded = true;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Complete(_pair, _enteredKdf && !_succeeded);
    }
}
