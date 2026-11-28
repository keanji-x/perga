using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.MemoryKit;

namespace Perga.Host.IntegrationTests;

public sealed partial class MemoryIntegrationTests
{
    [Fact]
    public async Task QueuedImageBatchHydratesMemoryOnlyInTheTurnThatContainsTheMention()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster(configureAgentRuntime: runtime => runtime.SupportsImages = true);
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var engine = host.Services.GetRequiredService<IMemoryEngine>();
        const string memoryBody = "Only the second image turn needs this memory body.";
        await engine.PutAsync(new(MemoryScopeKind.Global), new("secret", "Secret", "A global note", memoryBody), ct);

        const string holdPrompt = "Hold the turn for the image queue";
        const string firstContent = "Review these images";
        const string secondContent = "Review this image with @`memory:global/secret`";
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeTurn = async (input, token) =>
        {
            if (input.Prompt.EndsWith(holdPrompt, StringComparison.Ordinal))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };

        try
        {
            await kernel.SendSessionMessageAsync(sessionId, new(holdPrompt, "hold-images"), Owner, ct);
            await started.Task.WaitAsync(ct);
            var first = await kernel.SendSessionMessageAsync(sessionId, new(firstContent, "first-images", Mode: "queue")
            {
                Images = Enumerable.Repeat(png, ImageAttachmentContract.MaxAttachmentsPerMessage).ToArray()
            }, Owner, ct);
            var second = await kernel.SendSessionMessageAsync(sessionId, new(secondContent, "second-image", Mode: "queue")
            {
                Images = [png]
            }, Owner, ct);
            Assert.Null(first.TurnId);
            Assert.Null(second.TurnId);
        }
        finally
        {
            release.TrySetResult();
        }

        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var imageTurns = host.AgentRuntime.ExecutedTurns.Select(turn => turn.Input)
            .Where(input => input.Attachments is { Count: > 0 });
        Assert.Collection(imageTurns, first =>
        {
            Assert.Equal(ImageAttachmentContract.MaxAttachmentsPerMessage, first.Attachments!.Count);
            Assert.Equal(firstContent, first.Prompt);
        }, second =>
        {
            Assert.Single(second.Attachments!);
            Assert.StartsWith("<explicit_memory>\n", second.Prompt, StringComparison.Ordinal);
            Assert.Contains(memoryBody, second.Prompt);
            Assert.EndsWith(secondContent, second.Prompt, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrenderableEntriesNeverClearPreviouslyDeliveredMemory(bool invalidUtf8)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var engine = host.Services.GetRequiredService<IMemoryEngine>();
        var provider = host.Services.GetRequiredService<IMemoryContextProvider>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await engine.PutAsync(new(MemoryScopeKind.Global), new("rule", "Rule", "Rule summary", "Original rule", Pinned: true), ct);
        await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Load memory", ct);
        var path = Path.Combine(host.DataDir, "memory", "global", "rule.md");
        if (invalidUtf8)
            await File.WriteAllBytesAsync(path, new byte[] { 0xc3, 0x28 }, ct);
        else
            await File.WriteAllTextAsync(path, "---\npinned: true\n---\n" + new string('x', 3000), ct);

        var snapshot = await engine.GetContextSnapshotAsync(null, new(), ct);
        Assert.Equal(1, snapshot.SourceEntryCount);
        Assert.Equal(invalidUtf8 ? 1 : 0, snapshot.UnreadableEntryCount);
        Assert.Null(await provider.GetContextSnapshotAsync(null, ct));
        var unavailable = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Memory unavailable", ct);
        Assert.Equal("Memory unavailable", unavailable.Input.Prompt);
        Assert.Equal(["loaded"], await MemoryNoticeModesAsync(kernel, sessionId, ct));

        File.Delete(path);
        var cleared = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Memory removed", ct);
        Assert.StartsWith(ClearedContext, cleared.Input.Prompt, StringComparison.Ordinal);
        Assert.Equal(["loaded", "cleared"], await MemoryNoticeModesAsync(kernel, sessionId, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartRecoversAcknowledgedMemoryBeforeTurnSettlement(bool alreadyClearing)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        var directory = Directory.CreateTempSubdirectory("perga-memory-ack-restart-");
        try
        {
            var crashDb = Path.Combine(directory.FullName, "at-ack.db");
            string trackId;
            string sessionId;
            string nativeThreadId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                (trackId, sessionId) = await host.CreateTrackAsync();
                var engine = host.Services.GetRequiredService<IMemoryEngine>();
                var scope = new MemoryWriteScope(MemoryScopeKind.Global);
                var entry = await engine.PutAsync(scope, new("rule", "Rule", "Rule summary", "Stored rule", Pinned: true), ct);
                if (alreadyClearing)
                {
                    await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "First load", ct);
                    await engine.DeleteAsync(scope, "rule", entry.Meta.Etag, ct);
                }

                var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.AgentRuntime.SetTurnHandler((spec, _, token) => PauseAfterAcknowledgment(spec, token));
                async IAsyncEnumerable<UnifiedAgentEvent> PauseAfterAcknowledgment(HarnessLaunchSpec spec,
                    [EnumeratorCancellation] CancellationToken token)
                {
                    yield return new UnifiedAgentEvent.ThreadBound(spec.ResumeNativeThreadId ?? "memory-ack-thread");
                    yield return new UnifiedAgentEvent.TurnAccepted("memory-ack-turn");
                    acknowledged.TrySetResult();
                    await release.Task.WaitAsync(token);
                    yield return new UnifiedAgentEvent.TurnCompleted(null);
                }

                try
                {
                    var receipt = await kernel.SendSessionMessageAsync(sessionId, new("Pause after acknowledgment", "memory-ack"), Owner, ct);
                    await acknowledged.Task.WaitAsync(ct);
                    var paused = Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.CorrelationId == receipt.TurnId);
                    Assert.Contains(alreadyClearing ? ClearedContext : "Stored rule", paused.Input.Prompt);
                    await using var source = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, "perga.db")};Pooling=False");
                    await using var copy = new SqliteConnection($"Data Source={crashDb};Pooling=False");
                    await source.OpenAsync(ct);
                    await copy.OpenAsync(ct);
                    Assert.Equal("delivered:running", await source.ExecuteScalarAsync<string>(new CommandDefinition("""
                        SELECT m.delivery || ':' || t.status FROM session_messages m
                        JOIN session_turns t ON t.id = m.turn_id WHERE t.id = @TurnId;
                        """, new { receipt.TurnId }, cancellationToken: ct)));
                    nativeThreadId = Assert.IsType<string>(await source.ExecuteScalarAsync<string>(new CommandDefinition(
                        "SELECT native_thread_id FROM agent_sessions WHERE id = @SessionId;", new { SessionId = sessionId }, cancellationToken: ct)));
                    // Reopen a consistent copy of the live database. Graceful teardown can then finish only the original turn.
                    source.BackupDatabase(copy);
                    if (!alreadyClearing)
                        await engine.DeleteAsync(scope, "rule", entry.Meta.Etag, ct);
                }
                finally
                {
                    release.TrySetResult();
                }
                await kernel.WaitForTrackIdleAsync(trackId, ct);
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName, options => options.DbPath = crashDb);
            var restartedKernel = reopened.Services.GetRequiredService<IPergaKernel>();
            var interrupted = await restartedKernel.GetSessionItemsAsync(sessionId, new(), Owner, ct);
            Assert.Equal("unknown", interrupted.LastTurn?.Status);
            Assert.Equal(nativeThreadId, interrupted.Session.NativeThreadId);
            var reconnected = await restartedKernel.ControlSessionAsync(sessionId, new("reconnect"), Owner, ct);
            Assert.Equal(nativeThreadId, reconnected.NativeThreadId);
            Assert.Equal(interrupted.Session.Epoch, reconnected.Epoch);
            await restartedKernel.WaitForTrackIdleAsync(trackId, ct);
            await SendAndReadTurnAsync(reopened, restartedKernel, trackId, sessionId, "After acknowledged turn restart", ct);
            var turns = reopened.AgentRuntime.ExecutedTurns.ToArray();
            Assert.All(turns, turn => Assert.Equal(nativeThreadId, turn.Spec.ResumeNativeThreadId));
            var cleared = turns.Where(turn => turn.Input.Prompt.StartsWith(ClearedContext, StringComparison.Ordinal));
            if (alreadyClearing)
                Assert.Empty(cleared);
            else
                Assert.Equal(turns[0], Assert.Single(cleared));
            Assert.Equal(["loaded", "cleared"], await MemoryNoticeModesAsync(restartedKernel, sessionId, ct));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RejectionBeforePromptDoesNotPersistOrAdvanceMemoryDelivery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var engine = host.Services.GetRequiredService<IMemoryEngine>();
        await engine.PutAsync(new(MemoryScopeKind.Global), new("rule", "Rule", "Rule summary", "Stored rule", Pinned: true), ct);
        host.AgentRuntime.SetTurnHandler((_, input, _) => Deliver(input));
        async IAsyncEnumerable<UnifiedAgentEvent> Deliver(TurnInput input)
        {
            await Task.CompletedTask;
            if (input.Prompt.EndsWith("Rejected prompt", StringComparison.Ordinal))
                yield return new UnifiedAgentEvent.TurnError("rejected", "Before prompt", false, IsConclusive: true, BeforePrompt: true);
            else
                yield return new UnifiedAgentEvent.TurnCompleted(null);
        }

        var rejected = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Rejected prompt", ct);
        Assert.Contains("Stored rule", rejected.Input.Prompt);
        Assert.Empty(await MemoryNoticeModesAsync(kernel, sessionId, ct));
        var accepted = await SendAndReadTurnAsync(host, kernel, trackId, sessionId, "Accepted prompt", ct);
        Assert.Contains("Stored rule", accepted.Input.Prompt);
        Assert.Equal(["loaded"], await MemoryNoticeModesAsync(kernel, sessionId, ct));
    }
}
