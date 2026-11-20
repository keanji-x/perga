using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore
{
    public async Task<IReadOnlyList<ProviderSessionResumeFence>> ListSessionResumeFencesAsync(string trackId, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return (await conn.QueryAsync<(string SessionId, string TurnId, string NativeId, long Quiescent)>(new CommandDefinition(
            """
            SELECT id AS SessionId, COALESCE(unknown_turn_id, '') AS TurnId,
                   COALESCE(native_thread_id, '') AS NativeId, 1 AS Quiescent
            FROM agent_sessions WHERE track_id = @TrackId AND status = 'unknown';
            """, new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false)).Select(row => new ProviderSessionResumeFence(row.SessionId, row.TurnId, row.NativeId, row.Quiescent != 0)).ToArray();
    }

    public Task SaveSessionResumeFenceAsync(ProviderSessionResumeFence fence, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            RequireOne(await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE agent_sessions SET status = 'unknown', unknown_turn_id = @TurnId,
                    unknown_reason = COALESCE(unknown_reason, 'stream_lost')
                WHERE id = @SessionId;
                """, fence, tx, cancellationToken: token)).ConfigureAwait(false));
        }, ct);

    public Task CompleteSessionResumeAsync(AgentSessionDto session, PersistedSessionInput? unresolved,
        EventEnvelopeDto sessionEvent, int expectedEpoch, CancellationToken ct, SessionItemDto? boundary = null) =>
        _writer.EnqueueAsync(async (conn, tx, token) =>
        {
            var opEpoch = expectedEpoch;
            var currentEpoch = await conn.QuerySingleAsync<int>(new CommandDefinition(
                "SELECT epoch FROM agent_sessions WHERE id=@Id;", new { session.Id }, tx, cancellationToken: token)).ConfigureAwait(false);
            if (currentEpoch != opEpoch || session.Epoch != opEpoch + (boundary is null ? 0 : 1))
                throw new InvalidOperationException("Session epoch changed during recovery.");
            if (boundary is not null)
            {
                // Archive before the boundary, in the same transaction as the sweep and epoch CAS.
                await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE session_messages SET delivery='interrupted', resolved_at_ms=@Now
                    WHERE session_id=@Id AND turn_id IS NULL AND delivery IN ('queued','dispatching','unknown');
                    INSERT INTO session_items(session_id, turn_id, kind, payload_json, created_at_ms, message_ordinal)
                    SELECT session_id, NULL, 'user',
                        json_object('v',1,'content',content,'inputId',id,'epoch',@Epoch,
                            'status','interrupted','archived',json('true'),'reason','session_reset'), @Now, ordinal
                    FROM session_messages m WHERE session_id=@Id AND turn_id IS NULL
                        AND delivery='interrupted' AND resolved_at_ms=@Now
                        AND NOT EXISTS(SELECT 1 FROM session_items i WHERE i.message_ordinal=m.ordinal)
                    ORDER BY ordinal;
                    INSERT INTO session_items(session_id, turn_id, kind, payload_json, created_at_ms)
                    SELECT @Id, NULL, 'system',
                        json_object('v',1,'content',payload_json,'wakeId',id,'wakeKind',kind,'epoch',@Epoch,
                            'status','interrupted','archived',json('true'),'reason','session_reset'), @Now
                    FROM track_wakes WHERE track_id=@TrackId AND state IN ('pending','delivering')
                        AND (target_session_id=@Id OR (target_session_id IS NULL AND EXISTS(
                            SELECT 1 FROM tracks WHERE id=@TrackId AND lead_session_id=@Id)))
                        AND kind IN ('mail','task_settled','ask_answered') ORDER BY ordinal;
                    UPDATE track_wakes SET state='dropped', reason='session_reset', updated_at_ms=@Now
                    WHERE track_id=@TrackId AND state IN ('pending','delivering')
                        AND (target_session_id=@Id OR (target_session_id IS NULL AND EXISTS(
                            SELECT 1 FROM tracks WHERE id=@TrackId AND lead_session_id=@Id)));
                    """, new { session.Id, session.TrackId, Now = session.UpdatedAtMs, Epoch = opEpoch },
                    tx, cancellationToken: token)).ConfigureAwait(false);
            }
            if (unresolved is not null)
                await MarkSessionInputResolvedAsync(conn, tx, unresolved, session.UpdatedAtMs, token).ConfigureAwait(false);
            await UpsertSessionInternalAsync(conn, tx, session, token).ConfigureAwait(false);
            if (boundary is not null)
                await InsertSessionItemsAsync(conn, tx, session.TrackId, [boundary], token).ConfigureAwait(false);
            await InsertEventInternalAsync(conn, tx, sessionEvent, token).ConfigureAwait(false);
        }, ct);
}
