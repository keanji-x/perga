using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Storage.Tests;

public sealed class RepositoryTransactionTests
{
    [Fact]
    public async Task CreationFailureRollsBackReceiptInputAndEarlierEventsThenRetrySurvivesReopen()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        var key = new ResourceCreationKey("track-create", track.AreaId, "request-1", "hash-1");
        var creation = new TrackCreation(track, report, "source", lead, [],
            new SessionInputRequest("first-input", "input-hash", "Start work"), null,
            [StorageTestHarness.CreateEvent("creation.first"), StorageTestHarness.CreateEvent("creation.last")]);
        using (var conn = h.OpenRawInspectConnection())
            await conn.ExecuteAsync("""
                CREATE TRIGGER reject_last_event BEFORE INSERT ON events
                WHEN NEW.kind = 'creation.last'
                BEGIN SELECT RAISE(ABORT, 'last event failed'); END;
                """);

        var failure = await Assert.ThrowsAsync<SqliteException>(() => h.Store.CreateTrackAsync(creation, key, default));
        Assert.Equal(19, failure.SqliteErrorCode);
        Assert.Null(await h.Store.GetTrackAsync(track.Id, default));
        Assert.Null(await h.Store.FindCreationAsync(key, default));
        Assert.Empty(await h.Store.ListEventsSinceAsync(0));
        using (var conn = h.OpenRawInspectConnection())
        {
            foreach (var table in new[] { "reports", "report_revisions", "agent_sessions", "session_turns", "session_messages", "session_items" })
                Assert.Equal(0, await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table};"));
            await conn.ExecuteAsync("DROP TRIGGER reject_last_event;");
        }

        Assert.Equal(track.Id, await h.Store.CreateTrackAsync(creation, key, default));
        var input = Assert.Single(await h.Store.ListAcceptedTrackInputsAsync(track.Id, default));
        var eventIds = (await h.Store.ListEventsSinceAsync(0)).Select(e => e.EventId).ToArray();
        Assert.Equal(2, eventIds.Length);
        Assert.True(eventIds[0] < eventIds[1]);
        await h.Store.DisposeAsync();

        await using var reopened = await SqliteTrackStore.OpenAsync(h.DbPath, h.DataDir);
        Assert.Equal(track.Id, await reopened.CreateTrackAsync(creation, key, default));
        Assert.Equal(input.Receipt, Assert.Single(await reopened.ListAcceptedTrackInputsAsync(track.Id, default)).Receipt);
        var events = await reopened.ListEventsSinceAsync(0);
        Assert.Equal(new[] { "creation.first", "creation.last" }, events.Select(e => e.Kind));
        Assert.Equal(eventIds, events.Select(e => e.EventId));
        Assert.Equal(1, (await reopened.LoadTrackAggregateAsync(track.Id, default))!.Report.DocRev);
    }

    [Fact]
    public async Task ReportWakeFailureRollsBackRevisionAndEventAcrossReopen()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        using (var conn = h.OpenRawInspectConnection())
            await conn.ExecuteAsync("""
                CREATE TRIGGER reject_report_wake BEFORE INSERT ON track_wakes
                BEGIN SELECT RAISE(ABORT, 'wake failed after event'); END;
                """);
        var next = report with { DocRev = 2, Summary = "Changed" };
        var evt = StorageTestHarness.CreateEvent("report.updated");
        await Assert.ThrowsAsync<SqliteException>(() => h.Store.SaveReportAsync(
            track.Id, next, [], null, "edit", evt, default));
        await h.Store.DisposeAsync();

        await using var reopened = await SqliteTrackStore.OpenAsync(h.DbPath, h.DataDir);
        var snapshot = (await reopened.LoadTrackAggregateAsync(track.Id, default))!;
        Assert.Equal(report.Summary, snapshot.Report.Summary);
        Assert.Equal(1, snapshot.Report.DocRev);
        Assert.Empty(await reopened.ListEventsSinceAsync(0));
        Assert.Empty(await reopened.TrackWakes.ListAsync(track.Id, default));
        using (var conn = h.OpenRawInspectConnection())
        {
            Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM report_revisions;"));
            await conn.ExecuteAsync("DROP TRIGGER reject_report_wake;");
        }
        await reopened.SaveReportAsync(track.Id, next, [], null, "edit", evt, default);
        Assert.Equal(2, (await reopened.LoadTrackAggregateAsync(track.Id, default))!.Report.DocRev);
        var persistedEvent = Assert.Single(await reopened.ListEventsSinceAsync(0));
        Assert.Single(await reopened.TrackWakes.ListAsync(track.Id, default));
        using (var conn = h.OpenRawInspectConnection())
            Assert.Equal(persistedEvent.EventId, await conn.ExecuteScalarAsync<long>(
                "SELECT event_id FROM report_revisions WHERE doc_rev = 2;"));
    }
}
