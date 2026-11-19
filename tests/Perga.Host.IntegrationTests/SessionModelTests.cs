using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;

namespace Perga.Host.IntegrationTests;

public sealed class SessionModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivePostCompactionTurnRetainsDurableIdentity(bool wake)
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.AcceptMessageAsync(sessionId, new("warm", "identity-warm"), default);
        await kernel.WaitForTrackIdleAsync(trackId);
        await kernel.CompactSessionAsync(sessionId);
        Assert.Null(Assert.Single(await kernel.ListSessionsAsync(trackId)).NativeThreadId);
        var expectedId = host.AgentRuntime.OpenedSessions.Last().NativeThreadId;
        Assert.NotNull(expectedId);
        kernel.DispatchAdmission.SetDraining(true);
        await kernel.UpdateSessionModelAsync(sessionId, new("post-compaction-model", "high"));
        kernel.DispatchAdmission.SetDraining(false);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler(Stream);
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        try
        {
            if (wake)
            {
                var store = host.Services.GetRequiredService<Perga.Kernel.Ports.ITrackStore>();
                await store.TrackWakes.EnqueueAsync(Perga.Kernel.Ports.TrackWakeRequests.Prompt(trackId, "mail", "identity-wake", "hold"), default);
                await ((PergaKernelService)kernel).Actors.GetOrActivate(trackId).PokeLeadAsync(allowInline: false);
            }
            else await kernel.AcceptMessageAsync(sessionId, new("hold", "identity-hold"), default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var memory = Assert.Single(await kernel.ListSessionsAsync(trackId));
            Assert.Equal("running", memory.Status);
            await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
            var stored = await db.QuerySingleAsync<string?>("SELECT native_thread_id FROM agent_sessions WHERE id = @sessionId", new { sessionId });
            Assert.Equal(expectedId, stored);
            Assert.Equal(expectedId, memory.NativeThreadId);
            var readStore = host.Services.GetRequiredService<Perga.Kernel.Ports.ITrackStore>();
            var lastEvent = (await readStore.ListEventsSinceAsync(0, trackId)).Last(e => e.Kind == "session.updated");
            Assert.Equal("running", lastEvent.Payload.GetProperty("status").GetString());
            Assert.Equal(expectedId, lastEvent.Payload.GetProperty("nativeThreadId").GetString());
        }
        finally { release.TrySetResult(); }
        await kernel.WaitForTrackIdleAsync(trackId);
    }

    [Fact]
    public async Task DesiredDefaultModelAndEffortSurviveRejectedConfigurationAndHostRestart()
    {
        var data = Directory.CreateTempSubdirectory("perga-rejected-configuration-");
        try
        {
            string trackId;
            string sessionId;
            await using (var host = new PergaHostTestCluster(data.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                await kernel.AcceptMessageAsync(sessionId, new("warm", "rejected-config-restart"), default);
                await kernel.WaitForTrackIdleAsync(trackId);
                host.AgentRuntime.ConfigureHandler = _ => new SessionConfigurationOutcome.Rejected(
                    ConfigurationFailureCode.InvalidAcknowledgement, "effort rejected", new(null, null, false));
                using var response = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model = "  ", reasoningEffort = " high " });
                Assert.False(response.IsSuccessStatusCode);
            }
            await using var restarted = new PergaHostTestCluster(data.FullName);
            var restored = Assert.Single(await restarted.Services.GetRequiredService<IPergaKernel>().ListSessionsAsync(trackId));
            Assert.Equal(sessionId, restored.Id);
            Assert.Null(restored.Model);
            Assert.Equal("high", restored.ReasoningEffort);
            Assert.Empty(restarted.AgentRuntime.ExecutedTurns);
        }
        finally { data.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationFailurePreservesDesiredWriteWithoutReopenOrPrompt(bool unsupported)
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.AcceptMessageAsync(sessionId, new("warm", "configuration-warm"), default);
        await kernel.WaitForTrackIdleAsync(trackId);
        var opens = host.AgentRuntime.OpenedSessions.Count;
        var prompts = host.AgentRuntime.ExecutedTurns.Count;
        host.AgentRuntime.ConfigureHandler = _ => unsupported
            ? new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Effort, "effort unavailable")
            : new SessionConfigurationOutcome.Rejected(ConfigurationFailureCode.InvalidAcknowledgement, "effort rejected", new(null, null, false));
        using var response = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model = (string?)null, reasoningEffort = "high" });
        Assert.False(response.IsSuccessStatusCode);
        var desired = Assert.Single(await kernel.ListSessionsAsync(trackId));
        Assert.Null(desired.Model);
        Assert.Equal("high", desired.ReasoningEffort);
        Assert.Equal(opens, host.AgentRuntime.OpenedSessions.Count);
        Assert.Equal(prompts, host.AgentRuntime.ExecutedTurns.Count);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("chosen-model", false)]
    [InlineData("chosen-model", true)]
    public async Task ItemsReadbackKeepsEffortAfterModelPut(string? model, bool running)
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler(Stream);
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        try
        {
            if (running)
            {
                await kernel.AcceptMessageAsync(sessionId, new("hold turn", "effort-readback"), default);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            using var response = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model, reasoningEffort = "high" });
            response.EnsureSuccessStatusCode();
            var page = await host.Client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{sessionId}/items?view=run", PergaJsonDefaults.ApiOptions);
            Assert.Equal(model, page!.Session.Model);
            Assert.Equal("high", page.Session.ReasoningEffort);
            Assert.Equal(running, page.ActiveTurn is not null);
        }
        finally { release.TrySetResult(); }
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task NativeCompactWithoutUsageAfterModelSwitchPersistsResetAcrossRestart()
    {
        var directory = Directory.CreateTempSubdirectory("perga-compact-model-");
        try
        {
            string trackId;
            string sessionId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                host.AgentRuntime.SupportsModelSwitch = true;
                host.AgentRuntime.CompactHandler = _ => Task.FromResult(new NativeCompactResult(null, "Compacted."));
                host.AgentRuntime.SetTurnHandler(Stream);
                await kernel.AcceptMessageAsync(sessionId, new("sample", "before-model-switch"), default);
                await kernel.WaitForTrackIdleAsync(trackId);
                var sampled = Assert.Single(await kernel.ListSessionsAsync(trackId));
                Assert.Equal(250, sampled.ContextUsage?.UsedTokens);
                Assert.NotNull(sampled.ContextUsage?.ObservedAtMs);

                using var response = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model = "next-model" });
                response.EnsureSuccessStatusCode();
                var switched = await response.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions);
                Assert.Null(switched!.ContextUsage);

                var compacted = await kernel.CompactSessionAsync(sessionId);
                Assert.Single(host.AgentRuntime.OpenedSessions);
                Assert.Equal("next-model", compacted.Model);

                Assert.Null(compacted.ContextUsage);

            }
            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var restored = Assert.Single(await reopened.Services.GetRequiredService<IPergaKernel>().ListSessionsAsync(trackId));
            Assert.Equal(sessionId, restored.Id);
            Assert.Equal("next-model", restored.Model);

            Assert.Null(restored.ContextUsage);

        }
        finally { directory.Delete(recursive: true); }

        static async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(10, 5, 0, 1000, UsedTokens: 250));
            await Task.Yield();
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
    }

    [Fact]
    public async Task ContextSnapshotSurvivesRestartWithoutReusingItsWindow()
    {
        var directory = Directory.CreateTempSubdirectory("perga-context-reconnect-");
        try
        {
            string trackId;
            string sessionId;
            long observedAt;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                host.AgentRuntime.SetTurnHandler((_, _, _) => Report(window: 1000, used: 250));
                await SampleAsync(host, trackId, sessionId, "warm-window");
                using var page = await ReadUsagePageAsync(host, sessionId);
                var session = page.RootElement.GetProperty("session");
                Assert.False(session.TryGetProperty("contextUsageRatio", out _));
                Assert.False(session.TryGetProperty("contextUsageInvalidated", out _));
                var usage = session.GetProperty("contextUsage");
                Assert.Equal(250, usage.GetProperty("usedTokens").GetInt64());
                Assert.Equal(1000, usage.GetProperty("windowTokens").GetInt64());
                Assert.Equal(0.25, usage.GetProperty("ratio").GetDouble());
                host.AgentRuntime.SetTurnHandler((_, _, _) => Report(window: null, used: 300));
                await SampleAsync(host, trackId, sessionId, "same-binding-without-window");
                using var followUp = await ReadUsagePageAsync(host, sessionId);
                var sameBinding = followUp.RootElement.GetProperty("session").GetProperty("contextUsage");
                Assert.Equal(1000, sameBinding.GetProperty("windowTokens").GetInt64());
                observedAt = sameBinding.GetProperty("observedAtMs").GetInt64();
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            using var oldPage = await ReadUsagePageAsync(reopened, sessionId);
            Assert.Equal(observedAt, oldPage.RootElement.GetProperty("session").GetProperty("contextUsage")
                .GetProperty("observedAtMs").GetInt64());

            reopened.AgentRuntime.SetTurnHandler((_, _, _) => Report(window: null, used: 900));
            await SampleAsync(reopened, trackId, sessionId, "reconnected-without-window");
            using var newPage = await ReadUsagePageAsync(reopened, sessionId);
            var newUsage = newPage.RootElement.GetProperty("session").GetProperty("contextUsage");
            Assert.Equal(900, newUsage.GetProperty("usedTokens").GetInt64());
            Assert.Equal(JsonValueKind.Null, newUsage.GetProperty("windowTokens").ValueKind);
            Assert.Equal(JsonValueKind.Null, newUsage.GetProperty("ratio").ValueKind);
            Assert.Single(reopened.AgentRuntime.ExecutedTurns);
        }
        finally { directory.Delete(recursive: true); }

        static async IAsyncEnumerable<UnifiedAgentEvent> Report(long? window, long used)
        {
            await Task.Yield();
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(used, 0, 0, window, UsedTokens: used));
        }

        static async Task SampleAsync(PergaHostTestCluster host, string trackId, string sessionId, string key)
        {
            var kernel = host.Services.GetRequiredService<IPergaKernel>();
            await kernel.AcceptMessageAsync(sessionId, new("sample", key), default);
            await kernel.WaitForTrackIdleAsync(trackId);
        }

        static async Task<JsonDocument> ReadUsagePageAsync(PergaHostTestCluster host, string sessionId)
        {
            using var response = await host.Client.GetAsync($"/api/v1/sessions/{sessionId}/items");
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("null", "invalid_argument")]
    [InlineData("{", "invalid_json")]
    [InlineData("", "invalid_json")]
    public async Task ModelEndpointRejectsInvalidJson(string body, string code)
    {
        await using var host = new PergaHostTestCluster();
        var (_, sessionId) = await host.CreateTrackAsync();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await host.Client.PutAsync($"/api/v1/sessions/{sessionId}/model", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedModelPersistenceKeepsRunningModelUsageValid(bool noFinalUsage)
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.UpdateSessionModelAsync(sessionId, new("A"));
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler(Stream);
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(10, 5, 0, 1000, UsedTokens: 250));
            sampled.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(10, 5, 0, 1000, UsedTokens: 300));
            yield return new UnifiedAgentEvent.TurnCompleted(noFinalUsage ? null : new TokenUsage(10, 5, 0, 1000, UsedTokens: 400));
        }
        await kernel.AcceptMessageAsync(sessionId, new("sample A", "failed-model-write"), default);
        try
        {
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(Assert.Single(await kernel.ListSessionsAsync(trackId)).ContextUsage);
            await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
            await db.OpenAsync();
            await db.ExecuteAsync("""
                CREATE TRIGGER fail_model_update BEFORE UPDATE ON agent_sessions
                WHEN NEW.model = 'B'
                BEGIN
                    SELECT RAISE(ABORT, 'simulated model persistence failure');
                END;
                """);
            var error = await Assert.ThrowsAsync<SqliteException>(() => kernel.UpdateSessionModelAsync(sessionId, new("B")));
            Assert.Contains("simulated model persistence failure", error.Message);
            var unchanged = Assert.Single(await kernel.ListSessionsAsync(trackId));
            Assert.Equal("A", unchanged.Model);
            Assert.Equal(250, unchanged.ContextUsage?.UsedTokens);
        }
        finally { release.TrySetResult(); }
        await kernel.WaitForTrackIdleAsync(trackId);
        var completed = Assert.Single(await kernel.ListSessionsAsync(trackId));
        Assert.Equal("A", completed.Model);
        Assert.Equal(noFinalUsage ? 300 : 400, completed.ContextUsage?.UsedTokens);

    }

    [Fact]
    public async Task ModelEndpointPersistsAcrossRestartAndSupportsNull()
    {
        var directory = Directory.CreateTempSubdirectory("perga-model-");
        try
        {
            string trackId;
            string sessionId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                using var modeResponse = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/permission-mode", new { permissionMode = "ask" });
                Assert.Equal(HttpStatusCode.OK, modeResponse.StatusCode);
                using var response = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model = "chosen-model", reasoningEffort = "high" });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(sessionId, json.RootElement.GetProperty("id").GetString());
                Assert.Equal("chosen-model", json.RootElement.GetProperty("model").GetString());
                Assert.True(json.RootElement.TryGetProperty("trackId", out _));
                Assert.False(json.RootElement.TryGetProperty("TrackId", out _));
                var session = await response.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions);
                Assert.Equal("chosen-model", session!.Model);
                Assert.Equal("high", session.ReasoningEffort);
                Assert.Equal("ask", session.PermissionMode);
                Assert.Null(session.ContextUsage);
                using var missing = await host.Client.PutAsJsonAsync("/api/v1/sessions/missing/model", new { model = "chosen-model" });
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var kernel = reopened.Services.GetRequiredService<IPergaKernel>();
            Assert.Equal("chosen-model", (await kernel.ListSessionsAsync(trackId)).Single(s => s.Id == sessionId).Model);
            var restored = (await kernel.ListSessionsAsync(trackId)).Single(s => s.Id == sessionId);
            Assert.Equal("high", restored.ReasoningEffort);
            Assert.Equal("ask", restored.PermissionMode);
            Assert.Null(restored.ContextUsage);
            using var reset = await reopened.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model = (string?)null });
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            var cleared = await reset.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions);
            Assert.Null(cleared!.Model);
            Assert.Null(cleared.ReasoningEffort);
            Assert.Equal("ask", cleared.PermissionMode);
            Assert.Null((await kernel.ListSessionsAsync(trackId)).Single(s => s.Id == sessionId).Model);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DefaultModelKeepsIndependentEffortAcrossRestart(bool lead)
    {
        var directory = Directory.CreateTempSubdirectory("perga-default-effort-");
        try
        {
            string trackId;
            string sessionId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                if (!lead)
                    sessionId = (await kernel.CreateSubagentSessionAsync(trackId, new CreateSessionRequest(Provider: "codex"))).Id;
                await kernel.UpdateSessionModelAsync(sessionId, new("chosen", "low"));
                using var permission = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/permission-mode", new { permissionMode = "ask" });
                permission.EnsureSuccessStatusCode();
                using var response = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model",
                    new { model = (string?)null, reasoningEffort = " high " });
                response.EnsureSuccessStatusCode();
                var selected = (await response.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions))!;
                Assert.Null(selected.Model);
                Assert.Equal("high", selected.ReasoningEffort);
            }
            await using (var reopened = new PergaHostTestCluster(directory.FullName))
            {
                var restoredKernel = reopened.Services.GetRequiredService<IPergaKernel>();
                var restored = Assert.Single(await restoredKernel.ListSessionsAsync(trackId), s => s.Id == sessionId);
                Assert.Null(restored.Model);
                Assert.Equal("high", restored.ReasoningEffort);
                await restoredKernel.AcceptMessageAsync(sessionId, new SendMessageRequest("use default", "default-effort"), default);
                await restoredKernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));
                var launch = Assert.Single(reopened.AgentRuntime.ExecutedTurns).Spec;
                Assert.Null(launch.Model);
                Assert.Equal("high", launch.ReasoningEffort);
                Assert.Equal("ask", launch.PermissionMode);
                using var clearEffort = await reopened.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model",
                    new { model = "chosen", reasoningEffort = (string?)null });
                clearEffort.EnsureSuccessStatusCode();
                var cleared = (await clearEffort.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions))!;
                Assert.Equal("chosen", cleared.Model);
                Assert.Null(cleared.ReasoningEffort);
            }
            await using var clearedHost = new PergaHostTestCluster(directory.FullName);
            var persisted = Assert.Single(await clearedHost.Services.GetRequiredService<IPergaKernel>().ListSessionsAsync(trackId), s => s.Id == sessionId);
            Assert.Equal("chosen", persisted.Model);
            Assert.Null(persisted.ReasoningEffort);
            Assert.Equal("ask", persisted.PermissionMode);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task ConversationModelEndpointCannotBypassWorkerSettings()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, _) = await host.CreateTrackAsync();
        var dispatch = await kernel.DispatchWorkerAsync(trackId, new("Inspect safely"), "worker-model-boundary", null, CancellationToken.None);
        await kernel.WaitForTrackIdleAsync(trackId);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId), s => s.BoundTaskId == dispatch.Task.Id);
        using var response = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{worker.Id}/model",
            new { model = "unreported-model", reasoningEffort = "unsupported-effort" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var unchanged = Assert.Single(await kernel.ListSessionsAsync(trackId), s => s.Id == worker.Id);
        Assert.Equal(worker.Model, unchanged.Model);
        Assert.Equal(worker.ReasoningEffort, unchanged.ReasoningEffort);
    }
}
