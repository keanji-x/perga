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
        var sampled = Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id));
        Assert.Equal(250, sampled.ContextUsedTokens);
        Assert.NotNull(sampled.ContextUsageAtMs);
        Assert.False(sampled.ContextUsageInvalidated);
        var switched = await ctx.Kernel.UpdateSessionModelAsync(id, new("next-model"));
        Assert.True(switched.ContextUsageInvalidated);
        Assert.Equal(250, switched.ContextUsedTokens);
        var native = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Contains("next-model", native.ModelSwitches);

        var compacted = await ctx.Kernel.CompactSessionAsync(id);
        Assert.Equal(1, native.CompactCalls);
        Assert.Equal("next-model", compacted.Model);
        Assert.Equal(0, compacted.ContextUsedTokens);
        Assert.Equal(0, compacted.ContextUsageRatio);
        Assert.Null(compacted.ContextUsageAtMs);
        Assert.False(compacted.ContextUsageInvalidated);
        Assert.Null(ctx.Store.GetTurnsForSession(id).Last().Usage);
        var persisted = Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id));
        Assert.Equal(compacted, persisted);
        Assert.Contains(ctx.Store.PersistedEvents, e => e.Kind == KernelEventKinds.SessionUpdated &&
            e.Payload.GetProperty("id").GetString() == id &&
            e.Payload.TryGetProperty("model", out var model) && model.GetString() == "next-model" &&
            e.Payload.GetProperty("contextUsedTokens").GetInt64() == 0 &&
            (!e.Payload.TryGetProperty("contextUsageAtMs", out var at) || at.ValueKind == System.Text.Json.JsonValueKind.Null));
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
            var live = (await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single(s => s.Id == id);
            Assert.Equal(250, live.ContextUsedTokens);
            Assert.Equal(1000, live.ContextWindowTokens);
            Assert.Equal(0.25, live.ContextUsageRatio);
            Assert.NotNull(live.ContextUsageAtMs);
            Assert.False(live.ContextUsageInvalidated);
            Assert.Contains(ctx.Store.PersistedEvents, e => e.Kind == KernelEventKinds.SessionUpdated &&
                e.Payload.GetProperty("id").GetString() == id &&
                e.Payload.TryGetProperty("contextUsedTokens", out var used) && used.GetInt64() == 250);
            if (changeDuringTurn)
                Assert.True((await ctx.Kernel.UpdateSessionModelAsync(id, new("next-model"))).ContextUsageInvalidated);
        }
        finally { release.TrySetResult(); }
        await turn;
        var completed = (await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single(s => s.Id == id);
        Assert.Equal(noFinalUsage ? 250 : 400, completed.ContextUsedTokens);
        Assert.Equal(noFinalUsage ? 0.25 : 0.4, completed.ContextUsageRatio);
        Assert.Equal(changeDuringTurn, completed.ContextUsageInvalidated);
        if (!changeDuringTurn)
            Assert.True((await ctx.Kernel.UpdateSessionModelAsync(id, new("next-model"))).ContextUsageInvalidated);
        Assert.Contains(ctx.Store.PersistedEvents, e => e.Kind == KernelEventKinds.SessionUpdated &&
            e.Payload.GetProperty("id").GetString() == id && e.Payload.GetProperty("contextUsageInvalidated").GetBoolean());

        // A new turn on the selected model validates the new measurement.
        await ctx.SendAndWaitForTurnAsync(id, new("next", "usage-next"));
        Assert.False((await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single(s => s.Id == id).ContextUsageInvalidated);
        var compacted = await ctx.Kernel.CompactSessionAsync(id);
        Assert.Equal(0, compacted.ContextUsedTokens);
        Assert.Equal(0, compacted.ContextUsageRatio);
        Assert.Null(compacted.ContextUsageAtMs);
        Assert.False(compacted.ContextUsageInvalidated);
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
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(500, 50, 0, 1_048_576, UsedTokens: 550));
        }

        await ctx.SendAndWaitForTurnAsync(id, new("hello", "delayed-compact-1"));

        var compacted = await ctx.Kernel.CompactSessionAsync(id);
        Assert.Equal(1, compacted.Epoch);
        Assert.Equal("thr_native_1", compacted.NativeThreadId);
        Assert.Equal(100, compacted.ContextUsedTokens);
        Assert.Equal(1_048_576, compacted.ContextWindowTokens);

        var session = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal(1, session.CompactCalls);
        var lastTurn = ctx.Store.GetTurnsForSession(id).Last();
        Assert.Equal("native_compact", lastTurn.Input.GetProperty("kind").GetString());
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
        var oldEpochCompacted = await ctx.Kernel.CompactSessionAsync(id);
        Assert.Equal(2, oldEpochCompacted.Epoch);
        Assert.Null(oldEpochCompacted.NativeThreadId);
        Assert.Null(oldEpochCompacted.ContextWindowTokens);
        Assert.Equal(0, oldEpochCompacted.ContextUsedTokens);

        var opened = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Equal(2, opened.Length);
        var newEpochSession = opened[1];

        // Stale idle usage from the old disposed Epoch 1 session must be ignored.
        originalSession.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(99_999, 0, 0, 512_000, UsedTokens: 99_999),
            oldEpochCompacted.Model,
            "thr_epoch_old"));

        // Valid idle usage on the new Epoch 2 session updates the window and context ring data.
        newEpochSession.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(0, 0, 0, 1_048_576, UsedTokens: 0),
            oldEpochCompacted.Model,
            newEpochSession.NativeThreadId));

        var deadline = DateTime.UtcNow.AddSeconds(3);
        AgentSessionDto latest;
        do
        {
            latest = (await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single(s => s.Id == id);
            if (latest.ContextWindowTokens == 1_048_576)
                break;
            await Task.Delay(15);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(2, latest.Epoch);
        Assert.Equal(0, latest.ContextUsedTokens);
        Assert.Equal(1_048_576, latest.ContextWindowTokens);
        Assert.Equal(0.0, latest.ContextUsageRatio);
        Assert.False(latest.ContextUsageInvalidated);
    }

    [Fact]
    public async Task IdleContextUsage_AppliesOnlyToMatchingSessionModelAndThread()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.Kernel.UpdateSessionModelAsync(id, new("model-a"));

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
            "model-a",
            "thr_other"));

        // Matching session, model, epoch, and threadId updates idle usage.
        opened.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(1200, 0, 0, 1_048_576, UsedTokens: 1200),
            "model-a",
            "thr_model_test"));

        var deadline = DateTime.UtcNow.AddSeconds(3);
        AgentSessionDto synced;
        do
        {
            synced = (await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single(s => s.Id == id);
            if (synced.ContextUsedTokens == 1200)
                break;
            await Task.Delay(15);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(1200, synced.ContextUsedTokens);
        Assert.Equal(1_048_576, synced.ContextWindowTokens);

        // Switch model to model-b; stale model-a reading must be rejected, model-b reading accepted.
        var switched = await ctx.Kernel.UpdateSessionModelAsync(id, new("model-b"));
        Assert.True(switched.ContextUsageInvalidated);

        opened.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(5000, 0, 0, 1_048_576, UsedTokens: 5000),
            "model-a",
            "thr_model_test"));
        await Task.Delay(40);
        var stillInvalidated = (await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single(s => s.Id == id);
        Assert.True(stillInvalidated.ContextUsageInvalidated);
        Assert.Equal(1200, stillInvalidated.ContextUsedTokens);

        opened.EmitIdleContextUsage(new IdleContextUsage(
            new TokenUsage(0, 0, 0, 200_000, UsedTokens: 0),
            "model-b",
            "thr_model_test"));

        deadline = DateTime.UtcNow.AddSeconds(3);
        AgentSessionDto updatedModelB;
        do
        {
            updatedModelB = (await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single(s => s.Id == id);
            if (!updatedModelB.ContextUsageInvalidated && updatedModelB.ContextWindowTokens == 200_000)
                break;
            await Task.Delay(15);
        } while (DateTime.UtcNow < deadline);

        Assert.False(updatedModelB.ContextUsageInvalidated);
        Assert.Equal(0, updatedModelB.ContextUsedTokens);
        Assert.Equal(200_000, updatedModelB.ContextWindowTokens);
    }
}
