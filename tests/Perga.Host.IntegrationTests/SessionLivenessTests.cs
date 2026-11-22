using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Host.IntegrationTests.Fixtures;

namespace Perga.Host.IntegrationTests;

public sealed class SessionLivenessTests
{
    [Fact]
    public async Task ConfiguredIdleEvictionPreservesSqliteSessionAndResumesNextTurn()
    {
        var idleTimeout = TimeSpan.FromMinutes(2);
        await using var host = new PergaHostTestCluster(configureHostOptions: o => o.SessionIdleTimeout = idleTimeout);
        var (track, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var human = new InteropPrincipal.Human();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;

        await kernel.SendSessionMessageAsync(sessionId, new("first", "first"), human, ct);
        await kernel.WaitForTrackIdleAsync(track, ct);
        var before = Assert.Single((await store.LoadTrackAggregateAsync(track, ct))!.Sessions);
        var opened = Assert.Single(host.AgentRuntime.OpenedSessions);
        Assert.NotNull(before.NativeThreadId);
        Assert.NotNull(before.ContextUsage);

        host.TimeProvider.Advance(idleTimeout);
        Assert.Equal(1, await kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
        Assert.Equal(opened.NativeThreadId, Assert.Single(host.AgentRuntime.DisposedSessions));
        Assert.Equal(before, Assert.Single((await store.LoadTrackAggregateAsync(track, ct))!.Sessions));

        await kernel.SendSessionMessageAsync(sessionId, new("follow-up", "follow-up"), human, ct);
        await kernel.WaitForTrackIdleAsync(track, ct);
        Assert.Equal(before.NativeThreadId, host.AgentRuntime.OpenedSessions.Last().Spec.ResumeNativeThreadId);
        var page = await kernel.GetSessionItemsAsync(sessionId, new(), human, ct);
        Assert.Equal("completed", page.LastTurn?.Status);
        Assert.Equal(before.NativeThreadId, page.Session.NativeThreadId);
        Assert.Equal(before.Epoch, page.Session.Epoch);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupIsBoundedAndStopCancelsStartup(bool stop)
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: o => o.SessionStartupTimeout = TimeSpan.FromMilliseconds(250));
        var (track, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var session = (await kernel.CreateSessionAsync(track, new(), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default)).Receipt.Session;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeOpenAsync = async (_, ct) =>
        {
            using var registration = ct.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            // Deliberately ignore cancellation to verify the kernel's finite wait.
            await release.Task;
        };
        try
        {
            var accepted = await kernel.SendSessionMessageAsync(session.Id, new(Content: "launch", RequestKey: "launch"), new InteropPrincipal.Human(), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop)
            {
                using var response = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/control", new { action = "stop", turnId = accepted.TurnId });
                response.EnsureSuccessStatusCode();
            }
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
            var page = await kernel.GetSessionItemsAsync(session.Id, new(), new InteropPrincipal.Human(), default);
            Assert.Equal("idle", page.State);
            Assert.Equal("interrupted", page.LastTurn?.Status);
            Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, t => t.Input.Prompt == "launch");
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostStartupSessionOpensCircuitBreakerAndResetArchivesQueueWithoutDispatch(bool mismatch)
    {
        var dir = Directory.CreateTempSubdirectory("perga-liveness-");
        try
        {
            string id;
            await using (var host = new PergaHostTestCluster(dir.FullName))
            {
                var (track, _) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var session = (await kernel.CreateSessionAsync(track, new(), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default)).Receipt.Session;
                id = session.Id;
                var store = host.Services.GetRequiredService<ITrackStore>();
                // Hydrate the persisted native identity in a fresh host below.
                await store.UpsertSessionAsync(session with { NativeThreadId = "lost-thread" }, null, default);
            }
            await using (var host = new PergaHostTestCluster(dir.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var store = host.Services.GetRequiredService<ITrackStore>();
                var page = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
                var track = page.Session.TrackId;
                await store.SessionMessages!.InsertAsync(new("unknown-queue", id, "user", "queue", "hash", null, null,
                    "  copy this exact text\nagain  ", "unknown", 1, "queue"), default);
                var startupCalls = 0;
                host.AgentRuntime.BeforeOpenAsync = async (_, ct) =>
                {
                    Interlocked.Increment(ref startupCalls);
                    await store.SessionMessages.InsertAsync(new("waiting-queue", id, "user", "waiting", "hash", null, null,
                        "waiting for startup", "queued", 2, "queue"), ct);
                    await store.TrackWakes.EnqueueAsync(new(track, "mail", null,
                        System.Text.Json.JsonSerializer.SerializeToElement(new { prompt = "critical wake content" }), id), ct);
                };
                host.AgentRuntime.MismatchNativeThreadId = mismatch;
                if (!mismatch) host.AgentRuntime.OpenError = _ => new ProviderSessionNotFoundException("lost");
                await kernel.SendSessionMessageAsync(id, new(Content: "first", RequestKey: "first"), new InteropPrincipal.Human(), default);
                await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
                var lost = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
                Assert.Equal("unknown", lost.State);
                Assert.Equal("session_lost", lost.UnknownDetail?.Reason);
                Assert.True(lost.Session.ReconnectInvalidated);
                Assert.False(lost.UnknownDetail?.CanReconnect);
                Assert.True(lost.UnknownDetail?.CanResetContext);
                Assert.Equal(2, lost.QueuedMessages.Count);
                Assert.Equal(1, startupCalls);
                Assert.Equal("unknown", lost.LastTurn?.Status);
                var launches = host.AgentRuntime.OpenedSessions.Count;
                await Assert.ThrowsAsync<KernelException>(() => kernel.SendSessionMessageAsync(id, new(Content: "blocked", RequestKey: "blocked"), new InteropPrincipal.Human(), default));
                await kernel.ControlSessionAsync(id, new("reset_context"), new InteropPrincipal.Human(), default);
                await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
                var reset = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
                Assert.Equal("idle", reset.State);
                Assert.Empty(reset.QueuedMessages);
                Assert.Equal(launches, host.AgentRuntime.OpenedSessions.Count);
                Assert.Equal(1, startupCalls);
                Assert.All(await store.TrackWakes.ListAsync(track, default), w => Assert.Equal(("dropped", "session_reset"), (w.State, w.Reason)));
                Assert.Contains(reset.Items, i => i.TurnId is null && i.Kind == "system" &&
                    i.Payload.GetProperty("content").GetString()!.Contains("critical wake content"));
                Assert.Equal("epoch_boundary", reset.Items.Last().Kind);
                Assert.Contains(reset.Items, i => i.TurnId is null && i.Kind == "user" &&
                    i.Payload.GetProperty("content").GetString() == "  copy this exact text\nagain  ");
            }
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("unknown")]
    public async Task TurnlessQueueCanBeWithdrawn(string delivery)
    {
        await using var host = new PergaHostTestCluster();
        var (_, id) = await host.CreateTrackAsync();
        var store = host.Services.GetRequiredService<ITrackStore>();
        await store.SessionMessages!.InsertAsync(new("withdraw", id, "user", "withdraw", "hash", null, null,
            "withdraw text", delivery, 1, "queue"), default);
        using var response = await host.Client.DeleteAsync($"/api/v1/sessions/{id}/messages/withdraw");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("withdrawn", (await store.SessionMessages.GetAsync(id, "withdraw", default))?.Delivery);
    }

    [Fact]
    public async Task NonCooperativeCompactionHasFiniteFallbackAndClearsSlot()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: o => o.SessionCompactionTimeout = TimeSpan.FromMilliseconds(150));
        var (track, id) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var completion = new TaskCompletionSource<NativeCompactResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.CompactHandler = _ => completion.Task;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("unknown", (await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).State);
            await kernel.ControlSessionAsync(id, new("reset_context"), new InteropPrincipal.Human(), default);
            await kernel.SendSessionMessageAsync(id, new(Content: "after compact", RequestKey: "after compact"), new InteropPrincipal.Human(), default);
            await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("completed", (await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).LastTurn?.Status);
        }
        finally { completion.TrySetResult(new()); }
    }
}
