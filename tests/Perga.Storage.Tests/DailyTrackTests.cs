using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Tests;

public sealed class DailyTrackTests
{
    [Fact]
    public async Task EnsureIsAtomicIdempotentHiddenAndCascadesMapping()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var area = StorageTestHarness.CreateArea("system");
        TrackCreation Creation(int index)
        {
            var (track, report, lead) = StorageTestHarness.CreateSeedTrack($"daily{index}", area.Id, $"lead{index}");
            return new(track, report, null, lead, [], null, null, [], "2026-11-17", area);
        }
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => h.Store.CreateTrackAsync(Creation(i), null, default)));
        Assert.Single(ids.Distinct());
        Assert.Empty(await h.Store.ListAreasAsync(default));
        var id = ids[0];
        Assert.Equal(id, await h.Store.GetDailyTrackIdAsync("2026-11-17", default));
        var aggregate = (await h.Store.LoadTrackAggregateAsync(id, default))!;
        Assert.Single(aggregate.Sessions);
        Assert.True(aggregate.Sessions[0].IsLead);
        Assert.NotEmpty(aggregate.Report.Blocks);
        await using var conn = h.OpenRawInspectConnection();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM tracks"));
        await h.Store.DeleteTrackAsync(id, default);
        Assert.Null(await h.Store.GetDailyTrackIdAsync("2026-11-17", default));
        Assert.Empty(await h.Store.ListAreasAsync(default));
    }

    [Fact]
    public async Task FailedEnsureRollsBackSystemAreaAndMapping()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var area = StorageTestHarness.CreateArea("system");
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack(areaId: area.Id);
        await Assert.ThrowsAnyAsync<Exception>(() => h.Store.CreateTrackAsync(
            new(track, report, null, lead with { TrackId = "missing" }, [], null, null, [], "2026-11-17", area), null, default));
        Assert.Null(await h.Store.GetAreaAsync(area.Id, default));
        Assert.Null(await h.Store.GetDailyTrackIdAsync("2026-11-17", default));
    }
}
