using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Message SQL primitives shared by input admission, turn recording and queue delivery.
/// Each operation uses the caller's transaction; this class never enqueues work.</summary>
internal static class SessionMessageSql
{
    internal static Task<long> InsertSessionMessageAsync(SqliteConnection conn, SqliteTransaction tx, SessionMessage message, CancellationToken ct) =>
        conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO session_messages (id, session_id, kind, author_id, request_key, request_hash,
                                          target_turn_id, turn_id, delivered_turn_id, content, delivery, created_at_ms)
            VALUES (@Id, @SessionId, @Kind, @AuthorId, @RequestKey, @RequestHash, @TargetTurnId, @TurnId, @DeliveredTurnId,
                    @Content, @Delivery, @CreatedAtMs)
            RETURNING ordinal;
            """, message, tx, cancellationToken: ct));

    internal static async Task<bool> TransitionMessageAsync(SqliteConnection conn, SqliteTransaction tx,
        SessionMessage message, string expected, CancellationToken ct) =>
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE session_messages
            SET target_turn_id = COALESCE(target_turn_id, @TargetTurnId),
                turn_id = COALESCE(@TurnId, turn_id), delivered_turn_id = @DeliveredTurnId, delivery = @Delivery
            WHERE session_id = @SessionId AND id = @Id AND delivery = @Expected
              AND (target_turn_id IS NULL OR @TargetTurnId IS target_turn_id)
              AND (turn_id IS NULL OR @TurnId IS NULL OR @TurnId IS turn_id)
              AND (@Delivery != 'withdrawn' OR turn_id IS NULL);
            """, new { message.SessionId, message.Id, message.TargetTurnId, message.TurnId,
                message.DeliveredTurnId, message.Delivery, Expected = expected }, tx, cancellationToken: ct)).ConfigureAwait(false) == 1;

    internal static Task<long> ReadMessageOrdinalAsync(SqliteConnection conn, SqliteTransaction tx,
        SessionMessage message, CancellationToken ct) =>
        conn.QuerySingleAsync<long>(new CommandDefinition("""
            SELECT ordinal FROM session_messages WHERE session_id = @SessionId AND id = @Id;
            """, new { message.SessionId, message.Id }, tx, cancellationToken: ct));

    internal static void RequireOne(int updated)
    {
        if (updated != 1)
            throw new InvalidOperationException("The persisted session input changed unexpectedly.");
    }
}
