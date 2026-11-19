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

        await ctx.Kernel.SendMessageAsync(id, new("sample", "before-switch"));
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
        var turn = ctx.Kernel.SendMessageAsync(id, new("go", "usage"));
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
        await ctx.Kernel.SendMessageAsync(id, new("next", "usage-next"));
        Assert.False((await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single(s => s.Id == id).ContextUsageInvalidated);
        var compacted = await ctx.Kernel.CompactSessionAsync(id);
        Assert.Equal(0, compacted.ContextUsedTokens);
        Assert.Equal(0, compacted.ContextUsageRatio);
        Assert.Null(compacted.ContextUsageAtMs);
        Assert.False(compacted.ContextUsageInvalidated);
    }
}
