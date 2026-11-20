using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Tests;

public sealed class SessionResetSweepTests
{
    [Fact]
    public async Task ResetAtomicallyArchivesInterruptedInputsAndCriticalWakesBeforeBoundaryAndFencesEpoch()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        using var db = h.OpenRawInspectConnection();
        var originalText = "  exact text\nwith quotes: \"hello\" and 中文  ";
        foreach (var state in new[] { "queued", "dispatching", "unknown", "withdrawn" })
            await h.Store.SessionMessages.InsertAsync(new("msg_" + state, lead.Id, null, "user", state,
                "hash", null, null, originalText + state, state, 100, "queue"), default);
        await h.Store.TrackWakes.EnqueueAsync(new(track.Id, "mail", null,
            StorageTestHarness.Json("""{"prompt":"critical text","mailId":"mail_1"}""")), default);
        await h.Store.TrackWakes.ClaimBatchAsync(track.Id, lead.Id, 32, default);
        await h.Store.TrackWakes.EnqueueAsync(new(track.Id, "task_settled", null,
            StorageTestHarness.Json("""{"prompt":"task result"}"""), lead.Id), default);
        await h.Store.TrackWakes.EnqueueAsync(new(track.Id, "schedule", null,
            StorageTestHarness.Json("""{"prompt":"ephemeral"}""")), default);
        var other = lead with { Id = "other-session", IsLead = false };
        await h.Store.UpsertSessionAsync(other, null, default);
        await h.Store.TrackWakes.EnqueueAsync(new(track.Id, "mail", null,
            StorageTestHarness.Json("""{"prompt":"another session"}"""), other.Id), default);
        var reset = lead with { Epoch = lead.Epoch + 1, UpdatedAtMs = 9999 };
        var boundary = new SessionItemDto(0, lead.Id, null, "epoch_boundary", StorageTestHarness.Json("""{"epoch":1}"""), 9999);
        var evt = StorageTestHarness.CreateEvent("session.updated", track.Id, track.AreaId);
        await db.ExecuteAsync("CREATE TRIGGER reject_reset BEFORE INSERT ON session_items WHEN NEW.kind='epoch_boundary' BEGIN SELECT RAISE(ABORT,'reset failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => h.Store.CompleteSessionResumeAsync(reset, null, evt, lead.Epoch, default, boundary));
        Assert.Equal(lead.Epoch, await db.ExecuteScalarAsync<int>("SELECT epoch FROM agent_sessions WHERE id=@Id", new { lead.Id }));
        Assert.Equal(3, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM session_messages WHERE delivery IN ('queued','dispatching','unknown')"));
        Assert.Equal(4, (await h.Store.TrackWakes.ListAsync(track.Id, default)).Count(w => w.State is "pending" or "delivering"));
        Assert.Empty(await h.Store.ListSessionItemsAsync(lead.Id));
        await db.ExecuteAsync("DROP TRIGGER reject_reset;");
        await h.Store.CompleteSessionResumeAsync(reset, null, evt, lead.Epoch, default, boundary);
        var rows = (await db.QueryAsync<(string Delivery, long? Resolved)>("SELECT delivery,resolved_at_ms FROM session_messages WHERE id!='msg_withdrawn'")).ToArray();
        Assert.All(rows, row => Assert.Equal(("interrupted", (long?)9999), row));
        Assert.Equal("withdrawn", await db.ExecuteScalarAsync<string>("SELECT delivery FROM session_messages WHERE id='msg_withdrawn'"));
        Assert.Empty(await h.Store.SessionMessages.ListQueueAsync(lead.Id, default));
        var wakes = await h.Store.TrackWakes.ListAsync(track.Id, default);
        Assert.All(wakes.Where(w => w.TargetSessionId != other.Id), w => Assert.Equal(("dropped", "session_reset"), (w.State, w.Reason)));
        Assert.Equal("pending", Assert.Single(wakes, w => w.TargetSessionId == other.Id).State);
        var items = await h.Store.ListSessionItemsAsync(lead.Id);
        Assert.Equal("epoch_boundary", items.Last().Kind);
        Assert.Equal(6, items.Count);
        Assert.All(items.Take(5), i => { Assert.Null(i.TurnId); Assert.True(i.Payload.GetProperty("archived").GetBoolean()); Assert.Equal("interrupted", i.Payload.GetProperty("status").GetString()); });
        Assert.Equal(new[] { "queued", "dispatching", "unknown" }.Select(s => originalText + s), items.Where(i => i.Kind == "user").Select(i => i.Payload.GetProperty("content").GetString()));
        Assert.Contains(items, i => i.Kind == "system" && i.Payload.GetProperty("content").GetString()!.Contains("critical text"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.CompleteSessionResumeAsync(reset, null, evt, lead.Epoch, default, boundary));
        Assert.Equal(items.Count, (await h.Store.ListSessionItemsAsync(lead.Id)).Count);
        Assert.Empty(await db.QueryAsync("PRAGMA foreign_key_check;"));
    }
}
