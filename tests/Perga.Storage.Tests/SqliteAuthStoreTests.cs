using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class SqliteAuthStoreTests
{
    private const long Now = 1_800_000_000_000;
    private const long Day = 86_400_000;

    [Fact]
    public async Task SingleOwnerAndCasFencePasswordChangesAndRevocation()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var admin = new SqliteAuthAdministration(h.DbPath);
        var writes = await Task.WhenAll(
            Task.Run(() => admin.CreateInitialOwnerAsync("first", "First", "hash", Now, default)),
            Task.Run(() => admin.CreateInitialOwnerAsync("second", "Second", "hash", Now, default)));
        Assert.Single(writes, written => written);

        var owner = (await h.Store.GetOwnerAsync(default))!;
        var existing = NewSession(1, Now, Now + Day);
        Assert.NotNull(await h.Store.TryCreateSessionIfVersionMatchesAsync(owner, existing, default));

        Assert.True(await h.Store.UpgradePasswordHashIfVersionMatchesAsync(owner, "stronger", Now + 1, default));
        var upgraded = (await h.Store.GetOwnerAsync(default))!;
        Assert.Equal(owner.CredentialVersion, upgraded.CredentialVersion);
        Assert.Equal(owner.SessionGeneration, upgraded.SessionGeneration);
        Assert.Equal("stronger", upgraded.PasswordHash);
        Assert.True(await h.Store.IsOwnerSessionValidAsync(existing.Id, Now + 1, default));
        Assert.False(await h.Store.UpgradePasswordHashIfVersionMatchesAsync(owner, "stale", Now + 1, default));

        Assert.True(await admin.RevokeAllSessionsAsync(Now + 2, default));
        var revoked = (await h.Store.GetOwnerAsync(default))!;
        Assert.Equal(upgraded.CredentialVersion, revoked.CredentialVersion);
        Assert.Equal(upgraded.SessionGeneration + 1, revoked.SessionGeneration);
        Assert.False(await h.Store.IsOwnerSessionValidAsync(existing.Id, Now + 2, default));
        Assert.Null(await h.Store.TryCreateSessionIfVersionMatchesAsync(upgraded, NewSession(2, Now, Now + Day), default));

        Assert.True(await admin.RotateOwnerPasswordAsync("new-password", Now + 3, default));
        var rotated = (await h.Store.GetOwnerAsync(default))!;
        Assert.Equal(revoked.CredentialVersion + 1, rotated.CredentialVersion);
        Assert.Null(await h.Store.TryCreateSessionIfVersionMatchesAsync(
            rotated with { CredentialVersion = revoked.CredentialVersion }, NewSession(3, Now, Now + Day), default));
        Assert.False(await h.Store.UpgradePasswordHashIfVersionMatchesAsync(revoked, "stale", Now + 3, default));

        Assert.True(await admin.SetOwnerDisabledAsync(true, Now + 4, default));
        var disabled = (await h.Store.GetOwnerAsync(default))!;
        Assert.Null(await h.Store.TryCreateSessionIfVersionMatchesAsync(disabled, NewSession(4, Now, Now + Day), default));
        Assert.True(await admin.SetOwnerDisabledAsync(false, Now + 5, default));
        Assert.True(await admin.RenameOwnerAsync("renamed", "Renamed", Now + 6, default));
        Assert.Equal("renamed", (await admin.GetStatusAsync(Now + 6, default)).Username);
    }

    [Fact]
    public async Task InitialOwnerRollsBackIfSessionInsertFailsAndCanBeRetried()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var owner = new NewInitialOwner("owner", "Owner", "hash");
        var session = NewSession(1, Now, Now + Day);
        Assert.Equal(AuthSetupState.NeedsWebSetup, await h.Store.GetSetupStateAsync(default));
        await Assert.ThrowsAsync<SqliteException>(() => h.Store.TryCreateInitialOwnerWithSessionAsync(
            owner, session with { TokenHash = "invalid-hash" }, allowExistingBusinessData: false, default));

        Assert.Null(await h.Store.GetOwnerAsync(default));
        Assert.Equal(AuthSetupState.NeedsWebSetup, await h.Store.GetSetupStateAsync(default));
        var result = await h.Store.TryCreateInitialOwnerWithSessionAsync(owner, session, allowExistingBusinessData: false, default);
        Assert.Equal(InitialOwnerSetupOutcome.Created, result.Outcome);
        Assert.Equal(AuthSessionMode.Owner, result.Identity!.Mode);
        Assert.Equal((await h.Store.GetOwnerAsync(default))!.Id, result.Identity.UserId);
        Assert.Equal(result.Identity, await h.Store.GetValidOwnerSessionByTokenHashAsync(session.TokenHash, Now, default));
    }

    [Fact]
    public async Task SessionLookupTouchAndMaintenanceEnforceLifetimeAndRetention()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var admin = new SqliteAuthAdministration(h.DbPath);
        Assert.True(await admin.CreateInitialOwnerAsync("owner", "Owner", "hash", Now - 10 * Day, default));
        var owner = (await h.Store.GetOwnerAsync(default))!;
        var active = NewSession(1, Now - 1000, Now + Day);
        var recent = NewSession(2, Now - 2 * Day, Now);
        var revoked = NewSession(3, Now - 9 * Day, Now + Day);
        foreach (var session in new[] { active, recent, revoked })
            Assert.NotNull(await h.Store.TryCreateSessionIfVersionMatchesAsync(owner, session, default));
        for (var index = 4; index < 105; index++)
            Assert.NotNull(await h.Store.TryCreateSessionIfVersionMatchesAsync(owner,
                NewSession(index, Now - 9 * Day, Now - 8 * Day), default));

        Assert.Equal(active.Id, (await h.Store.GetValidOwnerSessionByTokenHashAsync(active.TokenHash, Now, default))!.SessionId);
        Assert.Null(await h.Store.GetValidOwnerSessionByTokenHashAsync(recent.TokenHash, Now, default));
        await h.Store.RevokeSessionByTokenHashAsync(revoked.TokenHash, Now - 8 * Day, default);
        Assert.Null(await h.Store.GetValidOwnerSessionByTokenHashAsync(revoked.TokenHash, Now, default));
        Assert.Equal(1, (await h.Store.GetAuthStatusAsync(Now, default)).ActiveSessions);

        using var db = h.OpenRawInspectConnection();
        async Task<long> LastSeen() => await db.ExecuteScalarAsync<long>(
            "SELECT last_seen_at_ms FROM auth_sessions WHERE id = @Id;", new { active.Id });
        await h.Store.TouchSessionLastSeenIfStaleAsync(active.Id, Now, default);
        Assert.Equal(active.CreatedAtMs, await LastSeen());
        await h.Store.TouchSessionLastSeenIfStaleAsync(active.Id, Now + 900_000, default);
        Assert.Equal(Now + 900_000, await LastSeen());

        Assert.Equal(102, await h.Store.PruneAuthSessionsAsync(Now, default));
        Assert.Equal(new[] { active.Id, recent.Id }, await db.QueryAsync<string>("SELECT id FROM auth_sessions ORDER BY id;"));
    }

    [Fact]
    public async Task MigrationRequiresOptInSerializesUpgradesAndRejectsFutureSchema()
    {
        var dir = Directory.CreateTempSubdirectory("perga-auth-migration-");
        try
        {
            var path = Path.Combine(dir.FullName, "perga.db");
            var migrator = new SqliteMigrator();
            await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.EnsureMigratedAsync(path, false));
            Assert.Empty(dir.EnumerateFiles());
            await new SqliteMigrator(migrator.Migrations.Take(25).ToArray()).MigrateAsync(path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => migrator.EnsureMigratedAsync(path, false));
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => migrator.EnsureMigratedAsync(path, true))));
            await migrator.EnsureMigratedAsync(path, false);
            Assert.True(File.Exists(path + ".bak-v25"));
            Assert.True(File.Exists(path + ".migrate.lock"));

            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await db.OpenAsync();
            Assert.Equal(2, await db.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('auth_users', 'auth_sessions');"));
            var nextVersion = SqliteMigrator.CurrentSchemaVersion + 1;
            await db.ExecuteAsync("""
                INSERT INTO schema_migrations (version, name, checksum_sha256, applied_at_ms)
                VALUES (@Version, @Name, lower(hex(zeroblob(32))), 0)
                """, new { Version = nextVersion, Name = $"{nextVersion:D4}_future.sql" });
            foreach (var allow in new[] { false, true })
            {
                var error = await Assert.ThrowsAsync<SqliteFutureSchemaException>(() => migrator.EnsureMigratedAsync(path, allow));
                Assert.Equal(nextVersion, error.DatabaseVersion);
                Assert.Equal(SqliteMigrator.CurrentSchemaVersion, error.SupportedVersion);
            }
            await Assert.ThrowsAsync<SqliteFutureSchemaException>(() => new SqliteAuthAdministration(path).GetStatusAsync(Now, default));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static NewAuthSession NewSession(int id, long created, long expires) =>
        new($"aus_{id:x32}", $"{id:x64}", created, expires, "test", "127.0.0.1");
}
