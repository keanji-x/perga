using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class ReadReceiptTests
{
    [Fact]
    public async Task MigrationBaselineAndMonotonicReceiptsSurviveReopen()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var h = await StorageTestHarness.CreateAsync();
        var initial = await h.Store.GetReadReceiptsAsync();
        Assert.InRange(initial.BaselineMs, before - 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Empty(initial.Receipts);
        Assert.True((await h.Store.MarkUnreadAsync("conversation", "s")).ManualUnread);
        Assert.Equal(50, (await h.Store.MarkReadAsync("conversation", "s", 50)).ReadThroughMs);
        var unread = await h.Store.MarkUnreadAsync("conversation", "s");
        Assert.Equal(50, unread.ReadThroughMs);
        Assert.True(unread.ManualUnread);
        var older = await h.Store.MarkReadAsync("conversation", "s", 10);
        Assert.Equal(50, older.ReadThroughMs);
        Assert.False(older.ManualUnread);
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => h.Store.MarkReadAsync("track", "t", i)));
        await using var reopened = await SqliteTrackStore.OpenAsync(h.DbPath, h.DataDir);
        var state = await reopened.GetReadReceiptsAsync();
        Assert.Equal(initial.BaselineMs, state.BaselineMs);
        Assert.Contains(older, state.Receipts);
        Assert.Equal(20, Assert.Single(state.Receipts, r => r.Kind == "track").ReadThroughMs);
    }

    [Fact]
    public async Task UpgradeFrom19SeedsBaselineWithoutChangingHistoricalTurns()
    {
        var dir = Directory.CreateTempSubdirectory("perga-upgrade20-");
        var db = Path.Combine(dir.FullName, "test.db");
        try
        {
            var migrations = SqliteMigrator.LoadEmbeddedMigrations();
            await new SqliteMigrator(migrations.Where(m => m.Version <= 19).ToArray()).MigrateAsync(db);
            await using var conn = new SqliteConnection($"Data Source={db};Pooling=False");
            await conn.OpenAsync();
            await conn.ExecuteAsync("""
                INSERT INTO areas(id, name, created_at_ms, updated_at_ms) VALUES ('a', 'area', 1, 1);
                INSERT INTO tracks(id, area_id, cwd, created_at_ms, updated_at_ms) VALUES ('t', 'a', '/tmp', 1, 1);
                INSERT INTO agent_sessions(id, track_id, provider, status, created_at_ms, updated_at_ms)
                VALUES ('s', 't', 'codex', 'idle', 1, 1);
                INSERT INTO session_turns(id, session_id, turn_index, trigger_kind, status, completed_at_ms)
                VALUES ('turn', 's', 1, 'user', 'completed', 2);
                """);
            var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await new SqliteMigrator().MigrateAsync(db);
            var baseline = await conn.ExecuteScalarAsync<long>("SELECT baseline_ms FROM read_receipt_meta WHERE id=1;");
            Assert.InRange(baseline, before - 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT completed_at_ms FROM session_turns WHERE id='turn';"));
            Assert.True(baseline > 2);
            await Assert.ThrowsAsync<SqliteException>(() => conn.ExecuteAsync("INSERT INTO read_receipt_meta VALUES (2, 0);"));
            await Assert.ThrowsAsync<SqliteException>(() => conn.ExecuteAsync("INSERT INTO read_receipts VALUES ('invalid', 's', 0, 0, 1);"));
            await Assert.ThrowsAsync<SqliteException>(() => conn.ExecuteAsync("INSERT INTO read_receipts VALUES ('track', 't', -1, 0, 1);"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task TaskLeaseExcludesWorkerWatermarksUntilTheConversationIsReleased()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var worker = lead with { Id = "worker", IsLead = false, Status = "unknown" };
        await h.Store.UpsertSessionAsync(worker, null, default);
        await using var conn = h.OpenRawInspectConnection();
        await conn.ExecuteAsync("""
            INSERT INTO tasks(id, track_id, block_id, task_key, kind, goal_or_cmd, status, session_id, run_count, started_at_ms, created_at_ms, updated_at_ms)
            VALUES ('task', @TrackId, 'block', 'task', 'subagent', 'test', 'running', 'worker', 1, 1, 1, 1);
            """, new { TrackId = track.Id });
        await h.Store.RecordTurnAndItemsAsync(new SessionTurnDto("worker-turn", worker.Id, 1, 0, "user",
            StorageTestHarness.Json("{}"), "failed", null, null, 3000, 5000), [], null, default);
        var active = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.True(active.HasBlockedSession);
        Assert.True(active.IsWorking);
        Assert.Empty(active.NotifiableSessions!);
        Assert.Equal(5000, Assert.Single((await h.Store.LoadTrackAggregateAsync(track.Id, default))!.Sessions, s => s.Id == worker.Id).LastNotifiableTurnAtMs);
        await conn.ExecuteAsync("UPDATE tasks SET status='failed', finished_at_ms=6000;");
        var finished = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.True(finished.HasBlockedSession);
        Assert.False(finished.IsWorking);
        Assert.False(finished.WorkspaceFailed);
        Assert.Equal(5000, Assert.Single(finished.NotifiableSessions!).LastNotifiableTurnAtMs);

        await h.Store.UpsertSessionAsync(worker with { ArchivedAtMs = 7000 }, null, default);
        var archived = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.False(archived.HasBlockedSession);
        Assert.Empty(archived.NotifiableSessions!);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(3, true, false)]
    [InlineData(4, false, true)]
    [InlineData(5, false, true)]
    [InlineData(6, false, false)]
    [InlineData(7, true, false)]
    public async Task WorkspacePhasesUseIndependentOverlayFields(int phase, bool working, bool failed)
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        await using var conn = h.OpenRawInspectConnection();
        await conn.ExecuteAsync("INSERT INTO track_workspace_preparations VALUES (@Id, @Intent);",
            new { track.Id, Intent = $"{{\"Phase\":{phase}}}" });
        var overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.Equal(working, overlay.IsWorking);
        Assert.Equal(failed, overlay.WorkspaceFailed);
        await h.Store.UpdateTrackAndEmitAsync(track with { ClosedAtMs = 6000 }, null, default);
        var closed = Assert.Single(await h.Store.ListTrackOverlaysAsync(true));
        Assert.False(closed.IsWorking);
        Assert.False(closed.WorkspaceFailed);
    }

    [Theory]
    [InlineData("completed", 5000L)]
    [InlineData("FAILED", 5000L)]
    [InlineData("interrupted", null)]
    [InlineData("unknown", null)]
    public async Task WatermarkUsesOnlyNotifiableTerminalTurns(string status, long? expected)
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        await h.Store.RecordTurnAndItemsAsync(new SessionTurnDto("turn", lead.Id, 1, 0, "user",
            StorageTestHarness.Json("{}"), status, null, null, 3000, 5000), [], null, default);
        var aggregate = await h.Store.LoadTrackAggregateAsync(track.Id, default);
        Assert.Equal(expected, Assert.Single(aggregate!.Sessions).LastNotifiableTurnAtMs);
        var overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        if (expected is null) Assert.Empty(overlay.NotifiableSessions!);
        else Assert.Equal(expected, Assert.Single(overlay.NotifiableSessions!).LastNotifiableTurnAtMs);
        await h.Store.UpsertSessionAsync(lead with { Title = "Renamed", UpdatedAtMs = 9000 }, null, default);
        Assert.Equal(expected, Assert.Single((await h.Store.LoadTrackAggregateAsync(track.Id, default))!.Sessions).LastNotifiableTurnAtMs);
        await h.Store.UpsertSessionAsync(lead with { ArchivedAtMs = 10000 }, null, default);
        Assert.Empty(Assert.Single(await h.Store.ListTrackOverlaysAsync()).NotifiableSessions!);
    }

    [Theory]
    [InlineData("queued", true, false)]
    [InlineData("running", true, false)]
    [InlineData("stopping", true, false)]
    [InlineData("compacting", true, false)]
    [InlineData("unknown", false, true)]
    [InlineData("idle", false, false)]
    public async Task OverlaySessionStatesAndClosedTrack(string status, bool working, bool blocked)
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead with { Status = status }, default);
        await h.Store.RecordTurnAndItemsAsync(new SessionTurnDto("turn", lead.Id, 1, 0, "user",
            StorageTestHarness.Json("{}"), "completed", null, null, 3000, 5000), [], null, default);
        await using var conn = h.OpenRawInspectConnection();
        await conn.ExecuteAsync("INSERT INTO asks(id, track_id, status, questions_json, created_at_ms) VALUES ('ask', @TrackId, 'open', '[]', 1);", new { TrackId = track.Id });
        var open = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.Equal(1, open.OpenAskCount);
        Assert.Equal(working, open.IsWorking);
        Assert.Equal(blocked, open.HasBlockedSession);
        await h.Store.UpdateTrackAndEmitAsync(track with { ClosedAtMs = 6000 }, null, default);
        var closed = Assert.Single(await h.Store.ListTrackOverlaysAsync(true));
        Assert.False(closed.IsWorking);
        Assert.False(closed.HasBlockedSession);
        Assert.False(closed.WorkspaceFailed);
        Assert.Equal(0, closed.OpenAskCount);
        Assert.Equal(0, closed.PendingApprovalCount);
        Assert.Single(closed.NotifiableSessions!);
    }
}
