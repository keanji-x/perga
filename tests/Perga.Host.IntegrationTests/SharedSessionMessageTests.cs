using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

namespace Perga.Host.IntegrationTests;

public sealed class SharedSessionMessageTests
{
    [Fact]
    public async Task VersionFiveUpgradePreservesTranscriptAndBothReceiptNamespaces()
    {
        var directory = Directory.CreateTempSubdirectory("perga-message-upgrade-");
        try
        {
            var database = Path.Combine(directory.FullName, "perga.db");
            await new SqliteMigrator(SqliteMigrator.LoadEmbeddedMigrations().Take(5).ToArray()).MigrateAsync(database);
            const string normalText = "Historical ordinary input";
            const string workerText = "Historical Worker message";
            var normalHash = Hash(new { version = 1, Content = normalText, SteerIfRunning = false });
            var workerHash = Hash(new { version = 1, AttemptId = "attempt", TurnId = (string?)null, Content = workerText, AllowQueue = false });
            await using (var db = OpenDatabase(directory.FullName))
            {
                await db.OpenAsync();
                await db.ExecuteAsync("""
                    INSERT INTO areas (id, name, created_at_ms, updated_at_ms) VALUES ('area', 'Migration', 1, 1);
                    INSERT INTO tracks (id, area_id, cwd, lead_session_id, created_at_ms, updated_at_ms)
                        VALUES ('track', 'area', @Cwd, 'normal', 1, 1);
                    INSERT INTO reports (track_id, updated_at_ms) VALUES ('track', 1);
                    INSERT INTO tasks (id, track_id, block_id, task_key, kind, goal_or_cmd, status, latest_attempt_id, created_at_ms, updated_at_ms)
                        VALUES ('task', 'track', 'block', 'migration', 'agent', 'Old goal', 'done', 'attempt', 1, 1);
                    INSERT INTO task_attempts (attempt_id, task_id, track_id, session_id, status, started_at_ms, finished_at_ms)
                        VALUES ('attempt', 'task', 'track', 'worker', 'completed', 1, 2);
                    INSERT INTO agent_sessions (id, track_id, bound_task_id, bound_attempt_id, provider, created_at_ms, updated_at_ms)
                        VALUES ('normal', 'track', NULL, NULL, 'codex', 1, 2), ('worker', 'track', 'task', 'attempt', 'codex', 1, 2);
                    INSERT INTO session_turns (id, session_id, turn_index, trigger_kind, status)
                        VALUES ('normal-turn', 'normal', 1, 'user', 'completed'), ('worker-turn', 'worker', 1, 'user', 'completed');
                    INSERT INTO session_inputs (id, session_id, turn_id, author_id, request_key, request_hash, content, accepted_at_ms, delivery_state)
                        VALUES ('normal-input', 'normal', 'normal-turn', 'owner:installation', 'normal-key', @NormalHash, @NormalText, 1, 'delivered'),
                               ('collision', 'worker', 'worker-turn', 'owner:installation', 'worker-key', 'older-input-hash', 'Older input for Worker', 1, 'delivered');
                    INSERT INTO session_items (id, session_id, turn_id, kind, payload_json, created_at_ms, accepted_input_id)
                        VALUES (21, 'normal', 'normal-turn', 'user', '{}', 2, 'normal-input'),
                               (22, 'worker', 'worker-turn', 'user', '{}', 2, 'collision');
                    INSERT INTO worker_messages (id, session_id, attempt_id, author_id, request_key, request_hash, result_turn_id, content, delivery, created_at_ms)
                        VALUES ('collision', 'worker', 'attempt', 'owner:installation', 'worker-key', @WorkerHash, 'worker-turn', @WorkerText, 'delivered', 2);
                    """, new { Cwd = directory.FullName, NormalHash = normalHash, NormalText = normalText, WorkerHash = workerHash, WorkerText = workerText });
            }

            await using var host = new PergaHostTestCluster(directory.FullName);
            var kernel = host.Services.GetRequiredService<IPergaKernel>();
            var input = (await host.Services.GetRequiredService<ITrackStore>().SessionInputs.FindByIdAsync("normal", "normal-input", default))!.Receipt;
            Assert.Equal(("normal-input", "normal-turn", "delivered"), (input.InputId, input.TurnId, input.DeliveryState));
            var item = Assert.Single((await kernel.GetSessionItemsAsync("normal", new(), new InteropPrincipal.Human(), default)).Items);
            Assert.Equal(21, item.Id);
            Assert.Equal(normalText, item.Payload.GetProperty("content").GetString());

            Assert.Equal("collision", ((await host.Services.GetRequiredService<ITrackStore>().SessionInputs.FindByIdAsync("worker", "collision", default))!.Receipt).InputId);
            var workerItem = Assert.Single((await kernel.GetSessionItemsAsync("worker", new(), new InteropPrincipal.Human(), default)).Items);
            Assert.Equal(22, workerItem.Id);
            Assert.Equal("Older input for Worker", workerItem.Payload.GetProperty("content").GetString());
            Assert.Empty(host.AgentRuntime.ExecutedTurns);
            Assert.True(File.Exists(database + ".bak-v5"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RestartRunsWaitingOrdinaryMessagesInOrderAndKeepsUncertainAndWithdrawnReceipts()
    {
        var directory = Directory.CreateTempSubdirectory("perga-shared-queue-");
        try
        {
            string sessionId;
            string trackId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                var store = host.Services.GetRequiredService<ITrackStore>().SessionMessages!;
                await InsertQueueAsync(store, sessionId, "declared-uncertain", "dispatching");
                await InsertQueueAsync(store, sessionId, "withdraw-me", "queued");
                using var withdrawn = await host.Client.DeleteAsync($"/api/v1/sessions/{sessionId}/messages/withdraw-me");
                Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
                await InsertQueueAsync(store, sessionId, "first-waiting", "queued");
                await InsertQueueAsync(store, sessionId, "second-waiting", "queued");
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var kernel = reopened.Services.GetRequiredService<IPergaKernel>();
            await kernel.WaitForTrackIdleAsync(trackId);
            Assert.Equal(["first-waiting\n\nsecond-waiting"], reopened.AgentRuntime.ExecutedTurns.Select(execution => execution.Input.Prompt));
            foreach (var (key, expected) in new[] { ("declared-uncertain", "unknown"), ("withdraw-me", "withdrawn"),
                         ("first-waiting", "delivered"), ("second-waiting", "delivered") })
            {
                using var replay = await SendAsync(reopened.Client, sessionId, key, new { content = key, mode = "queue" });
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                var receipt = (await replay.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
                Assert.Equal((key, expected), (receipt.MessageId, receipt.Delivery));
            }
            var page = await kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
            Assert.Equal("declared-uncertain", Assert.Single(page.QueuedMessages).Id);
            Assert.Single(page.Items, item => item.Kind == "user" && item.Payload.GetProperty("inputId").GetString() == "first-waiting");
            Assert.Single(page.Items, item => item.Kind == "user" && item.Payload.GetProperty("inputId").GetString() == "second-waiting");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RestartArchivesOldWorkerWithoutResendingAndExplicitDispatchUsesNewAttempt()
    {
        var directory = Directory.CreateTempSubdirectory("perga-worker-archive-");
        try
        {
            string trackId;
            string taskId;
            AgentSessionDto worker;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, _) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var dispatch = await kernel.CreateSessionAsync(trackId, new(Prompt: "Old Worker goal", Task: new()), "old-worker", new InteropPrincipal.Human(), default);
                taskId = dispatch.Receipt.Task!.Id;
                await kernel.WaitForTrackIdleAsync(trackId);
                worker = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default), summary => summary.TaskId == taskId).Session;
                var store = host.Services.GetRequiredService<ITrackStore>().SessionMessages!;
                await InsertQueueAsync(store, worker.Id, "worker-waiting", "queued");
                await InsertQueueAsync(store, worker.Id, "worker-uncertain", "dispatching");
                await InsertQueueAsync(store, worker.Id, "worker-confirmed", "delivered");
            }

            // Reproduce an abrupt host exit before the active task run's settlement transaction.
            await using (var db = OpenDatabase(directory.FullName))
            {
                await db.OpenAsync();
                await db.ExecuteAsync("""
                    UPDATE tasks SET status = 'running', finished_at_ms = NULL WHERE id = @TaskId;
                    UPDATE agent_sessions SET status = 'running', archived_at_ms = NULL WHERE id = @Id;
                    """, new { TaskId = taskId, worker.Id });
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var activeKernel = reopened.Services.GetRequiredService<IPergaKernel>();
            await activeKernel.WaitForTrackIdleAsync(trackId);
            var old = (await activeKernel.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), default)).Session;
            Assert.NotNull(old.ArchivedAtMs);

            foreach (var (key, expected) in new[] { ("worker-waiting", "interrupted"), ("worker-uncertain", "unknown"), ("worker-confirmed", "delivered") })
            {
                var replay = await activeKernel.SendSessionMessageAsync(worker.Id, new(Content: key, RequestKey: key, Mode: "queue", TurnId: null), new InteropPrincipal.Human(), default);
                Assert.Equal((key, expected), (replay.MessageId, replay.Delivery));
            }
            Assert.DoesNotContain(reopened.AgentRuntime.ExecutedTurns, execution => execution.Input.Prompt.StartsWith("worker-", StringComparison.Ordinal));

            var next = await activeKernel.CreateSessionAsync(trackId, new(Prompt: "New Worker goal", Task: new()), "fresh-worker", new InteropPrincipal.Human(), default);
            await activeKernel.WaitForTrackIdleAsync(trackId);
            var replacement = Assert.Single(await activeKernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default), summary => summary.TaskId == next.Receipt.Task!.Id).Session;
            Assert.NotEqual(old.Id, replacement.Id);
            Assert.Empty((await activeKernel.GetSessionItemsAsync(replacement.Id, new(), new InteropPrincipal.Human(), default)).QueuedMessages);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ADeclinedQueuedSteerReplaysAgainstItsOriginalTurnAndCanBeSentToANewTurn()
    {
        await using var host = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var endFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steers = 0;
        host.AgentRuntime.SetSteerHandler(_ => Task.FromResult(Interlocked.Increment(ref steers) == 2));
        host.AgentRuntime.SetTurnHandler(Stream);

        try
        {
            using var admitted = await SendAsync(host.Client, sessionId, "first", new { content = "first" });
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
            var firstTurn = await first.Task.WaitAsync(ct);
            using var follower = await SendAsync(host.Client, sessionId, "second", new { content = "second", mode = "queue" });
            Assert.Equal(HttpStatusCode.OK, follower.StatusCode);
            using var pending = await SendAsync(host.Client, sessionId, "content", new { content = "content", mode = "queue" });
            var message = (await pending.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions, ct))!;

            var messageId = message.MessageId;
            var path = $"/api/v1/sessions/{sessionId}/messages";
            async Task<SessionMessageReceipt> SteerAsync(string turnId)
            {
                using var response = await host.Client.PostAsJsonAsync(path, new { messageId, mode = "steer", turnId }, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return (await response.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions, ct))!;
            }

            Assert.Equal("queued", (await SteerAsync(firstTurn)).Delivery);
            Assert.Equal("queued", (await SteerAsync(firstTurn)).Delivery);
            Assert.Equal(1, steers);
            endFirst.TrySetResult();
            var secondTurn = await second.Task.WaitAsync(ct);
            // Both waiting messages were promoted into the second turn. A fresh
            // queued message can still be steered while that batch is running.
            Assert.Equal("queued", (await SteerAsync(firstTurn)).Delivery);
            using var fresh = await SendAsync(host.Client, sessionId, "fresh", new { content = "fresh", mode = "queue" });
            var freshMessage = (await fresh.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions, ct))!;
            messageId = freshMessage.MessageId;
            Assert.Equal("delivered", (await SteerAsync(secondTurn)).Delivery);
            Assert.Equal("delivered", (await SteerAsync(secondTurn)).Delivery);
            Assert.Equal(2, steers);

            var page = await kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), ct);
            Assert.DoesNotContain(page.QueuedMessages, entry => entry.Id == message.MessageId);
            var item = Assert.Single(page.Items, entry => entry.Kind == "user" && entry.Payload.GetProperty("inputId").GetString() == freshMessage.MessageId);
            Assert.Equal(secondTurn, item.TurnId);
            Assert.Equal("delivered", item.InputDelivery);
            Assert.True(item.Payload.GetProperty("steered").GetBoolean());
        }
        finally
        {
            endFirst.TrySetResult();
            endSecond.TrySetResult();
        }

        await kernel.WaitForTrackIdleAsync(trackId, ct);
        Assert.Equal(["first", "second\n\ncontent"], host.AgentRuntime.ExecutedTurns.Select(execution => execution.Input.Prompt));

        async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec _, TurnInput input,
            [EnumeratorCancellation] CancellationToken token)
        {
            yield return new UnifiedAgentEvent.TextDelta("Running");
            if (input.Prompt == "first")
            {
                first.TrySetResult(input.CorrelationId);
                await endFirst.Task.WaitAsync(token);
            }
            else if (input.Prompt == "second\n\ncontent")
            {
                second.TrySetResult(input.CorrelationId);
                await endSecond.Task.WaitAsync(token);
            }
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
    }

    private static Task InsertQueueAsync(ISessionMessageStore store, string sessionId, string key, string delivery)
    {
        var hash = Hash(new { version = 4, Content = key, Mode = "queue", TurnId = (string?)null, ExpectedEpoch = (int?)null });
        return store.InsertAsync(new(key, sessionId, "owner:installation", key, hash, null, null, key, delivery, 1, "queue"), default);
    }

    private static SqliteConnection OpenDatabase(string directory) =>
        new($"Data Source={Path.Combine(directory, "perga.db")};Pooling=False;Foreign Keys=True");

    private static string Hash<T>(T request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string sessionId, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/messages") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
