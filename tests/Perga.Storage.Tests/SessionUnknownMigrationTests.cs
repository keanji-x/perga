using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class SessionUnknownMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration19MovesResumeFencesAndPreservesHistoryAndConstraints(bool emptyHistory)
    {
        var dir = Directory.CreateTempSubdirectory("perga-v19-");
        var db = Path.Combine(dir.FullName, "test.db");
        try
        {
            var migrations = SqliteMigrator.LoadEmbeddedMigrations();
            await new SqliteMigrator(migrations.Where(m => m.Version <= 18).ToArray()).MigrateAsync(db);
            await using (var conn = new SqliteConnection($"Data Source={db};Pooling=False;Foreign Keys=True"))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync("""
                    INSERT INTO areas(id, name, created_at_ms, updated_at_ms) VALUES ('a', 'area', 1, 1);
                    INSERT INTO tracks(id, area_id, cwd, created_at_ms, updated_at_ms) VALUES ('t', 'a', '/tmp', 1, 1);
                    INSERT INTO agent_sessions(id, track_id, provider, status, created_at_ms, updated_at_ms)
                    VALUES ('s', 't', 'codex', 'idle', 1, 1);
                    INSERT INTO session_turns(id, session_id, turn_index, trigger_kind, status) VALUES ('turn', 's', 1, 'user', 'unknown');
                    INSERT INTO session_items(id, session_id, turn_id, kind, payload_json, created_at_ms) VALUES (42, 's', 'turn', 'assistant_text', '{"text":"keep"}', 2);
                    INSERT INTO session_items(id, session_id, turn_id, kind, payload_json, created_at_ms) VALUES (73, 's', 'turn', 'assistant_text', '{}', 2);
                    DELETE FROM session_items WHERE id = 73;
                    INSERT INTO provider_session_resume(session_id, turn_id, native_id, quiescent) VALUES ('s', 'turn', 'native', 0);
                    """);
                if (emptyHistory) await conn.ExecuteAsync("DELETE FROM session_items;");
            }
            await new SqliteMigrator().MigrateAsync(db);
            await using var upgraded = new SqliteConnection($"Data Source={db};Pooling=False;Foreign Keys=True");
            await upgraded.OpenAsync();
            var row = await upgraded.QuerySingleAsync<(string Status, string Reason, string Turn, string Native, long Invalid)>("""
                SELECT status, unknown_reason, unknown_turn_id, native_thread_id, reconnect_invalidated FROM agent_sessions WHERE id = 's';
                """);
            Assert.Equal(("unknown", "stream_lost", "turn", "native", 0L), row);
            Assert.Equal(emptyHistory ? null : "{\"text\":\"keep\"}", await upgraded.ExecuteScalarAsync<string?>("SELECT payload_json FROM session_items WHERE id = 42;"));
            Assert.Equal(0L, await upgraded.ExecuteScalarAsync<long>("SELECT count(*) FROM sqlite_master WHERE name = 'provider_session_resume';"));
            await upgraded.ExecuteAsync("INSERT INTO session_items(session_id, kind, payload_json, created_at_ms) VALUES ('s', 'epoch_boundary', '{\"epoch\":2}', 3);");
            Assert.True(await upgraded.ExecuteScalarAsync<long>("SELECT max(id) FROM session_items;") > 73);
            await Assert.ThrowsAsync<SqliteException>(() => upgraded.ExecuteAsync("INSERT INTO session_items(session_id, kind, payload_json, created_at_ms) VALUES ('s', 'user', '{}', 3);"));
            await upgraded.ExecuteAsync("""
                INSERT INTO session_messages(id, session_id, source, kind, author_id, request_key, request_hash, content, delivery, created_at_ms)
                VALUES ('queued', 's', 'session', 'queue', 'user', 'key', 'hash', 'exact text', 'interrupted', 3);
                INSERT INTO session_items(session_id, kind, payload_json, created_at_ms, message_ordinal)
                VALUES ('s', 'user', '{"content":"exact text","status":"interrupted","archived":true}', 3,
                    (SELECT ordinal FROM session_messages WHERE id='queued'));
                INSERT INTO session_items(session_id, kind, payload_json, created_at_ms)
                VALUES ('s', 'system', '{"content":"critical wake","status":"interrupted","archived":true}', 3);
                """);
            await Assert.ThrowsAsync<SqliteException>(() => upgraded.ExecuteAsync("UPDATE session_items SET kind='assistant_text' WHERE turn_id IS NULL AND kind='user';"));
            await Assert.ThrowsAsync<SqliteException>(() => upgraded.ExecuteAsync("UPDATE session_items SET payload_json='{}' WHERE turn_id IS NULL AND kind='user';"));
            Assert.Empty(await upgraded.QueryAsync("PRAGMA foreign_key_check;"));
        }
        finally { dir.Delete(true); }
    }
}
