using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore : ISessionMessageStore
{
    private const string MessageSelect = """
        SELECT id AS Id, session_id AS SessionId, author_id AS AuthorId,
               request_key AS RequestKey, request_hash AS RequestHash, target_turn_id AS TargetTurnId,
               turn_id AS TurnId, content AS Content, delivery AS Delivery, created_at_ms AS CreatedAtMs,
               kind AS Kind, delivered_turn_id AS DeliveredTurnId
        FROM session_messages
        """;

    public ISessionMessageStore SessionMessages => this;

    Task<SessionMessage?> ISessionMessageStore.FindAsync(string sessionId, string authorId, string requestKey, CancellationToken ct) =>
        ReadMessageAsync("WHERE session_id = @SessionId AND author_id = @AuthorId AND request_key = @RequestKey",
            new { SessionId = sessionId, AuthorId = authorId, RequestKey = requestKey }, ct);

    Task<SessionMessage?> ISessionMessageStore.GetAsync(string sessionId, string messageId, CancellationToken ct) =>
        ReadMessageAsync("WHERE session_id = @SessionId AND id = @Id",
            new { SessionId = sessionId, Id = messageId }, ct);

    Task<SessionMessage?> ISessionMessageStore.NextAsync(string sessionId, CancellationToken ct) =>
        ReadMessageAsync("""
            WHERE session_id = @SessionId AND delivery = 'queued'
              AND turn_id IS NULL AND kind IN ('queue', 'steer')
            ORDER BY ordinal LIMIT 1
            """, new { SessionId = sessionId }, ct);

    private async Task<SessionMessage?> ReadMessageAsync(string clause, object parameters, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<SessionMessage>(new CommandDefinition(
            MessageSelect + "\n" + clause, parameters, cancellationToken: ct)).ConfigureAwait(false);
    }

    Task ISessionMessageStore.InsertAsync(SessionMessage message, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
            { await InsertSessionMessageAsync(conn, tx, message, innerCt).ConfigureAwait(false); }, ct);

    private static Task<long> InsertSessionMessageAsync(SqliteConnection conn, SqliteTransaction tx, SessionMessage message, CancellationToken ct) =>
        conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO session_messages (id, session_id, kind, author_id, request_key, request_hash,
                                          target_turn_id, turn_id, delivered_turn_id, content, delivery, created_at_ms)
            VALUES (@Id, @SessionId, @Kind, @AuthorId, @RequestKey, @RequestHash, @TargetTurnId, @TurnId, @DeliveredTurnId,
                    @Content, @Delivery, @CreatedAtMs)
            RETURNING ordinal;
            """, message, tx, cancellationToken: ct));

    Task<bool> ISessionMessageStore.TransitionAsync(SessionMessage message, string expected, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
            await TransitionMessageAsync(conn, tx, message, expected, innerCt).ConfigureAwait(false), ct);

    private static async Task<bool> TransitionMessageAsync(SqliteConnection conn, SqliteTransaction tx,
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

    async Task<string?> ISessionMessageStore.FindQueuedSteerAsync(SessionMessage message, string turnId, CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await ReadQueuedSteerAsync(conn, null, message, turnId, ct).ConfigureAwait(false);
    }

    private static Task<string?> ReadQueuedSteerAsync(SqliteConnection conn, SqliteTransaction? tx,
        SessionMessage message, string turnId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT a.delivery FROM session_message_steers a
            JOIN session_messages m ON m.ordinal = a.message_ordinal
            WHERE m.session_id = @SessionId AND m.id = @Id AND a.target_turn_id = @TurnId;
            """, new { message.SessionId, message.Id, TurnId = turnId }, tx, cancellationToken: ct));

    Task<bool> ISessionMessageStore.ClaimQueuedSteerAsync(SessionMessage message, string turnId, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            if (!await TransitionMessageAsync(conn, tx, message with { Delivery = "dispatching" }, "queued", innerCt).ConfigureAwait(false))
                return false;
            RequireOne(await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO session_message_steers (message_ordinal, target_turn_id, delivery)
                SELECT ordinal, @TurnId, 'dispatching' FROM session_messages
                WHERE session_id = @SessionId AND id = @Id AND turn_id IS NULL;
                """, new { message.SessionId, message.Id, TurnId = turnId }, tx, cancellationToken: innerCt))
                .ConfigureAwait(false));
            return true;
        }, ct);

    Task<string> ISessionMessageStore.SettleQueuedSteerAsync(SessionMessage message, string turnId, string outcome,
        SessionItemDto? item, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            var previous = await ReadQueuedSteerAsync(conn, tx, message, turnId, innerCt).ConfigureAwait(false);
            if (previous != "dispatching") return previous ?? "unknown";
            var delivery = outcome == "rejected" ? "queued" : outcome;
            var settled = message with { Delivery = delivery, DeliveredTurnId = delivery == "delivered" ? turnId : null };
            if (!await TransitionMessageAsync(conn, tx, settled, "dispatching", innerCt).ConfigureAwait(false))
                throw new InvalidOperationException("The claimed steering attempt lost its message.");

            RequireOne(await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE session_message_steers SET delivery = @Outcome WHERE target_turn_id = @TurnId AND delivery = 'dispatching'
                AND message_ordinal = (SELECT ordinal FROM session_messages
                    WHERE session_id = @SessionId AND id = @Id);
                """, new { message.SessionId, message.Id, Outcome = outcome, TurnId = turnId },
                tx, cancellationToken: innerCt)).ConfigureAwait(false));
            if (item is not null)
                await AppendSteeredItemAsync(conn, tx, message, item, innerCt).ConfigureAwait(false);
            return outcome;
        }, ct);

    Task<bool> ISessionMessageStore.DeliverSteerAsync(SessionMessage message, SessionItemDto item, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            if (!await TransitionMessageAsync(conn, tx, message, "dispatching", innerCt).ConfigureAwait(false)) return false;
            await AppendSteeredItemAsync(conn, tx, message, item, innerCt).ConfigureAwait(false);
            return true;
        }, ct);

    private async Task AppendSteeredItemAsync(SqliteConnection conn, SqliteTransaction tx, SessionMessage message,
        SessionItemDto item, CancellationToken ct)
    {
        var trackId = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT track_id FROM agent_sessions WHERE id = @SessionId;", new { message.SessionId },
            tx, cancellationToken: ct)).ConfigureAwait(false);
        var ordinal = await ReadMessageOrdinalAsync(conn, tx, message, ct).ConfigureAwait(false);
        await InsertSessionItemsAsync(conn, tx, trackId, [item], ct, ordinal).ConfigureAwait(false);
    }

    private static Task<long> ReadMessageOrdinalAsync(SqliteConnection conn, SqliteTransaction tx,
        SessionMessage message, CancellationToken ct) =>
        conn.QuerySingleAsync<long>(new CommandDefinition("""
            SELECT ordinal FROM session_messages WHERE session_id = @SessionId AND id = @Id;
            """, new { message.SessionId, message.Id }, tx, cancellationToken: ct));

    async Task<IReadOnlyList<SessionMessage>> ISessionMessageStore.ListQueueAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SessionMessage>(new CommandDefinition(MessageSelect + "\n" + """
            WHERE session_id = @SessionId AND turn_id IS NULL
              AND kind IN ('queue', 'steer') AND delivery IN ('queued', 'dispatching', 'unknown')
            ORDER BY ordinal;
            """, new { SessionId = sessionId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    async Task<IReadOnlyList<string>> ISessionMessageStore.ListHandoffsAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT id FROM session_messages WHERE session_id = @SessionId AND kind IN ('queue', 'steer')
              AND (turn_id IS NOT NULL OR delivered_turn_id IS NOT NULL) ORDER BY ordinal DESC LIMIT 128;
            """, new { SessionId = sessionId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    async Task<IReadOnlyList<(string TrackId, string SessionId)>> ISessionMessageStore.ListRecoverableQueuesAsync(CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<(string, string)>(new CommandDefinition("""
            SELECT DISTINCT s.track_id, s.id FROM session_messages m
            JOIN agent_sessions s ON s.id = m.session_id JOIN tracks t ON t.id = s.track_id
            WHERE m.delivery = 'queued' AND m.turn_id IS NULL AND t.closed_at_ms IS NULL AND s.archived_at_ms IS NULL
              AND NOT EXISTS (SELECT 1 FROM tasks tk WHERE tk.session_id = s.id AND (tk.kind <> 'flow_wake' OR tk.block_id <> 'flow:' || tk.task_key OR json_type(tk.context_json, '$.flowNode') IS NOT 'true') AND LOWER(tk.status) IN ('pending', 'running', 'verifying'));
            """, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }
}
