using System.Text.Json;
using Dapper;
using Perga.Contracts;
using Perga.Kernel;
using Perga.ReportDoc;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class DailyReportTests
{
    [Fact]
    public async Task CommittedBoundarySurvivesMaintenancePruningAndReopen()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var store = h.Store;
        await store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await store.CreateTrackAsync(track, report, null, lead, default);
        var at = DateTimeOffset.Parse("2026-11-17T12:00:00Z").ToUnixTimeMilliseconds();
        var engine = new ReportDocEngine();
        var doc = engine.Parse("# Work\n\nRetained evidence.\n", 2, "Retained summary");
        await store.SaveReportAndTasksAsync(track.Id, doc, engine.CompressZstd(doc.Blocks), null, "edit", [],
            JsonSerializer.SerializeToElement(new { }),
            StorageTestHarness.CreateEvent("report.updated", track.Id, track.AreaId, createdAtMs: at), default);
        // Capture a boundary beyond the last report event, so MAX(report_event_id) is insufficient.
        await store.AppendEventAsync(StorageTestHarness.CreateEvent("track.updated", track.Id, track.AreaId, createdAtMs: at), default);
        var service = new TodayReportService(store, store, TimeProvider.System);
        var captured = await service.ChangesAsync("2026-11-17", null, null, default);
        Assert.Single(captured.Changes);
        Assert.True(captured.ThroughEventId > captured.Changes[0].LastEventId);
        var maintenance = await store.RunMaintenancePassAsync(at + (long)TimeSpan.FromDays(8).TotalMilliseconds, default);
        Assert.True(maintenance.PrunedEvents > 0);
        Assert.Empty(await store.ListEventsSinceAsync(0));
        Assert.Equal(captured.ThroughEventId, (await service.ChangesAsync(captured.Date, null, null, default)).ThroughEventId);
        await store.DisposeAsync();
        await using var reopened = new SqliteTrackStore(new SqliteStorageOptions { DbPath = h.DbPath, DataDir = h.DataDir });
        await reopened.InitializeAsync();
        service = new TodayReportService(reopened, reopened, TimeProvider.System);
        var current = await service.ChangesAsync(captured.Date, null, null, default);
        Assert.Equal(captured.ThroughEventId, current.ThroughEventId);
        Assert.Equal("Retained summary", Assert.Single(current.Changes).SummaryAfter);
        Assert.Single((await service.ChangesAsync(captured.Date, null, captured.ThroughEventId, default)).Changes);
        Assert.Single((await service.EditsAsync(captured.Date, track.Id, captured.ThroughEventId, null, default)).Edits);
    }

    [Fact]
    public async Task ReportPagesUseStableEventBoundaryAndRetainBeforeSnapshotsAfterPruning()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var store = h.Store;
        await store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var engine = new ReportDocEngine();
        var at = DateTimeOffset.Parse("2026-11-17T12:00:00Z").ToUnixTimeMilliseconds();
        var empty = JsonSerializer.SerializeToElement(new { });
        async Task Save(string id, int rev)
        {
            var doc = engine.Parse($"# Work\n\nBody {rev}.\n", rev, $"Summary {rev}");
            await store.SaveReportAndTasksAsync(id, doc, engine.CompressZstd(doc.Blocks), null, "edit", [], empty,
                new(null, "area_1", id, "report.updated", JsonSerializer.SerializeToElement(new { docRev = rev }), at), default);
        }
        for (var i = 0; i < 51; i++)
        {
            var (track, report, lead) = StorageTestHarness.CreateSeedTrack($"t{i:D2}", leadSessionId: $"s{i}");
            await store.CreateTrackAsync(track, report, null, lead, default);
            await Save(track.Id, 2);
        }
        var service = new TodayReportService(store, store, TimeProvider.System);
        var first = await service.ChangesAsync("2026-11-17", null, null, default);
        Assert.Equal(50, first.Changes.Count);
        Assert.NotNull(first.NextCursor);
        var last = await service.ChangesAsync(first.Date, first.NextCursor, first.ThroughEventId, default);
        Assert.Single(last.Changes);
        Assert.Null(last.NextCursor);
        Assert.DoesNotContain(last.Changes[0].TrackId, first.Changes.Select(c => c.TrackId));
        for (var rev = 3; rev <= 53; rev++) await Save("t00", rev);
        var tip = await store.GetEventTipIdAsync(default);
        var edits = await service.EditsAsync("2026-11-17", "t00", tip, null, default);
        Assert.Equal(50, edits.Edits.Count);
        Assert.NotNull(edits.NextCursor);
        var more = await service.EditsAsync("2026-11-17", "t00", tip, edits.NextCursor, default);
        Assert.Equal(2, more.Edits.Count);
        Assert.Null(more.NextCursor);
        await Save("t00", 54);
        Assert.Equal(2, (await service.EditsAsync("2026-11-17", "t00", tip, edits.NextCursor, default)).Edits.Count);
        // Ordinary maintenance can remove the prior revision and its event. Each new edit retains its before image.
        await using var conn = h.OpenRawInspectConnection();
        await conn.ExecuteAsync("DELETE FROM report_revisions WHERE track_id = 't00' AND doc_rev < 53; DELETE FROM events WHERE id < @Tip;", new { Tip = tip });
        var retained = await store.ListDailyReportRevisionsAsync(at, at + 1, tip, default);
        var revision = Assert.Single(retained, r => r.TrackId == "t00");
        Assert.Equal("Summary 52", revision.SummaryBefore);
        Assert.Contains("Body 52.", revision.BodyBefore);
        Assert.Equal("Summary 53", revision.SummaryAfter);
    }
}
