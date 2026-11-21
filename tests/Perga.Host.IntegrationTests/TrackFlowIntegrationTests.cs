using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Host.IntegrationTests.Fixtures;
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
                var views0 = await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/views");
                Assert.Equal(0, views0.GetProperty("flowRev").GetInt64());
                Assert.Equal(0, views0.GetProperty("flow").GetProperty("rev").GetInt64());
                Assert.Equal(0, views0.GetProperty("flow").GetProperty("nodes").GetArrayLength());

                // 2. GET /flow materializes flow.json and returns its path
                var flow0 = await host.Client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/flow");
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

                using var dryResp = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/flow/commit",
                    new { dryRun = true, expectedFlowRev = 0 });
                dryResp.EnsureSuccessStatusCode();
                var dryBody = await dryResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(dryBody.GetProperty("ok").GetBoolean());
                Assert.True(dryBody.GetProperty("dryRun").GetBoolean());
                Assert.Equal(0, dryBody.GetProperty("flowRev").GetInt64());
                Assert.Equal(2, dryBody.GetProperty("plan").GetProperty("added").GetArrayLength());

                // 4. Real commit via HTTP advances flowRev to 1
                using var commitResp = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/flow/commit",
                    new { dryRun = false, expectedFlowRev = 0 });
                commitResp.EnsureSuccessStatusCode();
                var commitBody = await commitResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(commitBody.GetProperty("ok").GetBoolean());
                Assert.Equal(1, commitBody.GetProperty("flowRev").GetInt64());
                Assert.Equal(2, commitBody.GetProperty("flow").GetProperty("nodes").GetArrayLength());

                // 5. Stale expectedFlowRev -> 409 Conflict
                using var staleResp = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/flow/commit",
                    new { dryRun = false, expectedFlowRev = 0 });
                Assert.Equal(HttpStatusCode.Conflict, staleResp.StatusCode);

                // 6. Cancel cron node via PUT /flow/nodes/{nodeId}/cancel
                using var cancelResp = await host.Client.PutAsync($"/api/v1/tracks/{trackId}/flow/nodes/hourly-check/cancel", null);
                cancelResp.EnsureSuccessStatusCode();
                var cancelBody = await cancelResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(cancelBody.GetProperty("ok").GetBoolean());
                Assert.Equal(2, cancelBody.GetProperty("flowRev").GetInt64());
                Assert.Equal("hourly-check", cancelBody.GetProperty("plan").GetProperty("canceled")[0].GetString());
            }

            // 7. Reopen the exact same SQLite DB in a fresh host and verify durable flow state!
            await using (var reopened = NewHost(tempDir.FullName))
            {
                var viewsAfterRestart = await reopened.Client.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{trackId}/views");
                Assert.Equal(2, viewsAfterRestart.GetProperty("flowRev").GetInt64());
                var nodes = viewsAfterRestart.GetProperty("flow").GetProperty("nodes");
                Assert.Equal(2, nodes.GetArrayLength());
                Assert.True(nodes[0].GetProperty("canceled").GetBoolean());
                Assert.Equal("canceled", nodes[0].GetProperty("status").GetString());
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
                var discovered = await store.FireDueAsync(due.ToUnixTimeMilliseconds(), _ => null, default);
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

    private static PergaHostTestCluster NewHost(string directory) =>
        new(explicitDataDir: directory, configureHostOptions: options =>
        {
            options.EnableScheduleService = false;
            options.EnableMaintenanceService = false;
        });
}
