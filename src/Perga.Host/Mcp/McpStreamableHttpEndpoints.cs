using Perga.Host.Auth;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Perga.Host.Http;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.Mcp;

public sealed class McpTransportSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, McpTransportSession> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    public McpTransportSession CreateOrGet(string? requestedSessionId, string ownerToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedSessionId) &&
            _sessions.TryGetValue(requestedSessionId, out var existing) &&
            string.Equals(existing.OwnerToken, ownerToken, StringComparison.Ordinal))
        {
            return existing;
        }

        var id = string.IsNullOrWhiteSpace(requestedSessionId)
            ? $"mcp_{Guid.NewGuid():N}"
            : requestedSessionId.Trim();
        var created = new McpTransportSession(id, ownerToken);
        return _sessions.GetOrAdd(id, created);
    }

    public McpTransportSession? TryGetAuthorized(string sessionId, string ownerToken)
    {
        if (_sessions.TryGetValue(sessionId, out var session) &&
            string.Equals(session.OwnerToken, ownerToken, StringComparison.Ordinal))
        {
            return session;
        }

        return null;
    }

    public bool Remove(string sessionId, string ownerToken)
    {
        if (_sessions.TryGetValue(sessionId, out var existing) &&
            string.Equals(existing.OwnerToken, ownerToken, StringComparison.Ordinal) &&
            _sessions.TryRemove(sessionId, out var removed))
        {
            removed.Dispose();
            return true;
        }

        return false;
    }

    public bool PublishNotification(string mcpSessionId, string method, object? parameters = null)
    {
        if (!_sessions.TryGetValue(mcpSessionId, out var session))
        {
            return false;
        }

        var json = JsonSerializer.Serialize(
            new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters
            },
            PergaJsonDefaults.McpOptions);
        return session.EnqueueSseEvent(json);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var kvp in _sessions.ToArray())
        {
            if (_sessions.TryRemove(kvp.Key, out var s))
            {
                s.Dispose();
            }
        }
    }
}

public sealed class McpTransportSession : IDisposable
{
    private readonly Channel<string> _sseChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private int _disposed;

    public McpTransportSession(string id, string ownerToken)
    {
        Id = id;
        OwnerToken = ownerToken;
    }

    public string Id { get; }
    public string OwnerToken { get; }
    public ChannelReader<string> Reader => _sseChannel.Reader;

    public bool EnqueueSseEvent(string jsonPayload) =>
        Volatile.Read(ref _disposed) == 0 && _sseChannel.Writer.TryWrite(jsonPayload);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _sseChannel.Writer.TryComplete();
        }
    }
}

public static class McpStreamableHttpEndpoints
{
    public const string DefaultProtocolVersion = "2025-03-26";
    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "2025-03-26",
        "2024-11-05"
    };

    public static IEndpointRouteBuilder MapPergaMcpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var mcp = endpoints.MapGroup("/mcp").RequireAuthorization(PergaAuthPolicies.McpSessionOnly);

        mcp.MapMethods("/kernel", ["POST", "GET", "DELETE"], (
            HttpContext ctx,
            KernelMcpBackend backend,
            McpTransportSessionManager sessions) =>
            HandleAuthorizedMcpAsync(ctx, ctx.Features.Get<McpSessionPrincipal>()!, backend, sessions, preAuthorizeAsync: null));

        mcp.MapMethods("/vcs", ["POST", "GET", "DELETE"], (
            HttpContext ctx,
            VcsMcpBackend backend,
            McpTransportSessionManager sessions) =>
            HandleAuthorizedMcpAsync(ctx, ctx.Features.Get<McpSessionPrincipal>()!, backend, sessions, preAuthorizeAsync: null));

        mcp.MapMethods("/p/{pluginId}", ["POST", "GET", "DELETE"], async (
            string pluginId,
            HttpContext ctx,
            McpPluginRouter pluginRouter,
            McpTransportSessionManager sessions) =>
        {
            var principal = ctx.Features.Get<McpSessionPrincipal>()!;
            var backend = pluginRouter.TryCreateBackend(pluginId);
            if (backend is null)
            {
                await HttpErrorHandling.NotFound($"MCP plugin '{pluginId}' is not configured.").ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            await HandleAuthorizedMcpAsync(
                ctx,
                principal,
                backend,
                sessions,
                preAuthorizeAsync: token => pluginRouter.VerifyTrackAccessAsync(principal, pluginId, token))
                .ConfigureAwait(false);
        });

        return endpoints;
    }

    private static async Task HandleAuthorizedMcpAsync(
        HttpContext ctx,
        McpSessionPrincipal principal,
        IMcpToolBackend backend,
        McpTransportSessionManager sessions,
        Func<CancellationToken, Task>? preAuthorizeAsync)
    {
        var ct = ctx.RequestAborted;

        if (HttpMethods.IsDelete(ctx.Request.Method))
        {
            var delSessionId = GetHeaderValue(ctx, "Mcp-Session-Id");
            if (string.IsNullOrWhiteSpace(delSessionId))
            {
                await HttpErrorHandling.BadRequest("missing_session_id", "Mcp-Session-Id header is required for DELETE.")
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            sessions.Remove(delSessionId, principal.Token);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            if (preAuthorizeAsync is not null)
            {
                await preAuthorizeAsync(ct).ConfigureAwait(false);
            }

            var sseSessionId = GetHeaderValue(ctx, "Mcp-Session-Id");
            if (string.IsNullOrWhiteSpace(sseSessionId))
            {
                await HttpErrorHandling.BadRequest("missing_session_id", "Mcp-Session-Id header is required for GET SSE stream.")
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            var transportSession = sessions.CreateOrGet(sseSessionId, principal.Token);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.Headers["Mcp-Session-Id"] = transportSession.Id;
            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            await WriteSseBytesAsync(ctx.Response, ": connected\n\n", ct).ConfigureAwait(false);

            try
            {
                while (await transportSession.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (transportSession.Reader.TryRead(out var jsonPayload))
                    {
                        var frame = $"event: message\ndata: {jsonPayload}\n\n";
                        await WriteSseBytesAsync(ctx.Response, frame, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Normal SSE stream disconnect.
            }

            return;
        }

        principal = ctx.RequestServices.GetRequiredService<IPergaKernel>().ObserveMcpExecution(principal);

        // POST: Single or batch JSON-RPC 2.0 Streamable HTTP
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException jex)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonRpcRawAsync(
                ctx,
                BuildJsonRpcError(id: null, code: -32700, message: $"Parse error: {jex.Message}"),
                preferSse: false,
                ct).ConfigureAwait(false);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var incomingSessionId = GetHeaderValue(ctx, "Mcp-Session-Id");
            string? responseSessionId = incomingSessionId;
            var preferSse = PrefersEventStream(ctx);

            if (root.ValueKind == JsonValueKind.Array)
            {
                var items = root.EnumerateArray().ToArray();
                if (items.Length == 0)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteJsonRpcRawAsync(
                        ctx,
                        BuildJsonRpcError(id: null, code: -32600, message: "Invalid Request: empty batch."),
                        preferSse: false,
                        ct).ConfigureAwait(false);
                    return;
                }

                var responses = new List<object>(items.Length);
                foreach (var item in items)
                {
                    var (respObj, mintedSid) = await DispatchSingleEnvelopeAsync(
                        item,
                        principal,
                        backend,
                        sessions,
                        responseSessionId,
                        preAuthorizeAsync,
                        ct).ConfigureAwait(false);
                    if (mintedSid is not null)
                    {
                        responseSessionId = mintedSid;
                    }

                    if (respObj is not null)
                    {
                        responses.Add(respObj);
                    }
                }

                if (!string.IsNullOrWhiteSpace(responseSessionId))
                {
                    ctx.Response.Headers["Mcp-Session-Id"] = responseSessionId;
                }

                if (responses.Count == 0)
                {
                    ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }

                await WriteJsonRpcRawAsync(ctx, responses, preferSse, ct).ConfigureAwait(false);
                return;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                var (singleResp, mintedSid) = await DispatchSingleEnvelopeAsync(
                    root,
                    principal,
                    backend,
                    sessions,
                    responseSessionId,
                    preAuthorizeAsync,
                    ct).ConfigureAwait(false);
                if (mintedSid is not null)
                {
                    responseSessionId = mintedSid;
                }

                if (!string.IsNullOrWhiteSpace(responseSessionId))
                {
                    ctx.Response.Headers["Mcp-Session-Id"] = responseSessionId;
                }

                if (singleResp is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                    return;
                }

                await WriteJsonRpcRawAsync(ctx, singleResp, preferSse, ct).ConfigureAwait(false);
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteJsonRpcRawAsync(
                ctx,
                BuildJsonRpcError(id: null, code: -32600, message: "Invalid JSON-RPC payload."),
                preferSse: false,
                ct).ConfigureAwait(false);
        }
    }

    private static async Task<(object? Response, string? MintedSessionId)> DispatchSingleEnvelopeAsync(
        JsonElement envelope,
        McpSessionPrincipal principal,
        IMcpToolBackend backend,
        McpTransportSessionManager sessions,
        string? currentSessionId,
        Func<CancellationToken, Task>? preAuthorizeAsync,
        CancellationToken ct)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            return (BuildJsonRpcError(null, -32600, "Invalid Request: expected JSON object."), null);
        }

        var hasId = envelope.TryGetProperty("id", out var idElem);
        JsonElement? clonedId = hasId ? idElem.Clone() : null;

        if (!envelope.TryGetProperty("method", out var methodElem) || methodElem.ValueKind != JsonValueKind.String)
        {
            // JSON-RPC client response acknowledgment (no method) -> accepted with no reply.
            if (!hasId || envelope.TryGetProperty("result", out _) || envelope.TryGetProperty("error", out _))
            {
                return (null, null);
            }

            return (BuildJsonRpcError(clonedId, -32600, "Invalid Request: missing 'method'."), null);
        }

        var method = methodElem.GetString() ?? string.Empty;
        var parameters = envelope.TryGetProperty("params", out var pElem)
            ? pElem
            : McpToolResultFormatter.EmptyObject;

        // Notifications (no 'id')
        if (!hasId)
        {
            // notifications/initialized, notifications/cancelled, etc.
            return (null, null);
        }

        switch (method)
        {
            case "initialize":
            {
                if (preAuthorizeAsync is not null)
                {
                    await preAuthorizeAsync(ct).ConfigureAwait(false);
                }

                var transportSession = sessions.CreateOrGet(currentSessionId, principal.Token);
                var requestedProtocol = McpToolResultFormatter.OptionalString(parameters, ["protocolVersion"]);
                var negotiatedProtocol = requestedProtocol is not null && SupportedProtocolVersions.Contains(requestedProtocol)
                    ? requestedProtocol
                    : DefaultProtocolVersion;

                var initResult = new
                {
                    protocolVersion = negotiatedProtocol,
                    capabilities = new
                    {
                        tools = new { listChanged = true }
                    },
                    serverInfo = new
                    {
                        name = backend.ServerName,
                        version = "1.0.0"
                    }
                };

                return (BuildJsonRpcSuccess(clonedId, initResult), transportSession.Id);
            }

            case "ping":
                return (BuildJsonRpcSuccess(clonedId, new { }), null);

            case "tools/list":
            {
                var tools = await backend.ListToolsAsync(principal, ct).ConfigureAwait(false);
                return (BuildJsonRpcSuccess(clonedId, new { tools }), null);
            }

            case "tools/call":
            {
                var toolName = McpToolResultFormatter.OptionalString(parameters, ["name"]);
                if (string.IsNullOrWhiteSpace(toolName))
                {
                    return (BuildJsonRpcError(clonedId, -32602, "Invalid params: 'name' is required for tools/call."), null);
                }

                var toolArgs = McpToolResultFormatter.TryGetPropertyIgnoreCase(parameters, ["arguments"], out var argsElem)
                    ? argsElem
                    : McpToolResultFormatter.EmptyObject;

                var callResult = await backend.CallToolAsync(principal, toolName, toolArgs, ct).ConfigureAwait(false);
                return (BuildJsonRpcSuccess(clonedId, callResult), null);
            }

            default:
                return (BuildJsonRpcError(clonedId, -32601, $"Method not found: '{method}'."), null);
        }
    }

    internal static McpSessionPrincipal? AuthenticateCaller(HttpContext ctx, IMcpSessionBridge bridge)
    {
        var authHeader = GetHeaderValue(ctx, "Authorization");
        if (string.IsNullOrWhiteSpace(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rawToken = authHeader["Bearer ".Length..].Trim();
        if (!rawToken.StartsWith("pg_", StringComparison.Ordinal))
        {
            return null;
        }

        return bridge.ResolveToken(rawToken);
    }

    private static string? GetHeaderValue(HttpContext ctx, string headerName)
    {
        if (ctx.Request.Headers.TryGetValue(headerName, out var values))
        {
            var raw = values.ToString().Trim();
            return raw.Length == 0 ? null : raw;
        }

        return null;
    }

    private static bool PrefersEventStream(HttpContext ctx)
    {
        if (ctx.Request.Query.TryGetValue("stream", out var streamVal) &&
            string.Equals(streamVal.ToString(), "sse", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var accept = GetHeaderValue(ctx, "Accept");
        if (string.IsNullOrWhiteSpace(accept))
        {
            return false;
        }

        var sseIdx = accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase);
        if (sseIdx < 0)
        {
            return false;
        }

        var jsonIdx = accept.IndexOf("application/json", StringComparison.OrdinalIgnoreCase);
        return jsonIdx < 0 || sseIdx < jsonIdx;
    }

    private static object BuildJsonRpcSuccess(JsonElement? id, object result) =>
        new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };

    private static object BuildJsonRpcError(JsonElement? id, int code, string message) =>
        new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new
            {
                code,
                message
            }
        };

    private static async Task WriteJsonRpcRawAsync(
        HttpContext ctx,
        object responsePayload,
        bool preferSse,
        CancellationToken ct)
    {
        if (!preferSse)
        {
            if (ctx.Response.StatusCode == StatusCodes.Status200OK || ctx.Response.StatusCode == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
            }

            ctx.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, responsePayload, PergaJsonDefaults.McpOptions, ct)
                .ConfigureAwait(false);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        if (responsePayload is IEnumerable<object> batchItems)
        {
            foreach (var item in batchItems)
            {
                var line = JsonSerializer.Serialize(item, PergaJsonDefaults.McpOptions);
                await WriteSseBytesAsync(ctx.Response, $"event: message\ndata: {line}\n\n", ct).ConfigureAwait(false);
            }
        }
        else
        {
            var line = JsonSerializer.Serialize(responsePayload, PergaJsonDefaults.McpOptions);
            await WriteSseBytesAsync(ctx.Response, $"event: message\ndata: {line}\n\n", ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteSseBytesAsync(HttpResponse response, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await response.Body.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
