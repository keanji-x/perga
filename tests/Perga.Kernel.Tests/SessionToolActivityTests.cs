using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Perga.AgentKit;
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
        var text = "Tool details: " + string.Concat(Enumerable.Repeat("界🙂\\\n", 1000));
        var args = JsonSerializer.SerializeToElement(new { path = completed ? "README.md" : text });
        var events = await ReadToolActivityAsync(args, completed ? text : "Failed to read file");
        var payload = events[completed ? 1 : 0].Payload;

        Assert.Equal("call-1", payload.GetProperty("callId").GetString());
        Assert.Equal("read_file", payload.GetProperty("name").GetString());
        Assert.Equal(completed ? "completed" : "started", payload.GetProperty("phase").GetString());
        if (completed)
            Assert.True(payload.GetProperty("isError").GetBoolean());
        Assert.True(payload.GetProperty("truncated").GetBoolean());
        var preview = payload.GetProperty(completed ? "output" : "args").GetString()!;
        Assert.StartsWith(completed ? "Tool details: " : """{"path":"Tool details: """, preview);
        Assert.InRange(Encoding.UTF8.GetByteCount(payload.GetRawText()), 1, 4096);
    }

    [Fact]
    public async Task CompletionIdentifiesTheToolAfterItsStartLeavesTheActivityBuffer()
    {
        var events = await ReadToolActivityAsync(TestJson.Parse("{}"), "Failed to read file", evictStart: true);
        var payload = Assert.Single(events).Payload;

        Assert.Equal("completed", payload.GetProperty("phase").GetString());
        Assert.Equal("call-1", payload.GetProperty("callId").GetString());
        Assert.Equal("read_file", payload.GetProperty("name").GetString());
        Assert.True(payload.GetProperty("isError").GetBoolean());
        Assert.Equal("Failed to read file", payload.GetProperty("output").GetString());
    }

    private static async Task<WorkerActivityEvent[]> ReadToolActivityAsync(JsonElement args, string output, bool evictStart = false)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => StreamAsync(ct);
        var running = ctx.SendAndWaitForTurnAsync(sessionId, new SendSessionMessageRequest("Read a file", "tool-activity"));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(Limit: 1), new InteropPrincipal.Human(), default);
            Assert.NotNull(page.Activity);
            var events = page.Activity.Events.Where(item => item.Kind == KernelEventKinds.SessionToolProgress).ToArray();
            Assert.Equal(evictStart ? 1 : 2, events.Length);
            Assert.All(events, item =>
            {
                Assert.Equal(page.ActiveTurn!.Id, item.Payload.GetProperty("turnId").GetString());
                Assert.Equal(sessionId, item.Payload.GetProperty("sessionId").GetString());
            });
            return events;
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
