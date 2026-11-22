using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;

namespace Perga.Storage.Tests;

public sealed class SqliteColdBlobAndConcurrencyTests
{
    [Fact]
    public async Task ColdBlobOffload_Over64KbSessionItemAndGateVerdict_WritesZstEnvelopeHydratesAndCleansUpOnTrackDelete()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;

        await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_1"), ct);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack("trk_blob", "area_1", "sess_blob");
        await store.CreateTrackAsync(track, report, null, lead, ct);

        // Build >64KB UTF-8 payloads including multi-byte CJK & surrogate-pair emoji near the 2KB preview boundary
        var repeatedSegment = "Perga冷存储校验🚀Line-0123456789ABCDEF|";
        var bigOutputText = string.Concat(Enumerable.Repeat(repeatedSegment, 3_200)); // > 110 KB UTF-8
        var bigGateLogText = string.Concat(Enumerable.Repeat("GATE-LOG-PASS-9876543210-", 3_500)); // > 85 KB UTF-8

        var bigItemPayload = StorageTestHarness.Json(JsonSerializer.Serialize(new
        {
            callId = "call_99",
            stdout = bigOutputText,
            exitCode = 0
        }));

        var bigGatePayload = StorageTestHarness.Json(JsonSerializer.Serialize(new
        {
            passed = true,
            log = bigGateLogText
        }));

        var turn = new SessionTurnDto(
            "turn_blob_1", "sess_blob", 1, 1, "user",
            StorageTestHarness.Json("""{"prompt":"run"}"""),
            "completed", null, null, 1_000, 1_100);
        await store.RecordTurnAndItemsAsync(
            turn,
            [new SessionItemDto(0, "sess_blob", "turn_blob_1", "tool_result", bigItemPayload, 1_100)],
            updatedSession: null,
            ct);

        var task = new TaskDto(
            "task_blob", "trk_blob", "b_1", "verify", 1, "subagent", "codex", AccessMode.ReadWrite,
            true, [], "Verify", null, null, null, TaskRunStatus.Done, null, SemanticVerdict.None, null, 1_000, 1_200,
            SessionId: "sess_blob", RunCount: 1, Meta: StorageTestHarness.Json("{}"), GateVerdict: bigGatePayload, StartedAtMs: 1_000, FinishedAtMs: 1_200);
        await store.UpsertTaskAsync(task, null, ct);

        // 1. Verify SQLite B-Tree stores compact {"v":1,"blobRef":"blobs/trk_blob/<sha256>.zst","bytes":...,"preview":...}
        string rawItemSqlJson;
        string rawGateSqlJson;
        using (var rawConn = harness.OpenRawInspectConnection())
        {
            rawItemSqlJson = await rawConn.ExecuteScalarAsync<string>("SELECT payload_json FROM session_items LIMIT 1;") ?? string.Empty;
            rawGateSqlJson = await rawConn.ExecuteScalarAsync<string>("SELECT gate_verdict_json FROM tasks WHERE id = 'task_blob';") ?? string.Empty;
        }

        Assert.True(rawItemSqlJson.Length < 4_096, $"Expected compact SQLite envelope, got {rawItemSqlJson.Length} chars.");
        Assert.True(rawGateSqlJson.Length < 4_096, $"Expected compact SQLite envelope, got {rawGateSqlJson.Length} chars.");

        using var itemEnvDoc = JsonDocument.Parse(rawItemSqlJson);
        Assert.Equal(1, itemEnvDoc.RootElement.GetProperty("v").GetInt32());
        var itemBlobRef = itemEnvDoc.RootElement.GetProperty("blobRef").GetString()!;
        Assert.StartsWith("blobs/trk_blob/", itemBlobRef, StringComparison.Ordinal);
        Assert.EndsWith(".zst", itemBlobRef, StringComparison.Ordinal);
        Assert.True(itemEnvDoc.RootElement.GetProperty("bytes").GetInt64() > 65_536);

        var trackBlobDir = Path.Combine(harness.DataDir, "blobs", "trk_blob");
        Assert.Equal(2, Directory.GetFiles(trackBlobDir, "*.zst").Length);

        // 2. Verify transparent decompression on read via ListSessionItemsAsync & LoadTrackAggregateAsync
        var hydratedItems = await store.ListSessionItemsAsync("sess_blob", afterId: 0, limit: 10, ct: ct);
        var hydratedItem = Assert.Single(hydratedItems);
        Assert.Equal(bigOutputText, hydratedItem.Payload.GetProperty("stdout").GetString());

        var hydratedTask = Assert.Single((await store.LoadTrackAggregateAsync("trk_blob", ct))!.Tasks);
        Assert.Equal(bigGateLogText, hydratedTask.GateVerdict!.Value.GetProperty("log").GetString());

        // 3. Simulate pruned .zst blob: reader returns compact envelope JSON cleanly without throwing
        using var gateEnvDoc = JsonDocument.Parse(rawGateSqlJson);
        var gateBlobRef = gateEnvDoc.RootElement.GetProperty("blobRef").GetString()!;
        var gateBlobPath = Path.Combine(harness.DataDir, gateBlobRef.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(gateBlobPath);

        var fallbackTask = Assert.Single((await store.LoadTrackAggregateAsync("trk_blob", ct))!.Tasks);
        var fallbackGate = fallbackTask.GateVerdict!.Value;
        Assert.Equal(1, fallbackGate.GetProperty("v").GetInt32());
        Assert.Equal(gateBlobRef, fallbackGate.GetProperty("blobRef").GetString());

        // 4. DeleteTrackAsync performs O(1) directory cleanup of {blobsDir}/{trackId}
        await store.DeleteTrackAsync("trk_blob", ct);
        Assert.False(Directory.Exists(trackBlobDir));
    }

    [Fact]
    public async Task HighConcurrencyStress_80WritersAnd32SnapshotReaders_ZeroSqliteBusyAndExactBatchFaultIsolation()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        var ct = CancellationToken.None;

        await store.UpsertAreaAsync(StorageTestHarness.CreateArea("area_stress"), ct);
        const int trackCount = 4;
        for (var t = 0; t < trackCount; t++)
        {
            var (track, report, lead) = StorageTestHarness.CreateSeedTrack($"trk_{t}", "area_stress", $"sess_{t}");
            await store.CreateTrackAsync(track, report, "export default {};", lead, ct);
        }

        // 1. 80 parallel writer tasks x 20 operations + 32 concurrent snapshot reader loops -> 0 SQLITE_BUSY!
        const int writerTaskCount = 80;
        const int opsPerWriter = 20;
        const int readerTaskCount = 32;

        using var readerStopCts = new CancellationTokenSource();
        var readerExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var totalReadsCompleted = 0;

        var readers = Enumerable.Range(0, readerTaskCount).Select(r => Task.Run(async () =>
        {
            var rng = new Random(1000 + r);
            while (!readerStopCts.IsCancellationRequested)
            {
                try
                {
                    var targetTrack = $"trk_{rng.Next(trackCount)}";
                    var targetSess = $"sess_{rng.Next(trackCount)}";
                    _ = await store.LoadTrackAggregateAsync(targetTrack, CancellationToken.None);
                    _ = await store.ListSessionItemsAsync(targetSess, 0, 25, CancellationToken.None);
                    _ = await store.ListEventsSinceAsync(0, targetTrack, 50, CancellationToken.None);
                    Interlocked.Increment(ref totalReadsCompleted);
                }
                catch (Exception ex)
                {
                    readerExceptions.Add(ex);
                    break;
                }
            }
        })).ToArray();

        var writers = Enumerable.Range(0, writerTaskCount).Select(w => Task.Run(async () =>
        {
            var trackIndex = w % trackCount;
            var trackId = $"trk_{trackIndex}";
            var sessionId = $"sess_{trackIndex}";

            for (var i = 0; i < opsPerWriter; i++)
            {
                var seq = w * 1000 + i + 1;
                await store.AppendEventAsync(
                    StorageTestHarness.CreateEvent("stress.evt", trackId, "area_stress", $$"""{"w":{{w}},"i":{{i}}}""", 10_000 + seq),
                    ct);

                if (i % 4 == 0)
                {
                    var turnId = $"turn_w{w}_i{i}";
                    var turn = new SessionTurnDto(
                        turnId,
                        sessionId,
                        TurnIndex: 100_000 + seq,
                        Epoch: 1,
                        TriggerKind: "wake",
                        Input: StorageTestHarness.Json($$"""{"seq":{{seq}}}"""),
                        Status: "completed",
                        Usage: null,
                        ErrorText: null,
                        StartedAtMs: 10_000 + seq,
                        CompletedAtMs: 10_001 + seq);
                    await store.RecordTurnAndItemsAsync(
                        turn,
                        [new SessionItemDto(0, sessionId, turnId, "assistant_text", StorageTestHarness.Json("""{"ok":true}"""), 10_001 + seq)],
                        updatedSession: null,
                        ct);
                }
            }
        })).ToArray();

        await Task.WhenAll(writers);
        await readerStopCts.CancelAsync();
        await Task.WhenAll(readers);

        Assert.Empty(readerExceptions);
        Assert.True(totalReadsCompleted > 0);

        var persistedEvents = await store.ListEventsSinceAsync(0, limit: 10_000, ct: ct);
        Assert.Equal(writerTaskCount * opsPerWriter, persistedEvents.Count);

        // 2. Micro-batch fault isolation: valid sibling writes in the same batch succeed when peers violate FK constraints!
        var validTasks = Enumerable.Range(0, 24).Select(k =>
            store.AppendEventAsync(
                StorageTestHarness.CreateEvent("batch.valid", "trk_0", "area_stress", $$"""{"k":{{k}}}""", 90_000 + k),
                ct)).ToArray();

        var badSessionTemplate = new AgentSessionDto(
            Id: "bad_sess",
            TrackId: "trk_nonexistent_fk_violation",
            IsLead: false,
            Title: null,
            Provider: "codex",
            Model: null,
            NativeThreadId: null,
            Epoch: 1,
            Status: "idle",
            ArchivedAtMs: null,
            CreatedAtMs: 90_000,
            UpdatedAtMs: 90_000);

        var invalidTasks = Enumerable.Range(0, 4).Select(b =>
            Assert.ThrowsAsync<SqliteException>(() =>
                store.UpsertSessionAsync(badSessionTemplate with { Id = $"bad_sess_{b}" }, evt: null, ct))).ToArray();

        await Task.WhenAll(validTasks);
        var fkErrors = await Task.WhenAll(invalidTasks);
        Assert.All(fkErrors, static ex => Assert.Equal(19, ex.SqliteErrorCode)); // SQLITE_CONSTRAINT == 19

        var totalAfterMixedBatch = await store.ListEventsSinceAsync(0, limit: 10_000, ct: ct);
        Assert.Equal(writerTaskCount * opsPerWriter + 24, totalAfterMixedBatch.Count);
    }
}
