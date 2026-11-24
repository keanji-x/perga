using Dapper;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Events;

namespace Perga.Storage.Tests;

public sealed class SessionInputBatchTests
{
    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public async Task PromotionRollsBackWhenAnySelectedMemberCannotBeBound(string rejectedId)
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var first = new SessionMessage("first", lead.Id, "owner:installation", "first", "hash",
            null, null, "First", "queued", 1000, "queue");
        await h.Store.SessionMessages!.InsertAsync(first, default);
        await h.Store.SessionMessages.InsertAsync(first with { Id = "second", RequestKey = "second" }, default);
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync($"""
            CREATE TRIGGER refuse_binding BEFORE UPDATE OF turn_id ON session_messages
            WHEN OLD.id='{rejectedId}' BEGIN SELECT RAISE(IGNORE); END;
            """);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Store.SessionInputs.PromoteQueuedBatchAsync(
            lead with { Status = "queued" }, first, StorageTestHarness.CreateEvent("session.updated"), default));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM session_turns;"));
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM session_items;"));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM session_messages WHERE turn_id IS NULL AND delivery='queued';"));
        Assert.Equal("idle", await db.ExecuteScalarAsync<string>("SELECT status FROM agent_sessions WHERE id=@Id;", lead));
    }

    [Fact]
    public async Task PromotionDoesNotBindIdenticalMessageIdsInAnotherSession()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var other = lead with { Id = "other-session", IsLead = false };
        await h.Store.UpsertSessionAsync(other, null, default);
        var first = new SessionMessage("first", lead.Id, "owner:installation", "first", "hash",
            null, null, "First", "queued", 1000, "queue");
        var second = first with { Id = "second", RequestKey = "second", Content = "Second" };
        foreach (var message in new[] { first, second, first with { SessionId = other.Id }, second with { SessionId = other.Id } })
            await h.Store.SessionMessages!.InsertAsync(message, default);

        var input = (await h.Store.SessionInputs.PromoteQueuedBatchAsync(lead with { Status = "queued" },
            first, StorageTestHarness.CreateEvent("session.updated"), default))!;
        Assert.Equal("First\n\nSecond", input.Content);
        foreach (var id in new[] { "first", "second" })
        {
            Assert.Equal(input.Receipt.TurnId, (await h.Store.SessionMessages!.GetAsync(lead.Id, id, default))!.TurnId);
            var untouched = (await h.Store.SessionMessages.GetAsync(other.Id, id, default))!;
            Assert.Null(untouched.TurnId);
            Assert.Equal("queued", untouched.Delivery);
        }
        Assert.Equal(2, (await h.Store.ListSessionItemsAsync(lead.Id)).Count);
        Assert.Empty(await h.Store.ListSessionItemsAsync(other.Id));
    }

    [Fact]
    public async Task BatchPromotionDeliveryRecoveryAndResolutionApplyToWholeTurn()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var messages = h.Store.SessionMessages!;
        var first = new SessionMessage("first", lead.Id, "owner:installation", "first-key", "hash1",
            null, null, "First prompt", "queued", 1000, "queue");
        var second = first with { Id = "second", RequestKey = "second-key", RequestHash = "hash2", Content = "Second prompt" };
        await messages.InsertAsync(first, default);
        await messages.InsertAsync(second, default);
        using var db = h.OpenRawInspectConnection();
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM session_messages WHERE turn_id IS NULL;"));
        var queued = lead with { Status = "queued" };
        var evt = StorageTestHarness.CreateEvent("session.updated");
        var input = (await h.Store.SessionInputs.PromoteQueuedBatchAsync(queued, first, evt, default))!;
        Assert.Equal("First prompt\n\nSecond prompt", input.Content);
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT count(DISTINCT turn_id) FROM session_messages;"));
        var bubbles = await h.Store.ListSessionItemsAsync(lead.Id);
        Assert.Equal(["First prompt", "Second prompt"], bubbles.Select(i => i.Payload.GetProperty("content").GetString()));
        Assert.All(bubbles, i => Assert.Equal(input.Receipt.TurnId, i.TurnId));
        Assert.Equal(input.Content, Assert.Single(await h.Store.ListAcceptedTrackInputsAsync(track.Id, default)).Content);
        Assert.Equal(input.Content, Assert.Single(await h.Store.RecoverSessionInputsAsync(default)).Input.Content);
        var running = lead with { Status = "running", UpdatedAtMs = 2000 };
        Assert.True(await h.Store.SessionInputs.ClaimAsync(input, running, evt, default));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM session_messages WHERE delivery='dispatching';"));
        await h.Store.SessionInputs.MarkDeliveredAsync(input, "thread", "provider-turn", default);
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM session_messages WHERE delivery='delivered';"));
        Assert.Empty(await h.Store.RecoverSessionInputsAsync(default));
        Assert.Single(await h.Store.ListEventsSinceAsync(0, track.Id), e => e.Kind == KernelEventKinds.TurnCompleted);
        var unknown = (await h.Store.SessionInputs.FindUnresolvedAsync(lead.Id, default))!;
        Assert.Equal("first", unknown.Receipt.InputId);
        await h.Store.SessionInputs.ResolveAsync(unknown, lead with { UpdatedAtMs = 3000 }, evt, default);
        Assert.Null(await h.Store.SessionInputs.FindUnresolvedAsync(lead.Id, default));
        Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM session_messages WHERE resolved_at_ms=3000;"));
    }

    [Fact]
    public async Task ImageBatchKeepsTheNextWholeMessageQueuedAndRecoversAttachments()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var bytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
        var id = $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}.png";
        var image = new StoredImageAttachment(id, "image/png", bytes.Length);
        ImageMediaWrite write = new(image, bytes);
        var first = new SessionMessage("first-images", lead.Id, "owner:installation", "first", "hash",
            null, null, "", "queued", 1000, "queue")
        { Attachments = Enumerable.Repeat(image, ImageAttachmentContract.MaxAttachmentsPerMessage).ToArray() };
        var second = first with { Id = "second-image", RequestKey = "second", Attachments = [image] };
        var messages = h.Store.SessionMessages!;
        await messages.InsertWithMediaAsync(track.Id, first, Enumerable.Repeat(write, first.Attachments.Count).ToArray(), default);
        await messages.InsertWithMediaAsync(track.Id, second, [write], default);

        var input = (await h.Store.SessionInputs.PromoteQueuedBatchAsync(lead with { Status = "queued" }, first,
            StorageTestHarness.CreateEvent("session.updated"), default))!;
        Assert.Equal("", input.Content);
        Assert.Equal(first.Attachments, input.Attachments);
        var waiting = await messages.GetAsync(lead.Id, second.Id, default);
        Assert.Null(waiting!.TurnId);
        Assert.Equal(second.Attachments, waiting.Attachments);
        Assert.Equal(first.Attachments, Assert.Single(await h.Store.RecoverSessionInputsAsync(default)).Input.Attachments);
    }

    [Fact]
    public async Task SettlementUpdatesAllMessagesAndKeepsEachReceiptIdentity()
    {
        await using var h = await StorageTestHarness.CreateAsync();
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var first = new SessionMessage("first", lead.Id, "owner:installation", "first-key", "hash1",
            null, null, "First", "queued", 1000, "queue");
        await h.Store.SessionMessages!.InsertAsync(first, default);
        await h.Store.SessionMessages.InsertAsync(first with { Id = "second", RequestKey = "second", Content = "Second" }, default);
        var evt = StorageTestHarness.CreateEvent("session.updated");
        var input = (await h.Store.SessionInputs.PromoteQueuedBatchAsync(lead with { Status = "queued" }, first, evt, default))!;
        await h.Store.SessionInputs.ClaimAsync(input, lead with { Status = "running" }, evt, default);
        await h.Store.SessionInputs.MarkDeliveredAsync(input, null, null, default);
        var turn = new SessionTurnDto(input.Receipt.TurnId, lead.Id, input.TurnIndex, lead.Epoch, "user",
            StorageTestHarness.Json("{}"), "completed", null, null, 1000, 2000);
        await h.Store.SessionInputs.SettleAsync(input, "delivered", turn, [], lead, [evt], default);
        foreach (var id in new[] { "first", "second" })
        {
            var receipt = (await h.Store.SessionInputs.FindByIdAsync(lead.Id, id, default))!.Receipt;
            Assert.Equal((id, input.Receipt.TurnId, "delivered", "completed"),
                (receipt.InputId, receipt.TurnId, receipt.DeliveryState, receipt.TurnStatus));
        }
    }
}
