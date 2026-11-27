using Perga.Host.Auth;
using Perga.Host.Configuration;
using Perga.Kernel.Ports;

namespace Perga.Host.WebSockets;

internal sealed class AuthWebSocketRegistry(
    IAuthStore store, TimeProvider time, PergaHostOptions options, LocalSessionManager localSessions, ILogger<AuthWebSocketRegistry> logger) : BackgroundService
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly Dictionary<string, Registration> _sessions = new(StringComparer.Ordinal);

    public Lease Register(string id)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(id, out var session))
                _sessions.Add(id, session = new());
            session.Leases++;
            return new(this, id, session);
        }
    }

    public void DisconnectSession(string id) => Disconnect(id, expected: null);

    public void DisconnectLocalSessions()
    {
        lock (_gate)
        {
            foreach (var (id, session) in _sessions)
            {
                if (!IsLocalSession(id))
                    continue;
                _sessions.Remove(id);
                session.Revoked.TrySetResult();
            }
        }
    }

    private void Disconnect(string id, Registration? expected)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var session) && (expected is null || ReferenceEquals(session, expected)))
            {
                _sessions.Remove(id);
                session.Revoked.TrySetResult();
            }
        }
    }

    public async Task<bool> ValidateAsync(Lease lease, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        try
        {
            // The SQLite provider can block synchronously while waiting for the database.
            return !lease.Revoked.IsCompleted &&
                await ProbeAsync(lease.Id, deadline.Token).WaitAsync(deadline.Token) &&
                !lease.Revoked.IsCompleted;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not validate WebSocket session {SessionId}", lease.Id);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.AuthWsRevalidationInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                KeyValuePair<string, Registration>[] snapshot;
                lock (_gate)
                    snapshot = [.. _sessions];

                await Parallel.ForEachAsync(snapshot, stoppingToken, (entry, ct) => new ValueTask(RevalidateAsync(entry, ct)));
                if (localSessions.IsActive && !snapshot.Any(entry => IsLocalSession(entry.Key)))
                {
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        deadline.CancelAfter(Deadline);
                        await ProbeLocalOwnerAsync(deadline.Token).WaitAsync(deadline.Token);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Could not check for an owner account in local mode");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RevalidateAsync(KeyValuePair<string, Registration> entry, CancellationToken ct)
    {
        var (id, registration) = entry;
        if (registration.Revoked.Task.IsCompleted)
            return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Deadline);
        bool valid;
        try
        {
            // A timed-out SQLite call can still be running. Wait for it to finish without reusing its result.
            if (registration.Probe is { IsCompleted: false } previous)
                await ((Task)previous).WaitAsync(deadline.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            deadline.Token.ThrowIfCancellationRequested();
            registration.Probe = ProbeAsync(id, deadline.Token);
            valid = await registration.Probe.WaitAsync(deadline.Token);
            registration.Probe = null;
            registration.Errors = 0;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (registration.Probe is { IsCompleted: true })
                registration.Probe = null;
            if (IsLocalSession(id) && localSessions.IsOwnerSetupPending)
                return;
            logger.LogWarning(ex, "Could not revalidate WebSocket session {SessionId}", id);
            valid = ++registration.Errors < 2;
        }

        if (!valid)
            Disconnect(id, registration);
    }

    private Task<bool> ProbeAsync(string id, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
            if (IsLocalSession(id))
                return !await localSessions.CheckOwnerAsync(store, this, ct) && localSessions.IsValidSessionId(id, nowMs);
            return await store.IsOwnerSessionValidAsync(id, nowMs, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // A timed-out background probe can finish after its caller stops awaiting it.
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }, ct);

    private static bool IsLocalSession(string id) => id.StartsWith("loc_", StringComparison.Ordinal);

    private Task<bool> ProbeLocalOwnerAsync(CancellationToken ct) =>
        Task.Run(() => localSessions.CheckOwnerAsync(store, this, ct), ct);

    internal sealed class Registration
    {
        public TaskCompletionSource Revoked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Leases { get; set; }
        public int Errors { get; set; }
        public Task<bool>? Probe { get; set; }
    }

    internal sealed class Lease(AuthWebSocketRegistry owner, string id, Registration session) : IDisposable
    {
        private int _disposed;
        public string Id => id;
        public Task Revoked => session.Revoked.Task;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (owner._gate)
            {
                if (--session.Leases == 0 && owner._sessions.TryGetValue(id, out var current) && ReferenceEquals(current, session))
                    owner._sessions.Remove(id);
            }
        }
    }
}
