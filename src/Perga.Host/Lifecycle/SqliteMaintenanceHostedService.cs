using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Kernel;

namespace Perga.Host.Lifecycle;

public sealed class SqliteMaintenanceHostedService : BackgroundService
{
    private readonly IPergaKernel _kernel;
    private readonly PergaHostOptions _options;
    private readonly ILogger<SqliteMaintenanceHostedService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private long _completedPasses;

    public SqliteMaintenanceHostedService(
        IPergaKernel kernel,
        PergaHostOptions options,
        ILogger<SqliteMaintenanceHostedService> logger)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public long CompletedPasses => Interlocked.Read(ref _completedPasses);
    public DbMaintenanceStatsDto? LastStats { get; private set; }

    public async Task<DbMaintenanceStatsDto> RunOnceAsync(CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stats = await _kernel.RunMaintenancePassAsync(ct).ConfigureAwait(false);
            LastStats = stats;
            Interlocked.Increment(ref _completedPasses);

            if (stats is { PrunedEvents: > 0 } or { ThinnedRevisions: > 0 } or { CompactedItems: > 0 }
                or { CompactedTasks: > 0 } or { ReclaimedBlobs: > 0 } or { ReclaimedPages: > 0 })
            {
                _logger.LogInformation(
                    "SQLite maintenance pass completed: prunedEvents={PrunedEvents}, thinnedRevisions={ThinnedRevisions}, compactedItems={CompactedItems}, compactedTasks={CompactedTasks}, reclaimedBlobs={ReclaimedBlobs}, reclaimedPages={ReclaimedPages}.",
                    stats.PrunedEvents,
                    stats.ThinnedRevisions,
                    stats.CompactedItems,
                    stats.CompactedTasks,
                    stats.ReclaimedBlobs,
                    stats.ReclaimedPages);
            }

            return stats;
        }
        finally
        {
            _runLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableMaintenanceService)
        {
            return;
        }

        var interval = _options.MaintenanceInterval <= TimeSpan.Zero
            ? TimeSpan.FromHours(6)
            : _options.MaintenanceInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SQLite periodic maintenance pass failed.");
            }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _runLock.Dispose();
    }
}
