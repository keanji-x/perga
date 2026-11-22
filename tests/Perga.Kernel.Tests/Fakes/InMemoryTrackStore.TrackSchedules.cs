using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests.Fakes;

internal sealed partial class InMemoryTrackStore : ITrackScheduleStore, ITodayReadStore
{
    private readonly List<TrackScheduleDto> _trackSchedules = [];
    public ITrackScheduleStore? TrackSchedules => this;

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

    public Task<IReadOnlyList<FiredTrackSchedule>> ListDueFlowClocksAsync(long nowMs, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<FiredTrackSchedule> due = _trackSchedules
                .Where(clock => clock.NodeKey is not null && clock.State == TrackScheduleStates.Active &&
                    clock.NextFireAtMs <= nowMs && _tracks[clock.TrackId].ClosedAtMs is null)
                .OrderBy(clock => clock.NextFireAtMs).ThenBy(clock => clock.Id, StringComparer.Ordinal)
                .Take(64).Select(clock => new FiredTrackSchedule(clock.TrackId, clock.Id, clock.NodeKey!, clock.NextFireAtMs!.Value)).ToArray();
            return Task.FromResult(due);
        }
    }

    public Task<bool> CommitFlowFireAsync(TrackScheduleDto nextClock, long expectedDueAtMs, TaskDto? armedTask,
        EventEnvelopeDto taskEvent, CancellationToken ct)
    {
        lock (_gate)
        {
            var idx = _trackSchedules.FindIndex(s => s.TrackId == nextClock.TrackId && s.Id == nextClock.Id &&
                s.NodeKey == nextClock.NodeKey && s.State == TrackScheduleStates.Active && s.NextFireAtMs == expectedDueAtMs);
            if (idx < 0)
                return Task.FromResult(false);
            if (armedTask is not null)
                UpsertTaskAsync(armedTask, null, taskEvent, ct).GetAwaiter().GetResult();
            _trackSchedules[idx] = nextClock;
            return Task.FromResult(true);
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
