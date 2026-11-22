using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

public sealed class SqliteAuthAdministration(string dbPath) : IAuthOwnerWriter
{
    internal const int AuthSchemaVersion = 26;

    public string DatabasePath => dbPath;

    public async Task<SystemAuthStatusDto> GetStatusAsync(long nowMs, CancellationToken ct)
    {
        if (!File.Exists(dbPath))
            return Unconfigured(0);

        await using var conn = await OpenAsync(dbPath, readOnly: true, ct).ConfigureAwait(false);
        var version = await SqliteMigrator.ReadVersionAsync(conn, ct).ConfigureAwait(false);
        if (version > SqliteMigrator.CurrentSchemaVersion)
            throw new SqliteFutureSchemaException(version, SqliteMigrator.CurrentSchemaVersion);

        return version < AuthSchemaVersion
            ? Unconfigured(version)
            : await ReadStatusAsync(conn, version, nowMs, ct).ConfigureAwait(false);
    }

    internal static async Task<SystemAuthStatusDto> ReadStatusAsync(SqliteConnection conn, int version, long nowMs, CancellationToken ct)
    {
        var owner = await conn.QuerySingleOrDefaultAsync<OwnerStatus>(new CommandDefinition("""
            SELECT u.username AS Username, u.display_name AS DisplayName, u.disabled_at_ms AS DisabledAtMs,
                   (SELECT count(*) FROM auth_sessions s WHERE s.user_id = u.id
                       AND s.revoked_at_ms IS NULL AND s.expires_at_ms > @Now AND u.disabled_at_ms IS NULL) AS ActiveSessions
            FROM auth_users u WHERE u.role = 'owner';
            """, new { Now = nowMs }, cancellationToken: ct)).ConfigureAwait(false);
        return owner is null
            ? Unconfigured(version)
            : new(version, true, owner.Username, owner.DisplayName, owner.DisabledAtMs is not null, owner.ActiveSessions);
    }

    private static SystemAuthStatusDto Unconfigured(int version) => new(version, false, null, null, false, 0);

    public Task<bool> CreateInitialOwnerAsync(string username, string displayName, string passwordHash, long nowMs, CancellationToken ct) =>
        WriteAsync("""
            INSERT INTO auth_users (id, username, display_name, password_hash, password_updated_at_ms, created_at_ms, updated_at_ms)
            VALUES (@Id, @Username, @DisplayName, @PasswordHash, @Now, @Now, @Now)
            ON CONFLICT DO NOTHING;
            """, new { Id = $"usr_{Guid.NewGuid():N}", Username = username, DisplayName = displayName, PasswordHash = passwordHash, Now = nowMs },
            revokeAt: null, ct);

    public Task<bool> RotateOwnerPasswordAsync(string passwordHash, long nowMs, CancellationToken ct) =>
        WriteAsync("""
            UPDATE auth_users SET password_hash = @PasswordHash, password_updated_at_ms = @Now,
                credential_version = credential_version + 1, session_generation = session_generation + 1, updated_at_ms = @Now
            WHERE role = 'owner';
            """, new { PasswordHash = passwordHash, Now = nowMs }, nowMs, ct);

    public Task<bool> RenameOwnerAsync(string username, string? displayName, long nowMs, CancellationToken ct) =>
        WriteAsync("""
            UPDATE auth_users SET username = @Username, display_name = COALESCE(@DisplayName, display_name),
                credential_version = credential_version + 1, session_generation = session_generation + 1, updated_at_ms = @Now
            WHERE role = 'owner';
            """, new { Username = username, DisplayName = displayName, Now = nowMs }, nowMs, ct);

    public Task<bool> SetOwnerDisabledAsync(bool disabled, long nowMs, CancellationToken ct) =>
        WriteAsync("""
            UPDATE auth_users SET disabled_at_ms = CASE WHEN @Disabled THEN COALESCE(disabled_at_ms, @Now) ELSE NULL END,
                credential_version = credential_version + 1, session_generation = session_generation + 1, updated_at_ms = @Now
            WHERE role = 'owner';
            """, new { Disabled = disabled, Now = nowMs }, nowMs, ct);

    public Task<bool> RevokeAllSessionsAsync(long nowMs, CancellationToken ct) =>
        WriteAsync("""
            UPDATE auth_users SET session_generation = session_generation + 1, updated_at_ms = @Now WHERE role = 'owner';
            """, new { Now = nowMs }, nowMs, ct);

    private async Task<bool> WriteAsync(string sql, object args, long? revokeAt, CancellationToken ct)
    {
        await using var conn = await OpenAsync(dbPath, readOnly: false, ct).ConfigureAwait(false);
        await using var tx = conn.BeginTransaction(deferred: false);
        var written = await conn.ExecuteAsync(new CommandDefinition(sql, args, tx, cancellationToken: ct)).ConfigureAwait(false);
        if (written != 0 && revokeAt is { } now)
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE auth_sessions SET revoked_at_ms = @Now WHERE revoked_at_ms IS NULL
                  AND user_id = (SELECT id FROM auth_users WHERE role = 'owner');
                """, new { Now = now }, tx, cancellationToken: ct)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return written != 0;
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool readOnly, CancellationToken ct)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false, DefaultTimeout = readOnly ? 2 : 30
        }.ToString());
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(new CommandDefinition(readOnly
                ? "PRAGMA query_only = ON; PRAGMA busy_timeout = 2000;"
                : "PRAGMA busy_timeout = 30000; PRAGMA foreign_keys = ON;", cancellationToken: ct)).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed record OwnerStatus(string Username, string DisplayName, long? DisabledAtMs, long ActiveSessions);
}
