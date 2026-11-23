using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class SessionRuntimeActivityMigrationTests
{
    [Fact]
    public async Task UpgradePreservesTranscriptHistoryConstraintsAndItemIds()
    {
        var directory = Directory.CreateTempSubdirectory("perga-runtime-activity-");
        var path = Path.Combine(directory.FullName, "v26.db");
        try
        {
            var original = SqliteMigrator.LoadEmbeddedMigrations().Where(migration => migration.Version <= 26).ToArray();
            await new SqliteMigrator(original).MigrateAsync(path);
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Foreign Keys=True");
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO areas (id, name, color, created_at_ms, updated_at_ms) VALUES ('area', 'Area', '#000000', 1, 1);
                INSERT INTO tracks (id, area_id, cwd, created_at_ms, updated_at_ms) VALUES ('track', 'area', '/tmp', 1, 1);
                INSERT INTO agent_sessions (id, track_id, provider, created_at_ms, updated_at_ms)
                VALUES ('session', 'track', 'codex', 1, 1);
                INSERT INTO session_turns (id, session_id, turn_index, trigger_kind, status)
                VALUES ('turn', 'session', 1, 'user', 'completed');
                INSERT INTO session_messages (ordinal, id, session_id, kind, author_id, request_key, request_hash, content, turn_id, delivery, created_at_ms)
                VALUES (10, 'delivered', 'session', 'input', 'owner', 'delivered', 'hash', 'Original message', 'turn', 'delivered', 1),
                       (11, 'interrupted', 'session', 'queue', 'owner', 'interrupted', 'hash', 'Queued message', NULL, 'interrupted', 2);
                INSERT INTO session_items (id, session_id, turn_id, kind, payload_json, created_at_ms)
                VALUES (5, 'session', 'turn', 'assistant_text', '{"text":"historical"}', 1),
                       (6, 'session', NULL, 'system', '{"status":"interrupted","archived":true}', 2),
                       (99, 'session', NULL, 'epoch_boundary', '{}', 3);
                INSERT INTO session_items (id, session_id, turn_id, kind, payload_json, created_at_ms, message_ordinal)
                VALUES (4, 'session', 'turn', 'user', '{"content":"Original message"}', 1, 10),
                       (7, 'session', NULL, 'user', '{"status":"interrupted","archived":true}', 2, 11);
                DELETE FROM session_items WHERE id = 99;
                """);

            const string readItems = """
                SELECT json_array(id, session_id, turn_id, kind, payload_json, created_at_ms, accepted_input_id, message_ordinal)
                FROM session_items ORDER BY id;
                """;
            const string readSchemaObjects = """
                SELECT name || ':' || sql FROM sqlite_schema
                WHERE tbl_name = 'session_items' AND type IN ('index', 'trigger') AND sql IS NOT NULL ORDER BY name;
                """;
            const string readForeignKeys = """
                SELECT json_array("table", "from", "to", on_update, on_delete, "match")
                FROM pragma_foreign_key_list('session_items') ORDER BY "table", "from", "to";
                """;
            const string readSequence = "SELECT seq FROM sqlite_sequence WHERE name = 'session_items';";
            var history = (await connection.QueryAsync<string>(readItems)).ToArray();
            var schemaObjects = (await connection.QueryAsync<string>(readSchemaObjects)).ToArray();
            var foreignKeys = (await connection.QueryAsync<string>(readForeignKeys)).ToArray();
            const string append = """
                INSERT INTO session_items (session_id, turn_id, kind, payload_json, created_at_ms)
                VALUES ('session', NULL, @Kind, '{}', 4);
                """;
            await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync(append, new { Kind = "assistant_text" }));

            await new SqliteMigrator().MigrateAsync(path);
            Assert.Equal(history, await connection.QueryAsync<string>(readItems));
            Assert.Equal(schemaObjects, await connection.QueryAsync<string>(readSchemaObjects));
            Assert.Equal(foreignKeys, await connection.QueryAsync<string>(readForeignKeys));
            Assert.Equal(99, await connection.ExecuteScalarAsync<long>(readSequence));
            foreach (var kind in new[] { "assistant_text", "thinking", "tool_call", "tool_result" })
                await connection.ExecuteAsync(append, new { Kind = kind });

            Assert.Equal(new long[] { 100, 101, 102, 103 }, await connection.QueryAsync<long>(
                "SELECT id FROM session_items WHERE id > 99 ORDER BY id;"));
            foreach (var kind in new[] { "user", "system", "turn_outcome" })
            {
                var error = await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync(append, new { Kind = kind }));
                Assert.Contains("CHECK constraint failed", error.Message);
            }
            var insertError = await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
                INSERT INTO session_items (session_id, kind, payload_json, created_at_ms, message_ordinal)
                VALUES ('session', 'assistant_text', '{}', 5, 10);
                """));
            Assert.Contains("session item does not match its message", insertError.Message);
            var updateError = await Assert.ThrowsAsync<SqliteException>(() => connection.ExecuteAsync("""
                UPDATE session_items SET turn_id = 'turn' WHERE id = 7;
                """));
            Assert.Contains("session item does not match its message", updateError.Message);
            Assert.Empty(await connection.QueryAsync("PRAGMA foreign_key_check;"));
        }
        finally { directory.Delete(recursive: true); }
    }
}
