using Dapper;
using Perga.Contracts;
using Perga.ReportDoc;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class SqliteMaintenanceGcTests
{
    private const long DayMs = 86_400_000L;

    [Fact]
    public async Task Rule1_PruneEvents_PrunesOlderThan7DaysAndCapsAt50000In1000RowBatches()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;
        const long nowMs = 100L * DayMs;

        // Seed 2,350 expired events (> 7 days old) + 51,200 recent events directly for fast setup (total = 53,550 rows)
        using (var rawConn = harness.OpenRawInspectConnection())
        {
            using var tx = rawConn.BeginTransaction();
            using var cmd = rawConn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO events (area_id, track_id, kind, payload_json, created_at_ms) VALUES ('a1', 't1', 'tick', '{}', $ts);";
            var tsParam = cmd.CreateParameter();
            tsParam.ParameterName = "$ts";
            cmd.Parameters.Add(tsParam);

            var expiredTs = nowMs - 8L * DayMs;
            for (var i = 0; i < 2_350; i++)
            {
                tsParam.Value = expiredTs + i;
                cmd.ExecuteNonQuery();
            }

            var recentStartTs = nowMs - 1L * DayMs;
            for (var i = 0; i < 51_200; i++)
            {
                tsParam.Value = recentStartTs + i;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        // Expected pruned: 2,350 expired + 1,200 oldest recent to hit exact 50,000 cap = 3,550 pruned!
        var stats = await store.RunMaintenancePassAsync(nowMs, ct);
        Assert.Equal(3_550, stats.PrunedEvents);

        using (var verifyConn = harness.OpenRawInspectConnection())
        {
            var remainingCount = await verifyConn.ExecuteScalarAsync<long>("SELECT COUNT(1) FROM events;");
            var oldestRemainingTs = await verifyConn.ExecuteScalarAsync<long>("SELECT MIN(created_at_ms) FROM events;");
            Assert.Equal(50_000L, remainingCount);
            Assert.Equal(nowMs - 1L * DayMs + 1_200, oldestRemainingTs);
        }
    }

    [Fact]
    public async Task EventRetention_CapsPayloadBytesAndKeepsAContiguousReplaySuffix()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        const long nowMs = 100L * DayMs;
        using var rawConn = harness.OpenRawInspectConnection();

        await rawConn.ExecuteAsync(
            """
            WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 34)
            INSERT INTO events (kind, payload_json, created_at_ms)
            SELECT 'test', '{"text":"' || hex(zeroblob(524288)) || '"}', @Now FROM numbers;
            """, new { Now = nowMs });

        var stats = await harness.Store.RunMaintenancePassAsync(nowMs, CancellationToken.None);
        Assert.Equal(3, stats.PrunedEvents);
        Assert.Equal(Enumerable.Range(4, 31), await rawConn.QueryAsync<int>("SELECT id FROM events ORDER BY id"));
    }

    [Fact]
    public async Task ReportRetention_PreservesInitialAndUsesShorterWindowForSettledClosedTracks()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;
        const long nowMs = 100L * DayMs;

        await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_1"), ct);

        // Active tracks keep initial and latest 100 revisions.
        var (activeTrack, activeInit, activeLead) = StorageTestHarness.CreateSeedTrack("trk_active", "area_1", "sess_active", nowMs: nowMs);
        await store.CreateTrackAsync(activeTrack, activeInit, null, activeLead, ct);

        // Settled closed tracks keep initial and latest 10 revisions.
        var (closedOldTrack, closedInit, closedLead) = StorageTestHarness.CreateSeedTrack(
            "trk_closed_old", "area_1", "sess_closed_old", nowMs: nowMs - 20L * DayMs, closedAtMs: nowMs - 15L * DayMs);
        await store.CreateTrackAsync(closedOldTrack, closedInit, null, closedLead, ct);

        // 3. Recently closed track (closed 3 days ago): revisions 1..25 -> all 25 kept!
        var (closedRecentTrack, recentInit, recentLead) = StorageTestHarness.CreateSeedTrack(
            "trk_closed_recent", "area_1", "sess_closed_recent", nowMs: nowMs - 5L * DayMs, closedAtMs: nowMs - 3L * DayMs);
        await store.CreateTrackAsync(closedRecentTrack, recentInit, null, recentLead, ct);

        var zstdPayload = SqliteJsonUpcaster.CompressReportBlocksZstd(activeInit.Blocks);
        using (var rawConn = harness.OpenRawInspectConnection())
        {
            using var tx = rawConn.BeginTransaction();
            for (var rev = 2; rev <= 155; rev++)
            {
                await rawConn.ExecuteAsync(
                    "INSERT INTO report_revisions (track_id, doc_rev, message, blocks_zstd, created_at_ms) VALUES ('trk_active', @Rev, 'r', @Blob, @Now);",
                    new { Rev = rev, Blob = zstdPayload, Now = nowMs },
                    tx);
            }

            for (var rev = 2; rev <= 34; rev++)
            {
                await rawConn.ExecuteAsync(
                    "INSERT INTO report_revisions (track_id, doc_rev, message, blocks_zstd, created_at_ms) VALUES ('trk_closed_old', @Rev, 'r', @Blob, @Now);",
                    new { Rev = rev, Blob = zstdPayload, Now = nowMs - 15L * DayMs },
                    tx);
            }

            for (var rev = 2; rev <= 25; rev++)
            {
                await rawConn.ExecuteAsync(
                    "INSERT INTO report_revisions (track_id, doc_rev, message, blocks_zstd, created_at_ms) VALUES ('trk_closed_recent', @Rev, 'r', @Blob, @Now);",
                    new { Rev = rev, Blob = zstdPayload, Now = nowMs - 3L * DayMs },
                    tx);
            }

            tx.Commit();
        }

        var stats = await store.RunMaintenancePassAsync(nowMs, ct);
        Assert.Equal(54 + 23, stats.ThinnedRevisions);

        using (var verifyConn = harness.OpenRawInspectConnection())
        {
            var activeRevs = (await verifyConn.QueryAsync<int>(
                "SELECT doc_rev FROM report_revisions WHERE track_id = 'trk_active' ORDER BY doc_rev ASC;")).ToList();
            Assert.Equal([1, .. Enumerable.Range(56, 100)], activeRevs);

            var closedOldRevs = (await verifyConn.QueryAsync<int>(
                "SELECT doc_rev FROM report_revisions WHERE track_id = 'trk_closed_old' ORDER BY doc_rev ASC;")).ToArray();
            Assert.Equal([1, .. Enumerable.Range(25, 10)], closedOldRevs);

            var closedRecentCount = await verifyConn.ExecuteScalarAsync<long>(
                "SELECT COUNT(1) FROM report_revisions WHERE track_id = 'trk_closed_recent';");
            Assert.Equal(25L, closedRecentCount);
        }
    }

    [Fact]
    public async Task SessionRetention_CompactsOldTurnsEvenDuringActiveWorkAndReclaimsPages()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;
        const long nowMs = 100L * DayMs;

        await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_gc"), ct);
        var (track, report, leadSeed) = StorageTestHarness.CreateSeedTrack("trk_gc", "area_gc", "sess_lead_epoch3", nowMs: nowMs);
        var leadEpoch3 = leadSeed with { Epoch = 3 };
        await store.CreateTrackAsync(track, report, null, leadEpoch3, ct);

        var mediumToolJson = StorageTestHarness.Json($$"""{"stdout":"{{new string('M', 8_000)}}"}"""); // ~8 KB inline (> 4 KB)
        var smallToolJson = StorageTestHarness.Json("""{"stdout":"short"}""");                           // < 4 KB inline
        var coldBlobToolJson = StorageTestHarness.Json($$"""{"callId":"tool-1","isError":true,"output":"{{new string('C', 90_000)}}"}"""); // > 64 KB cold .zst blob

        // A. Lead session at Epoch 3:
        //    - Turn in Epoch 1 (1 < 3 - 1) -> MUST be slimmed (1 thinking deleted + 1 inline >4KB tool_result + 1 >64KB cold-blob tool_result = 3 compacted items; user/assistant_text/tool_call/small tool_result preserved!)
        //    - Turn in Epoch 2 (2 == 3 - 1) -> MUST NOT be slimmed!
        await store.RecordTurnAndItemsAsync(
            new SessionTurnDto("turn_ep1", "sess_lead_epoch3", 1, Epoch: 1, "user", StorageTestHarness.Json("{}"), "completed", null, null, nowMs - 1_000, nowMs - 900),
            [
                new SessionItemDto(0, "sess_lead_epoch3", "turn_ep1", "user", StorageTestHarness.Json("""{"text":"q1"}"""), nowMs - 1_000),
                new SessionItemDto(0, "sess_lead_epoch3", "turn_ep1", "thinking", StorageTestHarness.Json("""{"text":"deep thought"}"""), nowMs - 980),
                new SessionItemDto(0, "sess_lead_epoch3", "turn_ep1", "tool_call", StorageTestHarness.Json("""{"name":"rg"}"""), nowMs - 960),
                new SessionItemDto(0, "sess_lead_epoch3", "turn_ep1", "tool_result", smallToolJson, nowMs - 950),
                new SessionItemDto(0, "sess_lead_epoch3", "turn_ep1", "tool_result", mediumToolJson, nowMs - 940),
                new SessionItemDto(0, "sess_lead_epoch3", "turn_ep1", "tool_result", coldBlobToolJson, nowMs - 930),
                new SessionItemDto(0, "sess_lead_epoch3", "turn_ep1", "assistant_text", StorageTestHarness.Json("""{"text":"a1"}"""), nowMs - 900)
            ],
            updatedSession: null,
            ct);

        await store.RecordTurnAndItemsAsync(
            new SessionTurnDto("turn_ep2", "sess_lead_epoch3", 2, Epoch: 2, "user", StorageTestHarness.Json("{}"), "completed", null, null, nowMs - 500, nowMs - 400),
            [
                new SessionItemDto(0, "sess_lead_epoch3", "turn_ep2", "thinking", StorageTestHarness.Json("""{"text":"keep ep2"}"""), nowMs - 480),
                new SessionItemDto(0, "sess_lead_epoch3", "turn_ep2", "tool_result", mediumToolJson, nowMs - 450)
            ],
            updatedSession: null,
            ct);

        // B. Settled contract subagent (bound_attempt_id != null, finished 15 days ago) -> MUST be slimmed (1 thinking + 1 >4KB tool_result = 2 compacted items)
        var taskSub = new TaskDto(
            "t_sub", "trk_gc", "b_1", "work", 1, "subagent", "codex", AccessMode.ReadWrite,
            true, [], "Work", null, null, null, TaskRunStatus.Done, null, SemanticVerdict.None, null, "att_old_settled", nowMs - 16L * DayMs, nowMs - 15L * DayMs);
        var attemptOld = new TaskAttemptDto(
            "att_old_settled", "t_sub", "trk_gc", "sess_sub_old", "done",
            StorageTestHarness.Json("""{"stdout":"Worker business result","stdoutTruncated":false}"""),
            StorageTestHarness.Json("{}"), null, StorageTestHarness.Json($$"""
                {"passed":true,"steps":[{"name":"check","exitCode":0,"stdout":"{{new string('F', 80_000)}}","stderr":""}]}
                """), nowMs - 16L * DayMs, nowMs - 15L * DayMs);
        await store.UpsertTaskAndAttemptAsync(taskSub, attemptOld, null, null, ct);

        var subOld = new AgentSessionDto(
            "sess_sub_old", "trk_gc", false, "t_sub", "att_old_settled", "Worker Old",
            "codex", null, null, Epoch: 1, "idle", 0.4, null, nowMs - 16L * DayMs, nowMs - 15L * DayMs);
        await store.UpsertSessionAsync(subOld, null, ct);
        await store.RecordTurnAndItemsAsync(
            new SessionTurnDto("turn_sub_old", "sess_sub_old", 1, Epoch: 1, "wake", StorageTestHarness.Json("{}"), "completed", null, null, nowMs - 16L * DayMs, nowMs - 15L * DayMs),
            [
                new SessionItemDto(0, "sess_sub_old", "turn_sub_old", "thinking", StorageTestHarness.Json("""{"text":"old sub thought"}"""), nowMs - 15L * DayMs),
                new SessionItemDto(0, "sess_sub_old", "turn_sub_old", "tool_result", mediumToolJson, nowMs - 15L * DayMs)
            ],
            updatedSession: null,
            ct);

        // C. An unbound session with a newer or still running turn may discard diagnostics from its old completed turn.
        var subFree = new AgentSessionDto(
            "sess_sub_free", "trk_gc", false, null, null, "Free Helper",
            "codex", null, null, Epoch: 1, "running", 0.2, null, nowMs - 16L * DayMs, nowMs - 15L * DayMs);
        await store.UpsertSessionAsync(subFree, null, ct);
        await store.RecordTurnAndItemsAsync(
            new SessionTurnDto("turn_sub_free", "sess_sub_free", 1, Epoch: 1, "user", StorageTestHarness.Json("{}"), "completed", null, null, nowMs - 16L * DayMs, nowMs - 15L * DayMs),
            [
                new SessionItemDto(0, "sess_sub_free", "turn_sub_free", "thinking", StorageTestHarness.Json("""{"text":"keep free sub"}"""), nowMs - 15L * DayMs)
            ],
            updatedSession: null,
            ct);

        await store.RecordTurnAndItemsAsync(
            new SessionTurnDto("turn_sub_active", "sess_sub_free", 2, Epoch: 1, "user", StorageTestHarness.Json("{}"), "running", null, null, nowMs - 15L * DayMs, null),
            [new SessionItemDto(0, "sess_sub_free", "turn_sub_active", "thinking", StorageTestHarness.Json("""{"text":"keep active turn"}"""), nowMs - 15L * DayMs)],
            updatedSession: null,
            ct);

        // D. Also add ~400 KB of expired events and flush WAL into .db first so we can verify Rule 4 physical page & file reclamation!
        using (var rawConn = harness.OpenRawInspectConnection())
        {
            var fatPayload = $$"""{"blob":"{{new string('P', 4096)}}"}""";
            using (var tx = rawConn.BeginTransaction())
            {
                for (var i = 0; i < 120; i++)
                {
                    await rawConn.ExecuteAsync(
                        "INSERT INTO events (area_id, track_id, kind, payload_json, created_at_ms) VALUES ('area_gc', 'trk_gc', 'fat', @Payload, @Ts);",
                        new { Payload = fatPayload, Ts = nowMs - 10L * DayMs },
                        tx);
                }

                tx.Commit();
            }

            await rawConn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var trackBlobDir = Path.Combine(harness.DataDir, "blobs", "trk_gc");
        Assert.Equal(2, Directory.GetFiles(trackBlobDir, "*.zst").Length);
        var dbSizeBeforeGc = new FileInfo(harness.DbPath).Length;

        var stats = await store.RunMaintenancePassAsync(nowMs, ct);

        Assert.Equal(120, stats.PrunedEvents);
        Assert.Equal(6, stats.CompactedItems);
        Assert.Equal(1, stats.CompactedAttempts);
        var compactedAttempt = Assert.Single(await store.ListAttemptsForTaskAsync("t_sub", ct));
        Assert.Equal("Worker business result", compactedAttempt.WorkerResult!.Value.GetProperty("stdout").GetString());
        var step = compactedAttempt.GateVerdict!.Value.GetProperty("steps")[0];
        Assert.False(step.TryGetProperty("stdout", out _));
        Assert.Equal(0, step.GetProperty("exitCode").GetInt32());
        Assert.True(stats.ReclaimedPages > 0, $"Expected positive ReclaimedPages from incremental_vacuum, got {stats.ReclaimedPages}.");

        // Verify unreferenced .zst cold blob was deleted!
        Assert.True(!Directory.Exists(trackBlobDir) || Directory.GetFiles(trackBlobDir, "*.zst").Length == 0);

        // Verify physical .db shrunk on disk and .db-wal was truncated!
        var dbSizeAfterGc = new FileInfo(harness.DbPath).Length;
        Assert.True(dbSizeAfterGc < dbSizeBeforeGc, $"Expected physical .db size to shrink ({dbSizeAfterGc} < {dbSizeBeforeGc}).");
        var walFile = new FileInfo($"{harness.DbPath}-wal");
        if (walFile.Exists)
        {
            Assert.Equal(0L, walFile.Length);
        }

        // Verify exact surviving & compacted items in Epoch 1 vs Epoch 2 vs settled/free subagents
        var leadItems = await store.ListSessionItemsAsync("sess_lead_epoch3", 0, 50, ct);
        var ep1Items = leadItems.Where(static x => x.TurnId == "turn_ep1").ToList();
        Assert.DoesNotContain(ep1Items, static x => x.Kind == "thinking");
        Assert.Contains(ep1Items, static x => x.Kind == "user");
        Assert.Contains(ep1Items, static x => x.Kind == "tool_call");
        Assert.Contains(ep1Items, static x => x.Kind == "assistant_text");

        var ep1ToolResults = ep1Items.Where(static x => x.Kind == "tool_result").ToList();
        Assert.Equal(3, ep1ToolResults.Count);
        Assert.Equal("short", ep1ToolResults[0].Payload.GetProperty("stdout").GetString());
        Assert.True(ep1ToolResults[1].Payload.GetProperty("compacted").GetBoolean());
        Assert.True(ep1ToolResults[2].Payload.GetProperty("compacted").GetBoolean());
        Assert.True(ep1ToolResults[2].Payload.GetProperty("bytes").GetInt64() > 65_536);
        Assert.Equal("tool-1", ep1ToolResults[2].Payload.GetProperty("callId").GetString());
        Assert.True(ep1ToolResults[2].Payload.GetProperty("isError").GetBoolean());

        var ep2Items = leadItems.Where(static x => x.TurnId == "turn_ep2").ToList();
        Assert.Equal(2, ep2Items.Count); // Both thinking & >4KB tool_result untouched in epoch == current - 1!

        var freeSubItems = await store.ListSessionItemsAsync("sess_sub_free", 0, 10, ct);
        Assert.Equal("turn_sub_active", Assert.Single(freeSubItems).TurnId);

        // Second maintenance pass must be 100% idempotent (0 CompactedItems)
        var secondPassStats = await store.RunMaintenancePassAsync(nowMs, ct);
        Assert.Equal(0, secondPassStats.PrunedEvents);
        Assert.Equal(0, secondPassStats.ThinnedRevisions);
        Assert.Equal(0, secondPassStats.CompactedItems);
    }

    [Fact]
    public async Task Rule3_SharedContentAddressedColdBlob_RetainsZstFileWhileActiveEpochStillReferencesIt()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;
        const long nowMs = 100L * DayMs;

        await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_1"), ct);
        var (track, report, leadSeed) = StorageTestHarness.CreateSeedTrack("trk_shared_blob", "area_1", "sess_shared", nowMs: nowMs);
        await store.CreateTrackAsync(track, report, null, leadSeed with { Epoch = 3 }, ct);

        var sharedPayloadText = new string('S', 80_000);
        var sharedColdPayload = StorageTestHarness.Json($$"""{"stdout":"{{sharedPayloadText}}"}""");

        // Epoch 1 (eligible for GC) and Epoch 3 (active, NOT eligible for GC) reference the exact same SHA-256 .zst blob!
        await store.RecordTurnAndItemsAsync(
            new SessionTurnDto("turn_old", "sess_shared", 1, Epoch: 1, "user", StorageTestHarness.Json("{}"), "completed", null, null, nowMs - 2_000, nowMs - 1_900),
            [new SessionItemDto(0, "sess_shared", "turn_old", "tool_result", sharedColdPayload, nowMs - 1_900)],
            updatedSession: null,
            ct);
        await store.RecordTurnAndItemsAsync(
            new SessionTurnDto("turn_cur", "sess_shared", 2, Epoch: 3, "user", StorageTestHarness.Json("{}"), "completed", null, null, nowMs - 1_000, nowMs - 900),
            [new SessionItemDto(0, "sess_shared", "turn_cur", "tool_result", sharedColdPayload, nowMs - 900)],
            updatedSession: null,
            ct);

        var blobDir = Path.Combine(harness.DataDir, "blobs", "trk_shared_blob");
        var referenced = Assert.Single(Directory.GetFiles(blobDir, "*.zst"));
        var orphan = Path.Combine(blobDir, new string('a', 64) + ".zst");
        File.Copy(referenced, orphan);

        var stats = await store.RunMaintenancePassAsync(nowMs, ct);
        Assert.Equal(1, stats.CompactedItems);
        Assert.Equal(1, stats.ReclaimedBlobs);
        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(orphan));

        // Epoch 3 item must still hydrate its full 80KB .zst payload!
        var items = await store.ListSessionItemsAsync("sess_shared", 0, 10, ct);
        var oldItem = items.Single(static i => i.TurnId == "turn_old");
        var curItem = items.Single(static i => i.TurnId == "turn_cur");
        Assert.True(oldItem.Payload.GetProperty("compacted").GetBoolean());
        Assert.Equal(sharedPayloadText, curItem.Payload.GetProperty("stdout").GetString());
    }
}
