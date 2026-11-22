using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Flow;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Host.IntegrationTests;

public sealed class McpTrackDiscoveryIntegrationTests
{
    [Fact]
    public async Task DailyTrackDiscoversItsSystemAreaWithNullArguments()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackReadStore>();
        var daily = (await kernel.GetDailyTrackAsync(date: null))!;
        var dailyTrack = (await store.GetTrackAsync(daily.TrackId, default))!;
        await kernel.WaitForTrackIdleAsync(daily.TrackId);
        var other = await host.CreateTrackAsync();
        var token = RegisterLead(host, daily.TrackId, dailyTrack.LeadSessionId!);

        var areas = await CallAsync(host, token, KernelMcpToolNames.AreaList, null);
        Assert.Equal(dailyTrack.AreaId, areas.GetProperty("currentAreaId").GetString());
        var dailyArea = Assert.Single(areas.GetProperty("areas").EnumerateArray(),
            area => area.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("Daily", dailyArea.GetProperty("name").GetString());
        Assert.Equal(1, dailyArea.GetProperty("openTrackCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, dailyArea.GetProperty("defaultCwd").ValueKind);
        Assert.True(JsonElement.DeepEquals(areas, await CallAsync(host, token, KernelMcpToolNames.AreaList, new { })));

        var current = await CallAsync(host, token, KernelMcpToolNames.TrackList, null);
        Assert.Equal("current_area", current.GetProperty("scope").GetString());
        Assert.Equal(dailyTrack.AreaId, current.GetProperty("area").GetProperty("id").GetString());
        var track = Assert.Single(current.GetProperty("tracks").EnumerateArray());
        Assert.Equal(daily.TrackId, track.GetProperty("id").GetString());
        Assert.True(track.GetProperty("isCurrent").GetBoolean());

        var explicitDaily = await CallAsync(host, token, KernelMcpToolNames.TrackList, new { area = dailyTrack.AreaId });
        Assert.True(JsonElement.DeepEquals(current.GetProperty("tracks"), explicitDaily.GetProperty("tracks")));
        var all = await CallAsync(host, token, KernelMcpToolNames.TrackList, new { area = "*" });
        Assert.Contains(all.GetProperty("tracks").EnumerateArray(), item => item.GetProperty("id").GetString() == other.TrackId);
        Assert.Contains(all.GetProperty("tracks").EnumerateArray(), item => item.GetProperty("id").GetString() == daily.TrackId);

        var report = await CallAsync(host, token, KernelMcpToolNames.ReportRead, null);
        Assert.True(report.TryGetProperty("anchor", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossTrackWidgetReadLeavesFlowFileAndSqliteUntouched(bool missingFlowFile)
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
        {
            options.EnableScheduleService = false;
            options.EnableMaintenanceService = false;
        });
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var caller = await host.CreateTrackAsync();
        var token = RegisterLead(host, caller.TrackId, caller.SessionId);
        var callerTrack = (await store.GetTrackAsync(caller.TrackId, default))!;
        kernel.Templates.RegisterTemplate("""
            export default defineTemplate({
              id: 'snapshot-widget', title: 'Snapshot widget',
              report: { views: { summary: (_, kernel) => ({ trackId: kernel.trackId }) } }
            });
            """);
        var target = (await kernel.CreateTrackAsync(new CreateTrackRequest(
            callerTrack.AreaId, Cwd: host.DataDir, TemplateId: "snapshot-widget"))).Track;
        await kernel.WaitForTrackIdleAsync(target.Id);
        var initial = await kernel.ReadReportAsync(target.Id, target.LeadSessionId);
        using var payload = JsonDocument.Parse("""{"use":"builtin:list","view":"summary"}""");
        await kernel.CommitReportAsync(target.Id, target.LeadSessionId!, new ReportCommitRequest(
            initial.Anchor, [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Widget, null, payload.RootElement)]));
        await kernel.Actors.RemoveAndDisposeAsync(target.Id);
        Assert.False(kernel.Actors.IsTrackActiveInMemory(target.Id));

        var flowPath = new TrackFlowFileStore(host.DataDir).GetFlowFilePath(target.Id);
        Assert.True(File.Exists(flowPath));
        const string staleFlow = """{"rev":0,"nodes":[]}""";
        if (missingFlowFile)
            File.Delete(flowPath);
        else
            await File.WriteAllTextAsync(flowPath, staleFlow);

        // A tracked stale hash would make Actor hydration reconcile the file and database.
        var storedHash = TrackFlowFileStore.ComputeSha256Hex(staleFlow);
        await store.RecordFlowFileHashAsync(target.Id, target.FlowRev, storedHash, default);
        Assert.Equal(storedHash, (await store.GetTrackAsync(target.Id, default))!.FlowFileSha256);
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(host.DataDir, "perga.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await db.OpenAsync();
        await using var version = db.CreateCommand();
        version.CommandText = "PRAGMA data_version";
        var before = await version.ExecuteScalarAsync();

        var result = await CallAsync(host, token, KernelMcpToolNames.ReportRead, new { trackId = target.Id, format = "full" });
        Assert.Equal(target.Id, result.GetProperty("templateViews").GetProperty("summary").GetProperty("trackId").GetString());
        Assert.False(result.TryGetProperty("anchor", out _));
        Assert.False(result.TryGetProperty("tasks", out _));
        Assert.False(kernel.Actors.IsTrackActiveInMemory(target.Id));
        Assert.Equal(before, await version.ExecuteScalarAsync());
        Assert.Equal(storedHash, (await store.GetTrackAsync(target.Id, default))!.FlowFileSha256);
        if (missingFlowFile)
            Assert.False(File.Exists(flowPath));
        else
            Assert.Equal(staleFlow, await File.ReadAllTextAsync(flowPath));
    }

    private static string RegisterLead(PergaHostTestCluster host, string trackId, string sessionId) =>
        host.Services.GetRequiredService<IMcpSessionBridge>()
            .RegisterSession(trackId, sessionId, isLead: true, null, null, []).Principal.Token;

    private static async Task<JsonElement> CallAsync(PergaHostTestCluster host, string token, string tool, object? args)
    {
        var (response, isError) = await McpKernelAndVcsIntegrationTests.CallToolAsync(host, "/mcp/kernel", token, tool, args, default);
        Assert.False(isError, response);
        using var doc = JsonDocument.Parse(response);
        return doc.RootElement.Clone();
    }
}
