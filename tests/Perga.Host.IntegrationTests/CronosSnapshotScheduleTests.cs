using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
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
            export default defineTemplate({ id:'clashing', title:'Catalog', flow: [] });
            """);
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Schedules", DefaultCwd: cluster.DataDir));
        var snap = await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Cwd: cluster.DataDir));
        var caller = await kernel.OpenMcpCallerAsync(snap.Track.LeadSessionId!);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, caller, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = """
                export default defineTemplate({ id:'clashing', title:'Snapshot', flow: [
                  { id:'snapshot-pulse', when:{cron:'* * * * *',tz:'UTC'}, do:{kind:'wake',session:'lead',prompt:'SNAPSHOT_PULSE'} }
                ] });
                """ }));
        Assert.Equal(0, kernel.Templates.TryGetPolicy("clashing")!.Manifest.Flow.GetArrayLength());
        var scheduler = cluster.Services.GetRequiredService<CronosScheduleHostedService>();
        cluster.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        kernel.DispatchAdmission.SetDraining(true);
        Assert.Equal(0, await scheduler.EvaluateDueSchedulesOnceAsync());
        Assert.DoesNotContain(cluster.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt.Contains("SNAPSHOT_PULSE", StringComparison.Ordinal));
        kernel.DispatchAdmission.SetDraining(false);
        Assert.Equal(1, await scheduler.EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(snap.Track.Id);
        Assert.Contains(cluster.AgentRuntime.ExecutedTurns,
            turn => turn.Input.Prompt.Contains("SNAPSHOT_PULSE", StringComparison.Ordinal));
    }
}
