using System.Text.Json;
using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Storage.Sqlite;

/// <summary>Atomic report/revision commits, template state, event linkage and report wakes.</summary>
internal sealed class ReportRepository
{
    private readonly SqliteStoreContext _context;

    internal ReportRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public Task SaveReportAsync(
        string trackId,
        ReportDocument report,
        byte[] compressedRevisionBlocks,
        string? authorSessionId,
        string commitMessage,
        JsonElement nextTemplateState,
        EventEnvelopeDto evt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(compressedRevisionBlocks);
        ArgumentNullException.ThrowIfNull(commitMessage);
        ArgumentNullException.ThrowIfNull(evt);
        _context.EnsureInitialized();

        var blocksJson = SqliteJsonUpcaster.SerializeReportBlocks(report.Blocks);
        var effectiveBlocksZstd = compressedRevisionBlocks.Length > 0
            ? compressedRevisionBlocks
            : SqliteJsonUpcaster.CompressReportBlocksZstd(report.Blocks);
        var nextTemplateStateJson = SqliteJsonUpcaster.SerializeElement(nextTemplateState);
        var nowMs = evt.CreatedAtMs > 0 ? evt.CreatedAtMs : _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                var previous = await conn.QuerySingleAsync<TodayPreviousReport>(new CommandDefinition(
                    "SELECT blocks_json AS Blocks, summary AS Summary, leading_comment AS Comment FROM reports WHERE track_id = @TrackId;",
                    new { TrackId = trackId }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                var beforeBlocks = SqliteJsonUpcaster.CompressReportBlocksZstd(SqliteJsonUpcaster.DeserializeReportBlocks(previous.Blocks));
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO reports (track_id, doc_rev, summary, leading_comment, blocks_json, updated_at_ms)
                        VALUES (@TrackId, @DocRev, @Summary, @LeadingComment, @BlocksJson, @UpdatedAtMs)
                        ON CONFLICT(track_id) DO UPDATE SET
                            doc_rev = excluded.doc_rev,
                            summary = excluded.summary,
                            leading_comment = excluded.leading_comment,
                            blocks_json = excluded.blocks_json,
                            updated_at_ms = excluded.updated_at_ms;
                        """,
                        new
                        {
                            TrackId = trackId,
                            report.DocRev,
                            report.Summary,
                            report.LeadingComment,
                            BlocksJson = blocksJson,
                            UpdatedAtMs = nowMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO report_revisions (track_id, doc_rev, author_session_id, message, blocks_zstd, created_at_ms)
                        VALUES (@TrackId, @DocRev, @AuthorSessionId, @Message, @BlocksZstd, @CreatedAtMs)
                        ON CONFLICT(track_id, doc_rev) DO UPDATE SET
                            author_session_id = excluded.author_session_id,
                            message = excluded.message,
                            blocks_zstd = excluded.blocks_zstd,
                            created_at_ms = excluded.created_at_ms;
                        """,
                        new
                        {
                            TrackId = trackId,
                            report.DocRev,
                            AuthorSessionId = authorSessionId,
                            Message = commitMessage,
                            BlocksZstd = effectiveBlocksZstd,
                            CreatedAtMs = nowMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE tracks
                        SET template_state_json = @NextTemplateStateJson,
                            updated_at_ms = @UpdatedAtMs
                        WHERE id = @TrackId;
                        """,
                        new
                        {
                            TrackId = trackId,
                            NextTemplateStateJson = nextTemplateStateJson,
                            UpdatedAtMs = nowMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                var reportEventId = await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE report_revisions SET event_id = @EventId, summary = @Summary, leading_comment = @LeadingComment, before_blocks_zstd = @BeforeBlocks, summary_before = @BeforeSummary, leading_comment_before = @BeforeComment WHERE track_id = @TrackId AND doc_rev = @DocRev;",
                    new { EventId = reportEventId, TrackId = trackId, report.DocRev, report.Summary, report.LeadingComment, BeforeBlocks = beforeBlocks, BeforeSummary = previous.Summary, BeforeComment = previous.Comment }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                if (authorSessionId is null)
                    await TrackWakesRepository.EnqueueWakeInternalAsync(conn, tx, TrackWakeRequests.Report(trackId, report.DocRev, evt.Payload), nowMs, innerCt).ConfigureAwait(false);
            },
            ct);
    }

    public async Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(
        string trackId,
        int docRev,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        _context.EnsureInitialized();

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var compressed = await conn.ExecuteScalarAsync<byte[]?>(
            new CommandDefinition(
                """
                SELECT blocks_zstd
                FROM report_revisions
                WHERE track_id = @TrackId AND doc_rev = @DocRev;
                """,
                new { TrackId = trackId, DocRev = docRev },
                cancellationToken: ct)).ConfigureAwait(false);

        return SqliteJsonUpcaster.DecompressReportBlocksZstd(compressed);
    }

    private sealed class TodayPreviousReport
    {
        public string Blocks { get; set; } = "[]";
        public string Summary { get; set; } = "";
        public string Comment { get; set; } = "";
    }
}
