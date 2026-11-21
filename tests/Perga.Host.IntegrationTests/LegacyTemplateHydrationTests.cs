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
using static Perga.Host.IntegrationTests.McpKernelAndVcsIntegrationTests;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class LegacyTemplateHydrationTests
{
    [Fact]
    public async Task CatalogUpgradePreservesUnmarkedGuardsAndInheritedHookNames()
    {
        var directory = Directory.CreateTempSubdirectory("perga-custom-catalog-");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "a-legacy.perga.js"), """
                export default {
                  id: "legacy", title: "Existing catalog template",
                  get before() {
                    return call => call.toolName === ["vcs", "pr_merge"].join("_") ? Deny("Legacy guard") : Allow();
                  },
                  get after() {
                    return (event, ctx) => {
                      if (event.type === "tool.vcs_pr_checks") ctx.state.received = "legacy";
                    };
                  }
                };
                """);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "b-current.perga.js"), """
                // @ts-check
                export default {
                  id: "current", title: "Updated catalog template", extends: "legacy",
                  toolNameVersion: 2,
                  before: call => call.toolName === "pr_merge" ? Deny("Canonical guard") : Allow()
                };
                """);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "declarative.perga.json"), """
                {"id":"declarative","title":"JSON template","toolNameVersion":2}
                """);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "settings.json"), "{}");
            await using var host = new PergaHostTestCluster(configureHostOptions: options => options.TemplatesDirectory = directory.FullName);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var ct = deadline.Token;
            var kernel = host.Services.GetRequiredService<IPergaKernel>();
            Assert.False(kernel.Templates.GetRequiredPolicy("declarative").UsesLegacyBeforeToolNames);
            var bridge = host.Services.GetRequiredService<IMcpSessionBridge>();
            var area = await kernel.CreateAreaAsync(new("Custom catalog", DefaultCwd: host.DataDir), ct);

            foreach (var (template, guard) in new[] { ("legacy", "Legacy guard"), ("current", "Canonical guard") })
            {
                var track = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: template), ct)).Track;
                var token = bridge.RegisterSession(track.Id, track.LeadSessionId!, isLead: true, null, null, []).Principal.Token;
                var (merge, mergeError) = await CallToolAsync(host, "/mcp/vcs", token, "pr_merge", new { prNumber = 42, expectedHeadSha = "abc1234def5678" }, ct);
                Assert.True(mergeError, merge);
                Assert.Contains(guard, merge, StringComparison.Ordinal);
                var (checks, checksError) = await CallToolAsync(host, "/mcp/vcs", token, "pr_checks", new { prNumber = 42 }, ct);
                Assert.False(checksError, checks);
                var state = (await kernel.GetTrackAggregateAsync(track.Id, ct)).Track.TemplateState;
                Assert.Equal("legacy", state.GetProperty("received").GetString());

                var templateId = template == "legacy" ? "legacy" : "__current__";
                var (inspection, inspectError) = await CallToolAsync(host, "/mcp/kernel", token, "template_read", new { templateId }, ct);
                Assert.False(inspectError, inspection);
                using var templateDocument = JsonDocument.Parse(inspection);
                var result = templateDocument.RootElement;
                var source = result.GetProperty(templateId == "__current__" ? "snapshot" : "jsSource").GetString();
                Assert.True(result.TryGetProperty("editableSource", out _));
                var (applied, applyError) = await CallToolAsync(host, "/mcp/kernel", token, "track_set", new { source }, ct);
                Assert.False(applyError, applied);
                var (reappliedMerge, reappliedError) = await CallToolAsync(host, "/mcp/vcs", token, "pr_merge",
                    new { prNumber = 42, expectedHeadSha = "abc1234def5678" }, ct);
                Assert.True(reappliedError, reappliedMerge);
                Assert.Contains(guard, reappliedMerge, StringComparison.Ordinal);
            }
            Assert.Empty(host.ForgeService.MergedPrs);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalMcpCallsHonorFrozenLegacyPolicyAfterHostRestart(bool inherited)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var dataDir = Directory.CreateTempSubdirectory("perga-legacy-mcp-");
        try
        {
            const string legacySource = """
                const merge = "vcs_pr_merge";
                const close = "perga_track_close";
                export default defineTemplate({
                  id: "legacy-tools", title: "Legacy tools",
                  before(call, ctx) {
                    if (call.type === "tool.perga_task_ls" && call.toolName === "perga_task_ls") ctx.state.taskListBefore = true;
                    if (call.type === "tool." + merge && call.toolName === merge)
                      return Deny("Frozen merge guard");
                    if (call.type === "tool." + close && call.toolName === close)
                      return AskHuman("Frozen close guard");
                    return Allow();
                  },
                  after(event, ctx) {
                    if (event.type === "tool.perga_task_ls") ctx.state.taskListAfter = true;
                    if (event.type === ["tool.perga", "track", "status"].join("_")) ctx.state.pergaDelivered = true;
                    if (event.type === ["tool.vcs", "pr", "checks"].join("_")) ctx.state.vcsDelivered = true;
                  }
                });
                """;
            var legacySnapshot = inherited
                ? JsonSerializer.Serialize(new
                {
                    format = "perga.snapshot.v1",
                    sources = new[]
                    {
                        legacySource.Replace("\"vcs_pr_merge\"", """["vcs", "pr", "merge"].join("_")""", StringComparison.Ordinal)
                            .Replace("\"perga_track_close\"", """["perga", "track", "close"].join("_")""", StringComparison.Ordinal),
                        "export default {id:'legacy-tools',title:'Legacy tools'};"
                    }
                })
                : legacySource;
            string trackId;
            string leadId;
            string currentTrackId;
            string currentLeadId;
            await using (var initial = new PergaHostTestCluster(explicitDataDir: dataDir.FullName))
            {
                var kernel = initial.Services.GetRequiredService<IPergaKernel>();
                var area = await kernel.CreateAreaAsync(new("Legacy policies", DefaultCwd: dataDir.FullName), ct);
                var legacy = await kernel.CreateTrackAsync(new(area.Id), ct);
                trackId = legacy.Track.Id;
                leadId = legacy.Track.LeadSessionId!;
                await kernel.WaitForTrackIdleAsync(trackId, ct);

                kernel.Templates.RegisterTemplate("""
                    export default defineTemplate({
                      id: "current-tools", title: "Current tools", description: "vcs_pr_merge",
                      before(call) {
                        if (call.type === "tool.pr_merge" && call.toolName === "pr_merge")
                          return Deny("Current merge guard");
                        return Allow();
                      },
                      after(event, ctx) {
                        if (event.type === "tool.pr_checks") ctx.state.vcsDelivered = true;
                      }
                    });
                    """);
                var current = await kernel.CreateTrackAsync(new(area.Id, TemplateId: "current-tools"), ct);
                currentTrackId = current.Track.Id;
                currentLeadId = current.Track.LeadSessionId!;
                Assert.StartsWith("export default", current.TemplateSnapshotSource);
                await kernel.WaitForTrackIdleAsync(currentTrackId, ct);
            }
            await using (var db = new SqliteConnection($"Data Source={Path.Combine(dataDir.FullName, "perga.db")};Pooling=False"))
            {
                await db.OpenAsync(ct);
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE tracks SET template_id = 'legacy-tools', template_snapshot = $source WHERE id = $id";
                command.Parameters.AddWithValue("$source", legacySnapshot);
                command.Parameters.AddWithValue("$id", trackId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync(ct));
            }

            await using var restored = new PergaHostTestCluster(explicitDataDir: dataDir.FullName);
            var restoredKernel = restored.Services.GetRequiredService<IPergaKernel>();
            var bridge = restored.Services.GetRequiredService<IMcpSessionBridge>();
            var token = bridge.RegisterSession(trackId, leadId, isLead: true, null, null, []).Principal.Token;

            var kernelTools = await ListToolNamesAsync(restored, "/mcp/kernel", token, ct);
            var vcsTools = await ListToolNamesAsync(restored, "/mcp/vcs", token, ct);
            Assert.Contains("report_read", kernelTools);
            Assert.Contains("provider_list", kernelTools);
            Assert.Contains("task_list", kernelTools);
            Assert.Contains("status", vcsTools);
            Assert.DoesNotContain(kernelTools, name => name.StartsWith("perga_", StringComparison.Ordinal));
            Assert.DoesNotContain(vcsTools, name => name.StartsWith("vcs_", StringComparison.Ordinal));

            foreach (var (path, legacyTool) in new[]
            {
                ("/mcp/kernel", "perga_track_status"), ("/mcp/vcs", "vcs_pr_checks"), ("/mcp/kernel", "task_ls")
            })
            {
                var (unknown, isError) = await CallToolAsync(restored, path, token, legacyTool, new { prNumber = 42 }, ct);
                Assert.True(isError, unknown);
                Assert.Contains("Unknown", unknown, StringComparison.Ordinal);
                Assert.Contains(legacyTool, unknown, StringComparison.Ordinal);
            }

            var (status, statusError) = await CallToolAsync(restored, "/mcp/kernel", token, "track_status", new { }, ct);
            Assert.False(statusError, status);
            var (tasks, tasksError) = await CallToolAsync(restored, "/mcp/kernel", token, "task_list", new { }, ct);
            Assert.False(tasksError, tasks);
            var (checks, checksError) = await CallToolAsync(restored, "/mcp/vcs", token, "pr_checks", new { prNumber = 42 }, ct);
            Assert.False(checksError, checks);
            var track = await restoredKernel.GetTrackAggregateAsync(trackId, ct);
            Assert.Equal(legacySnapshot, track.TemplateSnapshotSource);
            var state = track.Track.TemplateState;
            Assert.True(state.GetProperty("taskListBefore").GetBoolean());
            Assert.True(state.GetProperty("taskListAfter").GetBoolean());
            Assert.True(state.GetProperty("pergaDelivered").GetBoolean());
            Assert.True(state.GetProperty("vcsDelivered").GetBoolean());

            var (merge, mergeError) = await CallToolAsync(restored, "/mcp/vcs", token, "pr_merge",
                new { prNumber = 42, expectedHeadSha = "abc1234def5678" }, ct);
            Assert.True(mergeError, merge);
            Assert.Contains("Frozen merge guard", merge, StringComparison.Ordinal);
            Assert.Empty(restored.ForgeService.MergedPrs);

            var close = CallToolAsync(restored, "/mcp/kernel", token, "track_close", new { }, ct);
            ApprovalDto? approval;
            do
            {
                approval = (await restoredKernel.GetTrackAggregateAsync(trackId, ct)).PendingApprovals.SingleOrDefault();
                if (approval is null)
                {
                    Assert.False(close.IsCompleted);
                    await Task.Delay(10, ct);
                }
            } while (approval is null);
            Assert.Equal("Frozen close guard", approval.Title);
            await restoredKernel.RespondInteractionAsync(leadId, approval.Id,
                new(ExecutionId: approval.ExecutionId, OptionId: "deny"), new InteropPrincipal.Human(), ct);
            var (denied, closeError) = await close;
            Assert.True(closeError, denied);
            Assert.Contains("denied by human", denied, StringComparison.OrdinalIgnoreCase);
            Assert.Null((await restoredKernel.GetTrackAggregateAsync(trackId, ct)).Track.ClosedAtMs);

            var currentToken = bridge.RegisterSession(currentTrackId, currentLeadId, isLead: true, null, null, []).Principal.Token;
            var (currentChecks, currentChecksError) = await CallToolAsync(restored, "/mcp/vcs", currentToken, "pr_checks", new { prNumber = 42 }, ct);
            Assert.False(currentChecksError, currentChecks);
            var currentState = (await restoredKernel.GetTrackAggregateAsync(currentTrackId, ct)).Track.TemplateState;
            Assert.True(currentState.GetProperty("vcsDelivered").GetBoolean());
            var (currentMerge, currentMergeError) = await CallToolAsync(restored, "/mcp/vcs", currentToken, "pr_merge",
                new { prNumber = 42, expectedHeadSha = "abc1234def5678" }, ct);
            Assert.True(currentMergeError, currentMerge);
            Assert.Contains("Current merge guard", currentMerge, StringComparison.Ordinal);
            Assert.Empty(restored.ForgeService.MergedPrs);
        }
        finally
        {
            dataDir.Delete(recursive: true);
        }
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
            const string snapshot = "export default {id:'invalid',title:'Invalid',guardToolCall:()=>Allow()};";
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
            using var inspect = await reopened.Client.GetAsync($"/api/v1/tracks/{trackId}/template");
            Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
            var current = await inspect.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(snapshot, current.GetProperty("snapshot").GetString());
            Assert.False(current.TryGetProperty("manifest", out _));
            using var clear = await reopened.Client.DeleteAsync($"/api/v1/tracks/{trackId}/template");
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
        var trackId = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "dev"))).Track.Id;
        using var clear = await host.Client.DeleteAsync($"/api/v1/tracks/{trackId}/template");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        Assert.Null((await kernel.GetTrackAggregateAsync(trackId)).Track.TemplateId);
        var closedId = (await kernel.CreateTrackAsync(new(area.Id, TemplateId: "dev"))).Track.Id;
        using var close = await host.Client.PatchAsJsonAsync($"/api/v1/tracks/{closedId}", new { closed = true });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        using var refused = await host.Client.DeleteAsync($"/api/v1/tracks/{closedId}/template");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("dev", (await kernel.GetTrackAggregateAsync(closedId)).Track.TemplateId);
    }

    [Fact]
    public async Task Restart_IsolatesLegacySnapshotFromSiblingListingAndSchedules()
    {
        var directory = Directory.CreateTempSubdirectory("perga-legacy-sibling-");
        try
        {
            string areaId, legacyTrackId, healthyTrackId;
            await using (var host = new PergaHostTestCluster(explicitDataDir: directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                kernel.Templates.RegisterTemplate("""
                    export default defineTemplate({ id:'healthy-sibling', title:'Healthy', schedules: [
                      { id:'pulse', cron:'* * * * *', timezone:'UTC', prompt:'HEALTHY_SIBLING_PULSE' }
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
                "export default {id:'legacy',title:'Legacy',guardToolCall:()=>Deny('legacy rule')};",
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
            using var response = await reopened.Client.GetAsync($"/api/v1/areas/{areaId}/tracks");
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
            Assert.Contains(reopened.AgentRuntime.ExecutedTurns,
                turn => turn.Input.Prompt.Contains("HEALTHY_SIBLING_PULSE", StringComparison.Ordinal));

            Assert.Equal(HttpStatusCode.OK, (await reopened.Client.GetAsync($"/api/v1/tracks/{legacyTrackId}")).StatusCode);
            var dispatchError = await Assert.ThrowsAsync<KernelException>(() =>
                restarted.CreateSessionAsync(legacyTrackId, new(Prompt: "Must not dispatch", Task: new()), "plain", new InteropPrincipal.Human(), default));
            Assert.Contains("guardToolCall", dispatchError.Message);
            var persisted = await reopened.Services.GetRequiredService<ITrackStore>().LoadTrackAggregateAsync(legacyTrackId, default);
            Assert.Empty(persisted!.Tasks);
            Assert.Empty(persisted.OpenAsks);
            Assert.Equal(snapshot, persisted.TemplateSnapshotSource);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("guardToolCall", "guardToolCall:()=>Deny('legacy rule')", false, false)]
    [InlineData("guardToolCall", "guardToolCall:()=>Deny('legacy rule')", true, false)]
    [InlineData("buildTurnContext", "buildTurnContext:()=> 'legacy context'", false, false)]
    [InlineData("buildTurnContext", "buildTurnContext:()=> 'legacy context'", true, false)]
    [InlineData("report.customValidate", "report:{customValidate:()=>Deny('legacy rule')}", false, false)]
    [InlineData("report.customValidate", "report:{customValidate:()=>Deny('legacy rule')}", true, false)]
    [InlineData("tasks.onSettled", "tasks:{onSettled:()=> 'legacy settlement'}", false, false)]
    [InlineData("tasks.onSettled", "tasks:{onSettled:()=> 'legacy settlement'}", true, false)]
    [InlineData("template.before", "before:42", false, false)]
    [InlineData("template.before", "before:42", true, false)]
    [InlineData("template.before", "before:42", false, true)]
    public async Task Restart_UnavailableSnapshotAllowsInspectionAndRecoveryButDeniesGovernedWork(
        string key, string member, bool clear, bool versioned)
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

            // Simulate a snapshot persisted by a release that accepted the removed hooks.
            var snapshot = JsonSerializer.Serialize(new { format = "perga.snapshot.v1", sources = new[]
            {
                "export default {id:'legacy',title:'Legacy'," + member + "};",
                "export default {id:'child',title:'Child',before:" + (key == "template.before" ? "42" : "()=>Allow()") + ",report:{},tasks:{}};"
            } });
            var storedSnapshot = versioned
                ? JsonSerializer.Serialize(new { format = "perga.snapshot.v2", toolNameVersion = 2, source = snapshot })
                : snapshot;
            await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, "perga.db")};Pooling=False"))
            {
                await db.OpenAsync();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE tracks SET template_id = 'child', template_snapshot = $source WHERE id = $id";
                command.Parameters.AddWithValue("$source", storedSnapshot);
                command.Parameters.AddWithValue("$id", trackId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            await using var reopened = new PergaHostTestCluster(explicitDataDir: directory.FullName);
            var restarted = reopened.Services.GetRequiredService<IPergaKernel>();
            var aggregate = await restarted.GetTrackAggregateAsync(trackId);
            Assert.Equal(snapshot, aggregate.TemplateSnapshotSource);
            using var inspect = await reopened.Client.GetAsync($"/api/v1/tracks/{trackId}/template");
            inspect.EnsureSuccessStatusCode();
            var current = await inspect.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(snapshot, current.GetProperty("snapshot").GetString());
            Assert.Equal(HttpStatusCode.OK, (await reopened.Client.GetAsync($"/api/v1/tracks/{trackId}")).StatusCode);
            var dispatchError = await Assert.ThrowsAsync<KernelException>(() =>
                restarted.CreateSessionAsync(trackId, new(Prompt: "Must not dispatch", Task: new()), "plain", new InteropPrincipal.Human(), default));
            Assert.Equal(KernelErrorCode.PolicyDenied, dispatchError.Code);
            Assert.Contains(key, dispatchError.Message);
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
            Assert.Equal(storedSnapshot, persisted.TemplateSnapshotSource);
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
