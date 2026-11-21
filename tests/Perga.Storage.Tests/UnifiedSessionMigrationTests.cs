using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class UnifiedSessionMigrationTests
{
    [Theory]
    [InlineData(19, true, false)]
    [InlineData(20, true, false)]
    [InlineData(19, false, false)]
    [InlineData(20, false, false)]
    [InlineData(19, true, true)]
    [InlineData(20, true, true)]
    [InlineData(19, false, true)]
    [InlineData(20, false, true)]
    public async Task ReleasedVersionUpgradeBackfillsOwnershipAndConfigAndDropsWorkerSettings(int version, bool parentExists, bool reset)
    {
        var dir = Directory.CreateTempSubdirectory("perga-unified-migration-");
        var db = Path.Combine(dir.FullName, "test.db");
        try
        {
            await new SqliteMigrator(SqliteMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= version).ToArray()).MigrateAsync(db);
            await using (var conn = new SqliteConnection($"Data Source={db};Pooling=False;Foreign Keys=True"))
            {
                await conn.OpenAsync();
                if (version == 20)
                    await conn.ExecuteAsync("""
                        UPDATE read_receipt_meta SET baseline_ms=42 WHERE id=1;
                        INSERT INTO read_receipts(kind,target_id,read_through_ms,manual_unread,updated_at_ms)
                            VALUES('conversation','worker',123,1,456);
                        """);
                await conn.ExecuteAsync("""
                    INSERT INTO areas(id,name,created_at_ms,updated_at_ms) VALUES('a','area',1,1);
                    INSERT INTO tracks(id,area_id,cwd,created_at_ms,updated_at_ms) VALUES('t','a','/tmp',1,1);
                    INSERT INTO agent_sessions(id,track_id,provider,created_at_ms,updated_at_ms) VALUES('parent','t','codex',1,1);
                    INSERT INTO tasks(id,track_id,block_id,task_key,kind,access_mode,goal_or_cmd,created_at_ms,updated_at_ms)
                        VALUES('task','t','block','work','agent','read_only','inspect',1,1);
                    INSERT INTO agent_sessions(id,track_id,bound_task_id,provider,model,reasoning_effort,created_at_ms,updated_at_ms)
                        VALUES('worker','t','task','codex','old','low',1,1);
                    INSERT INTO worker_settings(session_id,version,model,reasoning_effort,approval_mode,author_id)
                        VALUES('worker',7,'model','high','deny','parent');
                    INSERT INTO creation_receipts(author_id,operation,scope_id,request_key,request_hash,resource_id,created_at_ms)
                        VALUES('parent','worker-dispatch','t','key','hash','task',1);
                    """);
                await conn.ExecuteAsync("INSERT INTO creation_receipts(author_id,operation,scope_id,request_key,request_hash,resource_id,created_at_ms) VALUES('owner:installation','session','t','create','hash','worker',1);");
                if (reset) await conn.ExecuteAsync("UPDATE worker_settings SET model=NULL,reasoning_effort=NULL WHERE session_id='worker';");
                if (!parentExists) await conn.ExecuteAsync("DELETE FROM agent_sessions WHERE id='parent';");
            }
            await new SqliteMigrator().MigrateAsync(db);
            await using var upgraded = new SqliteConnection($"Data Source={db};Pooling=False;Foreign Keys=True");
            await upgraded.OpenAsync();
            var row = await upgraded.QuerySingleAsync<(string? Parent, int Depth, int Version, string Notify, string Model, string Effort)>("""
                SELECT parent_session_id,spawn_depth,config_version,notify_parent,model,reasoning_effort
                FROM agent_sessions WHERE id='worker';
                """);
            Assert.Equal((parentExists ? "parent" : null, 1, 7, "task_settled", reset ? null : "model", reset ? null : "high"), row);
            Assert.Equal(0, await upgraded.ExecuteScalarAsync<int>("SELECT count(*) FROM sqlite_master WHERE name='worker_settings';"));
            Assert.Equal(1, await upgraded.ExecuteScalarAsync<int>("SELECT count(*) FROM pragma_table_info('asks') WHERE name='target_session_id';"));
            Assert.Equal(3, await upgraded.ExecuteScalarAsync<int>("SELECT count(*) FROM sqlite_master WHERE name IN ('idx_sessions_parent','idx_creation_receipts_resource','idx_asks_target');"));
            await Assert.ThrowsAsync<SqliteException>(() => upgraded.ExecuteAsync("UPDATE agent_sessions SET spawn_depth=-1 WHERE id='worker';"));
            await Assert.ThrowsAsync<SqliteException>(() => upgraded.ExecuteAsync("UPDATE agent_sessions SET notify_parent='invalid' WHERE id='worker';"));
            Assert.Equal("session-create", await upgraded.ExecuteScalarAsync<string>("SELECT operation FROM creation_receipts WHERE request_key='create';"));
            Assert.Equal(0, await upgraded.ExecuteScalarAsync<int>("SELECT count(*) FROM creation_receipts WHERE operation='session';"));
            Assert.Equal(21, await upgraded.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
            if (version == 20)
            {
                Assert.Equal(42, await upgraded.ExecuteScalarAsync<long>("SELECT baseline_ms FROM read_receipt_meta WHERE id=1;"));
                Assert.Equal((123L, 1L, 456L), await upgraded.QuerySingleAsync<(long, long, long)>(
                    "SELECT read_through_ms,manual_unread,updated_at_ms FROM read_receipts WHERE kind='conversation' AND target_id='worker';"));
            }
            Assert.Empty(await upgraded.QueryAsync("PRAGMA foreign_key_check;"));
        }
        finally { dir.Delete(true); }
    }
}
