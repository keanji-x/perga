using System.Net.Http.Json;
using System.Text.Json;
using Perga.Host.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Perga.Kernel;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class TemplatePresentationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("dev")]
    [InlineData("investment-research")]
    [InlineData("long-term-goal")]
    public async Task NewTracksHaveEmptyReports(string? templateId)
    {
        await using var host = new PergaHostTestCluster();
        using var areaResponse = await host.Client.PostAsJsonAsync("/api/v1/areas", new { name = "Presentation" });
        areaResponse.EnsureSuccessStatusCode();
        var area = await areaResponse.Content.ReadFromJsonAsync<JsonElement>();
        using var response = await host.Client.PostAsJsonAsync("/api/v1/tracks", new { areaId = area.GetProperty("id").GetString(), templateId });
        response.EnsureSuccessStatusCode();
        var snapshot = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(snapshot.GetProperty("report").GetProperty("blocks").EnumerateArray());
        var track = snapshot.GetProperty("track");
        var trackId = track.GetProperty("id").GetString();
        using var reportResponse = await host.Client.GetAsync($"/api/v1/tracks/{trackId}/report");
        var report = await reportResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("", report.GetProperty("renderedMarkdown").GetString());
        if (templateId is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(track.GetProperty("templateTitle").GetString()));
            Assert.Equal(JsonValueKind.Object, track.GetProperty("templateHeaderView").ValueKind);
            Assert.False(track.TryGetProperty("templateSchedules", out _));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("headerView() { throw new Error('broken'); },")]
    [InlineData("headerView() { return {metrics: 4}; },")]
    public async Task InvalidOrMissingHeaderDoesNotBreakTrackReads(string hook)
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate("export default defineTemplate({ id: 'broken-view', title: 'Still readable', " + hook + "});");
        var area = await kernel.CreateAreaAsync(new("Presentation", DefaultCwd: host.DataDir));
        var created = await kernel.CreateTrackAsync(new(area.Id, TemplateId: "broken-view"));
        foreach (var url in new[] { $"/api/v1/tracks/{created.Track.Id}", $"/api/v1/areas/{area.Id}/tracks" })
        {
            using var response = await host.Client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var track = json.ValueKind == JsonValueKind.Array ? json[0] : json.GetProperty("track");
            Assert.Equal("broken-view", track.GetProperty("templateId").GetString());
            Assert.True(!track.TryGetProperty("templateHeaderView", out var view) || view.ValueKind == JsonValueKind.Null);
            Assert.False(track.TryGetProperty("templateSchedules", out _));
        }
    }

    [Fact]
    public async Task RestProjectionUsesPersistedSnapshotAfterRestartAndDoesNotPersistHeaderStateChanges()
    {
        var dataDir = Directory.CreateTempSubdirectory("perga-presentation-").FullName;
        try
        {
            string trackId;
            string areaId;
            await using (var host = new PergaHostTestCluster(explicitDataDir: dataDir))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var area = await kernel.CreateAreaAsync(new("Snapshot", DefaultCwd: dataDir));
                areaId = area.Id;
                var created = await kernel.CreateTrackAsync(new(area.Id));
                trackId = created.Track.Id;
                var principal = new McpSessionPrincipal("token", trackId, created.Track.LeadSessionId!, true, null, null, []);
                await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
                    JsonSerializer.SerializeToElement(new { source = """
                        export default defineTemplate({
                          id: 'snapshot-view', title: 'Snapshot title',
                          inputs: { unit: { type: 'string', default: 'steps' } },
                          initialState: () => ({count: 7}),
                          headerView(ctx) {
                            const value = String(ctx.state.count) + ' ' + ctx.inputs.unit;
                            ctx.state.count++;
                            return {metrics: [{label: ctx.trackId, value}]};
                          },
                          schedules: [{id: 'snapshot-pulse', cron: '0 9 * * *', timezone: 'Asia/Shanghai', prompt: 'Check progress'}]
                        });
                        """, inputs = new { unit = "km" } }));
                await VerifyProjection(host, areaId, trackId);
            }
            await using (var host = new PergaHostTestCluster(explicitDataDir: dataDir))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                kernel.Templates.RegisterTemplate("export default defineTemplate({id:'snapshot-view', title:'Changed catalog', schedules:[]});");
                await VerifyProjection(host, areaId, trackId);
                using var response = await host.Client.PatchAsJsonAsync($"/api/v1/tracks/{trackId}", new { title = "Renamed" });
                response.EnsureSuccessStatusCode();
                var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("Snapshot title", updated.GetProperty("templateTitle").GetString());
                var persisted = await host.Services.GetRequiredService<ITrackStore>().LoadTrackAggregateAsync(trackId, default);
                Assert.Equal(7, persisted!.Track.TemplateState.GetProperty("count").GetInt32());
            }
        }
        finally { Directory.Delete(dataDir, recursive: true); }
    }

    private static async Task VerifyProjection(PergaHostTestCluster host, string areaId, string trackId)
    {
        foreach (var url in new[] { $"/api/v1/tracks/{trackId}" })
        {
            using var response = await host.Client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var track = json.ValueKind == JsonValueKind.Array ? json[0] : json.GetProperty("track");
            Assert.Equal("Snapshot title", track.GetProperty("templateTitle").GetString());
            var metric = Assert.Single(track.GetProperty("templateHeaderView").GetProperty("metrics").EnumerateArray());
            Assert.Equal(trackId, metric.GetProperty("label").GetString());
            Assert.Equal("7 km", metric.GetProperty("value").GetString());
            Assert.Equal(7, track.GetProperty("templateState").GetProperty("count").GetInt32());
        }
    }
}
