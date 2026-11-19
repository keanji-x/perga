using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Perga.Host.Configuration;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class CronosLightweightReadTests
{
    [Fact]
    public async Task EachTickReadsOpenScheduleSourcesOnceWithoutLoadingAggregates()
    {
        await using var host = new PergaHostTestCluster();
        var (openTrack, _) = await host.CreateTrackAsync();
        var (closedTrack, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.PatchTrackAsync(closedTrack, new(Closed: true));
        var store = host.Services.GetRequiredService<ITrackReadStore>();
        var source = Assert.Single(await store.ListOpenTrackScheduleSourcesAsync(default));
        var aggregate = await store.LoadTrackAggregateAsync(openTrack, default);
        Assert.Equal(openTrack, source.TrackId);
        Assert.Equal(aggregate!.Track.TemplateId, source.TemplateId);
        Assert.Equal(aggregate.TemplateSnapshotSource, source.TemplateSnapshotSource);
        Assert.Equal(aggregate.Track.CreatedAtMs, source.CreatedAtMs);

        var observed = DispatchProxy.Create<ITrackReadStore, ScheduleReadProxy>();
        var proxy = (ScheduleReadProxy)(object)observed;
        proxy.Inner = store;
        using var scheduler = new CronosScheduleHostedService(kernel, observed, new PergaHostOptions(), host.TimeProvider,
            NullLogger<CronosScheduleHostedService>.Instance);
        await scheduler.EvaluateDueSchedulesOnceAsync();
        Assert.Equal(1, proxy.SourceReads);
        await scheduler.EvaluateDueSchedulesOnceAsync();
        Assert.Equal(2, proxy.SourceReads);
    }

    public class ScheduleReadProxy : DispatchProxy
    {
        public ITrackReadStore Inner = null!;
        public int SourceReads;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ITrackReadStore.ListOpenTrackScheduleSourcesAsync)) SourceReads++;
            else throw new InvalidOperationException($"Cronos must not call {targetMethod.Name}.");
            return targetMethod.Invoke(Inner, args);
        }
    }
}
