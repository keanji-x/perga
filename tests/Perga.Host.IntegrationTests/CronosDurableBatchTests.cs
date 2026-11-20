using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class CronosDurableBatchTests
{
    [Fact]
    public async Task PokesEachCommittedBatchBeforeReadingTheNextBatch()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: o => o.EnableScheduleService = false);
        var (trackId, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId);
        for (var i = 0; i < 65; i++)
            await kernel.SetTrackScheduleAsync(trackId, new("Batch", "BATCH_PROMPT", Id: $"batch_{i:D2}", RunAtMs: 1));
        var store = host.Services.GetRequiredService<ITrackStore>().TrackSchedules!;
        var observed = DispatchProxy.Create<ITrackScheduleStore, ScheduleProxy>();
        var proxy = (ScheduleProxy)(object)observed;
        proxy.Inner = store;
        proxy.BeforeFire = calls =>
        {
            if (calls == 2)
                Assert.Contains(host.AgentRuntime.ExecutedTurns, t => t.Input.Prompt.Contains("BATCH_PROMPT", StringComparison.Ordinal));
        };
        using var scheduler = new CronosScheduleHostedService(kernel, host.Services.GetRequiredService<ITrackReadStore>(),
            new PergaHostOptions(), host.TimeProvider, NullLogger<CronosScheduleHostedService>.Instance, observed);
        Assert.Equal(65, await scheduler.EvaluateDueSchedulesOnceAsync());
        Assert.Equal(2, proxy.FireCalls);
        await kernel.WaitForTrackIdleAsync(trackId);
    }

    [Fact]
    public async Task RetriesFailedPokeOnNextTickEvenWhenNoScheduleIsDue()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: o => o.EnableScheduleService = false);
        var (trackId, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.WaitForTrackIdleAsync(trackId);
        await kernel.SetTrackScheduleAsync(trackId, new("Retry", "RETRY_PROMPT", RunAtMs: 1));
        var observed = DispatchProxy.Create<IPergaKernel, KernelProxy>();
        var proxy = (KernelProxy)(object)observed;
        proxy.Inner = kernel;
        using var scheduler = new CronosScheduleHostedService(observed, host.Services.GetRequiredService<ITrackReadStore>(),
            new PergaHostOptions(), host.TimeProvider, NullLogger<CronosScheduleHostedService>.Instance,
            host.Services.GetRequiredService<ITrackStore>().TrackSchedules!);
        Assert.Equal(1, await scheduler.EvaluateDueSchedulesOnceAsync());
        Assert.Equal(1, proxy.PokeAttempts);
        Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, t => t.Input.Prompt.Contains("RETRY_PROMPT", StringComparison.Ordinal));
        Assert.Equal(0, await scheduler.EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(trackId);
        Assert.Equal(2, proxy.PokeAttempts);
        Assert.Single(host.AgentRuntime.ExecutedTurns, t => t.Input.Prompt.Contains("RETRY_PROMPT", StringComparison.Ordinal));
        Assert.Equal(0, await scheduler.EvaluateDueSchedulesOnceAsync());
        Assert.Equal(2, proxy.PokeAttempts);
    }

    public class ScheduleProxy : DispatchProxy
    {
        public ITrackScheduleStore Inner = null!;
        public Action<int>? BeforeFire;
        public int FireCalls;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ITrackScheduleStore.FireDueAsync)) BeforeFire?.Invoke(++FireCalls);
            return targetMethod.Invoke(Inner, args);
        }
    }

    public class KernelProxy : DispatchProxy
    {
        public IPergaKernel Inner = null!;
        public int PokeAttempts;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_Actors" && ++PokeAttempts == 1)
                throw new InvalidOperationException("Injected transient poke failure");
            return targetMethod.Invoke(Inner, args);
        }
    }
}
