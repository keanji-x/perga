using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class TrackFlowIntegrationTests
{
    [Fact]
    public async Task FlowEndpointsAndViews_WorkOnTemplateLessTrack_AndPersistAcrossHostRestart()
    {
        var tempDir = Directory.CreateTempSubdirectory("perga-flow-it-");
        try
        {
            string trackId;
            string flowFilePath;

            await using (var host = NewHost(tempDir.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var area = await kernel.CreateAreaAsync(new("Flow Area", DefaultCwd: host.DataDir));
                var created = await kernel.CreateTrackAsync(new(area.Id, Title: "Flow Track"));
                trackId = created.Track.Id;
                await kernel.Actors.GetOrActivate(trackId).EnsureMountedAsync();

                // 1. GET /views works even without an active template and returns flowRev=0 + flow projection
                var views0 = await host.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/views");
                Assert.Equal(0, views0.GetProperty("flowRev").GetInt64());
                Assert.Equal(0, views0.GetProperty("flow").GetProperty("rev").GetInt64());
                Assert.Equal(0, views0.GetProperty("flow").GetProperty("nodes").GetArrayLength());

                // 2. GET /flow materializes flow.json and returns its path
                var flow0 = await host.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/flow");
                Assert.Equal(0, flow0.GetProperty("flowRev").GetInt64());
                flowFilePath = flow0.GetProperty("filePath").GetString()!;
                Assert.True(File.Exists(flowFilePath));

                // 3. Edit flow.json with a cron rule + downstream node and dryRun commit via HTTP
                await File.WriteAllTextAsync(flowFilePath, """
                    {
                      "rev": 0,
                      "nodes": [
                        {
                          "id": "hourly-check",
                          "title": "Hourly Check",
                          "when": { "cron": "0 * * * *", "tz": "UTC" },
                          "do": { "kind": "run", "cmd": "echo ok" }
                        },
                        {
                          "id": "triage",
                          "title": "Triage Failure",
                          "when": { "after": ["hourly-check"], "on": "attention" },
                          "do": { "kind": "run", "cmd": "echo triage" }
                        }
                      ]
                    }
                    """);

                using var dryResp = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/tracks/{trackId}/flow/commit",
                    new { dryRun = true, expectedFlowRev = 0 });
                dryResp.EnsureSuccessStatusCode();
                var dryBody = await dryResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(dryBody.GetProperty("ok").GetBoolean());
                Assert.True(dryBody.GetProperty("dryRun").GetBoolean());
                Assert.Equal(0, dryBody.GetProperty("flowRev").GetInt64());
                Assert.Equal(2, dryBody.GetProperty("plan").GetProperty("added").GetArrayLength());

                // 4. Real commit via HTTP advances flowRev to 1
                using var commitResp = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/tracks/{trackId}/flow/commit",
                    new { dryRun = false, expectedFlowRev = 0 });
                commitResp.EnsureSuccessStatusCode();
                var commitBody = await commitResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(commitBody.GetProperty("ok").GetBoolean());
                Assert.Equal(1, commitBody.GetProperty("flowRev").GetInt64());
                Assert.Equal(2, commitBody.GetProperty("flow").GetProperty("nodes").GetArrayLength());

                // 5. Stale expectedFlowRev -> 409 Conflict
                using var staleResp = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/tracks/{trackId}/flow/commit",
                    new { dryRun = false, expectedFlowRev = 0 });
                Assert.Equal(HttpStatusCode.Conflict, staleResp.StatusCode);

                // 6. Cancel cron node explicitly via flow.json canceled=true + POST /flow/commit
                await File.WriteAllTextAsync(flowFilePath, """
                    {
                      "rev": 1,
                      "nodes": [
                        {
                          "id": "hourly-check",
                          "title": "Hourly Check",
                          "when": { "cron": "0 * * * *", "tz": "UTC" },
                          "do": { "kind": "run", "cmd": "echo ok" },
                          "canceled": true
                        },
                        {
                          "id": "triage",
                          "title": "Triage Failure",
                          "when": { "after": ["hourly-check"], "on": "attention" },
                          "do": { "kind": "run", "cmd": "echo triage" }
                        }
                      ]
                    }
                    """);
                using var cancelResp = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/tracks/{trackId}/flow/commit",
                    new { dryRun = false });
                cancelResp.EnsureSuccessStatusCode();
                var cancelBody = await cancelResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(cancelBody.GetProperty("ok").GetBoolean());
                Assert.Equal(2, cancelBody.GetProperty("flowRev").GetInt64());
                Assert.Equal("hourly-check", cancelBody.GetProperty("plan").GetProperty("canceled")[0].GetString());
                await kernel.WaitForTrackIdleAsync(trackId);
            }

            // 7. Reopen the exact same SQLite DB in a fresh host and verify durable flow state!
            // Background GC archives the canceled cron node and its now-unreachable downstream branch and advances flowRev.
            await using (var reopened = NewHost(tempDir.FullName))
            {
                var viewsAfterRestart = await reopened.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/views");
                Assert.Equal(3, viewsAfterRestart.GetProperty("flowRev").GetInt64());
                var flow = viewsAfterRestart.GetProperty("flow");
                Assert.Equal(0, flow.GetProperty("nodes").GetArrayLength());
                var archived = flow.GetProperty("archived");
                Assert.Equal(2, archived.GetArrayLength());
                Assert.All(archived.EnumerateArray(), item => Assert.Equal("canceled", item.GetProperty("status").GetString()));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir.FullName))
                Directory.Delete(tempDir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task DueFlowClocksSurviveRestartAndAnUnchangedCommitPreservesOccurrenceLimit()
    {
        var directory = Directory.CreateTempSubdirectory("perga-flow-clock-");
        try
        {
            string trackId;
            string flowPath;
            var due = new DateTimeOffset(2026, 11, 17, 12, 5, 0, TimeSpan.Zero);
            await using (var host = NewHost(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var area = await kernel.CreateAreaAsync(new("Clocks", DefaultCwd: host.DataDir));
                trackId = (await kernel.CreateTrackAsync(new(area.Id, Title: "Clocks"))).Track.Id;
                await kernel.Actors.GetOrActivate(trackId).EnsureMountedAsync();
                flowPath = (await kernel.GetTrackFlowAsync(trackId)).FilePath!;
                await File.WriteAllTextAsync(flowPath, """
                    {"rev":0,"nodes":[
                      {"id":"once","when":{"at":"2026-11-17T12:05:00Z"},"do":{"kind":"run","cmd":"true"}},
                      {"id":"limited","when":{"cron":"5 * * * *","tz":"UTC","maxOccurrences":1},"do":{"kind":"run","cmd":"true"}}
                    ]}
                    """);
                Assert.True((await kernel.CommitTrackFlowAsync(trackId, false, 0, new InteropPrincipal.Human())).Ok);

                // Simulate a process ending after looking up due schedules but before an actor consumes them.
                var store = host.Services.GetRequiredService<ITrackScheduleStore>();
                var discovered = await store.ListDueFlowClocksAsync(due.ToUnixTimeMilliseconds(), default);
                Assert.Equal(2, discovered.Count);
                Assert.All(discovered, item => Assert.Equal(due.ToUnixTimeMilliseconds(), item.DueAtMs));
            }

            await using var reopened = NewHost(directory.FullName);
            reopened.TimeProvider.SetUtcNow(due);
            var restarted = reopened.Services.GetRequiredService<IPergaKernel>();
            Assert.Equal(2, (await restarted.FireDueTrackSchedulesAsync(due.ToUnixTimeMilliseconds())).Count);
            await restarted.WaitForTrackIdleAsync(trackId);
            var flow = await restarted.GetTrackFlowAsync(trackId);
            Assert.Equal("ok", Assert.Single(flow.Projection.Archived, node => node.Id == "once").Outcome);
            var limited = Assert.Single(flow.Projection.Nodes);
            Assert.Equal("limited", limited.Id);
            Assert.Equal(1, limited.RunCount);
            Assert.Equal(1, limited.FiredCount);
            Assert.Null(limited.NextFireAtMs);

            Assert.True((await restarted.CommitTrackFlowAsync(trackId, false, flow.FlowRev, new InteropPrincipal.Human())).Ok);
            reopened.TimeProvider.Advance(TimeSpan.FromHours(2));
            Assert.Empty(await restarted.FireDueTrackSchedulesAsync(reopened.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
            Assert.Equal(1, Assert.Single((await restarted.GetTrackFlowAsync(trackId)).Projection.Nodes).RunCount);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReopenedHostDeliversOriginalSettlementWakeAfterLeadReconnects()
    {
        var directory = Directory.CreateTempSubdirectory("perga-flow-wake-recovery-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var human = new InteropPrincipal.Human();
        const string explicitPrompt = "Explicit review update";
        const string interruptedTurnId = "turn_explicit_before_delivery";
        const string leadThread = "thr_interrupted_lead";
        try
        {
            string trackId;
            string leadId;
            TrackWake pending;
            await using (var host = NewHost(directory.FullName))
            {
                (trackId, leadId) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var store = host.Services.GetRequiredService<ITrackStore>();
                var review = await FlowFixture.StartWorkerAsync(kernel, trackId, "review", "Review", ct);
                await FlowFixture.AddAsync(kernel, trackId,
                    FlowFixture.Wake("notify", explicitPrompt, new(After: [new("review", "any")])));
                await kernel.WaitForTrackIdleAsync(trackId, ct);
                var snapshot = (await store.LoadTrackAggregateAsync(trackId, ct))!;
                var notify = Assert.Single(snapshot.Tasks, task => task.TaskKey == "notify");
                var lead = Assert.Single(snapshot.Sessions, session => session.Id == leadId);
                await kernel.Actors.RemoveAndDisposeAsync(trackId);

                // Persist the state left by a process exit before the explicit prompt reached the provider.
                var now = host.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                var settled = review.Task with
                {
                    Status = TaskRunStatus.Done, Outcome = FlowOutcome.Attention, FinishedAtMs = now, UpdatedAtMs = now
                };
                await store.SettleTaskAsync(settled, null, ct);
                await store.UpsertTaskAsync(notify with
                {
                    Status = TaskRunStatus.Running, Ready = true, SessionId = leadId, RunCount = 1,
                    TriggerRunKey = $"review#{settled.RunCount}", StartedAtMs = now, UpdatedAtMs = now
                }, null, ct);
                var turn = new SessionTurnDto(interruptedTurnId, leadId, 1, lead.Epoch, "wake",
                    JsonSerializer.SerializeToElement(new { prompt = explicitPrompt }), "running", null, null, now, null);
                await store.RecordTurnAndItemsAsync(turn, [], lead with { Status = "running", NativeThreadId = leadThread }, ct);
                pending = Assert.Single(await store.TrackWakes.ListAsync(trackId, ct),
                    row => row.CoalesceKey == TrackWakeRequests.TaskSettled(settled).CoalesceKey);
                Assert.Equal(TrackWakeStates.Pending, pending.State);
                Assert.Equal(0, pending.Attempts);
            }

            await using var reopened = NewHost(directory.FullName);
            var restarted = reopened.Services.GetRequiredService<IPergaKernel>();
            var reopenedStore = reopened.Services.GetRequiredService<ITrackStore>();
            var boot = reopened.Services.GetRequiredService<PergaBootHostedService>();
            Assert.Contains(trackId, boot.LastReconcileResult!.AffectedTrackIds);
            var blocked = await restarted.GetSessionItemsAsync(leadId, new(), human, ct);
            Assert.Equal("unknown", blocked.State);
            Assert.Equal(interruptedTurnId, blocked.LastTurn?.Id);
            Assert.Equal("unknown", blocked.LastTurn?.Status);
            Assert.Equal("host_restarted", blocked.UnknownDetail?.Reason);
            Assert.True(blocked.UnknownDetail?.CanReconnect);
            var actor = restarted.Actors.GetOrActivate(trackId);
            await actor.EnsureMountedAsync(ct);
            await actor.PokePendingWakesAsync(ct);
            await restarted.WaitForTrackIdleAsync(trackId, ct);
            Assert.Equal(pending, Assert.Single(await reopenedStore.TrackWakes.ListAsync(trackId, ct), row => row.Id == pending.Id));
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
            var recoveredTask = Assert.Single((await reopenedStore.LoadTrackAggregateAsync(trackId, ct))!.Tasks,
                task => task.TaskKey == "notify");
            Assert.Equal(TaskRunStatus.Failed, recoveredTask.Status);
            Assert.Equal(FlowOutcome.Blocked, recoveredTask.Outcome);

            await restarted.ControlSessionAsync(leadId, new("reconnect"), human, ct);
            await restarted.WaitForTrackIdleAsync(trackId, ct);
            var delivered = Assert.Single(await reopenedStore.TrackWakes.ListAsync(trackId, ct),
                row => row.CoalesceKey == pending.CoalesceKey);
            Assert.Equal(pending.Id, delivered.Id);
            Assert.Equal(TrackWakeStates.Done, delivered.State);
            Assert.Equal(TrackWakeReasons.Delivered, delivered.Reason);
            Assert.Equal(1, delivered.Attempts);
            var execution = Assert.Single(reopened.AgentRuntime.ExecutedTurns);
            Assert.Equal(leadId, execution.Spec.StateKey);
            Assert.Equal(leadThread, execution.Spec.ResumeNativeThreadId);
            Assert.Contains("Task 'review' (run #1) settled", execution.Input.Prompt);
            Assert.DoesNotContain(explicitPrompt, execution.Input.Prompt);
            var completed = (await restarted.GetSessionItemsAsync(leadId, new(), human, ct)).LastTurn;
            Assert.Equal("completed", completed?.Status);
            Assert.Equal(completed?.Id, delivered.TurnId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static PergaHostTestCluster NewHost(string directory) =>
        new(explicitDataDir: directory, configureHostOptions: options =>
        {
            options.EnableScheduleService = false;
            options.EnableMaintenanceService = false;
        });
}
