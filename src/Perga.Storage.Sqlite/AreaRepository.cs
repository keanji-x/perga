using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;

namespace Perga.Storage.Sqlite;

/// <summary>Area queries, upserts and cascading deletion.</summary>
internal sealed class AreaRepository
{
    private readonly SqliteStoreContext _context;

    internal AreaRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<AreaDto>> ListAreasAsync(CancellationToken ct, bool includeSystem)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<AreaRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       name AS Name,
                       color AS Color,
                       default_cwd AS DefaultCwd,
                       folders_json AS FoldersJson,
                       sort_order AS SortOrder,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs,
                       execution_target_id AS ExecutionTargetId
                FROM areas
                WHERE @IncludeSystem OR is_system = 0
                ORDER BY sort_order ASC, created_at_ms ASC, id ASC;
                """,
                new { IncludeSystem = includeSystem },
                cancellationToken: ct)).ConfigureAwait(false);

        return rows.Select(MapArea).ToList();
    }

    public async Task<AreaDto?> GetAreaAsync(string areaId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        _context.EnsureInitialized();

        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<AreaRow>(
            new CommandDefinition(
                """
                SELECT id AS Id,
                       name AS Name,
                       color AS Color,
                       default_cwd AS DefaultCwd,
                       folders_json AS FoldersJson,
                       sort_order AS SortOrder,
                       created_at_ms AS CreatedAtMs,
                       updated_at_ms AS UpdatedAtMs,
                       execution_target_id AS ExecutionTargetId
                FROM areas
                WHERE id = @AreaId;
                """,
                new { AreaId = areaId },
                cancellationToken: ct)).ConfigureAwait(false);

        return row is null ? null : MapArea(row);
    }

    public Task<AreaDto> UpsertAreaAsync(AreaDto area, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(area);
        _context.EnsureInitialized();

        return _context.Writer.EnqueueAsync<AreaDto>(
            async (conn, tx, innerCt) =>
            {
                await UpsertAreaInternalAsync(conn, tx, area, innerCt).ConfigureAwait(false);
                var persisted = await conn.QuerySingleAsync<AreaRow>(
                    new CommandDefinition(
                        """
                        SELECT id AS Id,
                               name AS Name,
                               color AS Color,
                               default_cwd AS DefaultCwd,
                               folders_json AS FoldersJson,
                               sort_order AS SortOrder,
                               created_at_ms AS CreatedAtMs,
                               updated_at_ms AS UpdatedAtMs,
                               execution_target_id AS ExecutionTargetId
                        FROM areas
                        WHERE id = @Id;
                        """,
                        new { area.Id },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                return MapArea(persisted);
            },
            ct);
    }

    internal static async Task UpsertAreaInternalAsync(SqliteConnection conn, SqliteTransaction tx, AreaDto area, CancellationToken ct)
    {
        var foldersJson = SqliteJsonUpcaster.SerializeStringList(area.Folders);
        var executionTargetId = string.IsNullOrWhiteSpace(area.ExecutionTargetId) ? "local" : area.ExecutionTargetId.Trim();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO areas (id, name, color, default_cwd, folders_json, sort_order, created_at_ms, updated_at_ms, execution_target_id)
                VALUES (@Id, @Name, @Color, @DefaultCwd, @FoldersJson, @SortOrder, @CreatedAtMs, @UpdatedAtMs, @ExecutionTargetId)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    color = excluded.color,
                    default_cwd = excluded.default_cwd,
                    folders_json = excluded.folders_json,
                    sort_order = excluded.sort_order,
                    execution_target_id = excluded.execution_target_id,
                    updated_at_ms = excluded.updated_at_ms;
                """,
                new
                {
                    area.Id,
                    area.Name,
                    area.Color,
                    area.DefaultCwd,
                    FoldersJson = foldersJson,
                    area.SortOrder,
                    area.CreatedAtMs,
                    area.UpdatedAtMs,
                    ExecutionTargetId = executionTargetId
                },
                transaction: tx,
                cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task DeleteAreaAsync(string areaId, CancellationToken ct, bool requireEmpty = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        _context.EnsureInitialized();

        var cascadedTrackIds = await _context.Writer.EnqueueAsync<IReadOnlyList<string>>(
            async (conn, tx, innerCt) =>
            {
                var trackIds = (await conn.QueryAsync<string>(
                    new CommandDefinition(
                        "SELECT id FROM tracks WHERE area_id = @AreaId;",
                        new { AreaId = areaId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false)).ToList();

                if (requireEmpty && trackIds.Count != 0)
                    throw Perga.Kernel.KernelException.Conflict("This Area has Tracks that must be removed before deletion.");

                await conn.ExecuteAsync(
                    new CommandDefinition(
                        "DELETE FROM areas WHERE id = @AreaId;",
                        new { AreaId = areaId },
                        transaction: tx,
                        cancellationToken: innerCt)).ConfigureAwait(false);

                return trackIds;
            },
            ct).ConfigureAwait(false);

        foreach (var trackId in cascadedTrackIds)
            _context.DeleteTrackFiles(trackId);
    }

    private static AreaDto MapArea(AreaRow r) =>
        new(
            Id: r.Id,
            Name: r.Name,
            Color: r.Color,
            DefaultCwd: r.DefaultCwd,
            Folders: SqliteJsonUpcaster.DeserializeStringList(r.FoldersJson),
            SortOrder: r.SortOrder,
            CreatedAtMs: r.CreatedAtMs,
            UpdatedAtMs: r.UpdatedAtMs,
            ExecutionTargetId: string.IsNullOrWhiteSpace(r.ExecutionTargetId) ? "local" : r.ExecutionTargetId);

    private sealed class AreaRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Color { get; init; } = string.Empty;
        public string? DefaultCwd { get; init; }
        public string FoldersJson { get; init; } = "[]";
        public double SortOrder { get; init; }
        public long CreatedAtMs { get; init; }
        public long UpdatedAtMs { get; init; }
        public string ExecutionTargetId { get; init; } = "local";
    }
}
