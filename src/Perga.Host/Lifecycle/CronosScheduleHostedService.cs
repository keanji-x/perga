using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.Lifecycle;

public sealed class CronosScheduleHostedService : BackgroundService
{
    private readonly IPergaKernel _kernel;
    private readonly PergaHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CronosScheduleHostedService> _logger;
    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private readonly HashSet<string> _tracksNeedingPoke = new(StringComparer.Ordinal);
    private long _triggeredWakeCount;

    [ActivatorUtilitiesConstructor]
    public CronosScheduleHostedService(
        IPergaKernel kernel,
        PergaHostOptions options,
        TimeProvider timeProvider,
        ILogger<CronosScheduleHostedService> logger)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public long TriggeredWakeCount => Interlocked.Read(ref _triggeredWakeCount);

    public async Task<int> EvaluateDueSchedulesOnceAsync(CancellationToken ct = default)
    {
        await _evalLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_kernel.DispatchAdmission.Draining) return 0;
            return await FireDueDurableSchedulesAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), ct).ConfigureAwait(false);
        }
        finally
        {
            _evalLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableScheduleService)
        {
            return;
        }

        var pollDelay = _options.SchedulePollInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(50)
            : _options.SchedulePollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollDelay, stoppingToken).ConfigureAwait(false);
                await EvaluateDueSchedulesOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cronos schedule evaluation pass encountered an error.");
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _evalLock.Dispose();
    }

    private async Task<int> FireDueDurableSchedulesAsync(long nowMs, CancellationToken ct)
    {
        var firedCount = 0;
        await PokePendingTracksAsync(ct).ConfigureAwait(false);
        while (!_kernel.DispatchAdmission.Draining)
        {
            var batch = await _kernel.FireDueTrackSchedulesAsync(nowMs, ct).ConfigureAwait(false);

            if (batch.Count == 0)
                break;

            foreach (var item in batch)
            {
                _tracksNeedingPoke.Add(item.TrackId);
                Interlocked.Increment(ref _triggeredWakeCount);
                firedCount++;
            }

            await PokePendingTracksAsync(ct).ConfigureAwait(false);

            if (batch.Count < 64)
                break;
        }

        return firedCount;
    }

    private async Task PokePendingTracksAsync(CancellationToken ct)
    {
        if (_tracksNeedingPoke.Count == 0) return;
        var open = (await _kernel.ListTracksAsync(ct: ct).ConfigureAwait(false)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        _tracksNeedingPoke.RemoveWhere(id => !open.Contains(id));
        foreach (var trackId in _tracksNeedingPoke.ToArray())
        {
            if (_kernel.DispatchAdmission.Draining)
                break;
            try
            {
                await _kernel.Actors.GetOrActivate(trackId).PokePendingWakesAsync(ct).ConfigureAwait(false);
                _tracksNeedingPoke.Remove(trackId);
            }
            catch (KernelException ex) when (ex.Code == KernelErrorCode.NotFound)
            {
                _tracksNeedingPoke.Remove(trackId);
                _logger.LogWarning(ex, "Track '{TrackId}' no longer exists; retiring durable schedule poke retry.", trackId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to poke lead for durable schedule wake on track '{TrackId}'.", trackId);
            }
        }
    }

}
