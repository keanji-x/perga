using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Sqlite;

/// <summary>Owner account and browser session persistence.</summary>
internal sealed class AuthRepository
{
    private const string ValidOwnerSession = """
        FROM auth_sessions s
        JOIN auth_users u ON u.id = s.user_id
        WHERE u.role = 'owner' AND u.disabled_at_ms IS NULL
          AND s.revoked_at_ms IS NULL AND s.expires_at_ms > @Now
        """;

    private const string SessionIdentityColumns = """
        s.id AS SessionId, u.id AS UserId, u.username AS Username, u.display_name AS DisplayName,
        u.role AS Role, s.expires_at_ms AS ExpiresAtMs
        """;

    private readonly SqliteStoreContext _context;

    internal AuthRepository(SqliteStoreContext context)
    {
        _context = context;
    }

    public async Task<AuthUserRecord?> GetOwnerAsync(CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<AuthUserRecord>(new CommandDefinition("""
            SELECT id AS Id, username AS Username, display_name AS DisplayName, role AS Role,
                   password_hash AS PasswordHash, credential_version AS CredentialVersion,
                   session_generation AS SessionGeneration, disabled_at_ms AS DisabledAtMs
            FROM auth_users WHERE role = 'owner';
            """, cancellationToken: ct)).ConfigureAwait(false);
    }

    public Task<SessionIdentityDto?> TryCreateSessionIfVersionMatchesAsync(
        AuthUserRecord owner, NewAuthSession session, CancellationToken ct) =>
        _context.Writer.EnqueueAsync<SessionIdentityDto?>(async (conn, tx, token) =>
        {
            var written = await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO auth_sessions (id, user_id, token_hash, created_at_ms, last_seen_at_ms,
                                           expires_at_ms, user_agent, created_ip)
                SELECT @Id, id, @TokenHash, @CreatedAtMs, @CreatedAtMs, @ExpiresAtMs, @UserAgent, @CreatedIp
                FROM auth_users
                WHERE id = @UserId AND role = 'owner' AND disabled_at_ms IS NULL
                  AND credential_version = @CredentialVersion AND session_generation = @SessionGeneration;
                """, new
                {
                    session.Id, UserId = owner.Id, owner.CredentialVersion, owner.SessionGeneration,
                    session.TokenHash, session.CreatedAtMs, session.ExpiresAtMs, session.UserAgent, session.CreatedIp
                }, tx, cancellationToken: token)).ConfigureAwait(false);
            return written == 0 ? null : await conn.QuerySingleAsync<SessionIdentityDto>(new CommandDefinition(
                $"SELECT {SessionIdentityColumns} {ValidOwnerSession} AND s.id = @Id;",
                new { session.Id, Now = session.CreatedAtMs }, tx, cancellationToken: token)).ConfigureAwait(false);
        }, ct);

    public Task<bool> UpgradePasswordHashIfVersionMatchesAsync(AuthUserRecord owner, string hash, long nowMs, CancellationToken ct) =>
        _context.Writer.EnqueueAsync<bool>(async (conn, tx, token) =>
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE auth_users SET password_hash = @Hash, updated_at_ms = @Now
                WHERE id = @Id AND role = 'owner' AND disabled_at_ms IS NULL
                  AND credential_version = @CredentialVersion AND password_hash = @PasswordHash;
                """, new { owner.Id, owner.CredentialVersion, owner.PasswordHash, Hash = hash, Now = nowMs },
                tx, cancellationToken: token)).ConfigureAwait(false) == 1, ct);

    public async Task<SessionIdentityDto?> GetValidOwnerSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.QuerySingleOrDefaultAsync<SessionIdentityDto>(new CommandDefinition(
            $"SELECT {SessionIdentityColumns} {ValidOwnerSession} AND s.token_hash = @TokenHash;",
            new { TokenHash = tokenHash, Now = nowMs }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> IsOwnerSessionValidAsync(string id, long nowMs, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"SELECT EXISTS(SELECT 1 {ValidOwnerSession} AND s.id = @Id);",
            new { Id = id, Now = nowMs }, cancellationToken: ct)).ConfigureAwait(false);
    }

    public Task TouchSessionLastSeenIfStaleAsync(string id, long nowMs, CancellationToken ct) =>
        _context.Writer.EnqueueAsync<int>(async (conn, tx, token) => await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE auth_sessions SET last_seen_at_ms = @Now
            WHERE id = @Id AND revoked_at_ms IS NULL AND expires_at_ms > @Now AND last_seen_at_ms <= @Cutoff;
            """, new { Id = id, Now = nowMs, Cutoff = nowMs - (long)TimeSpan.FromMinutes(15).TotalMilliseconds },
            tx, cancellationToken: token)), ct);

    public Task RevokeSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct) =>
        _context.Writer.EnqueueAsync<int>(async (conn, tx, token) => await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE auth_sessions SET revoked_at_ms = @Now WHERE token_hash = @TokenHash AND revoked_at_ms IS NULL;
            """, new { TokenHash = tokenHash, Now = nowMs }, tx, cancellationToken: token)), ct);

    public async Task<SystemAuthStatusDto> GetAuthStatusAsync(long nowMs, CancellationToken ct)
    {
        _context.EnsureInitialized();
        await using var conn = await _context.OpenReadConnectionAsync(ct).ConfigureAwait(false);
        return await SqliteAuthAdministration.ReadStatusAsync(conn, SqliteMigrator.CurrentSchemaVersion, nowMs, ct)
            .ConfigureAwait(false);
    }
}
