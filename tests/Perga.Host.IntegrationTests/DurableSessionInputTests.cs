using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.AgentKit;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Kernel.Events;

namespace Perga.Host.IntegrationTests;

public sealed class DurableSessionInputTests
{
    [Theory]
    [InlineData("low")]
    [InlineData(null)]
    public async Task AcceptedSelectionRecoversWithoutUsingCurrentEffort(string? effort)
    {
        var directory = Directory.CreateTempSubdirectory("perga-frozen-input-");
        try
        {
            string trackId;
            string sessionId;
            SessionInputReceipt receipt;
            int initialEpoch;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var selected = await kernel.UpdateSessionAsync(sessionId, new(Model: new("A"), ReasoningEffort: new(effort)), new InteropPrincipal.Human(), default);
                initialEpoch = selected.Epoch;
                var store = host.Services.GetRequiredService<ITrackStore>();
                // Commit the production acceptance transaction, then stop before dispatch.
                receipt = (await store.SessionInputs.AcceptAsync(selected with { Status = "queued" },
                    new("frozen-recovery", "hash", "recover selection"),
                    new(null, null, trackId, KernelEventKinds.SessionUpdated,
                        JsonSerializer.SerializeToElement(selected), selected.UpdatedAtMs), CancellationToken.None)).Receipt;
                using var update = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/sessions/{sessionId}", new { model = "B", reasoningEffort = "high" });
                update.EnsureSuccessStatusCode();
            }
            var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec spec, TurnInput input,
                [EnumeratorCancellation] CancellationToken ct)
            {
                yield return new UnifiedAgentEvent.ContextUsageUpdated(new TokenUsage(10, 5, 0, 1000, UsedTokens: 950));
                sampled.TrySetResult();
                await release.Task.WaitAsync(ct);
                yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(10, 5, 0, 1000, UsedTokens: 990));
            }
            await using (var reopened = new PergaHostTestCluster(directory.FullName,
                configureAgentRuntime: agent => agent.SetTurnHandler(Stream)))
            {
                var resumed = reopened.Services.GetRequiredService<IPergaKernel>();
                var idle = resumed.WaitForTrackIdleAsync(trackId);
                try
                {
                    await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var streaming = Assert.Single((await resumed.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());

                    Assert.Null(streaming.ContextUsage);
                }
                finally { release.TrySetResult(); }
                await idle.WaitAsync(TimeSpan.FromSeconds(10));
                var execution = Assert.Single(reopened.AgentRuntime.ExecutedTurns);
                Assert.Equal(receipt.TurnId, execution.Input.CorrelationId);
                Assert.Equal("A", execution.Spec.Model);
                Assert.Equal(effort, execution.Spec.ReasoningEffort);
                var desired = Assert.Single((await resumed.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());
                Assert.Equal("B", desired.Model);
                Assert.Equal("high", desired.ReasoningEffort);

                Assert.Null(desired.ContextUsage);
                Assert.Equal(initialEpoch, desired.Epoch);
            }
            await using var restarted = new PergaHostTestCluster(directory.FullName);
            var kernelAfterRestart = restarted.Services.GetRequiredService<IPergaKernel>();
            var persisted = Assert.Single((await kernelAfterRestart.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());
            Assert.Null(persisted.ContextUsage);

            await kernelAfterRestart.SendSessionMessageAsync(sessionId, new(Content: "use B", RequestKey: "current-model"), new InteropPrincipal.Human(), default);
            await kernelAfterRestart.WaitForTrackIdleAsync(trackId);
            Assert.Equal("B", Assert.Single(restarted.AgentRuntime.ExecutedTurns).Spec.Model);
            Assert.NotNull(Assert.Single((await kernelAfterRestart.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).ContextUsage);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task SameMessageAfterHostRestartKeepsItsTurnAndDispatchesOnce()
    {
        var directory = Directory.CreateTempSubdirectory("perga-input-");
        try
        {
            string sessionId;
            string trackId;
            string turnId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                using var accepted = await SendAsync(host.AuthenticatedClient, $"/api/v1/sessions/{sessionId}/messages",
                    "same-message", new { content = "Keep one turn" });
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                using var receipt = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
                turnId = receipt.RootElement.GetProperty("turnId").GetString()!;
                await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
                Assert.Single(host.AgentRuntime.ExecutedTurns);
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            using var replay = await SendAsync(reopened.AuthenticatedClient, $"/api/v1/sessions/{sessionId}/messages",
                "same-message", new { content = "Keep one turn" });
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var replayReceipt = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(turnId, replayReceipt.RootElement.GetProperty("turnId").GetString());
            Assert.Equal("delivered", replayReceipt.RootElement.GetProperty("delivery").GetString());
            using var mismatch = await SendAsync(reopened.AuthenticatedClient, $"/api/v1/sessions/{sessionId}/messages",
                "same-message", new { content = "Keep one turn", mode = "queue" });
            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task CreateWithFirstMessageReplaysTheSameSessionInputAndTranscript()
    {
        var directory = Directory.CreateTempSubdirectory("perga-create-input-");
        try
        {
            string trackId;
            SessionCreationReceipt created;
            var request = new { title = "First message", provider = "claude", prompt = "One initial message" };
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, _) = await host.CreateTrackAsync();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.AgentRuntime.SetTurnHandler(StreamAsync);

                async IAsyncEnumerable<UnifiedAgentEvent> StreamAsync(
                    HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    yield return new UnifiedAgentEvent.TextDelta("The stored answer");
                    yield return new UnifiedAgentEvent.TurnCompleted(null);
                }

                var url = $"/api/v1/tracks/{trackId}/sessions";
                var results = await Task.WhenAll(
                    SendAsync(host.AuthenticatedClient, url, "create-key", request),
                    SendAsync(host.AuthenticatedClient, url, "create-key", request));
                using var initial = Assert.Single(results, result => result.StatusCode == HttpStatusCode.Created);
                using var replay = Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
                created = (await initial.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
                Assert.NotNull(created.FirstInput);
                var duplicate = (await replay.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
                Assert.Equal(created.Session.Id, duplicate.Session.Id);
                Assert.Equal(created.FirstInput.InputId, duplicate.FirstInput!.InputId);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await AssertUserItemAsync(host.AuthenticatedClient, created.FirstInput, request.prompt);
                using var mismatch = await SendAsync(host.AuthenticatedClient, url, "create-key", new { prompt = "different" });
                Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
                release.SetResult();
                await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
                Assert.Single(host.AgentRuntime.ExecutedTurns);
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            using var response = await SendAsync(reopened.AuthenticatedClient, $"/api/v1/tracks/{trackId}/sessions", "create-key", request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var recovered = (await response.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
            Assert.Equal(created.Session.Id, recovered.Session.Id);
            Assert.Equal(created.FirstInput!.InputId, recovered.FirstInput!.InputId);
            Assert.Equal("completed", recovered.FirstInput.TurnStatus);
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
            await AssertUserItemAsync(reopened.AuthenticatedClient, recovered.FirstInput, request.prompt);
            using var delete = await reopened.AuthenticatedClient.DeleteAsync($"/api/v1/tracks/{trackId}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            using var gone = await SendAsync(reopened.AuthenticatedClient, $"/api/v1/tracks/{trackId}/sessions", "create-key", request);
            Assert.Equal(HttpStatusCode.Gone, gone.StatusCode);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \t ")]
    public async Task CreateReceiptReplaysAfterHostRestart(string? reasoningEffort)
    {
        var directory = Directory.CreateTempSubdirectory("perga-baseline-create-");
        try
        {
            string trackId;
            SessionCreationReceipt created;
            var request = new { title = "Legacy", provider = "codex", model = (string?)null, prompt = (string?)null, reasoningEffort };
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, _) = await host.CreateTrackAsync();
                using var response = await SendAsync(host.AuthenticatedClient, $"/api/v1/tracks/{trackId}/sessions", "legacy-key", request);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                created = (await response.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var path = $"/api/v1/tracks/{trackId}/sessions";
            using var replay = await SendAsync(reopened.AuthenticatedClient, path, "legacy-key", request);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var recovered = (await replay.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
            Assert.Equal(created.Session.Id, recovered.Session.Id);
            Assert.Null(recovered.FirstInput);
            using var mismatch = await SendAsync(reopened.AuthenticatedClient, path, "legacy-key", request with { reasoningEffort = "high" });
            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("low")]
    [InlineData(" low ")]
    public async Task CreateReceiptRejectsChangedReasoningEffort(string reasoningEffort)
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, _) = await host.CreateTrackAsync();
        var path = $"/api/v1/tracks/{trackId}/sessions";
        var request = new { title = "Assistant", provider = "codex", model = "chosen", reasoningEffort };
        using var created = await SendAsync(host.AuthenticatedClient, path, "effort-key", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var replay = await SendAsync(host.AuthenticatedClient, path, "effort-key", request with { reasoningEffort = "low" });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var mismatch = await SendAsync(host.AuthenticatedClient, path, "effort-key", request with { reasoningEffort = "high" });
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("dispatching")]
    [InlineData("delivered")]
    public async Task RestartDispatchesOnlyUnclaimedInputAndUnknownCanBeAbandoned(string state)
    {
        var directory = Directory.CreateTempSubdirectory("perga-input-recovery-");
        try
        {
            string trackId;
            string sessionId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
                (trackId, sessionId) = await host.CreateTrackAsync();

            // Seed the exact committed rows left at each crash boundary.
            await using (var db = new SqliteConnection($"Data Source={Path.Combine(directory.FullName, "perga.db")};Pooling=False"))
            {
                await db.OpenAsync();
                var turnStatus = state == "accepted" ? "queued" : "running";
                await db.ExecuteAsync(
                    """
                    INSERT INTO session_turns (id, session_id, turn_index, trigger_kind, status, accepted_at_ms, provider, model)
                    VALUES ('fixture-turn', @SessionId, 1, 'user', @TurnStatus, 1, 'codex', 'A');
                    INSERT INTO session_messages (id, session_id, kind, turn_id, author_id, request_key, request_hash, content, created_at_ms, delivery, delivered_turn_id)
                    VALUES ('fixture-input', @SessionId, 'input', 'fixture-turn', 'owner:installation', 'fixture-key',
                            'fixture-hash', 'Recover me', 1, @State, CASE WHEN @State = 'delivered' THEN 'fixture-turn' END);
                    INSERT INTO session_items (session_id, turn_id, kind, payload_json, created_at_ms, message_ordinal)
                    VALUES (@SessionId, 'fixture-turn', 'user', '{}', 1, last_insert_rowid());
                    UPDATE agent_sessions SET status = @TurnStatus, native_thread_id = 'old-thread', model = 'B', reasoning_effort = 'high' WHERE id = @SessionId;
                    """, new { SessionId = sessionId, TurnStatus = turnStatus, State = state == "accepted" ? "queued" : state });
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            await reopened.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
            var path = $"/api/v1/sessions/{sessionId}/items";
            var receipt = (await reopened.AuthenticatedClient.GetFromJsonAsync<SessionItemsPageDto>(path, PergaJsonDefaults.ApiOptions))!;
            if (state == "accepted")
            {
                Assert.Equal("completed", receipt.LastTurn!.Status);
                var execution = Assert.Single(reopened.AgentRuntime.ExecutedTurns);
                Assert.Equal("fixture-turn", execution.Input.CorrelationId);
                Assert.Equal("A", execution.Spec.Model);
                Assert.Null(execution.Spec.ReasoningEffort); // Historical turn has no recorded effort.
                return;
            }

            Assert.Equal("unknown", receipt.LastTurn!.Status);
            Assert.Equal("unconfirmed", Assert.Single(receipt.Items, i => i.Kind == "user").MessageStatus);
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
            using var busy = await SendAsync(reopened.AuthenticatedClient, $"/api/v1/sessions/{sessionId}/messages", "new-key", new { content = "Next" });
            Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
            using var resolve = await reopened.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/control", new { action = "reset_context" });
            resolve.EnsureSuccessStatusCode();
            var resolved = await reopened.AuthenticatedClient.GetFromJsonAsync<SessionItemsPageDto>(path, PergaJsonDefaults.ApiOptions);
            Assert.NotNull((await reopened.Services.GetRequiredService<ITrackStore>().SessionInputs.FindByIdAsync(sessionId, "fixture-input", default))!.Receipt.ResolvedAtMs);
            Assert.Equal("unknown", resolved!.LastTurn!.Status);
            using var next = await SendAsync(reopened.AuthenticatedClient, $"/api/v1/sessions/{sessionId}/messages", "new-key", new { content = "Next" });
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
            await reopened.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
            Assert.Null(Assert.Single(reopened.AgentRuntime.ExecutedTurns).Spec.ResumeNativeThreadId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReopeningLongHistoryPreservesItemsAndAllocatesFromPersistedTurns()
    {
        var directory = Directory.CreateTempSubdirectory("perga-input-history-");
        try
        {
            var dbPath = Path.Combine(directory.FullName, "perga.db");
            string trackId;
            string sessionId;
            await using (var original = new PergaHostTestCluster(directory.FullName))
                (trackId, sessionId) = await original.CreateTrackAsync();
            await using (var db = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                await db.OpenAsync();
                await db.ExecuteAsync(
                    """
                    INSERT INTO session_turns (id, session_id, turn_index, trigger_kind, input_json, status, started_at_ms, completed_at_ms)
                    VALUES ('trn_000001_fixture', @SessionId, 1, 'user', '{}', 'completed', 1, 1),
                           ('trn_000002_fixture', @SessionId, 2, 'user', '{}', 'completed', 2, 2);
                    WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 1001)
                    INSERT INTO session_items (id, session_id, turn_id, kind, payload_json, created_at_ms)
                    SELECT x, @SessionId, CASE WHEN x <= 1000 THEN 'trn_000001_fixture' ELSE 'trn_000002_fixture' END,
                           'assistant_text', '{"v":1,"text":"history"}', x FROM n;
                    """, new { SessionId = sessionId });
            }

            await using var host = new PergaHostTestCluster(directory.FullName);
            using var firstPage = await host.AuthenticatedClient.GetAsync($"/api/v1/sessions/{sessionId}/items?afterId=999&limit=2");
            using var history = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            Assert.Equal(new long[] { 1000, 1001 },
                history.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
            using var latest = await host.AuthenticatedClient.GetAsync($"/api/v1/sessions/{sessionId}/items?latest=true&limit=2");
            using var latestPage = JsonDocument.Parse(await latest.Content.ReadAsStringAsync());
            Assert.Equal(new long[] { 1000, 1001 },
                latestPage.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
            using var earlier = await host.AuthenticatedClient.GetAsync($"/api/v1/sessions/{sessionId}/items?beforeId=1000&limit=2");
            using var earlierPage = JsonDocument.Parse(await earlier.Content.ReadAsStringAsync());
            Assert.Equal(new long[] { 998, 999 },
                earlierPage.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
            using var accepted = await SendAsync(host.AuthenticatedClient, $"/api/v1/sessions/{sessionId}/messages", "long-history", new { content = "Turn three" });
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var receipt = (await accepted.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
            Assert.StartsWith("trn_000003_", receipt.TurnId);
            await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);

            await using var check = new SqliteConnection($"Data Source={dbPath};Pooling=False;Foreign Keys=True");
            await check.OpenAsync();
            var itemId = await check.ExecuteScalarAsync<long>("SELECT i.id FROM session_items i JOIN session_messages m ON m.ordinal = i.message_ordinal WHERE m.id = @InputId;",
                new { InputId = receipt.MessageId });
            Assert.Equal(1002, itemId);
            await Assert.ThrowsAsync<SqliteException>(() => check.ExecuteAsync(
                """
                UPDATE session_items SET turn_id = 'trn_000002_fixture'
                WHERE message_ordinal = (SELECT ordinal FROM session_messages WHERE id = @InputId);
                """, new { InputId = receipt.MessageId }));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FailedFinalizationKeepsDeliveryEvidenceAndBlocksSecondDispatch()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync();
        await db.ExecuteAsync(
            """
            CREATE TRIGGER fail_completed_turn BEFORE UPDATE OF status ON session_turns
            WHEN NEW.status = 'completed'
            BEGIN
                SELECT RAISE(ABORT, 'simulated finalization failure');
            END;
            """);

        var path = $"/api/v1/sessions/{sessionId}/messages";
        using var accepted = await SendAsync(host.AuthenticatedClient, path, "failed-finalization", new { content = "Once" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var initial = (await accepted.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
        var outcome = (await host.AuthenticatedClient.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{sessionId}/items", PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("unknown", outcome.LastTurn!.Status);
        Assert.Equal(initial.TurnId, outcome.LastTurn!.Id);
        using var duplicate = await SendAsync(host.AuthenticatedClient, path, "failed-finalization", new { content = "Once" });
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using var blocked = await SendAsync(host.AuthenticatedClient, path, "different", new { content = "Next" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
    }

    private static async Task AssertUserItemAsync(HttpClient client, SessionInputReceipt receipt, string text)
    {
        using var response = await client.GetAsync($"/api/v1/sessions/{receipt.SessionId}/items");
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(page.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "user");
        Assert.Equal(receipt.TurnId, item.GetProperty("turnId").GetString());
        Assert.Equal(receipt.MessageId, item.GetProperty("payload").GetProperty("inputId").GetString());
        Assert.Equal(text, item.GetProperty("payload").GetProperty("content").GetString());
    }

    [Fact]
    public async Task SilentSteeringDoesNotResendAndAnEndedTargetIsRejected()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverAnswers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler(Stream);
        host.AgentRuntime.SetSteerHandler(text =>
        {
            if (text != "silent supplement") return Task.FromResult(true);
            steerStarted.TrySetResult();
            return neverAnswers.Task;
        });

        async IAsyncEnumerable<UnifiedAgentEvent> Stream(
            HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.TextDelta("working");
            if (input.Prompt == "initial")
            {
                turnStarted.TrySetResult();
                await endTurn.Task.WaitAsync(ct);
            }
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }

        var messages = $"/api/v1/sessions/{sessionId}/messages";
        using var accepted = await SendAsync(host.AuthenticatedClient, messages, "initial-key", new { content = "initial" });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var initial = (await accepted.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task<SessionMessageReceipt> SteerAsync(string content) => PostSteerAsync(initial.TurnId!, content);
        async Task<SessionMessageReceipt> PostSteerAsync(string turnId, string content)
        {
            using var response = await SendAsync(host.AuthenticatedClient, messages, Guid.NewGuid().ToString("N"), new { mode = "steer", turnId, content });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        }

        try
        {
            var first = SteerAsync("silent supplement");
            await steerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = SteerAsync("another supplement");
            Assert.Equal("unknown", (await first.WaitAsync(TimeSpan.FromSeconds(10))).Delivery);
            Assert.Equal("delivered", (await second.WaitAsync(TimeSpan.FromSeconds(10))).Delivery);

            endTurn.TrySetResult();
            await kernel.WaitForTrackIdleAsync(trackId);
            Assert.Single(host.AgentRuntime.ExecutedTurns);

            using var invalid = await SendAsync(host.AuthenticatedClient, messages, Guid.NewGuid().ToString("N"),
                new { mode = "steer", turnId = "invented-turn", content = "must not run" });
            Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);

            using var stale = await SendAsync(host.AuthenticatedClient, messages, Guid.NewGuid().ToString("N"),
                new { mode = "steer", turnId = initial.TurnId, content = "Do not retarget" });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            await kernel.WaitForTrackIdleAsync(trackId);
            Assert.Single(host.AgentRuntime.ExecutedTurns);
        }
        finally
        {
            neverAnswers.TrySetResult(true);
            endTurn.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingMessagesDrainInOrderAndUnconfirmedSteeringDoesNotRunAgain(bool supportsSteering)
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followUpSending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowProviderOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followUpStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endFollowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverAnswers = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SupportsSteering = supportsSteering;
        host.AgentRuntime.SetTurnHandler(Stream);
        host.AgentRuntime.SetSteerHandler(text =>
        {
            Assert.Equal("first queued", text);
            steerStarted.TrySetResult();
            return neverAnswers.Task;
        });

        async IAsyncEnumerable<UnifiedAgentEvent> Stream(
            HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
        {
            if (input.Prompt.EndsWith("second queued", StringComparison.Ordinal))
            {
                followUpSending.TrySetResult();
                await allowProviderOutput.Task.WaitAsync(ct);
            }
            yield return new UnifiedAgentEvent.TextDelta("working");
            if (input.Prompt == "initial")
            {
                turnStarted.TrySetResult();
                await endTurn.Task.WaitAsync(ct);
            }
            if (input.Prompt.EndsWith("second queued", StringComparison.Ordinal))
            {
                followUpStarted.TrySetResult();
                await endFollowUp.Task.WaitAsync(ct);
            }
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }

        var messages = $"/api/v1/sessions/{sessionId}/messages";
        async Task<SessionItemsPageDto> ReadPageAsync() =>
            (await host.AuthenticatedClient.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{sessionId}/items",
                PergaJsonDefaults.ApiOptions))!;
        using var accepted = await SendAsync(host.AuthenticatedClient, messages, "initial", new { content = "initial" });
        var initial = (await accepted.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(initial.MessageId, initial.MessageId);
        await turnStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var processingInitial = await ReadPageAsync();
        Assert.Contains(processingInitial.Items, item => item.Kind == "user" && item.MessageStatus == "processing");

        async Task<SessionMessageReceipt> QueueAsync(string text, HttpStatusCode expected)
        {
            using var response = await SendAsync(host.AuthenticatedClient, messages, text, new { content = text, mode = "queue" });
            Assert.Equal(expected, response.StatusCode);
            Assert.Null(response.Headers.Location);
            return (await response.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        }

        try
        {
            var first = await QueueAsync("first queued", HttpStatusCode.OK);
            var second = await QueueAsync("second queued", HttpStatusCode.OK);
            Assert.Equal(first with { Replay = true }, await QueueAsync("first queued", HttpStatusCode.OK));
            using var stored = await host.AuthenticatedClient.GetAsync($"/api/v1/sessions/{sessionId}/inputs/{first.MessageId}");
            Assert.Equal(HttpStatusCode.NotFound, stored.StatusCode);
            var page = await ReadPageAsync();
            Assert.Equal(new[] { first.MessageId, second.MessageId }, page.QueuedMessages.Select(message => message.Id));
            Assert.Equal(supportsSteering, page.CanSteer);
            Assert.False(steerStarted.Task.IsCompleted);

            var attempt = host.AuthenticatedClient.PostAsJsonAsync(
                messages, new { messageId = first.MessageId, mode = "steer", turnId = initial.TurnId });
            if (supportsSteering)
                await steerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await attempt.WaitAsync(TimeSpan.FromSeconds(5));

            endTurn.TrySetResult();
            using var result = await attempt.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(supportsSteering ? HttpStatusCode.OK : HttpStatusCode.Conflict, result.StatusCode);
            if (supportsSteering)
            {
                var delivered = (await result.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
                Assert.Equal("unknown", delivered.Delivery);
            }
            await followUpSending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var beforeProvider = await ReadPageAsync();
            Assert.DoesNotContain(beforeProvider.QueuedMessages, message => message.Id == second.MessageId);
            Assert.Contains(second.MessageId, beforeProvider.QueueHandoffIds);
            using var tooLate = await host.AuthenticatedClient.DeleteAsync($"{messages}/{second.MessageId}");
            Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
            Assert.Equal("sending", Assert.Single(beforeProvider.Items, item => item.Kind == "user" &&
                item.Payload.TryGetProperty("inputId", out var id) && id.GetString() == second.MessageId).MessageStatus);
            allowProviderOutput.TrySetResult();
            await followUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var duringFollowUp = await ReadPageAsync();
            Assert.DoesNotContain(duringFollowUp.QueuedMessages, message => message.Id == second.MessageId);
            Assert.Equal("processing", Assert.Single(duringFollowUp.Items, item => item.Kind == "user" &&
                item.Payload.TryGetProperty("inputId", out var id) && id.GetString() == second.MessageId).MessageStatus);
            endFollowUp.TrySetResult();
            await kernel.WaitForTrackIdleAsync(trackId);

            Assert.Equal(supportsSteering ? new[] { "initial", "second queued" } : new[] { "initial", "first queued\n\nsecond queued" },
                host.AgentRuntime.ExecutedTurns.Select(execution => execution.Input.Prompt));
            var final = await ReadPageAsync();
            Assert.Equal("completed", Assert.Single(final.Items, item => item.Kind == "user" &&
                item.Payload.TryGetProperty("inputId", out var id) && id.GetString() == second.MessageId).MessageStatus);
            if (supportsSteering)
            {
                Assert.Equal("unconfirmed", Assert.Single(final.QueuedMessages).State);
                using var removed = await host.AuthenticatedClient.DeleteAsync($"{messages}/{first.MessageId}");
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
                Assert.Empty((await ReadPageAsync()).QueuedMessages);
            }
            else
                Assert.Empty(final.QueuedMessages);
        }
        finally
        {
            neverAnswers.TrySetResult(true);
            allowProviderOutput.TrySetResult();
            endTurn.TrySetResult();
            endFollowUp.TrySetResult();
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }
}
