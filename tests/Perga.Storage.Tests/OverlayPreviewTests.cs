using System.Text.Json;
using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;

namespace Perga.Storage.Tests;

public sealed class OverlayPreviewTests
{
    [Fact]
    public async Task PreviewPrioritizesLatestOpenAskThenErrorThenReplyThenSummary()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead with { Status = "unknown", UnknownTurnId = "turn" }, default);

        async Task AssertPreviewAsync(TrackOverlayPreviewKind kind, string? text, bool includeClosed = false)
        {
            var overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync(includeClosed));
            Assert.Equal(kind, overlay.PreviewKind);
            Assert.Equal(text, overlay.PreviewText);
        }

        // The blocked session has no error detail yet.
        await AssertPreviewAsync(TrackOverlayPreviewKind.Summary, report.Summary);
        await h.Store.AppendSessionItemsAsync(lead.Id, [Reply(lead.Id, "  Latest\n\t reply  ", 3000)]);
        await AssertPreviewAsync(TrackOverlayPreviewKind.Reply, "Latest reply");
        await h.Store.RecordTurnAndItemsAsync(new SessionTurnDto("turn", lead.Id, 1, 1, "user",
            StorageTestHarness.Json("{}"), "failed", null, "  Run\n failed  ", 3000, 4000), [], null, default);
        await AssertPreviewAsync(TrackOverlayPreviewKind.Error, "Run failed");

        var ask = new AskDto("older", track.Id, AskStatus.Open, [new("Older question?", [])], null, 5000, null, AskDelivery.Wake);
        var evt = StorageTestHarness.CreateEvent("test.ask");
        await h.Store.UpsertAskAsync(ask, evt, default);
        await h.Store.UpsertAskAsync(ask with
        {
            Id = "latest",
            CreatedAtMs = 6000,
            Questions = [new("  Which\t option?  ", []), new("Second question?", [])]
        }, evt, default);
        await AssertPreviewAsync(TrackOverlayPreviewKind.Ask, "Which option?");

        await h.Store.UpdateTrackAndEmitAsync(track with { ClosedAtMs = 7000 }, null, default);
        await AssertPreviewAsync(TrackOverlayPreviewKind.Reply, "Latest reply", includeClosed: true);

        await h.Store.UpsertSessionAsync(lead with { ArchivedAtMs = 8000 }, null, default);
        await AssertPreviewAsync(TrackOverlayPreviewKind.Summary, report.Summary, includeClosed: true);
    }

    [Fact]
    public async Task ReplyUsesNewestNonblankEligibleItemAndBoundsLongText()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var worker = lead with { Id = "worker", IsLead = false };
        var archived = worker with { Id = "archived", ArchivedAtMs = 7000 };
        await h.Store.UpsertSessionAsync(worker, null, default);
        await h.Store.UpsertSessionAsync(archived, null, default);
        await using var conn = h.OpenRawInspectConnection();
        await conn.ExecuteAsync("""
            INSERT INTO tasks(id, track_id, block_id, task_key, kind, goal_or_cmd, status, session_id, created_at_ms, updated_at_ms)
            VALUES ('task', @TrackId, 'block', 'task', 'subagent', 'test', 'running', 'worker', 1, 1);
            """, new { TrackId = track.Id });

        await h.Store.AppendSessionItemsAsync(lead.Id,
        [
            Reply(lead.Id, "First item at this timestamp", 4000),
            Reply(lead.Id, "\n\u2003 Long\t\nreply " + new string('x', 32_000), 3000),
            Reply(lead.Id, " \t\n\u2003", 5000)
        ]);
        await h.Store.AppendSessionItemsAsync(worker.Id, [Reply(worker.Id, "Worker output", 6000)]);
        await h.Store.AppendSessionItemsAsync(archived.Id, [Reply(archived.Id, "Archived output", 7000)]);

        var overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.Equal(TrackOverlayPreviewKind.Reply, overlay.PreviewKind);
        Assert.Equal("Long reply " + new string('x', 269), overlay.PreviewText);

        await conn.ExecuteAsync("UPDATE tasks SET status='done' WHERE id='task';");
        var released = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.Equal(TrackOverlayPreviewKind.Reply, released.PreviewKind);
        Assert.Equal("Worker output", released.PreviewText);
        var fallback = Assert.Single(await h.Store.ListTrackOverlayFallbacksAsync(
            [new(track.Id, false, false), new("missing", false, false)], default));
        Assert.Equal(new TrackOverlayFallback(track.Id, "Worker output", TrackOverlayPreviewKind.Reply), fallback);
    }

    [Fact]
    public async Task OverlaySkipsMalformedAskAndReplyJson()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        await h.Store.AppendSessionItemsAsync(lead.Id, [Reply(lead.Id, "Usable reply", 3000)]);
        await using var conn = h.OpenRawInspectConnection();
        // The expression index normally rejects malformed item JSON on write.
        await conn.ExecuteAsync("""
            DROP INDEX idx_session_items_blob_ref;
            INSERT INTO session_items(session_id, kind, payload_json, created_at_ms)
            VALUES (@SessionId, 'assistant_text', '{invalid', 4000);
            INSERT INTO asks(id, track_id, status, questions_json, created_at_ms)
            VALUES ('damaged', @TrackId, 'open', '[invalid', 4000);
            """, new { TrackId = track.Id, SessionId = lead.Id });

        var overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.Equal((TrackOverlayPreviewKind.Reply, "Usable reply"), (overlay.PreviewKind, overlay.PreviewText));
    }

    [Fact]
    public async Task ErrorPreviewComesFromTheBlockingSessionOrFailedWorkspace()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead with { Status = "unknown" }, default);
        var idle = lead with { Id = "idle", IsLead = false, UnknownLastError = "Old idle failure" };
        var worker = idle with { Id = "worker", Status = "unknown", UnknownLastError = "Pending worker failure" };
        await h.Store.UpsertSessionAsync(idle, null, default);
        await h.Store.UpsertSessionAsync(worker, null, default);
        await using var conn = h.OpenRawInspectConnection();
        await conn.ExecuteAsync("""
            INSERT INTO tasks(id, track_id, block_id, task_key, kind, goal_or_cmd, status, session_id, created_at_ms, updated_at_ms)
            VALUES ('task', @TrackId, 'block', 'task', 'subagent', 'test', 'pending', 'worker', 1, 1);
            INSERT INTO track_workspace_preparations(track_id, intent_json)
            VALUES (@TrackId, '{"Phase":4,"FailureDetail":"Workspace mount failed"}');
            """, new { TrackId = track.Id });
        await h.Store.RecordTurnAndItemsAsync(FailedTurn(lead.Id, "Blocking failure", 3000), [], null, default);
        await h.Store.UpsertSessionAsync(lead with { Status = "unknown", UnknownTurnId = "turn-" + lead.Id }, null, default);
        await h.Store.RecordTurnAndItemsAsync(FailedTurn(idle.Id, "Old idle failure", 4000), [], null, default);
        await h.Store.RecordTurnAndItemsAsync(FailedTurn(worker.Id, "Pending worker failure", 5000), [], null, default);

        async Task ExpectPreviewAsync(TrackOverlayPreviewKind kind, string text)
        {
            var overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync());
            Assert.Equal((kind, text), (overlay.PreviewKind, overlay.PreviewText));
        }

        await ExpectPreviewAsync(TrackOverlayPreviewKind.Error, "Blocking failure");
        var workspaceOnly = Assert.Single(await h.Store.ListTrackOverlayFallbacksAsync(
            [new(track.Id, IncludeBlockedSessionError: false, IncludeWorkspaceError: true)], default));
        Assert.Equal(new TrackOverlayFallback(track.Id, "Workspace mount failed", TrackOverlayPreviewKind.Error), workspaceOnly);
        await h.Store.UpsertSessionAsync(lead, null, default);
        await ExpectPreviewAsync(TrackOverlayPreviewKind.Error, "Workspace mount failed");
        await conn.ExecuteAsync("UPDATE track_workspace_preparations SET intent_json = json_remove(intent_json, '$.FailureDetail');");
        await ExpectPreviewAsync(TrackOverlayPreviewKind.Summary, report.Summary);
    }

    [Fact]
    public async Task FallbackIgnoresWorkspaceFailureWhileRetryClaimIsPending()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        var (blockedTrack, blockedReport, blockedLead) = StorageTestHarness.CreateSeedTrack(
            trackId: "blocked", leadSessionId: "blocked-lead");
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        await h.Store.CreateTrackAsync(blockedTrack, blockedReport, null, blockedLead with { Status = "unknown" }, default);
        await h.Store.AppendSessionItemsAsync(lead.Id, [Reply(lead.Id, "Usable reply", 3000)]);
        await using var conn = h.OpenRawInspectConnection();
        await conn.ExecuteAsync("""
            INSERT INTO track_workspace_preparations(track_id, intent_json)
            VALUES (@TrackId, '{"Phase":4,"FailureDetail":"Workspace mount failed"}'),
                   (@BlockedTrackId, '{"Phase":4,"FailureDetail":"Workspace mount failed"}');
            """, new { TrackId = track.Id, BlockedTrackId = blockedTrack.Id });
        var stored = await h.Store.ListTrackOverlaysAsync();
        Assert.All(stored, overlay => Assert.Equal(TrackOverlayPreviewKind.Error, overlay.PreviewKind));
        Assert.False(Assert.Single(stored, overlay => overlay.TrackId == track.Id).HasBlockedSession);
        Assert.True(Assert.Single(stored, overlay => overlay.TrackId == blockedTrack.Id).HasBlockedSession);

        // The retry has set live workspace state to pending while the durable claim is still waiting.
        var fallbacks = await h.Store.ListTrackOverlayFallbacksAsync(
        [
            new(track.Id, IncludeBlockedSessionError: false, IncludeWorkspaceError: false),
            new(blockedTrack.Id, IncludeBlockedSessionError: true, IncludeWorkspaceError: false)
        ], default);
        Assert.Contains(new TrackOverlayFallback(track.Id, "Usable reply", TrackOverlayPreviewKind.Reply), fallbacks);
        Assert.Contains(new TrackOverlayFallback(blockedTrack.Id, blockedReport.Summary, TrackOverlayPreviewKind.Summary), fallbacks);
    }

    [Fact]
    public async Task CurrentBlockerWithoutDetailDoesNotReuseAnOlderFailedTurn()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var old = FailedTurn(lead.Id, "An unrelated old failure", 3000);
        var current = old with { Id = "current-blocker", TurnIndex = 2, Status = "unknown", ErrorText = "" };
        await h.Store.RecordTurnAndItemsAsync(old, [], null, default);
        await h.Store.RecordTurnAndItemsAsync(current, [], null, default);
        var blocked = lead with { Status = "unknown", UnknownTurnId = current.Id, UnknownLastError = " \t" };
        await h.Store.UpsertSessionAsync(blocked, null, default);

        var overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.True(overlay.HasBlockedSession);
        Assert.Equal((TrackOverlayPreviewKind.Summary, report.Summary), (overlay.PreviewKind, overlay.PreviewText));

        await h.Store.RecordTurnAndItemsAsync(current with { ErrorText = "Current turn failure" }, [], null, default);
        var fallback = Assert.Single(await h.Store.ListTrackOverlayFallbacksAsync(
            [new(track.Id, IncludeBlockedSessionError: true, IncludeWorkspaceError: false)], default));
        Assert.Equal(new TrackOverlayFallback(track.Id, "Current turn failure", TrackOverlayPreviewKind.Error), fallback);

        await h.Store.UpsertSessionAsync(blocked with { UnknownLastError = "Current reconnect failure" }, null, default);
        overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.Equal((TrackOverlayPreviewKind.Error, "Current reconnect failure"), (overlay.PreviewKind, overlay.PreviewText));
    }

    [Fact]
    public async Task ColdReplyWithAnUnpairedSurrogateInItsPreviewPropertyNameFallsThrough()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var damaged = JsonSerializer.SerializeToElement(new
        {
            blobRef = "blobs/unavailable.zst",
            preview = """{"\uD800":0,"text":"Truncated"""
        });
        await h.Store.AppendSessionItemsAsync(lead.Id,
            [Reply(lead.Id, "Usable reply", 3000), new(0, lead.Id, null, "assistant_text", damaged, 4000)]);

        var overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.Equal((TrackOverlayPreviewKind.Reply, "Usable reply"), (overlay.PreviewKind, overlay.PreviewText));
    }

    [Fact]
    public async Task NewerColdReplyUsesBoundedInlineTextWithoutHydratingTheBlob()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        const string beginning = "New \"reply\" 🙂 ";
        await h.Store.AppendSessionItemsAsync(lead.Id,
            [Reply(lead.Id, "Old inline reply", 3000), Reply(lead.Id, "  New \"reply\"\n🙂 " + new string('x', 70_000), 4000)]);
        await using var conn = h.OpenRawInspectConnection();
        var blobRef = await conn.ExecuteScalarAsync<string>(
            "SELECT json_extract(payload_json, '$.blobRef') FROM session_items ORDER BY id DESC LIMIT 1;");
        Assert.NotNull(blobRef);
        var blobPath = Path.Combine(h.DataDir, blobRef);
        Assert.True(File.Exists(blobPath));
        File.Delete(blobPath);

        var overlay = Assert.Single(await h.Store.ListTrackOverlaysAsync());
        Assert.Equal(TrackOverlayPreviewKind.Reply, overlay.PreviewKind);
        Assert.Equal(beginning + new string('x', 280 - beginning.Length), overlay.PreviewText);
    }

    private static SessionTurnDto FailedTurn(string sessionId, string detail, long completedAtMs) =>
        new("turn-" + sessionId, sessionId, 1, 1, "user", StorageTestHarness.Json("{}"),
            "failed", null, detail, completedAtMs - 1, completedAtMs);

    private static SessionItemDto Reply(string sessionId, string text, long createdAtMs) =>
        new(0, sessionId, null, "assistant_text", JsonSerializer.SerializeToElement(new { v = 1, text }), createdAtMs);
}
