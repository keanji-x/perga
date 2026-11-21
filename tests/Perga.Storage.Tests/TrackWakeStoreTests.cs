using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Tests;

public sealed class TrackWakeStoreTests
{
    private static TrackWakeRequest Wake(string kind = "mail", string? key = "key", string? target = null) =>
        new("trk_1", kind, key, StorageTestHarness.Json("""{"prompt":"notice"}"""), target);

    private static async Task<StorageTestHarness> SeedAsync()
    {
        var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        return h;
    }

    [Fact]
    public async Task CriticalWakeStopsAfterThreeAttemptsAndLeavesOneVisibleSystemItem()
    {
        await using var h = await SeedAsync();
        await h.Store.TrackWakes.EnqueueAsync(Wake(key: null), default);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claimed = Assert.Single(await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "sess_lead_1", 32, default));
            Assert.Equal(attempt, claimed.Attempts);
            await h.Store.TrackWakes.RecoverOnBootAsync(default);
        }
        Assert.Empty(await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "sess_lead_1", 32, default));
        var row = Assert.Single(await h.Store.TrackWakes.ListAsync("trk_1", default));
        Assert.Equal(("dropped", "max_attempts_exceeded", 3L), (row.State, row.Reason, row.Attempts));
        var item = Assert.Single(await h.Store.ListSessionItemsAsync("sess_lead_1"));
        Assert.Equal("system", item.Kind);
        Assert.Null(item.TurnId);
        Assert.Equal("max_attempts_exceeded", item.Payload.GetProperty("reason").GetString());
        Assert.Contains("notice", item.Payload.GetProperty("content").GetString());
        await h.Store.TrackWakes.RecoverOnBootAsync(default);
        Assert.Single(await h.Store.ListSessionItemsAsync("sess_lead_1"));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("delivering")]
    public async Task ExhaustedWakeIsDroppedOnClaimOrRuntimeFailure(string state)
    {
        await using var h = await SeedAsync();
        await h.Store.TrackWakes.EnqueueAsync(Wake("schedule", key: null), default);
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync("UPDATE track_wakes SET attempts=3,state=@State;", new { State = state });
        if (state == "pending")
            Assert.Empty(await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "sess_lead_1", 32, default));
        else
            await h.Store.TrackWakes.MarkDroppedAsync(await h.Store.TrackWakes.ListAsync("trk_1", default),
                "max_attempts_exceeded", null, default);
        var wake = Assert.Single(await h.Store.TrackWakes.ListAsync("trk_1", default));
        Assert.Equal(("dropped", "max_attempts_exceeded", 3L), (wake.State, wake.Reason, wake.Attempts));
        Assert.Single(await h.Store.ListSessionItemsAsync("sess_lead_1"));
    }

    [Fact]
    public async Task CoalesceIsAppendOnlyAndCancelOnlyAffectsPending()
    {
        await using var h = await SeedAsync();
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync("CREATE TRIGGER forbid_wake_delete BEFORE DELETE ON track_wakes BEGIN SELECT RAISE(ABORT, 'runtime delete'); END;");
        await h.Store.TrackWakes.EnqueueAsync(Wake(), default);
        await h.Store.TrackWakes.EnqueueAsync(Wake(), default);
        var rows = await h.Store.TrackWakes.ListAsync("trk_1", default);
        Assert.Equal(2, rows.Count);
        Assert.Equal(("dropped", "coalesced"), (rows[0].State, rows[0].Reason));
        Assert.True(await h.Store.TrackWakes.CancelPendingWakeAsync("trk_1", rows[1].Id, default));
        Assert.False(await h.Store.TrackWakes.CancelPendingWakeAsync("trk_1", rows[0].Id, default));
        Assert.Empty(await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "sess_lead_1", 32, default));
        Assert.Equal("canceled", (await h.Store.TrackWakes.ListAsync("trk_1", default))[1].Reason);
    }

    [Fact]
    public async Task ClaimIsOrderedBoundedAndPreservesDynamicRouting()
    {
        await using var h = await SeedAsync();
        for (var i = 0; i < 35; i++) await h.Store.TrackWakes.EnqueueAsync(Wake(key: null), default);
        var batch = await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "sess_lead_1", 100, default);
        Assert.Equal(32, batch.Count);
        Assert.Equal(batch.OrderBy(w => w.Ordinal), batch);
        Assert.All(batch, w => { Assert.Null(w.TargetSessionId); Assert.Equal(1, w.Attempts); });
        await h.Store.TrackWakes.LinkTurnAsync(batch, "turn", default);
        await h.Store.TrackWakes.MarkDoneAsync(batch, "delivered", "turn", default);
        using var changed = h.OpenRawInspectConnection();
        await changed.ExecuteAsync("UPDATE tracks SET lead_session_id='different-lead' WHERE id='trk_1';");
        Assert.Equal(3, (await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "different-lead", 32, default)).Count);
        Assert.All((await h.Store.TrackWakes.ListAsync("trk_1", default)).Take(32), w => Assert.Equal(("done", "delivered", "turn"), (w.State, w.Reason, w.TurnId)));
    }

    [Fact]
    public async Task RecoveryDropsSupersededBeforeRequeueAndDoesNotReplayEphemeralOrAckedWakes()
    {
        await using var h = await SeedAsync();
        await h.Store.TrackWakes.EnqueueAsync(Wake(), default);
        await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "sess_lead_1", 32, default);
        await h.Store.TrackWakes.EnqueueAsync(Wake(), default);
        await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "sess_lead_1", 32, default);
        await h.Store.TrackWakes.EnqueueAsync(Wake(), default); // pending wins over both delivering
        await h.Store.TrackWakes.EnqueueAsync(Wake("schedule", "schedule"), default);
        await h.Store.TrackWakes.EnqueueAsync(Wake("task_settled", "task"), default);
        var claimed = await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "sess_lead_1", 32, default);
        await h.Store.TrackWakes.LinkTurnAsync(claimed, "old-turn", default);
        await h.Store.TrackWakes.EnqueueAsync(Wake(), default);
        await h.Store.TrackWakes.RecoverOnBootAsync(default);
        var rows = await h.Store.TrackWakes.ListAsync("trk_1", default);
        Assert.All(rows.Take(3), w => Assert.Equal(("dropped", "boot_superseded"), (w.State, w.Reason)));
        Assert.Equal(("dropped", "boot_dropped"), (rows[3].State, rows[3].Reason));
        Assert.Equal(("pending", "boot_requeued"), (rows[4].State, rows[4].Reason));
        Assert.Null(rows[4].TurnId);
        Assert.Null(rows[4].TargetSessionId);
        using var changed = h.OpenRawInspectConnection();
        await changed.ExecuteAsync("UPDATE tracks SET lead_session_id='new-lead' WHERE id='trk_1';");
        var retry = await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "new-lead", 32, default);
        await h.Store.TrackWakes.MarkDoneAsync(retry, "delivered", "new-turn", default);
        await h.Store.TrackWakes.RecoverOnBootAsync(default);
        Assert.Empty(await h.Store.TrackWakes.ListPendingTracksAsync(default));
    }

    [Fact]
    public async Task MailOutboxRollsBackTogetherAndGuardsUpdatesAndClosedTracks()
    {
        await using var h = await SeedAsync();
        var mail = new MailDto("mail", "trk_1", "trk_1", "summary", "body", null, 1000);
        var evt = StorageTestHarness.CreateEvent("mail.received", "trk_1", "area_1");
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync("CREATE TRIGGER reject_wake BEFORE INSERT ON track_wakes BEGIN SELECT RAISE(ABORT, 'outbox failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => h.Store.UpsertMailAsync(mail, evt, default));
        Assert.Empty(await h.Store.ListMailsAsync("trk_1"));
        Assert.Empty(await h.Store.ListEventsSinceAsync(0, "trk_1"));
        await db.ExecuteAsync("DROP TRIGGER reject_wake;");
        await h.Store.UpsertMailAsync(mail, evt, default, requestWake: false);
        await h.Store.UpsertMailAsync(mail with { ReadAtMs = 2000 }, evt, default);
        Assert.Empty(await h.Store.TrackWakes.ListAsync("trk_1", default));
        await h.Store.UpsertMailAsync(mail with { Id = "new" }, evt, default);
        Assert.Single(await h.Store.TrackWakes.ListAsync("trk_1", default));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT sum(wake_pending) FROM mails;"));
        var track = (await h.Store.GetTrackAsync("trk_1", default))!;
        await h.Store.UpdateTrackAndEmitAsync(track with { ClosedAtMs = 3000 }, null, default);
        await h.Store.UpsertMailAsync(mail with { Id = "closed" }, evt, default);
        var wake = Assert.Single(await h.Store.TrackWakes.ListAsync("trk_1", default));
        Assert.Equal(("dropped", "track_closed"), (wake.State, wake.Reason));
    }

    [Fact]
    public async Task TemplateChangePreservesUnownedWakesAndCloseDropsAllActive()
    {
        await using var h = await SeedAsync();
        await h.Store.TrackWakes.EnqueueAsync(Wake("schedule", "first"), default);
        await h.Store.TrackWakes.ClaimBatchAsync("trk_1", "sess_lead_1", 32, default);
        await h.Store.TrackWakes.EnqueueAsync(Wake("schedule", "second"), default);
        await h.Store.TrackWakes.EnqueueAsync(Wake(), default);
        var track = (await h.Store.GetTrackAsync("trk_1", default))!;
        await h.Store.UpdateTrackTemplateAndEmitAsync(track, "new", StorageTestHarness.CreateEvent("track.updated", track.Id, track.AreaId), default);
        var rows = await h.Store.TrackWakes.ListAsync("trk_1", default);
        Assert.Equal("delivering", rows[0].State);
        Assert.Equal(("pending", (string?)null), (rows[1].State, rows[1].Reason));
        Assert.Equal("pending", rows[2].State);
        await h.Store.UpdateTrackAndEmitAsync(track with { ClosedAtMs = 1 }, null, default);
        rows = await h.Store.TrackWakes.ListAsync("trk_1", default);
        Assert.All(rows, w => Assert.Equal("dropped", w.State));
        Assert.Equal("track_closed", rows[0].Reason);
        Assert.Equal("track_closed", rows[2].Reason);
    }

    [Fact]
    public async Task AskHoldNeverEnqueuesAndTaskAndReportOutboxesShareTheirBusinessTransactions()
    {
        await using var h = await SeedAsync();
        var evt = StorageTestHarness.CreateEvent("ask.answered");
        var ask = new AskDto("ask", "trk_1", AskStatus.Answered, [new AskQuestionDto("Question", ["Yes"])],
            StorageTestHarness.Json("true"), 1, 2, AskDelivery.Hold);
        await h.Store.UpsertAskAsync(ask, evt, default);
        Assert.Empty(await h.Store.TrackWakes.ListAsync("trk_1", default));
        await h.Store.UpsertAskAsync(ask with { Id = "wake-ask", Delivery = AskDelivery.Wake }, evt, default);
        Assert.Equal("ask_answered", Assert.Single(await h.Store.TrackWakes.ListAsync("trk_1", default)).Kind);
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync("CREATE TRIGGER reject_wake BEFORE INSERT ON track_wakes BEGIN SELECT RAISE(ABORT, 'outbox failure'); END;");
        var task = new TaskDto("task", "trk_1", "block", "key", 1, "subagent", "codex", AccessMode.ReadWrite,
            true, [], "Goal", null, null, null, TaskRunStatus.Done, null, SemanticVerdict.None, null, 1000, 1000,
            SessionId: "sess_lead_1", RunCount: 1, Meta: StorageTestHarness.Json("{}"), StartedAtMs: 1000, FinishedAtMs: 1001);
        await Assert.ThrowsAsync<SqliteException>(() => h.Store.UpsertTaskAsync(task, null, evt, default));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM tasks;"));
        var snapshot = (await h.Store.LoadTrackAggregateAsync("trk_1", default))!;
        await Assert.ThrowsAsync<SqliteException>(() => h.Store.SaveReportAndTasksAsync("trk_1",
            snapshot.Report with { DocRev = 2 }, [], null, "Human edit", [], snapshot.Track.TemplateState, evt, default));
        Assert.Equal(1, (await h.Store.LoadTrackAggregateAsync("trk_1", default))!.Report.DocRev);
        await db.ExecuteAsync("DROP TRIGGER reject_wake;");
        await h.Store.UpsertTaskAsync(task, null, evt, default);
        await h.Store.UpsertTaskAsync(task, null, evt, default);
        Assert.Single(await h.Store.TrackWakes.ListAsync("trk_1", default), w => w.Kind == "task_settled");
        await h.Store.SaveReportAndTasksAsync("trk_1", snapshot.Report with { DocRev = 2 }, [], null, "Human edit",
            [task], snapshot.Track.TemplateState, evt, default);
        Assert.Single(await h.Store.TrackWakes.ListAsync("trk_1", default), w => w.Kind == "report_edited");
    }

    [Fact]
    public async Task MaintenanceExpiresStaleDeliveringButRetainsOpenPendingAndCapsTerminalHistory()
    {
        await using var h = await SeedAsync();
        const long day = 86_400_000;
        const long now = 100 * day;
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync("""
            WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<51010)
            INSERT INTO track_wakes(id,track_id,kind,state,created_at_ms,updated_at_ms)
            SELECT 'terminal-'||x,'trk_1','mail','done',@Now,@Now FROM n;
            INSERT INTO track_wakes(id,track_id,kind,state,created_at_ms,updated_at_ms) VALUES
                ('old-terminal','trk_1','mail','done',1,1),
                ('open-pending','trk_1','mail','pending',1,1),
                ('stale','trk_1','mail','delivering',1,1),
                ('recent','trk_1','mail','delivering',@Now,@Now);
            """, new { Now = now });
        Assert.Equal(1012, await h.Store.PruneTrackWakesAsync(now, default));
        Assert.Equal(50000, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM track_wakes WHERE state IN ('done','dropped');"));
        Assert.Equal("pending", await db.ExecuteScalarAsync<string>("SELECT state FROM track_wakes WHERE id='open-pending';"));
        Assert.Equal("maintenance_expired", await db.ExecuteScalarAsync<string>("SELECT reason FROM track_wakes WHERE id='stale';"));
        Assert.Equal("delivering", await db.ExecuteScalarAsync<string>("SELECT state FROM track_wakes WHERE id='recent';"));
        await db.ExecuteAsync("UPDATE tracks SET closed_at_ms=1 WHERE id='trk_1';");
        await h.Store.PruneTrackWakesAsync(now, default);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM track_wakes WHERE state IN ('pending','delivering');"));
    }
}
