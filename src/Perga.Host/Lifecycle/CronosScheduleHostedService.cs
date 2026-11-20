using System.Collections.Concurrent;
using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.PolicyVm;

namespace Perga.Host.Lifecycle;

public sealed class CronosScheduleHostedService : BackgroundService
{
    private readonly IPergaKernel _kernel;
    private readonly ITrackReadStore _readStore;
    private readonly ITrackScheduleStore? _scheduleStore;
    private readonly IScheduleCronEvaluator _cronEvaluator;
    private readonly PergaHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CronosScheduleHostedService> _logger;
    private readonly ConcurrentDictionary<(string TrackId, string ScheduleId), DateTimeOffset> _watermarks = new();
    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private readonly HashSet<string> _tracksNeedingPoke = new(StringComparer.Ordinal);
    private DateTimeOffset _bootWatermarkUtc;
    private long _triggeredWakeCount;

    [ActivatorUtilitiesConstructor]
    public CronosScheduleHostedService(
        IPergaKernel kernel,
        ITrackReadStore readStore,
        PergaHostOptions options,
        TimeProvider timeProvider,
        ILogger<CronosScheduleHostedService> logger,
        ITrackScheduleStore? scheduleStore = null,
        IScheduleCronEvaluator? cronEvaluator = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _readStore = readStore ?? throw new ArgumentNullException(nameof(readStore));
        _scheduleStore = scheduleStore;
        _cronEvaluator = cronEvaluator ?? new CronosScheduleCronEvaluator();
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
            if (_kernel.DispatchAdmission.Draining) return 0;
            var nowUtc = _timeProvider.GetUtcNow();
            var openTracks = await _readStore.ListOpenTrackScheduleSourcesAsync(ct)
                .ConfigureAwait(false);
            var activeTrackIds = new HashSet<string>(openTracks.Select(static t => t.TrackId), StringComparer.Ordinal);

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
                var schedules = ResolveSchedulesForTrack(track.TemplateSnapshotSource, track.TemplateId, track.TrackId);
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

                    if (!CronosScheduleCronEvaluator.TryParseCron(spec.Cron, out var cronExpr))
                    {
                        _logger.LogWarning(
                            "Invalid cron expression '{Cron}' on schedule '{ScheduleId}' for track '{TrackId}'.",
                            spec.Cron,
                            spec.Id,
                            track.TrackId);
                        continue;
                    }

                    var tz = TodayCalendar.TryResolveZone(spec.Timezone, out var parsedTz) ? parsedTz : TimeZoneInfo.Utc;
                    var key = (track.TrackId, spec.Id);
                    var watermark = _watermarks.GetOrAdd(key, initialBaseline);

                    if (nowUtc <= watermark)
                    {
                        continue;
                    }

                    var nextOccurrence = cronExpr.GetNextOccurrence(watermark.UtcDateTime, tz, inclusive: false);
                    if (nextOccurrence.HasValue && nextOccurrence.Value <= nowUtc.UtcDateTime)
                    {
                        if (_kernel.DispatchAdmission.Draining) return fired;
                        _watermarks[key] = nowUtc;
                        try
                        {
                            await _kernel.TriggerScheduleWakeAsync(track.TrackId, spec.Id, ct).ConfigureAwait(false);
                            Interlocked.Increment(ref _triggeredWakeCount);
                            fired++;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(
                                ex,
                                "Failed to trigger schedule wake '{ScheduleId}' on track '{TrackId}'.",
                                spec.Id,
                                track.TrackId);
                        }
                    }
                }
            }

            if (_scheduleStore is not null && !_kernel.DispatchAdmission.Draining)
            {
                fired += await FireDueDurableSchedulesAsync(nowUtc.ToUnixTimeMilliseconds(), ct).ConfigureAwait(false);
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

    private async Task<int> FireDueDurableSchedulesAsync(long nowMs, CancellationToken ct)
    {
        var firedCount = 0;
        await PokePendingTracksAsync(ct).ConfigureAwait(false);
        while (!_kernel.DispatchAdmission.Draining)
        {
            var batch = await _scheduleStore!.FireDueAsync(nowMs, schedule =>
            {
                if (string.IsNullOrWhiteSpace(schedule.CronExpr))
                    return null;
                var next = _cronEvaluator.GetNextOccurrenceMs(schedule.CronExpr, schedule.Timezone, nowMs);
                if (!next.HasValue)
                    _logger.LogWarning("Durable schedule '{ScheduleId}' on track '{TrackId}' had no future occurrence and completed.", schedule.Id, schedule.TrackId);
                return next;
            }, ct).ConfigureAwait(false);

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
        foreach (var trackId in _tracksNeedingPoke.ToArray())
        {
            if (_kernel.DispatchAdmission.Draining)
                break;
            try
            {
                await _kernel.Actors.GetOrActivate(trackId).PokeLeadAsync(ct).ConfigureAwait(false);
                _tracksNeedingPoke.Remove(trackId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to poke lead for durable schedule wake on track '{TrackId}'.", trackId);
            }
        }
    }

    private IReadOnlyList<ScheduleSpec> ResolveSchedulesForTrack(
        string? snapshotSource, string? templateId, string trackId)
    {
        if (!string.IsNullOrWhiteSpace(snapshotSource))
        {
            try
            {
                var compiled = _kernel.Templates.GetOrCompileSnapshot(snapshotSource!);
                return compiled.Manifest.Schedules;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Could not compile template snapshot for track '{TrackId}'; skipping its schedules.", trackId);
                return [];
            }
        }

        if (!string.IsNullOrWhiteSpace(templateId) &&
            _kernel.Templates.TryGetPolicy(templateId) is { } catalogPolicy)
        {
            return catalogPolicy.Manifest.Schedules;
        }

        return [];
    }
}
