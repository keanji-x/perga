using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed partial class SqliteTrackStore
{
    public async Task<string?> GetLatestMemoryContextModeAsync(string sessionId, int epoch, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT json_extract(item.payload_json, '$.mode')
            FROM session_items item
            JOIN session_turns turn ON turn.session_id = item.session_id AND turn.id = item.turn_id
            WHERE item.session_id = @SessionId AND turn.epoch = @Epoch AND item.kind = 'system'
                AND json_extract(item.payload_json, '$.kind') = 'memory_context'
            ORDER BY item.id DESC
            LIMIT 1;
            """, new { SessionId = sessionId, Epoch = epoch }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<string?> GetMemoryTrackIdAsync(string scopeKey, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await ReadMemoryTrackIdAsync(conn, null, scopeKey, ct).ConfigureAwait(false);
    }

    internal static Task<string?> ReadMemoryTrackIdAsync(SqliteConnection conn, SqliteTransaction? tx,
        string scopeKey, CancellationToken ct) =>
        conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT track_id FROM memory_tracks WHERE scope_key = @ScopeKey;",
            new { ScopeKey = scopeKey }, tx, cancellationToken: ct));

    public async Task<MemoryTrackBinding?> FindMemoryBindingByTrackIdAsync(string trackId, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<MemoryTrackBinding>(new CommandDefinition(
            "SELECT scope_key AS ScopeKey, area_id AS AreaId, track_id AS TrackId FROM memory_tracks WHERE track_id = @TrackId;",
            new { TrackId = trackId }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> IsUserAreaAsync(string areaId, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await IsUserAreaAsync(conn, null, areaId, ct).ConfigureAwait(false);
    }

    internal static Task<bool> IsUserAreaAsync(SqliteConnection conn, SqliteTransaction? tx, string areaId, CancellationToken ct) =>
        conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM areas WHERE id = @AreaId AND is_system = 0);",
            new { AreaId = areaId }, tx, cancellationToken: ct));

    public Task DeleteAreaWithMemoryTrackAsync(string areaId, string? expectedMemoryTrackId, CancellationToken ct) =>
        _areaRepository.DeleteAreaAsync(areaId, ct, requireEmpty: true, checkMemoryTrack: true, expectedMemoryTrackId);
}
