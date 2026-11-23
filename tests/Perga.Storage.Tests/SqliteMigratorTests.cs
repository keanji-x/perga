using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class SqliteMigratorTests
{
    [Fact]
    public void EmbeddedInitMigration_MatchesFrozenSourceAndChecksum()
    {
        // Pin the released bytes independently of both the source and embedded resource.
        const string frozenSha256 = "05e8403f4c72725eb6afd5410ba4b500fa9f1073ef4488ab03a547db343d04cf";
        var init = Assert.Single(SqliteMigrator.LoadEmbeddedMigrations(), migration => migration.Version == 1);
        Assert.Equal(1, init.Version);
        Assert.Equal("0001_init.sql", init.Name);
        Assert.Equal(64, init.ChecksumSha256.Length);
        Assert.Equal(frozenSha256, init.ChecksumSha256);

        var sourceBytes = File.ReadAllBytes(FindRepoFile("src/Perga.Storage.Sqlite/Migrations/0001_init.sql"));
        Assert.Equal(frozenSha256, Convert.ToHexStringLower(SHA256.HashData(sourceBytes)));
        Assert.Equal(sourceBytes, Encoding.UTF8.GetBytes(init.Sql));
    }

    [Fact]
    public async Task MigrateAsync_OnFreshDatabase_SetsIncrementalAutoVacuumWalAndCreatesAllFrozenTablesAndIndexes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-migrator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, "fresh.db");
            var migrator = new SqliteMigrator();
            Assert.Equal(Enumerable.Range(1, 26), migrator.Migrations.Select(m => m.Version));
            Assert.Equal("0025_track_flow.sql", migrator.Migrations[^2].Name);
            Assert.Equal("0026_session_attachments.sql", migrator.Migrations[^1].Name);
            await migrator.MigrateAsync(dbPath);

            // Idempotent re-run on already-initialized v1 database must not create a spurious backup.
            await migrator.MigrateAsync(dbPath);
            Assert.False(File.Exists($"{dbPath}.bak-v1"));

            await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await conn.OpenAsync();

            Assert.Equal(26L, await conn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('areas') WHERE name = 'execution_target_id';"));
            Assert.Equal(3L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('tracks') WHERE name IN ('flow_spec_json', 'flow_rev', 'flow_file_sha256');"));
            Assert.Equal(4L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name IN ('trigger_run_key', 'outcome', 'outcome_summary', 'archived_at_ms');"));
            Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('track_schedules') WHERE name = 'node_key';"));
            Assert.Equal(13L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('track_wakes');"));
            Assert.Equal(0L, await conn.ExecuteScalarAsync<long>("SELECT [unique] FROM pragma_index_list('session_messages') WHERE name='idx_session_messages_input_turn';"));
            Assert.Equal(2L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('agent_sessions') WHERE name IN ('reasoning_effort', 'permission_mode');"));
            Assert.Equal(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('agent_sessions') WHERE name IN ('bound_task_id', 'bound_attempt_id');"));
            Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name = 'role';"));
            Assert.Equal(7L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('tasks') WHERE name IN ('session_id', 'run_count', 'worker_result_json', 'meta_json', 'gate_verdict_json', 'started_at_ms', 'finished_at_ms');"));
            var autoVacuum = await conn.ExecuteScalarAsync<long>("PRAGMA auto_vacuum;");
            var journalMode = await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode;");
            Assert.Equal(2L, autoVacuum); // 2 == INCREMENTAL
            Assert.Equal("wal", journalMode, ignoreCase: true);

            var tables = (await conn.QueryAsync<string>(
                """
                SELECT name FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                ORDER BY name ASC;
                """)).ToHashSet(StringComparer.Ordinal);

            string[] expectedTables =
            [
                "schema_migrations",
                "read_receipt_meta",
                "read_receipts",
                "daily_tracks",
                "areas",
                "tracks",
                "reports",
                "report_revisions",
                "tasks",
                "agent_sessions",
                "session_turns",
                "session_items",
                "session_inputs",
                "creation_receipts",
                "track_workspace_preparations",
                "asks",
                "mails",
                "terminals",
                "events",
                "session_messages",
                "system_upgrade_receipts",
                "track_wakes",
                "track_schedules",
                "session_message_steers",
                "execution_targets",
                "session_attachments",
                "session_message_attachments"
            ];

            Assert.Equal(expectedTables.OrderBy(static x => x), tables.OrderBy(static x => x));

            var indexes = (await conn.QueryAsync<string>(
                """
                SELECT name FROM sqlite_master
                WHERE type = 'index' AND name LIKE 'idx_%'
                ORDER BY name ASC;
                """)).ToHashSet(StringComparer.Ordinal);

            string[] expectedIndexes =
            [
                "idx_sessions_parent",
                "idx_creation_receipts_resource",
                "idx_asks_target",
                "idx_report_revisions_event",
                "idx_track_wakes_coalesce_pending",
                "idx_track_wakes_active",
                "idx_track_wakes_gc",
                "idx_track_schedules_due",
                "idx_track_schedules_track",
                "idx_tracks_area",
                "idx_tracks_parent",
                "idx_tasks_track_status",
                "idx_tasks_session",
                "idx_tasks_gate_blob_ref",
                "idx_sessions_track",
                "idx_turns_session_idx",
                "idx_turns_session_epoch",
                "idx_items_session",
                "idx_items_turn",
                "idx_inputs_delivery",
                "idx_asks_track",
                "idx_mails_pending_wake",
                "idx_mails_to_track",
                "idx_terminals_track",
                "idx_events_track_id",
                "idx_events_created",
                "idx_session_items_blob_ref",
                "idx_session_messages_queue",
                "idx_session_messages_boot",
                "idx_session_messages_input_turn",
                "idx_items_message_ordinal",
                "idx_session_attachments_session_status",
                "idx_session_attachments_staged_gc",
                "idx_session_attachments_track",
                "idx_session_message_attachments_attachment"
            ];

            Assert.Equal(expectedIndexes.OrderBy(static x => x), indexes.OrderBy(static x => x));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsync_WhenAppliedChecksumIsTampered_ThrowsSqliteMigrationTamperedException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-tamper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, "tamper.db");
            var migrator = new SqliteMigrator();
            await migrator.MigrateAsync(dbPath);

            await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString()))
            {
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    "UPDATE schema_migrations SET checksum_sha256 = '0000000000000000000000000000000000000000000000000000000000000000' WHERE version = 1;");
            }

            var ex = await Assert.ThrowsAsync<SqliteMigrationTamperedException>(() => migrator.MigrateAsync(dbPath));
            Assert.Equal(1, ex.Version);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsync_PendingV2Upgrade_CreatesOnlineBakV1BeforeApplyingAndRollsBackOnFailure()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, "upgrade.db");
            var v1 = SqliteMigrator.LoadEmbeddedMigrations()[0];

            var v1Migrator = new SqliteMigrator([v1]);
            await v1Migrator.MigrateAsync(dbPath);

            await using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync("""
                    INSERT INTO areas (id, name, color, created_at_ms, updated_at_ms)
                    VALUES ('area_v1', 'V1 Data', '#64748b', 0, 0);
                    """);
            }

            // 1. Failing v2 migration must create .bak-v1 BEFORE running v2 AND roll back live DB to v1.
            var failingV2 = SqliteMigration.Create(
                2,
                "0002_broken.sql",
                """
                ALTER TABLE areas ADD COLUMN notes TEXT DEFAULT '';
                INSERT INTO nonexistent_table_xyz VALUES (1);
                """);
            var failingMigrator = new SqliteMigrator([v1, failingV2]);
            await Assert.ThrowsAnyAsync<SqliteException>(() => failingMigrator.MigrateAsync(dbPath));

            var backupPath = $"{dbPath}.bak-v1";
            Assert.True(File.Exists(backupPath), "Online backup .bak-v1 must exist even when v2 fails.");

            await using (var liveConn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            {
                await liveConn.OpenAsync();
                var liveVersion = await liveConn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;");
                Assert.Equal(1L, liveVersion);
            }

            // 2. Valid v2 migration creates fresh .bak-v1 and advances live DB to v2 while .bak-v1 preserves v1 snapshot.
            var validV2 = SqliteMigration.Create(
                2,
                "0002_add_area_notes.sql",
                "ALTER TABLE areas ADD COLUMN notes TEXT NOT NULL DEFAULT 'migrated-v2';");
            var v2Migrator = new SqliteMigrator([v1, validV2]);
            await v2Migrator.MigrateAsync(dbPath);

            Assert.True(File.Exists(backupPath));

            await using (var backupConn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            {
                await backupConn.OpenAsync();
                var backupMaxVer = await backupConn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;");
                var backupAreaName = await backupConn.ExecuteScalarAsync<string>("SELECT name FROM areas WHERE id = 'area_v1';");
                Assert.Equal(1L, backupMaxVer);
                Assert.Equal("V1 Data", backupAreaName);
            }

            await using (var upgradedConn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            {
                await upgradedConn.OpenAsync();
                var upgradedMaxVer = await upgradedConn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;");
                var notesDefault = await upgradedConn.ExecuteScalarAsync<string>("SELECT notes FROM areas WHERE id = 'area_v1';");
                Assert.Equal(2L, upgradedMaxVer);
                Assert.Equal("migrated-v2", notesDefault);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsync_PreExistingEmptySqliteFileAndTableRebuildForeignKeyCheck_EnforcesInvariants()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-fkcheck-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dbPath = Path.Combine(tempDir, "preexisting.db");

            // Touch an empty 1-page SQLite database with default auto_vacuum = NONE (0) before MigrateAsync runs
            await using (var touchConn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString()))
            {
                await touchConn.OpenAsync();
                await touchConn.ExecuteAsync("CREATE TABLE temp_touch (x INT); DROP TABLE temp_touch;");
            }

            var v1 = SqliteMigrator.LoadEmbeddedMigrations()[0];
            await new SqliteMigrator([v1]).MigrateAsync(dbPath);

            await using (var checkConn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            {
                await checkConn.OpenAsync();
                Assert.Equal(2L, await checkConn.ExecuteScalarAsync<long>("PRAGMA auto_vacuum;"));
            }

            // Migration using PRAGMA foreign_keys = OFF that leaves an orphan FK row must fail PRAGMA foreign_key_check and roll back!
            var badFkV2 = SqliteMigration.Create(
                2,
                "0002_orphan_fk.sql",
                """
                PRAGMA foreign_keys = OFF;
                INSERT INTO tracks (id, area_id, title, cwd, created_at_ms, updated_at_ms)
                VALUES ('trk_orphan', 'area_missing', 'Orphan', '/tmp', 1, 1);
                """);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new SqliteMigrator([v1, badFkV2]).MigrateAsync(dbPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find '{relativePath}' walking upward from '{AppContext.BaseDirectory}'.");
    }
}
