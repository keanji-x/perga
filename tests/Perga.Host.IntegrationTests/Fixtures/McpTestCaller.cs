using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests.Fixtures;

internal static class McpTestCaller
{
    public static McpSessionPrincipal RuntimeCaller(this IPergaKernel kernel, string sessionId)
    {
        var token = kernel.McpSessionBridge.GetTokenForSession(sessionId);
        Assert.NotNull(token);
        var caller = kernel.McpSessionBridge.ResolveToken(token);
        Assert.NotNull(caller?.RuntimeBindingId);
        return caller;
    }

    public static async Task<McpSessionPrincipal> OpenMcpCallerAsync(this IPergaKernel kernel, string sessionId,
        CancellationToken ct = default)
    {
        if (kernel.McpSessionBridge.GetTokenForSession(sessionId) is null)
        {
            var human = new InteropPrincipal.Human();
            var page = await kernel.GetSessionItemsAsync(sessionId, new(), human, ct);
            await kernel.SendSessionMessageAsync(sessionId,
                new("Initialize test runtime", Guid.NewGuid().ToString("N")), human, ct);
            await kernel.WaitForTrackIdleAsync(page.Session.TrackId, ct);
        }
        return kernel.RuntimeCaller(sessionId);
    }
}
