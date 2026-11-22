using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class TrackTemplatePersistenceTests
{
    [Fact]
    public async Task Migration22AddsEmptyDataAndPreservesExistingTemplateState()
    {
        var directory = Directory.CreateTempSubdirectory("perga-track-data-migration-");
        var db = Path.Combine(directory.FullName, "test.db");
        try
        {
            var migrations = SqliteMigrator.LoadEmbeddedMigrations();
            await new SqliteMigrator(migrations.Where(m => m.Version <= 21).ToArray()).MigrateAsync(db);
            await using var connection = new SqliteConnection($"Data Source={db};Pooling=False");
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO areas(id,name,created_at_ms,updated_at_ms) VALUES('a','area',1,1);
                INSERT INTO tracks(id,area_id,cwd,created_at_ms,updated_at_ms,template_state_json)
                    VALUES('t','a','/tmp',1,1,'{"previousWork":42}');
                """);
            await new SqliteMigrator().MigrateAsync(db);
            var row = await connection.QuerySingleAsync<(string State, string Data, long Rev)>(
                "SELECT template_state_json,data_json,data_rev FROM tracks WHERE id='t';");
            Assert.Equal(("""{"previousWork":42}""", "{}", 0L), row);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task TemplateUpdate_PersistsSnapshotFieldsAndEventAcrossReopen_OrdinaryUpdatesPreserveSnapshot()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        await store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await store.CreateTrackAsync(track, report, "old source", lead, default);
        using (var conn = harness.OpenRawInspectConnection())
        {
            Assert.Equal("{}", await conn.QuerySingleAsync<string>("SELECT template_state_json FROM tracks WHERE id = @Id", new { track.Id }));
            await conn.ExecuteAsync("UPDATE tracks SET template_state_json = 'unused historical data' WHERE id = @Id", new { track.Id });
        }

        var updated = track with
        {
            TemplateId = "custom",
            TemplateInputs = StorageTestHarness.Json("""{"input":"value"}"""),
            Data = StorageTestHarness.Json("""{"research":42}"""),
            DataRev = 1,
            EnabledPlugins = ["existing", "custom"],
            UpdatedAtMs = 5000
        };
        const string source = "export default defineTemplate({id:'custom',title:'Custom'});";
        var evt = StorageTestHarness.CreateEvent("track.updated", track.Id, track.AreaId, createdAtMs: 5000);
        await store.UpdateTrackTemplateAndEmitAsync(updated, source, evt, default, writeData: true);
        await store.UpdateTrackAndEmitAsync(track with { Title = "Renamed", TemplateId = updated.TemplateId, TemplateInputs = updated.TemplateInputs, EnabledPlugins = updated.EnabledPlugins, UpdatedAtMs = updated.UpdatedAtMs }, null, default);
        await store.DisposeAsync();

        await using var reopened = new SqliteTrackStore(new SqliteStorageOptions { DbPath = harness.DbPath, DataDir = harness.DataDir });
        await reopened.InitializeAsync();
        var snapshot = Assert.IsType<Perga.Kernel.Ports.TrackAggregateSnapshot>(await reopened.LoadTrackAggregateAsync(track.Id, default));
        Assert.Equal(source, snapshot.TemplateSnapshotSource);
        Assert.Equal("custom", snapshot.Track.TemplateId);
        Assert.Equal("Renamed", snapshot.Track.Title);
        Assert.Equal("value", snapshot.Track.TemplateInputs.GetProperty("input").GetString());
        Assert.Equal(42, snapshot.Track.Data.GetProperty("research").GetInt32());
        Assert.Equal(1, snapshot.Track.DataRev);
        Assert.Equal(["existing", "custom"], snapshot.Track.EnabledPlugins);
        Assert.Equal(5000, snapshot.Track.UpdatedAtMs);
        Assert.Single(await reopened.ListEventsSinceAsync(0, track.Id), e => e.Kind == "track.updated");
        using var persisted = harness.OpenRawInspectConnection();
        Assert.Equal("unused historical data", await persisted.QuerySingleAsync<string>(
            "SELECT template_state_json FROM tracks WHERE id = @Id", new { track.Id }));

    }

    [Fact]
    public async Task TemplateUpdate_WhenEventWriteFails_RollsBackSnapshotAndTrack()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        await harness.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await harness.Store.CreateTrackAsync(track, report, "original", lead, default);
        using var conn = harness.OpenRawInspectConnection();
        await conn.ExecuteAsync("""
            CREATE TRIGGER reject_template_event BEFORE INSERT ON events
            BEGIN SELECT RAISE(ABORT, 'event failure'); END;
            """);
        var evt = StorageTestHarness.CreateEvent("track.updated", track.Id, track.AreaId);
        await Assert.ThrowsAsync<SqliteException>(() => harness.Store.UpdateTrackTemplateAndEmitAsync(
            track with { TemplateId = "replacement", EnabledPlugins = ["replacement"] }, "replacement source", evt, default));
        var snapshot = (await harness.Store.LoadTrackAggregateAsync(track.Id, default))!;
        Assert.Equal("original", snapshot.TemplateSnapshotSource);
        Assert.Equal(track.TemplateId, snapshot.Track.TemplateId);
        Assert.Equal(track.EnabledPlugins, snapshot.Track.EnabledPlugins);
        Assert.Empty(await harness.Store.ListEventsSinceAsync(0, track.Id));
    }
}
