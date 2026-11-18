using Dapper;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore : IWorkerSettingsStore
{
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
