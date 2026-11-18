using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class CronosSnapshotScheduleTests
{
    [Fact]
    public async Task EvaluateDueSchedules_PrefersPersistedSnapshotOverCatalogWithSameId()
    {
        await using var cluster = new PergaHostTestCluster(configureHostOptions: opts =>
        {
            opts.EnableScheduleService = false;
            opts.EnableMaintenanceService = false;
        });
        var kernel = cluster.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate("""
            export default defineTemplate({ id:'clashing', title:'Catalog', schedules: [] });
            """);
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Schedules", DefaultCwd: cluster.DataDir));
        var snap = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Cwd: cluster.DataDir));
        var caller = new McpSessionPrincipal("token", snap.Track.Id, snap.Track.LeadSessionId!, true, null, null, []);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, caller, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = """
                export default defineTemplate({ id:'clashing', title:'Snapshot', schedules: [
                  { id:'snapshot-pulse', cron:'* * * * *', timezone:'UTC', wakePrompt:'SNAPSHOT_PULSE' }
                ] });
                """ }));
        Assert.Empty(kernel.Templates.TryGetPolicy("clashing")!.Manifest.Schedules);
        var scheduler = cluster.Services.GetRequiredService<CronosScheduleHostedService>();
        cluster.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await scheduler.EvaluateDueSchedulesOnceAsync());
        Assert.Contains(cluster.AgentRuntime.ExecutedTurns,
            turn => turn.Input.Prompt.Contains("SNAPSHOT_PULSE", StringComparison.Ordinal));
    }
}
