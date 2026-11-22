using Dapper;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Storage.Sqlite;

/// <summary>Read-only daily Track, calendar and report revision projections.</summary>
internal sealed class TodayRepository
{
    private readonly SqliteStoreContext _context;

    internal TodayRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public async Task<string?> GetDailyTrackIdAsync(string date, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT track_id FROM daily_tracks WHERE date = @Date;", new { Date = date }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DailyTrackCalendarPresence>> ListDailyTrackPresenceAsync(
        IReadOnlyList<string> dates, CancellationToken ct)
    {
        if (dates.Count == 0)
            return [];
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<DailyPresenceRow>(new CommandDefinition("""
            SELECT d.date AS Date,
                   1 AS HasDailyTrack,
                   CASE WHEN r.track_id IS NOT NULL AND (
                       trim(r.leading_comment) <> '' OR
                       (r.blocks_json IS NOT NULL AND trim(r.blocks_json) <> '' AND trim(r.blocks_json) <> '[]')
                   ) THEN 1 ELSE 0 END AS HasReport
            FROM daily_tracks d
            JOIN tracks t ON t.id = d.track_id
            LEFT JOIN reports r ON r.track_id = d.track_id
            WHERE d.date IN @Dates;
            """, new { Dates = dates }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new DailyTrackCalendarPresence(r.Date, r.HasDailyTrack != 0, r.HasReport != 0)).ToArray();
    }

    public async Task<IReadOnlyList<TodayScheduleEntry>> ListCalendarSchedulesInWindowAsync(
        long startMs, long endMs, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TodayScheduleRow>(new CommandDefinition($"""
            SELECT s.id AS Id, s.track_id AS TrackId, t.title AS TrackTitle,
                   s.title AS Title, s.schedule_kind AS ScheduleKind,
                   s.cron_expr AS CronExpr, s.timezone AS Timezone,
                   s.next_fire_at_ms AS NextFireAtMs,
                   s.created_at_ms AS CreatedAtMs,
                   s.last_fired_at_ms AS LastFiredAtMs,
                   s.max_occurrences AS MaxOccurrences,
                   s.fired_count AS FiredCount,
                   s.state AS State
            FROM track_schedules s
            JOIN tracks t ON t.id = s.track_id
            WHERE t.closed_at_ms IS NULL AND s.node_key IS NOT NULL AND (
                (s.state = '{TrackScheduleStates.Active}' AND s.next_fire_at_ms IS NOT NULL AND (
                    s.schedule_kind = '{TrackScheduleKinds.Cron}'
                    OR (s.next_fire_at_ms >= @StartMs AND s.next_fire_at_ms < @EndMs)
                    OR (s.last_fired_at_ms >= @StartMs AND s.last_fired_at_ms < @EndMs)))
                OR (s.state = '{TrackScheduleStates.Done}' AND s.last_fired_at_ms IS NOT NULL
                    AND s.last_fired_at_ms >= @StartMs AND s.last_fired_at_ms < @EndMs))
            ORDER BY s.next_fire_at_ms ASC, s.track_id ASC, s.id ASC;
            """, new { StartMs = startMs, EndMs = endMs }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(r => new TodayScheduleEntry(
            r.Id, r.TrackId, r.TrackTitle, r.Title, r.ScheduleKind, r.CronExpr, r.Timezone,
            r.NextFireAtMs, r.CreatedAtMs, r.LastFiredAtMs,
            r.MaxOccurrences.HasValue ? checked((int)r.MaxOccurrences.Value) : null,
            checked((int)r.FiredCount), r.State)).ToArray();
    }

    public async Task<IReadOnlyList<DailyReportRevision>> ListDailyReportRevisionsAsync(
        long startMs, long endMs, long throughEventId, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
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

    private sealed class DailyPresenceRow
    {
        public string Date { get; set; } = "";
        public int HasDailyTrack { get; set; }
        public int HasReport { get; set; }
    }

    private sealed class TodayScheduleRow
    {
        public string Id { get; set; } = "";
        public string TrackId { get; set; } = "";
        public string TrackTitle { get; set; } = "";
        public string Title { get; set; } = "";
        public string ScheduleKind { get; set; } = "";
        public string? CronExpr { get; set; }
        public string Timezone { get; set; } = "";
        public long? NextFireAtMs { get; set; }
        public long CreatedAtMs { get; set; }
        public long? LastFiredAtMs { get; set; }
        public long? MaxOccurrences { get; set; }
        public long FiredCount { get; set; }
        public string State { get; set; } = "";
    }
}
