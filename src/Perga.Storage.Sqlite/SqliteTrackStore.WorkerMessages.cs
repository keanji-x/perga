using Dapper;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore : IWorkerMessageStore, IWorkerSettingsStore
{
    private const string WorkerMessageSelect = """
        SELECT id AS Id, session_id AS SessionId, attempt_id AS AttemptId, author_id AS AuthorId,
               request_key AS RequestKey, request_hash AS RequestHash, target_turn_id AS TargetTurnId,
               result_turn_id AS ResultTurnId, content AS Content, delivery AS Delivery, created_at_ms AS CreatedAtMs
        FROM worker_messages WHERE session_id = @SessionId
        """;

    public IWorkerMessageStore WorkerMessages => this;
    public IWorkerSettingsStore WorkerSettings => this;

    public async Task<StoredWorkerDispatch?> FindWorkerDispatchAsync(WorkerDispatchIntent dispatch, CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<StoredWorkerDispatch>(new CommandDefinition("""
            SELECT request_hash AS RequestHash, resource_id AS TaskId FROM creation_receipts
            WHERE author_id = @AuthorId AND operation = 'worker-dispatch' AND scope_id = @TrackId AND request_key = @RequestKey;
            """, dispatch, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListPendingWorkerDispatchTracksAsync(CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT t.track_id FROM tasks t JOIN creation_receipts r ON r.resource_id = t.id
            JOIN tracks tr ON tr.id = t.track_id
            WHERE r.operation = 'worker-dispatch' AND t.ready = 1 AND t.status = 'pending' AND tr.closed_at_ms IS NULL;
            """, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

    Task<WorkerMessage?> IWorkerMessageStore.FindAsync(string sessionId, string authorId, string requestKey, CancellationToken ct) =>
        ReadWorkerMessageAsync(" AND author_id = @AuthorId AND request_key = @RequestKey",
            new { SessionId = sessionId, AuthorId = authorId, RequestKey = requestKey }, ct);

    Task<WorkerMessage?> IWorkerMessageStore.NextAsync(string sessionId, CancellationToken ct) =>
        ReadWorkerMessageAsync(" AND delivery = 'queued' ORDER BY created_at_ms, rowid LIMIT 1",
            new { SessionId = sessionId }, ct);

    private async Task<WorkerMessage?> ReadWorkerMessageAsync(string clause, object parameters, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<WorkerMessage>(new CommandDefinition(
            WorkerMessageSelect + clause, parameters, cancellationToken: ct)).ConfigureAwait(false);
    }

    Task IWorkerMessageStore.InsertAsync(WorkerMessage message, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) => await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO worker_messages (id, session_id, attempt_id, author_id, request_key, request_hash,
                                         target_turn_id, result_turn_id, content, delivery, created_at_ms)
            VALUES (@Id, @SessionId, @AttemptId, @AuthorId, @RequestKey, @RequestHash,
                    @TargetTurnId, @ResultTurnId, @Content, @Delivery, @CreatedAtMs);
            """, message, tx, cancellationToken: innerCt)).ConfigureAwait(false), ct);

    Task IWorkerMessageStore.UpdateAsync(WorkerMessage message, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) => await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE worker_messages SET result_turn_id = @ResultTurnId, delivery = @Delivery
            WHERE id = @Id AND (delivery = 'dispatching' OR (delivery = 'queued' AND @Delivery = 'dispatching'));
            """, message, tx, cancellationToken: innerCt)).ConfigureAwait(false), ct);

    async Task<IReadOnlyList<WorkerMessage>> IWorkerMessageStore.RecentAsync(string sessionId, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var messages = await conn.QueryAsync<WorkerMessage>(new CommandDefinition(
            WorkerMessageSelect + " ORDER BY created_at_ms DESC, rowid DESC LIMIT 12",
            new { SessionId = sessionId }, cancellationToken: ct)).ConfigureAwait(false);
        return messages.AsList();
    }

    async Task<WorkerSettings?> IWorkerSettingsStore.ReadAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<WorkerSettings>(new CommandDefinition("""
            SELECT version AS Version, model AS Model, reasoning_effort AS ReasoningEffort, approval_mode AS ApprovalMode
            FROM worker_settings WHERE session_id = @SessionId;
            """, new { SessionId = sessionId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    Task IWorkerSettingsStore.WriteAsync(string sessionId, WorkerSettings settings, string authorId, EventEnvelopeDto evt, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO worker_settings (session_id, version, model, reasoning_effort, approval_mode, author_id)
            VALUES (@SessionId, @Version, @Model, @ReasoningEffort, @ApprovalMode, @AuthorId)
            ON CONFLICT(session_id) DO UPDATE SET version = excluded.version, model = excluded.model,
                reasoning_effort = excluded.reasoning_effort, approval_mode = excluded.approval_mode, author_id = excluded.author_id;
            """, new { SessionId = sessionId, settings.Version, settings.Model, settings.ReasoningEffort, settings.ApprovalMode, AuthorId = authorId },
                tx, cancellationToken: innerCt)).ConfigureAwait(false);
            await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
        }, ct);
}
