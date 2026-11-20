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
        var c2Wake = Assert.Single(wakesAfterCancel, w => w.CoalesceKey == TrackWakeRequests.TrackScheduleKey("sch_c2"));
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
    public async Task TemplateChangeAndCancelIsolateDynamicVsTemplateWakesAndTracksShareSemanticIds()
    {
        await using var h = await SeedAsync();
        // Same semantic id "daily-review" on both a dynamic schedule and a template wake on trk_1.
        await h.Store.TrackSchedules!.UpsertAsync(CronSchedule("daily-review", nextFireAtMs: 1_000, maxOccurrences: 5), default);
        await h.Store.TrackSchedules!.FireDueAsync(1_100, _ => 5_000, default);
        await h.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Schedule("trk_1", "daily-review"), default);

        // Updating an active schedule by id preserves fired_count, last_fired_at_ms, and created_at_ms.
        var updated = await h.Store.TrackSchedules!.UpsertAsync(
            CronSchedule("daily-review", nextFireAtMs: 5_000, maxOccurrences: 5) with { Title = "Updated title", Prompt = "Updated prompt", CreatedAtMs = 9_999 },
            default);
        Assert.Equal(1, updated.FiredCount);
        Assert.Equal(1_100, updated.LastFiredAtMs);
        Assert.Equal(500, updated.CreatedAtMs);
        Assert.Equal("Updated title", updated.Title);

        // Shrinking maxOccurrences at or below firedCount fails fast.
        var shrinkErr = await Assert.ThrowsAsync<KernelException>(() =>
            h.Store.TrackSchedules!.UpsertAsync(
                CronSchedule("daily-review", nextFireAtMs: 5_000, maxOccurrences: 1), default));
        Assert.Equal(KernelErrorCode.InvalidArgument, shrinkErr.Code);

        // Template change drops ONLY template schedule wake ("schedule:daily-review"), preserving "track_schedule:daily-review".
        var track = await h.Store.GetTrackAsync("trk_1", default);
        await h.Store.UpdateTrackTemplateAndEmitAsync(
            track! with { TemplateId = "new_tpl", UpdatedAtMs = 2_000 },
            "export default defineTemplate({ id: \"new_tpl\", title: \"New\" });",
            StorageTestHarness.CreateEvent("track.updated", track.Id, track.AreaId),
            default);

        var wakes = await h.Store.TrackWakes.ListAsync("trk_1", default);
        var durableWake = Assert.Single(wakes, w => w.CoalesceKey == TrackWakeRequests.TrackScheduleKey("daily-review"));
        var templateWake = Assert.Single(wakes, w => w.CoalesceKey == TrackWakeRequests.ScheduleKey("daily-review"));
        Assert.Equal(TrackWakeStates.Pending, durableWake.State);
        Assert.Equal(TrackWakeStates.Dropped, templateWake.State);
        Assert.Equal(TrackWakeReasons.TemplateChanged, templateWake.Reason);

        // Clearing a template (templateSnapshot = null) also drops non-track_schedule wakes (including keys like "track-schedule:x"
        // that would falsely match an unescaped SQLite LIKE wildcard) while preserving "track_schedule:daily-review".
        await h.Store.TrackWakes.EnqueueAsync(
            TrackWakeRequests.Prompt("trk_1", TrackWakeKinds.Schedule, "track-schedule:dash", "Template-like dash key"), default);
        await h.Store.UpdateTrackTemplateAndEmitAsync(
            track! with { TemplateId = null, UpdatedAtMs = 2_500 },
            null,
            StorageTestHarness.CreateEvent("track.updated", track.Id, track.AreaId),
            default);
        var wakesAfterClear = await h.Store.TrackWakes.ListAsync("trk_1", default);
        Assert.Equal(TrackWakeStates.Pending,
            Assert.Single(wakesAfterClear, w => w.CoalesceKey == TrackWakeRequests.TrackScheduleKey("daily-review")).State);
        Assert.Equal(TrackWakeStates.Dropped,
            Assert.Single(wakesAfterClear, w => w.CoalesceKey == "track-schedule:dash").State);

        // Canceling dynamic schedule "daily-review" drops ONLY its track_schedule wake, not a fresh template wake of the same id.
        await h.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Schedule("trk_1", "daily-review"), default);
        Assert.True(await h.Store.TrackSchedules!.CancelAsync("trk_1", "daily-review", default));
        var wakesAfterCancel = await h.Store.TrackWakes.ListAsync("trk_1", default);
        Assert.Equal(TrackWakeStates.Dropped,
            Assert.Single(wakesAfterCancel, w => w.CoalesceKey == TrackWakeRequests.TrackScheduleKey("daily-review")).State);
        Assert.Contains(wakesAfterCancel, w =>
            w.CoalesceKey == TrackWakeRequests.ScheduleKey("daily-review") && w.State == TrackWakeStates.Pending);

        // Re-arming a canceled schedule resets fired_count, last_fired_at_ms, created_at_ms, and returns to active.
        var rearmed = await h.Store.TrackSchedules!.UpsertAsync(
            CronSchedule("daily-review", nextFireAtMs: 8_000, maxOccurrences: 2) with { CreatedAtMs = 7_777 }, default);
        Assert.Equal(TrackScheduleStates.Active, rearmed.State);
        Assert.Equal(0, rearmed.FiredCount);
        Assert.Null(rearmed.LastFiredAtMs);
        Assert.Equal(7_777, rearmed.CreatedAtMs);

        // CancelActiveForTrackAsync cancels track schedules and drops only track_schedule:* wakes (template wake stays pending).
        await h.Store.TrackSchedules!.FireDueAsync(8_100, _ => 9_000, default);
        Assert.Equal(1, await h.Store.TrackSchedules!.CancelActiveForTrackAsync("trk_1", default));
        var wakesAfterBulkCancel = await h.Store.TrackWakes.ListAsync("trk_1", default);
        Assert.Contains(wakesAfterBulkCancel, w =>
            w.CoalesceKey == TrackWakeRequests.ScheduleKey("daily-review") && w.State == TrackWakeStates.Pending);

        // A second Track can independently use the exact same semantic id "daily-review",
        // and pruning old canceled "daily-review" on trk_1 must NOT delete trk_2's active "daily-review".
        var (track2, track2Report, track2Lead) = StorageTestHarness.CreateSeedTrack("trk_2", leadSessionId: "ses_lead_2");
        await h.Store.CreateTrackAsync(track2, track2Report, null, track2Lead, default);
        var track2Sched = await h.Store.TrackSchedules!.UpsertAsync(
            OnceSchedule("daily-review", runAtMs: 3_000, trackId: "trk_2"), default);
        Assert.Equal("trk_2", track2Sched.TrackId);
        Assert.Equal("daily-review", track2Sched.Id);

        var deleted = await h.Store.PruneTrackSchedulesAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 100L * 86_400_000L, default);
        Assert.Equal(1, deleted);
        Assert.Null(await h.Store.TrackSchedules!.GetAsync("trk_1", "daily-review", default));
        Assert.NotNull(await h.Store.TrackSchedules!.GetAsync("trk_2", "daily-review", default));
    }
}
