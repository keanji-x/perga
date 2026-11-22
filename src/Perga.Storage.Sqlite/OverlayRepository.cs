using Dapper;
using Perga.Contracts;
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

    private static readonly string ListTrackOverlaysSql = $$"""
        SELECT tr.id AS TrackId,
               tr.area_id AS AreaId,
               tr.closed_at_ms AS ClosedAtMs,
               CASE WHEN EXISTS (
                   SELECT 1 FROM agent_sessions s WHERE s.track_id = tr.id AND s.archived_at_ms IS NULL
                   AND LOWER(s.status) IN ('queued', 'running', 'stopping', 'compacting')
               ) OR EXISTS (
                   SELECT 1 FROM tasks t WHERE t.track_id = tr.id AND LOWER(t.status) IN ('running', 'verifying')
               ) OR EXISTS (
                   SELECT 1 FROM track_workspace_preparations p WHERE p.track_id = tr.id
                   AND json_extract(p.intent_json, '$.Phase') IN ({{(int)WorkspacePreparationPhase.Pending}}, {{(int)WorkspacePreparationPhase.Provisioning}}, {{(int)WorkspacePreparationPhase.Mounting}}, {{(int)WorkspacePreparationPhase.Initializing}}, {{(int)WorkspacePreparationPhase.Provisioned}})
               ) THEN 1 ELSE 0 END AS IsWorking,
               (SELECT COUNT(1) FROM asks a WHERE a.track_id = tr.id AND LOWER(a.status) = 'open') AS OpenAskCount,
               CASE WHEN EXISTS (
                   SELECT 1 FROM agent_sessions s WHERE s.track_id = tr.id AND s.archived_at_ms IS NULL
                   AND LOWER(s.status) = 'unknown' AND (
                       NOT EXISTS (SELECT 1 FROM tasks tk WHERE tk.session_id = s.id AND (tk.kind <> 'flow_wake' OR tk.block_id <> 'flow:' || tk.task_key OR json_type(tk.context_json, '$.flowNode') IS NOT 'true') AND LOWER(tk.status) IN ('pending', 'running', 'verifying'))
                       OR EXISTS (SELECT 1 FROM tasks tk WHERE tk.session_id = s.id AND LOWER(tk.status) IN ('running', 'verifying'))
                   )
               ) THEN 1 ELSE 0 END AS HasBlockedSession,
               CASE WHEN EXISTS (
                   SELECT 1 FROM track_workspace_preparations p WHERE p.track_id = tr.id
                   AND json_extract(p.intent_json, '$.Phase') IN ({{(int)WorkspacePreparationPhase.Failed}}, {{(int)WorkspacePreparationPhase.Unknown}})
               ) THEN 1 ELSE 0 END AS WorkspaceFailed,
               MAX(
                   tr.updated_at_ms,
                   COALESCE((SELECT MAX(s2.updated_at_ms) FROM agent_sessions s2 WHERE s2.track_id = tr.id), tr.updated_at_ms),
                   COALESCE((SELECT MAX(t2.updated_at_ms) FROM tasks t2 WHERE t2.track_id = tr.id), tr.updated_at_ms),
                   COALESCE((SELECT MAX(COALESCE(a2.answered_at_ms, a2.created_at_ms)) FROM asks a2 WHERE a2.track_id = tr.id), tr.updated_at_ms),
                   COALESCE((SELECT MAX(m2.created_at_ms) FROM mails m2 WHERE m2.to_track_id = tr.id), tr.updated_at_ms)
               ) AS UpdatedAtMs
        FROM tracks tr
        WHERE (@IncludeClosed = 1 OR tr.closed_at_ms IS NULL)
        ORDER BY tr.updated_at_ms DESC, tr.id ASC;
        """;

    public async Task<IReadOnlyList<TrackOverlayDto>> ListTrackOverlaysAsync(
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TrackOverlayRow>(
            new CommandDefinition(
                ListTrackOverlaysSql,
                new { IncludeClosed = includeClosed ? 1 : 0 },
                cancellationToken: ct)).ConfigureAwait(false);

        var watermarks = (await conn.QueryAsync<OverlayWatermarkRow>(new CommandDefinition("""
            SELECT s.track_id AS TrackId, s.id AS SessionId, MAX(t.completed_at_ms) AS LastNotifiableTurnAtMs
            FROM agent_sessions s JOIN session_turns t ON t.session_id = s.id
            JOIN tracks tr ON tr.id = s.track_id
            WHERE s.archived_at_ms IS NULL AND NOT EXISTS (SELECT 1 FROM tasks tk WHERE tk.session_id = s.id AND (tk.kind <> 'flow_wake' OR tk.block_id <> 'flow:' || tk.task_key OR json_type(tk.context_json, '$.flowNode') IS NOT 'true') AND LOWER(tk.status) IN ('pending', 'running', 'verifying'))
              AND LOWER(t.status) IN ('completed', 'failed') AND t.completed_at_ms IS NOT NULL
              AND (@IncludeClosed = 1 OR tr.closed_at_ms IS NULL)
            GROUP BY s.track_id, s.id ORDER BY s.id;
            """, new { IncludeClosed = includeClosed ? 1 : 0 }, cancellationToken: ct)).ConfigureAwait(false))
            .ToLookup(r => r.TrackId);
        return rows.Select(r => new TrackOverlayDto(r.TrackId, r.AreaId,
            r.ClosedAtMs is null && r.IsWorking != 0,
            r.ClosedAtMs is null ? (int)r.OpenAskCount : 0, 0,
            r.ClosedAtMs is null && r.HasBlockedSession != 0,
            r.ClosedAtMs is null && r.WorkspaceFailed != 0, r.UpdatedAtMs,
            watermarks[r.TrackId].Select(w => new NotifiableSessionWatermarkDto(w.SessionId, w.LastNotifiableTurnAtMs)).ToArray())).ToList();
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
    }
}
