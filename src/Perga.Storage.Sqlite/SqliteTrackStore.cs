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
public sealed partial class SqliteTrackStore : ITrackStore, IAsyncDisposable
{
    private readonly SqliteStorageOptions _options;
    private readonly SqliteMigrator _migrator;
    private readonly SqliteSingleWriter _writer;
    private readonly ColdBlobStore _coldBlobStore;
    private readonly SqliteJsonUpcaster _upcaster;
    private readonly TimeProvider _timeProvider;
    private readonly string _readConnectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private string _databaseId = string.Empty;
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
    public string DatabaseId
    {
        get
        {
            EnsureInitialized();
            return _databaseId;
        }
    }

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
            var fullDbPath = _options.GetFullDbPath();
            var dbExistedBeforeInit = File.Exists(fullDbPath);
            await _migrator.MigrateAsync(fullDbPath, ct).ConfigureAwait(false);
            await _writer.InitializeAsync(ct).ConfigureAwait(false);
            _databaseId = ResolveOrCreateDatabaseId(fullDbPath, dbExistedBeforeInit);
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

        return _writer.EnqueueAsync<AreaDto>(
            async (conn, tx, innerCt) =>
            {
                await UpsertAreaInternalAsync(conn, tx, area, innerCt).ConfigureAwait(false);
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

    private static async Task UpsertAreaInternalAsync(SqliteConnection conn, SqliteTransaction tx, AreaDto area, CancellationToken ct)
    {
        var foldersJson = SqliteJsonUpcaster.SerializeStringList(area.Folders);
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
                cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task DeleteAreaAsync(string areaId, CancellationToken ct, bool requireEmpty = false)
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

                if (requireEmpty && trackIds.Count != 0)
                    throw Perga.Kernel.KernelException.Conflict("This Area has Tracks that must be removed before deletion.");

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

    public Task<string> CreateTrackAsync(TrackDto track, ReportDocument report, string? templateSnapshot,
        AgentSessionDto lead, CancellationToken ct) =>
        CreateTrackAsync(new(track, report, templateSnapshot, lead, [], null, null, []), null, ct);

    public Task<string> CreateTrackAsync(TrackCreation creation, ResourceCreationKey? key, CancellationToken ct)
    {
        var (track, initialReport, templateSnapshot, initialLeadSession, tasks, firstInput, preparation, events) = creation;
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

        return _writer.EnqueueAsync<string>(
            async (conn, tx, innerCt) =>
            {
                var replay = await FindCreationInternalAsync(conn, tx, key, innerCt).ConfigureAwait(false);
                if (replay is not null)
                    return replay;
                if (await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT id FROM areas WHERE id = @AreaId;", new { track.AreaId }, tx, cancellationToken: innerCt)).ConfigureAwait(false) is null)
                    throw Perga.Kernel.KernelException.NotFound($"Area '{track.AreaId}' not found.");

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
                foreach (var task in tasks)
                    await UpsertTaskInternalAsync(conn, tx, task, innerCt).ConfigureAwait(false);
                var input = firstInput is null ? null
                    : await InsertInputAsync(conn, tx, initialLeadSession, firstInput, innerCt).ConfigureAwait(false);
                if (preparation is not null)
                    await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO track_workspace_preparations VALUES (@Id, @Intent);",
                    new { track.Id, Intent = JsonSerializer.Serialize(preparation) }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                await InsertResourceReceiptAsync(conn, tx, key, track.Id, track.CreatedAtMs, input?.Receipt.InputId, innerCt).ConfigureAwait(false);
                foreach (var evt in events)
                    await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                return track.Id;
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
                       reasoning_effort AS ReasoningEffort,
                       native_thread_id AS NativeThreadId,
                       epoch AS Epoch,
                       status AS Status,
                       context_usage_ratio AS ContextUsageRatio,
                       archived_at_ms AS ArchivedAtMs,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs,
                       permission_mode AS PermissionMode
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
                       answered_at_ms AS AnsweredAtMs,
                       delivery AS Delivery, session_id AS SessionId, turn_id AS TurnId
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

    public Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct) =>
        UpdateTrackCoreAsync(track, null, evt, ct);

    public Task UpdateTrackTemplateAndEmitAsync(TrackDto track, string templateSnapshot, EventEnvelopeDto evt, CancellationToken ct) =>
        UpdateTrackCoreAsync(track, templateSnapshot, evt, ct);

    private Task UpdateTrackCoreAsync(TrackDto track, string? templateSnapshot, EventEnvelopeDto? evt, CancellationToken ct)
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
                            template_snapshot = CASE WHEN @TemplateSnapshot IS NULL THEN template_snapshot ELSE @TemplateSnapshot END,
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
                            TemplateSnapshot = templateSnapshot,
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

        try
        {
            _coldBlobStore.DeleteTrackBlobsDirectory(trackId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Could not clean up blobs for deleted Track {0}: {1}", trackId, ex.Message);
        }
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
        CancellationToken ct,
        WorkerDispatchIntent? dispatch = null)
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
                if (dispatch is not null)
                {
                    var dispatched = syncedTasks.Single(task => task.TaskKey == dispatch.TaskKey);
                    await conn.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO creation_receipts (author_id, operation, scope_id, request_key, request_hash, resource_id, created_at_ms)
                        VALUES (@AuthorId, 'worker-dispatch', @TrackId, @RequestKey, @RequestHash, @TaskId, @Now);
                        """, new { dispatch.AuthorId, dispatch.TrackId, dispatch.RequestKey, dispatch.RequestHash, TaskId = dispatched.Id, Now = nowMs },
                        tx, cancellationToken: innerCt)).ConfigureAwait(false);
                }
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
        CancellationToken ct,
        AgentSessionDto? session = null,
        EventEnvelopeDto? sessionEvent = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        EnsureInitialized();

        var nextTemplateStateJson = nextTemplateState.HasValue
            ? SqliteJsonUpcaster.SerializeElement(nextTemplateState.Value)
            : null;

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await UpsertTaskInternalAsync(conn, tx, task, innerCt).ConfigureAwait(false);
                if (session is not null)
                    await UpsertSessionInternalAsync(conn, tx, session, innerCt).ConfigureAwait(false);
                if (sessionEvent is not null)
                    await InsertEventInternalAsync(conn, tx, sessionEvent, innerCt).ConfigureAwait(false);

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
                                GateVerdictJson = _coldBlobStore.StoreOrInlineNullable(attempt.TrackId, attempt.GateVerdict),
                                attempt.StartedAtMs,
                                attempt.FinishedAtMs
                            },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);

                    if (attempt.FinishedAtMs is not null)
                        await conn.ExecuteAsync(new CommandDefinition("""
                            UPDATE session_messages SET delivery = 'interrupted'
                            WHERE attempt_id = @AttemptId AND delivery = 'queued';
                            """, new { attempt.AttemptId }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
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

    public async Task RecordTurnAndItemsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct)
    {
        _ = await RecordTurnAndItemsWithIdsAsync(turn, newItems, updatedSession, ct).ConfigureAwait(false);
    }

    public Task<(SessionTurnDto Turn, IReadOnlyList<long> ItemIds)> RecordTurnAndItemsWithIdsAsync(
        SessionTurnDto turn,
        IReadOnlyList<SessionItemDto> newItems,
        AgentSessionDto? updatedSession,
        CancellationToken ct = default, SessionMessage? dispatchMessage = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(newItems);
        EnsureInitialized();

        var inputJson = SqliteJsonUpcaster.SerializeElement(turn.Input);
        var usageJson = SqliteJsonUpcaster.SerializeNullableElement(turn.Usage);

        return _writer.EnqueueAsync<(SessionTurnDto, IReadOnlyList<long>)>(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO session_turns (
                            id, session_id, turn_index, epoch,
                            trigger_kind, input_json, status, usage_json, error_text,
                            started_at_ms, completed_at_ms
                        )
                        VALUES (
                            @Id, @SessionId, @TurnIndex, @Epoch,
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

                long? messageOrdinal = null;
                if (dispatchMessage is not null)
                {
                    if (!await TransitionMessageAsync(conn, tx, dispatchMessage with { TurnId = turn.Id, Delivery = "dispatching" },
                        "dispatching", innerCt).ConfigureAwait(false))
                        throw new InvalidOperationException("The claimed message no longer belongs to this dispatch.");
                    messageOrdinal = await ReadMessageOrdinalAsync(conn, tx, dispatchMessage, innerCt).ConfigureAwait(false);
                }

                if (updatedSession is not null)
                {
                    await UpsertSessionInternalAsync(conn, tx, updatedSession, innerCt).ConfigureAwait(false);
                }

                IReadOnlyList<long> insertedIds = [];
                long? userItemId = turn.UserItemId;
                if (newItems.Count > 0)
                {
                    var trackId = updatedSession?.TrackId ?? await conn.ExecuteScalarAsync<string?>(
                        new CommandDefinition(
                            "SELECT track_id FROM agent_sessions WHERE id = @SessionId;",
                            new { turn.SessionId },
                            transaction: tx,
                            cancellationToken: innerCt)).ConfigureAwait(false);

                    insertedIds = await InsertSessionItemsAsync(conn, tx, trackId, newItems, innerCt, messageOrdinal).ConfigureAwait(false);
                    for (var i = 0; i < newItems.Count; i++)
                    {
                        if (userItemId is null && string.Equals(newItems[i].Kind, "user", StringComparison.Ordinal))
                        {
                            userItemId = insertedIds[i];
                            break;
                        }
                    }
                }

                userItemId ??= await conn.ExecuteScalarAsync<long?>(
                    new CommandDefinition(
                        "SELECT id FROM session_items WHERE turn_id = @TurnId AND kind = 'user' ORDER BY id ASC LIMIT 1;",
                        new { TurnId = turn.Id },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                var resolvedTurn = userItemId == turn.UserItemId ? turn : turn with { UserItemId = userItemId };
                return (resolvedTurn, insertedIds);
            },
            ct);
    }

    public Task<IReadOnlyList<long>> AppendSessionItemsAsync(
        string sessionId,
        IReadOnlyList<SessionItemDto> newItems,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(newItems);
        EnsureInitialized();

        if (newItems.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<long>>([]);
        }

        return _writer.EnqueueAsync<IReadOnlyList<long>>(
            async (conn, tx, innerCt) =>
            {
                var trackId = await conn.ExecuteScalarAsync<string?>(
                    new CommandDefinition(
                        "SELECT track_id FROM agent_sessions WHERE id = @SessionId;",
                        new { SessionId = sessionId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                return await InsertSessionItemsAsync(conn, tx, trackId, newItems, innerCt).ConfigureAwait(false);
            },
            ct);
    }

    private async Task<IReadOnlyList<long>> InsertSessionItemsAsync(
        SqliteConnection conn, SqliteTransaction tx, string? trackId, IReadOnlyList<SessionItemDto> items, CancellationToken ct, long? messageOrdinal = null)
    {
        var ids = new List<long>(items.Count);
        foreach (var item in items)
        {
            var payloadJson = !string.IsNullOrWhiteSpace(trackId)
                ? _coldBlobStore.StoreOrInline(trackId, item.Payload)
                : SqliteJsonUpcaster.SerializeElement(item.Payload);

            var insertedId = await conn.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    """
                    INSERT INTO session_items (session_id, turn_id, kind, payload_json, created_at_ms, message_ordinal)
                    VALUES (@SessionId, @TurnId, @Kind, @PayloadJson, @CreatedAtMs, @MessageOrdinal);
                    SELECT last_insert_rowid();
                    """,
                    new
                    {
                        item.SessionId,
                        item.TurnId,
                        item.Kind,
                        PayloadJson = payloadJson,
                        item.CreatedAtMs,
                        MessageOrdinal = item.Kind == "user" ? messageOrdinal : null
                    },
                    transaction: tx,
                    cancellationToken: ct)).ConfigureAwait(false);
            ids.Add(insertedId);
        }

        return ids;
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
        CancellationToken ct = default, bool latest = false, long? beforeId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        EnsureInitialized();

        var reverse = latest || beforeId is not null;
        var clampedLimit = Math.Max(1, limit);
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SessionItemRow>(
            new CommandDefinition(
                $"""
                SELECT item.id AS Id,
                       item.session_id AS SessionId,
                       item.turn_id AS TurnId,
                       item.kind AS Kind,
                       CASE WHEN input.id IS NULL OR input.source = 'worker' OR input.turn_id IS NULL THEN item.payload_json
                            ELSE json_object('v', 1, 'content', input.content, 'triggerKind', 'user', 'inputId', input.id)
                       END AS PayloadJson,
                       item.created_at_ms AS CreatedAtMs,
                       turn.status AS TurnStatus,
                       CASE WHEN input.delivery = 'queued' THEN 'accepted' ELSE input.delivery END AS InputDelivery
                FROM session_items item
                LEFT JOIN session_messages input ON input.ordinal = item.message_ordinal
                LEFT JOIN session_turns turn ON turn.session_id = item.session_id AND turn.id = item.turn_id
                WHERE item.session_id = @SessionId
                  AND item.id > @AfterId AND (@BeforeId IS NULL OR item.id < @BeforeId)
                ORDER BY item.id {(reverse ? "DESC" : "ASC")}
                LIMIT @Limit;
                """,
                new
                {
                    SessionId = sessionId,
                    AfterId = afterId,
                    BeforeId = beforeId,
                    Limit = clampedLimit
                },
                cancellationToken: ct)).ConfigureAwait(false);

        return (reverse ? rows.Reverse() : rows).Select(MapSessionItem).ToList();
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
                        INSERT INTO asks (id, track_id, status, questions_json, answer_json, created_at_ms, answered_at_ms, delivery, session_id, turn_id)
                        VALUES (@Id, @TrackId, @Status, @QuestionsJson, @AnswerJson, @CreatedAtMs, @AnsweredAtMs, @Delivery, @SessionId, @TurnId)
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
                            ask.AnsweredAtMs,
                            Delivery = ask.Delivery.ToString().ToLowerInvariant(),
                            ask.SessionId,
                            ask.TurnId
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
                       answered_at_ms AS AnsweredAtMs,
                       delivery AS Delivery, session_id AS SessionId, turn_id AS TurnId
                FROM asks
                WHERE id = @AskId;
                """,
                new { AskId = askId },
                cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : MapAsk(row);
    }

    public Task UpsertMailAsync(MailDto mail, EventEnvelopeDto evt, CancellationToken ct, bool requestWake = true)
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
                        INSERT INTO mails (id, from_track_id, to_track_id, summary, body, read_at_ms, created_at_ms, reply_to_mail_id, wake_pending)
                        VALUES (@Id, @FromTrackId, @ToTrackId, @Summary, @Body, @ReadAtMs, @CreatedAtMs, @ReplyToMailId, @RequestWake)
                        ON CONFLICT(id) DO UPDATE SET
                            summary = excluded.summary,
                            body = excluded.body,
                            read_at_ms = excluded.read_at_ms;
                        """,
                        new
                        {
                            mail.Id,
                            RequestWake = requestWake,
                            mail.ReplyToMailId,
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

    public Task AcknowledgeMailWakeAsync(string mailId, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE mails SET wake_pending = 0 WHERE id = @mailId;
                UPDATE system_upgrade_receipts SET notification_status = CASE
                    WHEN EXISTS (SELECT 1 FROM mails m JOIN tracks t ON t.id = m.to_track_id
                        WHERE m.id = @mailId AND t.closed_at_ms IS NULL) THEN 'notified' ELSE 'target_unavailable' END
                    WHERE 'system-upgrade:' || job_id = @mailId;
                """,
                new { mailId }, tx, cancellationToken: token)).ConfigureAwait(false);
        }, ct);

    private const string MailSelect = """
        SELECT id AS Id, from_track_id AS FromTrackId, to_track_id AS ToTrackId,
               summary AS Summary, body AS Body, read_at_ms AS ReadAtMs,
               created_at_ms AS CreatedAtMs, reply_to_mail_id AS ReplyToMailId
        FROM mails
        """;

    public async Task<MailDto?> FindReceivedMailAsync(string trackId, string mailId, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<MailRow>(new CommandDefinition(
            MailSelect + " WHERE id = @mailId AND to_track_id = @trackId;",
            new { trackId, mailId }, cancellationToken: ct)).ConfigureAwait(false);
        return row is null ? null : MapMail(row);
    }

    public Task<IReadOnlyList<MailDto>> ListPendingMailWakesAsync(CancellationToken ct) =>
        QueryMailsAsync(" WHERE wake_pending = 1 ORDER BY created_at_ms, id;", null, ct);

    public Task<IReadOnlyList<MailDto>> ListMailsAsync(string trackId, bool unreadOnly = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        var filter = unreadOnly ? " AND read_at_ms IS NULL" : "";
        return QueryMailsAsync(" WHERE to_track_id = @trackId" + filter + " ORDER BY created_at_ms DESC, id;", new { trackId }, ct);
    }

    private async Task<IReadOnlyList<MailDto>> QueryMailsAsync(string clause, object? parameters, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<MailRow>(new CommandDefinition(MailSelect + clause,
            parameters, cancellationToken: ct)).ConfigureAwait(false);
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

    public Task DeleteTerminalAsync(string trackId, string terminalId, EventEnvelopeDto? evt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        EnsureInitialized();

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM terminals WHERE id = @TerminalId AND track_id = @TrackId;",
                        new { TerminalId = terminalId, TrackId = trackId },
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

    public async Task<long> GetEventTipIdAsync(CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COALESCE(MAX(id), 0) FROM events", cancellationToken: ct)).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<TrackOverlayDto>> ListTrackOverlaysAsync(
        bool includeClosed = false,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<TrackOverlayRow>(
            new CommandDefinition(
                """
                SELECT tr.id AS TrackId,
                       tr.area_id AS AreaId,
                       CASE WHEN EXISTS (
                           SELECT 1 FROM agent_sessions s
                           WHERE s.track_id = tr.id AND LOWER(s.status) IN ('queued', 'running')
                       ) OR EXISTS (
                           SELECT 1 FROM tasks t
                           WHERE t.track_id = tr.id AND LOWER(t.status) IN ('running', 'verifying')
                       ) THEN 1 ELSE 0 END AS IsWorking,
                       (SELECT COUNT(1) FROM asks a WHERE a.track_id = tr.id AND LOWER(a.status) = 'open') AS OpenAskCount,
                       CASE WHEN EXISTS (
                           SELECT 1 FROM tasks tf
                           WHERE tf.track_id = tr.id AND LOWER(tf.status) = 'failed'
                       ) THEN 1 ELSE 0 END AS HasFailedTask,
                       (SELECT COUNT(1) FROM mails m WHERE m.to_track_id = tr.id AND m.read_at_ms IS NULL) AS UnreadMailCount,
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
                """,
                new { IncludeClosed = includeClosed ? 1 : 0 },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows
            .Select(static r => new TrackOverlayDto(
                TrackId: r.TrackId,
                AreaId: r.AreaId,
                IsWorking: r.IsWorking != 0,
                OpenAskCount: (int)r.OpenAskCount,
                HasFailedTask: r.HasFailedTask != 0,
                UnreadMailCount: (int)r.UnreadMailCount,
                UpdatedAtMs: r.UpdatedAtMs))
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Boot Reconciliation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns in-flight attempts with a lease to settle before boot marks them interrupted.
    /// </summary>
    public async Task<IReadOnlyList<InterruptedAttemptLeaseDto>> ListInFlightAttemptLeasesAsync(
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

        return liveRows.Select(static r => new InterruptedAttemptLeaseDto(
            r.AttemptId, r.TaskId, r.TrackId,
            SqliteJsonUpcaster.ParseElementOrDefault(r.WorkspaceLeaseJson))).ToArray();
    }

    public Task<IReadOnlyList<string>> MarkInFlightInterruptedOnBootAsync(CancellationToken ct)
    {
        EnsureInitialized();
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        return _writer.EnqueueAsync<IReadOnlyList<string>>(
            async (conn, tx, innerCt) =>
            {
                var affectedTrackIds = (await conn.QueryAsync<string>(
                    new CommandDefinition(
                        """
                        SELECT DISTINCT tr.id
                        FROM tracks tr
                        WHERE (
                              EXISTS (
                                  SELECT 1 FROM tasks task
                                  WHERE task.track_id = tr.id
                                    AND task.status IN ('running', 'verifying')
                              )
                              OR EXISTS (
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
                        UPDATE tasks AS t
                        SET status = CASE WHEN LOWER(a.status) = 'running' AND a.workspace_lease_json IS NULL AND a.session_id IS NULL THEN 'pending' ELSE 'failed' END,
                            status_detail = CASE WHEN LOWER(a.status) = 'running' AND a.workspace_lease_json IS NULL AND a.session_id IS NULL THEN NULL ELSE 'The host stopped during this attempt.' END,
                            updated_at_ms = @NowMs
                        FROM task_attempts a
                        WHERE a.attempt_id = t.latest_attempt_id AND a.task_id = t.id
                          AND t.status IN ('running', 'verifying') AND LOWER(a.status) IN ('running', 'verifying', 'interrupted');
                        UPDATE agent_sessions AS s
                        SET archived_at_ms = COALESCE(archived_at_ms, @NowMs), updated_at_ms = @NowMs
                        WHERE EXISTS (
                            SELECT 1 FROM task_attempts a WHERE a.attempt_id = s.bound_attempt_id AND a.session_id = s.id
                                AND LOWER(a.status) IN ('running', 'verifying')
                        );
                        UPDATE task_attempts
                        SET status = 'interrupted',
                            finished_at_ms = COALESCE(finished_at_ms, @NowMs)
                        WHERE LOWER(status) IN ('running', 'verifying');
                        UPDATE session_messages SET delivery = 'interrupted'
                        WHERE source = 'worker' AND delivery = 'queued';
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

                return affectedTrackIds;
            },
            ct);
    }

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
                    provider, model, reasoning_effort, native_thread_id, epoch, status,
                    context_usage_ratio, archived_at_ms, created_at_ms, updated_at_ms, permission_mode
                )
                VALUES (
                    @Id, @TrackId, @BoundTaskId, @BoundAttemptId, @Title,
                    @Provider, @Model, @ReasoningEffort, @NativeThreadId, @Epoch, @Status,
                    @ContextUsageRatio, @ArchivedAtMs, @CreatedAtMs, @UpdatedAtMs, @PermissionMode
                )
                ON CONFLICT(id) DO UPDATE SET
                    bound_task_id = excluded.bound_task_id,
                    bound_attempt_id = excluded.bound_attempt_id,
                    title = excluded.title,
                    provider = excluded.provider,
                    model = excluded.model,
                    reasoning_effort = excluded.reasoning_effort,
                    native_thread_id = excluded.native_thread_id,
                    epoch = excluded.epoch,
                    status = excluded.status,
                    context_usage_ratio = excluded.context_usage_ratio,
                    archived_at_ms = excluded.archived_at_ms,
                    updated_at_ms = excluded.updated_at_ms,
                    permission_mode = excluded.permission_mode;
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
                    session.ReasoningEffort,
                    session.NativeThreadId,
                    session.Epoch,
                    session.Status,
                    session.ContextUsageRatio,
                    session.ArchivedAtMs,
                    session.CreatedAtMs,
                    session.UpdatedAtMs,
                    session.PermissionMode
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
            UpdatedAtMs: r.UpdatedAtMs,
            ReasoningEffort: r.ReasoningEffort,
            PermissionMode: r.PermissionMode);

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
            CreatedAtMs: r.CreatedAtMs) { TurnStatus = r.TurnStatus, InputDelivery = r.InputDelivery };

    private static AskDto MapAsk(AskRow r) =>
        new(
            Id: r.Id,
            TrackId: r.TrackId,
            Status: SqliteJsonUpcaster.ParseAskStatus(r.Status),
            Questions: SqliteJsonUpcaster.DeserializeAskQuestions(r.QuestionsJson),
            Answer: SqliteJsonUpcaster.ParseNullableElement(r.AnswerJson),
            CreatedAtMs: r.CreatedAtMs,
            AnsweredAtMs: r.AnsweredAtMs,
            Delivery: r.Delivery switch { "hold" => AskDelivery.Hold, "wake" => AskDelivery.Wake, _ => throw new InvalidOperationException("Invalid ask delivery.") },
            SessionId: r.SessionId,
            TurnId: r.TurnId);

    private static MailDto MapMail(MailRow r) =>
        new(
            Id: r.Id,
            FromTrackId: r.FromTrackId,
            ToTrackId: r.ToTrackId,
            Summary: r.Summary,
            Body: r.Body,
            ReadAtMs: r.ReadAtMs,
            CreatedAtMs: r.CreatedAtMs,
            ReplyToMailId: r.ReplyToMailId);

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
        public string? ReasoningEffort { get; init; }
        public string? NativeThreadId { get; init; }
        public long Epoch { get; init; }
        public string Status { get; init; } = "idle";
        public double ContextUsageRatio { get; init; }
        public long? ArchivedAtMs { get; init; }
        public long CreatedAtMs { get; init; }
        public long UpdatedAtMs { get; init; }
        public string? PermissionMode { get; init; }
    }

    private sealed class SessionTurnRow
    {
        public string Id { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public long TurnIndex { get; init; }
        public long Epoch { get; init; }
        public string TriggerKind { get; init; } = string.Empty;
        public string InputJson { get; init; } = "{}";
        public string Status { get; init; } = string.Empty;
        public string? UsageJson { get; init; }
        public string? ErrorText { get; init; }
        public long? StartedAtMs { get; init; }
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
        public string? TurnStatus { get; init; }
        public string? InputDelivery { get; init; }
    }

    private sealed class AskRow
    {
        public string Delivery { get; init; } = string.Empty;
        public string? SessionId { get; init; }
        public string? TurnId { get; init; }
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
        public string? ReplyToMailId { get; init; }
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

    private static string ResolveOrCreateDatabaseId(string fullDbPath, bool dbExistedBeforeInit)
    {
        var idFilePath = fullDbPath + ".id";
        if (dbExistedBeforeInit && File.Exists(idFilePath))
        {
            try
            {
                var existing = File.ReadAllText(idFilePath, Encoding.UTF8).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    return existing;
                }
            }
            catch (IOException)
            {
                // Fall through to mint a new database ID.
            }
        }

        var created = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(idFilePath, created, Encoding.UTF8);
        }
        catch (IOException)
        {
            // Best-effort persistence on read-only filesystems.
        }

        return created;
    }

    private sealed class TrackOverlayRow
    {
        public string TrackId { get; init; } = string.Empty;
        public string AreaId { get; init; } = string.Empty;
        public long IsWorking { get; init; }
        public long OpenAskCount { get; init; }
        public long HasFailedTask { get; init; }
        public long UnreadMailCount { get; init; }
        public long UpdatedAtMs { get; init; }
    }

}
