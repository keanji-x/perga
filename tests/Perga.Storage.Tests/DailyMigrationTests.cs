using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class DailyMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeFromTenPreservesUserAreasAndRollsBackAtomically(bool fail)
    {
        var directory = Directory.CreateTempSubdirectory("today-upgrade-");
        var path = Path.Combine(directory.FullName, "old.db");
        try
        {
            var released = SqliteMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 10).ToArray();
            await new SqliteMigrator(released).MigrateAsync(path);
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await connection.ExecuteAsync("INSERT INTO areas (id, name, color, created_at_ms, updated_at_ms) VALUES ('user', 'Retained', 'gray', 1, 1);");
            if (fail)
            {
                await connection.ExecuteAsync("CREATE TRIGGER reject_today BEFORE INSERT ON schema_migrations WHEN NEW.version = 12 BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => new SqliteMigrator().MigrateAsync(path));
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM pragma_table_info('areas') WHERE name = 'is_system'"));
                Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM sqlite_master WHERE name = 'daily_tracks'"));
                await connection.ExecuteAsync("DROP TRIGGER reject_today");
            }
            await new SqliteMigrator().MigrateAsync(path);
            await new SqliteMigrator().MigrateAsync(path);
            Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT is_system FROM areas WHERE id = 'user'"));
            Assert.Equal("Retained", await connection.ExecuteScalarAsync<string>("SELECT name FROM areas WHERE id = 'user'"));
            foreach (var migration in released)
                Assert.Equal(migration.ChecksumSha256, await connection.ExecuteScalarAsync<string>(
                    "SELECT checksum_sha256 FROM schema_migrations WHERE version = @Version", migration));
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(true); }
    }
}
