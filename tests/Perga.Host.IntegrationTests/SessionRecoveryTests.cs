using Dapper;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Kernel.Events;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace Perga.Host.IntegrationTests;

public sealed class SessionRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupStopAllowsNextSendOrReturnsDedicatedRecoveryErrorUntilShutdownRetry(bool confirmed)
    {
        await using var host = new PergaHostTestCluster();
        var (track, id) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.ShutdownConfirmed = confirmed;
        host.AgentRuntime.BeforeOpenAsync = async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var accepted = await kernel.SendSessionMessageAsync(id, new(Content: "cancel startup", RequestKey: "cancel-startup"),
            new InteropPrincipal.Human(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var stopped = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "stop", turnId = accepted.TurnId });
        stopped.EnsureSuccessStatusCode();
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
        var page = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("interrupted", page.LastTurn!.Status);
        if (!confirmed)
        {
            Assert.Equal("shutdown_unconfirmed", page.UnknownDetail!.Reason);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{id}/messages")
            { Content = JsonContent.Create(new { content = "blocked" }) };
            request.Headers.Add("Idempotency-Key", "blocked");
            using var refused = await host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            using var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("session_recovery_required", error.RootElement.GetProperty("code").GetString());
            var store = host.Services.GetRequiredService<ITrackStore>();
            Assert.Equal("shutdown_unconfirmed", (await store.LoadTrackAggregateAsync(track, default))!.Sessions.Single().UnknownReason);
            host.AgentRuntime.ShutdownConfirmed = true;
            using var reset = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reset_context" });
            reset.EnsureSuccessStatusCode();
        }
        else Assert.Equal("idle", page.State);
        host.AgentRuntime.BeforeOpenAsync = null;
        await kernel.SendSessionMessageAsync(id, new(Content: "next", RequestKey: "next"), new InteropPrincipal.Human(), default);
        await kernel.WaitForTrackIdleAsync(track);
        Assert.Equal("completed", (await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).LastTurn!.Status);
    }

    [Fact]
    public async Task IdleSessionCanResetContextAndRetainsBoundary()
    {
        await using var host = new PergaHostTestCluster();
        var (_, id) = await host.CreateTrackAsync();
        var path = $"/api/v1/sessions/{id}";
        using var before = await host.Client.GetFromJsonAsync<JsonDocument>(path + "/items");
        Assert.Equal("idle", before!.RootElement.GetProperty("state").GetString());
        using var reset = await host.Client.PostAsJsonAsync(path + "/control", new { action = "reset_context" });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        using var after = await host.Client.GetFromJsonAsync<JsonDocument>(path + "/items");
        Assert.Equal("idle", after!.RootElement.GetProperty("state").GetString());
        Assert.Equal(before.RootElement.GetProperty("session").GetProperty("epoch").GetInt32() + 1,
            after.RootElement.GetProperty("session").GetProperty("epoch").GetInt32());
        Assert.Contains(after.RootElement.GetProperty("items").EnumerateArray(), i => i.GetProperty("kind").GetString() == "epoch_boundary");
    }

    [Fact]
    public async Task MissingAndInvalidNativeIdsPersistReconnectInvalidationAcrossRestart()
    {
        var dir = Directory.CreateTempSubdirectory("perga-recover-");
        try
        {
            string id;
            await using (var host = new PergaHostTestCluster(dir.FullName))
            {
                var (track, session) = await host.CreateTrackAsync();
                id = session;
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                host.AgentRuntime.SetTurnHandler(Unknown);
                await kernel.SendSessionMessageAsync(id, new(Content: "unknown", RequestKey: "unknown"), new InteropPrincipal.Human(), default);
                await kernel.WaitForTrackIdleAsync(track);
                host.AgentRuntime.OpenError = _ => new ProviderSessionNotFoundException("Original thread not found");
                using var fail = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reconnect" });
                Assert.Equal(HttpStatusCode.Conflict, fail.StatusCode);
                var page = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
                Assert.False(page.UnknownDetail?.CanReconnect);
                Assert.Equal("Original thread not found", page.UnknownDetail?.LastError);
            }
            await using (var host = new PergaHostTestCluster(dir.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var page = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
                Assert.Equal("unknown", page.State);
                Assert.False(page.UnknownDetail?.CanReconnect);
                Assert.Equal("Original thread not found", page.UnknownDetail?.LastError);
                var store = host.Services.GetRequiredService<ITrackStore>();
                await store.UpsertSessionAsync(page.Session with { NativeThreadId = null, ReconnectInvalidated = false }, null, default);
            }
            await using (var host = new PergaHostTestCluster(dir.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                Assert.False((await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).UnknownDetail?.CanReconnect);
                using var fail = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reconnect" });
                Assert.Equal(HttpStatusCode.Conflict, fail.StatusCode);
                Assert.True((await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).Session.ReconnectInvalidated);
                using var reset = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reset_context" });
                reset.EnsureSuccessStatusCode();
                Assert.Equal("idle", (await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).State);
            }
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task ResetDoesNotClearIdentityUntilSubprocessShutdownIsConfirmed()
    {
        await using var host = new PergaHostTestCluster();
        var (track, id) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        host.AgentRuntime.SetTurnHandler(Unknown);
        await kernel.SendSessionMessageAsync(id, new(Content: "unknown", RequestKey: "unknown"), new InteropPrincipal.Human(), default);
        await kernel.WaitForTrackIdleAsync(track);
        var before = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        host.AgentRuntime.ShutdownConfirmed = false;
        using var failed = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reset_context" });
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        var after = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("unknown", after.State);
        Assert.Equal(before.Session.Epoch, after.Session.Epoch);
        Assert.Equal(before.Session.NativeThreadId, after.Session.NativeThreadId);
        Assert.DoesNotContain(after.Items, item => item.Kind == "epoch_boundary");
        host.AgentRuntime.ShutdownConfirmed = true;
        using var retry = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reset_context" });
        retry.EnsureSuccessStatusCode();
        Assert.Null((await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).Session.NativeThreadId);
    }

    [Fact]
    public async Task StopTimeoutSettlesUnknownAndPublishesStoppingAndUnknown()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
        {
            options.ProviderCancelDispatchGrace = TimeSpan.FromMilliseconds(100);
            options.ProviderTerminalStopGrace = TimeSpan.FromMilliseconds(100);
        });
        var (track, id) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.RequiresTerminalStop = true;
        host.AgentRuntime.CancelHandler = _ => Task.CompletedTask;
        host.AgentRuntime.SetTurnHandler(Stream);
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.TextDelta("running");
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        await kernel.SendSessionMessageAsync(id, new(Content: "running", RequestKey: "running"), new InteropPrincipal.Human(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<KernelException>(async () => await kernel.ControlSessionAsync(id, new("stop", TurnId: (await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).ActiveTurn!.Id), new InteropPrincipal.Human(), default));
        await kernel.WaitForTrackIdleAsync(track).WaitAsync(TimeSpan.FromSeconds(5));
        var page = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("unknown", page.State);
        Assert.Equal("unknown", page.LastTurn?.Status);
        Assert.Equal("stop_timeout", page.UnknownDetail?.Reason);
        var events = await host.Services.GetRequiredService<ITrackStore>().ListEventsSinceAsync(0, track);
        var states = events.Where(e => e.Kind == KernelEventKinds.SessionUpdated && e.Payload.TryGetProperty("id", out var session) && session.GetString() == id)
            .Select(e => e.Payload.GetProperty("status").GetString()).ToArray();
        Assert.Contains("stopping", states);
        Assert.Equal("unknown", states.Last());
    }

    [Fact]
    public async Task ResetTransactionFailureKeepsEpochAndUnresolvedInputForRetry()
    {
        await using var host = new PergaHostTestCluster();
        var (track, id) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        host.AgentRuntime.SetTurnHandler(Unknown);
        var accepted = await kernel.SendSessionMessageAsync(id, new(Content: "unknown", RequestKey: "unknown"), new InteropPrincipal.Human(), default);
        await kernel.WaitForTrackIdleAsync(track);
        var before = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        var oldMcp = Assert.IsType<McpServerConfig.HttpMcpServer>(host.AgentRuntime.OpenedSessions.Last().Spec.McpServers["perga"]);
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync();
        await db.ExecuteAsync("CREATE TRIGGER reject_boundary BEFORE INSERT ON session_items WHEN NEW.kind = 'epoch_boundary' BEGIN SELECT RAISE(ABORT, 'injected boundary failure'); END;");
        using var failed = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reset_context" });
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        var after = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("unknown", after.State);
        Assert.Equal(before.Session.Epoch, after.Session.Epoch);
        Assert.Equal(before.Session.NativeThreadId, after.Session.NativeThreadId);
        Assert.Null(((await host.Services.GetRequiredService<ITrackStore>().SessionInputs.FindByIdAsync(id, accepted.MessageId, default))!.Receipt).ResolvedAtMs);
        Assert.DoesNotContain(after.Items, item => item.Kind == "epoch_boundary");
        Assert.Null(kernel.McpSessionBridge.ResolveToken(oldMcp.Headers["Authorization"]["Bearer ".Length..]));
        await db.ExecuteAsync("DROP TRIGGER reject_boundary;");
        using var retry = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reset_context" });
        retry.EnsureSuccessStatusCode();
        var restored = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal(before.Session.Epoch + 1, restored.Session.Epoch);
        Assert.Single(restored.Items, item => item.Kind == "epoch_boundary");
        Assert.NotNull(((await host.Services.GetRequiredService<ITrackStore>().SessionInputs.FindByIdAsync(id, accepted.MessageId, default))!.Receipt).ResolvedAtMs);
    }

    [Fact]
    public async Task NativeCompactionPublishesBothStatesAndCompletedLastTurnSurvivesIdle()
    {
        await using var host = new PergaHostTestCluster();
        var (track, id) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<NativeCompactResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.CompactHandler = ct => { entered.TrySetResult(); return finish.Task.WaitAsync(ct); };
        var compact = kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("compacting", (await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).State);
        finish.TrySetResult(new());
        await compact;
        var page = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("idle", page.State);
        Assert.Equal("completed", page.LastTurn?.Status);
        Assert.Null(page.ActiveTurn);
        var events = await host.Services.GetRequiredService<ITrackStore>().ListEventsSinceAsync(0, track);
        var states = events.Where(e => e.Kind == KernelEventKinds.SessionUpdated && e.Payload.TryGetProperty("id", out var session) && session.GetString() == id)
            .Select(e => e.Payload.GetProperty("status").GetString()).ToArray();
        Assert.Contains("compacting", states);
        Assert.Equal("idle", states.Last());
    }

    [Fact]
    public async Task FinishedWorkerCannotResetContextWithoutItsLease()
    {
        var dir = Directory.CreateTempSubdirectory("perga-worker-recover-");
        try
        {
            AgentSessionDto worker;
            await using (var host = new PergaHostTestCluster(dir.FullName))
            {
                var (track, _) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var dispatched = await FlowFixture.StartWorkerAsync(kernel, track, "recover-worker", "Inspect");
                await kernel.WaitForTrackIdleAsync(track);
                worker = Assert.Single(await kernel.ListSessionsAsync(track, new(IncludeArchived: true), new InteropPrincipal.Human(), default), summary => summary.TaskId == dispatched.Task.Id).Session;
                await host.Services.GetRequiredService<ITrackStore>().UpsertSessionAsync(worker with
                    { Status = "unknown", UnknownReason = "host_restarted" }, null, default);
            }
            await using (var host = new PergaHostTestCluster(dir.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var page = await kernel.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), default);
                Assert.Equal("unknown", page.State);
                Assert.False(page.UnknownDetail?.CanResetContext);
                Assert.False(page.UnknownDetail?.CanReconnect);
                using var reset = await host.Client.PostAsJsonAsync($"/api/v1/sessions/{worker.Id}/control", new { action = "reset_context" });
                Assert.Equal(HttpStatusCode.Conflict, reset.StatusCode);
                Assert.Equal(worker.Epoch, (await kernel.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), default)).Session.Epoch);
            }
        }
        finally { dir.Delete(true); }
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> Unknown(HarnessLaunchSpec spec, TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new UnifiedAgentEvent.TextDelta("partial");
        yield return new UnifiedAgentEvent.TurnError("stream_lost", "Disconnected", false);
    }
}
