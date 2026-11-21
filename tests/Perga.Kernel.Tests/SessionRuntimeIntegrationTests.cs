using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionRuntimeIntegrationTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupCancelThenSendClosesInPlaceOrRequiresShutdownRetry(bool confirmed)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        var initializing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.BeforeOpenAsync = () => { initializing.SetResult(); return release.Task; };
        ctx.AgentRuntime.OnOpenedSession = agent => agent.ShutdownConfirmed = confirmed;
        var receipt = await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "cancel me", RequestKey: "cancel"), Human, default);
        await initializing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await ctx.Kernel.ControlSessionAsync(id, new("stop", TurnId: receipt.TurnId), Human, default);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id).WaitAsync(TimeSpan.FromSeconds(3));
        var first = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Empty(first.RecordedInputs);
        var page = await ctx.Kernel.GetSessionItemsAsync(id, new(), Human, default);
        Assert.Equal("interrupted", page.LastTurn!.Status);
        if (confirmed) Assert.Equal("idle", page.State);
        else
        {
            Assert.Equal("unknown", page.State);
            Assert.Equal("shutdown_unconfirmed", page.UnknownDetail!.Reason);
            var refused = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.SendSessionMessageAsync(id,
                new(Content: "blocked", RequestKey: "blocked"), Human, default));
            Assert.Equal(KernelErrorCode.SessionRecoveryRequired, refused.Code);
            await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.ControlSessionAsync(id, new("reset_context"), Human, default));
            first.ShutdownConfirmed = true;
            await ctx.Kernel.ControlSessionAsync(id, new("reset_context"), Human, default);
            Assert.Single(ctx.AgentRuntime.OpenedSessions);
            Assert.True(first.DisposeCalls >= 2);
        }
        release.SetResult();
        ctx.AgentRuntime.BeforeOpenAsync = null;
        ctx.AgentRuntime.OnOpenedSession = null;
        await ctx.SendAndWaitForTurnAsync(id, new("after stop", "after-stop"));
        Assert.Equal("completed", ctx.Store.GetTurnsForSession(id).Last().Status);
        Assert.Equal(2, ctx.AgentRuntime.OpenedSessions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueTransientWakesWaitMsWithoutWritingEvents(bool worker)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => Hold(release.Task, ct);
        using var events = ctx.EventHub.Subscribe(snapshot.Track.Id);
        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: "hold", Task: worker ? new() : null), "create", Human, default);
        var id = created.Receipt.Session.Id;
        SessionItemsPageDto page;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        do
        {
            page = await ctx.Kernel.GetSessionItemsAsync(id, new(), Human, deadline.Token);
            if (page.Activity?.Text == "started") break;
            await Task.Delay(10, deadline.Token);
        } while (true);
        while (true)
        {
            var observed = await events.Reader.ReadAsync(deadline.Token);
            if (observed.Kind == KernelEventKinds.SessionTextDelta && observed.Payload.GetProperty("sessionId").GetString() == id) break;
        }
        var count = ctx.Store.PersistedEvents.Count;
        var waiting = ctx.Kernel.GetSessionItemsAsync(id,
            new(ActivityTurnId: page.ActiveTurn!.Id, AfterSequence: page.Activity!.Sequence, WaitMs: 10_000), Human, default);
        try
        {
            Assert.False(waiting.IsCompleted);
            await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "queued", RequestKey: "queued", Mode: "queue"), Human, default);
            var awakened = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull(awakened.ActiveTurn);
            Assert.Equal(count, ctx.Store.PersistedEvents.Count);
            var changed = await events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(KernelEventKinds.SessionStateChanged, changed.Kind);
            Assert.Null(changed.EventId);
            Assert.Equal(id, changed.Payload.GetProperty("sessionId").GetString());
            Assert.Single(changed.Payload.EnumerateObject());
            Assert.DoesNotContain(ctx.Store.PersistedEvents, e => e.Kind == KernelEventKinds.SessionStateChanged);
        }
        finally { release.TrySetResult(); }
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReconnectFencesOrdinaryAndWorkerSessionsUntilRetainedReplacementCloses(bool worker)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        ctx.AgentRuntime.SupportsSessionResume = true;
        ctx.AgentRuntime.TurnScript = (_, _, ct) => Lost(ct);
        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: "lose stream", Task: worker ? new() : null), "lost", Human, default);
        var id = created.Receipt.Session.Id;
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var before = (await ctx.Kernel.GetSessionItemsAsync(id, new(), Human, default)).Session;
        Assert.Equal("unknown", before.Status);
        ctx.AgentRuntime.OnOpenedSession = agent => { agent.NativeThreadId = "wrong"; agent.ShutdownConfirmed = false; };
        await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.ControlSessionAsync(id, new("reconnect"), Human, default));
        var replacement = ctx.AgentRuntime.OpenedSessions.Last();
        Assert.Empty(replacement.RecordedInputs);
        var after = await ctx.Kernel.GetSessionItemsAsync(id, new(), Human, default);
        Assert.Equal(before.Epoch, after.Session.Epoch);
        Assert.Equal(before.NativeThreadId, after.Session.NativeThreadId);
        Assert.Equal("shutdown_unconfirmed", after.UnknownDetail!.Reason);
        var refused = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.SendSessionMessageAsync(id,
            new(Content: "blocked", RequestKey: "blocked"), Human, default));
        Assert.Equal(KernelErrorCode.SessionRecoveryRequired, refused.Code);
        await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.ControlSessionAsync(id, new("reset_context"), Human, default));
        Assert.Equal(2, ctx.AgentRuntime.OpenedSessions.Count);
        replacement.ShutdownConfirmed = true;
        var reset = await ctx.Kernel.ControlSessionAsync(id, new("reset_context"), Human, default);
        Assert.Equal(before.Epoch + 1, reset.Epoch);
        Assert.Null(reset.NativeThreadId);
        Assert.True(replacement.DisposeCalls >= 2);
    }

    [Fact]
    public async Task RecoveryProgressIsTransientAndSuccessWritesOneDurableSessionEvent()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        ctx.AgentRuntime.SupportsSessionResume = true;
        ctx.AgentRuntime.TurnScript = (_, _, ct) => Lost(ct);
        await ctx.SendAndWaitForTurnAsync(id, new("lose stream", "lost"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.BeforeOpenAsync = () => { entered.SetResult(); return release.Task; };
        using var events = ctx.EventHub.Subscribe(snapshot.Track.Id);
        var before = ctx.Store.PersistedEvents.Count;
        var wait = ctx.Kernel.GetSessionItemsAsync(id, new(WaitMs: 10_000), Human, default);
        var recovery = ctx.Kernel.ControlSessionAsync(id, new("reconnect"), Human, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var recovering = await wait.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(recovering.UnknownDetail!.Recovering);
            Assert.Equal(before, ctx.Store.PersistedEvents.Count);
        }
        finally { release.TrySetResult(); }
        await recovery;
        Assert.Equal(before + 1, ctx.Store.PersistedEvents.Count);
        var transitions = new List<EventEnvelopeDto>();
        while (events.Reader.TryRead(out var evt))
            if (evt.Kind == KernelEventKinds.SessionStateChanged) transitions.Add(evt);
        Assert.Equal(2, transitions.Count);
        Assert.All(transitions, evt => Assert.Null(evt.EventId));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> Lost([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new UnifiedAgentEvent.TextDelta("partial");
        yield return new UnifiedAgentEvent.TurnError("stream_lost", "disconnected", false);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> Hold(Task release, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.TextDelta("started");
        await release.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }
}
