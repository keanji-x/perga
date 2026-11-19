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

    private static TrackScheduleDto OnceSchedule(string id = "sch_1", long runAtMs = 1_000, string trackId = "trk_1") => new(
        Id: id,
        TrackId: trackId,
        TargetSessionId: null,
        SourceKind: TrackScheduleSources.Agent,
        ScheduleKind: TrackScheduleKinds.Once,
        Title: "Reminder",
        Prompt: "Check build status",
        CronExpr: null,
        Timezone: "Etc/UTC",
        RunAtMs: runAtMs,
        NextFireAtMs: runAtMs,
        LastFiredAtMs: null,
        MaxOccurrences: 1,
        FiredCount: 0,
        CoalesceKey: null,
        State: TrackScheduleStates.Active,
        CreatedAtMs: 500,
        UpdatedAtMs: 500);

    private static TrackScheduleDto CronSchedule(string id = "sch_cron", long nextFireAtMs = 1_000, int? maxOccurrences = null) => new(
        Id: id,
        TrackId: "trk_1",
        TargetSessionId: null,
        SourceKind: TrackScheduleSources.Agent,
        ScheduleKind: TrackScheduleKinds.Cron,
        Title: "Standup",
        Prompt: "Prepare daily summary",
        CronExpr: "0 9 * * *",
        Timezone: "Etc/UTC",
        RunAtMs: null,
        NextFireAtMs: nextFireAtMs,
        LastFiredAtMs: null,
        MaxOccurrences: maxOccurrences,
        FiredCount: 0,
        CoalesceKey: null,
        State: TrackScheduleStates.Active,
        CreatedAtMs: 500,
        UpdatedAtMs: 500);

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
            Assert.Equal(18L, await conn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(18L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM pragma_table_info('track_schedules');"));
            foreach (var migration in original)
                Assert.Equal(migration.ChecksumSha256, await conn.ExecuteScalarAsync<string>(
                    "SELECT checksum_sha256 FROM schema_migrations WHERE version = @Version AND name = @Name;", migration));
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task FireDueAtomicallyAdvancesSchedulesAndEnqueuesWake()
    {
        await using var h = await SeedAsync();
        await h.Store.TrackSchedules!.UpsertAsync(OnceSchedule("sch_once", runAtMs: 1_000), default);
        await h.Store.TrackSchedules!.UpsertAsync(CronSchedule("sch_cron", nextFireAtMs: 1_000, maxOccurrences: 2), default);

        var fired1 = await h.Store.TrackSchedules!.FireDueAsync(1_500, _ => 2_500, default);
        Assert.Equal(2, fired1.Count);

        var once = await h.Store.TrackSchedules!.GetAsync("trk_1", "sch_once", default);
        Assert.NotNull(once);
        Assert.Equal(TrackScheduleStates.Done, once!.State);
        Assert.Null(once.NextFireAtMs);
        Assert.Equal(1, once.FiredCount);

        var cron = await h.Store.TrackSchedules!.GetAsync("trk_1", "sch_cron", default);
        Assert.NotNull(cron);
        Assert.Equal(TrackScheduleStates.Active, cron!.State);
        Assert.Equal(2_500, cron.NextFireAtMs);
        Assert.Equal(1, cron.FiredCount);

        var wakes = await h.Store.TrackWakes.ListAsync("trk_1", default);
        Assert.Equal(2, wakes.Count);
        Assert.All(wakes, w =>
        {
            Assert.Equal(TrackWakeKinds.Schedule, w.Kind);
            Assert.Equal(TrackWakeStates.Pending, w.State);
        });

        var fired2 = await h.Store.TrackSchedules!.FireDueAsync(2_600, _ => 3_500, default);
        Assert.Single(fired2);
        var cronDone = await h.Store.TrackSchedules!.GetAsync("trk_1", "sch_cron", default);
        Assert.Equal(TrackScheduleStates.Done, cronDone!.State);
        Assert.Null(cronDone.NextFireAtMs);
        Assert.Equal(2, cronDone.FiredCount);
    }

    [Fact]
    public async Task CancelAndTrackCloseDropSchedulesAndPendingWakes()
    {
        await using var h = await SeedAsync();
        await h.Store.TrackSchedules!.UpsertAsync(OnceSchedule("sch_cancel", runAtMs: 1_000), default);
        await h.Store.TrackSchedules!.FireDueAsync(1_100, _ => null, default);
        // Re-schedule an active cron with a pending wake
        await h.Store.TrackSchedules!.UpsertAsync(CronSchedule("sch_c2", nextFireAtMs: 1_000), default);
        await h.Store.TrackSchedules!.FireDueAsync(1_200, _ => 5_000, default);
        Assert.True(await h.Store.TrackSchedules!.CancelAsync("trk_1", "sch_c2", default));
        Assert.False(await h.Store.TrackSchedules!.CancelAsync("trk_1", "sch_c2", default));

        var wakesAfterCancel = await h.Store.TrackWakes.ListAsync("trk_1", default);
        var c2Wake = Assert.Single(wakesAfterCancel, w => w.CoalesceKey == TrackWakeRequests.ScheduleKey("sch_c2"));
        Assert.Equal(TrackWakeStates.Dropped, c2Wake.State);
        Assert.Equal(TrackWakeReasons.Canceled, c2Wake.Reason);

        await h.Store.TrackSchedules!.UpsertAsync(CronSchedule("sch_open", nextFireAtMs: 2_000), default);
        var track = await h.Store.GetTrackAsync("trk_1", default);
        await h.Store.UpdateTrackAndEmitAsync(track! with { ClosedAtMs = 3_000, UpdatedAtMs = 3_000 }, null, default);

        var closedSchedule = await h.Store.TrackSchedules!.GetAsync("trk_1", "sch_open", default);
        Assert.Equal(TrackScheduleStates.Canceled, closedSchedule!.State);
        Assert.Null(closedSchedule.NextFireAtMs);

        await Assert.ThrowsAsync<KernelException>(() =>
            h.Store.TrackSchedules!.UpsertAsync(OnceSchedule("sch_after_close", runAtMs: 4_000), default));
    }

    [Fact]
    public async Task MaintenancePrunesOldTerminalSchedulesAndRetainsRecent()
    {
        await using var h = await SeedAsync();
        var oldMs = 1_000L;
        var recentMs = 95L * 86_400_000L;
        await h.Store.TrackSchedules!.UpsertAsync(OnceSchedule("sch_old", runAtMs: oldMs) with
        {
            State = TrackScheduleStates.Done,
            NextFireAtMs = null,
            UpdatedAtMs = oldMs
        }, default);
        await h.Store.TrackSchedules!.UpsertAsync(OnceSchedule("sch_recent", runAtMs: recentMs) with
        {
            State = TrackScheduleStates.Done,
            NextFireAtMs = null,
            UpdatedAtMs = recentMs
        }, default);

        var deleted = await h.Store.PruneTrackSchedulesAsync(100L * 86_400_000L, default);
        Assert.Equal(1, deleted);
        Assert.Null(await h.Store.TrackSchedules!.GetAsync("trk_1", "sch_old", default));
        Assert.NotNull(await h.Store.TrackSchedules!.GetAsync("trk_1", "sch_recent", default));
    }

    [Fact]
    public async Task TemplateChangePreservesDurableScheduleWakesAndRejectsCrossTrackId()
    {
        await using var h = await SeedAsync();
        await h.Store.TrackSchedules!.UpsertAsync(OnceSchedule("sch_shared", runAtMs: 1_000, trackId: "trk_1"), default);
        await h.Store.TrackSchedules!.FireDueAsync(1_100, _ => null, default);
        await h.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Schedule("trk_1", "tpl_only"), default);

        var track = await h.Store.GetTrackAsync("trk_1", default);
        await h.Store.UpdateTrackTemplateAndEmitAsync(
            track! with { TemplateId = "new_tpl", UpdatedAtMs = 2_000 },
            "export default defineTemplate({ id: \"new_tpl\", title: \"New\" });",
            StorageTestHarness.CreateEvent("track.updated", track.Id, track.AreaId),
            default);

        var wakes = await h.Store.TrackWakes.ListAsync("trk_1", default);
        var durableWake = Assert.Single(wakes, w => w.CoalesceKey == TrackWakeRequests.ScheduleKey("sch_shared"));
        var templateWake = Assert.Single(wakes, w => w.CoalesceKey == TrackWakeRequests.ScheduleKey("tpl_only"));
        Assert.Equal(TrackWakeStates.Pending, durableWake.State);
        Assert.Equal(TrackWakeStates.Dropped, templateWake.State);
        Assert.Equal(TrackWakeReasons.TemplateChanged, templateWake.Reason);

        var (track2, track2Report, track2Lead) = StorageTestHarness.CreateSeedTrack("trk_2", leadSessionId: "ses_lead_2");
        await h.Store.CreateTrackAsync(track2, track2Report, null, track2Lead, default);

        var conflict = await Assert.ThrowsAsync<KernelException>(() =>
            h.Store.TrackSchedules!.UpsertAsync(OnceSchedule("sch_shared", runAtMs: 3_000, trackId: "trk_2"), default));
        Assert.Equal(KernelErrorCode.Conflict, conflict.Code);
    }
}
