using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionContextUsageTests
{
    [Fact]
    public async Task NativeCompactWithoutUsageAfterModelSwitchResetsOldMeasurement()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            session.SupportsModelSwitch = true;
            session.SupportsNativeCompact = true;
            yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(10, 5, 0, 1000, UsedTokens: 250));
            await Task.Yield();
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }

        await ctx.SendAndWaitForTurnAsync(id, new("sample", "before-switch"));
        var sampled = Assert.Single((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());
        Assert.Equal(250, sampled.ContextUsage?.UsedTokens);
        Assert.NotNull(sampled.ContextUsage?.ObservedAtMs);

        var switched = await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("next-model")), new InteropPrincipal.Human(), default);
        Assert.Null(switched.ContextUsage);
        var native = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Empty(native.ModelSwitches);

        var compacted = await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
        Assert.Equal(1, native.CompactCalls);
        Assert.Equal("next-model", compacted.Model);
        Assert.Null(compacted.ContextUsage);

        Assert.Null(ctx.Store.GetTurnsForSession(id).Last().Usage);
        var persisted = Assert.Single((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());
        Assert.Equal(compacted, persisted);
        Assert.Contains(ctx.Store.PersistedEvents, e => e.Kind == KernelEventKinds.SessionUpdated &&
            e.Payload.GetProperty("id").GetString() == id &&
            e.Payload.TryGetProperty("model", out var model) && model.GetString() == "next-model" &&
            (!e.Payload.TryGetProperty("contextUsage", out var usage) || usage.ValueKind == System.Text.Json.JsonValueKind.Null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StreamingUsagePersistsAndBroadcastsBeforeCompletionAndModelChangesInvalidate(bool changeDuringTurn, bool noFinalUsage)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(10, 5, 0, 1000, UsedTokens: 250));
            sampled.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnCompleted(noFinalUsage ? null : new TokenUsage(10, 5, 0, 1000, UsedTokens: 400));
        }
        var turn = ctx.SendAndWaitForTurnAsync(id, new("go", "usage"));
        try
        {
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var live = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
            Assert.Equal(250, live.ContextUsage?.UsedTokens);
            Assert.Equal(1000, live.ContextUsage?.WindowTokens);
            Assert.Equal(0.25, live.ContextUsage?.Ratio);
            Assert.NotNull(live.ContextUsage?.ObservedAtMs);

            Assert.Contains(ctx.Store.PersistedEvents, e => e.Kind == KernelEventKinds.SessionUpdated &&
                e.Payload.GetProperty("id").GetString() == id &&
                e.Payload.TryGetProperty("contextUsage", out var usage) && usage.ValueKind == System.Text.Json.JsonValueKind.Object && usage.GetProperty("usedTokens").GetInt64() == 250);
            if (changeDuringTurn)
                Assert.Null((await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("next-model")), new InteropPrincipal.Human(), default)).ContextUsage);
        }
        finally { release.TrySetResult(); }
        await turn;
        var completed = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
        if (changeDuringTurn)
            Assert.Null(completed.ContextUsage);
        else
        {
            Assert.Equal(noFinalUsage ? 250 : 400, completed.ContextUsage?.UsedTokens);
            Assert.Equal(noFinalUsage ? 0.25 : 0.4, completed.ContextUsage?.Ratio);
        }
        if (!changeDuringTurn)
            Assert.Null((await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("next-model")), new InteropPrincipal.Human(), default)).ContextUsage);
        Assert.Contains(ctx.Store.PersistedEvents, e => e.Kind == KernelEventKinds.SessionUpdated &&
            e.Payload.GetProperty("id").GetString() == id &&
            (!e.Payload.TryGetProperty("contextUsage", out var usage) || usage.ValueKind == System.Text.Json.JsonValueKind.Null));

        // A new turn on the selected model validates the new measurement.
        await ctx.SendAndWaitForTurnAsync(id, new("next", "usage-next"));
        Assert.NotNull(((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id).ContextUsage);
        var compacted = await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
        Assert.Null(compacted.ContextUsage);

    }

    [Fact]
    public async Task CompactSession_WaitsForDelayedNativeCompactCapabilityAndKeepsEpochAndThread()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            session.ResolveNativeCompactSupportHandler = async (s, token) =>
            {
                await Task.Delay(30, token);
                s.SupportsNativeCompact = true;
                return true;
            };
            session.CompactHandler = (_, _) => Task.FromResult(
                new NativeCompactResult(new TokenUsage(100, 0, 0, 1_048_576, UsedTokens: 100), "Native compacted."));
            yield return new UnifiedAgentEvent.ThreadBound("thr_native_1");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(500, 50, 0,
                input.Prompt == "after compact" ? null : 1000, UsedTokens: 550));
        }

        await ctx.SendAndWaitForTurnAsync(id, new("hello", "delayed-compact-1"));

        var compacted = await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
        Assert.Equal(1, compacted.Epoch);
        Assert.Equal("thr_native_1", compacted.NativeThreadId);
        Assert.Equal(100, compacted.ContextUsage?.UsedTokens);
        Assert.Equal(1_048_576, compacted.ContextUsage?.WindowTokens);

        await ctx.SendAndWaitForTurnAsync(id, new("after compact", "compact-window-follow-up"));
        var after = Assert.Single((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());
        Assert.Equal(550, after.ContextUsage?.UsedTokens);
        Assert.Equal(1_048_576, after.ContextUsage?.WindowTokens);

        var session = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal(1, session.CompactCalls);
        var lastTurn = ctx.Store.GetTurnsForSession(id)[^2];
        Assert.Equal("native_compact", lastTurn.Input.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task TrailingUsageWinsOnlyForThePromptBeingSettled()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        ctx.AgentRuntime.TurnScript = Stream;

        await ctx.SendAndWaitForTurnAsync(id, new("first", "trailing-1"));
        var initial = Assert.Single((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());
        Assert.Equal(220, initial.ContextUsage?.UsedTokens);
        Assert.Equal(2000, initial.ContextUsage?.WindowTokens);

        await ctx.SendAndWaitForTurnAsync(id, new("second", "trailing-2"));
        var latest = Assert.Single((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());
        Assert.Equal(100, latest.ContextUsage?.UsedTokens);
        Assert.Equal(1000, latest.ContextUsage?.WindowTokens);

        static async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            // The actor receives both notifications before it settles the terminal event.
            session.EmitIdleContextUsage(new IdleContextUsage(new TokenUsage(200, 0, 0, 2000), 0, session.NativeThreadId, CompletedPromptId: 1));
            session.EmitIdleContextUsage(new IdleContextUsage(new TokenUsage(220, 0, 0, null), 0, session.NativeThreadId, CompletedPromptId: 1));
            await Task.Yield();
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(100, 0, 0, 1000),
                CompletedPromptId: input.Prompt == "first" ? 1 : 2);
        }
    }

    [Fact]
    public async Task CompactSession_WithoutNativeCompact_PreservesHandoverClearsOldWindowAndAppliesNewEpochIdleUsage()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;

        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            session.SupportsNativeCompact = false;
            yield return new UnifiedAgentEvent.ThreadBound("thr_epoch_old");
            yield return new UnifiedAgentEvent.TextDelta("HANDOVER_SUMMARY");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(90_000, 10_000, 0, 512_000, UsedTokens: 100_000));
        }

        await ctx.SendAndWaitForTurnAsync(id, new("hello", "handover-idle-1"));

        var originalSession = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var oldEpochCompacted = await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
        Assert.Equal(2, oldEpochCompacted.Epoch);
        Assert.Null(oldEpochCompacted.NativeThreadId);
        Assert.Null(oldEpochCompacted.ContextUsage);

        var opened = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Equal(2, opened.Length);
        var newEpochSession = opened[1];

        // Stale idle usage from the old disposed Epoch 1 session must be ignored.
        originalSession.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(99_999, 0, 0, 512_000, UsedTokens: 99_999),
            0,
            "thr_epoch_old"));

        // Valid idle usage on the new Epoch 2 session updates the window and context ring data.
        newEpochSession.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(0, 0, 0, 1_048_576, UsedTokens: 0),
            0,
            newEpochSession.NativeThreadId));

        var deadline = DateTime.UtcNow.AddSeconds(3);
        AgentSessionDto latest;
        do
        {
            latest = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
            if (latest.ContextUsage?.WindowTokens == 1_048_576)
                break;
            await Task.Delay(15);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(2, latest.Epoch);
        Assert.Equal(0, latest.ContextUsage?.UsedTokens);
        Assert.Equal(1_048_576, latest.ContextUsage?.WindowTokens);
        Assert.Equal(0.0, latest.ContextUsage?.Ratio);

    }

    [Fact]
    public async Task IdleContextUsage_AppliesOnlyToMatchingSessionModelAndThread()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("model-a")), new InteropPrincipal.Human(), default);

        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            session.SupportsModelSwitch = true;
            yield return new UnifiedAgentEvent.ThreadBound("thr_model_test");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(100, 10, 0, 1_000_000, UsedTokens: 110));
        }

        await ctx.SendAndWaitForTurnAsync(id, new("turn-1", "idle-model-1"));
        var opened = Assert.Single(ctx.AgentRuntime.OpenedSessions);

        // Wrong threadId is ignored.
        opened.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(777, 0, 0, 500_000, UsedTokens: 777),
            0,
            "thr_other"));

        // Matching session, model, epoch, and threadId updates idle usage.
        opened.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(1200, 0, 0, 1_048_576, UsedTokens: 1200),
            0,
            "thr_model_test"));

        var deadline = DateTime.UtcNow.AddSeconds(3);
        AgentSessionDto synced;
        do
        {
            synced = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
            if (synced.ContextUsage?.UsedTokens == 1200)
                break;
            await Task.Delay(15);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(1200, synced.ContextUsage?.UsedTokens);
        Assert.Equal(1_048_576, synced.ContextUsage?.WindowTokens);

        // Lazy model change invalidates old observations until the next turn binds model-b.
        var switched = await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("model-b")), new InteropPrincipal.Human(), default);
        Assert.Null(switched.ContextUsage);

        opened.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(5000, 0, 0, 1_048_576, UsedTokens: 5000),
            -1,
            "thr_model_test"));
        await Task.Delay(40);
        var stillInvalidated = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
        Assert.Null(stillInvalidated.ContextUsage);

        await ctx.SendAndWaitForTurnAsync(id, new("bind model-b", "bind-model-b"));
        opened.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(0, 0, 0, 200_000, UsedTokens: 0),
            0,
            "thr_model_test"));

        deadline = DateTime.UtcNow.AddSeconds(3);
        AgentSessionDto updatedModelB;
        do
        {
            updatedModelB = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
            if (updatedModelB.ContextUsage?.WindowTokens == 200_000)
                break;
            await Task.Delay(15);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(0, updatedModelB.ContextUsage?.UsedTokens);
        Assert.Equal(200_000, updatedModelB.ContextUsage?.WindowTokens);
    }

    [Fact]
    public async Task ApplyContextUsage_PreservesExistingWindowWhenSubsequentTurnOmitsSizeOnSameModel()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("model-a")), new InteropPrincipal.Human(), default);

        var turnIndex = 0;
        var turn3Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTurn3 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            turnIndex++;
            if (turnIndex == 1)
            {
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(100, 10, 0, 1_000_000, UsedTokens: 110));
            }
            else if (turnIndex == 2)
            {
                // Same model, no window reported on turn 2: preserve existing window.
                yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(200, 20, 0, null, UsedTokens: 220));
                await Task.Yield();
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(200, 20, 0, null, UsedTokens: 220));
            }
            else
            {
                // After model switch (invalidated), null window must not inherit old model's window.
                turn3Started.TrySetResult();
                await releaseTurn3.Task.WaitAsync(ct);
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(50, 5, 0, null, UsedTokens: 55));
            }
        }

        await ctx.SendAndWaitForTurnAsync(id, new("t1", "same-model-1"));
        var after1 = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
        Assert.Equal(1_000_000, after1.ContextUsage?.WindowTokens);

        await ctx.SendAndWaitForTurnAsync(id, new("t2", "same-model-2"));
        var after2 = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
        Assert.Equal(220, after2.ContextUsage?.UsedTokens);
        Assert.Equal(1_000_000, after2.ContextUsage?.WindowTokens);
        Assert.Equal(220d / 1_000_000d, after2.ContextUsage!.Ratio!.Value, precision: 6);


        // Switch model while turn 3 executes on old model (or without window); invalidated clears old window.
        var turn3Task = ctx.SendAndWaitForTurnAsync(id, new("t3", "switch-model-3"));
        try
        {
            await turn3Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("model-b")), new InteropPrincipal.Human(), default);
        }
        finally
        {
            releaseTurn3.TrySetResult();
        }
        await turn3Task;
        var after3 = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
        Assert.Null(after3.ContextUsage);
        Assert.Null(after3.ContextUsage?.WindowTokens);
    }

    [Fact]
    public async Task ResumeProviderSession_SucceedsAndKeepsSessionOpenWhenPendingIdleUsagePersistenceFails()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;

        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCount = 0;
        ctx.AgentRuntime.OnOpenedSession = session =>
        {
            openCount++;
            session.SupportsSessionResume = true;
            session.SupportsScopedClose = true;
            if (openCount == 1)
            {
                session.OnCancelTurn = () => releaseOld.TrySetResult();
            }
            if (openCount == 2)
            {
                session.PendingIdleUsage = new IdleContextUsage(
                    new TokenUsage(123, 0, 0, 1_048_576, UsedTokens: 123),
                    0,
                    session.NativeThreadId);
            }
        };

        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (openCount == 1)
            {
                yield return new UnifiedAgentEvent.ThreadBound("thr_resume_fault");
                oldStarted.TrySetResult();
                await releaseOld.Task.WaitAsync(ct);
                yield return new UnifiedAgentEvent.TurnError("stream_lost", "Disconnected", false);
                yield break;
            }

            yield return new UnifiedAgentEvent.TextDelta("after-resume");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(10, 5, 0, 1_048_576, UsedTokens: 15));
        }

        var oldTurnTask = ctx.SendAndWaitForTurnAsync(id, new("stuck", "resume-fault-1"));
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseOld.TrySetResult();
        await oldTurnTask;
        var items = await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        var targetTurnId = items.LastTurn!.Id;

        var throwOnPendingUsage = false;
        var pendingWriteAttempts = 0;
        ctx.Store.OnBeforeUpsertSession = session =>
        {
            if (throwOnPendingUsage && session.ContextUsage?.UsedTokens == 123)
            {
                pendingWriteAttempts++;
                throw new IOException("Simulated store failure during optional pending idle usage persistence.");
            }
        };

        // Allow pending idle usage write to fail after CompleteSessionResumeAsync commits.
        throwOnPendingUsage = true;
        var resumed = await ctx.Kernel.ControlSessionAsync(id, new("reconnect"), new InteropPrincipal.Human(), CancellationToken.None);
        await oldTurnTask;

        Assert.Equal(1, pendingWriteAttempts);
        Assert.Equal("idle", resumed.Status);
        Assert.Equal("thr_resume_fault", resumed.NativeThreadId);
        Assert.Null(resumed.ContextUsage?.UsedTokens);

        var opened = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Equal(2, opened.Length);
        var replacement = opened[1];
        Assert.False(replacement.Disposed);

        // Subsequent turn reuses the live replacement session without reopening.
        throwOnPendingUsage = false;
        var nextTurn = await ctx.SendAndWaitForTurnAsync(id, new("continue", "resume-fault-2"));
        Assert.Equal("completed", nextTurn.Status);
        Assert.Equal(2, ctx.AgentRuntime.OpenedSessions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterModelSwitch_FirstTurnWithoutWindow_ClearsStaleWindowAndDoesNotAutoCompact(bool effortOnly)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("model-a"), ReasoningEffort: new("low")), new InteropPrincipal.Human(), default);

        var turnIndex = 0;
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            session.SupportsModelSwitch = true;
            session.SupportsNativeCompact = true;
            turnIndex++;
            if (turnIndex == 1)
            {
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(100, 50, 0, 1000, UsedTokens: 150));
            }
            else
            {
                // Model B reports only used tokens without a window size.
                yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(850, 50, 0, null, UsedTokens: 900));
                await Task.Yield();
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(850, 50, 0, null, UsedTokens: 900));
            }
        }

        await ctx.SendAndWaitForTurnAsync(id, new("turn on model A", "switch-no-win-1"));
        var afterA = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
        Assert.Equal(1000, afterA.ContextUsage?.WindowTokens);
        Assert.Equal(0.15, afterA.ContextUsage!.Ratio!.Value, precision: 6);


        var switched = await ctx.Kernel.UpdateSessionAsync(id, new(Model: new(effortOnly ? "model-a" : "model-b"), ReasoningEffort: new("high")), new InteropPrincipal.Human(), default);
        Assert.Null(switched.ContextUsage);

        await ctx.SendAndWaitForTurnAsync(id, new("turn on model B", "switch-no-win-2"));
        var afterB = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
        var opened = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal(effortOnly ? "model-a" : "model-b", afterB.Model);
        Assert.Equal("high", afterB.ReasoningEffort);
        Assert.Equal(900, afterB.ContextUsage?.UsedTokens);
        Assert.Null(afterB.ContextUsage?.WindowTokens);
        Assert.Null(afterB.ContextUsage?.Ratio);

        Assert.Equal(1, afterB.Epoch);
        Assert.Equal(0, opened.CompactCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterModelSwitch_NativeCompactWithoutWindow_ClearsStaleWindow(bool reportsUsedWithoutWindow)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("model-a")), new InteropPrincipal.Human(), default);

        var turnIndex = 0;
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            session.SupportsModelSwitch = true;
            session.SupportsNativeCompact = true;
            session.CompactHandler = (_, _) => Task.FromResult(new NativeCompactResult(
                reportsUsedWithoutWindow ? new TokenUsage(100, 0, 0, null, UsedTokens: 100) : null,
                "Compacted."));
            turnIndex++;
            if (turnIndex == 1)
            {
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(100, 50, 0, 1000, UsedTokens: 150));
            }
            else
            {
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(850, 50, 0, null, UsedTokens: 900));
            }
        }

        await ctx.SendAndWaitForTurnAsync(id, new("turn on model A", "compact-switch-1"));
        var switched = await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("model-b")), new InteropPrincipal.Human(), default);
        Assert.Null(switched.ContextUsage);

        var compacted = await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
        Assert.Null(compacted.ContextUsage?.WindowTokens);


        await ctx.SendAndWaitForTurnAsync(id, new("turn on model B", "compact-switch-2"));
        var afterB = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id);
        var opened = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal(900, afterB.ContextUsage?.UsedTokens);
        Assert.Null(afterB.ContextUsage?.WindowTokens);
        Assert.Null(afterB.ContextUsage?.Ratio);
        Assert.Equal(1, opened.CompactCalls);
    }
}
