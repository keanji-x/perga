using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore
{
    private const long DayMs = 86_400_000L;
    private const long EventRetentionWindowMs = 7L * DayMs;
    private const long DiagnosticRetentionWindowMs = 14L * DayMs;
    private const int EventRetentionMaxRows = 50_000;
    private const int EventRetentionPayloadBytes = 32 * 1024 * 1024;
    private const int EventPruneBatchSize = 1_000;
    private const int ActiveRevisionWindow = 100;
    private const int ClosedRevisionWindow = 10;
    private const int ToolResultInlineCompactThresholdBytes = 4_096;
    private const int MaintenanceBatchSize = 250;

    public async Task<DbMaintenanceStatsDto> RunMaintenancePassAsync(long nowMs, CancellationToken ct)
    {
        EnsureInitialized();

        await PruneTrackWakesAsync(nowMs, ct).ConfigureAwait(false);
        await PruneTrackSchedulesAsync(nowMs, ct).ConfigureAwait(false);
        var prunedEvents = await PruneEventsAsync(nowMs, ct).ConfigureAwait(false);
        var thinnedRevisions = await RunMaintenanceBatchesAsync(
            () => ThinReportRevisionsAsync(nowMs, ct), MaintenanceBatchSize).ConfigureAwait(false);
        var compactedItems = await RunMaintenanceBatchesAsync(
            () => SlimSessionItemsAsync(nowMs, ct), MaintenanceBatchSize).ConfigureAwait(false);
        var compactedAttempts = await RunMaintenanceBatchesAsync(
            () => CompactAttemptDiagnosticsAsync(nowMs, ct), MaintenanceBatchSize).ConfigureAwait(false);
        var reclaimedBlobs = await SweepUnreferencedBlobsAsync(ct).ConfigureAwait(false);
        var reclaimedPages = await VacuumAndCheckpointAsync(ct).ConfigureAwait(false);

        return new DbMaintenanceStatsDto(prunedEvents, thinnedRevisions, compactedItems, reclaimedPages)
        {
            CompactedAttempts = compactedAttempts,
            ReclaimedBlobs = reclaimedBlobs
        };
    }

    private static async Task<int> RunMaintenanceBatchesAsync(Func<Task<int>> runBatch, int batchSize)
    {
        var total = 0;
        while (true)
        {
            var count = await runBatch().ConfigureAwait(false);
            total += count;
            if (count < batchSize)
            {
                break;
            }
        }

        return total;
    }

    private async Task<int> PruneEventsAsync(long nowMs, CancellationToken ct)
    {
        // Both limits produce a single replay boundary. Leave newer events in ID order even
        // when timestamps arrive out of order.
        long cutoff;
        await using (var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false))
        {
            cutoff = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                WITH recent AS (
                    SELECT id, length(CAST(payload_json AS BLOB)) AS bytes
                    FROM events ORDER BY id DESC LIMIT @ProbeRows
                ),
                retention AS (
                    SELECT id, row_number() OVER (ORDER BY id DESC) AS position,
                           sum(bytes) OVER (ORDER BY id DESC) AS payload_bytes
                    FROM recent
                )
                SELECT max(
                    COALESCE((SELECT min(id) - 1 FROM events WHERE created_at_ms > @AgeCutoff),
                             (SELECT max(id) FROM events), 0),
                    COALESCE((SELECT max(id) FROM retention
                              WHERE position > @MaxRows OR payload_bytes > @MaxBytes), 0)
                );
                """,
                new
                {
                    AgeCutoff = nowMs - EventRetentionWindowMs,
                    ProbeRows = EventRetentionMaxRows + 1,
                    MaxRows = EventRetentionMaxRows,
                    MaxBytes = EventRetentionPayloadBytes
                },
                cancellationToken: ct)).ConfigureAwait(false);
        }

        return await RunMaintenanceBatchesAsync(
            () => _writer.EnqueueAsync<int>(
                async (conn, tx, innerCt) => await conn.ExecuteAsync(new CommandDefinition(
                    """
                    DELETE FROM events WHERE id IN (
                        SELECT id FROM events WHERE id <= @Cutoff ORDER BY id LIMIT @BatchSize
                    );
                    """,
                    new { Cutoff = cutoff, BatchSize = EventPruneBatchSize }, tx, cancellationToken: innerCt))
                    .ConfigureAwait(false), ct),
            EventPruneBatchSize).ConfigureAwait(false);
    }

    public async Task<int> PruneTrackWakesAsync(long nowMs, CancellationToken ct)
    {
        await _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_wakes SET state='{TrackWakeStates.Dropped}', reason='{TrackWakeReasons.MaintenanceExpired}', updated_at_ms=@Now
                WHERE (state IN ('{TrackWakeStates.Pending}','{TrackWakeStates.Delivering}') AND EXISTS (
                    SELECT 1 FROM tracks t WHERE t.id=track_id AND t.closed_at_ms IS NOT NULL))
                   OR (state='{TrackWakeStates.Delivering}' AND updated_at_ms < @Stale);
                """, new { Now = nowMs, Stale = nowMs - 14 * DayMs }, tx, cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return await RunMaintenanceBatchesAsync(() => _writer.EnqueueAsync<int>(async (conn, tx, token) =>
            await conn.ExecuteAsync(new CommandDefinition($"""
                DELETE FROM track_wakes WHERE ordinal IN (
                    SELECT ordinal FROM (
                        SELECT ordinal, updated_at_ms, row_number() OVER (ORDER BY updated_at_ms DESC, ordinal DESC) AS position
                        FROM track_wakes WHERE state IN ('{TrackWakeStates.Done}','{TrackWakeStates.Dropped}')
                    ) WHERE updated_at_ms < @Cutoff OR position > 50000 ORDER BY ordinal LIMIT 1000
                );
                """, new { Cutoff = nowMs - 7 * DayMs }, tx, cancellationToken: token)).ConfigureAwait(false), ct), 1000).ConfigureAwait(false);
    }

    public async Task<int> PruneTrackSchedulesAsync(long nowMs, CancellationToken ct)
    {
        await _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE track_schedules SET state='{TrackScheduleStates.Canceled}', next_fire_at_ms=NULL, updated_at_ms=@Now
                WHERE state='{TrackScheduleStates.Active}' AND EXISTS (
                    SELECT 1 FROM tracks t WHERE t.id=track_id AND t.closed_at_ms IS NOT NULL);
                """, new { Now = nowMs }, tx, cancellationToken: token)).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return await RunMaintenanceBatchesAsync(() => _writer.EnqueueAsync<int>(async (conn, tx, token) =>
            await conn.ExecuteAsync(new CommandDefinition($"""
                DELETE FROM track_schedules WHERE rowid IN (
                    SELECT rowid FROM track_schedules
                    WHERE state IN ('{TrackScheduleStates.Done}','{TrackScheduleStates.Canceled}')
                      AND updated_at_ms < @Cutoff
                    ORDER BY updated_at_ms ASC, track_id ASC, id ASC LIMIT 1000
                );
                """, new { Cutoff = nowMs - 90L * DayMs }, tx, cancellationToken: token)).ConfigureAwait(false), ct), 1000).ConfigureAwait(false);
    }

    private Task<int> ThinReportRevisionsAsync(long nowMs, CancellationToken ct) =>
        _writer.EnqueueAsync<int>(
            async (conn, tx, innerCt) => await conn.ExecuteAsync(new CommandDefinition(
                """
                WITH bounds AS MATERIALIZED (
                    SELECT t.id AS track_id,
                           (SELECT min(r.doc_rev) FROM report_revisions r WHERE r.track_id = t.id) AS first_rev,
                           CASE WHEN t.closed_at_ms <= @ClosedCutoff THEN (
                               SELECT r.doc_rev FROM report_revisions r WHERE r.track_id = t.id
                               ORDER BY r.doc_rev DESC LIMIT 1 OFFSET @ClosedOffset
                           ) ELSE (
                               SELECT r.doc_rev FROM report_revisions r WHERE r.track_id = t.id
                               ORDER BY r.doc_rev DESC LIMIT 1 OFFSET @ActiveOffset
                           ) END AS oldest_recent_rev
                    FROM tracks t
                )
                DELETE FROM report_revisions WHERE rowid IN (
                    SELECT r.rowid FROM bounds b
                    JOIN report_revisions r ON r.track_id = b.track_id
                    WHERE r.doc_rev > b.first_rev AND r.doc_rev < b.oldest_recent_rev
                    LIMIT @BatchSize
                );
                """,
                new
                {
                    ClosedCutoff = nowMs - DiagnosticRetentionWindowMs,
                    ClosedOffset = ClosedRevisionWindow - 1,
                    ActiveOffset = ActiveRevisionWindow - 1,
                    BatchSize = MaintenanceBatchSize
                }, tx, cancellationToken: innerCt)).ConfigureAwait(false), ct);

    private async Task<int> SlimSessionItemsAsync(long nowMs, CancellationToken ct)
    {
        const string eligible = """
            FROM session_items i
            JOIN session_turns t ON t.id = i.turn_id
            JOIN agent_sessions s ON s.id = i.session_id
            WHERE lower(t.status) IN ('completed', 'failed', 'interrupted', 'canceled', 'cancelled')
              AND t.completed_at_ms IS NOT NULL
              AND (t.completed_at_ms <= @Cutoff OR t.epoch < s.epoch - 1)
              AND (
                  lower(i.kind) = 'thinking'
                  OR (
                      lower(i.kind) = 'tool_result'
                      AND (length(CAST(i.payload_json AS BLOB)) > @MaxBytes OR json_type(i.payload_json, '$.blobRef') = 'text')
                      AND COALESCE(json_extract(i.payload_json, '$.compacted'), 0) != 1
                  )
              )
            """;
        var parameters = new DynamicParameters(new
        {
            Cutoff = nowMs - DiagnosticRetentionWindowMs,
            MaxBytes = ToolResultInlineCompactThresholdBytes,
            BatchSize = MaintenanceBatchSize
        });
        await using (var reader = await OpenReadConnectionAsync(ct).ConfigureAwait(false))
        {
            var ids = (await reader.QueryAsync<long>(new CommandDefinition(
                "SELECT i.id " + eligible + " ORDER BY i.id LIMIT @BatchSize;",
                parameters, cancellationToken: ct)).ConfigureAwait(false)).AsList();
            if (ids.Count == 0)
            {
                return 0;
            }

            parameters.Add("Ids", ids);
        }

        return await _writer.EnqueueAsync<int>(async (conn, tx, innerCt) =>
        {
            var items = await conn.QueryAsync<SessionItemRow>(new CommandDefinition(
                "SELECT i.id AS Id, i.kind AS Kind, i.payload_json AS PayloadJson " + eligible + " AND i.id IN @Ids;",
                parameters, tx, cancellationToken: innerCt)).ConfigureAwait(false);

            var compacted = 0;
            foreach (var item in items)
            {
                var thinking = string.Equals(item.Kind, "thinking", StringComparison.OrdinalIgnoreCase);
                var sql = thinking
                    ? "DELETE FROM session_items WHERE id = @Id;"
                    : "UPDATE session_items SET payload_json = @PayloadJson WHERE id = @Id;";
                compacted += await conn.ExecuteAsync(new CommandDefinition(sql,
                    new { item.Id, PayloadJson = thinking ? null : ColdBlobStore.BuildCompactedTombstone(item.PayloadJson) },
                    tx, cancellationToken: innerCt)).ConfigureAwait(false);
            }

            return compacted;
        }, ct).ConfigureAwait(false);
    }

    private async Task<int> CompactAttemptDiagnosticsAsync(long nowMs, CancellationToken ct)
    {
        const string eligible = """
            FROM task_attempts
            WHERE finished_at_ms <= @Cutoff
              AND lower(status) IN ('done', 'failed', 'canceled', 'cancelled', 'interrupted')
              AND (
                  (
                      session_id IS NULL AND json_type(worker_result_json, '$.stdoutTruncated') IN ('true', 'false')
                      AND (json_type(worker_result_json, '$.stdout') = 'text' OR json_type(worker_result_json, '$.stderr') = 'text')
                  )
                  OR (
                      COALESCE(json_extract(gate_verdict_json, '$.outputExpired'), 0) != 1
                      AND (json_type(gate_verdict_json, '$.blobRef') = 'text' OR (
                          json_type(gate_verdict_json, '$.steps') = 'array' AND EXISTS (
                              SELECT 1 FROM json_each(gate_verdict_json, '$.steps') step
                              WHERE step.type = 'object'
                                AND (json_type(step.value, '$.stdout') = 'text' OR json_type(step.value, '$.stderr') = 'text')
                          )
                      ))
                  )
              )
            """;
        var parameters = new DynamicParameters(new
        {
            Cutoff = nowMs - DiagnosticRetentionWindowMs,
            BatchSize = MaintenanceBatchSize
        });
        await using (var reader = await OpenReadConnectionAsync(ct).ConfigureAwait(false))
        {
            var ids = (await reader.QueryAsync<string>(new CommandDefinition(
                "SELECT attempt_id " + eligible + " ORDER BY finished_at_ms, attempt_id LIMIT @BatchSize;",
                parameters, cancellationToken: ct)).ConfigureAwait(false)).AsList();
            if (ids.Count == 0)
            {
                return 0;
            }

            parameters.Add("Ids", ids);
        }

        return await _writer.EnqueueAsync<int>(async (conn, tx, innerCt) =>
        {
            var attempts = await conn.QueryAsync<AttemptDiagnosticRow>(new CommandDefinition(
                """
                SELECT attempt_id AS AttemptId, track_id AS TrackId, session_id AS SessionId,
                       worker_result_json AS WorkerResultJson, gate_verdict_json AS GateVerdictJson
                """ + " " + eligible + " AND attempt_id IN @Ids;",
                parameters, tx, cancellationToken: innerCt)).ConfigureAwait(false);

            var compacted = 0;
            foreach (var attempt in attempts)
            {
                var resultJson = attempt.WorkerResultJson;
                if (attempt.SessionId is null && resultJson is not null && JsonNode.Parse(resultJson) is JsonObject result &&
                    result["stdoutTruncated"] is JsonValue truncated && truncated.TryGetValue<bool>(out _) &&
                    ExpireShellOutput(result))
                {
                    resultJson = result.ToJsonString();
                }

                var gateJson = ExpireGateDiagnostics(attempt);
                compacted += await conn.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE task_attempts
                       SET worker_result_json = @ResultJson, gate_verdict_json = @GateJson
                     WHERE attempt_id = @AttemptId;
                    """,
                    new { attempt.AttemptId, ResultJson = resultJson, GateJson = gateJson },
                    tx, cancellationToken: innerCt)).ConfigureAwait(false);
            }

            return compacted;
        }, ct).ConfigureAwait(false);
    }

    private string? ExpireGateDiagnostics(AttemptDiagnosticRow attempt)
    {
        var gateJson = attempt.GateVerdictJson;
        if (gateJson is null || JsonNode.Parse(gateJson) is not JsonObject storedGate ||
            storedGate["outputExpired"]?.GetValue<bool>() == true)
        {
            return gateJson;
        }

        var verdict = _coldBlobStore.Hydrate(gateJson, "task_attempts.gate_verdict_json");
        if (JsonNode.Parse(verdict.GetRawText()) is JsonObject gate && gate["steps"] is JsonArray steps)
        {
            var expired = false;
            foreach (var step in steps.OfType<JsonObject>())
            {
                expired |= ExpireShellOutput(step);
            }

            if (expired)
            {
                gate["outputExpired"] = true;
                gateJson = _coldBlobStore.StoreOrInline(attempt.TrackId, JsonSerializer.SerializeToElement(gate));
                storedGate = (JsonObject)JsonNode.Parse(gateJson)!;
            }
        }

        // Mark cold envelopes after inspection so maintenance does not reopen them each pass.
        if (storedGate["blobRef"] is not null)
        {
            storedGate["outputExpired"] = true;
            gateJson = storedGate.ToJsonString();
        }

        return gateJson;
    }

    private static bool ExpireShellOutput(JsonObject value)
    {
        var removed = value.Remove("stdout");
        removed |= value.Remove("stderr");
        if (removed)
        {
            value["outputExpired"] = true;
        }

        return removed;
    }

    private async Task<int> SweepUnreferencedBlobsAsync(CancellationToken ct)
    {
        using var files = _coldBlobStore.EnumerateBlobRefs().GetEnumerator();
        if (!files.MoveNext())
        {
            return 0;
        }

        HashSet<string> refs;
        await using (var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false))
        {
            refs = await ReadColdBlobRefsAsync(conn, ct).ConfigureAwait(false);
        }

        var candidates = new List<string>(MaintenanceBatchSize);
        var reclaimed = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            if (!refs.Contains(files.Current))
            {
                candidates.Add(files.Current);
                if (candidates.Count == MaintenanceBatchSize)
                {
                    reclaimed += await ReclaimBlobsAsync(candidates, ct).ConfigureAwait(false);
                    candidates.Clear();
                }
            }
        }
        while (files.MoveNext());

        if (candidates.Count > 0)
        {
            reclaimed += await ReclaimBlobsAsync(candidates, ct).ConfigureAwait(false);
        }

        return reclaimed;
    }

    private Task<int> ReclaimBlobsAsync(IReadOnlyList<string> candidates, CancellationToken ct) =>
        // All cold blob writes and inserts run on this writer. Recheck after any
        // queued business writes have committed before deleting the candidate files.
        _writer.EnqueueNonTransactionalAsync<int>(async (conn, innerCt) =>
        {
            var current = await ReadColdBlobRefsAsync(conn, innerCt, candidates).ConfigureAwait(false);
            var reclaimed = 0;
            foreach (var candidate in candidates)
            {
                innerCt.ThrowIfCancellationRequested();
                if (!current.Contains(candidate) && _coldBlobStore.DeleteBlobFileIfPresent(candidate))
                {
                    reclaimed++;
                }
            }

            return reclaimed;
        }, ct);

    private static async Task<HashSet<string>> ReadColdBlobRefsAsync(
        SqliteConnection conn, CancellationToken ct, IReadOnlyList<string>? candidates = null)
    {
        const string sessionRefs = """
            SELECT json_extract(payload_json, '$.blobRef') FROM session_items
            WHERE json_type(payload_json, '$.blobRef') = 'text'
            """;
        const string gateRefs = """
            SELECT json_extract(gate_verdict_json, '$.blobRef') FROM task_attempts
            WHERE json_type(gate_verdict_json, '$.blobRef') = 'text'
            """;
        var sql = candidates is null ? sessionRefs + " UNION ALL " + gateRefs
            : sessionRefs + " AND json_extract(payload_json, '$.blobRef') IN @Candidates UNION ALL " +
              gateRefs + " AND json_extract(gate_verdict_json, '$.blobRef') IN @Candidates";
        var refs = await conn.QueryAsync<string>(
            new CommandDefinition(sql, new { Candidates = candidates }, cancellationToken: ct)).ConfigureAwait(false);
        return refs.ToHashSet(StringComparer.Ordinal);
    }

    private sealed record AttemptDiagnosticRow(
        string AttemptId, string TrackId, string? SessionId, string? WorkerResultJson, string? GateVerdictJson);

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

                var busyTimeout = await conn.ExecuteScalarAsync<int>(
                    new CommandDefinition("PRAGMA busy_timeout;", cancellationToken: innerCt)).ConfigureAwait(false);
                await conn.ExecuteAsync(
                    new CommandDefinition("PRAGMA busy_timeout = 0;", cancellationToken: innerCt)).ConfigureAwait(false);
                try
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "PRAGMA wal_checkpoint(TRUNCATE);", commandTimeout: 1, cancellationToken: innerCt)).ConfigureAwait(false);
                }
                finally
                {
                    await conn.ExecuteAsync($"PRAGMA busy_timeout = {busyTimeout};").ConfigureAwait(false);
                }

                return reclaimed;
            },
            ct);
}
