using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class MainMigrationUpgradeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AskIdentityUpgradePreservesHistoryAndRetriesAtomically(bool failUpgrade)
    {
        var directory = Directory.CreateTempSubdirectory("perga-ask-upgrade-");
        var path = Path.Combine(directory.FullName, "v9.db");
        try
        {
            var original = SqliteMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 9).ToArray();
            await new SqliteMigrator(original).MigrateAsync(path);
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO areas (id, name, color, created_at_ms, updated_at_ms) VALUES ('area', 'Area', '#000000', 1, 1);
                INSERT INTO tracks (id, area_id, cwd, created_at_ms, updated_at_ms) VALUES ('track', 'area', '/tmp', 1, 1);
                INSERT INTO asks (id, track_id, questions_json, answer_json, created_at_ms)
                VALUES ('ask', 'track', '[{"question":"Approve tool?","options":["Approve","Deny"]}]', NULL, 1);
                INSERT INTO events (id, track_id, kind, payload_json, created_at_ms)
                VALUES (17, 'track', 'ask.changed', '{"id":"ask","trackId":"track","status":"open","questions":[],"createdAtMs":1}', 1);
                """);
            if (failUpgrade)
            {
                await connection.ExecuteAsync("CREATE TRIGGER reject_v10 BEFORE INSERT ON schema_migrations WHEN NEW.version = 10 BEGIN SELECT RAISE(ABORT, 'injected receipt failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => new SqliteMigrator().MigrateAsync(path));
                Assert.Equal(9, await connection.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;"));
                Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('asks') WHERE name IN ('delivery', 'session_id', 'turn_id');"));
                Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT json_extract(payload_json, '$.delivery') FROM events WHERE id = 17;"));
                await connection.ExecuteAsync("DROP TRIGGER reject_v10;");
            }
            await new SqliteMigrator().MigrateAsync(path);
            await new SqliteMigrator().MigrateAsync(path);
            Assert.Equal("wake", await connection.ExecuteScalarAsync<string>("SELECT delivery FROM asks WHERE id = 'ask';"));
            Assert.Equal("wake", await connection.ExecuteScalarAsync<string>("SELECT json_extract(payload_json, '$.delivery') FROM events WHERE id = 17;"));
            Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT json_extract(payload_json, '$.sessionId') FROM events WHERE id = 17;"));
            Assert.Equal("open", await connection.ExecuteScalarAsync<string>("SELECT json_extract(payload_json, '$.status') FROM events WHERE id = 17;"));
            Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT session_id FROM asks WHERE id = 'ask';"));
            Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT turn_id FROM asks WHERE id = 'ask';"));
            Assert.Equal("open", await connection.ExecuteScalarAsync<string>("SELECT status FROM asks WHERE id = 'ask';"));
            foreach (var migration in original)
                Assert.Equal(migration.ChecksumSha256, await connection.ExecuteScalarAsync<string>(
                    "SELECT checksum_sha256 FROM schema_migrations WHERE version = @Version AND name = @Name;", migration));
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionEightTurnEffortUpgradeLeavesHistoryUnspecifiedAndRollsBackOnFailure(bool failUpgrade)
    {
        var directory = Directory.CreateTempSubdirectory("perga-turn-effort-upgrade-");
        var path = Path.Combine(directory.FullName, "v8.db");
        try
        {
            var original = SqliteMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 8).ToArray();
            await new SqliteMigrator(original).MigrateAsync(path);
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO areas (id, name, color, created_at_ms, updated_at_ms) VALUES ('area', 'Area', '#000000', 1, 1);
                INSERT INTO tracks (id, area_id, cwd, created_at_ms, updated_at_ms) VALUES ('track', 'area', '/tmp', 1, 1);
                INSERT INTO agent_sessions (id, track_id, provider, model, reasoning_effort, created_at_ms, updated_at_ms)
                VALUES ('session', 'track', 'codex', 'B', 'high', 1, 1);
                INSERT INTO session_turns (id, session_id, turn_index, trigger_kind, status, provider, model)
                VALUES ('turn', 'session', 1, 'user', 'queued', 'codex', 'A');
                """);
            if (failUpgrade)
            {
                await connection.ExecuteAsync("CREATE TRIGGER reject_v9 BEFORE INSERT ON schema_migrations WHEN NEW.version = 9 BEGIN SELECT RAISE(ABORT, 'injected receipt failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => new SqliteMigrator().MigrateAsync(path));
                Assert.Equal(8, await connection.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;"));
                Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('session_turns') WHERE name = 'reasoning_effort';"));
                await connection.ExecuteAsync("DROP TRIGGER reject_v9;");
            }
            await new SqliteMigrator().MigrateAsync(path);
            await new SqliteMigrator().MigrateAsync(path);
            Assert.Null(await connection.ExecuteScalarAsync<string?>("SELECT reasoning_effort FROM session_turns WHERE id = 'turn';"));
            Assert.Equal("A", await connection.ExecuteScalarAsync<string>("SELECT model FROM session_turns WHERE id = 'turn';"));
            Assert.Equal("high", await connection.ExecuteScalarAsync<string>("SELECT reasoning_effort FROM agent_sessions WHERE id = 'session';"));
            foreach (var migration in original)
                Assert.Equal(migration.ChecksumSha256, await connection.ExecuteScalarAsync<string>(
                    "SELECT checksum_sha256 FROM schema_migrations WHERE version = @Version AND name = @Name;", migration));
            Assert.True(File.Exists($"{path}.bak-v8"));
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedMain_UpgradesToReasoningEffortWithoutRewritingReceiptsOrLosingData(bool failUpgrade)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"perga-lineage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "lineage.db");
        try
        {
            var original = SqliteMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 7).ToArray();
            await new SqliteMigrator(original).MigrateAsync(path);
            await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync("""
                    INSERT INTO areas (id, name, color, created_at_ms, updated_at_ms) VALUES ('retained', 'Retained', '#000000', 1, 1);
                    INSERT INTO tracks (id, area_id, cwd, created_at_ms, updated_at_ms) VALUES ('track', 'retained', '/tmp', 1, 1);
                    INSERT INTO agent_sessions (id, track_id, provider, model, created_at_ms, updated_at_ms)
                    VALUES ('session', 'track', 'codex', 'retained-model', 1, 1);
                    UPDATE agent_sessions SET permission_mode = 'full-access' WHERE id = 'session';
                    INSERT INTO session_turns (id, session_id, turn_index, trigger_kind, status) VALUES ('turn', 'session', 1, 'user', 'queued');
                    """);
                await connection.ExecuteAsync("""
                        INSERT INTO session_messages (id, session_id, source, kind, author_id, request_key, request_hash, content, turn_id, delivery, created_at_ms)
                        VALUES ('input', 'session', 'session', 'input', 'owner', 'original-key', 'original-hash', 'Retained input', 'turn', 'queued', 1);
                        INSERT INTO track_workspace_preparations (track_id, intent_json) VALUES ('track', '{"retained":true}');
                        """);
            }
            if (failUpgrade)
            {
                await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
                await connection.OpenAsync();
                await connection.ExecuteAsync("CREATE TRIGGER reject_upgrade BEFORE INSERT ON schema_migrations WHEN NEW.version = 8 BEGIN SELECT RAISE(ABORT, 'injected receipt failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => new SqliteMigrator().MigrateAsync(path));
                Assert.Equal(7, await connection.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;"));
                Assert.Equal(0, await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('agent_sessions') WHERE name = 'reasoning_effort';"));
                await connection.ExecuteAsync("DROP TRIGGER reject_upgrade;");
            }
            await new SqliteMigrator().MigrateAsync(path);
            await new SqliteMigrator().MigrateAsync(path);
            await using var upgraded = new SqliteConnection($"Data Source={path};Pooling=False");
            await upgraded.OpenAsync();
            Assert.Equal("Retained", await upgraded.ExecuteScalarAsync<string>("SELECT name FROM areas WHERE id = 'retained';"));
            Assert.Equal("retained-model", await upgraded.ExecuteScalarAsync<string>("SELECT model FROM agent_sessions WHERE id = 'session';"));
            Assert.Null(await upgraded.ExecuteScalarAsync<string?>("SELECT reasoning_effort FROM agent_sessions WHERE id = 'session';"));
            Assert.Equal("Retained input", await upgraded.ExecuteScalarAsync<string>("SELECT content FROM session_messages WHERE id = 'input' AND request_key = 'original-key' AND request_hash = 'original-hash' AND delivery = 'queued';"));
            Assert.Equal("{\"retained\":true}", await upgraded.ExecuteScalarAsync<string>("SELECT intent_json FROM track_workspace_preparations WHERE track_id = 'track';"));
            Assert.Equal(11, await upgraded.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;"));
            foreach (var migration in original)
            {
                Assert.Equal(migration.ChecksumSha256, await upgraded.ExecuteScalarAsync<string>(
                    "SELECT checksum_sha256 FROM schema_migrations WHERE version = @Version AND name = @Name;", migration));
            }
            Assert.Equal("full-access", await upgraded.ExecuteScalarAsync<string>("SELECT permission_mode FROM agent_sessions WHERE id = 'session';"));
            Assert.Contains(await upgraded.QueryAsync<string>("SELECT name FROM pragma_table_info('agent_sessions');"), name => name == "reasoning_effort");
            Assert.Equal(1, await upgraded.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'track_workspace_preparations';"));
            Assert.Equal(1, await upgraded.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'session_messages';"));
            Assert.True(File.Exists($"{path}.bak-v{original.Length}"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

}
