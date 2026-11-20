using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public async Task<TrackScheduleDto> SetTrackScheduleAsync(
        string trackId,
        SetTrackScheduleRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        ArgumentNullException.ThrowIfNull(request);

        var scheduleStore = _store.TrackSchedules
            ?? throw KernelException.PreconditionFailed("Track schedules are not enabled on this store.");

        var title = request.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
            throw KernelException.InvalidArgument("Schedule title must be 1 to 200 characters.");

        var prompt = request.Prompt?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
            throw KernelException.InvalidArgument("Schedule prompt must be a non-empty string.");

        var hasCron = !string.IsNullOrWhiteSpace(request.CronExpr);
        var hasRunAtMs = request.RunAtMs.HasValue;
        var hasRunAtStr = !string.IsNullOrWhiteSpace(request.RunAt);
        var scheduleTargets = (hasCron ? 1 : 0) + (hasRunAtMs ? 1 : 0) + (hasRunAtStr ? 1 : 0);
        if (scheduleTargets != 1)
            throw KernelException.InvalidArgument("Specify exactly one of 'runAt', 'runAtMs', or 'cron'.");

        var scheduleId = string.IsNullOrWhiteSpace(request.Id)
            ? $"sch_{Guid.NewGuid():N}"
            : request.Id.Trim();
        if (scheduleId.Length > 128 || scheduleId.Any(char.IsWhiteSpace))
            throw KernelException.InvalidArgument("Schedule id must be 1 to 128 non-whitespace characters.");

        var sourceKind = string.IsNullOrWhiteSpace(request.SourceKind)
            ? TrackScheduleSources.Agent
            : request.SourceKind.Trim();
        if (sourceKind is not (TrackScheduleSources.Agent or TrackScheduleSources.User or TrackScheduleSources.Plugin or TrackScheduleSources.Template))
            throw KernelException.InvalidArgument("Unsupported schedule sourceKind.");

        var nowMs = NowMs();
        var defaultTz = TodayCalendar.IanaId(TodayCalendar.Zone(_timeProvider));
        var requestedTz = string.IsNullOrWhiteSpace(request.Timezone) ? defaultTz : request.Timezone.Trim();

        string scheduleKind;
        string? cronExpr = null;
        string normalizedTz;
        long? runAtMs = null;
        long nextFireAtMs;
        int? maxOccurrences;

        if (!hasCron)
        {
            var resolvedRunAtMs = request.RunAtMs ?? TodayCalendar.ParseRunAtMs(request.RunAt!, requestedTz, _timeProvider);
            if (resolvedRunAtMs < 0)
                throw KernelException.InvalidArgument("runAtMs must be a nonnegative Unix timestamp in milliseconds.");
            if (request.MaxOccurrences.HasValue && request.MaxOccurrences.Value != 1)
                throw KernelException.InvalidArgument("One-shot schedules only support maxOccurrences = 1.");

            normalizedTz = TodayCalendar.IanaId(TodayCalendar.ResolveZone(requestedTz));
            scheduleKind = TrackScheduleKinds.Once;
            runAtMs = resolvedRunAtMs;
            nextFireAtMs = resolvedRunAtMs;
            maxOccurrences = 1;
        }
        else
        {
            var evaluator = _scheduleCronEvaluator
                ?? throw KernelException.PreconditionFailed("Cron schedule evaluator is not configured.");
            cronExpr = request.CronExpr!.Trim();
            if (!evaluator.TryValidate(cronExpr, requestedTz, out normalizedTz, out var cronError))
                throw KernelException.InvalidArgument(cronError ?? "Invalid cron expression or timezone.");
            if (request.MaxOccurrences.HasValue && request.MaxOccurrences.Value < 1)
                throw KernelException.InvalidArgument("maxOccurrences must be at least 1.");

            var next = evaluator.GetNextOccurrenceMs(cronExpr, normalizedTz, nowMs)
                ?? throw KernelException.InvalidArgument("Cron expression produced no upcoming occurrence.");
            scheduleKind = TrackScheduleKinds.Cron;
            nextFireAtMs = next;
            maxOccurrences = request.MaxOccurrences;
        }

        var dto = new TrackScheduleDto(
            Id: scheduleId,
            TrackId: trackId,
            SourceKind: sourceKind,
            ScheduleKind: scheduleKind,
            Title: title,
            Prompt: prompt,
            CronExpr: cronExpr,
            Timezone: normalizedTz,
            RunAtMs: runAtMs,
            NextFireAtMs: nextFireAtMs,
            LastFiredAtMs: null,
            MaxOccurrences: maxOccurrences,
            FiredCount: 0,
            State: TrackScheduleStates.Active,
            CreatedAtMs: nowMs,
            UpdatedAtMs: nowMs);

        return await scheduleStore.UpsertAsync(dto, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrackScheduleDto>> ListTrackSchedulesAsync(
        string trackId,
        bool includeTerminal = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        var scheduleStore = _store.TrackSchedules
            ?? throw KernelException.PreconditionFailed("Track schedules are not enabled on this store.");
        return await scheduleStore.ListForTrackAsync(trackId, includeTerminal, ct).ConfigureAwait(false);
    }

    public async Task<bool> CancelTrackScheduleAsync(
        string trackId,
        string scheduleId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        var scheduleStore = _store.TrackSchedules
            ?? throw KernelException.PreconditionFailed("Track schedules are not enabled on this store.");
        return await scheduleStore.CancelAsync(trackId, scheduleId.Trim(), ct).ConfigureAwait(false);
    }
}
