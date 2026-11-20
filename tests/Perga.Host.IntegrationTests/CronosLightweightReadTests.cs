using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Perga.Host.Configuration;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Kernel;

namespace Perga.Host.IntegrationTests;

public sealed class CronosLightweightReadTests
{
    [Fact]
    public async Task CatalogSchedulesAreNotExecutedWithoutMaterializedRows()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate("""
            export default {id:'unused', title:'Unused', schedules:[{id:'pulse', cron:'* * * * *', prompt:'unused'}]};
            """);
        using var scheduler = new CronosScheduleHostedService(kernel, new PergaHostOptions(), host.TimeProvider,
            NullLogger<CronosScheduleHostedService>.Instance);
        host.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, await scheduler.EvaluateDueSchedulesOnceAsync());
        Assert.Empty(host.AgentRuntime.ExecutedTurns);
    }
}
