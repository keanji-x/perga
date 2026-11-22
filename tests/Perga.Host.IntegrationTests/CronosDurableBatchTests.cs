using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Kernel;

namespace Perga.Host.IntegrationTests;

public sealed class CronosDurableBatchTests
{
    [Fact]
    public async Task DueFlowClockPollContinuesPastTheFirstBatch()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: o => o.EnableScheduleService = false);
        var (trackId, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var nodes = Enumerable.Range(0, 65).Select(i => new FlowNodeSpecDto($"batch-{i:D2}",
            new("run", Cmd: "true", Access: "read_only"), When: new(At: "2026-11-17T12:00:00Z"))).ToArray();
        await FlowFixture.AddAsync(kernel, trackId, nodes);

        var scheduler = host.Services.GetRequiredService<CronosScheduleHostedService>();
        host.TimeProvider.SetUtcNow(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        Assert.Equal(65, await scheduler.EvaluateDueSchedulesOnceAsync());
        Assert.Equal(0, await scheduler.EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(trackId);
        Assert.All(await kernel.ListTasksAsync(trackId), task => Assert.Equal(1, task.RunCount));
    }
}
