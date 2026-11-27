using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
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
                WHEN json_type(i.payload_json, '$.blobRef') = 'text' AND json_type(i.payload_json, '$.preview') = 'text'
                THEN overlay_blob_text(SUBSTR(json_extract(i.payload_json, '$.preview'), 1, @BlobPreviewReadLength))
            END, @Whitespace), '')
        END
        """;

    // Seek the newest eligible item within each session, then compare IDs across sessions.
    // The assistant-only index keeps the normal lookup independent of transcript length.
    private static readonly string ReplyOrSummarySql = $"""
        COALESCE(
            (SELECT '{TrackOverlayPreviewKind.Reply}:' || SUBSTR({ReplyTextSql}, 1, @PreviewReadLength)
             FROM session_items i WHERE i.id = (
                 SELECT MAX((
                     SELECT i.id
                     FROM session_items i INDEXED BY idx_items_session_assistant_latest
                     WHERE i.session_id = s.id AND i.kind = 'assistant_text' AND ({ReplyTextSql}) IS NOT NULL
                     ORDER BY i.id DESC LIMIT 1
                 ))
                 FROM agent_sessions s
                 WHERE s.track_id = tr.id AND s.archived_at_ms IS NULL AND {ExcludeTaskOwnedSessionSql}
             )),
            (SELECT '{TrackOverlayPreviewKind.Summary}:' || SUBSTR(NULLIF(LTRIM(r.summary, @Whitespace), ''), 1, @PreviewReadLength)
             FROM reports r WHERE r.track_id = tr.id)
        )
        """;

    private static string FallbackPreviewSql(string includeBlockedSessionError, string includeWorkspaceError) => $"""
        COALESCE(
            CASE WHEN tr.closed_at_ms IS NULL THEN COALESCE(
                CASE WHEN {includeBlockedSessionError} = 1 THEN
                    (SELECT '{TrackOverlayPreviewKind.Error}:' || SUBSTR(COALESCE(
                        NULLIF(LTRIM(s.unknown_last_error, @Whitespace), ''),
                        NULLIF(LTRIM(t.error_text, @Whitespace), '')), 1, @PreviewReadLength)
                     FROM agent_sessions s
                     LEFT JOIN session_turns t ON t.session_id = s.id AND t.id = s.unknown_turn_id
                     WHERE s.track_id = tr.id AND s.archived_at_ms IS NULL AND ({BlockingSessionSql})
                       AND (LTRIM(s.unknown_last_error, @Whitespace) <> '' OR LTRIM(t.error_text, @Whitespace) <> '')
                     ORDER BY s.updated_at_ms DESC, s.id DESC LIMIT 1)
                END,
                CASE WHEN {includeWorkspaceError} = 1 THEN
                    CASE WHEN {WorkspaceFailedSql} THEN
                        '{TrackOverlayPreviewKind.Error}:' || SUBSTR(NULLIF(LTRIM(CASE
                            WHEN json_type(p.intent_json, '$.FailureDetail') = 'text'
                            THEN json_extract(p.intent_json, '$.FailureDetail')
                        END, @Whitespace), ''), 1, @PreviewReadLength)
                    END
                END
            ) END,
            {ReplyOrSummarySql}
        )
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
               COALESCE(
                   CASE WHEN tr.closed_at_ms IS NULL THEN
                       (SELECT '{{TrackOverlayPreviewKind.Ask}}:' || SUBSTR(NULLIF(LTRIM(CASE
                           WHEN json_valid(a.questions_json) = 1 THEN CASE
                               WHEN json_type(a.questions_json, '$[0].question') = 'text'
                               THEN json_extract(a.questions_json, '$[0].question')
                               WHEN json_type(a.questions_json, '$[0].Question') = 'text'
                               THEN json_extract(a.questions_json, '$[0].Question')
                           END
                           END, @Whitespace), ''), 1, @PreviewReadLength)
                        FROM asks a
                        WHERE a.track_id = tr.id AND LOWER(a.status) = 'open' AND json_valid(a.questions_json) = 1
                        ORDER BY a.created_at_ms DESC, a.id DESC LIMIT 1)
                   END,
                   {{FallbackPreviewSql("1", "1")}}
               ) AS Preview
        FROM tracks tr LEFT JOIN track_workspace_preparations p ON p.track_id = tr.id
        WHERE tr.area_id != @MemorySystemAreaId AND (@IncludeClosed = 1 OR tr.closed_at_ms IS NULL)
        ORDER BY tr.updated_at_ms DESC, tr.id ASC;
        """;

    public async Task<IReadOnlyList<TrackOverlayDto>> ListTrackOverlaysAsync(
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        RegisterPreviewFunctions(conn);
        var filter = new DynamicParameters(new { IncludeClosed = includeClosed ? 1 : 0, MemorySystemAreaId = MemoryTrackBinding.SystemAreaId });
        AddPreviewParameters(filter);
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
            var (text, kind) = ReadPreview(r.Preview);
            return new TrackOverlayDto(r.TrackId, r.AreaId,
                r.ClosedAtMs is null && r.IsWorking != 0,
                r.ClosedAtMs is null ? (int)r.OpenAskCount : 0, 0,
                r.ClosedAtMs is null && r.HasBlockedSession != 0,
                r.ClosedAtMs is null && r.WorkspaceFailed != 0, r.UpdatedAtMs,
                watermarks[r.TrackId].Select(w => new NotifiableSessionWatermarkDto(w.SessionId, w.LastNotifiableTurnAtMs)).ToArray(),
                text, kind);
        }).ToList();
    }

    public async Task<IReadOnlyList<TrackOverlayFallback>> ListTrackOverlayFallbacksAsync(
        IReadOnlyList<TrackOverlayFallbackRequest> requests, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        RegisterPreviewFunctions(conn);
        var filter = new DynamicParameters(new { Requests = JsonSerializer.Serialize(requests, JsonSerializerOptions.Web) });
        AddPreviewParameters(filter);
        var rows = await conn.QueryAsync<TrackOverlayRow>(new CommandDefinition($"""
            SELECT tr.id AS TrackId, {FallbackPreviewSql(
                "json_extract(req.value, '$.includeBlockedSessionError')",
                "json_extract(req.value, '$.includeWorkspaceError')")} AS Preview
            FROM json_each(@Requests) req JOIN tracks tr ON tr.id = json_extract(req.value, '$.trackId')
            LEFT JOIN track_workspace_preparations p ON p.track_id = tr.id;
            """, filter, cancellationToken: ct)).ConfigureAwait(false);

        var fallbacks = new List<TrackOverlayFallback>();
        foreach (var row in rows)
        {
            var (text, kind) = ReadPreview(row.Preview);
            if (text is not null && kind is not null) fallbacks.Add(new(row.TrackId, text, kind.Value));
        }
        return fallbacks;
    }

    private static void RegisterPreviewFunctions(SqliteConnection conn) =>
        conn.CreateFunction<string?, string?>("overlay_blob_text", ColdBlobStore.ReadTextPreview, isDeterministic: true);

    private static void AddPreviewParameters(DynamicParameters parameters)
    {
        // Leave room for whitespace collapse and escaped Unicode in the bounded SQL reads.
        parameters.Add("PreviewReadLength", TrackOverlayPreview.MaxLength * 4);
        parameters.Add("BlobPreviewReadLength", TrackOverlayPreview.MaxLength * 12);
        parameters.Add("Whitespace", " \t\n\v\f\r\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000");
    }

    // SQL prefixes the first nonblank value with its kind so COALESCE can
    // short-circuit the lower-priority reads and return both in one column.
    private static (string? Text, TrackOverlayPreviewKind? Kind) ReadPreview(string? preview)
    {
        if (preview is null) return (null, null);
        var separator = preview.IndexOf(':');
        var text = TrackOverlayPreview.Normalize(preview[(separator + 1)..]);
        return text is null ? (null, null) : (text, Enum.Parse<TrackOverlayPreviewKind>(preview.AsSpan(0, separator)));
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
        public string? Preview { get; init; }
    }
}
