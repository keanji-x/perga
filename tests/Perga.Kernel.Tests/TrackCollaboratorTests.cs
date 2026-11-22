using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class TrackCollaboratorTests
{
    [Fact]
    public async Task ConcurrentTerminalOpensShareOneIdentity_AndSnapshotsCannotReplaceOwnedState()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        var opens = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => ctx.Kernel.OpenTerminalAsync(
            id, snapshot.Track.LeadSessionId, new OpenTerminalRequest(IdempotencyKey: "shared"))));

        Assert.Single(opens.Select(t => t.Id).Distinct());
        Assert.Single(ctx.TerminalClient.EnsureCalls);
        var terminals = Assert.IsType<TerminalDto[]>(await ctx.Kernel.ListTerminalsAsync(id));
        terminals[0] = terminals[0] with { Title = "caller replaced snapshot" };
        Assert.Equal(opens[0], Assert.Single(await ctx.Kernel.ListTerminalsAsync(id)));

        // A fresh Actor must hydrate the same identity before deciding whether to open.
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(id);
        var reopened = await ctx.Kernel.OpenTerminalAsync(id, snapshot.Track.LeadSessionId,
            new OpenTerminalRequest(IdempotencyKey: "shared"));
        Assert.Equal(opens[0], reopened);
        Assert.Single(ctx.TerminalClient.EnsureCalls);
    }

    [Fact]
    public async Task ClosingTerminalRemovesPersistedIdentity_AndMissingTerminalKeepsNotFoundCode()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        var terminal = await ctx.Kernel.OpenTerminalAsync(id, null, new OpenTerminalRequest());
        await ctx.Kernel.CloseTerminalAsync(id, terminal.Id);
        Assert.Equal(terminal.Id, Assert.Single(ctx.TerminalClient.KillCalls));
        Assert.Empty(await ctx.Kernel.ListTerminalsAsync(id));
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(id);
        Assert.Empty(await ctx.Kernel.ListTerminalsAsync(id));
        var error = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.ResizeTerminalAsync(id, terminal.Id, 80, 24));
        Assert.Equal(KernelErrorCode.NotFound, error.Code);
    }

    [Fact]
    public async Task DeletingTrackCancelsItsAskWaiter_AndKillsOnlyItsRunningTerminals()
    {
        await using var ctx = new KernelTestContext();
        var (_, first) = await ctx.CreateDefaultTrackAsync();
        var (_, second) = await ctx.CreateDefaultTrackAsync();
        var terminal = await ctx.Kernel.OpenTerminalAsync(first.Track.Id, null, new OpenTerminalRequest());
        var other = await ctx.Kernel.OpenTerminalAsync(second.Track.Id, null, new OpenTerminalRequest());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var actor = ctx.Kernel.Actors.GetOrActivate(first.Track.Id);
        var waiting = actor.AskUserAsync(first.Track.LeadSessionId, [new AskQuestionDto("Continue?", ["Yes", "No"])], timeout.Token);
        // The ask's mailbox admission was queued before this read; no polling/sleep is needed.
        Assert.Single((await actor.GetAggregateSnapshotAsync(timeout.Token)).OpenAsks);

        await ctx.Kernel.DeleteTrackAsync(first.Track.Id, timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(terminal.Id, Assert.Single(ctx.TerminalClient.KillCalls));
        Assert.Equal(other, Assert.Single(await ctx.Kernel.ListTerminalsAsync(second.Track.Id)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => actor.GetStatusAsync());
    }

    [Fact]
    public async Task ConcurrentAskAnswersSettleOneWaiterOnce_AndPersistBeforeReturning()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        var session = snapshot.Track.LeadSessionId!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var actor = ctx.Kernel.Actors.GetOrActivate(id);
        var waiting = actor.AskUserAsync(session, [new AskQuestionDto("Choose", ["A", "B"])], timeout.Token);
        var ask = Assert.Single((await actor.GetAggregateSnapshotAsync(timeout.Token)).OpenAsks);
        var answer = TestJson.FromObject(new { choice = "A" });
        async Task<bool> RespondAsync()
        {
            try
            {
                await ctx.Kernel.RespondInteractionAsync(session, ask.Id, new(Answers: answer), new InteropPrincipal.Human(), timeout.Token);
                return true;
            }
            catch (KernelException error) when (error.Code == KernelErrorCode.Conflict) { return false; }
        }
        var outcomes = await Task.WhenAll(RespondAsync(), RespondAsync());
        Assert.Single(outcomes, success => success);
        Assert.Equal(answer.GetRawText(), (await waiting).GetRawText());
        Assert.Empty((await actor.GetAggregateSnapshotAsync(timeout.Token)).OpenAsks);
        Assert.Empty((await ctx.Store.LoadTrackAggregateAsync(id, timeout.Token))!.OpenAsks);
    }
}
