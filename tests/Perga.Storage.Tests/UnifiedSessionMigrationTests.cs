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
            Assert.Equal(SqliteMigrator.CurrentSchemaVersion, await upgraded.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal("worker", await upgraded.ExecuteScalarAsync<string>("SELECT session_id FROM tasks WHERE id='task';"));
            Assert.Equal(0, await upgraded.ExecuteScalarAsync<int>("SELECT count(*) FROM sqlite_master WHERE name='task_attempts';"));
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

    [Fact]
    public async Task UpgradeFrom22BackfillsTasksTerminalsAndUnifiesSessionMessagesPreservingOrdinals()
    {
        var dir = Directory.CreateTempSubdirectory("perga-v23-migration-");
        var db = Path.Combine(dir.FullName, "test.db");
        try
        {
            await new SqliteMigrator(SqliteMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 22).ToArray()).MigrateAsync(db);
            await using (var conn = new SqliteConnection($"Data Source={db};Pooling=False;Foreign Keys=True"))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync("""
                    INSERT INTO areas(id,name,created_at_ms,updated_at_ms) VALUES('a','area',1,1);
                    INSERT INTO tracks(id,area_id,cwd,created_at_ms,updated_at_ms) VALUES('t','a','/tmp',1,1);
                    INSERT INTO tasks(id,track_id,block_id,task_key,kind,access_mode,goal_or_cmd,status,latest_attempt_id,created_at_ms,updated_at_ms)
                        VALUES('tsk1','t','b1','impl','agent','read_write','ship','done','att2',10,30);
                    INSERT INTO agent_sessions(id,track_id,bound_task_id,bound_attempt_id,provider,created_at_ms,updated_at_ms)
                        VALUES('ses_w','t','tsk1','att2','codex',10,30);
                    INSERT INTO task_attempts(attempt_id,task_id,track_id,session_id,status,worker_result_json,meta_json,gate_verdict_json,started_at_ms,finished_at_ms)
                        VALUES('att1','tsk1','t','ses_w','failed','{"try":1}','{"k":"v1"}','{"passed":false}',11,15),
                              ('att2','tsk1','t','ses_w','done','{"try":2}','{"k":"v2"}','{"passed":true}',20,30);
                    INSERT INTO terminals(id,track_id,attempt_id,title,program,cwd,status,created_at_ms)
                        VALUES('trm1','t','att2','sh','/bin/sh','/tmp','running',21);
                    INSERT INTO session_turns(id,session_id,turn_index,trigger_kind,status,started_at_ms,completed_at_ms)
                        VALUES('trn1','ses_w',1,'wake','completed',20,25);
                    INSERT INTO session_messages(id,source,session_id,attempt_id,kind,author_id,request_key,request_hash,content,turn_id,delivered_turn_id,delivery,created_at_ms)
                        VALUES('id','worker','ses_w','att1','worker','owner:installation','same','h1','first','trn1','trn1','delivered',11),
                              ('id','session','ses_w',NULL,'queue','owner:installation','same','h2','second',NULL,NULL,'queued',22),
                              ('id_2','session','ses_w',NULL,'queue','owner:installation','same#2','h3','third',NULL,NULL,'queued',23);
                    UPDATE sqlite_sequence SET seq=100 WHERE name='session_messages';
                    INSERT INTO session_items(session_id,turn_id,kind,payload_json,created_at_ms,message_ordinal)
                        VALUES('ses_w','trn1','user','{"content":"first"}',11,1);
                    """);
            }

            await new SqliteMigrator().MigrateAsync(db);
            await using var upgraded = new SqliteConnection($"Data Source={db};Pooling=False;Foreign Keys=True");
            await upgraded.OpenAsync();

            var taskRow = await upgraded.QuerySingleAsync<(string SessionId, int RunCount, string WorkerResult, string Meta, string GateVerdict, long StartedAtMs, long FinishedAtMs)>("""
                SELECT session_id, run_count, worker_result_json, meta_json, gate_verdict_json, started_at_ms, finished_at_ms
                FROM tasks WHERE id='tsk1';
                """);
            Assert.Equal(("ses_w", 2, "{\"try\":2}", "{\"k\":\"v2\"}", "{\"passed\":true}", 20L, 30L), taskRow);
            Assert.Equal("ses_w", await upgraded.ExecuteScalarAsync<string>("SELECT session_id FROM terminals WHERE id='trm1';"));
            Assert.Equal(3, await upgraded.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM session_messages WHERE session_id='ses_w';"));
            Assert.Equal(new long[] { 1, 2, 3 }, (await upgraded.QueryAsync<long>("SELECT ordinal FROM session_messages ORDER BY ordinal;")).ToArray());
            Assert.Equal(100L, await upgraded.ExecuteScalarAsync<long>("SELECT seq FROM sqlite_sequence WHERE name='session_messages';"));
            Assert.Equal(3, await upgraded.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT id) FROM session_messages;"));
            Assert.Equal(3, await upgraded.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT request_key) FROM session_messages;"));
            Assert.Equal(1L, await upgraded.ExecuteScalarAsync<long>("SELECT message_ordinal FROM session_items WHERE session_id='ses_w';"));
            Assert.Equal(0, await upgraded.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='task_attempts';"));
            Assert.Empty(await upgraded.QueryAsync("PRAGMA foreign_key_check;"));
        }
        finally { dir.Delete(true); }
    }
}
