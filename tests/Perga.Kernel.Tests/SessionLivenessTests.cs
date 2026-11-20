using Perga.AgentKit;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionLivenessTests
{
    [Fact]
    public async Task StopCancelsQueuedInputWhileDispatchAdmissionIsDraining()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        try
        {
            var accepted = await ctx.Kernel.AcceptMessageAsync(id, new("queued", "queued"), default);
            await ctx.Kernel.InterruptSessionAsync(id, turnId: accepted.Receipt!.TurnId);
            await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
            var page = await ctx.Kernel.GetSessionItemsAsync(id);
            Assert.Equal("idle", page.State);
            Assert.Equal("interrupted", page.LastTurn?.Status);
            Assert.Empty(ctx.AgentRuntime.OpenedSessions);
        }
        finally { ctx.Kernel.DispatchAdmission.SetDraining(false); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupIdentityLossFencesSessionEvenWithoutQueue(bool mismatch)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        await ctx.Kernel.UpdateSessionModelAsync(id, new("next-model", "high"));
        // Force an adapter reopen with the persisted native identity.
        ctx.AgentRuntime.OnBeforeOpenSession = _ =>
        {
            if (!mismatch) throw new ProviderSessionNotFoundException("lost original session");
        };
        ctx.AgentRuntime.OnOpenedSession = session => session.NativeThreadId = "different-thread";
        ctx.Kernel.DispatchAdmission.SetDraining(false);
        await ctx.Kernel.AcceptMessageAsync(id, new("reopen", "reopen"), default);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
        var page = await ctx.Kernel.GetSessionItemsAsync(id);
        Assert.Equal("unknown", page.State);
        Assert.Equal("session_lost", page.UnknownDetail?.Reason);
        Assert.False(page.UnknownDetail?.CanReconnect);
        Assert.True(page.UnknownDetail?.CanResetContext);
        Assert.True(page.Session.ReconnectInvalidated);
        Assert.Equal("unknown", page.LastTurn?.Status);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5000000000)]
    public void InvalidTimeoutConfigurationCannotDisableLiveness(long milliseconds)
    {
        var options = new KernelOptions
        {
            SessionStartupTimeout = TimeSpan.FromMilliseconds(milliseconds),
            SessionCompactionTimeout = TimeSpan.FromMilliseconds(milliseconds)
        };
        Assert.Equal(TimeSpan.FromSeconds(30), options.SessionStartupTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.SessionCompactionTimeout);
    }
}
