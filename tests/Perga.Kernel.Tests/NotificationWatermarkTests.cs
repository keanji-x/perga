using Perga.Kernel.Ports;
using Perga.VcsKit;
using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class NotificationWatermarkTests
{
    [Fact]
    public async Task ActiveOverlayUsesLatestAskAndPreservesFallbackAfterAttentionChanges()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var actor = ctx.Kernel.Actors.GetOrActivate(trackId);
        var overlay = Assert.Single((await ctx.Kernel.GetOverlaysAsync(ct: ct)).Tracks);
        var stored = new StoredTrackOverlay(overlay, new(Reply: "Stored reply"));

        var older = actor.AskUserAsync(snap.Track.LeadSessionId, [new("Choose A?", ["Yes", "No"])], ct);
        var first = await actor.MergeOverlayAsync(stored, ct);
        Assert.Equal(1, first.OpenAskCount);
        Assert.Equal((TrackOverlayPreviewKind.Ask, "Choose A?"), (first.PreviewKind, first.PreviewText));

        ctx.Time.Advance(TimeSpan.FromMilliseconds(1));
        var latest = actor.AskUserAsync(snap.Track.LeadSessionId,
            [new("  Choose\n\t B? " + new string('x', 400), ["Yes", "No"]), new("Second question?", [])], ct);
        var active = await actor.MergeOverlayAsync(stored, ct);
        Assert.Equal(2, active.OpenAskCount);
        Assert.Equal(TrackOverlayPreviewKind.Ask, active.PreviewKind);
        Assert.Equal("Choose B? " + new string('x', 270), active.PreviewText);

        var asks = (await actor.GetAggregateSnapshotAsync(ct)).OpenAsks;
        var lead = snap.Track.LeadSessionId!;
        foreach (var ask in asks)
            await ctx.Kernel.RespondInteractionAsync(lead, ask.Id,
                new(Answers: TestJson.FromObject(new { choice = "Yes" })), new InteropPrincipal.Human(), ct);
        await Task.WhenAll(older, latest);
        var staleAsk = new StoredTrackOverlay(active, new(Ask: active.PreviewText, Summary: "Report fallback"));
        var answered = Assert.Single((await ctx.Kernel.MergeOverlaysAsync([staleAsk], ct)).Tracks);
        Assert.Equal(0, answered.OpenAskCount);
        Assert.Equal((TrackOverlayPreviewKind.Summary, "Report fallback"), (answered.PreviewKind, answered.PreviewText));

        await ctx.Kernel.PatchTrackAsync(trackId, new(Closed: true), ct: ct);
        var closed = Assert.Single((await ctx.Kernel.MergeOverlaysAsync([staleAsk], ct)).Tracks);
        Assert.Equal((TrackOverlayPreviewKind.Summary, "Report fallback"), (closed.PreviewKind, closed.PreviewText));
        var closedWithReply = await actor.MergeOverlayAsync(stored, ct);
        Assert.Equal((TrackOverlayPreviewKind.Reply, "Stored reply"), (closedWithReply.PreviewKind, closedWithReply.PreviewText));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnsweredAskRevealsTheLiveBlockerInTheSameOverlayRead(bool errorOnTurn)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var lead = snap.Track.LeadSessionId!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var session = Assert.Single(await ctx.Kernel.ListSessionsAsync(trackId, new(), new InteropPrincipal.Human(), ct)).Session;
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(trackId);
        await ctx.Store.UpsertSessionAsync(session with
        {
            Status = "unknown",
            UnknownLastError = errorOnTurn ? null : "  Reconnect\n failed. ",
            UnknownTurnId = errorOnTurn ? "current-turn" : null
        }, null, ct);
        var actor = ctx.Kernel.Actors.GetOrActivate(trackId);
        var waiting = actor.AskUserAsync(lead, [new("Continue?", ["Yes", "No"])], ct);
        var stored = Assert.Single((await ctx.Kernel.GetOverlaysAsync(ct: ct)).Tracks);
        Assert.True(stored.HasBlockedSession);
        Assert.Equal(TrackOverlayPreviewKind.Ask, stored.PreviewKind);

        var ask = Assert.Single((await actor.GetAggregateSnapshotAsync(ct)).OpenAsks);
        await ctx.Kernel.RespondInteractionAsync(lead, ask.Id,
            new(Answers: TestJson.FromObject(new { choice = "Yes" })), new InteropPrincipal.Human(), ct);
        await waiting;

        var candidates = new StoredTrackOverlay(stored,
            new(Ask: stored.PreviewText, BlockedSessionError: "Reconnect failed.", Reply: "Previous reply"))
        {
            BlockedErrorSource = errorOnTurn ? (lead, "current-turn") : null
        };
        var answered = Assert.Single((await ctx.Kernel.MergeOverlaysAsync([candidates], ct)).Tracks);
        Assert.True(answered.HasBlockedSession);
        Assert.Equal(0, answered.OpenAskCount);
        Assert.Equal((TrackOverlayPreviewKind.Error, "Reconnect failed."), (answered.PreviewKind, answered.PreviewText));

        var refreshed = await actor.MergeOverlayAsync(candidates with
        {
            Overlay = answered with { PreviewText = "Previous blocker." },
            BlockedErrorSource = (lead, "previous-turn")
        }, ct);
        Assert.Equal(errorOnTurn ? (TrackOverlayPreviewKind.Reply, "Previous reply") : (TrackOverlayPreviewKind.Error, "Reconnect failed."),
            (refreshed.PreviewKind, refreshed.PreviewText));
    }

    [Theory]
    [InlineData(false, TrackOverlayPreviewKind.Reply, "Previous reply")]
    [InlineData(true, TrackOverlayPreviewKind.Summary, "Report summary")]
    public async Task WorkspaceRetryUsesStoredFallbackCandidates(bool blocked, TrackOverlayPreviewKind kind, string text)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var session = Assert.Single(snap.Sessions);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(trackId);
        if (blocked)
            await ctx.Store.UpsertSessionAsync(session with { Status = "unknown", UnknownTurnId = "current-turn" }, null, default);
        await ctx.Store.SaveWorkspacePreparationAsync(trackId,
            new(new WorkspaceProvisionRequest(trackId, "/tmp/source", "/tmp/target", []), null,
                WorkspacePreparationPhase.Failed, FailureDetail: "Workspace mount failed"), null, null, default);
        ctx.Kernel.Actors.GetOrActivate(trackId);
        var failed = Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks);
        Assert.Equal((TrackOverlayPreviewKind.Error, "Workspace mount failed"), (failed.PreviewKind, failed.PreviewText));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.DirectDriver.ProvisionGate = gate.Task;
        try
        {
            await ctx.Kernel.RetryWorkspaceAsync(trackId);
            var stored = new StoredTrackOverlay(failed, new(WorkspaceError: failed.PreviewText,
                Reply: blocked ? null : "Previous reply", Summary: "Report summary"));
            var retrying = Assert.Single((await ctx.Kernel.MergeOverlaysAsync([stored], default)).Tracks);
            Assert.True(retrying.IsWorking);
            Assert.False(retrying.WorkspaceFailed);
            Assert.Equal(blocked, retrying.HasBlockedSession);
            Assert.Equal((kind, text), (retrying.PreviewKind, retrying.PreviewText));
        }
        finally { gate.TrySetResult(); }
    }

    [Fact]
    public void PreviewNormalizationKeepsSurrogatePairsWholeAtTheUtf16Limit()
    {
        var fits = new string('x', TrackOverlayPreview.MaxLength - 2);
        Assert.Equal(fits + "🙂", TrackOverlayPreview.Normalize(fits + "🙂 more"));
        Assert.Equal(fits + "x", TrackOverlayPreview.Normalize(fits + "x🙂 more"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlayLookupConcurrentWithIdleEvictionUsesStoreOrNewActor(bool replace)
    {
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            ActorIdleTimeout = TimeSpan.FromMinutes(1), ActorEvictionSweepInterval = TimeSpan.Zero
        });
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var stored = Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks) with
        {
            NotifiableSessions = [new(snap.Track.LeadSessionId!, 123)]
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Awaiting an overlay result does not imply its mailbox finally or workspace cleanup has finished.
        await ctx.WaitForActorQuiescenceAsync(snap.Track.Id, deadline.Token);
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var lookups = 0;
        var merge = Task.Run(() => PergaKernelService.MergeActiveOverlayAsync(new(stored, new()), () =>
        {
            ctx.Kernel.Actors.TryGetActive(snap.Track.Id, out var actor);
            if (Interlocked.Increment(ref lookups) == 1)
            {
                selected.SetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return actor;
        }, default));
        try
        {
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleActorsAsync());
            if (replace) await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        }
        finally { release.Set(); }
        var overlay = await merge;
        Assert.Equal(2, lookups);
        if (replace) Assert.Empty(overlay.NotifiableSessions!); // The newly hydrated actor wins over the stale store input.
        else Assert.Equal(stored, overlay);
        Assert.Equal(snap.Track.Id, Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks).TrackId);
    }

    [Theory]
    [InlineData(WorkspacePreparationPhase.Ready, false, false)]
    [InlineData(WorkspacePreparationPhase.Pending, true, false)]
    [InlineData(WorkspacePreparationPhase.Provisioning, true, false)]
    [InlineData(WorkspacePreparationPhase.Mounting, true, false)]
    [InlineData(WorkspacePreparationPhase.Initializing, true, false)]
    [InlineData(WorkspacePreparationPhase.Provisioned, true, false)]
    [InlineData(WorkspacePreparationPhase.Failed, false, true)]
    [InlineData(WorkspacePreparationPhase.Unknown, false, true)]
    public async Task ActorWorkspaceAndClosedOverlayPreserveWatermarks(WorkspacePreparationPhase phase, bool working, bool failed)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.Id;
        // Keep the seeded pending preparation from finishing before the overlay read.
        if (phase == WorkspacePreparationPhase.Pending)
            ctx.DirectDriver.ProvisionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        var session = Assert.Single(await ctx.Kernel.ListSessionsAsync(id, new(), new InteropPrincipal.Human(), default)).Session with { LastNotifiableTurnAtMs = 123 };
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(id);
        await ctx.Store.UpsertSessionAsync(session, null, default);
        await ctx.Store.SaveWorkspacePreparationAsync(id,
            new(new WorkspaceProvisionRequest(id, "/tmp/source", "/tmp/target", []), null, phase), null, null, default);
        // Activate without starting preparation: overlay reads hydrate the saved phase themselves.
        ctx.Kernel.Actors.GetOrActivate(id);
        var overlay = Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks);
        Assert.Equal(working, overlay.IsWorking);
        Assert.Equal(failed, overlay.WorkspaceFailed);
        // An active actor is the single source of truth over contradictory stored flags.
        var active = await PergaKernelService.MergeActiveOverlayAsync(
            new(overlay with { IsWorking = !working, WorkspaceFailed = !failed, HasBlockedSession = true, OpenAskCount = 3 }, new()),
            () => ctx.Kernel.Actors.GetOrActivate(id), default);
        Assert.Equal(working, active.IsWorking);
        Assert.Equal(failed, active.WorkspaceFailed);
        Assert.False(active.HasBlockedSession);
        Assert.Equal(0, active.OpenAskCount);
        Assert.Equal(123, Assert.Single(overlay.NotifiableSessions!).LastNotifiableTurnAtMs);
        await ctx.Kernel.PatchTrackAsync(id, new(Closed: true));
        var closed = Assert.Single((await ctx.Kernel.GetOverlaysAsync(includeClosed: true)).Tracks);
        Assert.False(closed.IsWorking);
        Assert.False(closed.WorkspaceFailed);
        Assert.False(closed.HasBlockedSession);
        Assert.Equal(0, closed.OpenAskCount);
        Assert.Equal(0, closed.PendingApprovalCount);
        Assert.Equal(123, Assert.Single(closed.NotifiableSessions!).LastNotifiableTurnAtMs);
        Assert.Empty((await ctx.Kernel.GetOverlaysAsync()).Tracks);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("interrupted")]
    public async Task TerminalTurnsImmediatelyUpdateActorWatermarks(string status)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession agent, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new UnifiedAgentEvent.ThreadBound(agent.NativeThreadId!);
            if (status == "failed") yield return new UnifiedAgentEvent.TurnError("test", "test failure", false, IsConclusive: true);
            else if (status == "interrupted") yield return new UnifiedAgentEvent.TurnInterrupted();
            else yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        ctx.AgentRuntime.TurnScript = Stream;
        Assert.Null(Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(), new InteropPrincipal.Human(), default)).Session.LastNotifiableTurnAtMs);
        var turn = await ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("hello", "notification-test"));
        var session = Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(), new InteropPrincipal.Human(), default)).Session;
        Assert.Equal(status, turn.Status);
        if (status == "interrupted")
        {
            Assert.Null(session.LastNotifiableTurnAtMs);
            Assert.Empty(Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks).NotifiableSessions!);
            return;
        }
        Assert.NotNull(session.LastNotifiableTurnAtMs);
        Assert.Equal(turn.CompletedAtMs, session.LastNotifiableTurnAtMs);
        var snapshot = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        Assert.Equal(session.LastNotifiableTurnAtMs, Assert.Single(snapshot.Sessions).LastNotifiableTurnAtMs);
        var overlay = Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks);
        Assert.Equal(session.LastNotifiableTurnAtMs, Assert.Single(overlay.NotifiableSessions!).LastNotifiableTurnAtMs);
        await ctx.Kernel.UpdateSessionAsync(id, new UpdateSessionRequest(Title: "renamed"), new InteropPrincipal.Human(), default);
        Assert.Equal(session.LastNotifiableTurnAtMs, Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(), new InteropPrincipal.Human(), default)).Session.LastNotifiableTurnAtMs);
    }
}
