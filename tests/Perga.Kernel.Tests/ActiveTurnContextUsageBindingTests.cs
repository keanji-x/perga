using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Kernel.Events;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class ActiveTurnContextUsageBindingTests
{
    [Theory]
    [InlineData(false, "streaming")]
    [InlineData(true, "streaming")]
    [InlineData(false, "final")]
    [InlineData(true, "final")]
    [InlineData(false, "reuse")]
    [InlineData(true, "reuse")]
    public async Task InvalidatedActiveBindingRejectsUsageAndAutoCompaction(bool loseConfirmation, string usagePath)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var initialTokens = usagePath == "reuse" ? 900 : 100;
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = Stream;

        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            session.SupportsNativeCompact = true;
            yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(initialTokens, 0, 0, 1000));
            sampled.TrySetResult();
            await release.Task.WaitAsync(ct);
            if (usagePath == "streaming")
                yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(950, 0, 0, 1000));
            // Enumeration resumes after the streaming event's mailbox operation has finished.
            streamed.TrySetResult();
            await finish.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnCompleted(
                usagePath == "final" ? new TokenUsage(950, 0, 0, 1000) : null);
        }

        var turn = ctx.SendAndWaitForTurnAsync(id, new("sample", "invalidated-context"));
        try
        {
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var before = Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id));
            Assert.Equal(initialTokens, before.ContextUsage?.UsedTokens);
            if (loseConfirmation)
                Assert.Single(ctx.AgentRuntime.OpenedSessions).ConfigurationState = null;
            else
                await ctx.Kernel.SetSessionPermissionModeAsync(id, "full");
            release.TrySetResult();
            await streamed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var live = Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id));
            Assert.Equal(before.ContextUsage, live.ContextUsage);
        }
        finally
        {
            release.TrySetResult();
            finish.TrySetResult();
            await turn;
        }

        var completed = Assert.Single(await ctx.Kernel.ListSessionsAsync(snap.Track.Id));
        var durable = Assert.Single((await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!.Sessions);
        Assert.DoesNotContain(ctx.Store.PersistedEvents, e => e.Kind == KernelEventKinds.SessionUpdated &&
            e.Payload.GetProperty("id").GetString() == id &&
            e.Payload.TryGetProperty("contextUsage", out var usage) &&
            usage.ValueKind == System.Text.Json.JsonValueKind.Object && usage.GetProperty("usedTokens").GetInt64() == 950);
        Assert.Equal(0, ctx.AgentRuntime.OpenedSessions.Sum(s => s.CompactCalls));
        Assert.Equal(initialTokens, completed.ContextUsage?.UsedTokens);
        Assert.Equal(completed.ContextUsage, durable.ContextUsage);
        Assert.Equal(1, completed.Epoch);
        Assert.Single(ctx.Store.GetTurnsForSession(id));
    }
}
