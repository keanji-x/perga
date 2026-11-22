using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;

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

        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var result = await scheduleStore.UpsertAsync(dto, ct).ConfigureAwait(false);
        await PublishScheduleEventsAsync(trackId).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<TrackScheduleDto>> ListTrackSchedulesAsync(
        string trackId,
        bool includeTerminal = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        var scheduleStore = _store.TrackSchedules
            ?? throw KernelException.PreconditionFailed("Track schedules are not enabled on this store.");
        await RequireScheduleTrackAsync(trackId, ct).ConfigureAwait(false);
        var schedules = await scheduleStore.ListForTrackAsync(trackId, includeTerminal, ct).ConfigureAwait(false);
        return schedules.Where(s => s.NodeKey is null).ToArray();
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
        await RequireScheduleTrackAsync(trackId, ct).ConfigureAwait(false);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var canceled = await scheduleStore.CancelAsync(trackId, scheduleId.Trim(), ct).ConfigureAwait(false);
        if (canceled) await PublishScheduleEventsAsync(trackId).ConfigureAwait(false);
        return canceled;
    }

    public async Task<TrackSchedulesSnapshot> GetTrackSchedulesAsync(string trackId, bool includeTerminal = false, int runsLimit = 20, CancellationToken ct = default)
    {
        if (runsLimit is < 0 or > 200) throw KernelException.InvalidArgument("runsLimit must be between 0 and 200.");
        var schedules = await ListTrackSchedulesAsync(trackId, includeTerminal, ct).ConfigureAwait(false);
        var runs = await _store.TrackWakes.ListScheduleRunsAsync(trackId, runsLimit, ct).ConfigureAwait(false);
        return new(schedules, runs.Select(wake => new TrackScheduleRunDto(
            wake.Id, TrackWakePayload.Parse(wake.PayloadJson).ScheduleId, wake.State, wake.Reason,
            wake.TurnId, wake.CreatedAtMs, wake.UpdatedAtMs)).ToArray());
    }

    public async Task<bool> TriggerTrackScheduleAsync(string trackId, string scheduleId, CancellationToken ct = default)
    {
        await RequireScheduleTrackAsync(trackId, ct).ConfigureAwait(false);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var store = _store.TrackSchedules ?? throw KernelException.PreconditionFailed("Track schedules are not enabled on this store.");
        var triggered = await store.TriggerAsync(trackId, scheduleId, ct).ConfigureAwait(false);
        if (triggered)
        {
            await PublishScheduleEventsAsync(trackId).ConfigureAwait(false);
            await Actors.GetOrActivate(trackId).PokePendingWakesAsync(ct).ConfigureAwait(false);
        }
        return triggered;
    }

    public async Task<IReadOnlyList<FiredTrackSchedule>> FireDueTrackSchedulesAsync(long nowMs, CancellationToken ct = default)
    {
        if (_store.TrackSchedules is not { } store) return [];
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var fired = await store.FireDueAsync(nowMs, schedule => schedule.CronExpr is null ? null :
            _scheduleCronEvaluator?.GetNextOccurrenceMs(schedule.CronExpr, schedule.Timezone, nowMs), ct).ConfigureAwait(false);
        var committed = new List<FiredTrackSchedule>(fired.Count);
        foreach (var item in fired)
        {
            try
            {
                if (item.NodeKey is null || await Actors.GetOrActivate(item.TrackId).FireFlowScheduleAsync(item, nowMs, ct)
                        .ConfigureAwait(false))
                    committed.Add(item);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                System.Diagnostics.Trace.TraceWarning("Could not dispatch due Flow clock {0}: {1}", item.ScheduleId, ex.Message);
            }
        }
        foreach (var trackId in committed.Select(s => s.TrackId).Distinct())
            await PublishScheduleEventsAsync(trackId).ConfigureAwait(false);
        return committed;
    }

    private async Task RequireScheduleTrackAsync(string trackId, CancellationToken ct)
    {
        if (await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false) is null)
            throw KernelException.NotFound($"Track '{trackId}' not found.");
    }

    private async Task PublishScheduleEventsAsync(string trackId)
    {
        try
        {
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store,
                new EventEnvelopeDto(null, null, trackId, KernelEventKinds.TrackSchedulesUpdated, JsonHelpers.ToElement(new { trackId }), NowMs()),
                CancellationToken.None, allowFallback: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not publish committed schedule events for Track {0}: {1}", trackId, ex);
        }
    }
}
