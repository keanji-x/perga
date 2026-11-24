using Dapper;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Atomic reconciliation of interrupted tasks, turns and sessions on boot.</summary>
internal sealed class BootRecoveryRepository
{
    private readonly SqliteStoreContext _context;

    internal BootRecoveryRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public Task<IReadOnlyList<string>> MarkInFlightInterruptedOnBootAsync(CancellationToken ct)
    {
        _context.EnsureInitialized();
        var nowMs = _context.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        return _context.Writer.EnqueueAsync<IReadOnlyList<string>>(
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
                                    AND task.kind NOT IN ('flow_watch', 'flow_track')
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

                var ownedSessionIds = (await conn.QueryAsync<string>(new CommandDefinition("""
                    SELECT DISTINCT tk.session_id FROM tasks tk
                    JOIN tracks tr ON tr.id = tk.track_id
                    WHERE tk.status IN ('running', 'verifying') AND tk.owns_session = 1
                      AND tk.session_id IS NOT NULL AND tk.session_id IS NOT tr.lead_session_id;
                    """, transaction: tx, cancellationToken: innerCt)).ConfigureAwait(false)).AsList();
                foreach (var sessionId in ownedSessionIds)
                    await SessionMessageSql.InterruptUnstartedOwnedSessionWorkAsync(conn, tx, sessionId, nowMs, innerCt)
                        .ConfigureAwait(false);

                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE agent_sessions SET archived_at_ms = COALESCE(archived_at_ms, @NowMs), updated_at_ms = @NowMs,
                        status = CASE WHEN status = 'running' OR (status = 'queued' AND NOT EXISTS (
                            SELECT 1 FROM session_turns t WHERE t.session_id = agent_sessions.id
                              AND LOWER(t.status) IN ('queued', 'running', 'verifying')
                        )) THEN 'idle' ELSE status END
                    WHERE id IN @OwnedSessionIds;
                    """, new { OwnedSessionIds = ownedSessionIds, NowMs = nowMs }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE tasks
                        SET status = 'failed',
                            status_detail = 'The host stopped during this task.',
                            outcome = CASE WHEN block_id = 'flow:' || task_key AND json_type(context_json, '$.flowNode') IS 'true' THEN 'blocked' ELSE outcome END,
                            outcome_summary = CASE WHEN block_id = 'flow:' || task_key AND json_type(context_json, '$.flowNode') IS 'true' THEN 'The host stopped during this task.' ELSE outcome_summary END,
                            finished_at_ms = COALESCE(finished_at_ms, @NowMs),
                            updated_at_ms = @NowMs
                        WHERE status IN ('running', 'verifying') AND kind NOT IN ('flow_watch', 'flow_track');
                        """,
                        new { NowMs = nowMs },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                // Durable input recovery also updates delivery and emits its turn outcome event.
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE session_turns
                        SET status = 'unknown',
                            completed_at_ms = COALESCE(completed_at_ms, @NowMs)
                        WHERE LOWER(status) IN ('running', 'verifying')
                          AND NOT (status = 'running' AND EXISTS (
                              SELECT 1 FROM session_messages m
                              WHERE m.turn_id = session_turns.id AND m.session_id = session_turns.session_id
                          ));
                        """,
                        new { NowMs = nowMs },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE agent_sessions
                        SET status = 'unknown', unknown_reason = 'host_restarted',
                            unknown_turn_id = (SELECT id FROM session_turns WHERE session_id = agent_sessions.id ORDER BY turn_index DESC LIMIT 1),
                            updated_at_ms = @NowMs
                        WHERE LOWER(status) IN ('running', 'verifying', 'stopping', 'compacting');
                        """,
                        new { NowMs = nowMs },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                foreach (var trackId in affectedTrackIds)
                    if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                        "SELECT EXISTS(SELECT 1 FROM tracks WHERE id=@Id AND lead_session_id IS NOT NULL);",
                        new { Id = trackId }, tx, cancellationToken: innerCt)).ConfigureAwait(false))
                        await TrackWakesRepository.EnqueueWakeInternalAsync(conn, tx, TrackWakeRequests.Boot(trackId), nowMs, innerCt).ConfigureAwait(false);
                return affectedTrackIds;
            },
            ct);
    }
}
