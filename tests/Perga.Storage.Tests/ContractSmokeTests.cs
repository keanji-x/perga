using Perga.Contracts;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public async Task DisposedStoreRetainsPublicExceptionIdentity()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        Assert.Same(h.Store, h.Store.SessionInputs);
        Assert.Same(h.Store, h.Store.SessionMessages);
        Assert.Same(h.Store, h.Store.TrackSchedules);
        Assert.Same(h.Store, h.Store.TrackWakes);
        await h.Store.DisposeAsync();
        var initialize = await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Store.InitializeAsync());
        var read = await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Store.ListAreasAsync(default));
        Assert.Equal(typeof(SqliteTrackStore).FullName, initialize.ObjectName);
        Assert.Equal(typeof(SqliteTrackStore).FullName, read.ObjectName);
    }

    [Fact]
    public void StorageSqliteAnchorAndContracts_AreAccessible()
    {
        var stats = new DbMaintenanceStatsDto(0, 0, 0, 0);

        Assert.Equal("WAL", PergaStorageSqliteAssembly.DefaultJournalMode);
        Assert.Equal(0, stats.PrunedEvents);
    }
}
