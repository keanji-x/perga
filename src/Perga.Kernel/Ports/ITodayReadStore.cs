using Perga.Contracts;

namespace Perga.Kernel.Ports;

public sealed record DailyReportRevision(long EventId, long At, string TrackId, string TrackTitle,
    string AreaId, string AreaName, int DocRev, string SummaryBefore, string SummaryAfter,
    string BodyBefore, string BodyAfter);

public sealed record DailyTrackCalendarPresence(string Date, bool HasDailyTrack, bool HasReport);

public interface ITodayReadStore
{
    Task<string?> GetDailyTrackIdAsync(string date, CancellationToken ct);
    Task<IReadOnlyList<DailyReportRevision>> ListDailyReportRevisionsAsync(long startMs, long endMs,
        long throughEventId, CancellationToken ct);
    Task<IReadOnlyList<DailyTrackCalendarPresence>> ListDailyTrackPresenceAsync(
        IReadOnlyList<string> dates, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DailyTrackCalendarPresence>>([]);
    Task<IReadOnlyList<TodayScheduleItemDto>> ListActiveSchedulesInWindowAsync(
        long startMs, long endMs, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TodayScheduleItemDto>>([]);
}
