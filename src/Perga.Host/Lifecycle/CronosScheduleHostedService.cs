using System.Collections.Concurrent;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Perga.Host.Configuration;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.PolicyVm;

namespace Perga.Host.Lifecycle;

public sealed class CronosScheduleHostedService : BackgroundService
{
    private readonly IPergaKernel _kernel;
    private readonly ITrackReadStore _readStore;
    private readonly PergaHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CronosScheduleHostedService> _logger;
    private readonly ConcurrentDictionary<(string TrackId, string ScheduleId), DateTimeOffset> _watermarks = new();
    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private DateTimeOffset _bootWatermarkUtc;
    private long _triggeredWakeCount;

    public CronosScheduleHostedService(
        IPergaKernel kernel,
        ITrackReadStore readStore,
        PergaHostOptions options,
        TimeProvider timeProvider,
        ILogger<CronosScheduleHostedService> logger)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _readStore = readStore ?? throw new ArgumentNullException(nameof(readStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bootWatermarkUtc = _timeProvider.GetUtcNow();
    }

    public long TriggeredWakeCount => Interlocked.Read(ref _triggeredWakeCount);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _bootWatermarkUtc = _timeProvider.GetUtcNow();
        return base.StartAsync(cancellationToken);
    }

    public async Task<int> EvaluateDueSchedulesOnceAsync(CancellationToken ct = default)
    {
        await _evalLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var nowUtc = _timeProvider.GetUtcNow();
            var openTracks = await _kernel.ListTracksAsync(areaId: null, includeClosed: false, ct)
                .ConfigureAwait(false);
            var activeTrackIds = new HashSet<string>(openTracks.Select(static t => t.Id), StringComparer.Ordinal);

            // Clean up watermarks for closed or deleted tracks.
            foreach (var key in _watermarks.Keys.ToArray())
            {
                if (!activeTrackIds.Contains(key.TrackId))
                {
                    _watermarks.TryRemove(key, out _);
                }
            }

            var fired = 0;
            foreach (var track in openTracks)
            {
                ct.ThrowIfCancellationRequested();
                var schedules = await ResolveSchedulesForTrackAsync(track.Id, track.TemplateId, ct)
                    .ConfigureAwait(false);
                if (schedules.Count == 0)
                {
                    continue;
                }

                var trackCreatedUtc = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, track.CreatedAtMs));
                var initialBaseline = trackCreatedUtc > _bootWatermarkUtc ? trackCreatedUtc : _bootWatermarkUtc;

                foreach (var spec in schedules)
                {
                    if (string.IsNullOrWhiteSpace(spec.Id) || string.IsNullOrWhiteSpace(spec.Cron))
                    {
                        continue;
                    }

                    if (!TryParseCron(spec.Cron, out var cronExpr))
                    {
                        _logger.LogWarning(
                            "Invalid cron expression '{Cron}' on schedule '{ScheduleId}' for track '{TrackId}'.",
                            spec.Cron,
                            spec.Id,
                            track.Id);
                        continue;
                    }

                    var tz = ResolveTimeZone(spec.Timezone);
                    var key = (track.Id, spec.Id);
                    var watermark = _watermarks.GetOrAdd(key, initialBaseline);

                    if (nowUtc <= watermark)
                    {
                        continue;
                    }

                    var nextOccurrence = cronExpr.GetNextOccurrence(watermark.UtcDateTime, tz, inclusive: false);
                    if (nextOccurrence.HasValue && nextOccurrence.Value <= nowUtc.UtcDateTime)
                    {
                        _watermarks[key] = nowUtc;
                        try
                        {
                            await _kernel.TriggerScheduleWakeAsync(track.Id, spec.Id, ct).ConfigureAwait(false);
                            Interlocked.Increment(ref _triggeredWakeCount);
                            fired++;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(
                                ex,
                                "Failed to trigger schedule wake '{ScheduleId}' on track '{TrackId}'.",
                                spec.Id,
                                track.Id);
                        }
                    }
                }
            }

            return fired;
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

    private async Task<IReadOnlyList<ScheduleSpec>> ResolveSchedulesForTrackAsync(
        string trackId,
        string? templateId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(templateId) &&
            _kernel.Templates.TryGetPolicy(templateId) is { } catalogPolicy)
        {
            return catalogPolicy.Manifest.Schedules;
        }

        var snap = await _readStore.LoadTrackAggregateAsync(trackId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(snap?.TemplateSnapshotSource))
        {
            var compiled = _kernel.Templates.GetOrCompileSnapshot(snap.TemplateSnapshotSource!);
            return compiled.Manifest.Schedules;
        }

        return [];
    }

    private static bool TryParseCron(string rawCron, out CronExpression expression)
    {
        var parts = rawCron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var format = parts.Length == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
        return CronExpression.TryParse(rawCron, format, out expression!);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone) ||
            string.Equals(timezone, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone.Trim());
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
