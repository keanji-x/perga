using Perga.Contracts;

namespace Perga.Kernel.Ports;

public sealed record DailyReportRevision(long EventId, long At, string TrackId, string TrackTitle,
    string AreaId, string AreaName, int DocRev, string SummaryBefore, string SummaryAfter,
    string BodyBefore, string BodyAfter);

public interface ITodayReadStore
{
    Task<string?> GetDailyTrackIdAsync(string date, CancellationToken ct);
    Task<IReadOnlyList<DailyReportRevision>> ListDailyReportRevisionsAsync(long startMs, long endMs,
        long throughEventId, CancellationToken ct);
}
