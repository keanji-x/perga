using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Perga.Contracts;
using Perga.Host.Http;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.WebSockets;

public static class EventsWebSocketHandler
{
    public const int MaxReplayEvents = 2_000;

    public static async Task HandleAsync(
        HttpContext context,
        IPergaKernel kernel,
        ITrackReadStore readStore)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            var err = HttpErrorHandling.BadRequest("websocket_required", "Expected a WebSocket upgrade request.");
            await err.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        long since = 0;
        if (context.Request.Query.TryGetValue("since", out var sinceValues) &&
            !string.IsNullOrWhiteSpace(sinceValues.ToString()))
        {
            if (!long.TryParse(sinceValues.ToString().Trim(), out since) || since < 0)
            {
                var err = HttpErrorHandling.BadRequest("invalid_argument", "Query parameter 'since' must be a non-negative integer.");
                await err.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        string? trackId = null;
        if (context.Request.Query.TryGetValue("trackId", out var trackValues) &&
            !string.IsNullOrWhiteSpace(trackValues.ToString()))
        {
            trackId = trackValues.ToString().Trim();
        }

        await AuthenticatedWebSocket.RunAsync(context, (socket, _, close, ct) =>
            StreamAsync(socket, close, kernel, readStore, since, trackId, ct));
    }

    private static async Task StreamAsync(WebSocket webSocket, WsSessionCloseCoordinator close, IPergaKernel kernel,
        ITrackReadStore readStore, long since, string? trackId, CancellationToken lifetime)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var ct = sessionCts.Token;

        // 1. Subscribe to live EventBus FIRST so zero events can slip into a race window during replay!
        await using var subscription = kernel.EventBus.Subscribe(trackId);

        var receiveTask = PumpClientControlFramesAsync(webSocket, sessionCts);
        try
        {
            // 2. Check pruning boundary against global event ledger + query missed persisted events.
            var missed = await readStore.ListEventsSinceAsync(since, trackId, limit: MaxReplayEvents + 1, ct)
                .ConfigureAwait(false);
            // Check retention after reading the suffix so concurrent pruning cannot hide a gap.
            var earliestGlobal = await readStore.ListEventsSinceAsync(0, trackId: null, limit: 1, ct).ConfigureAwait(false);
            var earliestRetainedId = earliestGlobal.Count > 0 ? earliestGlobal[0].EventId : null;
            var wasPrunedBeforeSince = earliestRetainedId is { } earliest ? since < earliest - 1 : since > 0;

            long maxDeliveredPersistentId = since;

            if (wasPrunedBeforeSince || missed.Count > MaxReplayEvents)
            {
                // Determine current store tip so subsequent live frames do not duplicate pre-snapshot history.
                maxDeliveredPersistentId = await readStore.GetEventTipIdAsync(ct).ConfigureAwait(false);
                await SendJsonFrameAsync(close, new { kind = "_snapshot_required", tipId = maxDeliveredPersistentId }, ct).ConfigureAwait(false);
            }
            else
            {
                foreach (var persisted in missed)
                {
                    if (persisted.EventId is { } id && id > maxDeliveredPersistentId)
                    {
                        maxDeliveredPersistentId = id;
                    }

                    await SendJsonFrameAsync(close, persisted, ct).ConfigureAwait(false);
                }

                await SendJsonFrameAsync(
                    close,
                    new { kind = "_replay_complete", tipId = maxDeliveredPersistentId },
                    ct).ConfigureAwait(false);
            }

            // 3. Stream live persistent (deduplicated against maxDeliveredPersistentId) & transient frames!
            await foreach (var live in subscription.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (webSocket.State != WebSocketState.Open)
                {
                    break;
                }

                if (live.EventId is { } liveId)
                {
                    if (liveId <= maxDeliveredPersistentId)
                    {
                        continue;
                    }

                    maxDeliveredPersistentId = liveId;
                }

                await SendJsonFrameAsync(close, live, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal disconnect or shutdown.
        }
        catch (WebSocketException)
        {
            // Peer reset connection.
        }
        finally
        {
            sessionCts.Cancel();
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore receive pump termination errors.
            }
        }
    }

    private static async Task PumpClientControlFramesAsync(WebSocket webSocket, CancellationTokenSource sessionCts)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024);
        try
        {
            while (webSocket.State == WebSocketState.Open && !sessionCts.IsCancellationRequested)
            {
                var result = await webSocket.ReceiveAsync(buffer.AsMemory(), sessionCts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    sessionCts.Cancel();
                    break;
                }
            }
        }
        catch
        {
            sessionCts.Cancel();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ValueTask SendJsonFrameAsync<T>(WsSessionCloseCoordinator close, T payload, CancellationToken ct)
    {
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(payload, PergaJsonDefaults.ApiOptions);
        return close.SendAsync(utf8.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
