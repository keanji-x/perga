using Dapper;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Read-only Track activity and notification watermark projections.</summary>
internal sealed class OverlayRepository
{
    private readonly SqliteStoreContext _context;

    internal OverlayRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    private const string ExcludeTaskOwnedSessionSql = """
        NOT EXISTS (SELECT 1 FROM tasks tk WHERE tk.session_id = s.id AND tk.owns_session = 1 AND LOWER(tk.status) IN ('pending', 'running', 'verifying'))
        """;

    private const string BlockingSessionSql = $"""
        LOWER(s.status) = 'unknown' AND (
            {ExcludeTaskOwnedSessionSql}
            OR EXISTS (SELECT 1 FROM tasks tk WHERE tk.session_id = s.id AND LOWER(tk.status) IN ('running', 'verifying'))
        )
        """;

    private const string WorkspacePhaseSql = """
        CASE WHEN json_valid(p.intent_json) = 1 THEN json_extract(p.intent_json, '$.Phase') END
        """;

    private static readonly string WorkspaceFailedSql = $"""
        {WorkspacePhaseSql} IN ({(int)WorkspacePreparationPhase.Failed}, {(int)WorkspacePreparationPhase.Unknown})
        """;

    private const string ReplyTextSql = """
        CASE WHEN json_valid(i.payload_json) = 1 THEN
            NULLIF(LTRIM(CASE
                WHEN json_type(i.payload_json, '$.text') = 'text' THEN json_extract(i.payload_json, '$.text')
                WHEN json_type(i.payload_json, '$.textPreview') = 'text' THEN json_extract(i.payload_json, '$.textPreview')
            END, @Whitespace), '')
        END
        """;

    // Seek the latest eligible assistant item per session, then compare IDs across sessions.
    private static readonly string ReplyPreviewSql = $"""
        (SELECT SUBSTR({ReplyTextSql}, 1, @PreviewReadLength)
         FROM session_items i WHERE i.id = (
             SELECT MAX((
                 SELECT i.id FROM session_items i INDEXED BY idx_items_session_assistant_latest
                 WHERE i.session_id = s.id AND i.kind = 'assistant_text' AND ({ReplyTextSql}) IS NOT NULL
                 ORDER BY i.id DESC LIMIT 1
             ))
             FROM agent_sessions s
             WHERE s.track_id = tr.id AND s.archived_at_ms IS NULL AND {ExcludeTaskOwnedSessionSql}
         ))
        """;

    private static readonly string ListTrackOverlaysSql = $$"""
        SELECT tr.id AS TrackId,
               tr.area_id AS AreaId,
               tr.closed_at_ms AS ClosedAtMs,
               CASE WHEN EXISTS (
                   SELECT 1 FROM agent_sessions s WHERE s.track_id = tr.id AND s.archived_at_ms IS NULL
                   AND LOWER(s.status) IN ('queued', 'running', 'stopping', 'compacting')
               ) OR EXISTS (
                   SELECT 1 FROM tasks t WHERE t.track_id = tr.id AND LOWER(t.status) IN ('running', 'verifying')
               ) OR {{WorkspacePhaseSql}} IN ({{(int)WorkspacePreparationPhase.Pending}}, {{(int)WorkspacePreparationPhase.Provisioning}}, {{(int)WorkspacePreparationPhase.Mounting}}, {{(int)WorkspacePreparationPhase.Initializing}}, {{(int)WorkspacePreparationPhase.Provisioned}})
               THEN 1 ELSE 0 END AS IsWorking,
               (SELECT COUNT(1) FROM asks a WHERE a.track_id = tr.id AND LOWER(a.status) = 'open') AS OpenAskCount,
               EXISTS (
                   SELECT 1 FROM agent_sessions s WHERE s.track_id = tr.id AND s.archived_at_ms IS NULL
                   AND ({{BlockingSessionSql}})
               ) AS HasBlockedSession,
               COALESCE({{WorkspaceFailedSql}}, 0) AS WorkspaceFailed,
               MAX(
                   tr.updated_at_ms,
                   COALESCE((SELECT MAX(s2.updated_at_ms) FROM agent_sessions s2 WHERE s2.track_id = tr.id), tr.updated_at_ms),
                   COALESCE((SELECT MAX(t2.updated_at_ms) FROM tasks t2 WHERE t2.track_id = tr.id), tr.updated_at_ms),
                   COALESCE((SELECT MAX(COALESCE(a2.answered_at_ms, a2.created_at_ms)) FROM asks a2 WHERE a2.track_id = tr.id), tr.updated_at_ms),
                   COALESCE((SELECT MAX(m2.created_at_ms) FROM mails m2 WHERE m2.to_track_id = tr.id), tr.updated_at_ms)
               ) AS UpdatedAtMs,
               CASE WHEN tr.closed_at_ms IS NULL THEN (
                   SELECT SUBSTR(LTRIM(CASE WHEN json_valid(a.questions_json) = 1 THEN CASE
                       WHEN json_type(a.questions_json, '$[0].question') = 'text'
                       THEN json_extract(a.questions_json, '$[0].question')
                       WHEN json_type(a.questions_json, '$[0].Question') = 'text'
                       THEN json_extract(a.questions_json, '$[0].Question')
                   END END, @Whitespace), 1, @PreviewReadLength)
                   FROM asks a
                   WHERE a.track_id = tr.id AND LOWER(a.status) = 'open' AND json_valid(a.questions_json) = 1
                   ORDER BY a.created_at_ms DESC, a.id DESC LIMIT 1
               ) END AS AskPreview,
               SUBSTR(COALESCE(NULLIF(LTRIM(blocker.unknown_last_error, @Whitespace), ''),
                   LTRIM(blocked_turn.error_text, @Whitespace)), 1, @PreviewReadLength) AS BlockedErrorPreview,
               blocker.id AS BlockedErrorSessionId,
               CASE WHEN NULLIF(LTRIM(blocker.unknown_last_error, @Whitespace), '') IS NULL
                    THEN blocked_turn.id END AS BlockedErrorTurnId,
               CASE WHEN tr.closed_at_ms IS NULL AND {{WorkspaceFailedSql}} THEN
                   SUBSTR(LTRIM(CASE WHEN json_type(p.intent_json, '$.FailureDetail') = 'text'
                       THEN json_extract(p.intent_json, '$.FailureDetail') END, @Whitespace), 1, @PreviewReadLength)
               END AS WorkspaceErrorPreview,
               {{ReplyPreviewSql}} AS ReplyPreview,
               SUBSTR(LTRIM(r.summary, @Whitespace), 1, @PreviewReadLength) AS SummaryPreview
        FROM tracks tr LEFT JOIN track_workspace_preparations p ON p.track_id = tr.id
        LEFT JOIN agent_sessions blocker ON blocker.id = CASE WHEN tr.closed_at_ms IS NULL THEN (
            SELECT s.id FROM agent_sessions s LEFT JOIN session_turns t ON t.session_id = s.id AND t.id = s.unknown_turn_id
            WHERE s.track_id = tr.id AND s.archived_at_ms IS NULL AND ({{BlockingSessionSql}})
              AND (LTRIM(s.unknown_last_error, @Whitespace) <> '' OR LTRIM(t.error_text, @Whitespace) <> '')
            ORDER BY s.updated_at_ms DESC, s.id DESC LIMIT 1
        ) END
        LEFT JOIN session_turns blocked_turn ON blocked_turn.session_id = blocker.id AND blocked_turn.id = blocker.unknown_turn_id
        LEFT JOIN reports r ON r.track_id = tr.id
        WHERE tr.area_id != @MemorySystemAreaId AND (@IncludeClosed = 1 OR tr.closed_at_ms IS NULL)
        ORDER BY tr.updated_at_ms DESC, tr.id ASC;
        """;

    public async Task<IReadOnlyList<StoredTrackOverlay>> ListStoredTrackOverlaysAsync(
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var filter = new
        {
            IncludeClosed = includeClosed ? 1 : 0,
            MemorySystemAreaId = MemoryTrackBinding.SystemAreaId,
            PreviewReadLength = TrackOverlayPreview.MaxLength * 4,
            Whitespace = " \t\n\v\f\r\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000"
        };
        var rows = await conn.QueryAsync<TrackOverlayRow>(
            new CommandDefinition(
                ListTrackOverlaysSql,
                filter,
                cancellationToken: ct)).ConfigureAwait(false);

        var watermarks = (await conn.QueryAsync<OverlayWatermarkRow>(new CommandDefinition($"""
            SELECT s.track_id AS TrackId, s.id AS SessionId, MAX(t.completed_at_ms) AS LastNotifiableTurnAtMs
            FROM agent_sessions s JOIN session_turns t ON t.session_id = s.id
            JOIN tracks tr ON tr.id = s.track_id
            WHERE tr.area_id != @MemorySystemAreaId AND s.archived_at_ms IS NULL AND {ExcludeTaskOwnedSessionSql}
              AND LOWER(t.status) IN ('completed', 'failed') AND t.completed_at_ms IS NOT NULL
              AND (@IncludeClosed = 1 OR tr.closed_at_ms IS NULL)
            GROUP BY s.track_id, s.id ORDER BY s.id;
            """, filter, cancellationToken: ct)).ConfigureAwait(false))
            .ToLookup(r => r.TrackId);
        return rows.Select(r =>
        {
            var open = r.ClosedAtMs is null;
            var previews = new TrackOverlayPreviewCandidates(
                r.AskPreview, r.BlockedErrorPreview, r.WorkspaceErrorPreview, r.ReplyPreview, r.SummaryPreview);
            var (text, kind) = TrackOverlayPreview.Select(previews, open);
            var overlay = new TrackOverlayDto(r.TrackId, r.AreaId, open && r.IsWorking != 0,
                open ? (int)r.OpenAskCount : 0, 0, open && r.HasBlockedSession != 0, open && r.WorkspaceFailed != 0,
                r.UpdatedAtMs, watermarks[r.TrackId].Select(w => new NotifiableSessionWatermarkDto(w.SessionId, w.LastNotifiableTurnAtMs)).ToArray(),
                text, kind);
            return new StoredTrackOverlay(overlay, previews)
            {
                BlockedErrorSource = r.BlockedErrorSessionId is { } sessionId && r.BlockedErrorTurnId is { } turnId
                    ? (sessionId, turnId) : null
            };
        }).ToList();
    }

    private sealed class OverlayWatermarkRow
    {
        public string TrackId { get; init; } = "";
        public string SessionId { get; init; } = "";
        public long LastNotifiableTurnAtMs { get; init; }
    }

    private sealed class TrackOverlayRow
    {
        public string TrackId { get; init; } = string.Empty;
        public string AreaId { get; init; } = string.Empty;
        public long IsWorking { get; init; }
        public long OpenAskCount { get; init; }
        public long? ClosedAtMs { get; init; }
        public long HasBlockedSession { get; init; }
        public long WorkspaceFailed { get; init; }
        public long UpdatedAtMs { get; init; }
        public string? AskPreview { get; init; }
        public string? BlockedErrorPreview { get; init; }
        public string? BlockedErrorSessionId { get; init; }
        public string? BlockedErrorTurnId { get; init; }
        public string? WorkspaceErrorPreview { get; init; }
        public string? ReplyPreview { get; init; }
        public string? SummaryPreview { get; init; }
    }
}
