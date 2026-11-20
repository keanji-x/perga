using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests.Fakes;

internal sealed partial class InMemoryTrackStore : ITrackScheduleStore, ITodayReadStore
{
    private readonly List<TrackScheduleDto> _trackSchedules = [];
    public ITrackScheduleStore? TrackSchedules => this;

    public Task<TrackScheduleDto> UpsertAsync(TrackScheduleDto schedule, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_tracks.TryGetValue(schedule.TrackId, out var track))
                throw KernelException.NotFound($"Track '{schedule.TrackId}' was not found.");
            if (track.ClosedAtMs is not null)
                throw KernelException.Conflict($"Cannot schedule wake on closed track '{schedule.TrackId}'.");

            var existingIndex = _trackSchedules.FindIndex(s => s.TrackId == schedule.TrackId && s.Id == schedule.Id);
            var toPersist = schedule;
            if (existingIndex >= 0)
            {
                var existing = _trackSchedules[existingIndex];
                if (existing.State is TrackScheduleStates.Active or TrackScheduleStates.Paused)
                {
                    if (schedule.MaxOccurrences is { } max && existing.FiredCount >= max)
                        throw KernelException.InvalidArgument($"maxOccurrences ({max}) must exceed current firedCount ({existing.FiredCount}).");
                    toPersist = schedule with
                    {
                        FiredCount = existing.FiredCount,
                        LastFiredAtMs = existing.LastFiredAtMs,
                        CreatedAtMs = existing.CreatedAtMs
                    };
                }

                _trackSchedules[existingIndex] = toPersist;
            }
            else
            {
                _trackSchedules.Add(toPersist);
            }

            return Task.FromResult(toPersist);
        }
    }

    public Task<TrackScheduleDto?> GetAsync(string trackId, string scheduleId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_trackSchedules.FirstOrDefault(s => s.TrackId == trackId && s.Id == scheduleId));
        }
    }

    public Task<IReadOnlyList<TrackScheduleDto>> ListForTrackAsync(string trackId, bool includeTerminal, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TrackScheduleDto>>(_trackSchedules
                .Where(s => s.TrackId == trackId &&
                            (includeTerminal || s.State is TrackScheduleStates.Active or TrackScheduleStates.Paused))
                .OrderBy(s => s.NextFireAtMs ?? long.MaxValue)
                .ThenBy(s => s.CreatedAtMs)
                .ToArray());
        }
    }

    public Task<bool> CancelAsync(string trackId, string scheduleId, CancellationToken ct)
    {
        lock (_gate)
        {
            var idx = _trackSchedules.FindIndex(s =>
                s.TrackId == trackId && s.Id == scheduleId &&
                s.State is TrackScheduleStates.Active or TrackScheduleStates.Paused);
            if (idx < 0)
                return Task.FromResult(false);

            var existing = _trackSchedules[idx];
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _trackSchedules[idx] = existing with { State = TrackScheduleStates.Canceled, NextFireAtMs = null, UpdatedAtMs = now };
            var key = TrackWakeRequests.TrackScheduleKey(existing.Id, existing.CoalesceKey);
            for (var i = 0; i < _trackWakes.Count; i++)
                if (_trackWakes[i] is { State: TrackWakeStates.Pending, Kind: TrackWakeKinds.Schedule } w &&
                    w.TrackId == trackId && w.CoalesceKey == key)
                    _trackWakes[i] = w with { State = TrackWakeStates.Dropped, Reason = TrackWakeReasons.Canceled, UpdatedAtMs = now };
            return Task.FromResult(true);
        }
    }

    public Task<int> CancelActiveForTrackAsync(string trackId, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var count = 0;
            for (var i = 0; i < _trackSchedules.Count; i++)
                if (_trackSchedules[i] is { State: TrackScheduleStates.Active or TrackScheduleStates.Paused } s && s.TrackId == trackId)
                {
                    _trackSchedules[i] = s with { State = TrackScheduleStates.Canceled, NextFireAtMs = null, UpdatedAtMs = now };
                    count++;
                }
            for (var i = 0; i < _trackWakes.Count; i++)
                if (_trackWakes[i] is { State: TrackWakeStates.Pending, Kind: TrackWakeKinds.Schedule } w &&
                    w.TrackId == trackId && TrackWakeRequests.IsTrackScheduleWake(w))
                    _trackWakes[i] = w with { State = TrackWakeStates.Dropped, Reason = TrackWakeReasons.Canceled, UpdatedAtMs = now };
            return Task.FromResult(count);
        }
    }

    public Task<IReadOnlyList<FiredTrackSchedule>> FireDueAsync(long nowMs, Func<TrackScheduleDto, long?> computeNextFireAtMs, CancellationToken ct)
    {
        lock (_gate)
        {
            var fired = new List<FiredTrackSchedule>();
            for (var i = 0; i < _trackSchedules.Count; i++)
            {
                var s = _trackSchedules[i];
                if (s.State is not (TrackScheduleStates.Active or TrackScheduleStates.Paused))
                    continue;
                if (!_tracks.TryGetValue(s.TrackId, out var t) || t.ClosedAtMs is not null)
                {
                    _trackSchedules[i] = s with { State = TrackScheduleStates.Canceled, NextFireAtMs = null, UpdatedAtMs = nowMs };
                    continue;
                }
                if (s.State != TrackScheduleStates.Active)
                    continue;
                if (s.NextFireAtMs is null || s.NextFireAtMs > nowMs)
                    continue;

                var nextCount = s.FiredCount + 1;
                long? nextFire = null;
                var nextState = TrackScheduleStates.Done;
                if (s.ScheduleKind == TrackScheduleKinds.Cron && (!s.MaxOccurrences.HasValue || nextCount < s.MaxOccurrences.Value))
                {
                    nextFire = computeNextFireAtMs(s);
                    if (nextFire.HasValue)
                        nextState = TrackScheduleStates.Active;
                }

                _trackSchedules[i] = s with
                {
                    FiredCount = nextCount,
                    LastFiredAtMs = nowMs,
                    NextFireAtMs = nextFire,
                    State = nextState,
                    UpdatedAtMs = nowMs
                };
                EnqueueWakeLocked(TrackWakeRequests.DynamicSchedule(s.TrackId, s.Id, s.Prompt, s.CoalesceKey, s.TargetSessionId));
                fired.Add(new FiredTrackSchedule(s.TrackId, s.Id));
            }
            return Task.FromResult<IReadOnlyList<FiredTrackSchedule>>(fired);
        }
    }

    public Task<string?> GetDailyTrackIdAsync(string date, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<IReadOnlyList<DailyReportRevision>> ListDailyReportRevisionsAsync(long startMs, long endMs, long throughEventId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DailyReportRevision>>([]);

    public Task<IReadOnlyList<DailyTrackCalendarPresence>> ListDailyTrackPresenceAsync(IReadOnlyList<string> dates, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DailyTrackCalendarPresence>>([]);

    public Task<IReadOnlyList<TodayScheduleEntry>> ListActiveSchedulesInWindowAsync(long startMs, long endMs, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TodayScheduleEntry>>(_trackSchedules
                .Where(s => s.State == TrackScheduleStates.Active &&
                            _tracks.TryGetValue(s.TrackId, out var t) && t.ClosedAtMs is null &&
                            s.NextFireAtMs is >= 0 &&
                            (s.ScheduleKind == TrackScheduleKinds.Cron ||
                             (s.NextFireAtMs >= startMs && s.NextFireAtMs < endMs)))
                .OrderBy(s => s.NextFireAtMs)
                .ThenBy(s => s.TrackId, StringComparer.Ordinal)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .Select(s => new TodayScheduleEntry(
                    s.Id,
                    s.TrackId,
                    _tracks[s.TrackId].Title,
                    s.Title,
                    s.ScheduleKind,
                    s.CronExpr,
                    s.Timezone,
                    s.NextFireAtMs!.Value,
                    s.CreatedAtMs,
                    s.LastFiredAtMs,
                    s.MaxOccurrences,
                    s.FiredCount,
                    s.State))
                .ToArray());
        }
    }
}
