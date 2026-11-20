using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests.Fakes;

internal sealed partial class InMemoryTrackStore : ITrackScheduleStore, ITodayReadStore
{
    private readonly List<TrackScheduleDto> _trackSchedules = [];
    public ITrackScheduleStore? TrackSchedules => this;

    private void SyncTemplateSchedulesLocked(string trackId, IReadOnlyList<TrackScheduleDto> schedules)
    {
        foreach (var s in schedules)
            if (_trackSchedules.Any(old => old.TrackId == trackId && old.Id == s.Id && old.SourceKind != "template" && old.State == "active"))
                throw KernelException.Conflict("Schedule id conflict.");
        var ids = schedules.Select(s => s.Id).ToHashSet();
        foreach (var old in _trackSchedules.Where(s => s.TrackId == trackId && s.SourceKind == "template" && !ids.Contains(s.Id)).ToArray())
            CancelAsync(trackId, old.Id, default).GetAwaiter().GetResult();
        foreach (var s in schedules)
        {
            var index = _trackSchedules.FindIndex(old => old.TrackId == trackId && old.Id == s.Id);
            if (index < 0) _trackSchedules.Add(s);
            else
            {
                var old = _trackSchedules[index];
                _trackSchedules[index] = old.State == "active" && old.SourceKind == "template" && old.CronExpr == s.CronExpr && old.Timezone == s.Timezone
                    ? old with { Title = s.Title, Prompt = s.Prompt, UpdatedAtMs = s.UpdatedAtMs } : s;
            }
        }
    }

    public Task<bool> TriggerAsync(string trackId, string scheduleId, CancellationToken ct)
    {
        lock (_gate)
        {
            var row = _trackSchedules.FirstOrDefault(s => s.TrackId == trackId && s.Id == scheduleId);
            if (row is null) return Task.FromResult(false);
            if (row.State != "active") throw KernelException.Conflict("Only active schedules can be triggered.");
            EnqueueWakeLocked(TrackWakeRequests.DynamicSchedule(trackId, scheduleId, row.Prompt, row.SourceKind));
            return Task.FromResult(true);
        }
    }

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
                if (existing.State is TrackScheduleStates.Active)
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
                            (includeTerminal || s.State is TrackScheduleStates.Active))
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
                s.State is TrackScheduleStates.Active);
            if (idx < 0)
                return Task.FromResult(false);

            var existing = _trackSchedules[idx];
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _trackSchedules[idx] = existing with { State = TrackScheduleStates.Canceled, NextFireAtMs = null, UpdatedAtMs = now };
            var key = TrackWakeRequests.TrackScheduleKey(existing.Id);
            for (var i = 0; i < _trackWakes.Count; i++)
                if (_trackWakes[i] is { State: TrackWakeStates.Pending, Kind: TrackWakeKinds.Schedule } w &&
                    w.TrackId == trackId && w.CoalesceKey == key &&
                    HasScheduleId(w, scheduleId))
                    _trackWakes[i] = w with { State = TrackWakeStates.Dropped, Reason = TrackWakeReasons.Canceled, UpdatedAtMs = now };
            return Task.FromResult(true);
        }
    }

    private static bool HasScheduleId(TrackWake wake, string scheduleId)
    {
        using var payload = System.Text.Json.JsonDocument.Parse(wake.PayloadJson);
        return payload.RootElement.TryGetProperty("scheduleId", out var id) && id.GetString() == scheduleId;
    }

    public Task<int> CancelActiveForTrackAsync(string trackId, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var count = 0;
            for (var i = 0; i < _trackSchedules.Count; i++)
                if (_trackSchedules[i] is { State: TrackScheduleStates.Active } s && s.TrackId == trackId)
                {
                    _trackSchedules[i] = s with { State = TrackScheduleStates.Canceled, NextFireAtMs = null, UpdatedAtMs = now };
                    count++;
                }
            for (var i = 0; i < _trackWakes.Count; i++)
                if (_trackWakes[i] is { State: TrackWakeStates.Pending, Kind: TrackWakeKinds.Schedule } w &&
                    w.TrackId == trackId)
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
                if (s.State != TrackScheduleStates.Active)
                    continue;
                if (!_tracks.TryGetValue(s.TrackId, out var t) || t.ClosedAtMs is not null)
                {
                    _trackSchedules[i] = s with { State = TrackScheduleStates.Canceled, NextFireAtMs = null, UpdatedAtMs = nowMs };
                    continue;
                }
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
                EnqueueWakeLocked(TrackWakeRequests.DynamicSchedule(s.TrackId, s.Id, s.Prompt, s.SourceKind));
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

    public Task<IReadOnlyList<TodayScheduleEntry>> ListCalendarSchedulesInWindowAsync(long startMs, long endMs, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TodayScheduleEntry>>(_trackSchedules
                .Where(s => _tracks.TryGetValue(s.TrackId, out var t) && t.ClosedAtMs is null &&
                    ((s.State == TrackScheduleStates.Active && s.NextFireAtMs is not null &&
                      (s.ScheduleKind == TrackScheduleKinds.Cron ||
                       (s.NextFireAtMs >= startMs && s.NextFireAtMs < endMs) ||
                       (s.LastFiredAtMs >= startMs && s.LastFiredAtMs < endMs))) ||
                     (s.State == TrackScheduleStates.Done && s.LastFiredAtMs >= startMs && s.LastFiredAtMs < endMs)))
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
                    s.NextFireAtMs,
                    s.CreatedAtMs,
                    s.LastFiredAtMs,
                    s.MaxOccurrences,
                    s.FiredCount,
                    s.State))
                .ToArray());
        }
    }
}
