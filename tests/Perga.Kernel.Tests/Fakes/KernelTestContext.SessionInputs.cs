using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Contracts;

namespace Perga.Kernel.Tests.Fakes;

internal sealed partial class KernelTestContext
{
    public McpSessionPrincipal RuntimeCaller(string sessionId)
    {
        var token = Kernel.McpSessionBridge.GetTokenForSession(sessionId);
        Assert.NotNull(token);
        var principal = Kernel.McpSessionBridge.ResolveToken(token);
        Assert.NotNull(principal?.RuntimeBindingId);
        return principal;
    }

    public async Task<McpSessionPrincipal> OpenMcpCallerAsync(string sessionId, CancellationToken ct = default)
    {
        if (Kernel.McpSessionBridge.GetTokenForSession(sessionId) is null)
            Assert.Equal("completed", (await SendAndWaitForTurnAsync(sessionId,
                new("Initialize test runtime", Guid.NewGuid().ToString("N")), ct)).Status);
        return RuntimeCaller(sessionId);
    }

    public async Task<InteropPrincipal.Session> AuthorizeAsync(McpSessionPrincipal caller, CancellationToken ct = default)
    {
        var generation = await Kernel.GuardMcpToolCallAsync(caller, "perga", KernelMcpToolNames.TrackStatus, TestJson.Parse("{}"), ct);
        return new(caller.TrackId, caller.SessionId, caller.IsLead, caller.BoundTaskId, 0, caller.Token, caller.RuntimeBindingId)
            { AuthorizedPolicyGeneration = generation };
    }

    public async Task<SessionTurnDto> SendAndWaitForTurnAsync(string sessionId, SendSessionMessageRequest request, CancellationToken ct = default)
    {
        var accepted = await Kernel.SendSessionMessageAsync(sessionId, request, new InteropPrincipal.Human(), ct);
        var receipt = accepted;
        if (request.Mode == "steer") return Store.GetTurnsForSession(sessionId).Single(t => t.Id == receipt.TurnId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var turn = Store.GetTurnsForSession(sessionId).Single(t => t.Id == receipt.TurnId);
            if (turn.Status is not ("queued" or "running"))
            {
                var trackId = (await Store.FindTrackIdBySessionAsync(sessionId, deadline.Token))!;
                await Kernel.WaitForTrackIdleAsync(trackId, deadline.Token);
                return turn;
            }
            await Task.Delay(5, deadline.Token);
        }
    }

    // Keep the manual clock fixed while establishing the idle precondition. An operation's
    // result can arrive before its mailbox finally and tracked background cleanup complete.
    public async Task WaitForActorQuiescenceAsync(string trackId, CancellationToken ct)
    {
        var actor = Kernel.Actors.GetOrActivate(trackId);
        await actor.WaitForBackgroundWorkAsync(ct);
        while (actor.HasActiveWork)
            await Task.Delay(1, ct);
    }
}
