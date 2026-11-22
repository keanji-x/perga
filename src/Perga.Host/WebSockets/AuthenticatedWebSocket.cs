using System.Net.WebSockets;
using Perga.Host.Auth;
using Perga.Host.Http;

namespace Perga.Host.WebSockets;

internal static class AuthenticatedWebSocket
{
    public static async Task RunAsync(HttpContext context,
        Func<WebSocket, WebSocket?, WsSessionCloseCoordinator, CancellationToken, Task> run,
        Func<CancellationToken, Task<WebSocket?>>? connectUpstream = null)
    {
        if (context.Features.Get<AuthenticatedSession>() is not { } session)
        {
            await HttpErrorHandling.Unauthorized().ExecuteAsync(context);
            return;
        }

        var registry = context.RequestServices.GetRequiredService<AuthWebSocketRegistry>();
        using var lease = registry.Register(session.Identity.SessionId);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        if (!await registry.ValidateAsync(lease, lifetime.Token))
        {
            await HttpErrorHandling.Unauthorized().ExecuteAsync(context);
            return;
        }

        using var upstream = connectUpstream is null ? null : await PrepareAsync(connectUpstream, lease, lifetime);
        if (connectUpstream is not null && upstream is null)
        {
            if (lease.Revoked.IsCompleted && !context.Response.HasStarted)
                await HttpErrorHandling.Unauthorized().ExecuteAsync(context);
            return;
        }

        if (lease.Revoked.IsCompleted || (connectUpstream is not null && !await registry.ValidateAsync(lease, lifetime.Token)))
        {
            await HttpErrorHandling.Unauthorized().ExecuteAsync(context);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var close = new WsSessionCloseCoordinator(socket);
        var work = lease.Revoked.IsCompleted ? Task.CompletedTask : run(socket, upstream, close, lifetime.Token);
        try
        {
            await Task.WhenAny(work, lease.Revoked);
            await close.CloseAsync(lease.Revoked.IsCompleted);
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await work;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task<WebSocket?> PrepareAsync(Func<CancellationToken, Task<WebSocket?>> connect,
        AuthWebSocketRegistry.Lease lease, CancellationTokenSource lifetime)
    {
        var dialing = connect(lifetime.Token);
        if (await Task.WhenAny(dialing, lease.Revoked) == lease.Revoked)
            await lifetime.CancelAsync();
        try
        {
            return await dialing;
        }
        catch (OperationCanceledException) when (lease.Revoked.IsCompleted)
        {
            return null;
        }
    }
}
