using System.Globalization;
using System.Text;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

/// <summary>Dated report evidence; all pages share the caller's captured event boundary.</summary>
public sealed class TodayReportService(ITrackReadStore tracks, ITodayReadStore today, TimeProvider time)
{
    private const int PageSize = 50;

    public async Task<DailyReportChangesPageDto> ChangesAsync(string date, string? cursor, long? through, CancellationToken ct)
    {
        if (cursor is not null && through is null)
            throw KernelException.InvalidArgument("A cursor requires through_event_id.");
        var afterTrack = cursor is null ? null : ParseChangesCursor(cursor);
        var boundary = await BoundaryAsync(through, ct).ConfigureAwait(false);
        var zone = TodayCalendar.Zone(time);
        var (start, end) = TodayCalendar.Bounds(date, zone);
        var rows = await today.ListDailyReportRevisionsAsync(start, end, boundary, ct).ConfigureAwait(false);
        var groups = rows.GroupBy(r => r.TrackId).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Where(g => afterTrack is null || string.CompareOrdinal(g.Key, afterTrack) > 0).Take(PageSize + 1).ToArray();
        var changes = groups.Take(PageSize).Select(g =>
        {
            var first = g.First();
            var last = g.Last();
            var patch = UnifiedPatch(first.BodyBefore, last.BodyAfter);
            return new DailyReportChangeDto(first.TrackId, last.TrackTitle, last.AreaId, last.AreaName,
                g.Count(), first.EventId, last.EventId, first.SummaryBefore, last.SummaryAfter,
                patch.Text, patch.Truncated);
        }).ToArray();
        return new(date, TodayCalendar.IanaId(zone), boundary, changes,
            groups.Length > PageSize ? EncodeChangesCursor(changes[^1].TrackId) : null);
    }

    public async Task<DailyReportEditsPageDto> EditsAsync(string date, string trackId, long through, string? cursor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(trackId)) throw KernelException.InvalidArgument("track_id is required.");
        var boundary = await BoundaryAsync(through, ct).ConfigureAwait(false);
        var after = cursor is null ? 0 : ParseEventId(cursor);
        var (start, end) = TodayCalendar.Bounds(date, TodayCalendar.Zone(time));
        var rows = (await today.ListDailyReportRevisionsAsync(start, end, boundary, ct).ConfigureAwait(false))
            .Where(r => r.TrackId == trackId && r.EventId > after).Take(PageSize + 1).ToArray();
        var edits = rows.Take(PageSize).Select(r => new DailyReportEditEntryDto(r.EventId, r.At,
            new(r.TrackId, r.DocRev.ToString(CultureInfo.InvariantCulture), r.SummaryBefore,
                r.SummaryAfter, r.BodyBefore, r.BodyAfter))).ToArray();
        return new(edits, rows.Length > PageSize ? edits[^1].EventId.ToString(CultureInfo.InvariantCulture) : null);
    }

    private static string EncodeChangesCursor(string trackId) => Convert.ToBase64String(Encoding.UTF8.GetBytes(trackId));

    private static string ParseChangesCursor(string cursor)
    {
        try
        {
            var trackId = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (!string.IsNullOrWhiteSpace(trackId) && EncodeChangesCursor(trackId) == cursor) return trackId;
        }
        catch (FormatException) { }
        throw KernelException.InvalidArgument("Invalid report changes cursor.");
    }

    public static long ParseEventId(string value) => long.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var id) && id >= 0 ? id
        : throw KernelException.InvalidArgument("Event identities must be nonnegative integers.");

    private async Task<long> BoundaryAsync(long? through, CancellationToken ct)
    {
        if (through < 0) throw KernelException.InvalidArgument("through_event_id must be nonnegative.");
        var tip = await tracks.GetEventTipIdAsync(ct).ConfigureAwait(false);
        if (through > tip) throw KernelException.InvalidArgument("through_event_id exceeds the event tip.");
        return through ?? tip;
    }

    private static (string Text, bool Truncated) UnifiedPatch(string before, string after)
    {
        if (before == after) return (string.Empty, false);
        // Whole-body replacement is a valid unified diff and avoids quadratic diff work.
        static string[] Lines(string text) => text.Length == 0 ? [] : (text.EndsWith('\n') ? text[..^1] : text).Split('\n');
        var oldLines = Lines(before);
        var newLines = Lines(after);
        var patch = new StringBuilder("--- a/report.md\n+++ b/report.md\n");
        patch.Append(CultureInfo.InvariantCulture, $"@@ -{(oldLines.Length == 0 ? 0 : 1)},{oldLines.Length} +{(newLines.Length == 0 ? 0 : 1)},{newLines.Length} @@\n");
        void Append(string[] lines, char prefix, string body)
        {
            foreach (var line in lines) patch.Append(prefix).Append(line).Append('\n');
            if (lines.Length > 0 && !body.EndsWith('\n')) patch.Append("\\ No newline at end of file\n");
        }
        Append(oldLines, '-', before);
        Append(newLines, '+', after);
        return (patch.ToString(), false);
    }
}
