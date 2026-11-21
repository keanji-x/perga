using Dapper;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore
{
    public async Task<StoredWorkerDispatch?> FindWorkerDispatchAsync(WorkerDispatchIntent dispatch, CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<StoredWorkerDispatch>(new CommandDefinition("""
            SELECT r.request_hash AS RequestHash, s.bound_task_id AS TaskId FROM creation_receipts r
            LEFT JOIN agent_sessions s ON s.id=r.resource_id
            WHERE author_id = @AuthorId AND operation = 'session-create' AND scope_id = @TrackId AND request_key = @RequestKey;
            """, dispatch, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListPendingWorkerDispatchTracksAsync(CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT t.track_id FROM tasks t
            JOIN tracks tr ON tr.id = t.track_id
            WHERE t.ready = 1 AND t.status = 'pending' AND tr.closed_at_ms IS NULL;
            """, cancellationToken: ct)).ConfigureAwait(false);
        return rows.AsList();
    }

}
