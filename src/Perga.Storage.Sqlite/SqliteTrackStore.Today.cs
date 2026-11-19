using Dapper;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore : ITodayReadStore
{
    public async Task<string?> GetDailyTrackIdAsync(string date, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT track_id FROM daily_tracks WHERE date = @Date;", new { Date = date }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DailyReportRevision>> ListDailyReportRevisionsAsync(
        long startMs, long endMs, long throughEventId, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TodayRevisionRow>(new CommandDefinition("""
            SELECT coalesce(r.event_id, e.id) AS EventId, r.created_at_ms AS At, t.id AS TrackId, t.title AS TrackTitle,
                   a.id AS AreaId, a.name AS AreaName, r.doc_rev AS DocRev,
                   coalesce(r.summary_before, p.summary, '') AS SummaryBefore,
                   coalesce(r.summary, json_extract(e.payload_json, '$.summary'), '') AS SummaryAfter,
                   coalesce(r.before_blocks_zstd, p.blocks_zstd) AS BeforeBlocks, r.blocks_zstd AS AfterBlocks,
                   coalesce(r.leading_comment_before, p.leading_comment, '') AS BeforeComment, coalesce(r.leading_comment, '') AS AfterComment
            FROM report_revisions r
            JOIN tracks t ON t.id = r.track_id JOIN areas a ON a.id = t.area_id
            LEFT JOIN events e ON e.id = r.event_id OR (r.event_id IS NULL AND e.track_id = r.track_id
                AND e.kind = 'report.updated' AND json_extract(e.payload_json, '$.docRev') = r.doc_rev)
            LEFT JOIN report_revisions p ON p.track_id = r.track_id AND p.doc_rev = r.doc_rev - 1
            WHERE a.is_system = 0 AND r.created_at_ms >= @Start AND r.created_at_ms < @End
                AND coalesce(r.event_id, e.id) <= @Through
            ORDER BY coalesce(r.event_id, e.id);
            """, new { Start = startMs, End = endMs, Through = throughEventId }, cancellationToken: ct)).ConfigureAwait(false);
        var engine = new ReportDocEngine();
        string Render(byte[]? blocks, string comment) => blocks is null ? string.Empty
            : engine.RenderMarkdown(new(0, "", comment, SqliteJsonUpcaster.DecompressReportBlocksZstd(blocks)));
        return rows.Select(r => new DailyReportRevision(r.EventId, r.At, r.TrackId, r.TrackTitle,
            r.AreaId, r.AreaName, r.DocRev, r.SummaryBefore, r.SummaryAfter,
            Render(r.BeforeBlocks, r.BeforeComment), Render(r.AfterBlocks, r.AfterComment))).ToArray();
    }

    private sealed class TodayPreviousReport
    {
        public string Blocks { get; set; } = "[]";
        public string Summary { get; set; } = "";
        public string Comment { get; set; } = "";
    }

    private sealed class TodayRevisionRow
    {
        public long EventId { get; set; }
        public long At { get; set; }
        public string TrackId { get; set; } = "";
        public string TrackTitle { get; set; } = "";
        public string AreaId { get; set; } = "";
        public string AreaName { get; set; } = "";
        public int DocRev { get; set; }
        public string SummaryBefore { get; set; } = "";
        public string SummaryAfter { get; set; } = "";
        public byte[]? BeforeBlocks { get; set; }
        public byte[] AfterBlocks { get; set; } = [];
        public string BeforeComment { get; set; } = "";
        public string AfterComment { get; set; } = "";
    }
}
