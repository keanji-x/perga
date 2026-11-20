using System.Runtime.CompilerServices;
using System.Text.Json;
using Perga.AgentKit;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class ScriptableApprovalClaimTests
{
    [Fact]
    public async Task ConcurrentClaimsHaveOneWinnerAndClaimedDeliverySurvivesCancellation()
    {
        await using var session = await OpenSession();
        await using var turn = session.StreamTurnAsync(new("test", "turn"), default).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        var claims = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => Claim(session))));
        var sender = Assert.Single(claims, claim => claim is not null);
        await session.CancelTurnAsync(default);
        await session.DisposeAsync();
        Assert.Null(Claim(session));
        await sender!();
        Assert.Equal(("request", true, "option", "reason"),
            Assert.Single(((ScriptableFakeAgentSession)session).DetailedApprovals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndDisposalExpirePendingAndLaterEmittedRequests(bool dispose)
    {
        await using var session = await OpenSession();
        await using var turn = session.StreamTurnAsync(new("test", "turn"), default).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        if (dispose)
            await session.DisposeAsync();
        else
            await session.CancelTurnAsync(default);
        var claims = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => Claim(session))));
        Assert.All(claims, Assert.Null);
        Assert.True(await turn.MoveNextAsync());
        Assert.Null(session.TryClaimApprovalResponse("late", true, null, null, default));
    }

    [Fact]
    public async Task CompletionExpiresPendingRequestsBeforeCompletionIsObserved()
    {
        await using var session = await OpenSession();
        await using var turn = session.StreamTurnAsync(new("test", "turn"), default).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        Assert.True(await turn.MoveNextAsync());
        Assert.True(await turn.MoveNextAsync());
        Assert.IsType<UnifiedAgentEvent.TurnCompleted>(turn.Current);
        Assert.Null(Claim(session));
        Assert.Null(session.TryClaimApprovalResponse("late", true, null, null, default));
    }

    [Fact]
    public async Task DirectResponseConsumesRegisteredRequestAndCannotClaimItAgain()
    {
        await using var session = await OpenSession();
        Assert.Null(Claim(session));
        await using var turn = session.StreamTurnAsync(new("test", "turn"), default).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        await session.RespondApprovalAsync("request", true, "option", "reason");
        Assert.Null(Claim(session));
        Assert.Equal(("request", true, "option", "reason"),
            Assert.Single(((ScriptableFakeAgentSession)session).DetailedApprovals));
    }

    [Fact]
    public async Task ReemittingClaimedOrExpiredRequestCannotMakeItPendingAgain()
    {
        await using var session = await OpenSession();
        await using (var first = session.StreamTurnAsync(new("test", "first"), default).GetAsyncEnumerator())
        {
            Assert.True(await first.MoveNextAsync());
            var sender = Assert.IsType<Func<Task>>(Claim(session));
            await sender();
            Assert.True(await first.MoveNextAsync());
        }
        await using var second = session.StreamTurnAsync(new("test", "second"), default).GetAsyncEnumerator();
        Assert.True(await second.MoveNextAsync());
        Assert.Null(Claim(session));
        Assert.True(await second.MoveNextAsync());
        Assert.Null(session.TryClaimApprovalResponse("late", true, null, null, default));
    }

    private static Func<Task>? Claim(IAgentSession session) =>
        session.TryClaimApprovalResponse("request", true, "option", "reason", default);

    private static async Task<IAgentSession> OpenSession()
    {
        var runtime = new ScriptableFakeAgentRuntime { TurnScript = (_, _, ct) => Stream(ct) };
        return await runtime.OpenSessionAsync(new("codex", null, ".", null, null,
            new Dictionary<string, McpServerConfig>()), default);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> Stream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return new UnifiedAgentEvent.ApprovalRequested("request", "tool", JsonSerializer.SerializeToElement(new { }));
        yield return new UnifiedAgentEvent.ApprovalRequested("late", "tool", JsonSerializer.SerializeToElement(new { }));
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }
}
