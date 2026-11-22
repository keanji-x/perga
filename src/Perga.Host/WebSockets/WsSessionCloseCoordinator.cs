using System.Net.WebSockets;

namespace Perga.Host.WebSockets;

internal sealed class WsSessionCloseCoordinator(WebSocket socket) : IDisposable
{
    private readonly SemaphoreSlim _output = new(1, 1);
    private int _closing;

    public bool IsClosing => Volatile.Read(ref _closing) != 0;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type, bool endOfMessage, CancellationToken ct)
    {
        await _output.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _closing) != 0)
                return;
            await socket.SendAsync(payload, type, endOfMessage, ct);
        }
        finally
        {
            _output.Release();
        }
    }

    public async Task CloseAsync(bool revoked)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0)
            return;

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await _output.WaitAsync(deadline.Token);
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseOutputAsync(revoked ? WebSocketCloseStatus.PolicyViolation : WebSocketCloseStatus.NormalClosure,
                        revoked ? "session_revoked" : "closing", deadline.Token);
            }
            finally
            {
                _output.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException)
        {
            socket.Abort();
        }
    }

    public void Dispose() => _output.Dispose();
}
