using Perga.Contracts;

namespace Perga.Kernel.Tests.Fakes;

internal sealed partial class KernelTestContext
{
    public async Task<SessionTurnDto> SendAndWaitForTurnAsync(string sessionId, SendMessageRequest request, CancellationToken ct = default)
    {
        var accepted = await Kernel.AcceptMessageAsync(sessionId, request, ct);
        if (accepted.SteeredTurn is { } steered) return steered;
        var receipt = accepted.Receipt ?? throw new InvalidOperationException("Expected an accepted input.");
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
}
