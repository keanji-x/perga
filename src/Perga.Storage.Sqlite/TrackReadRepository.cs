using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Storage.Sqlite;

/// <summary>Track queries, row mapping and a consistent aggregate snapshot transaction.</summary>
internal sealed class TrackReadRepository
{
    private readonly SqliteStoreContext _context;
    private readonly TaskRepository _taskRepository;

    internal TrackReadRepository(SqliteStoreContext context, TaskRepository taskRepository)
    {
        _context = context;
        _taskRepository = taskRepository;
    }

    public async Task<IReadOnlyDictionary<string, AreaTrackCounts>> GetAreaTrackCountsAsync(CancellationToken ct = default)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<(string AreaId, long OpenCount, long ClosedCount)>(new CommandDefinition(
            """
            SELECT area_id,
                   SUM(CASE WHEN closed_at_ms IS NULL THEN 1 ELSE 0 END) AS OpenCount,
                   SUM(CASE WHEN closed_at_ms IS NOT NULL THEN 1 ELSE 0 END) AS ClosedCount
            FROM tracks
            GROUP BY area_id;
            """,
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.ToDictionary(
            row => row.AreaId,
            row => new AreaTrackCounts((int)row.OpenCount, (int)row.ClosedCount),
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<TrackDto>> ListTracksAsync(
        string? areaId = null,
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);

        var sql = new StringBuilder(
            """
            SELECT t.id AS Id,
                   t.area_id AS AreaId,
                   t.title AS Title,
                   t.cwd AS Cwd,
                   t.workspace_driver AS WorkspaceDriver,
                   t.workspace_spec_json AS WorkspaceSpecJson,
                   t.lead_session_id AS LeadSessionId,
                   t.template_id AS TemplateId,
                   t.template_snapshot AS TemplateSnapshot,
                   t.template_inputs_json AS TemplateInputsJson,
                   t.data_json AS DataJson,
                   t.data_rev AS DataRev,
                   t.flow_spec_json AS FlowSpecJson,
                   t.flow_rev AS FlowRev,
                   t.flow_file_sha256 AS FlowFileSha256,
                   t.enabled_plugins_json AS EnabledPluginsJson,
                   t.closed_at_ms AS ClosedAtMs,
                   t.pinned_at_ms AS PinnedAtMs,
                   t.sort_order AS SortOrder,
                   t.created_at_ms AS CreatedAtMs,
                   t.updated_at_ms AS UpdatedAtMs,
                   t.execution_target_id AS ExecutionTargetId
            FROM tracks t
            WHERE 1 = 1
            """);

        if (!string.IsNullOrWhiteSpace(areaId))
        {
            sql.Append(" AND t.area_id = @AreaId");
        }

        if (!includeClosed)
        {
            sql.Append(" AND t.closed_at_ms IS NULL");
        }

        sql.Append(" ORDER BY CASE WHEN t.pinned_at_ms IS NOT NULL THEN 0 ELSE 1 END ASC, t.sort_order ASC, t.created_at_ms ASC, t.id ASC;");

        var rows = await conn.QueryAsync<TrackRow>(
            new CommandDefinition(
                sql.ToString(),
                new { AreaId = areaId },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapTrack).ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetReportSummariesAsync(
        IReadOnlyCollection<string> trackIds, CancellationToken ct)
    {
        if (trackIds.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<(string TrackId, string Summary)>(new CommandDefinition(
            """
            SELECT track_id, summary
            FROM reports
            WHERE track_id IN (SELECT value FROM json_each(@TrackIds));
            """,
            new { TrackIds = JsonSerializer.Serialize(trackIds) },
            cancellationToken: ct)).ConfigureAwait(false);

        return rows.ToDictionary(row => row.TrackId, row => row.Summary, StringComparer.Ordinal);
    }

    public async Task<TrackDto?> GetTrackAsync(string trackId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        _context.EnsureInitialized();

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await QueryTrackRowAsync(conn, tx: null, trackId, ct).ConfigureAwait(false);
        return row is null ? null : MapTrack(row);
    }

    public async Task<TrackAggregateSnapshot?> LoadTrackAggregateAsync(string trackId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        _context.EnsureInitialized();

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = conn.BeginTransaction(deferred: true);

        var trackRow = await QueryTrackRowAsync(conn, tx, trackId, ct).ConfigureAwait(false);
        if (trackRow is null)
        {
            return null;
        }

        var trackDto = MapTrack(trackRow);

        var reportRow = await conn.QuerySingleOrDefaultAsync<ReportRow>(
            new CommandDefinition(
                """
                SELECT track_id AS TrackId,
                       doc_rev AS DocRev,
                       summary AS Summary,
                       leading_comment AS LeadingComment,
                       blocks_json AS BlocksJson,
                       updated_at_ms AS UpdatedAtMs
                FROM reports
                WHERE track_id = @TrackId;
                """,
                new { TrackId = trackId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

        var reportDoc = reportRow is null
            ? new ReportDocument(0, string.Empty, string.Empty, Array.Empty<ReportBlock>())
            : new ReportDocument(
                DocRev: (int)reportRow.DocRev,
                Summary: reportRow.Summary,
                LeadingComment: reportRow.LeadingComment,
                Blocks: SqliteJsonUpcaster.DeserializeReportBlocks(reportRow.BlocksJson));

        var taskRows = await conn.QueryAsync<TaskRepository.TaskRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       track_id AS TrackId,
                       block_id AS BlockId,
                       task_key AS TaskKey,
                       generation AS Generation,
                       kind AS Kind,
                       provider AS Provider,
                       role AS Role,
                       access_mode AS AccessMode,
                       ready AS Ready,
                       depends_on_json AS DependsOnJson,
                       goal_or_cmd AS GoalOrCmd,
                       context_json AS ContextJson,
                       acceptance AS Acceptance,
                       gate_json AS GateJson,
                       status AS Status,
                       status_detail AS StatusDetail,
                       semantic_verdict AS SemanticVerdict,
                       semantic_note AS SemanticNote,
                       session_id AS SessionId,
                       run_count AS RunCount,
                       worker_result_json AS WorkerResultJson,
                       meta_json AS MetaJson,
                       gate_verdict_json AS GateVerdictJson,
                       started_at_ms AS StartedAtMs,
                       finished_at_ms AS FinishedAtMs,
                       outcome AS Outcome,
                       outcome_summary AS OutcomeSummary,
                       trigger_run_key AS TriggerRunKey,
                       archived_at_ms AS ArchivedAtMs,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs
                FROM tasks
                WHERE track_id = @TrackId
                ORDER BY created_at_ms ASC, id ASC;
                """,
                new { TrackId = trackId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

        var sessionRows = await conn.QueryAsync<SessionRepository.SessionRow>(
            new CommandDefinition(
                $"""
                SELECT {SessionRepository.SessionColumns}
                FROM agent_sessions
                WHERE track_id = @TrackId
                ORDER BY created_at_ms ASC, id ASC;
                """,
                new { TrackId = trackId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

        var openAskRows = await conn.QueryAsync<CorrespondenceRepository.AskRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       track_id AS TrackId,
                       status AS Status,
                       questions_json AS QuestionsJson,
                       answer_json AS AnswerJson,
                       created_at_ms AS CreatedAtMs,
                       answered_at_ms AS AnsweredAtMs,
                       delivery AS Delivery, session_id AS SessionId, turn_id AS TurnId, target_session_id AS TargetSessionId
                FROM asks
                WHERE track_id = @TrackId AND LOWER(status) = 'open'
                ORDER BY created_at_ms ASC, id ASC;
                """,
                new { TrackId = trackId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

        return new TrackAggregateSnapshot(
            Track: trackDto,
            Report: reportDoc,
            Tasks: taskRows.Select(_taskRepository.MapTask).ToList(),
            Sessions: sessionRows.Select(r => SessionRepository.MapSession(r, trackDto.LeadSessionId)).ToList(),
            OpenAsks: openAskRows.Select(CorrespondenceRepository.MapAsk).ToList(),
            TemplateSnapshotSource: trackRow.TemplateSnapshot);
    }

    private static async Task<TrackRow?> QueryTrackRowAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        string trackId,
        CancellationToken ct)
    {
        return await conn.QuerySingleOrDefaultAsync<TrackRow>(
            new CommandDefinition(
                """
                SELECT t.id AS Id,
                       t.area_id AS AreaId,
                       t.title AS Title,
                       t.cwd AS Cwd,
                       t.workspace_driver AS WorkspaceDriver,
                       t.workspace_spec_json AS WorkspaceSpecJson,
                       t.lead_session_id AS LeadSessionId,
                       t.template_id AS TemplateId,
                       t.template_snapshot AS TemplateSnapshot,
                       t.template_inputs_json AS TemplateInputsJson,
                       t.data_json AS DataJson,
                       t.data_rev AS DataRev,
                       t.flow_spec_json AS FlowSpecJson,
                       t.flow_rev AS FlowRev,
                       t.flow_file_sha256 AS FlowFileSha256,
                       t.enabled_plugins_json AS EnabledPluginsJson,
                       t.closed_at_ms AS ClosedAtMs,
                       t.pinned_at_ms AS PinnedAtMs,
                       t.sort_order AS SortOrder,
                       t.created_at_ms AS CreatedAtMs,
                       t.updated_at_ms AS UpdatedAtMs,
                       t.execution_target_id AS ExecutionTargetId
                FROM tracks t
                WHERE t.id = @TrackId;
                """,
                new { TrackId = trackId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    private TrackDto MapTrack(TrackRow r) =>
        new(
            Id: r.Id,
            AreaId: r.AreaId,
            Title: r.Title,
            Cwd: r.Cwd,
            WorkspaceDriver: r.WorkspaceDriver,
            WorkspaceSpec: _context.Upcaster.UpcastObjectOrElement(r.WorkspaceSpecJson, "tracks.workspace_spec_json"),
            LeadSessionId: r.LeadSessionId,
            TemplateId: r.TemplateId,
            TemplateInputs: _context.Upcaster.UpcastObjectOrElement(r.TemplateInputsJson, "tracks.template_inputs_json"),
            Data: _context.Upcaster.UpcastObjectOrElement(r.DataJson, "tracks.data_json"),
            DataRev: r.DataRev,
            FlowRev: r.FlowRev,
            FlowSpecJson: r.FlowSpecJson,
            FlowFileSha256: r.FlowFileSha256,
            EnabledPlugins: SqliteJsonUpcaster.DeserializeStringList(r.EnabledPluginsJson),
            ClosedAtMs: r.ClosedAtMs,
            PinnedAtMs: r.PinnedAtMs,
            SortOrder: r.SortOrder,
            CreatedAtMs: r.CreatedAtMs,
            UpdatedAtMs: r.UpdatedAtMs,
            ExecutionTargetId: string.IsNullOrWhiteSpace(r.ExecutionTargetId) ? "local" : r.ExecutionTargetId);

    private sealed class TrackRow
    {
        public string Id { get; init; } = string.Empty;
        public string AreaId { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Cwd { get; init; } = string.Empty;
        public string WorkspaceDriver { get; init; } = "direct";
        public string WorkspaceSpecJson { get; init; } = "{}";
        public string? LeadSessionId { get; init; }
        public string? TemplateId { get; init; }
        public string? TemplateSnapshot { get; init; }
        public string TemplateInputsJson { get; init; } = "{}";
        public string DataJson { get; init; } = "{}";
        public long DataRev { get; init; }
        public string FlowSpecJson { get; init; } = """{"rev":0,"nodes":[]}""";
        public long FlowRev { get; init; }
        public string? FlowFileSha256 { get; init; }
        public string EnabledPluginsJson { get; init; } = "[]";
        public long? ClosedAtMs { get; init; }
        public long? PinnedAtMs { get; init; }
        public double SortOrder { get; init; }
        public long CreatedAtMs { get; init; }
        public long UpdatedAtMs { get; init; }
        public string ExecutionTargetId { get; init; } = "local";
    }

    private sealed class ReportRow
    {
        public string TrackId { get; init; } = string.Empty;
        public long DocRev { get; init; }
        public string Summary { get; init; } = string.Empty;
        public string LeadingComment { get; init; } = string.Empty;
        public string BlocksJson { get; init; } = "[]";
        public long UpdatedAtMs { get; init; }
    }
}
