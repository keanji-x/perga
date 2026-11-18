using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class TrackTemplatePersistenceTests
{
    [Fact]
    public async Task TemplateUpdate_PersistsSnapshotFieldsAndEventAcrossReopen_OrdinaryUpdatesPreserveSnapshot()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var store = harness.Store;
        await store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await store.CreateTrackAsync(track, report, "old source", lead, default);
        var updated = track with
        {
            TemplateId = "custom",
            TemplateInputs = StorageTestHarness.Json("""{"input":"value"}"""),
            TemplateState = StorageTestHarness.Json("""{"initialized":true}"""),
            EnabledPlugins = ["existing", "custom"],
            UpdatedAtMs = 5000
        };
        const string source = "export default defineTemplate({id:'custom',title:'Custom'});";
        var evt = StorageTestHarness.CreateEvent("track.updated", track.Id, track.AreaId, createdAtMs: 5000);
        await store.UpdateTrackTemplateAndEmitAsync(updated, source, evt, default);
        await store.UpdateTrackAndEmitAsync(updated with { Title = "Renamed" }, null, default);
        await store.DisposeAsync();

        await using var reopened = new SqliteTrackStore(new SqliteStorageOptions { DbPath = harness.DbPath, DataDir = harness.DataDir });
        await reopened.InitializeAsync();
        var snapshot = Assert.IsType<Perga.Kernel.Ports.TrackAggregateSnapshot>(await reopened.LoadTrackAggregateAsync(track.Id, default));
        Assert.Equal(source, snapshot.TemplateSnapshotSource);
        Assert.Equal("custom", snapshot.Track.TemplateId);
        Assert.Equal("Renamed", snapshot.Track.Title);
        Assert.Equal("value", snapshot.Track.TemplateInputs.GetProperty("input").GetString());
        Assert.True(snapshot.Track.TemplateState.GetProperty("initialized").GetBoolean());
        Assert.Equal(["existing", "custom"], snapshot.Track.EnabledPlugins);
        Assert.Equal(5000, snapshot.Track.UpdatedAtMs);
        Assert.Single(await reopened.ListEventsSinceAsync(0, track.Id), e => e.Kind == "track.updated");
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
