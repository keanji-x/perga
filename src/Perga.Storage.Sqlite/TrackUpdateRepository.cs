using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Track/template/data updates and atomic Flow task/clock commits.</summary>
internal sealed class TrackUpdateRepository
{
    private readonly SqliteStoreContext _context;
    private readonly TaskRepository _taskRepository;

    internal TrackUpdateRepository(SqliteStoreContext context, TaskRepository taskRepository)
    {
        _context = context;
        _taskRepository = taskRepository;
    }

    public Task UpdateTrackAndEmitAsync(TrackDto track, EventEnvelopeDto? evt, CancellationToken ct, bool writeData = false) =>
        UpdateTrackCoreAsync(track, null, evt, ct, writeData: writeData);

    public Task UpdateTrackTemplateAndEmitAsync(TrackDto track, string? templateSnapshot, EventEnvelopeDto evt, CancellationToken ct, bool writeData = false, TrackFlowUpdate? flow = null) =>
        UpdateTrackCoreAsync(track, templateSnapshot, evt, ct, replaceTemplateSnapshot: true, writeData, flow);

    public Task RecordFlowFileHashAsync(string trackId, long flowRev, string sha256, CancellationToken ct)
    {
        _context.EnsureInitialized();
        return _context.Writer.EnqueueAsync(async (conn, tx, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tracks SET flow_file_sha256=@Sha256 WHERE id=@TrackId AND flow_rev=@FlowRev;",
                new { TrackId = trackId, FlowRev = flowRev, Sha256 = sha256 }, tx, cancellationToken: token))
                .ConfigureAwait(false);
        }, ct);
    }

    public Task CommitTrackFlowAsync(
        TrackDto track,
        long expectedFlowRev,
        IReadOnlyList<TaskDto> upsertedTasks,
        IReadOnlyList<TrackScheduleDto> activeFlowSchedules,
        IReadOnlyList<string> canceledFlowScheduleIds,
        IReadOnlyList<EventEnvelopeDto> events,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(upsertedTasks);
        ArgumentNullException.ThrowIfNull(activeFlowSchedules);
        ArgumentNullException.ThrowIfNull(canceledFlowScheduleIds);
        ArgumentNullException.ThrowIfNull(events);
        _context.EnsureInitialized();

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await CommitTrackFlowInternalAsync(conn, tx, track, expectedFlowRev, upsertedTasks,
                    activeFlowSchedules, canceledFlowScheduleIds, innerCt).ConfigureAwait(false);
                foreach (var evt in events)
                {
                    await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }

    private async Task CommitTrackFlowInternalAsync(
        SqliteConnection conn, SqliteTransaction tx, TrackDto track, long expectedFlowRev,
        IReadOnlyList<TaskDto> upsertedTasks, IReadOnlyList<TrackScheduleDto> activeFlowSchedules,
        IReadOnlyList<string> canceledFlowScheduleIds, CancellationToken innerCt,
        IReadOnlySet<string>? resetClockIds = null)
    {
        var updated = await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE tracks
            SET flow_spec_json = @FlowSpecJson,
                flow_rev = @FlowRev,
                flow_file_sha256 = @FlowFileSha256,
                updated_at_ms = @UpdatedAtMs
            WHERE id = @Id AND flow_rev = @ExpectedFlowRev;
            """,
            new
            {
                FlowSpecJson = string.IsNullOrWhiteSpace(track.FlowSpecJson) ? FlowSyntax.DefaultFlowSpecJson : track.FlowSpecJson,
                track.FlowRev,
                track.FlowFileSha256,
                track.UpdatedAtMs,
                track.Id,
                ExpectedFlowRev = expectedFlowRev
            },
            transaction: tx,
            cancellationToken: innerCt)).ConfigureAwait(false);
        if (updated == 0)
        {
            throw KernelException.Conflict(
                $"Flow revision conflict: expected {expectedFlowRev}, but the track flow was modified concurrently.");
        }

        foreach (var task in upsertedTasks)
        {
            await _taskRepository.UpsertTaskInternalAsync(conn, tx, task, innerCt).ConfigureAwait(false);
        }

        foreach (var schedule in activeFlowSchedules)
        {
            await TrackSchedulesRepository.UpsertFlowScheduleInternalAsync(conn, tx, schedule, innerCt,
                resetProgress: resetClockIds?.Contains(schedule.Id) == true).ConfigureAwait(false);
        }

        foreach (var scheduleId in canceledFlowScheduleIds)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                $"""
                UPDATE track_schedules
                SET state = '{TrackScheduleStates.Canceled}', next_fire_at_ms = NULL, updated_at_ms = @Now
                WHERE track_id = @TrackId AND id = @ScheduleId AND node_key IS NOT NULL AND state = '{TrackScheduleStates.Active}';
                """,
                new { TrackId = track.Id, ScheduleId = scheduleId, Now = track.UpdatedAtMs },
                transaction: tx,
                cancellationToken: innerCt)).ConfigureAwait(false);
        }

        if (activeFlowSchedules.Count > 0 || canceledFlowScheduleIds.Count > 0)
        {
            await TrackSchedulesRepository.InsertSchedulesEventAsync(conn, tx, track.Id, track.UpdatedAtMs, innerCt).ConfigureAwait(false);
        }
    }

    private Task UpdateTrackCoreAsync(TrackDto track, string? templateSnapshot, EventEnvelopeDto? evt, CancellationToken ct, bool replaceTemplateSnapshot = false, bool writeData = false, TrackFlowUpdate? flow = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        _context.EnsureInitialized();

        var workspaceSpecJson = SqliteJsonUpcaster.SerializeElement(track.WorkspaceSpec);
        var templateInputsJson = SqliteJsonUpcaster.SerializeElement(track.TemplateInputs);
        var dataJson = writeData ? SqliteJsonUpcaster.SerializeElement(track.Data) : null;
        var enabledPluginsJson = SqliteJsonUpcaster.SerializeStringList(track.EnabledPlugins);

        return _context.Writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                if (writeData)
                {
                    var expectedRev = checked(track.DataRev - 1);
                    var updated = await conn.ExecuteAsync(new CommandDefinition(
                        "UPDATE tracks SET data_json = @DataJson, data_rev = @DataRev WHERE id = @Id AND data_rev = @ExpectedDataRev;",
                        new { DataJson = dataJson, track.DataRev, track.Id, ExpectedDataRev = expectedRev }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                    if (updated == 0)
                        throw KernelException.Conflict($"Data revision conflict: expected {expectedRev}, but the track data was modified concurrently.");
                }

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
                            template_snapshot = CASE WHEN @ReplaceTemplateSnapshot = 1 THEN @TemplateSnapshot ELSE template_snapshot END,
                            template_id = @TemplateId,
                            template_inputs_json = @TemplateInputsJson,
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
                            ReplaceTemplateSnapshot = replaceTemplateSnapshot ? 1 : 0,
                            TemplateInputsJson = templateInputsJson,
                            EnabledPluginsJson = enabledPluginsJson,
                            track.ClosedAtMs,
                            track.PinnedAtMs,
                            track.SortOrder,
                            track.UpdatedAtMs
                        },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                if (flow is not null)
                    await CommitTrackFlowInternalAsync(conn, tx, track, flow.ExpectedRev, flow.Tasks,
                        flow.Clocks, flow.CanceledClockIds, innerCt, flow.ResetClockIds).ConfigureAwait(false);
                if (track.ClosedAtMs is not null)
                    await conn.ExecuteAsync(new CommandDefinition($"""
                        UPDATE track_wakes SET state='{TrackWakeStates.Dropped}', reason='{TrackWakeReasons.TrackClosed}', updated_at_ms=@Now
                        WHERE track_id=@TrackId AND state IN ('{TrackWakeStates.Pending}','{TrackWakeStates.Delivering}');
                        """, new { TrackId = track.Id, Now = track.UpdatedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false);

                if (track.ClosedAtMs is not null)
                {
                    var canceled = await conn.ExecuteAsync(new CommandDefinition($"""
                        UPDATE track_schedules SET state='{TrackScheduleStates.Canceled}', next_fire_at_ms=NULL, updated_at_ms=@Now
                        WHERE track_id=@TrackId AND state='{TrackScheduleStates.Active}';
                        """, new { TrackId = track.Id, Now = track.UpdatedAtMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                    if (canceled > 0) await TrackSchedulesRepository.InsertSchedulesEventAsync(conn, tx, track.Id, track.UpdatedAtMs, innerCt).ConfigureAwait(false);
                }

                if (evt is not null)
                {
                    await EventRepository.InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
                }
            },
            ct);
    }
}
