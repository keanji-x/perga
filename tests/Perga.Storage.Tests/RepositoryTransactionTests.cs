using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
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

    [Fact]
    public async Task ClaimBatchSkipsExcludedPendingWakeWithoutCountingAnAttempt()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var wakes = h.Store.TrackWakes;
        await wakes.EnqueueAsync(TrackWakeRequests.Prompt(track.Id, TrackWakeKinds.TaskSettled, "task:tsk_review:1", "Review finished"), default);
        await wakes.EnqueueAsync(TrackWakeRequests.Prompt(track.Id, TrackWakeKinds.Mail, "mail:1", "New mail"), default);

        var excluded = Assert.Single(await wakes.ListAsync(track.Id, default), wake => wake.Kind == TrackWakeKinds.TaskSettled);
        string[] exclusions = [excluded.Id, .. Enumerable.Range(0, 33_000).Select(index => $"unused-{index}")];
        var claimed = Assert.Single(await wakes.ClaimBatchAsync(track.Id, lead.Id, 1, default, excludeWakeIds: exclusions));
        Assert.Equal(TrackWakeKinds.Mail, claimed.Kind);
        Assert.Equal(TrackWakeStates.Delivering, claimed.State);
        Assert.Equal(1, claimed.Attempts);

        var retained = Assert.Single(await wakes.ListAsync(track.Id, default), wake => wake.Id == excluded.Id);
        Assert.Equal(TrackWakeStates.Pending, retained.State);
        Assert.Equal(0, retained.Attempts);
        await wakes.MarkDroppedAsync([retained], TrackWakeReasons.SupersededByFlowWake, "trn_flow", default);
        var dropped = Assert.Single(await wakes.ListAsync(track.Id, default), wake => wake.Id == excluded.Id);
        Assert.Equal(TrackWakeStates.Dropped, dropped.State);
        Assert.Equal(TrackWakeReasons.SupersededByFlowWake, dropped.Reason);
        Assert.Equal("trn_flow", dropped.TurnId);
        Assert.Equal(0, dropped.Attempts);
        Assert.Empty(await wakes.ClaimBatchAsync(track.Id, lead.Id, 32, default, excludeWakeIds: []));
    }

    [Fact]
    public async Task ReopenAfterInterruptedExplicitWakePreservesPendingTaskSettlement()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        await h.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(
            track.Id, TrackWakeKinds.TaskSettled, "task:tsk_review:1", "Review finished"), default);
        var pending = Assert.Single(await h.Store.TrackWakes.ListAsync(track.Id, default));
        Assert.Empty(await h.Store.TrackWakes.ClaimBatchAsync(track.Id, lead.Id, 1, default, excludeWakeIds: [pending.Id]));
        var explicitTurn = new SessionTurnDto("trn_explicit", lead.Id, 1, lead.Epoch, "wake",
            StorageTestHarness.Json("""{"prompt":"Review update"}"""), "running", null, null, 200, null);
        await h.Store.RecordTurnAndItemsAsync(explicitTurn, [], lead with { Status = "running" }, default);
        await h.Store.DisposeAsync();

        await using var reopened = await SqliteTrackStore.OpenAsync(h.DbPath, h.DataDir);
        await reopened.MarkInFlightInterruptedOnBootAsync(default);
        await reopened.TrackWakes.RecoverOnBootAsync(default);
        Assert.Equal("unknown", Assert.Single(await reopened.ListSessionTurnsAsync(lead.Id, default)).Status);
        Assert.Equal(pending, Assert.Single(await reopened.TrackWakes.ListAsync(track.Id, default), wake => wake.Id == pending.Id));
        var claimed = Assert.Single(await reopened.TrackWakes.ClaimBatchAsync(track.Id, lead.Id, 1, default));
        Assert.Equal(pending.Id, claimed.Id);
        Assert.Equal(TrackWakeStates.Delivering, claimed.State);
        Assert.Equal(1, claimed.Attempts);
        await reopened.TrackWakes.MarkDoneAsync([claimed], TrackWakeReasons.Delivered, "trn_recovered", default);
        var delivered = Assert.Single(await reopened.TrackWakes.ListAsync(track.Id, default), wake => wake.Id == pending.Id);
        Assert.Equal(TrackWakeStates.Done, delivered.State);
        Assert.Equal("trn_recovered", delivered.TurnId);
    }
}
