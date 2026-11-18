using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore
{
    public async Task<string?> FindCreationAsync(ResourceCreationKey key, CancellationToken ct)
    {
        EnsureInitialized();
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await FindCreationInternalAsync(conn, null, key, ct).ConfigureAwait(false);
    }

    public Task<string> CreateAreaAsync(AreaDto area, ResourceCreationKey? key, CancellationToken ct)
    {
        EnsureInitialized();
        return _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            var replay = await FindCreationInternalAsync(conn, tx, key, innerCt).ConfigureAwait(false);
            if (replay is not null)
                return replay;
            await UpsertAreaInternalAsync(conn, tx, area, innerCt).ConfigureAwait(false);
            await InsertResourceReceiptAsync(conn, tx, key, area.Id, area.CreatedAtMs, null, innerCt).ConfigureAwait(false);
            return area.Id;
        }, ct);
    }

    private static async Task<string?> FindCreationInternalAsync(
        SqliteConnection conn, SqliteTransaction? tx, ResourceCreationKey? key, CancellationToken ct)
    {
        if (key is null)
            return null;
        var row = await conn.QuerySingleOrDefaultAsync<ResourceCreationRow>(new CommandDefinition(
            """
            SELECT resource_id AS Id, request_hash AS Hash FROM creation_receipts
            WHERE author_id = @Author AND operation = @Operation AND scope_id = @ScopeId AND request_key = @Key;
            """, new { Author = OwnerAuthor, key.Operation, key.ScopeId, key.Key }, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (row is not null && row.Hash != key.Hash)
            throw new KernelException(KernelErrorCode.IdempotencyKeyReused, "This request key was already used with different input.");
        return row?.Id;
    }

    private sealed record ResourceCreationRow(string Id, string Hash);

    private static Task InsertResourceReceiptAsync(SqliteConnection conn, SqliteTransaction tx,
        ResourceCreationKey? key, string id, long createdAt, string? inputId, CancellationToken ct) =>
        key is null ? Task.CompletedTask : conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO creation_receipts (author_id, operation, scope_id, request_key, request_hash, resource_id, first_input_id, created_at_ms)
            VALUES (@Author, @Operation, @ScopeId, @Key, @Hash, @Id, @InputId, @CreatedAt);
            """, new { Author = OwnerAuthor, key.Operation, key.ScopeId, key.Key, key.Hash, Id = id, InputId = inputId, CreatedAt = createdAt },
            tx, cancellationToken: ct));

    public async Task<WorkspacePreparation?> GetWorkspacePreparationAsync(string trackId, CancellationToken ct)
    {
        await using var conn = await OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var json = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT intent_json FROM track_workspace_preparations WHERE track_id = @TrackId;",
            new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<WorkspacePreparation>(json);
    }

    public Task<IReadOnlyList<string>> RecoverWorkspacePreparationsAsync(CancellationToken ct) =>
        _writer.EnqueueAsync<IReadOnlyList<string>>(async (conn, tx, innerCt) =>
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE track_workspace_preparations SET intent_json = json_set(intent_json, '$.Phase', @Unknown)
                WHERE json_extract(intent_json, '$.Phase') IN (@Provisioning, @Initializing);
                """, new { Unknown = WorkspacePreparationPhase.Unknown, Provisioning = WorkspacePreparationPhase.Provisioning,
                    Initializing = WorkspacePreparationPhase.Initializing }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE track_workspace_preparations SET intent_json = json_set(intent_json, '$.Phase', @Provisioned)
                WHERE json_extract(intent_json, '$.Phase') = @Mounting;
                """, new { Provisioned = WorkspacePreparationPhase.Provisioned, Mounting = WorkspacePreparationPhase.Mounting },
                tx, cancellationToken: innerCt)).ConfigureAwait(false);
            return (await conn.QueryAsync<string>(new CommandDefinition(
                """
                SELECT p.track_id FROM track_workspace_preparations p JOIN tracks t ON t.id = p.track_id
                WHERE t.closed_at_ms IS NULL AND (json_extract(p.intent_json, '$.Phase') IN (@Pending, @Provisioned)
                    OR (json_extract(p.intent_json, '$.Phase') = @Ready AND EXISTS (
                        SELECT 1 FROM tasks WHERE track_id = t.id AND ready = 1 AND status = 'pending'
                    )));
                """, new { Pending = WorkspacePreparationPhase.Pending, Provisioned = WorkspacePreparationPhase.Provisioned, Ready = WorkspacePreparationPhase.Ready },
                tx, cancellationToken: innerCt)).ConfigureAwait(false)).ToArray();
        }, ct);

    public Task<bool> TryClaimWorkspacePreparationAsync(string trackId, WorkspacePreparationPhase expected, WorkspacePreparation claimed, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE track_workspace_preparations SET intent_json = @Intent
                WHERE track_id = @TrackId AND json_extract(intent_json, '$.Phase') = @Expected
                  AND EXISTS (SELECT 1 FROM tracks WHERE id = @TrackId AND closed_at_ms IS NULL);
                """, new { TrackId = trackId, Intent = JsonSerializer.Serialize(claimed), Expected = expected },
                tx, cancellationToken: innerCt)).ConfigureAwait(false) == 1, ct);

    public Task SaveWorkspacePreparationAsync(string trackId, WorkspacePreparation preparation, TrackDto? readyTrack, EventEnvelopeDto? evt, CancellationToken ct) =>
        _writer.EnqueueAsync(async (conn, tx, innerCt) =>
        {
            var updated = await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE track_workspace_preparations SET intent_json = @Intent WHERE track_id = @TrackId;",
                new { TrackId = trackId, Intent = JsonSerializer.Serialize(preparation) }, tx, cancellationToken: innerCt)).ConfigureAwait(false);
            if (updated != 1)
                throw KernelException.NotFound($"Track '{trackId}' not found.");
            if (readyTrack is not null)
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE tracks SET cwd = @Cwd, workspace_spec_json = @Spec WHERE id = @TrackId;",
                    new { TrackId = trackId, readyTrack.Cwd, Spec = readyTrack.WorkspaceSpec.GetRawText() },
                    tx, cancellationToken: innerCt)).ConfigureAwait(false);
            if (evt is not null)
                await InsertEventInternalAsync(conn, tx, evt, innerCt).ConfigureAwait(false);
        }, ct);
}
