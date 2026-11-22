using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Kernel.Mcp;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class TemplateHydrationTests
{
    [Fact]
    public async Task RestartRetainsFrozenCanonicalGuardAndReadsCurrentData()
    {
        var directory = Directory.CreateTempSubdirectory("perga-policy-snapshot-");
        try
        {
            string trackId;
            const string parent = """
                export default { id: 'base', title: 'Base', data: { initial: { frozen: true } },
                  before(action, ctx) {
                    if (action.type === 'tool.track_rename' && action.toolName === 'track_rename' && ctx.data.frozen)
                      return Deny('Frozen title');
                    return Allow();
                  } };
                """;
            await using (var host = new PergaHostTestCluster(explicitDataDir: directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                kernel.Templates.RegisterTemplate(parent);
                kernel.Templates.RegisterTemplate("export default { id: 'child', title: 'Child', extends: 'base' };");
                var area = await kernel.CreateAreaAsync(new("Snapshot", DefaultCwd: directory.FullName));
                trackId = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "child"))).Track.Id;
                await kernel.WaitForTrackIdleAsync(trackId);
            }

            await using var reopened = new PergaHostTestCluster(explicitDataDir: directory.FullName);
            var restored = reopened.Services.GetRequiredService<IPergaKernel>();
            restored.Templates.RegisterTemplate("export default { id: 'base', title: 'Updated base' };");
            var track = (await restored.GetTrackAggregateAsync(trackId)).Track;
            var caller = new McpSessionPrincipal("token", trackId, track.LeadSessionId!, true, null, null, []);
            var denied = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(
                restored, caller, KernelMcpToolNames.TrackRename, JsonSerializer.SerializeToElement(new { title = "Renamed" })));
            Assert.Equal(KernelErrorCode.PolicyDenied, denied.Code);
            Assert.Equal("Frozen title", denied.Message);
            Assert.Equal("Child", (await restored.GetTrackAggregateAsync(trackId)).Track.Title);

            var inspection = await reopened.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/template");
            Assert.True(inspection.GetProperty("data").GetProperty("frozen").GetBoolean());
            Assert.False(inspection.TryGetProperty("state", out _));
            Assert.False(inspection.TryGetProperty("editableSource", out _));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Restart_PendingTaskWaitsForHttpTemplateRecovery()
    {
        var directory = Directory.CreateTempSubdirectory("perga-pending-recovery-");
        try
        {
            string trackId;
            await using (var host = new PergaHostTestCluster(explicitDataDir: directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var area = await kernel.CreateAreaAsync(new("Recovery", DefaultCwd: directory.FullName));
                trackId = (await kernel.CreateTrackAsync(new(area.Id))).Track.Id;
                await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));
            }
            const string snapshot = "export default {id:'invalid',title:'Invalid',before:42};";
            await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, "perga.db")};Pooling=False"))
            {
                await db.OpenAsync();
                using var command = db.CreateCommand();
                command.CommandText = """
                    UPDATE tracks SET template_id = 'invalid', template_snapshot = $source WHERE id = $id;
                    INSERT INTO tasks (id, track_id, block_id, task_key, kind, access_mode, ready, goal_or_cmd, status, created_at_ms, updated_at_ms)
                    VALUES ('retained-task', $id, 'retained-block', 'retained', 'task', 'read_only', 1, 'Inspect retained task', 'pending', 1, 1);
                    """;
                command.Parameters.AddWithValue("$source", snapshot);
                command.Parameters.AddWithValue("$id", trackId);
                await command.ExecuteNonQueryAsync();
            }
            await using var reopened = new PergaHostTestCluster(explicitDataDir: directory.FullName);
            var restarted = reopened.Services.GetRequiredService<IPergaKernel>();
            await restarted.GetTrackAggregateAsync(trackId);
            await restarted.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));
            var store = reopened.Services.GetRequiredService<ITrackStore>();
            Assert.Equal(TaskRunStatus.Pending, Assert.Single((await store.LoadTrackAggregateAsync(trackId, default))!.Tasks).Status);
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
            using var inspect = await reopened.AuthenticatedClient.GetAsync($"/api/v1/tracks/{trackId}/template");
            Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
            var current = await inspect.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(snapshot, current.GetProperty("snapshot").GetString());
            Assert.False(current.TryGetProperty("manifest", out _));
            using var clear = await reopened.AuthenticatedClient.DeleteAsync($"/api/v1/tracks/{trackId}/template");
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            var detached = await clear.Content.ReadFromJsonAsync<TrackDto>(PergaJsonDefaults.ApiOptions);
            Assert.Null(detached!.TemplateId);
            await restarted.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));
            var recovered = (await store.LoadTrackAggregateAsync(trackId, default))!;
            Assert.Null(recovered.TemplateSnapshotSource);
            Assert.True(Assert.Single(recovered.Tasks).RunCount >= 1);
            Assert.Contains(reopened.AgentRuntime.ExecutedTurns, t => t.Input.Prompt.Contains("Inspect retained task", StringComparison.Ordinal));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task HttpTemplateClear_DetachesActiveTemplateAndRefusesClosedTrack()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new("Clear", DefaultCwd: host.DataDir));
        var trackId = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "agents-delivery"))).Track.Id;
        using var clear = await host.AuthenticatedClient.DeleteAsync($"/api/v1/tracks/{trackId}/template");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null((await kernel.GetTrackAggregateAsync(trackId)).Track.TemplateId);
        var closedId = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "agents-delivery"))).Track.Id;
        using var close = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{closedId}", new { closed = true });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        using var refused = await host.AuthenticatedClient.DeleteAsync($"/api/v1/tracks/{closedId}/template");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("agents-delivery", (await kernel.GetTrackAggregateAsync(closedId)).Track.TemplateId);
    }

    [Fact]
    public async Task Restart_IsolatesUnavailableSnapshotFromSiblingListingAndSchedules()
    {
        var directory = Directory.CreateTempSubdirectory("perga-legacy-sibling-");
        try
        {
            string areaId, legacyTrackId, healthyTrackId;
            await using (var host = new PergaHostTestCluster(explicitDataDir: directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                kernel.Templates.RegisterTemplate("""
                    export default defineTemplate({ id:'healthy-sibling', title:'Healthy', flow: [
                      { id:'pulse', when:{cron:'* * * * *',tz:'UTC'}, do:{kind:'wake',session:'lead',prompt:'HEALTHY_SIBLING_PULSE'} }
                    ] });
                    """);
                areaId = (await kernel.CreateAreaAsync(new("Legacy siblings", DefaultCwd: directory.FullName))).Id;
                legacyTrackId = (await kernel.CreateTrackAsync(new(areaId))).Track.Id;
                healthyTrackId = (await kernel.CreateTrackAsync(new(areaId, TemplateId: "healthy-sibling"))).Track.Id;
                await kernel.WaitForTrackIdleAsync(legacyTrackId).WaitAsync(TimeSpan.FromSeconds(10));
                await kernel.WaitForTrackIdleAsync(healthyTrackId).WaitAsync(TimeSpan.FromSeconds(10));
            }

            var snapshot = JsonSerializer.Serialize(new { format = "perga.snapshot.v1", sources = new[]
            {
                "export default {id:'legacy',title:'Legacy',headerView:42};",
                "export default {id:'child',title:'Child',before:()=>Allow()};"
            } });
            await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, "perga.db")};Pooling=False"))
            {
                await db.OpenAsync();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE tracks SET template_id = 'child', template_snapshot = $source WHERE id = $id";
                command.Parameters.AddWithValue("$source", snapshot);
                command.Parameters.AddWithValue("$id", legacyTrackId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await using var reopened = new PergaHostTestCluster(explicitDataDir: directory.FullName);
            var restarted = reopened.Services.GetRequiredService<IPergaKernel>();
            using var response = await reopened.AuthenticatedClient.GetAsync($"/api/v1/areas/{areaId}/tracks");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var areaTracks = await response.Content.ReadFromJsonAsync<TrackDto[]>(PergaJsonDefaults.ApiOptions);
            Assert.Equal(2, areaTracks!.Length);
            var legacy = Assert.Single(areaTracks, t => t.Id == legacyTrackId);
            Assert.Null(legacy.TemplateHeaderView);
            Assert.Contains(areaTracks, t => t.Id == healthyTrackId);
            var allTracks = await restarted.ListTracksAsync();
            Assert.Equal(2, allTracks.Count);
            Assert.Contains(allTracks, t => t.Id == legacyTrackId);
            Assert.Contains(allTracks, t => t.Id == healthyTrackId);

            var scheduler = reopened.Services.GetRequiredService<CronosScheduleHostedService>();
            reopened.TimeProvider.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(1, await scheduler.EvaluateDueSchedulesOnceAsync());
            Assert.Equal(1, scheduler.TriggeredWakeCount);
            await restarted.WaitForTrackIdleAsync(healthyTrackId);
            Assert.Contains(reopened.AgentRuntime.ExecutedTurns,
                turn => turn.Input.Prompt.Contains("HEALTHY_SIBLING_PULSE", StringComparison.Ordinal));

            Assert.Equal(HttpStatusCode.OK, (await reopened.AuthenticatedClient.GetAsync($"/api/v1/tracks/{legacyTrackId}")).StatusCode);
            var dispatchError = await Assert.ThrowsAsync<KernelException>(() =>
                FlowFixture.AppendAsync(restarted, legacyTrackId, new FlowNodeSpecDto("blocked", new("run", Prompt: "Must not dispatch"))));
            Assert.Contains("headerView", dispatchError.Message);
            var persisted = await reopened.Services.GetRequiredService<ITrackStore>().LoadTrackAggregateAsync(legacyTrackId, default);
            Assert.Empty(persisted!.Tasks);
            Assert.Empty(persisted.OpenAsks);
            Assert.Equal(snapshot, persisted.TemplateSnapshotSource);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_UnavailableSnapshotAllowsInspectionAndRecoveryButDeniesGovernedWork(bool clear)
    {
        var directory = Directory.CreateTempSubdirectory("perga-legacy-hooks-");
        try
        {
            string trackId;
            await using (var host = new PergaHostTestCluster(explicitDataDir: directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var area = await kernel.CreateAreaAsync(new("Legacy snapshot", DefaultCwd: directory.FullName));
                trackId = (await kernel.CreateTrackAsync(new(area.Id))).Track.Id;
                await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));
            }

            const string snapshot = "export default { id: 'child', title: 'Child', before: 42 };";
            await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, "perga.db")};Pooling=False"))
            {
                await db.OpenAsync();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE tracks SET template_id = 'child', template_snapshot = $source WHERE id = $id";
                command.Parameters.AddWithValue("$source", snapshot);
                command.Parameters.AddWithValue("$id", trackId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await using var reopened = new PergaHostTestCluster(explicitDataDir: directory.FullName);
            var restarted = reopened.Services.GetRequiredService<IPergaKernel>();
            var aggregate = await restarted.GetTrackAggregateAsync(trackId);
            Assert.Equal(snapshot, aggregate.TemplateSnapshotSource);
            using var inspect = await reopened.AuthenticatedClient.GetAsync($"/api/v1/tracks/{trackId}/template");
            inspect.EnsureSuccessStatusCode();
            var current = await inspect.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(snapshot, current.GetProperty("snapshot").GetString());
            Assert.Equal(HttpStatusCode.OK, (await reopened.AuthenticatedClient.GetAsync($"/api/v1/tracks/{trackId}")).StatusCode);
            var dispatchError = await Assert.ThrowsAsync<KernelException>(() =>
                FlowFixture.AppendAsync(restarted, trackId, new FlowNodeSpecDto("blocked", new("run", Prompt: "Must not dispatch"))));
            Assert.Equal(KernelErrorCode.PolicyDenied, dispatchError.Code);
            Assert.Contains("template.before", dispatchError.Message);
            var caller = new McpSessionPrincipal("token", trackId, aggregate.Track.LeadSessionId!, true, null, null, []);
            var commandError = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(restarted,
                caller, KernelMcpToolNames.TrackRename, JsonSerializer.SerializeToElement(new { title = "Forbidden rename" })));
            Assert.Equal(KernelErrorCode.PolicyDenied, commandError.Code);
            await restarted.SendSessionMessageAsync(caller.SessionId, new(Content: "Must not reach provider", RequestKey: "blocked-turn"), new InteropPrincipal.Human(), default);
            await restarted.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
            var persisted = await reopened.Services.GetRequiredService<ITrackStore>().LoadTrackAggregateAsync(trackId, default);
            Assert.Empty(persisted!.Tasks);
            Assert.Empty(persisted.OpenAsks);
            Assert.Equal(snapshot, persisted.TemplateSnapshotSource);
            // Clear and replacement each work directly against the unavailable snapshot.
            await KernelMcpDispatcher.ExecuteGuardedAsync(restarted, caller, KernelMcpToolNames.TrackSet,
                clear ? JsonSerializer.SerializeToElement(new { clear = true }) :
                    JsonSerializer.SerializeToElement(new { source = "export default {id:'recovered',title:'Recovered'};" }));
            Assert.Equal(clear ? null : "recovered", (await restarted.GetTrackAggregateAsync(trackId)).Track.TemplateId);
            await KernelMcpDispatcher.ExecuteGuardedAsync(restarted, caller, KernelMcpToolNames.TrackRename,
                JsonSerializer.SerializeToElement(new { title = "Recovered track" }));
            Assert.Equal("Recovered track", (await restarted.GetTrackAggregateAsync(trackId)).Track.Title);
        }
        finally { directory.Delete(recursive: true); }
    }
}
