using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;

namespace Perga.Storage.Tests;

public sealed class TaskSessionSettlementTests
{
    [Theory]
    [InlineData(TaskRunStatus.Done)]
    [InlineData(TaskRunStatus.Canceled)]
    public async Task SettlementCommitsTaskQueueSessionAndWakeTogether(TaskRunStatus status)
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var session = await SeedTrackAsync(h);
        var store = h.Store;
        var task = CreateTask(session, "owned");
        await store.UpsertTaskAsync(task, null, default);
        var batch = await PromoteBatchAsync(h, session, 2);
        await store.SessionMessages.InsertAsync(Message(session, "waiting"), default);
        await store.SessionMessages.InsertAsync(Message(session, "dispatching") with { Delivery = "dispatching" }, default);
        await store.SessionMessages.InsertAsync(Message(session, "unknown") with { Delivery = "unknown" }, default);

        var settled = task with { Status = status, FinishedAtMs = 3000, UpdatedAtMs = 3000 };
        var evt = StorageTestHarness.CreateEvent(KernelEventKinds.TaskUpdated);
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync("""
            CREATE TRIGGER reject_task_event BEFORE INSERT ON events WHEN NEW.kind = 'task.updated'
            BEGIN SELECT RAISE(ABORT, 'last write fails'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => store.SettleTaskAsync(settled, evt, default, session.Id));
        Assert.Equal(TaskRunStatus.Running, Assert.Single((await store.LoadTrackAggregateAsync(session.TrackId, default))!.Tasks).Status);
        Assert.Equal("queued", Assert.Single(await store.ListSessionTurnsAsync(session.Id, default)).Status);
        Assert.Equal("queued", (await store.SessionMessages.GetAsync(session.Id, "waiting", default))!.Delivery);
        Assert.Equal("queued", (await store.SessionMessages.GetAsync(session.Id, batch.Receipt.InputId, default))!.Delivery);
        Assert.Equal("queued", await db.ExecuteScalarAsync<string>("SELECT status FROM agent_sessions WHERE id = @Id;", session));
        Assert.Empty(await store.TrackWakes.ListAsync(session.TrackId, default));
        Assert.Empty(await store.ListEventsSinceAsync(0, session.TrackId));

        await db.ExecuteAsync("DROP TRIGGER reject_task_event;");
        await store.UpsertTaskAsync(settled, null, default);
        Assert.Equal("queued", (await store.SessionMessages.GetAsync(session.Id, "waiting", default))!.Delivery);
        Assert.Empty(await store.TrackWakes.ListAsync(session.TrackId, default));
        await store.UpsertTaskAsync(task, null, default);
        var result = await store.SettleTaskAsync(settled, evt, default, session.Id);
        Assert.True(result.QueueChanged);
        Assert.Equal(session with { Status = "idle", UpdatedAtMs = 3000 }, result.Session);
        Assert.Equal(status, Assert.Single((await store.LoadTrackAggregateAsync(session.TrackId, default))!.Tasks).Status);

        var turn = Assert.Single(await store.ListSessionTurnsAsync(session.Id, default));
        Assert.Equal((batch.Receipt.TurnId, "interrupted", 3000L), (turn.Id, turn.Status, turn.CompletedAtMs!.Value));
        Assert.Null(turn.StartedAtMs);
        foreach (var id in new[] { batch.Receipt.InputId, "member-2", "waiting" })
            Assert.Equal("interrupted", (await store.SessionMessages.GetAsync(session.Id, id, default))!.Delivery);
        foreach (var id in new[] { "dispatching", "unknown" })
            Assert.Equal(id, (await store.SessionMessages.GetAsync(session.Id, id, default))!.Delivery);
        Assert.False((await store.SettleTaskAsync(settled, null, default, session.Id)).QueueChanged);
        Assert.Single(await store.TrackWakes.ListAsync(session.TrackId, default), wake => wake.Kind == "task_settled");
        Assert.Equal([KernelEventKinds.SessionUpdated, KernelEventKinds.TaskUpdated],
            (await store.ListEventsSinceAsync(0, session.TrackId)).Select(e => e.Kind));
    }

    [Theory]
    [InlineData("wake", TaskRunStatus.Running)]
    [InlineData("reuse", TaskRunStatus.Running)]
    [InlineData("reuse", TaskRunStatus.Verifying)]
    [InlineData("owned", TaskRunStatus.Running)]
    public async Task BootAppliesSettlementOnlyToOwnedSessions(string kind, TaskRunStatus status)
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var session = await SeedTrackAsync(h);
        var store = h.Store;
        var task = CreateTask(session, kind) with { Status = status };
        await store.UpsertTaskAsync(task, null, default);
        var batch = await PromoteBatchAsync(h, session, 2);
        await store.SessionMessages.InsertAsync(Message(session, "waiting"), default);

        Assert.Equal([session.TrackId], await store.MarkInFlightInterruptedOnBootAsync(default));
        var snapshot = (await store.LoadTrackAggregateAsync(session.TrackId, default))!;
        var ended = Assert.Single(snapshot.Tasks);
        Assert.Equal(TaskRunStatus.Failed, ended.Status);
        Assert.Equal(kind == "owned" ? FlowOutcome.None : FlowOutcome.Blocked, ended.Outcome);
        var actual = Assert.Single(snapshot.Sessions, s => s.Id == session.Id);
        Assert.Equal(kind == "owned", actual.ArchivedAtMs is not null);
        Assert.Equal(kind == "owned" ? "idle" : "queued", actual.Status);
        var recovery = await store.RecoverSessionInputsAsync(default);
        if (kind == "owned")
        {
            Assert.Empty(recovery);
            Assert.Equal("interrupted", Assert.Single(await store.ListSessionTurnsAsync(session.Id, default)).Status);
            foreach (var id in new[] { batch.Receipt.InputId, "member-2", "waiting" })
                Assert.Equal("interrupted", (await store.SessionMessages.GetAsync(session.Id, id, default))!.Delivery);
        }
        else
        {
            Assert.Equal(batch, Assert.Single(recovery).Input);
            Assert.Equal("queued", (await store.SessionMessages.GetAsync(session.Id, "waiting", default))!.Delivery);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HistoricalRepairPreservesTheOriginalBatchAndIsIdempotent(int count)
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var session = await SeedTrackAsync(h);
        await h.Store.UpsertTaskAsync(CreateTask(session, "reuse") with { Status = TaskRunStatus.Done }, null, default);
        var batch = await PromoteBatchAsync(h, session, count);
        using var db = h.OpenRawInspectConnection();
        var turnId = batch.Receipt.TurnId;
        await db.ExecuteAsync("UPDATE session_messages SET delivery = 'interrupted' WHERE turn_id = @TurnId;", new { TurnId = turnId });
        var ordinals = (await db.QueryAsync<long>("SELECT ordinal FROM session_messages ORDER BY ordinal;")).ToArray();
        Assert.Equal(batch, Assert.Single(await h.Store.RecoverSessionInputsAsync(default)).Input);
        Assert.Equal(batch, Assert.Single(await h.Store.RecoverSessionInputsAsync(default)).Input);
        Assert.Equal(ordinals, await db.QueryAsync<long>("SELECT ordinal FROM session_messages ORDER BY ordinal;"));
        Assert.Equal(count, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM session_items WHERE turn_id = @TurnId;", new { TurnId = turnId }));
        var snap = (await h.Store.LoadTrackAggregateAsync(session.TrackId, default))!;
        Assert.Equal(session.NativeThreadId, Assert.Single(snap.Sessions, s => s.Id == session.Id).NativeThreadId);
    }

    [Theory]
    [InlineData("started")]
    [InlineData("completed")]
    [InlineData("provider_ref")]
    [InlineData("delivered_turn")]
    [InlineData("resolved")]
    [InlineData("dispatching")]
    [InlineData("delivered")]
    [InlineData("unknown")]
    [InlineData("epoch")]
    [InlineData("conflicting")]
    [InlineData("newer")]
    [InlineData("owned")]
    public async Task HistoricalRepairRequiresEvidenceForEveryBatchMember(string evidence)
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var session = await SeedTrackAsync(h);
        var batch = await PromoteBatchAsync(h, session, 2);
        using var db = h.OpenRawInspectConnection();
        var turnId = batch.Receipt.TurnId;
        var args = new { TurnId = turnId, SessionId = session.Id, Evidence = evidence };
        // A mixed batch must not replay even its still-queued member when the proof fails.
        await db.ExecuteAsync("UPDATE session_messages SET delivery = 'interrupted' WHERE id = 'member-2';");
        switch (evidence)
        {
            case "started":
                await db.ExecuteAsync("UPDATE session_turns SET started_at_ms = 10 WHERE id = @TurnId;", args);
                break;
            case "completed":
                await db.ExecuteAsync("UPDATE session_turns SET completed_at_ms = 10 WHERE id = @TurnId;", args);
                break;
            case "provider_ref":
                await db.ExecuteAsync("UPDATE session_turns SET provider_turn_ref = 'accepted' WHERE id = @TurnId;", args);
                break;
            case "delivered_turn":
                await db.ExecuteAsync("UPDATE session_messages SET delivered_turn_id = @TurnId WHERE id = 'member-2';", args);
                break;
            case "resolved":
                await db.ExecuteAsync("UPDATE session_messages SET resolved_at_ms = 10 WHERE id = 'member-2';", args);
                break;
            case "epoch":
                await db.ExecuteAsync("UPDATE agent_sessions SET epoch = epoch + 1 WHERE id = @SessionId;", args);
                break;
            case "conflicting":
            case "newer":
                await db.ExecuteAsync("""
                    INSERT INTO session_turns(id, session_id, turn_index, epoch, trigger_kind, status)
                    VALUES ('other', @SessionId, CASE WHEN @Evidence = 'newer' THEN 2 ELSE 0 END, 1,
                            'user', CASE WHEN @Evidence = 'newer' THEN 'completed' ELSE 'unknown' END);
                    """, args);
                break;
            case "owned":
                await h.Store.UpsertTaskAsync(CreateTask(session, "owned") with { Status = TaskRunStatus.Done }, null, default);
                break;
            default:
                await db.ExecuteAsync("""
                    INSERT INTO session_message_steers(message_ordinal, target_turn_id, delivery)
                    SELECT ordinal, 'older-turn', @Evidence FROM session_messages WHERE id = 'member-2';
                    """, args);
                break;
        }
        Assert.Empty(await h.Store.RecoverSessionInputsAsync(default));
        Assert.Empty(await h.Store.ListAcceptedTrackInputsAsync(session.TrackId, default));
        Assert.False(await h.Store.SessionInputs.ClaimAsync(batch, session with { Status = "running" },
            StorageTestHarness.CreateEvent("fixture.claimed"), default));
        Assert.Equal(["queued", "interrupted"], await db.QueryAsync<string>("SELECT delivery FROM session_messages ORDER BY ordinal;"));
    }

    [Theory]
    [InlineData("reset_context", true)]
    [InlineData("reconnect_durable", true)]
    [InlineData("reconnect_wake", true)]
    [InlineData("unresolved_durable", false)]
    public async Task HistoricalRepairRespectsResolvedVsUnresolvedPriorUnknownTurns(string scenario, bool shouldRecover)
    {
        await using var h = await StorageTestHarness.CreateAsync();
        var session = await SeedTrackAsync(h);
        if (scenario == "reset_context")
        {
            session = session with { Epoch = 2 };
            await h.Store.UpsertSessionAsync(session, null, default);
        }

        await h.Store.UpsertTaskAsync(CreateTask(session, "wake") with { Status = TaskRunStatus.Done }, null, default);
        using var db = h.OpenRawInspectConnection();
        var priorEpoch = scenario == "reset_context" ? 1 : session.Epoch;
        var priorTrigger = scenario == "reconnect_wake" ? "wake" : "user";
        await db.ExecuteAsync("""
            INSERT INTO session_turns(id, session_id, turn_index, epoch, trigger_kind, status)
            VALUES ('prior-unknown', @SessionId, 1, @Epoch, @Trigger, 'unknown');
            """, new { SessionId = session.Id, Epoch = priorEpoch, Trigger = priorTrigger });
        if (scenario is "reset_context" or "reconnect_durable" or "unresolved_durable")
        {
            await db.ExecuteAsync("""
                INSERT INTO session_messages(id, session_id, kind, author_id, request_key, request_hash,
                                             turn_id, content, delivery, created_at_ms, resolved_at_ms)
                VALUES ('prior-msg', @SessionId, 'input', 'owner:installation', 'prior-key', 'hash',
                        'prior-unknown', 'old input', 'unknown', 1000, @ResolvedAtMs);
                """, new { SessionId = session.Id, ResolvedAtMs = scenario == "unresolved_durable" ? (long?)null : 1500L });
        }

        var batch = await PromoteBatchAsync(h, session, 1);
        await db.ExecuteAsync("UPDATE session_messages SET delivery = 'interrupted' WHERE turn_id = @TurnId;",
            new { batch.Receipt.TurnId });

        var recovered = await h.Store.RecoverSessionInputsAsync(default);
        if (shouldRecover)
            Assert.Equal(batch, Assert.Single(recovered).Input);
        else
            Assert.Empty(recovered);
    }

    private static async Task<AgentSessionDto> SeedTrackAsync(StorageTestHarness h)
    {
        await h.Store.UpsertAreaAsync(StorageTestHarness.CreateArea(), default);
        var (track, report, lead) = StorageTestHarness.CreateSeedTrack();
        await h.Store.CreateTrackAsync(track, report, null, lead, default);
        var session = lead with { Id = "worker", IsLead = false, NativeThreadId = "existing-thread" };
        await h.Store.UpsertSessionAsync(session, null, default);
        return session;
    }

    private static TaskDto CreateTask(AgentSessionDto session, string kind) =>
        new("task", session.TrackId, kind == "owned" ? "block" : $"flow:{kind}", kind, 1,
            kind == "wake" ? "flow_wake" : "subagent", "codex", AccessMode.ReadOnly, true, [], "Test",
            kind == "owned" ? null : StorageTestHarness.Json(kind == "reuse" ? """{"flowNode":true,"reuseSession":true}""" : """{"flowNode":true}"""),
            null, null, TaskRunStatus.Running, null, SemanticVerdict.None, null, 2000, 2000,
            SessionId: session.Id, RunCount: 1, StartedAtMs: 2000);

    private static SessionMessage Message(AgentSessionDto session, string id) =>
        new(id, session.Id, "owner:installation", id, "hash", null, null, id, "queued", 2000, "queue");

    private static async Task<PersistedSessionInput> PromoteBatchAsync(StorageTestHarness h, AgentSessionDto session, int count)
    {
        for (var i = 1; i <= count; i++)
            await h.Store.SessionMessages.InsertAsync(Message(session, $"member-{i}"), default);
        var promoted = (await h.Store.SessionInputs.PromoteQueuedBatchAsync(session with { Status = "queued" },
            Message(session, "member-1"), StorageTestHarness.CreateEvent("fixture.promoted"), default))!;
        using var db = h.OpenRawInspectConnection();
        await db.ExecuteAsync("DELETE FROM events WHERE kind = 'fixture.promoted';");
        return promoted;
    }
}
