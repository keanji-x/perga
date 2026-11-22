using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class TrackScheduleStoreTests
{
    private static async Task<StorageTestHarness> SeedAsync()
    {
        var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        return h;
    }

    private static TrackScheduleDto Clock(string id = "flow:pulse") => new(
        id, "trk_1", TrackScheduleSources.Template, TrackScheduleKinds.Cron,
        "Pulse", "Check progress", "0 9 * * *", "UTC", null, 1000, null,
        2, 0, TrackScheduleStates.Active, 500, 500, NodeKey: "pulse");

    [Fact]
    public async Task DueFlowClockSelectionIgnoresStandaloneHistoricalRowsAndFencesRetries()
    {
        await using var h = await SeedAsync();
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync("""
            INSERT INTO track_schedules (id, track_id, source_kind, schedule_kind, title, prompt, timezone,
                run_at_ms, next_fire_at_ms, state, created_at_ms, updated_at_ms)
            VALUES ('legacy', 'trk_1', 'agent', 'once', 'Legacy', 'Legacy', 'UTC', 100, 100, 'active', 1, 1);
            """);
        var track = (await h.Store.GetTrackAsync("trk_1", default))!;
        var clock = Clock();
        await h.Store.CommitTrackFlowAsync(track with { FlowRev = 1, FlowSpecJson = """{"rev":1,"nodes":[]}""" }, 0, [], [clock], [], [], default);
        var clocks = h.Store.TrackSchedules!;
        Assert.Empty(await clocks.ListDueFlowClocksAsync(999, default));
        var due = Assert.Single(await clocks.ListDueFlowClocksAsync(1000, default));
        Assert.Equal(clock.Id, due.ScheduleId);
        Assert.Equal(1000, due.DueAtMs);
        var next = clock with { FiredCount = 1, NextFireAtMs = 2000, LastFiredAtMs = 1000 };
        var evt = StorageTestHarness.CreateEvent("track.tasks.updated", track.Id, track.AreaId);
        Assert.True(await clocks.CommitFlowFireAsync(next, 1000, null, evt, default));
        Assert.False(await clocks.CommitFlowFireAsync(next, 1000, null, evt, default));
        Assert.Empty(await clocks.ListDueFlowClocksAsync(1000, default));
        Assert.Equal(0, (await clocks.GetAsync(track.Id, "legacy", default))!.FiredCount);
        Assert.Empty(await h.Store.TrackWakes.ListAsync(track.Id, default));
    }

    [Fact]
    public async Task TemplateFlowUpdateRollsBackAtomicallyAndRefreshPreservesClockProgress()
    {
        await using var h = await SeedAsync();
        var track = (await h.Store.GetTrackAsync("trk_1", default))!;
        var evt = StorageTestHarness.CreateEvent("track.updated", track.Id, track.AreaId);
        var template = track with { TemplateId = "tpl", FlowRev = 1, FlowSpecJson = """{"rev":1,"nodes":[]}""" };
        var clock = Clock();
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync("CREATE TRIGGER reject_clock BEFORE INSERT ON track_schedules BEGIN SELECT RAISE(ABORT, 'clock failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => h.Store.UpdateTrackTemplateAndEmitAsync(template,
            "snapshot", evt, default, flow: new(0, [], [clock])));
        var before = (await h.Store.LoadTrackAggregateAsync(track.Id, default))!;
        Assert.Equal(track.TemplateId, before.Track.TemplateId);
        Assert.Equal(0, before.Track.FlowRev);
        Assert.Null(before.TemplateSnapshotSource);
        await db.ExecuteAsync("DROP TRIGGER reject_clock;");

        await h.Store.UpdateTrackTemplateAndEmitAsync(template, "snapshot", evt, default, flow: new(0, [], [clock]));
        var clocks = h.Store.TrackSchedules!;
        var fired = clock with { FiredCount = 1, NextFireAtMs = 2000, LastFiredAtMs = 1000 };
        Assert.True(await clocks.CommitFlowFireAsync(fired, 1000, null, evt, default));
        await h.Store.UpdateTrackTemplateAndEmitAsync(template, "snapshot", evt, default,
            flow: new(1, [], [clock with { Prompt = "Changed" }]));
        var refreshed = (await clocks.GetAsync(track.Id, clock.Id, default))!;
        Assert.Equal("Changed", refreshed.Prompt);
        Assert.Equal(1, refreshed.FiredCount);
        Assert.Equal(2000, refreshed.NextFireAtMs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeFromV17CreatesTrackSchedulesAndRollsBackOnFailure(bool failUpgrade)
    {
        var directory = Directory.CreateTempSubdirectory("perga-schedule-upgrade-");
        var path = Path.Combine(directory.FullName, "v17.db");
        try
        {
            var original = SqliteMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 17).ToArray();
            await new SqliteMigrator(original).MigrateAsync(path);
            await using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
            await conn.OpenAsync();
            await conn.ExecuteAsync("""
                INSERT INTO areas (id, name, color, created_at_ms, updated_at_ms) VALUES ('area', 'Area', '#000000', 1, 1);
                INSERT INTO tracks (id, area_id, cwd, created_at_ms, updated_at_ms) VALUES ('track', 'area', '/tmp', 1, 1);
                """);
            if (failUpgrade)
            {
                await conn.ExecuteAsync("CREATE TRIGGER reject_v18 BEFORE INSERT ON schema_migrations WHEN NEW.version = 18 BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => new SqliteMigrator().MigrateAsync(path));
                Assert.Equal(17L, await conn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;"));
                Assert.Equal(0L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name='track_schedules';"));
                await conn.ExecuteAsync("DROP TRIGGER reject_v18;");
            }

            await new SqliteMigrator().MigrateAsync(path);
            await new SqliteMigrator().MigrateAsync(path);
            Assert.Equal(25L, await conn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(17L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('track_schedules');"));
            Assert.Equal("Area", await conn.ExecuteScalarAsync<string>("SELECT name FROM areas WHERE id='area';"));
            Assert.Equal("area", await conn.ExecuteScalarAsync<string>("SELECT area_id FROM tracks WHERE id='track';"));
            Assert.Equal("/tmp", await conn.ExecuteScalarAsync<string>("SELECT cwd FROM tracks WHERE id='track';"));
            foreach (var migration in original)
                Assert.Equal(migration.ChecksumSha256, await conn.ExecuteScalarAsync<string>(
                    "SELECT checksum_sha256 FROM schema_migrations WHERE version = @Version AND name = @Name;", migration));
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(recursive: true); }
    }
}
