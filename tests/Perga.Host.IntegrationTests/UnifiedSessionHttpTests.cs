using Dapper;
using Microsoft.Data.Sqlite;
using Perga.ReportDoc;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;

namespace Perga.Host.IntegrationTests;

public sealed class UnifiedSessionHttpTests
{
    [Fact]
    public async Task InitialRunWriteFailureRollsBackSessionAndPersistsFailedTask()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, leadId) = await host.CreateTrackAsync();
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.OpenAsync();
        await db.ExecuteAsync("""
            CREATE TRIGGER fail_initial_run BEFORE INSERT ON tasks
            WHEN NEW.status = 'running'
            BEGIN SELECT RAISE(ABORT, 'initial run write failed'); END;
            """);
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await FlowFixture.AddAsync(kernel, trackId, new FlowNodeSpecDto("write-failure", new("run", Prompt: "inspect")));
        await kernel.WaitForTrackIdleAsync(trackId);
        var failed = Assert.Single(await kernel.ListTasksAsync(trackId));
        Assert.Equal(TaskRunStatus.Failed, failed.Status);
        Assert.Contains("initial run write failed", failed.StatusDetail);
        Assert.Null(failed.SessionId);
        Assert.Equal(0, failed.RunCount);
        Assert.NotNull(failed.FinishedAtMs);
        Assert.Equal("failed", await db.ExecuteScalarAsync<string>("SELECT status FROM tasks WHERE id = @Id", new { failed.Id }));
        Assert.Null(await db.ExecuteScalarAsync<string?>("SELECT session_id FROM tasks WHERE id = @Id", new { failed.Id }));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM agent_sessions WHERE track_id = @TrackId", new { TrackId = trackId }));
        Assert.Empty(await db.QueryAsync("PRAGMA foreign_key_check;"));
    }

    [Theory]
    [InlineData("GET", "tracks/t/schedules")]
    [InlineData("POST", "tracks/t/schedules")]
    [InlineData("DELETE", "tracks/t/schedules/s")]
    [InlineData("POST", "tracks/t/schedules/s/trigger")]
    [InlineData("PUT", "tracks/t/report/blocks/b")]
    [InlineData("POST", "tracks/t/tasks/key/accept")]
    [InlineData("POST", "tracks/t/tasks/key/reject")]
    [InlineData("POST", "tracks/t/tasks/dispatch")]
    [InlineData("GET", "tracks/t/tasks/key/attempts")]
    [InlineData("PUT", "sessions/s/model")]
    [InlineData("PUT", "sessions/s/permission-mode")]
    [InlineData("PATCH", "sessions/s/settings")]
    [InlineData("POST", "sessions/s/messages/steer")]
    [InlineData("POST", "sessions/s/messages/queue/m/steer")]
    [InlineData("DELETE", "sessions/s/messages/queue/m")]
    [InlineData("GET", "sessions/s/inputs/i")]
    [InlineData("POST", "sessions/s/interrupt")]
    [InlineData("POST", "sessions/s/recover")]
    [InlineData("POST", "sessions/s/compact")]
    [InlineData("POST", "tracks/t/asks/a/answer")]
    [InlineData("POST", "tracks/t/executions/e/approvals/a/decision")]
    public async Task RetiredRoutesAreAbsent(string method, string path)
    {
        await using var host = new PergaHostTestCluster();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/" + path)
        { Content = JsonContent.Create(new { }) };
        using var response = await host.Client.SendAsync(request);
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"{method} {path}: {response.StatusCode}");
    }

    [Fact]
    public async Task CreateReplayUpdateNullResetAndMessageContracts()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, _) = await host.CreateTrackAsync();
        var path = $"/api/v1/tracks/{trackId}/sessions";
        using var missing = await host.Client.PostAsJsonAsync(path, new { });
        await Error(missing, HttpStatusCode.BadRequest, "invalid_argument");
        using var created = await Post(host.Client, path, "create", new { title = "Assistant" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var receipt = (await created.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal("create", receipt.RequestKey);
        Assert.Null(receipt.FirstInput);
        using var replay = await Post(host.Client, path, "create", new { title = "Assistant" });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(receipt.Session.Id, (await replay.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions))!.Session.Id);
        using var conflict = await Post(host.Client, path, "create", new { title = "Changed" });
        await Error(conflict, HttpStatusCode.Conflict, "conflict");
        var sessionPath = $"/api/v1/sessions/{receipt.Session.Id}";
        async Task<AgentSessionDto> Update(object body)
        {
            using var response = await host.Client.PatchAsJsonAsync(sessionPath, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions))!;
        }
        var configured = await Update(new { model = "chosen", reasoningEffort = "high", permissionMode = "full" });
        var renamed = await Update(new { title = "Renamed" });
        Assert.Equal((configured.Model, configured.ReasoningEffort, configured.PermissionMode, configured.ConfigVersion),
            (renamed.Model, renamed.ReasoningEffort, renamed.PermissionMode, renamed.ConfigVersion));
        var reset = await Update(new { model = (string?)null, reasoningEffort = (string?)null, permissionMode = (string?)null });
        Assert.Null(reset.Model);
        Assert.Null(reset.ReasoningEffort);
        Assert.Equal("never", reset.PermissionMode);
        Assert.Equal(configured.ConfigVersion + 1, reset.ConfigVersion);
        using var stale = await host.Client.PatchAsJsonAsync(sessionPath, new { expectedConfigVersion = configured.ConfigVersion, model = "stale" });
        await Error(stale, HttpStatusCode.Conflict, "conflict");
        using var noMessageKey = await host.Client.PostAsJsonAsync(sessionPath + "/messages", new { content = "Hello" });
        await Error(noMessageKey, HttpStatusCode.BadRequest, "invalid_argument");
        using var sent = await Post(host.Client, sessionPath + "/messages", "message", new { content = "Hello" });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var message = (await sent.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.False(message.Replay);
        Assert.NotNull(message.TurnId);
        await host.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
        using var repeated = await Post(host.Client, sessionPath + "/messages", "message", new { content = "Hello" });
        var repeatedMessage = (await repeated.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions))!;
        Assert.True(repeatedMessage.Replay);
        Assert.Equal(message.MessageId, repeatedMessage.MessageId);
        Assert.Equal("delivered", repeatedMessage.Delivery);
        using var badControl = await host.Client.PostAsJsonAsync(sessionPath + "/control", new { action = "invented" });
        await Error(badControl, HttpStatusCode.BadRequest, "invalid_argument");
        using var promote = await host.Client.PostAsJsonAsync(sessionPath + "/messages", new { messageId = "missing", mode = "steer", turnId = message.TurnId });
        await Error(promote, HttpStatusCode.NotFound, "not_found");
        using var badRead = await host.Client.GetAsync(sessionPath + "/items?waitMs=10001");
        await Error(badRead, HttpStatusCode.BadRequest, "invalid_argument");
    }

    [Fact]
    public async Task ReadWaitsForActivityAndRespondAnswersTheSessionsAsk()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler(Stream);
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec _, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.TextDelta("start");
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TextDelta("finish");
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        var path = $"/api/v1/sessions/{sessionId}";
        try
        {
            using var sent = await Post(host.Client, path + "/messages", "activity", new { content = "Work" });
            sent.EnsureSuccessStatusCode();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = (await host.Client.GetFromJsonAsync<SessionItemsPageDto>(path + "/items", PergaJsonDefaults.ApiOptions))!;
            var waiting = host.Client.GetFromJsonAsync<SessionItemsPageDto>(path + $"/items?activityTurnId={before.Activity!.TurnId}&afterSequence={before.Activity.Sequence}&waitMs=10000", PergaJsonDefaults.ApiOptions);
            Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(100)));
            release.TrySetResult();
            var after = (await waiting.WaitAsync(TimeSpan.FromSeconds(5)))!;
            Assert.True(after.Activity!.Sequence > before.Activity.Sequence || after.ActiveTurn is null);
        }
        finally { release.TrySetResult(); }
        await kernel.WaitForTrackIdleAsync(trackId);
        var asking = kernel.AskUserAsync(trackId, sessionId, [new AskQuestionDto("Choose", ["yes", "no"])]);
        AskDto? ask = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (ask is null)
        {
            ask = (await kernel.GetTrackAggregateAsync(trackId, timeout.Token)).OpenAsks.FirstOrDefault();
            if (ask is null) await Task.Delay(10, timeout.Token);
        }
        using var response = await host.Client.PostAsJsonAsync(path + $"/interactions/{ask.Id}/respond", new { answers = new[] { "yes" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new SessionInteractionResult(ask.Id, "ask"), await response.Content.ReadFromJsonAsync<SessionInteractionResult>(PergaJsonDefaults.ApiOptions));
        Assert.Equal("yes", (await asking.WaitAsync(TimeSpan.FromSeconds(5)))[0].GetString());
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string key, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, payload.RootElement.GetProperty("code").GetString());
    }
}
