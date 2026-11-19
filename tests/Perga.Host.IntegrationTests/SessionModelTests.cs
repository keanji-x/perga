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
                await kernel.SendMessageAsync(sessionId, new("sample", "before-model-switch"));
                var sampled = Assert.Single(await kernel.ListSessionsAsync(trackId));
                Assert.Equal(250, sampled.ContextUsedTokens);
                Assert.NotNull(sampled.ContextUsageAtMs);
                Assert.False(sampled.ContextUsageInvalidated);
                using var response = await host.Client.PutAsJsonAsync($"/api/v1/sessions/{sessionId}/model", new { model = "next-model" });
                response.EnsureSuccessStatusCode();
                var switched = await response.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions);
                Assert.True(switched!.ContextUsageInvalidated);
                Assert.Equal(250, switched.ContextUsedTokens);
                var compacted = await kernel.CompactSessionAsync(sessionId);
                Assert.Single(host.AgentRuntime.OpenedSessions);
                Assert.Equal("next-model", compacted.Model);
                Assert.Equal(0, compacted.ContextUsedTokens);
                Assert.Equal(0, compacted.ContextUsageRatio);
                Assert.Null(compacted.ContextUsageAtMs);
                Assert.False(compacted.ContextUsageInvalidated);
            }
            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var restored = Assert.Single(await reopened.Services.GetRequiredService<IPergaKernel>().ListSessionsAsync(trackId));
            Assert.Equal(sessionId, restored.Id);
            Assert.Equal("next-model", restored.Model);
            Assert.Equal(0, restored.ContextUsedTokens);
            Assert.Equal(0, restored.ContextUsageRatio);
            Assert.Null(restored.ContextUsageAtMs);
            Assert.False(restored.ContextUsageInvalidated);
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
        var turn = kernel.SendMessageAsync(sessionId, new("sample A", "failed-model-write"));
        try
        {
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(Assert.Single(await kernel.ListSessionsAsync(trackId)).ContextUsageInvalidated);
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
            Assert.False(unchanged.ContextUsageInvalidated);
        }
        finally { release.TrySetResult(); }
        await turn;
        var completed = Assert.Single(await kernel.ListSessionsAsync(trackId));
        Assert.Equal("A", completed.Model);
        Assert.Equal(noFinalUsage ? 300 : 400, completed.ContextUsedTokens);
        Assert.False(completed.ContextUsageInvalidated);
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
                Assert.True(session.ContextUsageInvalidated);
                using var missing = await host.Client.PutAsJsonAsync("/api/v1/sessions/missing/model", new { model = "chosen-model" });
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var kernel = reopened.Services.GetRequiredService<IPergaKernel>();
            Assert.Equal("chosen-model", (await kernel.ListSessionsAsync(trackId)).Single(s => s.Id == sessionId).Model);
            var restored = (await kernel.ListSessionsAsync(trackId)).Single(s => s.Id == sessionId);
            Assert.Equal("high", restored.ReasoningEffort);
            Assert.Equal("ask", restored.PermissionMode);
            Assert.True(restored.ContextUsageInvalidated);
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
                await restoredKernel.SendMessageAsync(sessionId, new SendMessageRequest("use default", "default-effort"));
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
