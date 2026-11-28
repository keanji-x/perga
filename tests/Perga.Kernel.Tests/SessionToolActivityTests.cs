using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionToolActivityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedToolActivityRetainsItsMetadata(bool completed)
    {
        var prefix = (completed ? "Tool details: " : "{\"path\":\"Tool details: ").PadRight(2047, '界');
        var oversized = prefix + "🙂" + new string('界', 4096);
        var args = TestJson.Parse(completed ? """{"path":"README.md"}""" : oversized + "\"}");
        var output = completed ? oversized : "Failed to read file";
        var (events, published, items) = await ReadToolActivityAsync(args, output);
        var payload = events[completed ? 1 : 0].Payload;
        var phase = completed ? "completed" : "started";
        var field = completed ? "output" : "args";

        Assert.Equal("call-1", payload.GetProperty("callId").GetString());
        Assert.Equal("read_file", payload.GetProperty("name").GetString());
        Assert.Equal(phase, payload.GetProperty("phase").GetString());
        if (completed)
            Assert.True(payload.GetProperty("isError").GetBoolean());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        var preview = payload.GetProperty(field).GetString()!;
        Assert.StartsWith(prefix, preview);
        Assert.EndsWith("…", preview);
        Assert.InRange(preview.Length, 1, 2049);
        Assert.DoesNotContain(Rune.ReplacementChar, preview.EnumerateRunes());

        var transient = Assert.Single(published, item => item.Payload.GetProperty("phase").GetString() == phase);
        Assert.Null(transient.EventId);
        Assert.True(transient.Payload.GetProperty("truncated").GetBoolean());
        Assert.Equal(preview, transient.Payload.GetProperty(field).GetString());

        var call = Assert.Single(items, item => item.Kind == "tool_call");
        var result = Assert.Single(items, item => item.Kind == "tool_result");
        Assert.True(JsonElement.DeepEquals(args, call.Payload.GetProperty("args")));
        Assert.Equal(output, result.Payload.GetProperty("output").GetString());
    }

    [Fact]
    public async Task ToolActivityAtTheCharacterLimitKeepsItsFullData()
    {
        var path = new string('界', 2048 - """{"path":""}""".Length);
        var args = TestJson.Parse($$"""{"path":"{{path}}"}""");
        var output = new string('界', 2048);
        var (events, _, _) = await ReadToolActivityAsync(args, output);

        Assert.Equal(path, events[0].Payload.GetProperty("args").GetProperty("path").GetString());
        Assert.Equal(output, events[1].Payload.GetProperty("output").GetString());
        Assert.All(events, item => Assert.False(item.Payload.TryGetProperty("truncated", out _)));
    }

    [Fact]
    public async Task CompletionIdentifiesTheToolAfterItsStartLeavesTheActivityBuffer()
    {
        var (events, _, _) = await ReadToolActivityAsync(TestJson.Parse("{}"), "Failed to read file", evictStart: true);
        var payload = Assert.Single(events).Payload;

        Assert.Equal("completed", payload.GetProperty("phase").GetString());
        Assert.Equal("call-1", payload.GetProperty("callId").GetString());
        Assert.Equal("read_file", payload.GetProperty("name").GetString());
        Assert.True(payload.GetProperty("isError").GetBoolean());
        Assert.Equal("Failed to read file", payload.GetProperty("output").GetString());
    }

    private static async Task<(WorkerActivityEvent[] Events, List<EventEnvelopeDto> Published, IReadOnlyList<SessionItemDto> Items)>
        ReadToolActivityAsync(JsonElement args, string output, bool evictStart = false)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        using var subscription = ctx.Kernel.EventBus.Subscribe(snapshot.Track.Id);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => StreamAsync(ct);
        var running = ctx.SendAndWaitForTurnAsync(sessionId, new SendSessionMessageRequest("Read a file", "tool-activity"));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
            Assert.NotNull(page.Activity);
            var events = page.Activity.Events.Where(item => item.Kind == KernelEventKinds.SessionToolProgress).ToArray();
            Assert.Equal(evictStart ? 1 : 2, events.Length);
            Assert.All(events, item =>
            {
                Assert.Equal(page.ActiveTurn!.Id, item.Payload.GetProperty("turnId").GetString());
                Assert.Equal(sessionId, item.Payload.GetProperty("sessionId").GetString());
            });
            var published = new List<EventEnvelopeDto>();
            while (subscription.Reader.TryRead(out var item))
                if (item.Kind == KernelEventKinds.SessionToolProgress)
                    published.Add(item);
            return (events, published, page.Items);
        }
        finally
        {
            finish.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }

        async IAsyncEnumerable<UnifiedAgentEvent> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.ToolCallStarted("call-1", "read_file", args);
            if (evictStart)
                for (var i = 0; i < 128; i++)
                    yield return new UnifiedAgentEvent.TextDelta("working");
            yield return new UnifiedAgentEvent.ToolCallCompleted("call-1", output, IsError: true);
            ready.SetResult();
            await finish.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
    }
}
