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

        await AuthenticatedWebSocket.RunAsync(context, RelayAsync, async ct =>
        {
            try
            {
                return await ConnectUpstreamAsync(terminalClient, termId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or TerminalBridgeException)
            {
                ct.ThrowIfCancellationRequested();
                await HttpErrorHandling.NotFound($"Unable to attach WebSocket to terminal '{termId}': {ex.Message}")
                    .ExecuteAsync(context).ConfigureAwait(false);
                return null;
            }
        });
    }

    private static async Task RelayAsync(WebSocket client, WebSocket? upstream,
        WsSessionCloseCoordinator close, CancellationToken ct)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var upstreamToClient = PumpFramesAsync(upstream!, close.SendAsync, relayCts.Token);
        var clientToUpstream = PumpFramesAsync(client, (payload, type, endOfMessage, token) =>
            close.IsClosing ? ValueTask.CompletedTask : upstream!.SendAsync(payload, type, endOfMessage, token),
            relayCts.Token);

        await Task.WhenAny(upstreamToClient, clientToUpstream).ConfigureAwait(false);
        await relayCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(upstreamToClient, clientToUpstream).ConfigureAwait(false);
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
        Func<ReadOnlyMemory<byte>, WebSocketMessageType, bool, CancellationToken, ValueTask> send,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RelayBufferBytes);
        try
        {
            while (source.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                await send(
                    buffer.AsMemory(0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
