using System.Net.WebSockets;
using Perga.Host.Configuration;
using Perga.TermClient;

namespace Perga.Host.Terminals;

public interface IHostTerminalWebSocketConnector
{
    Task<ClientWebSocket> ConnectWebSocketAsync(string terminalId, CancellationToken ct = default);
}

/// <summary>
/// Supervises either an externally configured <c>perga-term-bridge</c> endpoint or an auto-managed
/// child <see cref="TerminalBridgeDaemon"/> process, lazily or eagerly starting the daemon on demand
/// and tearing it down cleanly when <c>Perga.Host</c> stops.
/// </summary>
public sealed class ManagedTerminalBridgeClient : ITerminalBridgeClient, IHostTerminalWebSocketConnector
{
    private readonly PergaHostOptions _options;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private TerminalBridgeDaemon? _ownedDaemon;
    private TerminalBridgeClient? _innerClient;
    private int _disposed;

    public ManagedTerminalBridgeClient(PergaHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public TerminalBridgeDaemon? ManagedDaemon => _ownedDaemon;

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        _ = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
    }

    public async Task<TermEnsureResult> EnsureAsync(TermEnsureRequest req, CancellationToken ct)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        return await client.EnsureAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<TermObserveResult> ObserveAsync(TermObserveRequest req, CancellationToken ct)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        return await client.ObserveAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<TermActionResult> SendActionAsync(TermActionRequest req, CancellationToken ct)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        return await client.SendActionAsync(req, ct).ConfigureAwait(false);
    }

    public async Task ResizeAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        await client.ResizeAsync(terminalId, cols, rows, ct).ConfigureAwait(false);
    }

    public async Task KillAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        await client.KillAsync(terminalId, graceMs, ct).ConfigureAwait(false);
    }

    public Uri GetWebSocketRelayUri(string terminalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        if (_innerClient is not null)
        {
            return _innerClient.GetWebSocketRelayUri(terminalId);
        }

        if (!string.IsNullOrWhiteSpace(_options.TerminalBridgeEndpoint))
        {
            var raw = new Uri(_options.TerminalBridgeEndpoint);
            if (string.Equals(raw.Scheme, "unix", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw.Scheme, "http+unix", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri($"ws://localhost/ws/{Uri.EscapeDataString(terminalId)}");
            }

            var wsScheme = string.Equals(raw.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(raw.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
                ? "wss"
                : "ws";
            var builder = new UriBuilder(raw)
            {
                Scheme = wsScheme,
                Path = $"/ws/{Uri.EscapeDataString(terminalId)}",
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri;
        }

        return new Uri($"ws://127.0.0.1:0/ws/{Uri.EscapeDataString(terminalId)}");
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync(string terminalId, CancellationToken ct = default)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        return await client.ConnectWebSocketAsync(terminalId, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_innerClient is not null)
        {
            await _innerClient.DisposeAsync().ConfigureAwait(false);
            _innerClient = null;
        }

        if (_ownedDaemon is not null)
        {
            await _ownedDaemon.DisposeAsync().ConfigureAwait(false);
            _ownedDaemon = null;
        }

        _initLock.Dispose();
    }

    private async Task<TerminalBridgeClient> GetOrInitializeClientAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_innerClient is not null)
        {
            return _innerClient;
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_innerClient is not null)
            {
                return _innerClient;
            }

            if (!string.IsNullOrWhiteSpace(_options.TerminalBridgeEndpoint))
            {
                _innerClient = new TerminalBridgeClient(new Uri(_options.TerminalBridgeEndpoint));
                return _innerClient;
            }

            if (!_options.AutoManageTerminalBridge)
            {
                throw new TerminalBridgeException(
                    "No TerminalBridgeEndpoint is configured and AutoManageTerminalBridge is disabled.");
            }

            // Use a short /tmp UDS path (< 100 bytes for sun_path limit) + loopback TCP port 0.
            string? udsPath = null;
            if (!OperatingSystem.IsWindows())
            {
                udsPath = Path.Combine(Path.GetTempPath(), $"pg-tb-{Guid.NewGuid():N}"[..22] + ".sock");
            }

            var daemon = await TerminalBridgeDaemon.StartAsync(
                new TerminalBridgeDaemonOptions(
                    ExecutablePath: _options.TerminalBridgeExecutablePath,
                    ListenUnixSocketPath: udsPath,
                    ListenTcpAddress: "127.0.0.1:0",
                    EnableLinuxUnshare: _options.EnableTerminalLinuxUnshare),
                ct).ConfigureAwait(false);

            _ownedDaemon = daemon;
            _innerClient = daemon.TcpEndpoint is not null
                ? daemon.CreateTcpClient()
                : daemon.CreateClient();
            return _innerClient;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
