using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Kernel.Events;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.AgentKit;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.IntegrationTests;

public sealed class FlowSessionSettlementTests
{
    private static readonly InteropPrincipal.Human Human = new();

    [Fact]
    public async Task WakeSettlementBeforeClaimPreservesThePromotedInput()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var wakeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promoted = new TaskCompletionSource<PersistedSessionInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        PromotionGate pause = null!;
        await using var host = new PergaHostTestCluster(configureExtraServices: services => AddPromotionGate(services, gate => pause = gate));
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<SqliteTrackStore>();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var flowPath = (await kernel.GetTrackFlowAsync(trackId, ct)).FilePath!;
        await File.WriteAllTextAsync(flowPath, """
            {"rev":0,"nodes":[{"id":"wake-lead","do":{"kind":"wake","session":"lead","prompt":"flow wake"}}]}
            """, ct);
        host.AgentRuntime.SetTurnHandler((_, input, turnCt) => RunTurn(input, turnCt));
        pause.OnPromoted = input =>
        {
            // Pause dispatch after the real promotion transaction, before the actor can start ClaimAsync.
            kernel.DispatchAdmission.SetDraining(true);
            promoted.TrySetResult(input);
        };

        try
        {
            Assert.True((await kernel.CommitTrackFlowAsync(trackId, false, 0, Human, ct)).Ok);
            await wakeStarted.Task.WaitAsync(ct);
            var waiting = await kernel.SendSessionMessageAsync(sessionId,
                new(Content: "queued follow-up", RequestKey: "follow-up", Mode: "queue"), Human, ct);
            releaseWake.TrySetResult();
            var accepted = await promoted.Task.WaitAsync(ct);
            var receipt = accepted.Receipt;
            while (!(await kernel.GetTrackFlowAsync(trackId, ct)).Projection.Archived.Any(node => node.Id == "wake-lead" && node.Outcome == "ok"))
                await Task.Delay(10, ct);

            Assert.Equal(waiting.MessageId, receipt.InputId);
            var originalTurn = Assert.Single(await store.ListSessionTurnsAsync(sessionId, ct), turn => turn.Id == receipt.TurnId);
            Assert.Equal("queued", originalTurn.Status);
            Assert.Null(originalTurn.StartedAtMs);
            Assert.Single(host.AgentRuntime.ExecutedTurns);
            Assert.Equal("queued", (await store.SessionMessages!.GetAsync(sessionId, receipt.InputId, ct))!.Delivery);
            var nativeThreadId = Assert.Single((await store.LoadTrackAggregateAsync(trackId, ct))!.Sessions).NativeThreadId;
            Assert.NotNull(nativeThreadId);
            Assert.Equal(Assert.Single(host.AgentRuntime.OpenedSessions).NativeThreadId, nativeThreadId);

            pause.OnPromoted = null;
            kernel.DispatchAdmission.SetDraining(false);
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
            var execution = Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "queued follow-up");
            Assert.Equal(receipt.TurnId, execution.Input.CorrelationId);
            Assert.Equal("completed", (await store.SessionInputs.FindByIdAsync(sessionId, receipt.InputId, ct))!.Receipt.TurnStatus);
            Assert.Equal(nativeThreadId, Assert.Single((await store.LoadTrackAggregateAsync(trackId, ct))!.Sessions).NativeThreadId);

            await kernel.SendSessionMessageAsync(sessionId, new(Content: "later input", RequestKey: "later"), Human, ct);
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
            Assert.Equal(["flow wake", "queued follow-up", "later input"], host.AgentRuntime.ExecutedTurns.Select(turn => turn.Input.Prompt));
        }
        finally
        {
            releaseWake.TrySetResult();
            kernel.DispatchAdmission.SetDraining(false);
        }

        async IAsyncEnumerable<UnifiedAgentEvent> RunTurn(TurnInput input, [EnumeratorCancellation] CancellationToken turnCt)
        {
            if (input.Prompt == "flow wake")
            {
                yield return new UnifiedAgentEvent.TurnAccepted("wake-provider-turn");
                wakeStarted.TrySetResult();
                await releaseWake.Task.WaitAsync(turnCt);
            }
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
    }

    [Fact]
    public async Task WakeSettlementAfterClaimLeavesTheRunningTurnIntact()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var session = Assert.Single((await store.LoadTrackAggregateAsync(trackId, ct))!.Sessions);
        var wake = BorrowedTask(session, "wake");
        await store.UpsertTaskAsync(wake, null, ct);
        var started = Signal();
        var release = Signal();
        host.AgentRuntime.SetTurnHandler((_, _, turnCt) => HoldTurnAsync(started, release.Task, turnCt));

        try
        {
            var sent = await kernel.SendSessionMessageAsync(sessionId,
                new(Content: "claimed before settlement", RequestKey: "claimed"), Human, ct);
            await started.Task.WaitAsync(ct);
            // The real dispatcher has claimed the turn. Settle the persisted wake at the transaction boundary.
            var before = (await store.SessionInputs.FindByIdAsync(sessionId, sent.MessageId, ct))!.Receipt;
            Assert.Equal("running", before.TurnStatus);
            Assert.Equal("dispatching", before.DeliveryState);
            Assert.NotNull(Assert.Single(await store.ListSessionTurnsAsync(sessionId, ct)).StartedAtMs);
            await store.SettleTaskAsync(wake with { Status = TaskRunStatus.Done, FinishedAtMs = wake.UpdatedAtMs },
                null, ct, suppressDefaultWake: true);
            Assert.Equal(before, (await store.SessionInputs.FindByIdAsync(sessionId, sent.MessageId, ct))!.Receipt);
            release.TrySetResult();
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
            Assert.Equal(before.TurnId, Assert.Single(host.AgentRuntime.ExecutedTurns).Input.CorrelationId);
            Assert.Equal("completed", (await store.SessionInputs.FindByIdAsync(sessionId, sent.MessageId, ct))!.Receipt.TurnStatus);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("wake", "cancel")]
    [InlineData("reuse", "finish")]
    [InlineData("reuse", "finish-rollback")]
    [InlineData("reuse", "fail")]
    [InlineData("reuse", "fail-retry")]
    [InlineData("reuse", "cancel")]
    [InlineData("reuse", "cancel-promoted")]
    [InlineData("reuse", "cancel-edited")]
    public async Task BorrowedSessionDrainsAfterTaskEnds(string kind, string action)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        PromotionGate pause = null!;
        await using var host = new PergaHostTestCluster(configureExtraServices: services => AddPromotionGate(services, gate => pause = gate));
        var (trackId, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var sessionId = kind == "wake" ? lead :
            (await kernel.CreateSessionAsync(trackId, new(Title: "Existing review conversation"), "review", Human, ct)).Receipt.Session.Id;
        host.AgentRuntime.SetTurnHandler((_, _, _) => CompletedTurnAsync());
        await kernel.SendSessionMessageAsync(sessionId, new(Content: "establish context", RequestKey: "context"), Human, ct);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
        var original = Assert.Single((await store.LoadTrackAggregateAsync(trackId, ct))!.Sessions, s => s.Id == sessionId);
        Assert.NotNull(original.NativeThreadId);
        var started = Signal();
        var release = Signal();
        var promoted = new TaskCompletionSource<PersistedSessionInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (action == "cancel-promoted")
            pause.OnPromoted = input =>
            {
                kernel.DispatchAdmission.SetDraining(true);
                promoted.TrySetResult(input);
            };
        host.AgentRuntime.SetTurnHandler((spec, input, turnCt) => spec.StateKey == sessionId &&
            (input.Prompt == "borrowed wake" || input.Prompt.Contains("Execute contract task", StringComparison.Ordinal))
                ? HoldTurnAsync(started, release.Task, turnCt) : CompletedTurnAsync());

        try
        {
            await FlowFixture.AddAsync(kernel, trackId, new FlowNodeSpecDto("borrowed",
                kind == "wake" ? new("wake", Session: "lead", Prompt: "borrowed wake")
                               : new("run", Session: sessionId, Prompt: "borrowed run", Access: "read_only")));
            await started.Task.WaitAsync(ct);
            var workerToken = kind == "reuse" ? Token(host.AgentRuntime.OpenedSessions.Last(open => open.Spec.StateKey == sessionId)) : null;
            if (workerToken is not null)
                Assert.NotNull(kernel.McpSessionBridge.ResolveToken(workerToken)!.BoundTaskId);
            var sent = await kernel.SendSessionMessageAsync(sessionId,
                new(Content: "borrowed follow-up", RequestKey: "follow-up", Mode: "queue"), Human, ct);
            if (action == "cancel-promoted")
            {
                release.TrySetResult();
                Assert.Equal(sent.MessageId, (await promoted.Task.WaitAsync(ct)).Receipt.InputId);
            }
            if (action == "cancel-edited")
            {
                var draft = """{"rev":1,"nodes":[{"id":"borrowed","canceled":true,"do":{"kind":"run","prompt":"cancel","access":"read_only"}}]}""";
                var canceled = await kernel.CommitTrackFlowAsync(trackId, false, 1, Human, ct, content: draft);
                Assert.True(canceled.Ok, string.Join(Environment.NewLine, canceled.Diagnostics));
            }
            if (action is "fail-retry" or "finish-rollback")
            {
                await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
                await db.OpenAsync(ct);
                await db.ExecuteAsync("""
                    CREATE TRIGGER reject_settlement BEFORE INSERT ON events
                    WHEN NEW.kind = 'task.updated' AND EXISTS (SELECT 1 FROM tasks WHERE status IN ('done', 'failed'))
                    BEGIN SELECT RAISE(ABORT, 'settlement failure'); END;
                    """);
                var finish = action == "finish-rollback";
                await Assert.ThrowsAsync<SqliteException>(() => finish
                    ? kernel.CompleteTaskDoneAsync(trackId, sessionId, ct: ct)
                    : kernel.CompleteTaskFailAsync(trackId, sessionId, "Worker failed", ct: ct));
                Assert.Equal(finish ? TaskRunStatus.Verifying : TaskRunStatus.Running,
                    Assert.Single(await kernel.ListTasksAsync(trackId, ct)).Status);
                Assert.Equal("queued", (await store.SessionMessages!.GetAsync(sessionId, sent.MessageId, ct))!.Delivery);
                Assert.Null(kernel.McpSessionBridge.ResolveToken(workerToken!));
                await db.ExecuteAsync("DROP TRIGGER reject_settlement;");
            }
            var ended = action switch
            {
                "finish" => await kernel.CompleteTaskDoneAsync(trackId, sessionId, ct: ct),
                "fail" or "fail-retry" => await kernel.CompleteTaskFailAsync(trackId, sessionId, "Worker failed", ct: ct),
                "cancel-edited" => Assert.Single((await store.LoadTrackAggregateAsync(trackId, ct))!.Tasks),
                _ => await kernel.CancelTaskAsync(trackId, null, "borrowed", ct: ct)
            };
            Assert.Equal(action switch
            {
                "finish" => TaskRunStatus.Done,
                "fail" or "fail-retry" => TaskRunStatus.Failed,
                _ => TaskRunStatus.Canceled
            }, ended.Status);
            release.TrySetResult();
            if (action == "cancel-promoted")
            {
                var stillQueued = await store.SessionMessages!.GetAsync(sessionId, sent.MessageId, ct);
                Assert.Equal("queued", stillQueued!.Delivery);
                pause.OnPromoted = null;
                kernel.DispatchAdmission.SetDraining(false);
            }
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);

            var receipt = (await store.SessionInputs.FindByIdAsync(sessionId, sent.MessageId, ct))!.Receipt;
            Assert.Equal("completed", receipt.TurnStatus);
            Assert.Equal(receipt.TurnId, Assert.Single(host.AgentRuntime.ExecutedTurns,
                turn => turn.Input.Prompt == "borrowed follow-up").Input.CorrelationId);
            var session = Assert.Single((await store.LoadTrackAggregateAsync(trackId, ct))!.Sessions, s => s.Id == sessionId);
            Assert.Null(session.ArchivedAtMs);
            Assert.Equal((original.NativeThreadId, original.Epoch, "idle"), (session.NativeThreadId, session.Epoch, session.Status));
            if (workerToken is not null)
            {
                Assert.Null(kernel.McpSessionBridge.ResolveToken(workerToken));
                var followUpToken = Token(host.AgentRuntime.OpenedSessions.Last(open => open.Spec.StateKey == sessionId));
                var ordinary = kernel.McpSessionBridge.ResolveToken(followUpToken);
                Assert.NotNull(ordinary);
                Assert.Equal(sessionId, ordinary.SessionId);
                Assert.Null(ordinary.BoundTaskId);
            }
            await kernel.SendSessionMessageAsync(sessionId, new(Content: "after settlement", RequestKey: "after"), Human, ct);
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
            Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Spec.StateKey == sessionId && turn.Input.Prompt == "after settlement");
        }
        finally
        {
            release.TrySetResult();
            kernel.DispatchAdmission.SetDraining(false);
        }
    }

    [Theory]
    [InlineData("wake")]
    [InlineData("reuse")]
    public async Task BorrowedSessionDrainsAfterProviderStartupFailure(string kind)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        await using var host = new PergaHostTestCluster();
        var (trackId, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var sessionId = kind == "wake" ? lead :
            (await kernel.CreateSessionAsync(trackId, new(), "session", Human, ct)).Receipt.Session.Id;
        var opening = Signal();
        var failOpen = Signal();
        var attempts = 0;
        host.AgentRuntime.BeforeOpenAsync = async (spec, launchCt) =>
        {
            if (spec.StateKey != sessionId || Interlocked.Increment(ref attempts) != 1) return;
            opening.TrySetResult();
            await failOpen.Task.WaitAsync(launchCt);
            throw new InvalidOperationException("Controlled provider startup failure");
        };
        try
        {
            await FlowFixture.AddAsync(kernel, trackId, new FlowNodeSpecDto("borrowed",
                kind == "wake" ? new("wake", Session: "lead", Prompt: "borrowed wake")
                               : new("run", Session: sessionId, Prompt: "borrowed run", Access: "read_only")));
            await opening.Task.WaitAsync(ct);
            var sent = await kernel.SendSessionMessageAsync(sessionId,
                new(Content: "queued during startup", RequestKey: "during-startup", Mode: "queue"), Human, ct);
            failOpen.TrySetResult();
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
            Assert.Equal(TaskRunStatus.Failed, Assert.Single((await store.LoadTrackAggregateAsync(trackId, ct))!.Tasks).Status);
            var receipt = (await store.SessionInputs.FindByIdAsync(sessionId, sent.MessageId, ct))!.Receipt;
            Assert.Equal("completed", receipt.TurnStatus);
            Assert.Equal(receipt.TurnId, Assert.Single(host.AgentRuntime.ExecutedTurns,
                turn => turn.Input.Prompt == "queued during startup").Input.CorrelationId);
        }
        finally { failOpen.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedWorkerSettlementInterruptsOnlyUnstartedWork(bool cancel)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        PromotionGate pause = null!;
        await using var host = new PergaHostTestCluster(configureExtraServices: services => AddPromotionGate(services, gate => pause = gate));
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<SqliteTrackStore>();
        var (trackId, _) = await host.CreateTrackAsync();
        var started = Signal();
        var release = Signal();
        var promoted = new TaskCompletionSource<PersistedSessionInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler((_, input, turnCt) => input.Prompt.Contains("Execute contract task", StringComparison.Ordinal)
            ? HoldTurnAsync(started, release.Task, turnCt) : CompletedTurnAsync());

        try
        {
            var (worker, task) = await FlowFixture.StartWorkerAsync(kernel, trackId, "owned", "Inspect", ct);
            await started.Task.WaitAsync(ct);
            pause.OnPromoted = input =>
            {
                if (input.Receipt.SessionId != worker.Id) return;
                kernel.DispatchAdmission.SetDraining(true);
                promoted.TrySetResult(input);
            };
            var sent = await kernel.SendSessionMessageAsync(worker.Id,
                new(Content: "promoted follow-up", RequestKey: "promoted", Mode: "queue"), Human, ct);
            release.TrySetResult();
            var input = await promoted.Task.WaitAsync(ct);
            Assert.Equal(sent.MessageId, input.Receipt.InputId);
            foreach (var (id, delivery) in new[] { ("waiting", "queued"), ("dispatching", "dispatching"), ("unknown", "unknown") })
                await store.SessionMessages.InsertAsync(new(id, worker.Id, "owner:installation", id, id, null, null, id,
                    delivery, worker.UpdatedAtMs, "queue"), ct);

            var terminal = cancel
                ? await kernel.CancelTaskAsync(trackId, null, task.Id, ct: ct)
                : await kernel.CompleteTaskDoneAsync(trackId, worker.Id, ct: ct);
            Assert.Equal(cancel ? TaskRunStatus.Canceled : TaskRunStatus.Done, terminal.Status);
            var snapshot = (await store.LoadTrackAggregateAsync(trackId, ct))!;
            Assert.Equal("idle", Assert.Single(snapshot.Sessions, s => s.Id == worker.Id).Status);
            var turn = Assert.Single(await store.ListSessionTurnsAsync(worker.Id, ct), t => t.Id == input.Receipt.TurnId);
            Assert.Equal("interrupted", turn.Status);
            Assert.Null(turn.StartedAtMs);
            foreach (var id in new[] { sent.MessageId, "waiting" })
                Assert.Equal("interrupted", (await store.SessionMessages.GetAsync(worker.Id, id, ct))!.Delivery);
            foreach (var id in new[] { "dispatching", "unknown" })
                Assert.Equal(id, (await store.SessionMessages.GetAsync(worker.Id, id, ct))!.Delivery);
            kernel.DispatchAdmission.SetDraining(false);
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
            Assert.Single(host.AgentRuntime.ExecutedTurns, turnExecution => turnExecution.Spec.StateKey == worker.Id);
        }
        finally
        {
            release.TrySetResult();
            kernel.DispatchAdmission.SetDraining(false);
        }
    }

    [Theory]
    [InlineData("wake", 1, false, false)]
    [InlineData("reuse", 2, false, false)]
    [InlineData("wake", 1, true, false)]
    [InlineData("reuse", 2, true, false)]
    [InlineData("wake", 2, true, true)]
    public async Task RestartRecoversOnlyProvablyUnstartedBorrowedInputs(string kind, int count, bool historical, bool ambiguous)
    {
        var directory = Directory.CreateTempSubdirectory("perga-flow-input-restart-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        try
        {
            string trackId;
            string sessionId;
            PersistedSessionInput input;
            long[] itemIds;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, var lead) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var store = host.Services.GetRequiredService<ITrackStore>();
                sessionId = kind == "wake" ? lead :
                    (await kernel.CreateSessionAsync(trackId, new(), "borrowed-session", Human, ct)).Receipt.Session.Id;
                var session = Assert.Single((await store.LoadTrackAggregateAsync(trackId, ct))!.Sessions, s => s.Id == sessionId);
                session = session with { NativeThreadId = "existing-borrowed-thread" };
                await store.UpsertSessionAsync(session, null, ct);
                var task = BorrowedTask(session, kind);
                if (historical)
                    task = task with { Status = TaskRunStatus.Done, FinishedAtMs = task.UpdatedAtMs };
                await store.UpsertTaskAsync(task, null, ct);
                SessionMessage Message(int i) => new($"member-{i}", sessionId, "owner:installation", $"key-{i}", "hash",
                    null, null, $"test input {i}", "queued", session.UpdatedAtMs, "queue");
                for (var i = 1; i <= count; i++)
                    await store.SessionMessages!.InsertAsync(Message(i), ct);
                input = (await store.SessionInputs.PromoteQueuedBatchAsync(session with { Status = "queued" }, Message(1),
                    new(null, null, trackId, KernelEventKinds.SessionUpdated, JsonSerializer.SerializeToElement(session), session.UpdatedAtMs), ct))!;
                var items = await store.ListSessionItemsAsync(sessionId, ct: ct);
                itemIds = items.Where(item => item.TurnId == input.Receipt.TurnId).Select(item => item.Id).ToArray();
                if (historical)
                {
                    await using var db = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, "perga.db")};Pooling=False");
                    await db.OpenAsync(ct);
                    await db.ExecuteAsync("UPDATE session_messages SET delivery = 'interrupted' WHERE turn_id = @TurnId;", input.Receipt);
                    if (ambiguous)
                        await db.ExecuteAsync("UPDATE session_turns SET provider_turn_ref = 'already-accepted' WHERE id = @TurnId;", input.Receipt);
                }
            }

            await using (var reopened = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = reopened.Services.GetRequiredService<IPergaKernel>();
                await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
                var store = reopened.Services.GetRequiredService<ITrackStore>();
                var snap = (await store.LoadTrackAggregateAsync(trackId, ct))!;
                var ended = Assert.Single(snap.Tasks);
                Assert.Equal(historical ? TaskRunStatus.Done : TaskRunStatus.Failed, ended.Status);
                if (!historical) Assert.Equal(FlowOutcome.Blocked, ended.Outcome);
                var session = Assert.Single(snap.Sessions, s => s.Id == sessionId);
                Assert.Null(session.ArchivedAtMs);
                Assert.Equal("existing-borrowed-thread", session.NativeThreadId);
                var executions = reopened.AgentRuntime.ExecutedTurns.Where(turn => turn.Input.CorrelationId == input.Receipt.TurnId).ToArray();
                if (ambiguous)
                    Assert.Empty(executions);
                else
                {
                    var execution = Assert.Single(executions);
                    Assert.Equal(input.Content, execution.Input.Prompt);
                    Assert.Equal(session.NativeThreadId, execution.Spec.ResumeNativeThreadId);
                }
                var items = await store.ListSessionItemsAsync(sessionId, ct: ct);
                Assert.Equal(itemIds, items.Where(i => i.Kind == "user" && i.TurnId == input.Receipt.TurnId).Select(i => i.Id));
                for (var i = 1; i <= count; i++)
                {
                    var recovered = (await store.SessionInputs.FindByIdAsync(sessionId, $"member-{i}", ct))!.Receipt;
                    Assert.Equal(input.Receipt.TurnId, recovered.TurnId);
                    Assert.Equal(ambiguous ? ("interrupted", "queued") : ("delivered", "completed"), (recovered.DeliveryState, recovered.TurnStatus));
                }
            }
            await using var again = new PergaHostTestCluster(directory.FullName);
            await again.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId).WaitAsync(ct);
            Assert.Empty(again.AgentRuntime.ExecutedTurns);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task EditingAnOwnedNodeToBorrowPreservesItsPreviousWorkerOwnershipAcrossRestart()
    {
        var directory = Directory.CreateTempSubdirectory("perga-flow-ownership-edit-");
        try
        {
            string trackId;
            SessionInputReceipt stranded;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, _) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var store = host.Services.GetRequiredService<ITrackStore>();
                var worker = (await kernel.CreateSessionAsync(trackId, new(), "previous-worker", Human, default)).Receipt.Session;
                var target = (await kernel.CreateSessionAsync(trackId, new(), "new-target", Human, default)).Receipt.Session;
                var clock = new FlowWhenSpecDto(Cron: "0 0 * * *");
                await FlowFixture.AddAsync(kernel, trackId, new FlowNodeSpecDto("daily",
                    new("run", Prompt: "Owned run", Access: "read_only"), When: clock));
                var task = Assert.Single(await kernel.ListTasksAsync(trackId));
                await kernel.Actors.RemoveAndDisposeAsync(trackId);
                await store.UpsertTaskAsync(task with
                {
                    SessionId = worker.Id, Status = TaskRunStatus.Done, RunCount = 1, FinishedAtMs = worker.UpdatedAtMs
                }, null, default);
                stranded = (await store.SessionInputs.AcceptAsync(worker with { Status = "queued" },
                    new("legacy", "hash", "Owned work from the old run"),
                    new(null, null, trackId, KernelEventKinds.SessionUpdated, JsonSerializer.SerializeToElement(worker), worker.UpdatedAtMs), default)).Receipt;
                await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, "perga.db")};Pooling=False"))
                {
                    await db.OpenAsync();
                    await db.ExecuteAsync("UPDATE session_messages SET delivery = 'interrupted' WHERE turn_id = @TurnId;", stranded);
                }

                var draft = $$"""
                    {"rev":1,"nodes":[{"id":"daily","when":{"cron":"0 0 * * *"},
                        "do":{"kind":"run","prompt":"Borrowed run","access":"read_only","session":"{{target.Id}}"} }]}
                    """;
                var updated = await kernel.CommitTrackFlowAsync(trackId, false, 1, Human, content: draft);
                Assert.True(updated.Ok, string.Join(Environment.NewLine, updated.Diagnostics));
                var tasks = (await store.LoadTrackAggregateAsync(trackId, default))!.Tasks;
                Assert.Null(Assert.Single(tasks, t => t.Id == task.Id).SessionId);
                Assert.Equal(worker.Id, Assert.Single(tasks, t => t.ArchivedAtMs is not null).SessionId);
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            await reopened.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
            var receipt = (await reopened.Services.GetRequiredService<ITrackStore>().SessionInputs
                .FindByIdAsync(stranded.SessionId, stranded.InputId, default))!.Receipt;
            Assert.Equal(("interrupted", "queued"), (receipt.DeliveryState, receipt.TurnStatus));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task VerifyingEventReadFailureStillRevokesWorkerBindingAndFinishesGate()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        PromotionGate gate = null!;
        await using var host = new PergaHostTestCluster(configureExtraServices: services => AddPromotionGate(services, g => gate = g));
        var (trackId, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var sessionId = (await kernel.CreateSessionAsync(trackId, new(Title: "Reuse verify test"), "reuse-verify", Human, ct)).Receipt.Session.Id;
        var started = Signal();
        var release = Signal();
        host.AgentRuntime.SetTurnHandler((spec, input, turnCt) => spec.StateKey == sessionId &&
            input.Prompt.Contains("Execute contract task", StringComparison.Ordinal)
                ? HoldTurnAsync(started, release.Task, turnCt) : CompletedTurnAsync());

        try
        {
            await FlowFixture.AddAsync(kernel, trackId, new FlowNodeSpecDto("borrowed",
                new("run", Session: sessionId, Prompt: "borrowed run", Access: "read_only")));
            await started.Task.WaitAsync(ct);
            var workerToken = Token(host.AgentRuntime.OpenedSessions.Last(open => open.Spec.StateKey == sessionId));
            Assert.NotNull(kernel.McpSessionBridge.ResolveToken(workerToken)!.BoundTaskId);
            var sent = await kernel.SendSessionMessageAsync(sessionId,
                new(Content: "after verifying event failure", RequestKey: "after-verify", Mode: "queue"), Human, ct);

            var failedOnce = 0;
            gate.OnListEventsSince = () =>
            {
                if (Interlocked.Exchange(ref failedOnce, 1) == 0)
                    throw new IOException("Transient event history read failure after Verifying commit");
            };

            var done = await kernel.CompleteTaskDoneAsync(trackId, sessionId, ct: ct);
            Assert.Equal(TaskRunStatus.Done, done.Status);
            Assert.Equal(1, Volatile.Read(ref failedOnce));
            Assert.Null(kernel.McpSessionBridge.ResolveToken(workerToken));

            release.TrySetResult();
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
            var receipt = (await store.SessionInputs.FindByIdAsync(sessionId, sent.MessageId, ct))!.Receipt;
            Assert.Equal("completed", receipt.TurnStatus);
            Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "after verifying event failure");
        }
        finally
        {
            gate.OnListEventsSince = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task FrozenClaimRefusalRedispatchesOriginalTurnAfterReuseTaskSettles()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        PromotionGate gate = null!;
        await using var host = new PergaHostTestCluster(configureExtraServices: services => AddPromotionGate(services, g => gate = g));
        var (trackId, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var sessionId = (await kernel.CreateSessionAsync(trackId, new(Title: "Frozen claim test"), "frozen-claim", Human, ct)).Receipt.Session.Id;
        var started = Signal();
        var release = Signal();
        var promoted = new TaskCompletionSource<PersistedSessionInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler((spec, input, turnCt) => spec.StateKey == sessionId &&
            input.Prompt.Contains("Execute contract task", StringComparison.Ordinal)
                ? HoldTurnAsync(started, release.Task, turnCt) : CompletedTurnAsync());

        var gateDir = Directory.CreateTempSubdirectory("perga-frozen-claim-gate-");
        var gateReleaseFile = Path.Combine(gateDir.FullName, "release");
        var gateStartedFile = Path.Combine(gateDir.FullName, "started");
        try
        {
            var gateCmd = $"touch \"{gateStartedFile}\" && while [ ! -f \"{gateReleaseFile}\" ]; do sleep 0.02; done";
            await FlowFixture.AddAsync(kernel, trackId, new FlowNodeSpecDto("borrowed",
                new("run", Session: sessionId, Prompt: "borrowed run", Access: "read_only", Gate: gateCmd)));
            await started.Task.WaitAsync(ct);
            gate.OnPromoted = input =>
            {
                if (input.Receipt.SessionId != sessionId) return;
                kernel.DispatchAdmission.SetDraining(true);
                promoted.TrySetResult(input);
            };
            var sent = await kernel.SendSessionMessageAsync(sessionId,
                new(Content: "redispatched after freeze", RequestKey: "frozen-redispatch", Mode: "queue"), Human, ct);
            release.TrySetResult();
            var accepted = await promoted.Task.WaitAsync(ct);

            // Enter Verifying so IsSessionFrozenForDispatchCore is true, then un-drain dispatch so the
            // promoted input actually hits ClaimCoreAsync, gets refused cleanly, and stays queued until Done.
            var doneTask = kernel.CompleteTaskDoneAsync(trackId, sessionId, ct: ct);
            while (!File.Exists(gateStartedFile))
                await Task.Delay(10, ct);
            Assert.Equal(TaskRunStatus.Verifying, Assert.Single(await kernel.ListTasksAsync(trackId, ct)).Status);
            gate.OnPromoted = null;
            kernel.DispatchAdmission.SetDraining(false);
            await Task.Delay(150, ct);
            var stillQueued = (await store.SessionInputs.FindByIdAsync(sessionId, sent.MessageId, ct))!.Receipt;
            Assert.Equal((accepted.Receipt.TurnId, "accepted", "queued"), (stillQueued.TurnId, stillQueued.DeliveryState, stillQueued.TurnStatus));
            Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "redispatched after freeze");

            await File.WriteAllTextAsync(gateReleaseFile, "go", ct);
            var done = await doneTask.WaitAsync(ct);
            Assert.Equal(TaskRunStatus.Done, done.Status);
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(ct);
            var execution = Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "redispatched after freeze");
            Assert.Equal(accepted.Receipt.TurnId, execution.Input.CorrelationId);
            Assert.Equal("completed", (await store.SessionInputs.FindByIdAsync(sessionId, sent.MessageId, ct))!.Receipt.TurnStatus);
        }
        finally
        {
            gate.OnPromoted = null;
            kernel.DispatchAdmission.SetDraining(false);
            release.TrySetResult();
            try { File.WriteAllText(gateReleaseFile, "go"); } catch { }
            try { gateDir.Delete(recursive: true); } catch { }
        }
    }

    private static TaskDto BorrowedTask(AgentSessionDto session, string kind) =>
        new("fixture-task", session.TrackId, $"flow:{kind}", kind, 1, kind == "wake" ? "flow_wake" : "subagent",
            session.Provider, AccessMode.ReadOnly, true, [], "Test wake",
            JsonSerializer.SerializeToElement(new { flowNode = true, reuseSession = kind == "reuse", targetSession = session.Id }),
            null, null, TaskRunStatus.Running, null, SemanticVerdict.None, null, session.CreatedAtMs, session.UpdatedAtMs,
            SessionId: session.Id, RunCount: 1, StartedAtMs: session.UpdatedAtMs);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Token(RecordedSessionOpen open) =>
        Assert.IsType<McpServerConfig.HttpMcpServer>(open.Spec.McpServers["perga"]).Headers["Authorization"];

    private static async IAsyncEnumerable<UnifiedAgentEvent> HoldTurnAsync(TaskCompletionSource started, Task release,
        [EnumeratorCancellation] CancellationToken ct)
    {
        started.TrySetResult();
        var canceled = false;
        try { await release.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { canceled = true; }
        if (canceled) yield return new UnifiedAgentEvent.TurnInterrupted();
        else yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> CompletedTurnAsync()
    {
        await Task.CompletedTask;
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

    private static void AddPromotionGate(IServiceCollection services, Action<PromotionGate> initialized)
    {
        services.RemoveAll<ITrackStore>();
        services.AddSingleton<ITrackStore>(sp =>
        {
            var sqlite = sp.GetRequiredService<SqliteTrackStore>();
            sqlite.InitializeAsync().GetAwaiter().GetResult();
            var wrapped = DispatchProxy.Create<ITrackStore, PromotionGate>();
            var gate = (PromotionGate)wrapped;
            gate.Inner = sqlite;
            initialized(gate);
            return wrapped;
        });
    }

    public class PromotionGate : DispatchProxy
    {
        public SqliteTrackStore Inner { private get; set; } = null!;
        public Action<PersistedSessionInput>? OnPromoted { get; set; }
        public Action? OnListEventsSince { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ITrackReadStore.ListEventsSinceAsync))
                OnListEventsSince?.Invoke();
            if (method.Name != "get_SessionInputs")
                return method.Invoke(Inner, args);
            var proxy = DispatchProxy.Create<ISessionInputStore, PromotionInputStore>();
            ((PromotionInputStore)proxy).Gate = this;
            return proxy;
        }

        public class PromotionInputStore : DispatchProxy
        {
            public PromotionGate Gate { private get; set; } = null!;

            protected override object? Invoke(MethodInfo? method, object?[]? args)
            {
                var result = method!.Invoke(Gate.Inner.SessionInputs, args);
                return method.Name == nameof(ISessionInputStore.PromoteQueuedBatchAsync)
                    ? NotifyPromotionAsync((Task<PersistedSessionInput?>)result!) : result;
            }

            private async Task<PersistedSessionInput?> NotifyPromotionAsync(Task<PersistedSessionInput?> promotion)
            {
                var input = await promotion;
                if (input is not null)
                    Gate.OnPromoted?.Invoke(input);
                return input;
            }
        }
    }
}
