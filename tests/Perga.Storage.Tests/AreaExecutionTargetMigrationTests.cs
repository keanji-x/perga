using Perga.Contracts;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class AreaExecutionTargetMigrationTests
{
    [Fact]
    public async Task AreaAndTrackExecutionTarget_PersistAcrossRestart_AndTrackPreservesOwnTargetWhenAreaChanges()
    {
        await using var harness = await StorageTestHarness.CreateAsync();

        var jasperArea = StorageTestHarness.CreateArea(id: "area_jasper", name: "Jasper Work") with
        {
            ExecutionTargetId = "jasper"
        };
        await harness.Store.UpsertAreaAsync(jasperArea, CancellationToken.None);

        var loadedArea = await harness.Store.GetAreaAsync("area_jasper", CancellationToken.None);
        Assert.NotNull(loadedArea);
        Assert.Equal("jasper", loadedArea!.ExecutionTargetId);

        // Create a Track bound to jasper inside this Area.
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack(
            trackId: "trk_jasper_1",
            areaId: "area_jasper",
            leadSessionId: "ses_jasper_lead");
        await harness.Store.CreateTrackAsync(
            track with { ExecutionTargetId = "jasper" },
            report,
            null,
            lead,
            CancellationToken.None);

        // Updating the Area's default target updates the Area for future Tracks,
        // while existing Tracks keep their own bound executionTargetId.
        await harness.Store.UpsertAreaAsync(
            loadedArea with { Name = "Jasper Renamed", ExecutionTargetId = "local", UpdatedAtMs = 2_000 },
            CancellationToken.None);

        var afterUpdate = await harness.Store.GetAreaAsync("area_jasper", CancellationToken.None);
        Assert.NotNull(afterUpdate);
        Assert.Equal("Jasper Renamed", afterUpdate!.Name);
        Assert.Equal("local", afterUpdate.ExecutionTargetId);

        var fetchedTrack = await harness.Store.GetTrackAsync("trk_jasper_1", CancellationToken.None);
        Assert.NotNull(fetchedTrack);
        Assert.Equal("jasper", fetchedTrack!.ExecutionTargetId);

        var listedTracks = await harness.Store.ListTracksAsync("area_jasper", includeClosed: true, CancellationToken.None);
        Assert.Single(listedTracks);
        Assert.Equal("jasper", listedTracks[0].ExecutionTargetId);

        // Terminal lookup maps terminal ID back to track ID.
        var term = new TerminalDto(
            Id: "term_jasper_1",
            TrackId: "trk_jasper_1",
            SessionId: "ses_jasper_lead",
            IdempotencyKey: "k1",
            Title: "bash",
            Program: "/bin/bash",
            Cwd: "/home/jasper/app",
            Status: TerminalStatus.Running,
            ExitCode: null,
            CreatedAtMs: 3_000,
            ExitedAtMs: null);
        await harness.Store.UpsertTerminalAsync(term, null, CancellationToken.None);

        Assert.Equal("trk_jasper_1", await harness.Store.FindTrackIdByTerminalAsync("term_jasper_1", CancellationToken.None));
        Assert.Null(await harness.Store.FindTrackIdByTerminalAsync("term_missing", CancellationToken.None));

        // Reopen the same SQLite database in a fresh store instance to verify restart durability.
        await using var reopened = new SqliteTrackStore(new SqliteStorageOptions
        {
            DbPath = harness.DbPath,
            DataDir = harness.DataDir
        });
        await reopened.InitializeAsync(CancellationToken.None);

        var reopenedArea = await reopened.GetAreaAsync("area_jasper", CancellationToken.None);
        Assert.NotNull(reopenedArea);
        Assert.Equal("local", reopenedArea!.ExecutionTargetId);

        var reopenedTrack = await reopened.GetTrackAsync("trk_jasper_1", CancellationToken.None);
        Assert.NotNull(reopenedTrack);
        Assert.Equal("jasper", reopenedTrack!.ExecutionTargetId);
        Assert.Equal("trk_jasper_1", await reopened.FindTrackIdByTerminalAsync("term_jasper_1", CancellationToken.None));
    }
}
