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
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task StartupIsBoundedAndStopCancelsStartup(bool stop, bool lateHandle, bool shutdownConfirmed)
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: o => o.SessionStartupTimeout = TimeSpan.FromMilliseconds(250));
        var (track, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var session = await kernel.CreateSubagentSessionAsync(track, new());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeOpenAsync = async (_, ct) =>
        {
            using var registration = ct.Register(() => canceled.TrySetResult());
            entered.TrySetResult();
            // Keep the open pending until after the kernel settles the canceled turn.
            await release.Task;
            if (!lateHandle) ct.ThrowIfCancellationRequested();
        };
        try
        {
            var accepted = await kernel.AcceptMessageAsync(session.Id, new("launch", "launch"), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop)
            {
                using var response = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{session.Id}/interrupt", new { turnId = accepted.Receipt!.TurnId });
                response.EnsureSuccessStatusCode();
            }
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
            var page = (await host.Client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{session.Id}/items"))!;
            Assert.Equal("idle", page.State);
            Assert.Equal("idle", page.Session.Status);
            Assert.Null(page.UnknownDetail);
            Assert.NotNull(page.ProviderCleanup);
            Assert.Equal("interrupted", page.LastTurn?.Status);
            var original = await kernel.GetSessionInputAsync(session.Id, accepted.Receipt!.InputId, default);
            Assert.Equal("rejected", original.DeliveryState);
            Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, t => t.Input.Prompt == "launch");

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var blocked = await SendAfterCleanupAsync(host.Client, session.Id, deadline.Token);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Equal("session_recovery_required", (await blocked.Content.ReadFromJsonAsync<Http.ApiErrorResponse>(deadline.Token))?.Code);

            if (!shutdownConfirmed)
            {
                host.AgentRuntime.ConfirmShutdownAttempt = attempt => attempt >= 2;
                host.AgentRuntime.AfterOpenError = agent => new ProviderOpenShutdownUnconfirmedException(agent, new OperationCanceledException());
            }
            release.TrySetResult();
            if (!shutdownConfirmed)
            {
                while (host.AgentRuntime.DisposedSessions.IsEmpty)
                    await Task.Delay(10, deadline.Token);
                Assert.NotNull((await kernel.GetSessionItemsAsync(session.Id)).ProviderCleanup);
                using var stillBlocked = await SendAfterCleanupAsync(host.Client, session.Id, deadline.Token);
                Assert.Equal(HttpStatusCode.Conflict, stillBlocked.StatusCode);
                Assert.Single(host.AgentRuntime.OpenedSessions);

                await kernel.RecoverSessionAsync(session.Id, new("reset_context"), deadline.Token);
            }
            while ((await kernel.GetSessionItemsAsync(session.Id, ct: deadline.Token)).ProviderCleanup is not null)
                await Task.Delay(10, deadline.Token);
            host.AgentRuntime.BeforeOpenAsync = null;
            host.AgentRuntime.AfterOpenError = null;

            using var next = await SendAfterCleanupAsync(host.Client, session.Id, deadline.Token);
            Assert.True(next.StatusCode == HttpStatusCode.Accepted,
                $"Expected a new message to be accepted after provider cleanup. Received {(int)next.StatusCode}: {await next.Content.ReadAsStringAsync()}");
            await kernel.WaitForTrackIdleAsync(track).WaitAsync(deadline.Token);
            Assert.Equal("completed", (await kernel.GetSessionItemsAsync(session.Id, ct: deadline.Token)).LastTurn?.Status);
            Assert.Equal("after provider cleanup", Assert.Single(host.AgentRuntime.ExecutedTurns).Input.Prompt);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task LateCleanupUnblocksIdleSessionAfterRecoveryTimesOut()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: o => o.SessionStartupTimeout = TimeSpan.FromMilliseconds(250));
        var (track, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeOpenAsync = async (_, _) =>
        {
            opening.TrySetResult();
            await release.Task;
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        try
        {
            var accepted = await kernel.AcceptMessageAsync(sessionId, new("launch", "launch"), ct);
            await opening.Task.WaitAsync(ct);
            await kernel.InterruptSessionAsync(sessionId, ct, accepted.Receipt!.TurnId);
            await kernel.WaitForTrackIdleAsync(track).WaitAsync(ct);
            var failure = await Assert.ThrowsAsync<KernelException>(() => kernel.RecoverSessionAsync(sessionId, new("reset_context"), ct));
            Assert.Equal(KernelErrorCode.SessionRecoveryRequired, failure.Code);
            Assert.Equal("Provider recovery timed out.", failure.Message);
            var blocked = await kernel.GetSessionItemsAsync(sessionId, ct: ct);
            Assert.NotNull(blocked.ProviderCleanup);
            Assert.Null(blocked.Session.UnknownLastError);
            var store = host.Services.GetRequiredService<ITrackStore>();
            var beforeCleanup = await store.ListEventsSinceAsync(0, track, ct: ct);

            host.AgentRuntime.BeforeOpenAsync = null;
            release.TrySetResult();
            while ((await kernel.GetSessionItemsAsync(sessionId, ct: ct)).ProviderCleanup is not null)
                await Task.Delay(10, ct);
            var afterCleanup = await store.ListEventsSinceAsync(0, track, ct: ct);
            Assert.Equal(beforeCleanup.Select(evt => evt.EventId), afterCleanup.Select(evt => evt.EventId));
            using var sent = await SendAfterCleanupAsync(host.Client, sessionId, ct);
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedReconnectWhileCleaningUpKeepsTheOriginalSessionFenced(bool wrongNativeId)
    {
        const string originalThread = "original-provider-thread";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var directory = Directory.CreateTempSubdirectory("perga-reconnect-cleanup-");
        try
        {
            string trackId;
            string sessionId;
            await using (var initial = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await initial.CreateTrackAsync();
                var kernel = initial.Services.GetRequiredService<IPergaKernel>();
                var session = Assert.Single(await kernel.ListSessionsAsync(trackId, ct));
                var store = initial.Services.GetRequiredService<ITrackStore>();
                await store.UpsertSessionAsync(session with { NativeThreadId = originalThread }, null, ct);
            }
            await using var host = new PergaHostTestCluster(directory.FullName);
            var live = host.Services.GetRequiredService<IPergaKernel>();
            var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var validating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseValidation = new ManualResetEventSlim();
            host.AgentRuntime.BeforeOpenAsync = async (_, _) =>
            {
                if (opening.TrySetResult())
                {
                    await release.Task;
                    return;
                }
                host.AgentRuntime.ConfirmShutdownAttempt = _ => false;
                host.AgentRuntime.MismatchNativeThreadId = wrongNativeId;
                host.AgentRuntime.LaunchConfirmationHandler = _ => new(SessionConfiguration.FromNullable("wrong-model", null, null));
                host.AgentRuntime.BeforeConfirmSessionResume = () =>
                {
                    validating.TrySetResult();
                    releaseValidation.Wait(ct);
                };
            };
            try
            {
                var accepted = await live.AcceptMessageAsync(sessionId, new("launch", "launch"), ct);
                await opening.Task.WaitAsync(ct);
                await live.InterruptSessionAsync(sessionId, ct, accepted.Receipt!.TurnId);
                await live.WaitForTrackIdleAsync(trackId).WaitAsync(ct);

                var recovery = live.RecoverSessionAsync(sessionId, new("reconnect"), ct);
                while ((await live.GetSessionItemsAsync(sessionId, ct: ct)).ProviderCleanup?.Recovering != true)
                    await Task.Delay(10, ct);
                release.TrySetResult();
                await validating.Task.WaitAsync(ct);
                using var model = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model = "next-model" }, ct);
                model.EnsureSuccessStatusCode();
                Assert.Empty(host.AgentRuntime.Configurations);
                releaseValidation.Set();
                var refusedRecovery = await Assert.ThrowsAsync<KernelException>(() => recovery);
                Assert.Equal(KernelErrorCode.SessionRecoveryRequired, refusedRecovery.Code);
                var blocked = await live.GetSessionItemsAsync(sessionId, ct: ct);
                Assert.Equal(wrongNativeId ? "unknown" : "idle", blocked.State);
                Assert.Equal(wrongNativeId ? "session_lost" : null, blocked.UnknownDetail?.Reason);
                Assert.Equal(!wrongNativeId, blocked.ProviderCleanup is not null);
                if (!wrongNativeId) Assert.Null(blocked.Session.UnknownLastError);
                Assert.Equal(originalThread, blocked.Session.NativeThreadId);
                Assert.Equal("next-model", blocked.Session.Model);
                using var refused = await SendAfterCleanupAsync(host.Client, sessionId, ct);
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
                Assert.Empty(host.AgentRuntime.ExecutedTurns);
            }
            finally
            {
                release.TrySetResult();
                releaseValidation.Set();
            }
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task<HttpResponseMessage> SendAfterCleanupAsync(HttpClient client, string sessionId, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/messages")
        {
            Content = JsonContent.Create(new { content = "after provider cleanup" })
        };
        message.Headers.Add("Idempotency-Key", "after-cleanup");
        return await client.SendAsync(message, ct);
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
                var session = await kernel.CreateSubagentSessionAsync(track, new());
                id = session.Id;
                var store = host.Services.GetRequiredService<ITrackStore>();
                // Hydrate the persisted native identity in a fresh host below.
                await store.UpsertSessionAsync(session with { NativeThreadId = "lost-thread" }, null, default);
            }
            await using (var host = new PergaHostTestCluster(dir.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var store = host.Services.GetRequiredService<ITrackStore>();
                var page = await kernel.GetSessionItemsAsync(id);
                var track = page.Session.TrackId;
                await store.SessionMessages!.InsertAsync(new("unknown-queue", id, null, "user", "queue", "hash", null, null,
                    "  copy this exact text\nagain  ", "unknown", 1, "queue"), default);
                var startupCalls = 0;
                host.AgentRuntime.BeforeOpenAsync = async (_, ct) =>
                {
                    Interlocked.Increment(ref startupCalls);
                    await store.SessionMessages.InsertAsync(new("waiting-queue", id, null, "user", "waiting", "hash", null, null,
                        "waiting for startup", "queued", 2, "queue"), ct);
                    await store.TrackWakes.EnqueueAsync(new(track, "mail", null,
                        System.Text.Json.JsonSerializer.SerializeToElement(new { prompt = "critical wake content" }), id), ct);
                };
                host.AgentRuntime.MismatchNativeThreadId = mismatch;
                if (!mismatch) host.AgentRuntime.OpenError = _ => new ProviderSessionNotFoundException("lost");
                await kernel.AcceptMessageAsync(id, new("first", "first"), default);
                await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
                var lost = await kernel.GetSessionItemsAsync(id);
                Assert.Equal("unknown", lost.State);
                Assert.Equal("session_lost", lost.UnknownDetail?.Reason);
                Assert.True(lost.Session.ReconnectInvalidated);
                Assert.False(lost.UnknownDetail?.CanReconnect);
                Assert.True(lost.UnknownDetail?.CanResetContext);
                Assert.Equal(2, lost.QueuedMessages.Count);
                Assert.Equal(1, startupCalls);
                Assert.Equal("unknown", lost.LastTurn?.Status);
                var launches = host.AgentRuntime.OpenedSessions.Count;
                await Assert.ThrowsAsync<KernelException>(() => kernel.AcceptMessageAsync(id, new("blocked", "blocked"), default));
                await kernel.RecoverSessionAsync(id, new("reset_context"), default);
                await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
                var reset = await kernel.GetSessionItemsAsync(id);
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
        await store.SessionMessages!.InsertAsync(new("withdraw", id, null, "user", "withdraw", "hash", null, null,
            "withdraw text", delivery, 1, "queue"), default);
        using var response = await host.Client.DeleteAsync($"/api/v1/sessions/{id}/messages/queue/withdraw");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("withdrawn", (await store.SessionMessages.GetAsync(id, false, "withdraw", default))?.Delivery);
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kernel.CompactSessionAsync(id).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("unknown", (await kernel.GetSessionItemsAsync(id)).State);
            await kernel.RecoverSessionAsync(id, new("reset_context"), default);
            await kernel.AcceptMessageAsync(id, new("after compact", "after compact"), default);
            await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("completed", (await kernel.GetSessionItemsAsync(id)).LastTurn?.Status);
        }
        finally { completion.TrySetResult(new()); }
    }
}
