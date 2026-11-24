using System.Text.Json;
using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Storage.Sqlite;

/// <summary>Atomic Track creation across report, session, input, receipt and event records.</summary>
internal sealed class TrackCreationRepository
{
    private readonly SqliteStoreContext _context;
    private readonly TaskRepository _taskRepository;

    internal TrackCreationRepository(SqliteStoreContext context, TaskRepository taskRepository)
    {
        _context = context;
        _taskRepository = taskRepository;
    }

    public Task<string> CreateTrackAsync(TrackDto track, ReportDocument report, string? templateSnapshot,
        AgentSessionDto lead, CancellationToken ct) =>
        CreateTrackAsync(new(track, report, templateSnapshot, lead, [], null, null, []), null, ct);

    public Task<string> CreateTrackAsync(TrackCreation creation, ResourceCreationKey? key, CancellationToken ct)
    {
        var (track, initialReport, templateSnapshot, initialLeadSession, tasks, firstInput, preparation, events, dailyDate, systemArea) = creation;
        _context.EnsureInitialized();

        var leadSessionId = string.IsNullOrWhiteSpace(track.LeadSessionId)
            ? initialLeadSession.Id
            : track.LeadSessionId;

        var workspaceSpecJson = SqliteJsonUpcaster.SerializeElement(track.WorkspaceSpec);
        var templateInputsJson = SqliteJsonUpcaster.SerializeElement(track.TemplateInputs);
        var dataJson = SqliteJsonUpcaster.SerializeElement(track.Data);
        var enabledPluginsJson = SqliteJsonUpcaster.SerializeStringList(track.EnabledPlugins);
        var blocksJson = SqliteJsonUpcaster.SerializeReportBlocks(initialReport.Blocks);
        var initialBlocksZstd = SqliteJsonUpcaster.CompressReportBlocksZstd(initialReport.Blocks);

        return _context.Writer.EnqueueAsync<string>(
            async (conn, tx, innerCt) =>
            {
                var memory = creation.MemoryBinding;
                if (memory is not null)
                {
                    if (memory.AreaId is { } areaId && !await SqliteTrackStore.IsUserAreaAsync(conn, tx, areaId, innerCt).ConfigureAwait(false))
                        throw Perga.Kernel.KernelException.NotFound($"Area '{areaId}' not found.");
                    var existing = await SqliteTrackStore.ReadMemoryTrackIdAsync(conn, tx, memory.ScopeKey, innerCt).ConfigureAwait(false);
                    if (existing is not null) return existing;
                }

                if (dailyDate is not null)
                {
                    var existing = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                        "SELECT track_id FROM daily_tracks WHERE date = @Date;", new { Date = dailyDate }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                    if (existing is not null) return existing;
                }

                if (systemArea is not null)
                {
                    await AreaRepository.UpsertAreaInternalAsync(conn, tx, systemArea, innerCt).ConfigureAwait(false);
                    await conn.ExecuteAsync(new CommandDefinition("UPDATE areas SET is_system = 1 WHERE id = @Id;",
                        new { systemArea.Id }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                }
                var replay = await CreationRepository.FindCreationInternalAsync(conn, tx, key, innerCt).ConfigureAwait(false);
                if (replay is not null)
                    return replay;
                if (await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT id FROM areas WHERE id = @AreaId;", new { track.AreaId }, tx, cancellationToken: innerCt)).ConfigureAwait(false) is null)
                    throw Perga.Kernel.KernelException.NotFound($"Area '{track.AreaId}' not found.");

                var trackExecutionTargetId = string.IsNullOrWhiteSpace(track.ExecutionTargetId)
                    ? "local"
                    : track.ExecutionTargetId.Trim();
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO tracks (
                            id, area_id, title, cwd,
                            workspace_driver, workspace_spec_json, lead_session_id,
                            template_id, template_snapshot, template_inputs_json, template_state_json, data_json, data_rev,
                            flow_spec_json, flow_rev, flow_file_sha256,
                            enabled_plugins_json, closed_at_ms, pinned_at_ms, sort_order,
                            created_at_ms, updated_at_ms, execution_target_id, origin_track_id
                        )
                        VALUES (
                            @Id, @AreaId, @Title, @Cwd,
                            @WorkspaceDriver, @WorkspaceSpecJson, @LeadSessionId,
                            @TemplateId, @TemplateSnapshot, @TemplateInputsJson, '{}', @DataJson, @DataRev,
                            @FlowSpecJson, @FlowRev, @FlowFileSha256,
                            @EnabledPluginsJson, @ClosedAtMs, @PinnedAtMs, @SortOrder,
                            @CreatedAtMs, @UpdatedAtMs, @ExecutionTargetId, @OriginTrackId
                        );
                        """,
                        new
                        {
                            track.Id,
                            track.AreaId,
                            track.Title,
                            track.Cwd,
                            track.WorkspaceDriver,
                            WorkspaceSpecJson = workspaceSpecJson,
                            LeadSessionId = leadSessionId,
                            track.TemplateId,
                            TemplateSnapshot = templateSnapshot,
                            TemplateInputsJson = templateInputsJson,
                            DataJson = dataJson,
                            track.DataRev,
                            FlowSpecJson = string.IsNullOrWhiteSpace(track.FlowSpecJson) ? """{"rev":0,"nodes":[]}""" : track.FlowSpecJson,
                            track.FlowRev,
                            track.FlowFileSha256,
                            EnabledPluginsJson = enabledPluginsJson,
                            track.ClosedAtMs,
                            track.PinnedAtMs,
                            track.SortOrder,
                            track.CreatedAtMs,
                            track.UpdatedAtMs,
                            ExecutionTargetId = trackExecutionTargetId,
                            track.OriginTrackId
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

                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE report_revisions SET summary = @Summary, leading_comment = @LeadingComment WHERE track_id = @Id AND doc_rev = @DocRev;",
                    new { track.Id, initialReport.DocRev, initialReport.Summary, initialReport.LeadingComment }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                if (dailyDate is not null)
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO daily_tracks (date, track_id, created_at_ms) VALUES (@Date, @Id, @CreatedAtMs);",
                        new { Date = dailyDate, track.Id, track.CreatedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                if (memory is not null)
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO memory_tracks (scope_key, area_id, track_id, created_at_ms) VALUES (@ScopeKey, @AreaId, @TrackId, @CreatedAtMs);",
                        new { memory.ScopeKey, memory.AreaId, TrackId = track.Id, track.CreatedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                await SessionRepository.UpsertSessionInternalAsync(conn, tx, initialLeadSession, innerCt).ConfigureAwait(false);
                foreach (var task in tasks)
                    await _taskRepository.UpsertTaskInternalAsync(conn, tx, task, innerCt).ConfigureAwait(false);
                var input = firstInput is null ? null
                    : await SessionInputsRepository.InsertInputAsync(conn, tx, initialLeadSession, firstInput, innerCt).ConfigureAwait(false);
                if (preparation is not null)
                    await conn.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO track_workspace_preparations VALUES (@Id, @Intent);",
                    new { track.Id, Intent = JsonSerializer.Serialize(preparation) }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                await CreationRepository.InsertResourceReceiptAsync(conn, tx, key, track.Id, track.CreatedAtMs, input?.Receipt.InputId, innerCt).ConfigureAwait(false);
                foreach (var clock in creation.FlowClocks)
                    await TrackSchedulesRepository.UpsertFlowScheduleInternalAsync(conn, tx, clock, innerCt).ConfigureAwait(false);
                foreach (var evt in events)
                    await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                return track.Id;
            },
            ct);
    }
}
