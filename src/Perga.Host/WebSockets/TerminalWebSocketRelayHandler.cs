using System.Buffers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Perga.Host.Http;
using Perga.Host.Terminals;
using Perga.TermClient;

namespace Perga.Host.WebSockets;

public static class TerminalWebSocketRelayHandler
{
    private const int RelayBufferBytes = 68 * 1024; // Fits 64KB + 1 byte initial replay frame in a single read when possible.

    public static async Task HandleAsync(
        HttpContext context,
        string termId,
        ITerminalBridgeClient terminalClient)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            var err = HttpErrorHandling.BadRequest("websocket_required", "Expected a WebSocket upgrade request.");
            await err.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(termId))
        {
            var err = HttpErrorHandling.BadRequest("invalid_argument", "Terminal ID is required.");
            await err.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        ClientWebSocket upstreamWs;
        try
        {
            upstreamWs = await ConnectUpstreamAsync(terminalClient, termId, context.RequestAborted).ConfigureAwait(false);
        }
        catch (WebSocketException wsEx)
        {
            var err = HttpErrorHandling.NotFound($"Unable to attach WebSocket to terminal '{termId}': {wsEx.Message}");
            await err.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }
        catch (TerminalBridgeException tbEx)
        {
            var err = HttpErrorHandling.NotFound($"Terminal bridge unavailable for '{termId}': {tbEx.Message}");
            await err.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        using (upstreamWs)
        {
            using var clientWs = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

            var upstreamToClient = PumpFramesAsync(upstreamWs, clientWs, relayCts);
            var clientToUpstream = PumpFramesAsync(clientWs, upstreamWs, relayCts);

            await Task.WhenAny(upstreamToClient, clientToUpstream).ConfigureAwait(false);
            relayCts.Cancel();

            try
            {
                await Task.WhenAll(upstreamToClient, clientToUpstream).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation / reset on shutdown.
            }

            await SafeCloseAsync(upstreamWs).ConfigureAwait(false);
            await SafeCloseAsync(clientWs).ConfigureAwait(false);
        }
    }

    private static async Task<ClientWebSocket> ConnectUpstreamAsync(
        ITerminalBridgeClient terminalClient,
        string termId,
        CancellationToken ct)
    {
        if (terminalClient is IHostTerminalWebSocketConnector connector)
        {
            return await connector.ConnectWebSocketAsync(termId, ct).ConfigureAwait(false);
        }

        if (terminalClient is TerminalBridgeClient directBridgeClient)
        {
            return await directBridgeClient.ConnectWebSocketAsync(termId, ct).ConfigureAwait(false);
        }

        var relayUri = terminalClient.GetWebSocketRelayUri(termId);
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(relayUri, ct).ConfigureAwait(false);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    private static async Task PumpFramesAsync(
        WebSocket source,
        WebSocket destination,
        CancellationTokenSource relayCts)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RelayBufferBytes);
        try
        {
            while (source.State == WebSocketState.Open &&
                   destination.State == WebSocketState.Open &&
                   !relayCts.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(buffer.AsMemory(), relayCts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    relayCts.Cancel();
                    break;
                }

                await destination.SendAsync(
                    buffer.AsMemory(0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    relayCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
            relayCts.Cancel();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task SafeCloseAsync(WebSocket ws)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "relay closed", cts.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }
}
