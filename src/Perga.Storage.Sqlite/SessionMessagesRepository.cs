using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Queued messages and atomic steering claims, settlement and item insertion.</summary>
internal sealed class SessionMessagesRepository : ISessionMessageStore
{
    private readonly SqliteStoreContext _context;
    private readonly SessionRepository _sessionRepository;
    private readonly SessionAttachmentsRepository _attachmentsRepository;

    internal SessionMessagesRepository(
        SqliteStoreContext context,
        SessionRepository sessionRepository,
        SessionAttachmentsRepository attachmentsRepository)
    {
        _context = context;
        _sessionRepository = sessionRepository;
        _attachmentsRepository = attachmentsRepository;
    }

    private const string MessageSelect = """
        SELECT ordinal AS Ordinal, id AS Id, session_id AS SessionId, author_id AS AuthorId,
               request_key AS RequestKey, request_hash AS RequestHash, target_turn_id AS TargetTurnId,
               turn_id AS TurnId, content AS Content, delivery AS Delivery, created_at_ms AS CreatedAtMs,
               kind AS Kind, delivered_turn_id AS DeliveredTurnId
        FROM session_messages
        """;

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
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<SessionMessageRow>(new CommandDefinition(
            MessageSelect + "\n" + clause, parameters, cancellationToken: ct)).ConfigureAwait(false);
        if (row is null) return null;
        var attachments = await _attachmentsRepository
            .LoadByMessageOrdinalsAsync(conn, tx: null, [row.Ordinal], ct).ConfigureAwait(false);
        return row.ToMessage(attachments.GetValueOrDefault(row.Ordinal));
    }

    Task<SessionMessage> ISessionMessageStore.InsertAsync(SessionMessage message, CancellationToken ct) =>
        _context.Writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            var ordinal = await SessionMessageSql.InsertSessionMessageAsync(conn, tx, message, innerCt).ConfigureAwait(false);
            if (message.AttachmentIds is { Count: > 0 } attachmentIds)
            {
                var bound = await _attachmentsRepository.BindSessionAttachmentsInternalAsync(
                    conn, tx, message.SessionId, ordinal, attachmentIds, message.CreatedAtMs, innerCt).ConfigureAwait(false);
                if (bound.Count > 0)
                {
                    return message with { Attachments = bound };
                }
            }

            return message;
        }, ct);

    Task<bool> ISessionMessageStore.TransitionAsync(SessionMessage message, string expected, CancellationToken ct) =>
        _context.Writer.EnqueueAsync(async (conn, tx, innerCt) =>
            await SessionMessageSql.TransitionMessageAsync(conn, tx, message, expected, innerCt).ConfigureAwait(false), ct);

    async Task<string?> ISessionMessageStore.FindQueuedSteerAsync(SessionMessage message, string turnId, CancellationToken ct)
    {
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
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
        _context.Writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            if (!await SessionMessageSql.TransitionMessageAsync(conn, tx, message with { Delivery = "dispatching" }, "queued", innerCt).ConfigureAwait(false))
                return false;
            SessionMessageSql.RequireOne(await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO session_message_steers (message_ordinal, target_turn_id, delivery)
                SELECT ordinal, @TurnId, 'dispatching' FROM session_messages
                WHERE session_id = @SessionId AND id = @Id AND turn_id IS NULL;
                """, new { message.SessionId, message.Id, TurnId = turnId }, tx, cancellationToken: innerCt))
                .ConfigureAwait(false));
            return true;
        }, ct);

    Task<string> ISessionMessageStore.SettleQueuedSteerAsync(SessionMessage message, string turnId, string outcome,
        SessionItemDto? item, CancellationToken ct) =>
        _context.Writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            var previous = await ReadQueuedSteerAsync(conn, tx, message, turnId, innerCt).ConfigureAwait(false);
            if (previous != "dispatching") return previous ?? "unknown";
            var delivery = outcome == "rejected" ? "queued" : outcome;
            var settled = message with { Delivery = delivery, DeliveredTurnId = delivery == "delivered" ? turnId : null };
            if (!await SessionMessageSql.TransitionMessageAsync(conn, tx, settled, "dispatching", innerCt).ConfigureAwait(false))
                throw new InvalidOperationException("The claimed steering attempt lost its message.");

            SessionMessageSql.RequireOne(await conn.ExecuteAsync(new CommandDefinition("""
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
        _context.Writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            if (!await SessionMessageSql.TransitionMessageAsync(conn, tx, message, "dispatching", innerCt).ConfigureAwait(false)) return false;
            await AppendSteeredItemAsync(conn, tx, message, item, innerCt).ConfigureAwait(false);
            return true;
        }, ct);

    private async Task AppendSteeredItemAsync(SqliteConnection conn, SqliteTransaction tx, SessionMessage message,
        SessionItemDto item, CancellationToken ct)
    {
        var trackId = await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT track_id FROM agent_sessions WHERE id = @SessionId;", new { message.SessionId },
            tx, cancellationToken: ct)).ConfigureAwait(false);
        var ordinal = await SessionMessageSql.ReadMessageOrdinalAsync(conn, tx, message, ct).ConfigureAwait(false);
        await _sessionRepository.InsertSessionItemsAsync(conn, tx, trackId, [item], ct, ordinal).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<SessionMessage>> ISessionMessageStore.ListQueueAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = (await conn.QueryAsync<SessionMessageRow>(new CommandDefinition(MessageSelect + "\n" + """
            WHERE session_id = @SessionId AND turn_id IS NULL
              AND kind IN ('queue', 'steer') AND delivery IN ('queued', 'dispatching', 'unknown')
            ORDER BY ordinal;
            """, new { SessionId = sessionId }, cancellationToken: ct)).ConfigureAwait(false)).AsList();
        if (rows.Count == 0)
        {
            return [];
        }

        var attachmentsByOrdinal = await _attachmentsRepository
            .LoadByMessageOrdinalsAsync(conn, tx: null, rows.Select(static r => r.Ordinal).ToArray(), ct)
            .ConfigureAwait(false);
        return rows.Select(r => r.ToMessage(attachmentsByOrdinal.GetValueOrDefault(r.Ordinal))).ToArray();
    }

    async Task<IReadOnlyList<string>> ISessionMessageStore.ListHandoffsAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT id FROM session_messages WHERE session_id = @SessionId AND kind IN ('queue', 'steer')
              AND (turn_id IS NOT NULL OR delivered_turn_id IS NOT NULL) ORDER BY ordinal DESC LIMIT 128;
            """, new { SessionId = sessionId }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    async Task<IReadOnlyList<(string TrackId, string SessionId)>> ISessionMessageStore.ListRecoverableQueuesAsync(CancellationToken ct)
    {
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<(string, string)>(new CommandDefinition("""
            SELECT DISTINCT s.track_id, s.id FROM session_messages m
            JOIN agent_sessions s ON s.id = m.session_id JOIN tracks t ON t.id = s.track_id
            WHERE m.delivery = 'queued' AND m.turn_id IS NULL AND t.closed_at_ms IS NULL AND s.archived_at_ms IS NULL
              AND NOT EXISTS (SELECT 1 FROM tasks tk WHERE tk.session_id = s.id AND (tk.kind <> 'flow_wake' OR tk.block_id <> 'flow:' || tk.task_key OR json_type(tk.context_json, '$.flowNode') IS NOT 'true') AND LOWER(tk.status) IN ('pending', 'running', 'verifying'));
            """, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    private sealed class SessionMessageRow
    {
        public long Ordinal { get; init; }
        public string Id { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public string AuthorId { get; init; } = string.Empty;
        public string RequestKey { get; init; } = string.Empty;
        public string RequestHash { get; init; } = string.Empty;
        public string? TargetTurnId { get; init; }
        public string? TurnId { get; init; }
        public string Content { get; init; } = string.Empty;
        public string Delivery { get; init; } = string.Empty;
        public long CreatedAtMs { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string? DeliveredTurnId { get; init; }

        public SessionMessage ToMessage(IReadOnlyList<StoredSessionAttachment>? attachments) =>
            new(Id, SessionId, AuthorId, RequestKey, RequestHash, TargetTurnId, TurnId, Content, Delivery, CreatedAtMs, Kind, DeliveredTurnId)
            {
                Attachments = attachments is { Count: > 0 } ? attachments : null
            };
    }
}
