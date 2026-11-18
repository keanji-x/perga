using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Storage.Sqlite;

/// <summary>
/// Full SQLite WAL implementation of <see cref="ITrackStore"/> and <see cref="ITrackReadStore"/>
/// backed by <see cref="SqliteSingleWriter"/> micro-batching, lock-free <c>Mode=ReadOnly</c> snapshot readers,
/// <c>&gt; 64 KB</c> Zstd cold blob offloading, and deterministic sliding-window GC.
/// </summary>
public sealed class SqliteTrackStore : ITrackStore, IAsyncDisposable
{
    private const long DayMs = 86_400_000L;
    private const long EventRetentionWindowMs = 7L * DayMs;
    private const int EventRetentionMaxRows = 50_000;
    private const int EventPruneBatchSize = 1_000;
    private const int ActiveRevisionFullWindow = 100;
    private const long SettledRetentionWindowMs = 14L * DayMs;
    private const int ToolResultInlineCompactThresholdBytes = 4_096;

    private readonly SqliteStorageOptions _options;
    private readonly SqliteMigrator _migrator;
    private readonly SqliteSingleWriter _writer;
    private readonly ColdBlobStore _coldBlobStore;
    private readonly SqliteJsonUpcaster _upcaster;
    private readonly TimeProvider _timeProvider;
    private readonly string _readConnectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private volatile IReadOnlyList<InterruptedAttemptLeaseDto> _bootInterruptedLeases = Array.Empty<InterruptedAttemptLeaseDto>();
    private bool _initialized;
    private int _disposed;

    public SqliteTrackStore(
        string dbPath,
        string? dataDir = null,
        TimeProvider? timeProvider = null)
        : this(
            new SqliteStorageOptions
            {
                DbPath = dbPath,
                DataDir = dataDir
            },
            migrator: null,
            timeProvider: timeProvider)
    {
    }

    public SqliteTrackStore(
        SqliteStorageOptions options,
        SqliteMigrator? migrator = null,
        TimeProvider? timeProvider = null,
        SqliteJsonUpcaster? upcaster = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DbPath);

        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _upcaster = upcaster ?? SqliteJsonUpcaster.Default;
        _migrator = migrator ?? new SqliteMigrator(timeProvider: _timeProvider);
        _writer = new SqliteSingleWriter(options);
        _coldBlobStore = new ColdBlobStore(options, _upcaster);

        var readCsb = new SqliteConnectionStringBuilder
        {
            DataSource = options.GetFullDbPath(),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
            DefaultTimeout = options.BusyTimeoutSeconds
        };
        _readConnectionString = readCsb.ToString();
    }

    public SqliteStorageOptions Options => _options;
    public SqliteJsonUpcaster Upcaster => _upcaster;

    public static async Task<SqliteTrackStore> OpenAsync(
        string dbPath,
        string? dataDir = null,
        CancellationToken ct = default)
    {
        var store = new SqliteTrackStore(dbPath, dataDir);
        try
        {
            await store.InitializeAsync(ct).ConfigureAwait(false);
            return store;
        }
        catch
        {
            await store.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_options.ResolveDataDir());
            await _migrator.MigrateAsync(_options.GetFullDbPath(), ct).ConfigureAwait(false);
            await _writer.InitializeAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Areas
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<AreaRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       name AS Name,
                       color AS Color,
                       default_cwd AS DefaultCwd,
                       folders_json AS FoldersJson,
                       sort_order AS SortOrder,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs
                FROM areas
                ORDER BY sort_order ASC, created_at_ms ASC, id ASC;
                """,
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapArea).ToList();
    }

    public async Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<AreaRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       name AS Name,
                       color AS Color,
                       default_cwd AS DefaultCwd,
                       folders_json AS FoldersJson,
                       sort_order AS SortOrder,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs
                FROM areas
                WHERE id = @AreaId;
                """,
                new { AreaId = areaId },
                cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : MapArea(row);
    }

    public Task<AreaDto> UpsertAreaAsync(AreaDto area, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);
        EnsureInitialized();

        var foldersJson = SqliteJsonUpcaster.SerializeStringList(area.Folders);

        return _writer.EnqueueAsync<AreaDto>(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO areas (id, name, color, default_cwd, folders_json, sort_order, created_at_ms, updated_at_ms)
                        VALUES (@Id, @Name, @Color, @DefaultCwd, @FoldersJson, @SortOrder, @CreatedAtMs, @UpdatedAtMs)
                        ON CONFLICT(id) DO UPDATE SET
                            name = excluded.name,
                            color = excluded.color,
                            default_cwd = excluded.default_cwd,
                            folders_json = excluded.folders_json,
                            sort_order = excluded.sort_order,
                            updated_at_ms = excluded.updated_at_ms;
                        """,
                        new
                        {
                            area.Id,
                            area.Name,
                            area.Color,
                            area.DefaultCwd,
                            FoldersJson = foldersJson,
                            area.SortOrder,
                            area.CreatedAtMs,
                            area.UpdatedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                var persisted = await conn.QuerySingleAsync<AreaRow>(
                    new CommandDefinition(
                        """
                        SELECT id AS Id,
                               name AS Name,
                               color AS Color,
                               default_cwd AS DefaultCwd,
                               folders_json AS FoldersJson,
                               sort_order AS SortOrder,
                               created_at_ms AS CreatedAtMs,
                               updated_at_ms AS UpdatedAtMs
                        FROM areas
                        WHERE id = @Id;
                        """,
                        new { area.Id },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                return MapArea(persisted);
            },
            ct);
    }

    public async Task DeleteAreaAsync(string areaId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        EnsureInitialized();

        var cascadedTrackIds = await _writer.EnqueueAsync<IReadOnlyList<string>>(
            async (conn, tx, innerCt) =>
            {
                var trackIds = (await conn.QueryAsync<string>(
                    new CommandDefinition(
                        "SELECT id FROM tracks WHERE area_id = @AreaId;",
                        new { AreaId = areaId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false)).ToList();

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM areas WHERE id = @AreaId;",
                        new { AreaId = areaId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                return trackIds;
            },
            ct).ConfigureAwait(false);

        foreach (var trackId in cascadedTrackIds)
        {
            _coldBlobStore.DeleteTrackBlobsDirectory(trackId);
        }
    }

    // -------------------------------------------------------------------------
    // Tracks & Aggregates
    // -------------------------------------------------------------------------

    public Task CreateTrackAsync(
        TrackDto track,
        ReportDocument initialReport,
        string? templateSnapshot,
        AgentSessionDto initialLeadSession,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(initialReport);
        ArgumentNullException.ThrowIfNull(initialLeadSession);
        EnsureInitialized();

        var leadSessionId = string.IsNullOrWhiteSpace(track.LeadSessionId)
            ? initialLeadSession.Id
            : track.LeadSessionId;

        var workspaceSpecJson = SqliteJsonUpcaster.SerializeElement(track.WorkspaceSpec);
        var templateInputsJson = SqliteJsonUpcaster.SerializeElement(track.TemplateInputs);
        var templateStateJson = SqliteJsonUpcaster.SerializeElement(track.TemplateState);
        var enabledPluginsJson = SqliteJsonUpcaster.SerializeStringList(track.EnabledPlugins);
        var blocksJson = SqliteJsonUpcaster.SerializeReportBlocks(initialReport.Blocks);
        var initialBlocksZstd = SqliteJsonUpcaster.CompressReportBlocksZstd(initialReport.Blocks);

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO tracks (
                            id, area_id, parent_track_id, parent_attempt_id, title, cwd,
                            workspace_driver, workspace_spec_json, lead_session_id,
                            template_id, template_snapshot, template_inputs_json, template_state_json,
                            enabled_plugins_json, closed_at_ms, pinned_at_ms, sort_order,
                            created_at_ms, updated_at_ms
                        )
                        VALUES (
                            @Id, @AreaId, @ParentTrackId, @ParentAttemptId, @Title, @Cwd,
                            @WorkspaceDriver, @WorkspaceSpecJson, @LeadSessionId,
                            @TemplateId, @TemplateSnapshot, @TemplateInputsJson, @TemplateStateJson,
                            @EnabledPluginsJson, @ClosedAtMs, @PinnedAtMs, @SortOrder,
                            @CreatedAtMs, @UpdatedAtMs
                        );
                        """,
                        new
                        {
                            track.Id,
                            track.AreaId,
                            track.ParentTrackId,
                            track.ParentAttemptId,
                            track.Title,
                            track.Cwd,
                            track.WorkspaceDriver,
                            WorkspaceSpecJson = workspaceSpecJson,
                            LeadSessionId = leadSessionId,
                            track.TemplateId,
                            TemplateSnapshot = templateSnapshot,
                            TemplateInputsJson = templateInputsJson,
                            TemplateStateJson = templateStateJson,
                            EnabledPluginsJson = enabledPluginsJson,
                            track.ClosedAtMs,
                            track.PinnedAtMs,
                            track.SortOrder,
                            track.CreatedAtMs,
                            track.UpdatedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO reports (track_id, doc_rev, summary, leading_comment, blocks_json, updated_at_ms)
                        VALUES (@TrackId, @DocRev, @Summary, @LeadingComment, @BlocksJson, @UpdatedAtMs);
                        """,
                        new
                        {
                            TrackId = track.Id,
                            initialReport.DocRev,
                            initialReport.Summary,
                            initialReport.LeadingComment,
                            BlocksJson = blocksJson,
                            track.UpdatedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO report_revisions (track_id, doc_rev, author_session_id, message, blocks_zstd, created_at_ms)
                        VALUES (@TrackId, @DocRev, @AuthorSessionId, @Message, @BlocksZstd, @CreatedAtMs);
                        """,
                        new
                        {
                            TrackId = track.Id,
                            initialReport.DocRev,
                            AuthorSessionId = initialLeadSession.Id,
                            Message = "Initial report",
                            BlocksZstd = initialBlocksZstd,
                            track.CreatedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await UpsertSessionInternalAsync(conn, tx, initialLeadSession, innerCt).ConfigureAwait(false);
            },
            ct);
    }

    public async Task<IReadOnlyList<TrackDto>> ListTracksAsync(
        string? areaId = null,
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);

        var sql = new StringBuilder(
            """
            SELECT id AS Id,
                   area_id AS AreaId,
                   parent_track_id AS ParentTrackId,
                   parent_attempt_id AS ParentAttemptId,
                   title AS Title,
                   cwd AS Cwd,
                   workspace_driver AS WorkspaceDriver,
                   workspace_spec_json AS WorkspaceSpecJson,
                   lead_session_id AS LeadSessionId,
                   template_id AS TemplateId,
                   template_snapshot AS TemplateSnapshot,
                   template_inputs_json AS TemplateInputsJson,
                   template_state_json AS TemplateStateJson,
                   enabled_plugins_json AS EnabledPluginsJson,
                   closed_at_ms AS ClosedAtMs,
                   pinned_at_ms AS PinnedAtMs,
                   sort_order AS SortOrder,
                   created_at_ms AS CreatedAtMs,
                   updated_at_ms AS UpdatedAtMs
            FROM tracks
            WHERE 1 = 1
            """);

        if (!string.IsNullOrWhiteSpace(areaId))
        {
            sql.Append(" AND area_id = @AreaId");
        }

        if (!includeClosed)
        {
            sql.Append(" AND closed_at_ms IS NULL");
        }

        sql.Append(" ORDER BY CASE WHEN pinned_at_ms IS NOT NULL THEN 0 ELSE 1 END ASC, sort_order ASC, created_at_ms ASC, id ASC;");

        var rows = await conn.QueryAsync<TrackRow>(
            new CommandDefinition(
                sql.ToString(),
                new { AreaId = areaId },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapTrack).ToList();
    }

    public async Task<TrackDto?> GetTrackAsync(string trackId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await QueryTrackRowAsync(conn, tx: null, trackId, ct).ConfigureAwait(false);
        return row is null ? null : MapTrack(row);
    }

    public async Task<TrackAggregateSnapshot?> LoadTrackAggregateAsync(string trackId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
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

        var taskRows = await conn.QueryAsync<TaskRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       track_id AS TrackId,
                       block_id AS BlockId,
                       task_key AS TaskKey,
                       generation AS Generation,
                       kind AS Kind,
                       provider AS Provider,
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
                       latest_attempt_id AS LatestAttemptId,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs
                FROM tasks
                WHERE track_id = @TrackId
                ORDER BY created_at_ms ASC, id ASC;
                """,
                new { TrackId = trackId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

        var sessionRows = await conn.QueryAsync<SessionRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       track_id AS TrackId,
                       bound_task_id AS BoundTaskId,
                       bound_attempt_id AS BoundAttemptId,
                       title AS Title,
                       provider AS Provider,
                       model AS Model,
                       native_thread_id AS NativeThreadId,
                       epoch AS Epoch,
                       status AS Status,
                       context_usage_ratio AS ContextUsageRatio,
                       archived_at_ms AS ArchivedAtMs,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs
                FROM agent_sessions
                WHERE track_id = @TrackId
                ORDER BY created_at_ms ASC, id ASC;
                """,
                new { TrackId = trackId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

        var openAskRows = await conn.QueryAsync<AskRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       track_id AS TrackId,
                       status AS Status,
                       questions_json AS QuestionsJson,
                       answer_json AS AnswerJson,
                       created_at_ms AS CreatedAtMs,
                       answered_at_ms AS AnsweredAtMs
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
            Tasks: taskRows.Select(MapTask).ToList(),
            Sessions: sessionRows.Select(r => MapSession(r, trackDto.LeadSessionId)).ToList(),
            OpenAsks: openAskRows.Select(MapAsk).ToList(),
            TemplateSnapshotSource: trackRow.TemplateSnapshot);
    }

    public Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(track);
        EnsureInitialized();

        var workspaceSpecJson = SqliteJsonUpcaster.SerializeElement(track.WorkspaceSpec);
        var templateInputsJson = SqliteJsonUpcaster.SerializeElement(track.TemplateInputs);
        var templateStateJson = SqliteJsonUpcaster.SerializeElement(track.TemplateState);
        var enabledPluginsJson = SqliteJsonUpcaster.SerializeStringList(track.EnabledPlugins);

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE tracks
                        SET area_id = @AreaId,
                            parent_track_id = @ParentTrackId,
                            parent_attempt_id = @ParentAttemptId,
                            title = @Title,
                            cwd = @Cwd,
                            workspace_driver = @WorkspaceDriver,
                            workspace_spec_json = @WorkspaceSpecJson,
                            lead_session_id = @LeadSessionId,
                            template_id = @TemplateId,
                            template_inputs_json = @TemplateInputsJson,
                            template_state_json = @TemplateStateJson,
                            enabled_plugins_json = @EnabledPluginsJson,
                            closed_at_ms = @ClosedAtMs,
                            pinned_at_ms = @PinnedAtMs,
                            sort_order = @SortOrder,
                            updated_at_ms = @UpdatedAtMs
                        WHERE id = @Id;
                        """,
                        new
                        {
                            track.Id,
                            track.AreaId,
                            track.ParentTrackId,
                            track.ParentAttemptId,
                            track.Title,
                            track.Cwd,
                            track.WorkspaceDriver,
                            WorkspaceSpecJson = workspaceSpecJson,
                            track.LeadSessionId,
                            track.TemplateId,
                            TemplateInputsJson = templateInputsJson,
                            TemplateStateJson = templateStateJson,
                            EnabledPluginsJson = enabledPluginsJson,
                            track.ClosedAtMs,
                            track.PinnedAtMs,
                            track.SortOrder,
                            track.UpdatedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                if (evt is not null)
                {
                    await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }

    public async Task DeleteTrackAsync(string trackId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        EnsureInitialized();

        await _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM tracks WHERE id = @TrackId;",
                        new { TrackId = trackId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        _coldBlobStore.DeleteTrackBlobsDirectory(trackId);
    }

    // -------------------------------------------------------------------------
    // Reports & Revisions
    // -------------------------------------------------------------------------

    public Task SaveReportAndTasksAsync(
        string trackId,
        ReportDocument report,
        byte[] compressedRevisionBlocks,
        string? authorSessionId,
        string commitMessage,
        IReadOnlyList<TaskDto> syncedTasks,
        JsonElement nextTemplateState,
        EventEnvelopeDto evt,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(compressedRevisionBlocks);
        ArgumentNullException.ThrowIfNull(commitMessage);
        ArgumentNullException.ThrowIfNull(syncedTasks);
        ArgumentNullException.ThrowIfNull(evt);
        EnsureInitialized();

        var blocksJson = SqliteJsonUpcaster.SerializeReportBlocks(report.Blocks);
        var effectiveBlocksZstd = compressedRevisionBlocks.Length > 0
            ? compressedRevisionBlocks
            : SqliteJsonUpcaster.CompressReportBlocksZstd(report.Blocks);
        var nextTemplateStateJson = SqliteJsonUpcaster.SerializeElement(nextTemplateState);
        var nowMs = evt.CreatedAtMs > 0 ? evt.CreatedAtMs : _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
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

                if (syncedTasks.Count == 0)
                {
                    await conn.ExecuteAsync(
                        new CommandDefinition(
                            "DELETE FROM tasks WHERE track_id = @TrackId;",
                            new { TrackId = trackId },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);
                }
                else
                {
                    var keepIds = syncedTasks.Select(static t => t.Id).ToArray();
                    await conn.ExecuteAsync(
                        new CommandDefinition(
                            "DELETE FROM tasks WHERE track_id = @TrackId AND id NOT IN @KeepIds;",
                            new { TrackId = trackId, KeepIds = keepIds },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);

                    foreach (var task in syncedTasks)
                    {
                        await UpsertTaskInternalAsync(conn, tx, task, innerCt).ConfigureAwait(false);
                    }
                }

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

                await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
            },
            ct);
    }

    public async Task<IReadOnlyList<ReportBlock>> GetReportRevisionBlocksAsync(
        string trackId,
        int docRev,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
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

    // -------------------------------------------------------------------------
    // Tasks & Attempts
    // -------------------------------------------------------------------------

    public Task UpsertTaskAndAttemptAsync(
        TaskDto task,
        TaskAttemptDto? attempt,
        JsonElement? nextTemplateState,
        EventEnvelopeDto? evt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        EnsureInitialized();

        var gateVerdictStoredJson = attempt is null
            ? null
            : _coldBlobStore.StoreOrInlineNullable(attempt.TrackId, attempt.GateVerdict);

        var nextTemplateStateJson = nextTemplateState.HasValue
            ? SqliteJsonUpcaster.SerializeElement(nextTemplateState.Value)
            : null;

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await UpsertTaskInternalAsync(conn, tx, task, innerCt).ConfigureAwait(false);

                if (attempt is not null)
                {
                    await conn.ExecuteAsync(
                        new CommandDefinition(
                            """
                            INSERT INTO task_attempts (
                                attempt_id, task_id, track_id, session_id, status,
                                worker_result_json, meta_json, workspace_lease_json, gate_verdict_json,
                                started_at_ms, finished_at_ms
                            )
                            VALUES (
                                @AttemptId, @TaskId, @TrackId, @SessionId, @Status,
                                @WorkerResultJson, @MetaJson, @WorkspaceLeaseJson, @GateVerdictJson,
                                @StartedAtMs, @FinishedAtMs
                            )
                            ON CONFLICT(attempt_id) DO UPDATE SET
                                session_id = excluded.session_id,
                                status = excluded.status,
                                worker_result_json = excluded.worker_result_json,
                                meta_json = excluded.meta_json,
                                workspace_lease_json = excluded.workspace_lease_json,
                                gate_verdict_json = excluded.gate_verdict_json,
                                finished_at_ms = excluded.finished_at_ms;
                            """,
                            new
                            {
                                attempt.AttemptId,
                                attempt.TaskId,
                                attempt.TrackId,
                                attempt.SessionId,
                                attempt.Status,
                                WorkerResultJson = SqliteJsonUpcaster.SerializeNullableElement(attempt.WorkerResult),
                                MetaJson = SqliteJsonUpcaster.SerializeElement(attempt.Meta),
                                WorkspaceLeaseJson = SqliteJsonUpcaster.SerializeNullableElement(attempt.WorkspaceLease),
                                GateVerdictJson = gateVerdictStoredJson,
                                attempt.StartedAtMs,
                                attempt.FinishedAtMs
                            },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);
                }

                if (nextTemplateStateJson is not null)
                {
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
                                task.TrackId,
                                NextTemplateStateJson = nextTemplateStateJson,
                                UpdatedAtMs = task.UpdatedAtMs
                            },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);
                }

                if (evt is not null)
                {
                    await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }

    public async Task<IReadOnlyList<TaskAttemptDto>> ListAttemptsForTaskAsync(string taskId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TaskAttemptRow>(
            new CommandDefinition(
                """
                SELECT attempt_id AS AttemptId,
                       task_id AS TaskId,
                       track_id AS TrackId,
                       session_id AS SessionId,
                       status AS Status,
                       worker_result_json AS WorkerResultJson,
                       meta_json AS MetaJson,
                       workspace_lease_json AS WorkspaceLeaseJson,
                       gate_verdict_json AS GateVerdictJson,
                       started_at_ms AS StartedAtMs,
                       finished_at_ms AS FinishedAtMs
                FROM task_attempts
                WHERE task_id = @TaskId
                ORDER BY started_at_ms ASC, attempt_id ASC;
                """,
                new { TaskId = taskId },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapTaskAttempt).ToList();
    }

    // -------------------------------------------------------------------------
    // Sessions, Turns, & Items
    // -------------------------------------------------------------------------

    public Task UpsertSessionAsync(AgentSessionDto session, EventEnvelopeDto? evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureInitialized();

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await UpsertSessionInternalAsync(conn, tx, session, innerCt).ConfigureAwait(false);
                if (session.IsLead)
                {
                    await conn.ExecuteAsync(
                        new CommandDefinition(
                            "UPDATE tracks SET lead_session_id = @SessionId WHERE id = @TrackId AND lead_session_id IS NULL;",
                            new { SessionId = session.Id, session.TrackId },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);
                }

                if (evt is not null)
                {
                    await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }

    public async Task<string?> FindTrackIdBySessionAsync(string sessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT track_id FROM agent_sessions WHERE id = @SessionId;",
                new { SessionId = sessionId },
                cancellationToken: ct)).ConfigureAwait(false);
    }

    public Task RecordTurnAndItemsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct) =>
        RecordTurnAndItemsAsync(turn, idempotencyKey: null, newItems, updatedSession, ct);

    public Task<SessionTurnDto> RecordTurnAndItemsAsync(
        SessionTurnDto turn,
        string? idempotencyKey,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(newItems);
        EnsureInitialized();

        var effectiveIdempotencyKey = !string.IsNullOrWhiteSpace(idempotencyKey)
            ? idempotencyKey
            : SqliteJsonUpcaster.ExtractIdempotencyKeyFromInput(turn.Input);

        var inputJson = SqliteJsonUpcaster.SerializeElement(turn.Input);
        var usageJson = SqliteJsonUpcaster.SerializeNullableElement(turn.Usage);

        return _writer.EnqueueAsync<SessionTurnDto>(
            async (conn, tx, innerCt) =>
            {
                if (!string.IsNullOrWhiteSpace(effectiveIdempotencyKey))
                {
                    var existingByKey = await conn.QuerySingleOrDefaultAsync<SessionTurnRow>(
                        new CommandDefinition(
                            """
                            SELECT id AS Id,
                                   session_id AS SessionId,
                                   turn_index AS TurnIndex,
                                   epoch AS Epoch,
                                   idempotency_key AS IdempotencyKey,
                                   trigger_kind AS TriggerKind,
                                   input_json AS InputJson,
                                   status AS Status,
                                   usage_json AS UsageJson,
                                   error_text AS ErrorText,
                                   started_at_ms AS StartedAtMs,
                                   completed_at_ms AS CompletedAtMs
                            FROM session_turns
                            WHERE session_id = @SessionId AND idempotency_key = @IdempotencyKey;
                            """,
                            new { turn.SessionId, IdempotencyKey = effectiveIdempotencyKey },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);

                    if (existingByKey is not null && !string.Equals(existingByKey.Id, turn.Id, StringComparison.Ordinal))
                    {
                        return MapTurn(existingByKey);
                    }
                }

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO session_turns (
                            id, session_id, turn_index, epoch, idempotency_key,
                            trigger_kind, input_json, status, usage_json, error_text,
                            started_at_ms, completed_at_ms
                        )
                        VALUES (
                            @Id, @SessionId, @TurnIndex, @Epoch, @IdempotencyKey,
                            @TriggerKind, @InputJson, @Status, @UsageJson, @ErrorText,
                            @StartedAtMs, @CompletedAtMs
                        )
                        ON CONFLICT(id) DO UPDATE SET
                            epoch = excluded.epoch,
                            trigger_kind = excluded.trigger_kind,
                            input_json = excluded.input_json,
                            status = excluded.status,
                            usage_json = excluded.usage_json,
                            error_text = excluded.error_text,
                            completed_at_ms = excluded.completed_at_ms;
                        """,
                        new
                        {
                            turn.Id,
                            turn.SessionId,
                            turn.TurnIndex,
                            turn.Epoch,
                            IdempotencyKey = effectiveIdempotencyKey,
                            turn.TriggerKind,
                            InputJson = inputJson,
                            turn.Status,
                            UsageJson = usageJson,
                            turn.ErrorText,
                            turn.StartedAtMs,
                            turn.CompletedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                if (updatedSession is not null)
                {
                    await UpsertSessionInternalAsync(conn, tx, updatedSession, innerCt).ConfigureAwait(false);
                }

                if (newItems.Count > 0)
                {
                    var trackId = updatedSession?.TrackId ?? await conn.ExecuteScalarAsync<string?>(
                        new CommandDefinition(
                            "SELECT track_id FROM agent_sessions WHERE id = @SessionId;",
                            new { turn.SessionId },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);

                    foreach (var item in newItems)
                    {
                        var payloadJson = !string.IsNullOrWhiteSpace(trackId)
                            ? _coldBlobStore.StoreOrInline(trackId, item.Payload)
                            : SqliteJsonUpcaster.SerializeElement(item.Payload);

                        await conn.ExecuteAsync(
                            new CommandDefinition(
                                """
                                INSERT INTO session_items (session_id, turn_id, kind, payload_json, created_at_ms)
                                VALUES (@SessionId, @TurnId, @Kind, @PayloadJson, @CreatedAtMs);
                                """,
                                new
                                {
                                    item.SessionId,
                                    item.TurnId,
                                    item.Kind,
                                    PayloadJson = payloadJson,
                                    item.CreatedAtMs
                                },
                                transaction: tx,
                                cancellationToken: innerCt)).ConfigureAwait(false);
                    }
                }

                return turn;
            },
            ct);
    }

    public async Task<IReadOnlyList<SessionTurnDto>> ListSessionTurnsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SessionTurnRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       session_id AS SessionId,
                       turn_index AS TurnIndex,
                       epoch AS Epoch,
                       idempotency_key AS IdempotencyKey,
                       trigger_kind AS TriggerKind,
                       input_json AS InputJson,
                       status AS Status,
                       usage_json AS UsageJson,
                       error_text AS ErrorText,
                       started_at_ms AS StartedAtMs,
                       completed_at_ms AS CompletedAtMs
                FROM session_turns
                WHERE session_id = @SessionId
                ORDER BY turn_index ASC, started_at_ms ASC;
                """,
                new { SessionId = sessionId },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapTurn).ToList();
    }

    public async Task<IReadOnlyList<SessionItemDto>> ListSessionItemsAsync(
        string sessionId,
        long afterId = 0,
        int limit = 100,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsureInitialized();

        var clampedLimit = Math.Max(1, limit);
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SessionItemRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       session_id AS SessionId,
                       turn_id AS TurnId,
                       kind AS Kind,
                       payload_json AS PayloadJson,
                       created_at_ms AS CreatedAtMs
                FROM session_items
                WHERE session_id = @SessionId AND id > @AfterId
                ORDER BY id ASC
                LIMIT @Limit;
                """,
                new
                {
                    SessionId = sessionId,
                    AfterId = afterId,
                    Limit = clampedLimit
                },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapSessionItem).ToList();
    }

    // -------------------------------------------------------------------------
    // Asks, Mails, Terminals, & Events
    // -------------------------------------------------------------------------

    public Task UpsertAskAsync(AskDto ask, EventEnvelopeDto evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(evt);
        EnsureInitialized();

        var status = SqliteJsonUpcaster.FormatAskStatus(ask.Status);
        var questionsJson = SqliteJsonUpcaster.SerializeAskQuestions(ask.Questions);
        var answerJson = SqliteJsonUpcaster.SerializeNullableElement(ask.Answer);

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO asks (id, track_id, status, questions_json, answer_json, created_at_ms, answered_at_ms)
                        VALUES (@Id, @TrackId, @Status, @QuestionsJson, @AnswerJson, @CreatedAtMs, @AnsweredAtMs)
                        ON CONFLICT(id) DO UPDATE SET
                            status = excluded.status,
                            questions_json = excluded.questions_json,
                            answer_json = excluded.answer_json,
                            answered_at_ms = excluded.answered_at_ms;
                        """,
                        new
                        {
                            ask.Id,
                            ask.TrackId,
                            Status = status,
                            QuestionsJson = questionsJson,
                            AnswerJson = answerJson,
                            ask.CreatedAtMs,
                            ask.AnsweredAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
            },
            ct);
    }

    public async Task<AskDto?> GetAskAsync(string askId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(askId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<AskRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       track_id AS TrackId,
                       status AS Status,
                       questions_json AS QuestionsJson,
                       answer_json AS AnswerJson,
                       created_at_ms AS CreatedAtMs,
                       answered_at_ms AS AnsweredAtMs
                FROM asks
                WHERE id = @AskId;
                """,
                new { AskId = askId },
                cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : MapAsk(row);
    }

    public Task UpsertMailAsync(MailDto mail, EventEnvelopeDto evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(evt);
        EnsureInitialized();

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO mails (id, from_track_id, to_track_id, summary, body, read_at_ms, created_at_ms)
                        VALUES (@Id, @FromTrackId, @ToTrackId, @Summary, @Body, @ReadAtMs, @CreatedAtMs)
                        ON CONFLICT(id) DO UPDATE SET
                            summary = excluded.summary,
                            body = excluded.body,
                            read_at_ms = excluded.read_at_ms;
                        """,
                        new
                        {
                            mail.Id,
                            mail.FromTrackId,
                            mail.ToTrackId,
                            mail.Summary,
                            mail.Body,
                            mail.ReadAtMs,
                            mail.CreatedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
            },
            ct);
    }

    public Task MarkMailReadAsync(string mailId, long readAtMs, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mailId);
        EnsureInitialized();

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        "UPDATE mails SET read_at_ms = COALESCE(read_at_ms, @ReadAtMs) WHERE id = @MailId;",
                        new { MailId = mailId, ReadAtMs = readAtMs },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);
            },
            ct);
    }

    public async Task<IReadOnlyList<MailDto>> ListMailsAsync(
        string trackId,
        bool unreadOnly = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var sql = unreadOnly
            ? """
              SELECT id AS Id,
                     from_track_id AS FromTrackId,
                     to_track_id AS ToTrackId,
                     summary AS Summary,
                     body AS Body,
                     read_at_ms AS ReadAtMs,
                     created_at_ms AS CreatedAtMs
              FROM mails
              WHERE to_track_id = @TrackId AND read_at_ms IS NULL
              ORDER BY created_at_ms DESC, id ASC;
              """
            : """
              SELECT id AS Id,
                     from_track_id AS FromTrackId,
                     to_track_id AS ToTrackId,
                     summary AS Summary,
                     body AS Body,
                     read_at_ms AS ReadAtMs,
                     created_at_ms AS CreatedAtMs
              FROM mails
              WHERE to_track_id = @TrackId
              ORDER BY created_at_ms DESC, id ASC;
              """;

        var rows = await conn.QueryAsync<MailRow>(
            new CommandDefinition(sql, new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapMail).ToList();
    }

    public Task UpsertTerminalAsync(TerminalDto terminal, EventEnvelopeDto? evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        EnsureInitialized();

        var status = SqliteJsonUpcaster.FormatTerminalStatus(terminal.Status);

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO terminals (
                            id, track_id, attempt_id, idempotency_key, title,
                            program, cwd, status, exit_code, created_at_ms, exited_at_ms
                        )
                        VALUES (
                            @Id, @TrackId, @AttemptId, @IdempotencyKey, @Title,
                            @Program, @Cwd, @Status, @ExitCode, @CreatedAtMs, @ExitedAtMs
                        )
                        ON CONFLICT(id) DO UPDATE SET
                            attempt_id = excluded.attempt_id,
                            idempotency_key = excluded.idempotency_key,
                            title = excluded.title,
                            program = excluded.program,
                            cwd = excluded.cwd,
                            status = excluded.status,
                            exit_code = excluded.exit_code,
                            exited_at_ms = excluded.exited_at_ms;
                        """,
                        new
                        {
                            terminal.Id,
                            terminal.TrackId,
                            terminal.AttemptId,
                            terminal.IdempotencyKey,
                            terminal.Title,
                            terminal.Program,
                            terminal.Cwd,
                            Status = status,
                            terminal.ExitCode,
                            terminal.CreatedAtMs,
                            terminal.ExitedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                if (evt is not null)
                {
                    await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }

    public async Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(string trackId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TerminalRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       track_id AS TrackId,
                       attempt_id AS AttemptId,
                       idempotency_key AS IdempotencyKey,
                       title AS Title,
                       program AS Program,
                       cwd AS Cwd,
                       status AS Status,
                       exit_code AS ExitCode,
                       created_at_ms AS CreatedAtMs,
                       exited_at_ms AS ExitedAtMs
                FROM terminals
                WHERE track_id = @TrackId
                ORDER BY created_at_ms ASC, id ASC;
                """,
                new { TrackId = trackId },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapTerminal).ToList();
    }

    public Task AppendEventAsync(EventEnvelopeDto evt, CancellationToken ct) =>
        AppendEventWithIdAsync(evt, ct);

    public Task<EventEnvelopeDto> AppendEventWithIdAsync(EventEnvelopeDto evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        EnsureInitialized();

        return _writer.EnqueueAsync<EventEnvelopeDto>(
            async (conn, tx, innerCt) =>
            {
                var id = await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                return evt with { EventId = id };
            },
            ct);
    }

    public async Task<IReadOnlyList<EventEnvelopeDto>> ListEventsSinceAsync(
        long sinceEventId,
        string? trackId = null,
        int limit = 500,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        var clampedLimit = Math.Max(1, limit);

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var sql = string.IsNullOrWhiteSpace(trackId)
            ? """
              SELECT id AS Id,
                     area_id AS AreaId,
                     track_id AS TrackId,
                     kind AS Kind,
                     payload_json AS PayloadJson,
                     created_at_ms AS CreatedAtMs
              FROM events
              WHERE id > @SinceEventId
              ORDER BY id ASC
              LIMIT @Limit;
              """
            : """
              SELECT id AS Id,
                     area_id AS AreaId,
                     track_id AS TrackId,
                     kind AS Kind,
                     payload_json AS PayloadJson,
                     created_at_ms AS CreatedAtMs
              FROM events
              WHERE track_id = @TrackId AND id > @SinceEventId
              ORDER BY id ASC
              LIMIT @Limit;
              """;

        var rows = await conn.QueryAsync<EventRow>(
            new CommandDefinition(
                sql,
                new
                {
                    SinceEventId = sinceEventId,
                    TrackId = trackId,
                    Limit = clampedLimit
                },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapEvent).ToList();
    }

    // -------------------------------------------------------------------------
    // Boot Reconciliation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns any in-flight <c>task_attempts</c> carrying a non-null <c>workspace_lease_json</c>
    /// (works whether called before or after <see cref="MarkInFlightInterruptedOnBootAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<InterruptedAttemptLeaseDto>> GetInterruptedAttemptLeasesOnBootAsync(
        CancellationToken ct = default)
    {
        EnsureInitialized();

        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var liveRows = (await conn.QueryAsync<InterruptedLeaseRow>(
            new CommandDefinition(
                """
                SELECT attempt_id AS AttemptId,
                       task_id AS TaskId,
                       track_id AS TrackId,
                       workspace_lease_json AS WorkspaceLeaseJson
                FROM task_attempts
                WHERE LOWER(status) IN ('running', 'verifying')
                  AND workspace_lease_json IS NOT NULL;
                """,
                cancellationToken: ct)).ConfigureAwait(false)).ToList();

        if (liveRows.Count > 0)
        {
            return liveRows
                .Select(static r => new InterruptedAttemptLeaseDto(
                    r.AttemptId,
                    r.TaskId,
                    r.TrackId,
                    SqliteJsonUpcaster.ParseElementOrDefault(r.WorkspaceLeaseJson)))
                .ToList();
        }

        return _bootInterruptedLeases;
    }

    public Task<IReadOnlyList<string>> MarkInFlightInterruptedOnBootAsync(CancellationToken ct)
    {
        EnsureInitialized();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        return _writer.EnqueueAsync<IReadOnlyList<string>>(
            async (conn, tx, innerCt) =>
            {
                var leaseRows = (await conn.QueryAsync<InterruptedLeaseRow>(
                    new CommandDefinition(
                        """
                        SELECT attempt_id AS AttemptId,
                               task_id AS TaskId,
                               track_id AS TrackId,
                               workspace_lease_json AS WorkspaceLeaseJson
                        FROM task_attempts
                        WHERE LOWER(status) IN ('running', 'verifying')
                          AND workspace_lease_json IS NOT NULL;
                        """,
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false)).ToList();

                _bootInterruptedLeases = leaseRows
                    .Select(static r => new InterruptedAttemptLeaseDto(
                        r.AttemptId,
                        r.TaskId,
                        r.TrackId,
                        SqliteJsonUpcaster.ParseElementOrDefault(r.WorkspaceLeaseJson)))
                    .ToList();

                var affectedOpenTrackIds = (await conn.QueryAsync<string>(
                    new CommandDefinition(
                        """
                        SELECT DISTINCT tr.id
                        FROM tracks tr
                        WHERE tr.closed_at_ms IS NULL
                          AND (
                              EXISTS (
                                  SELECT 1 FROM task_attempts a
                                  WHERE a.track_id = tr.id
                                    AND LOWER(a.status) IN ('running', 'verifying')
                              )
                              OR EXISTS (
                                  SELECT 1 FROM session_turns t
                                  JOIN agent_sessions s ON s.id = t.session_id
                                  WHERE s.track_id = tr.id
                                    AND LOWER(t.status) IN ('running', 'verifying')
                              )
                              OR EXISTS (
                                  SELECT 1 FROM agent_sessions s2
                                  WHERE s2.track_id = tr.id
                                    AND LOWER(s2.status) IN ('running', 'verifying')
                              )
                          )
                        ORDER BY tr.id ASC;
                        """,
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false)).ToList();

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE task_attempts
                        SET status = 'interrupted',
                            finished_at_ms = COALESCE(finished_at_ms, @NowMs)
                        WHERE LOWER(status) IN ('running', 'verifying');
                        """,
                        new { NowMs = nowMs },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE session_turns
                        SET status = 'interrupted',
                            completed_at_ms = COALESCE(completed_at_ms, @NowMs)
                        WHERE LOWER(status) IN ('running', 'verifying');
                        """,
                        new { NowMs = nowMs },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE agent_sessions
                        SET status = 'idle',
                            updated_at_ms = @NowMs
                        WHERE LOWER(status) IN ('running', 'verifying');
                        """,
                        new { NowMs = nowMs },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                return affectedOpenTrackIds;
            },
            ct);
    }

    // -------------------------------------------------------------------------
    // Deterministic Sliding-Window GC & Maintenance (DESIGN.md 3.2)
    // -------------------------------------------------------------------------

    public async Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(long nowMs, CancellationToken ct)
    {
        EnsureInitialized();

        var prunedEvents = await PruneEventsInBatchesAsync(nowMs, ct).ConfigureAwait(false);
        var thinnedRevisions = await ThinReportRevisionsAsync(nowMs, ct).ConfigureAwait(false);
        var compactedItems = await SlimEligibleSessionItemsAsync(nowMs, ct).ConfigureAwait(false);
        var reclaimedPages = await VacuumAndCheckpointAsync(ct).ConfigureAwait(false);

        return new DbMaintenanceStatsDto(
            PrunedEvents: prunedEvents,
            ThinnedRevisions: thinnedRevisions,
            CompactedItems: compactedItems,
            ReclaimedPages: reclaimedPages);
    }

    private async Task<int> PruneEventsInBatchesAsync(long nowMs, CancellationToken ct)
    {
        var totalPruned = 0;
        var ageCutoffMs = nowMs - EventRetentionWindowMs;

        // 1A. Prune events older than 7 days in batches of 1,000.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await _writer.EnqueueAsync<int>(
                async (conn, tx, innerCt) =>
                {
                    return await conn.ExecuteAsync(
                        new CommandDefinition(
                            """
                            DELETE FROM events
                            WHERE id IN (
                                SELECT id FROM events
                                WHERE created_at_ms <= @AgeCutoffMs
                                ORDER BY id ASC
                                LIMIT @BatchSize
                            );
                            """,
                            new { AgeCutoffMs = ageCutoffMs, BatchSize = EventPruneBatchSize },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            totalPruned += deleted;
            if (deleted < EventPruneBatchSize)
            {
                break;
            }
        }

        // 1B. Enforce hard cap of 50,000 rows in batches of up to 1,000.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await _writer.EnqueueAsync<int>(
                async (conn, tx, innerCt) =>
                {
                    var remaining = await conn.ExecuteScalarAsync<long>(
                        new CommandDefinition(
                            "SELECT COUNT(1) FROM events;",
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);

                    if (remaining <= EventRetentionMaxRows)
                    {
                        return 0;
                    }

                    var toDelete = (int)Math.Min(EventPruneBatchSize, remaining - EventRetentionMaxRows);
                    return await conn.ExecuteAsync(
                        new CommandDefinition(
                            """
                            DELETE FROM events
                            WHERE id IN (
                                SELECT id FROM events
                                ORDER BY id ASC
                                LIMIT @BatchSize
                            );
                            """,
                            new { BatchSize = toDelete },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            totalPruned += deleted;
            if (deleted == 0)
            {
                break;
            }
        }

        return totalPruned;
    }

    private Task<int> ThinReportRevisionsAsync(long nowMs, CancellationToken ct)
    {
        var closedCutoffMs = nowMs - SettledRetentionWindowMs;

        return _writer.EnqueueAsync<int>(
            async (conn, tx, innerCt) =>
            {
                var trackBounds = (await conn.QueryAsync<RevisionBoundsRow>(
                    new CommandDefinition(
                        """
                        SELECT r.track_id AS TrackId,
                               MIN(r.doc_rev) AS MinRev,
                               MAX(r.doc_rev) AS MaxRev,
                               t.closed_at_ms AS ClosedAtMs
                        FROM report_revisions r
                        JOIN tracks t ON t.id = r.track_id
                        GROUP BY r.track_id;
                        """,
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false)).ToList();

                var thinned = 0;
                foreach (var bound in trackBounds)
                {
                    var isSettledClosed = bound.ClosedAtMs.HasValue && bound.ClosedAtMs.Value <= closedCutoffMs;
                    if (isSettledClosed)
                    {
                        thinned += await conn.ExecuteAsync(
                            new CommandDefinition(
                                """
                                DELETE FROM report_revisions
                                WHERE track_id = @TrackId
                                  AND doc_rev != @MinRev
                                  AND doc_rev != @MaxRev
                                  AND (doc_rev % 10) != 0;
                                """,
                                new
                                {
                                    bound.TrackId,
                                    bound.MinRev,
                                    bound.MaxRev
                                },
                                transaction: tx,
                                cancellationToken: innerCt)).ConfigureAwait(false);
                    }
                    else
                    {
                        var maxThinRev = bound.MaxRev - ActiveRevisionFullWindow;
                        if (maxThinRev >= bound.MinRev)
                        {
                            thinned += await conn.ExecuteAsync(
                                new CommandDefinition(
                                    """
                                    DELETE FROM report_revisions
                                    WHERE track_id = @TrackId
                                      AND doc_rev <= @MaxThinRev
                                      AND doc_rev != @MinRev
                                      AND doc_rev != @MaxRev
                                      AND (doc_rev % 10) != 0;
                                    """,
                                    new
                                    {
                                        bound.TrackId,
                                        MaxThinRev = maxThinRev,
                                        bound.MinRev,
                                        bound.MaxRev
                                    },
                                    transaction: tx,
                                    cancellationToken: innerCt)).ConfigureAwait(false);
                        }
                    }
                }

                return thinned;
            },
            ct);
    }

    private async Task<int> SlimEligibleSessionItemsAsync(long nowMs, CancellationToken ct)
    {
        var settledCutoffMs = nowMs - SettledRetentionWindowMs;

        var (compactedCount, candidateBlobRefs) = await _writer.EnqueueAsync<(int Compacted, HashSet<string> BlobRefs)>(
            async (conn, tx, innerCt) =>
            {
                var candidateItems = (await conn.QueryAsync<SessionItemRow>(
                    new CommandDefinition(
                        """
                        SELECT i.id AS Id,
                               i.session_id AS SessionId,
                               i.turn_id AS TurnId,
                               i.kind AS Kind,
                               i.payload_json AS PayloadJson,
                               i.created_at_ms AS CreatedAtMs
                        FROM session_items i
                        JOIN session_turns t ON t.id = i.turn_id
                        JOIN agent_sessions s ON s.id = i.session_id
                        LEFT JOIN task_attempts a ON a.attempt_id = s.bound_attempt_id
                        WHERE LOWER(i.kind) IN ('thinking', 'tool_result')
                          AND (
                              t.epoch < (s.epoch - 1)
                              OR (
                                  s.bound_attempt_id IS NOT NULL
                                  AND NOT EXISTS (
                                      SELECT 1 FROM session_turns tr
                                      WHERE tr.session_id = s.id
                                        AND LOWER(tr.status) IN ('running', 'verifying')
                                  )
                                  AND (
                                      a.attempt_id IS NULL
                                      OR (
                                          LOWER(a.status) NOT IN ('running', 'verifying')
                                          AND COALESCE(a.finished_at_ms, a.started_at_ms) <= @SettledCutoffMs
                                      )
                                  )
                                  AND (
                                      SELECT MAX(COALESCE(t2.completed_at_ms, t2.started_at_ms))
                                      FROM session_turns t2
                                      WHERE t2.session_id = s.id
                                  ) <= @SettledCutoffMs
                              )
                          )
                        ORDER BY i.id ASC;
                        """,
                        new { SettledCutoffMs = settledCutoffMs },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false)).ToList();

                var compacted = 0;
                var blobRefs = new HashSet<string>(StringComparer.Ordinal);

                foreach (var item in candidateItems)
                {
                    if (string.Equals(item.Kind, "thinking", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ColdBlobStore.TryInspectEnvelope(item.PayloadJson, out var thinkingBlobRef, out _, out _, out _) &&
                            !string.IsNullOrWhiteSpace(thinkingBlobRef))
                        {
                            blobRefs.Add(thinkingBlobRef);
                        }

                        var deleted = await conn.ExecuteAsync(
                            new CommandDefinition(
                                "DELETE FROM session_items WHERE id = @Id;",
                                new { item.Id },
                                transaction: tx,
                                cancellationToken: innerCt)).ConfigureAwait(false);
                        compacted += deleted;
                        continue;
                    }

                    if (string.Equals(item.Kind, "tool_result", StringComparison.OrdinalIgnoreCase))
                    {
                        var hasEnvelope = ColdBlobStore.TryInspectEnvelope(
                            item.PayloadJson,
                            out var toolBlobRef,
                            out _,
                            out _,
                            out var isAlreadyCompacted);

                        if (isAlreadyCompacted)
                        {
                            continue;
                        }

                        var isColdBlob = hasEnvelope && !string.IsNullOrWhiteSpace(toolBlobRef);
                        var inlineByteCount = Encoding.UTF8.GetByteCount(item.PayloadJson);
                        if (!isColdBlob && inlineByteCount <= ToolResultInlineCompactThresholdBytes)
                        {
                            continue;
                        }

                        var tombstoneJson = ColdBlobStore.BuildCompactedTombstone(item.PayloadJson);
                        var updated = await conn.ExecuteAsync(
                            new CommandDefinition(
                                "UPDATE session_items SET payload_json = @PayloadJson WHERE id = @Id;",
                                new { item.Id, PayloadJson = tombstoneJson },
                                transaction: tx,
                                cancellationToken: innerCt)).ConfigureAwait(false);

                        if (updated > 0)
                        {
                            compacted += updated;
                            if (isColdBlob && toolBlobRef is not null)
                            {
                                blobRefs.Add(toolBlobRef);
                            }
                        }
                    }
                }

                return (compacted, blobRefs);
            },
            ct).ConfigureAwait(false);

        if (candidateBlobRefs.Count > 0)
        {
            await using var readConn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
            foreach (var blobRef in candidateBlobRefs)
            {
                var pattern = $"%\"{blobRef}\"%";
                var remainingRefs = await readConn.ExecuteScalarAsync<long>(
                    new CommandDefinition(
                        """
                        SELECT (
                            (SELECT COUNT(1) FROM session_items WHERE payload_json LIKE @Pattern) +
                            (SELECT COUNT(1) FROM task_attempts WHERE gate_verdict_json LIKE @Pattern)
                        );
                        """,
                        new { Pattern = pattern },
                        cancellationToken: ct)).ConfigureAwait(false);

                if (remainingRefs == 0)
                {
                    _coldBlobStore.DeleteBlobFileIfPresent(blobRef);
                }
            }
        }

        return compactedCount;
    }

    private Task<int> VacuumAndCheckpointAsync(CancellationToken ct) =>
        _writer.EnqueueNonTransactionalAsync<int>(
            async (conn, innerCt) =>
            {
                var freeBefore = await conn.ExecuteScalarAsync<long>(
                    new CommandDefinition("PRAGMA freelist_count;", cancellationToken: innerCt)).ConfigureAwait(false);

                var reclaimed = 0;
                if (freeBefore > _options.FreePageVacuumThreshold)
                {
                    await conn.ExecuteAsync(
                        new CommandDefinition("PRAGMA incremental_vacuum(500);", cancellationToken: innerCt)).ConfigureAwait(false);

                    var freeAfter = await conn.ExecuteScalarAsync<long>(
                        new CommandDefinition("PRAGMA freelist_count;", cancellationToken: innerCt)).ConfigureAwait(false);

                    reclaimed = (int)Math.Max(0L, freeBefore - freeAfter);
                }

                await conn.ExecuteAsync(
                    new CommandDefinition("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken: innerCt)).ConfigureAwait(false);

                return reclaimed;
            },
            ct);

    // -------------------------------------------------------------------------
    // Internal SQL & Mapping Helpers
    // -------------------------------------------------------------------------

    private static async Task<TrackRow?> QueryTrackRowAsync(
        SqliteConnection conn,
        SqliteTransaction? tx,
        string trackId,
        CancellationToken ct)
    {
        return await conn.QuerySingleOrDefaultAsync<TrackRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       area_id AS AreaId,
                       parent_track_id AS ParentTrackId,
                       parent_attempt_id AS ParentAttemptId,
                       title AS Title,
                       cwd AS Cwd,
                       workspace_driver AS WorkspaceDriver,
                       workspace_spec_json AS WorkspaceSpecJson,
                       lead_session_id AS LeadSessionId,
                       template_id AS TemplateId,
                       template_snapshot AS TemplateSnapshot,
                       template_inputs_json AS TemplateInputsJson,
                       template_state_json AS TemplateStateJson,
                       enabled_plugins_json AS EnabledPluginsJson,
                       closed_at_ms AS ClosedAtMs,
                       pinned_at_ms AS PinnedAtMs,
                       sort_order AS SortOrder,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs
                FROM tracks
                WHERE id = @TrackId;
                """,
                new { TrackId = trackId },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    private static async Task UpsertTaskInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        TaskDto task,
        CancellationToken ct)
    {
        // Guard against an updated Id replacing an existing (track_id, task_key) row while preserving normal in-place Id updates.
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM tasks WHERE track_id = @TrackId AND task_key = @TaskKey AND id != @Id;",
                new { task.TrackId, task.TaskKey, task.Id },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);

        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO tasks (
                    id, track_id, block_id, task_key, generation, kind, provider,
                    access_mode, ready, depends_on_json, goal_or_cmd, context_json,
                    acceptance, gate_json, status, status_detail, semantic_verdict,
                    semantic_note, latest_attempt_id, created_at_ms, updated_at_ms
                )
                VALUES (
                    @Id, @TrackId, @BlockId, @TaskKey, @Generation, @Kind, @Provider,
                    @AccessMode, @Ready, @DependsOnJson, @GoalOrCmd, @ContextJson,
                    @Acceptance, @GateJson, @Status, @StatusDetail, @SemanticVerdict,
                    @SemanticNote, @LatestAttemptId, @CreatedAtMs, @UpdatedAtMs
                )
                ON CONFLICT(id) DO UPDATE SET
                    block_id = excluded.block_id,
                    task_key = excluded.task_key,
                    generation = excluded.generation,
                    kind = excluded.kind,
                    provider = excluded.provider,
                    access_mode = excluded.access_mode,
                    ready = excluded.ready,
                    depends_on_json = excluded.depends_on_json,
                    goal_or_cmd = excluded.goal_or_cmd,
                    context_json = excluded.context_json,
                    acceptance = excluded.acceptance,
                    gate_json = excluded.gate_json,
                    status = excluded.status,
                    status_detail = excluded.status_detail,
                    semantic_verdict = excluded.semantic_verdict,
                    semantic_note = excluded.semantic_note,
                    latest_attempt_id = excluded.latest_attempt_id,
                    updated_at_ms = excluded.updated_at_ms;
                """,
                new
                {
                    task.Id,
                    task.TrackId,
                    task.BlockId,
                    task.TaskKey,
                    task.Generation,
                    task.Kind,
                    task.Provider,
                    AccessMode = SqliteJsonUpcaster.FormatAccessMode(task.Access),
                    Ready = task.Ready ? 1 : 0,
                    DependsOnJson = SqliteJsonUpcaster.SerializeStringList(task.DependsOn),
                    task.GoalOrCmd,
                    ContextJson = SqliteJsonUpcaster.SerializeNullableElement(task.Context),
                    task.Acceptance,
                    GateJson = SqliteJsonUpcaster.SerializeGateSpec(task.Gate),
                    Status = SqliteJsonUpcaster.FormatTaskStatus(task.Status),
                    task.StatusDetail,
                    SemanticVerdict = SqliteJsonUpcaster.FormatSemanticVerdict(task.SemanticVerdict),
                    task.SemanticNote,
                    task.LatestAttemptId,
                    task.CreatedAtMs,
                    task.UpdatedAtMs
                },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    private static Task UpsertSessionInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        AgentSessionDto session,
        CancellationToken ct)
    {
        return conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO agent_sessions (
                    id, track_id, bound_task_id, bound_attempt_id, title,
                    provider, model, native_thread_id, epoch, status,
                    context_usage_ratio, archived_at_ms, created_at_ms, updated_at_ms
                )
                VALUES (
                    @Id, @TrackId, @BoundTaskId, @BoundAttemptId, @Title,
                    @Provider, @Model, @NativeThreadId, @Epoch, @Status,
                    @ContextUsageRatio, @ArchivedAtMs, @CreatedAtMs, @UpdatedAtMs
                )
                ON CONFLICT(id) DO UPDATE SET
                    bound_task_id = excluded.bound_task_id,
                    bound_attempt_id = excluded.bound_attempt_id,
                    title = excluded.title,
                    provider = excluded.provider,
                    model = excluded.model,
                    native_thread_id = excluded.native_thread_id,
                    epoch = excluded.epoch,
                    status = excluded.status,
                    context_usage_ratio = excluded.context_usage_ratio,
                    archived_at_ms = excluded.archived_at_ms,
                    updated_at_ms = excluded.updated_at_ms;
                """,
                new
                {
                    session.Id,
                    session.TrackId,
                    session.BoundTaskId,
                    session.BoundAttemptId,
                    session.Title,
                    session.Provider,
                    session.Model,
                    session.NativeThreadId,
                    session.Epoch,
                    session.Status,
                    session.ContextUsageRatio,
                    session.ArchivedAtMs,
                    session.CreatedAtMs,
                    session.UpdatedAtMs
                },
                transaction: tx,
                cancellationToken: ct));
    }

    private static Task<long> InsertEventInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        EventEnvelopeDto evt,
        CancellationToken ct)
    {
        return conn.ExecuteScalarAsync<long>(
            new CommandDefinition(
                """
                INSERT INTO events (area_id, track_id, kind, payload_json, created_at_ms)
                VALUES (@AreaId, @TrackId, @Kind, @PayloadJson, @CreatedAtMs);
                SELECT last_insert_rowid();
                """,
                new
                {
                    evt.AreaId,
                    evt.TrackId,
                    evt.Kind,
                    PayloadJson = SqliteJsonUpcaster.SerializeElement(evt.Payload),
                    evt.CreatedAtMs
                },
                transaction: tx,
                cancellationToken: ct));
    }

    private async Task<SqliteConnection> OpenReadConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_readConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var busyMs = Math.Max(1, _options.BusyTimeoutSeconds) * 1000;
            await conn.ExecuteAsync(
                new CommandDefinition(
                    $"PRAGMA busy_timeout = {busyMs}; PRAGMA query_only = ON; PRAGMA temp_store = MEMORY;",
                    cancellationToken: ct)).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("SqliteTrackStore has not been initialized. Call InitializeAsync first.");
        }
    }

    private static AreaDto MapArea(AreaRow r) =>
        new(
            Id: r.Id,
            Name: r.Name,
            Color: r.Color,
            DefaultCwd: r.DefaultCwd,
            Folders: SqliteJsonUpcaster.DeserializeStringList(r.FoldersJson),
            SortOrder: r.SortOrder,
            CreatedAtMs: r.CreatedAtMs,
            UpdatedAtMs: r.UpdatedAtMs);

    private TrackDto MapTrack(TrackRow r) =>
        new(
            Id: r.Id,
            AreaId: r.AreaId,
            ParentTrackId: r.ParentTrackId,
            ParentAttemptId: r.ParentAttemptId,
            Title: r.Title,
            Cwd: r.Cwd,
            WorkspaceDriver: r.WorkspaceDriver,
            WorkspaceSpec: _upcaster.UpcastObjectOrElement(r.WorkspaceSpecJson, "tracks.workspace_spec_json"),
            LeadSessionId: r.LeadSessionId,
            TemplateId: r.TemplateId,
            TemplateInputs: _upcaster.UpcastObjectOrElement(r.TemplateInputsJson, "tracks.template_inputs_json"),
            TemplateState: _upcaster.UpcastObjectOrElement(r.TemplateStateJson, "tracks.template_state_json"),
            EnabledPlugins: SqliteJsonUpcaster.DeserializeStringList(r.EnabledPluginsJson),
            ClosedAtMs: r.ClosedAtMs,
            PinnedAtMs: r.PinnedAtMs,
            SortOrder: r.SortOrder,
            CreatedAtMs: r.CreatedAtMs,
            UpdatedAtMs: r.UpdatedAtMs);

    private static TaskDto MapTask(TaskRow r) =>
        new(
            Id: r.Id,
            TrackId: r.TrackId,
            BlockId: r.BlockId,
            TaskKey: r.TaskKey,
            Generation: (int)r.Generation,
            Kind: r.Kind,
            Provider: r.Provider,
            Access: SqliteJsonUpcaster.ParseAccessMode(r.AccessMode),
            Ready: r.Ready != 0,
            DependsOn: SqliteJsonUpcaster.DeserializeStringList(r.DependsOnJson),
            GoalOrCmd: r.GoalOrCmd,
            Context: SqliteJsonUpcaster.ParseNullableElement(r.ContextJson),
            Acceptance: r.Acceptance,
            Gate: SqliteJsonUpcaster.DeserializeGateSpec(r.GateJson),
            Status: SqliteJsonUpcaster.ParseTaskStatus(r.Status),
            StatusDetail: r.StatusDetail,
            SemanticVerdict: SqliteJsonUpcaster.ParseSemanticVerdict(r.SemanticVerdict),
            SemanticNote: r.SemanticNote,
            LatestAttemptId: r.LatestAttemptId,
            CreatedAtMs: r.CreatedAtMs,
            UpdatedAtMs: r.UpdatedAtMs);

    private TaskAttemptDto MapTaskAttempt(TaskAttemptRow r) =>
        new(
            AttemptId: r.AttemptId,
            TaskId: r.TaskId,
            TrackId: r.TrackId,
            SessionId: r.SessionId,
            Status: r.Status,
            WorkerResult: SqliteJsonUpcaster.ParseNullableElement(r.WorkerResultJson),
            Meta: _upcaster.UpcastObjectOrElement(r.MetaJson, "task_attempts.meta_json"),
            WorkspaceLease: SqliteJsonUpcaster.ParseNullableElement(r.WorkspaceLeaseJson),
            GateVerdict: _coldBlobStore.HydrateNullable(r.GateVerdictJson, "task_attempts.gate_verdict_json"),
            StartedAtMs: r.StartedAtMs,
            FinishedAtMs: r.FinishedAtMs);

    private static AgentSessionDto MapSession(SessionRow r, string? leadSessionId) =>
        new(
            Id: r.Id,
            TrackId: r.TrackId,
            IsLead: !string.IsNullOrWhiteSpace(leadSessionId) &&
                    string.Equals(r.Id, leadSessionId, StringComparison.Ordinal),
            BoundTaskId: r.BoundTaskId,
            BoundAttemptId: r.BoundAttemptId,
            Title: r.Title,
            Provider: r.Provider,
            Model: r.Model,
            NativeThreadId: r.NativeThreadId,
            Epoch: (int)r.Epoch,
            Status: r.Status,
            ContextUsageRatio: r.ContextUsageRatio,
            ArchivedAtMs: r.ArchivedAtMs,
            CreatedAtMs: r.CreatedAtMs,
            UpdatedAtMs: r.UpdatedAtMs);

    private static SessionTurnDto MapTurn(SessionTurnRow r) =>
        new(
            Id: r.Id,
            SessionId: r.SessionId,
            TurnIndex: (int)r.TurnIndex,
            Epoch: (int)r.Epoch,
            TriggerKind: r.TriggerKind,
            Input: SqliteJsonUpcaster.ParseElementOrDefault(r.InputJson),
            Status: r.Status,
            Usage: SqliteJsonUpcaster.ParseNullableElement(r.UsageJson),
            ErrorText: r.ErrorText,
            StartedAtMs: r.StartedAtMs,
            CompletedAtMs: r.CompletedAtMs);

    private SessionItemDto MapSessionItem(SessionItemRow r) =>
        new(
            Id: r.Id,
            SessionId: r.SessionId,
            TurnId: r.TurnId,
            Kind: r.Kind,
            Payload: _coldBlobStore.Hydrate(r.PayloadJson, "session_items.payload_json"),
            CreatedAtMs: r.CreatedAtMs);

    private static AskDto MapAsk(AskRow r) =>
        new(
            Id: r.Id,
            TrackId: r.TrackId,
            Status: SqliteJsonUpcaster.ParseAskStatus(r.Status),
            Questions: SqliteJsonUpcaster.DeserializeAskQuestions(r.QuestionsJson),
            Answer: SqliteJsonUpcaster.ParseNullableElement(r.AnswerJson),
            CreatedAtMs: r.CreatedAtMs,
            AnsweredAtMs: r.AnsweredAtMs);

    private static MailDto MapMail(MailRow r) =>
        new(
            Id: r.Id,
            FromTrackId: r.FromTrackId,
            ToTrackId: r.ToTrackId,
            Summary: r.Summary,
            Body: r.Body,
            ReadAtMs: r.ReadAtMs,
            CreatedAtMs: r.CreatedAtMs);

    private static TerminalDto MapTerminal(TerminalRow r) =>
        new(
            Id: r.Id,
            TrackId: r.TrackId,
            AttemptId: r.AttemptId,
            IdempotencyKey: r.IdempotencyKey,
            Title: r.Title,
            Program: r.Program,
            Cwd: r.Cwd,
            Status: SqliteJsonUpcaster.ParseTerminalStatus(r.Status),
            ExitCode: r.ExitCode.HasValue ? (int)r.ExitCode.Value : null,
            CreatedAtMs: r.CreatedAtMs,
            ExitedAtMs: r.ExitedAtMs);

    private EventEnvelopeDto MapEvent(EventRow r) =>
        new(
            EventId: r.Id,
            AreaId: r.AreaId,
            TrackId: r.TrackId,
            Kind: r.Kind,
            Payload: _upcaster.UpcastObjectOrElement(r.PayloadJson, "events.payload_json"),
            CreatedAtMs: r.CreatedAtMs);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _writer.DisposeAsync().ConfigureAwait(false);

        using (var poolClearConn = new SqliteConnection(_readConnectionString))
        {
            SqliteConnection.ClearPool(poolClearConn);
        }

        _initLock.Dispose();
    }

    // -------------------------------------------------------------------------
    // Internal Dapper Row Types (using Int64 for SQLite INTEGER columns)
    // -------------------------------------------------------------------------

    private sealed class AreaRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Color { get; init; } = string.Empty;
        public string? DefaultCwd { get; init; }
        public string FoldersJson { get; init; } = "[]";
        public double SortOrder { get; init; }
        public long CreatedAtMs { get; init; }
        public long UpdatedAtMs { get; init; }
    }

    private sealed class TrackRow
    {
        public string Id { get; init; } = string.Empty;
        public string AreaId { get; init; } = string.Empty;
        public string? ParentTrackId { get; init; }
        public string? ParentAttemptId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Cwd { get; init; } = string.Empty;
        public string WorkspaceDriver { get; init; } = "direct";
        public string WorkspaceSpecJson { get; init; } = "{}";
        public string? LeadSessionId { get; init; }
        public string? TemplateId { get; init; }
        public string? TemplateSnapshot { get; init; }
        public string TemplateInputsJson { get; init; } = "{}";
        public string TemplateStateJson { get; init; } = "{}";
        public string EnabledPluginsJson { get; init; } = "[]";
        public long? ClosedAtMs { get; init; }
        public long? PinnedAtMs { get; init; }
        public double SortOrder { get; init; }
        public long CreatedAtMs { get; init; }
        public long UpdatedAtMs { get; init; }
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

    private sealed class TaskRow
    {
        public string Id { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string BlockId { get; init; } = string.Empty;
        public string TaskKey { get; init; } = string.Empty;
        public long Generation { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string? Provider { get; init; }
        public string AccessMode { get; init; } = "read_write";
        public long Ready { get; init; }
        public string DependsOnJson { get; init; } = "[]";
        public string GoalOrCmd { get; init; } = string.Empty;
        public string? ContextJson { get; init; }
        public string? Acceptance { get; init; }
        public string? GateJson { get; init; }
        public string Status { get; init; } = "pending";
        public string? StatusDetail { get; init; }
        public string SemanticVerdict { get; init; } = "none";
        public string? SemanticNote { get; init; }
        public string? LatestAttemptId { get; init; }
        public long CreatedAtMs { get; init; }
        public long UpdatedAtMs { get; init; }
    }

    private sealed class TaskAttemptRow
    {
        public string AttemptId { get; init; } = string.Empty;
        public string TaskId { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string? SessionId { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? WorkerResultJson { get; init; }
        public string MetaJson { get; init; } = "{}";
        public string? WorkspaceLeaseJson { get; init; }
        public string? GateVerdictJson { get; init; }
        public long StartedAtMs { get; init; }
        public long? FinishedAtMs { get; init; }
    }

    private sealed class SessionRow
    {
        public string Id { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string? BoundTaskId { get; init; }
        public string? BoundAttemptId { get; init; }
        public string? Title { get; init; }
        public string Provider { get; init; } = string.Empty;
        public string? Model { get; init; }
        public string? NativeThreadId { get; init; }
        public long Epoch { get; init; }
        public string Status { get; init; } = "idle";
        public double ContextUsageRatio { get; init; }
        public long? ArchivedAtMs { get; init; }
        public long CreatedAtMs { get; init; }
        public long UpdatedAtMs { get; init; }
    }

    private sealed class SessionTurnRow
    {
        public string Id { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public long TurnIndex { get; init; }
        public long Epoch { get; init; }
        public string? IdempotencyKey { get; init; }
        public string TriggerKind { get; init; } = string.Empty;
        public string InputJson { get; init; } = "{}";
        public string Status { get; init; } = string.Empty;
        public string? UsageJson { get; init; }
        public string? ErrorText { get; init; }
        public long StartedAtMs { get; init; }
        public long? CompletedAtMs { get; init; }
    }

    private sealed class SessionItemRow
    {
        public long Id { get; init; }
        public string SessionId { get; init; } = string.Empty;
        public string TurnId { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = "{}";
        public long CreatedAtMs { get; init; }
    }

    private sealed class AskRow
    {
        public string Id { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string Status { get; init; } = "open";
        public string QuestionsJson { get; init; } = "[]";
        public string? AnswerJson { get; init; }
        public long CreatedAtMs { get; init; }
        public long? AnsweredAtMs { get; init; }
    }

    private sealed class MailRow
    {
        public string Id { get; init; } = string.Empty;
        public string FromTrackId { get; init; } = string.Empty;
        public string ToTrackId { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public long? ReadAtMs { get; init; }
        public long CreatedAtMs { get; init; }
    }

    private sealed class TerminalRow
    {
        public string Id { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string? AttemptId { get; init; }
        public string? IdempotencyKey { get; init; }
        public string Title { get; init; } = "Terminal";
        public string? Program { get; init; }
        public string Cwd { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public long? ExitCode { get; init; }
        public long CreatedAtMs { get; init; }
        public long? ExitedAtMs { get; init; }
    }

    private sealed class EventRow
    {
        public long Id { get; init; }
        public string? AreaId { get; init; }
        public string? TrackId { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string PayloadJson { get; init; } = "{}";
        public long CreatedAtMs { get; init; }
    }

    private sealed class InterruptedLeaseRow
    {
        public string AttemptId { get; init; } = string.Empty;
        public string TaskId { get; init; } = string.Empty;
        public string TrackId { get; init; } = string.Empty;
        public string WorkspaceLeaseJson { get; init; } = "{}";
    }

    private sealed class RevisionBoundsRow
    {
        public string TrackId { get; init; } = string.Empty;
        public long MinRev { get; init; }
        public long MaxRev { get; init; }
        public long? ClosedAtMs { get; init; }
    }
}
