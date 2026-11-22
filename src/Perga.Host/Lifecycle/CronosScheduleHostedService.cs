using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Perga.Host.Configuration;
using Perga.Kernel;

namespace Perga.Host.Lifecycle;

public sealed class CronosScheduleHostedService : BackgroundService
{
    private readonly IPergaKernel _kernel;
    private readonly PergaHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CronosScheduleHostedService> _logger;
    private readonly SemaphoreSlim _evalLock = new(1, 1);
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
        while (!_kernel.DispatchAdmission.Draining)
        {
            var batch = await _kernel.FireDueTrackSchedulesAsync(nowMs, ct).ConfigureAwait(false);

            if (batch.Count == 0)
                break;

            Interlocked.Add(ref _triggeredWakeCount, batch.Count);
            firedCount += batch.Count;

            if (batch.Count < 64)
                break;
        }

        return firedCount;
    }
}
