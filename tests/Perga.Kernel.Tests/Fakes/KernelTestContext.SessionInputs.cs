using Perga.Contracts;

namespace Perga.Kernel.Tests.Fakes;

internal sealed partial class KernelTestContext
{
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
