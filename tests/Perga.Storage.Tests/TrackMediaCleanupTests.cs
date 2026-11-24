using Dapper;

namespace Perga.Storage.Tests;

public sealed class TrackMediaCleanupTests
{
    [Fact]
    public async Task AreaCascadeContinuesPastBlobAndMediaCleanupFailures()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var h = await StorageTestHarness.CreateAsync();
        var area = StorageTestHarness.CreateArea();
        await h.Store.UpsertAreaAsync(area, default);
        var trackIds = new[] { "trk_blob_failure", "trk_media_failure", "trk_remaining" };
        foreach (var id in trackIds)
        {
            var (track, report, lead) = StorageTestHarness.CreateSeedTrack(id, leadSessionId: $"ses_{id}");
            await h.Store.CreateTrackAsync(track, report, null, lead, default);
            var dir = Directory.CreateDirectory(Path.Combine(h.DataDir, "media", id));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "file"), "image");
        }
        var blobs = Directory.CreateDirectory(Path.Combine(h.DataDir, "blobs", trackIds[0])).FullName;
        await File.WriteAllTextAsync(Path.Combine(blobs, "file"), "blob");
        var media = Path.Combine(h.DataDir, "media", trackIds[1]);
        var oldBlobMode = File.GetUnixFileMode(blobs);
        var oldMediaMode = File.GetUnixFileMode(media);
        var readOnly = UnixFileMode.UserRead | UnixFileMode.UserExecute;
        File.SetUnixFileMode(blobs, readOnly);
        File.SetUnixFileMode(media, readOnly);
        try
        {
            await h.Store.DeleteAreaAsync(area.Id, default);
            Assert.False(Directory.Exists(Path.Combine(h.DataDir, "media", trackIds[0])));
            Assert.False(Directory.Exists(Path.Combine(h.DataDir, "media", trackIds[2])));
            using var db = h.OpenRawInspectConnection();
            Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tracks"));
        }
        finally
        {
            if (Directory.Exists(blobs)) File.SetUnixFileMode(blobs, oldBlobMode);
            if (Directory.Exists(media)) File.SetUnixFileMode(media, oldMediaMode);
        }

        await using var reopened = await Perga.Storage.Sqlite.SqliteTrackStore.OpenAsync(
            h.DbPath, h.DataDir);
        Assert.False(Directory.Exists(media));
    }
}
