using Dapper;
using Perga.Contracts;
using Perga.Kernel;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore
{
    private sealed record ExecutionTargetRow(
        string Id,
        string Label,
        string Kind,
        string Host,
        long Port,
        string SshUser,
        string? IdentityFile,
        string WorkspacesRoot,
        long CreatedAtMs,
        long UpdatedAtMs);

    private static StoredExecutionTarget MapExecutionTarget(ExecutionTargetRow row) =>
        new(
            Id: row.Id,
            Label: row.Label,
            Kind: row.Kind,
            Host: row.Host,
            Port: (int)row.Port,
            User: row.SshUser,
            IdentityFile: row.IdentityFile,
            WorkspacesRoot: row.WorkspacesRoot,
            CreatedAtMs: row.CreatedAtMs,
            UpdatedAtMs: row.UpdatedAtMs);

    public async Task<IReadOnlyList<StoredExecutionTarget>> ListExecutionTargetsAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<ExecutionTargetRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       label AS Label,
                       kind AS Kind,
                       host AS Host,
                       port AS Port,
                       ssh_user AS SshUser,
                       identity_file AS IdentityFile,
                       workspaces_root AS WorkspacesRoot,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs
                FROM execution_targets
                ORDER BY created_at_ms ASC, id ASC;
                """,
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapExecutionTarget).ToList();
    }

    public Task<StoredExecutionTarget> UpsertExecutionTargetAsync(StoredExecutionTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        EnsureInitialized();

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO execution_targets (
                            id, label, kind, host, port, ssh_user, identity_file, workspaces_root, created_at_ms, updated_at_ms
                        ) VALUES (
                            @Id, @Label, @Kind, @Host, @Port, @User, @IdentityFile, @WorkspacesRoot, @CreatedAtMs, @UpdatedAtMs
                        )
                        ON CONFLICT(id) DO UPDATE SET
                            label = excluded.label,
                            kind = excluded.kind,
                            host = excluded.host,
                            port = excluded.port,
                            ssh_user = excluded.ssh_user,
                            identity_file = excluded.identity_file,
                            workspaces_root = excluded.workspaces_root,
                            updated_at_ms = excluded.updated_at_ms;
                        """,
                        target,
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                var persisted = await conn.QuerySingleAsync<ExecutionTargetRow>(
                    new CommandDefinition(
                        """
                        SELECT id AS Id,
                               label AS Label,
                               kind AS Kind,
                               host AS Host,
                               port AS Port,
                               ssh_user AS SshUser,
                               identity_file AS IdentityFile,
                               workspaces_root AS WorkspacesRoot,
                               created_at_ms AS CreatedAtMs,
                               updated_at_ms AS UpdatedAtMs
                        FROM execution_targets
                        WHERE id = @Id;
                        """,
                        new { target.Id },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                return MapExecutionTarget(persisted);
            },
            ct);
    }

    public Task<bool> DeleteExecutionTargetAsync(string targetId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        EnsureInitialized();

        return _writer.EnqueueAsync(
            async (conn, tx, innerCt) =>
            {
                var deleted = await conn.ExecuteAsync(
                    new CommandDefinition(
                        """
                        DELETE FROM execution_targets WHERE id = @Id
                        AND NOT EXISTS (SELECT 1 FROM areas WHERE execution_target_id = @Id)
                        AND NOT EXISTS (SELECT 1 FROM tracks WHERE execution_target_id = @Id);
                        """,
                        new { Id = targetId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);
                if (deleted == 0 && await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT EXISTS (SELECT 1 FROM execution_targets WHERE id = @Id);",
                    new { Id = targetId }, transaction: tx, cancellationToken: innerCt)).ConfigureAwait(false))
                {
                    throw KernelException.Conflict($"Execution target '{targetId}' is still in use by an Area or Track.");
                }
                return deleted > 0;
            },
            ct);
    }
}
