using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perga.TermClient;

/// <summary>
/// Pure BCL JSON-RPC 2.0 and WebSocket client for <c>perga-term-bridge</c>, supporting both
/// TCP endpoints (<c>http://127.0.0.1:PORT</c>) and Unix Domain Sockets (<c>unix:///path/to/bridge.sock</c>).
/// </summary>
public sealed class TerminalBridgeClient : ITerminalBridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly Uri _rpcUri;
    private readonly Uri _wsBaseUri;
    private readonly string? _unixSocketPath;
    private readonly IAsyncDisposable? _ownedResource;
    private long _nextRequestId;
    private int _disposed;

    /// <summary>
    /// Creates a client connected to either a TCP URI (<c>http://127.0.0.1:PORT</c>) or
    /// a Unix Domain Socket URI (<c>unix:///path/to/sock</c>).
    /// </summary>
    public TerminalBridgeClient(Uri endpointUri, IAsyncDisposable? ownedResource = null)
    {
        ArgumentNullException.ThrowIfNull(endpointUri);
        _ownedResource = ownedResource;
        _disposeHttpClient = true;

        if (IsUnixSocketUri(endpointUri, out var socketPath))
        {
            _unixSocketPath = socketPath;
            _rpcUri = new Uri("http://localhost/rpc");
            _wsBaseUri = new Uri("ws://localhost");
            _httpClient = CreateUnixSocketHttpClient(socketPath);
        }
        else
        {
            var (httpBase, wsBase) = NormalizeTcpUris(endpointUri);
            _rpcUri = new Uri(httpBase, "/rpc");
            _wsBaseUri = wsBase;
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                UseProxy = false
            };
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    /// <summary>
    /// Creates a client using a caller-supplied <see cref="HttpClient"/> and explicit RPC/WS base URIs.
    /// </summary>
    public TerminalBridgeClient(HttpClient httpClient, Uri baseUri, bool disposeHttpClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUri);
        var (httpBase, wsBase) = NormalizeTcpUris(baseUri);
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _rpcUri = new Uri(httpBase, "/rpc");
        _wsBaseUri = wsBase;
    }

    /// <summary>
    /// Gets the Unix Domain Socket path if this client is connected over UDS; otherwise <see langword="null"/>.
    /// </summary>
    public string? UnixSocketPath => _unixSocketPath;

    public static TerminalBridgeClient ForTcp(Uri tcpEndpoint) => new(tcpEndpoint);

    public static TerminalBridgeClient ForUnixSocket(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        var fullPath = Path.GetFullPath(socketPath);
        return new TerminalBridgeClient(BuildUnixUri(fullPath));
    }

    public static Uri BuildUnixUri(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        var fullPath = Path.GetFullPath(socketPath).Replace('\\', '/');
        if (!fullPath.StartsWith('/'))
        {
            fullPath = "/" + fullPath;
        }

        return new Uri($"unix://{fullPath}");
    }

    public async Task<TermEnsureResult> EnsureAsync(TermEnsureRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ThrowIfDisposed();

        var wireParams = new EnsureParamsWire(
            req.TerminalId,
            req.Program,
            req.Args,
            req.Cwd,
            req.Env,
            req.Cols,
            req.Rows);

        var wireResult = await InvokeRpcAsync<EnsureParamsWire, EnsureResultWire>("term.ensure", wireParams, ct)
            .ConfigureAwait(false);

        return new TermEnsureResult(wireResult.TerminalId, wireResult.Pid, wireResult.Created);
    }

    public async Task<TermObserveResult> ObserveAsync(TermObserveRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ThrowIfDisposed();

        var waitFor = req.WaitFor switch
        {
            TermWaitMode.None => "none",
            TermWaitMode.Elapsed => "elapsed",
            TermWaitMode.Change => "change",
            TermWaitMode.Text => "text",
            _ => throw new ArgumentOutOfRangeException(nameof(req), req.WaitFor, "Unsupported TermWaitMode.")
        };

        // When WaitFor is Change or Text and caller left WaitMs at 0, let perga-term-bridge apply its mode default budget.
        int? waitMs = req.WaitFor is TermWaitMode.Change or TermWaitMode.Text && req.WaitMs <= 0
            ? null
            : Math.Max(0, req.WaitMs);

        var wireParams = new ObserveParamsWire(
            req.TerminalId,
            Math.Max(0, req.ScrollOffset),
            waitFor,
            waitMs,
            Math.Max(0, req.SettleMs),
            req.WaitText,
            req.WaitTextAbsent);

        var wireResult = await InvokeRpcAsync<ObserveParamsWire, ObserveResultWire>("term.observe", wireParams, ct)
            .ConfigureAwait(false);

        if (wireResult.Frame is null || wireResult.Frame.Cursor is null)
        {
            throw new TerminalBridgeException("Malformed term.observe response: missing frame or cursor.");
        }

        var frame = new TerminalFrame(
            wireResult.Frame.Cols,
            wireResult.Frame.Rows,
            new TerminalCursor(
                wireResult.Frame.Cursor.Column,
                wireResult.Frame.Cursor.Row,
                wireResult.Frame.Cursor.Visible),
            wireResult.Frame.Alternate,
            wireResult.Frame.ScrollOffset,
            wireResult.Frame.HistoryRows,
            wireResult.Frame.Modes,
            wireResult.Frame.Text ?? []);

        return new TermObserveResult(
            wireResult.Revision,
            frame,
            wireResult.Exited,
            wireResult.ExitCode);
    }

    public async Task<TermActionResult> SendActionAsync(TermActionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ThrowIfDisposed();

        var kind = req.Kind switch
        {
            TermActionKind.Text => "text",
            TermActionKind.Keys => "keys",
            TermActionKind.Paste => "paste",
            TermActionKind.Submit => "submit",
            _ => throw new ArgumentOutOfRangeException(nameof(req), req.Kind, "Unsupported TermActionKind.")
        };

        var payload = req.Payload.ValueKind == JsonValueKind.Undefined
            ? ParseEmptyObject()
            : req.Payload;

        var wireParams = new ActionParamsWire(req.TerminalId, kind, payload);
        var wireResult = await InvokeRpcAsync<ActionParamsWire, ActionResultWire>("term.action", wireParams, ct)
            .ConfigureAwait(false);

        return new TermActionResult(wireResult.AckRevision);
    }

    public async Task ResizeAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        ThrowIfDisposed();

        var wireParams = new ResizeParamsWire(terminalId, cols, rows);
        _ = await InvokeRpcAsync<ResizeParamsWire, ResizeResultWire>("term.resize", wireParams, ct)
            .ConfigureAwait(false);
    }

    public async Task KillAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        ThrowIfDisposed();

        var wireParams = new KillParamsWire(terminalId, Math.Max(0, graceMs));
        _ = await InvokeRpcAsync<KillParamsWire, KillResultWire>("term.kill", wireParams, ct)
            .ConfigureAwait(false);
    }

    public Uri GetWebSocketRelayUri(string terminalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        var escaped = Uri.EscapeDataString(terminalId);
        var builder = new UriBuilder(_wsBaseUri)
        {
            Path = $"/ws/{escaped}"
        };
        return builder.Uri;
    }

    /// <summary>
    /// Opens and connects a <see cref="ClientWebSocket"/> to <c>/ws/{terminalId}</c>,
    /// transparently routing over either TCP or the configured Unix Domain Socket.
    /// </summary>
    public async Task<ClientWebSocket> ConnectWebSocketAsync(
        string terminalId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var wsUri = GetWebSocketRelayUri(terminalId);
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(wsUri, _httpClient, ct).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        if (_ownedResource is not null)
        {
            await _ownedResource.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<TResult> InvokeRpcAsync<TParams, TResult>(
        string method,
        TParams parameters,
        CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var requestEnvelope = new JsonRpcRequestEnvelope<TParams>("2.0", id, method, parameters);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(requestEnvelope, JsonOptions);

        using var content = new ByteArrayContent(jsonBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName
        };

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, _rpcUri)
        {
            Content = content
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TerminalBridgeException(
                $"Failed to send JSON-RPC '{method}' request to perga-term-bridge: {ex.Message}",
                innerException: ex);
        }

        using (response)
        {
            var bodyBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = Encoding.UTF8.GetString(bodyBytes);
                throw new TerminalBridgeException(
                    $"HTTP {(int)response.StatusCode} from perga-term-bridge on '{method}': {snippet}");
            }

            JsonRpcResponseEnvelope<TResult>? rpcEnvelope;
            try
            {
                rpcEnvelope = JsonSerializer.Deserialize<JsonRpcResponseEnvelope<TResult>>(bodyBytes, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new TerminalBridgeException(
                    $"Invalid JSON-RPC response from perga-term-bridge on '{method}': {ex.Message}",
                    innerException: ex);
            }

            if (rpcEnvelope is null)
            {
                throw new TerminalBridgeException($"Empty JSON-RPC response from perga-term-bridge on '{method}'.");
            }

            if (rpcEnvelope.Error is not null)
            {
                throw new TerminalBridgeException(
                    $"JSON-RPC '{method}' failed ({rpcEnvelope.Error.Code}): {rpcEnvelope.Error.Message}",
                    rpcEnvelope.Error.Code);
            }

            if (rpcEnvelope.Result is null)
            {
                throw new TerminalBridgeException($"JSON-RPC '{method}' returned null result.");
            }

            return rpcEnvelope.Result;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static HttpClient CreateUnixSocketHttpClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    var endpoint = new UnixDomainSocketEndPoint(socketPath);
                    await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static bool IsUnixSocketUri(Uri uri, out string socketPath)
    {
        if (string.Equals(uri.Scheme, "unix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "http+unix", StringComparison.OrdinalIgnoreCase))
        {
            socketPath = Uri.UnescapeDataString(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(socketPath))
            {
                throw new ArgumentException($"Unix socket URI '{uri}' does not specify a socket path.", nameof(uri));
            }

            return true;
        }

        socketPath = string.Empty;
        return false;
    }

    private static (Uri HttpBase, Uri WsBase) NormalizeTcpUris(Uri endpointUri)
    {
        var scheme = endpointUri.Scheme.ToLowerInvariant();
        var (httpScheme, wsScheme) = scheme switch
        {
            "http" or "ws" => ("http", "ws"),
            "https" or "wss" => ("https", "wss"),
            _ => throw new ArgumentException(
                $"Unsupported terminal bridge endpoint scheme '{endpointUri.Scheme}'. Expected http, https, ws, wss, or unix.",
                nameof(endpointUri))
        };

        var httpBuilder = new UriBuilder(endpointUri)
        {
            Scheme = httpScheme,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        var wsBuilder = new UriBuilder(endpointUri)
        {
            Scheme = wsScheme,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return (httpBuilder.Uri, wsBuilder.Uri);
    }

    private static JsonElement ParseEmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    private sealed record JsonRpcRequestEnvelope<TParams>(
        string Jsonrpc,
        long Id,
        string Method,
        TParams Params);

    private sealed record JsonRpcErrorWire(int Code, string Message);

    private sealed record JsonRpcResponseEnvelope<TResult>(
        string? Jsonrpc,
        JsonRpcErrorWire? Error,
        TResult? Result);

    private sealed record EnsureParamsWire(
        string TerminalId,
        string Program,
        IReadOnlyList<string> Args,
        string Cwd,
        IReadOnlyDictionary<string, string> Env,
        ushort Cols,
        ushort Rows);

    private sealed record EnsureResultWire(string TerminalId, int Pid, bool Created);

    private sealed record ObserveParamsWire(
        string TerminalId,
        int ScrollOffset,
        string WaitFor,
        int? WaitMs,
        int SettleMs,
        IReadOnlyList<string>? WaitText,
        IReadOnlyList<string>? WaitTextAbsent);

    private sealed record CursorWire(uint Column, uint Row, bool Visible);

    private sealed record FrameWire(
        ushort Cols,
        ushort Rows,
        CursorWire? Cursor,
        bool Alternate,
        int ScrollOffset,
        int HistoryRows,
        uint Modes,
        List<string>? Text);

    private sealed record ObserveResultWire(
        ulong Revision,
        FrameWire? Frame,
        bool Exited,
        int? ExitCode);

    private sealed record ActionParamsWire(
        string TerminalId,
        string Kind,
        JsonElement Payload);

    private sealed record ActionResultWire(ulong AckRevision);

    private sealed record ResizeParamsWire(string TerminalId, ushort Cols, ushort Rows);

    private sealed record ResizeResultWire(bool Ok);

    private sealed record KillParamsWire(string TerminalId, int GraceMs);

    private sealed record KillResultWire(bool Exited, int? ExitCode);
}
