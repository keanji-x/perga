using Perga.Contracts;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void StorageSqliteAnchorAndContracts_AreAccessible()
    {
        var stats = new DbMaintenanceStatsDto(0, 0, 0, 0);

        Assert.Equal("WAL", PergaStorageSqliteAssembly.DefaultJournalMode);
        Assert.Equal(0, stats.PrunedEvents);
    }
}
