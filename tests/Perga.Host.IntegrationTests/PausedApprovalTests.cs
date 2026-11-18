using Dapper;
using Microsoft.Data.Sqlite;
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
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class PausedApprovalTests
{
    [Theory]
    [InlineData("{\"option\":0,\"value\":\"Approve\"}", true)]
    [InlineData("{\"option\":1,\"value\":\"Deny\"}", false)]
    [InlineData("true", true)]
    [InlineData("{\"option\":0,\"value\":\"Deny\"}", false)]
    public async Task ProviderApprovalPublishesIdentityAndOnlyResumesItsOriginalTurn(string answer, bool approved)
    {
        await using var host = new PergaHostTestCluster();
        host.AgentRuntime.SetTurnHandler(ApprovalTurn);
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var ask = await WaitForHold(host, trackId);
        Assert.Equal(sessionId, ask.SessionId);
        Assert.NotNull(ask.TurnId);
        var page = await host.Client.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{sessionId}/items", PergaJsonDefaults.ApiOptions);
        Assert.Equal(page!.ActiveTurn!.Id, ask.TurnId);
        Assert.Empty(host.AgentRuntime.ApprovalResponses);

        var (otherTrack, _) = await host.CreateTrackAsync();
        using var crossTrack = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{otherTrack}/asks/{ask.Id}/answer", true);
        Assert.Equal(HttpStatusCode.NotFound, crossTrack.StatusCode);
        Assert.Empty(host.AgentRuntime.ApprovalResponses);

        using var response = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/asks/{ask.Id}/answer", JsonSerializer.Deserialize<JsonElement>(answer));
        response.EnsureSuccessStatusCode();
        var answered = (await response.Content.ReadFromJsonAsync<AskDto>(PergaJsonDefaults.ApiOptions))!;
        Assert.Equal(AskDelivery.Hold, answered.Delivery);
        Assert.Equal(ask.SessionId, answered.SessionId);
        Assert.Equal(ask.TurnId, answered.TurnId);
        for (var i = 0; i < 100 && host.AgentRuntime.ApprovalResponses.IsEmpty; i++)
            await Task.Delay(10);
        Assert.Equal(("approval-request", approved), Assert.Single(host.AgentRuntime.ApprovalResponses));
        using var duplicate = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/asks/{ask.Id}/answer", true);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
    }

    [Fact]
    public async Task FailedAnswerEventWriteDoesNotConsumeApprovalAndCanRetry()
    {
        await using var host = new PergaHostTestCluster();
        host.AgentRuntime.SetTurnHandler(ApprovalTurn);
        var (trackId, sessionId) = await host.CreateTrackAsync();
        await StartTurn(host, sessionId);
        var ask = await WaitForHold(host, trackId);
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            CREATE TRIGGER reject_answer_event BEFORE INSERT ON events
            WHEN NEW.kind = 'ask.changed' AND json_extract(NEW.payload_json, '$.status') = 'answered'
            BEGIN SELECT RAISE(ABORT, 'injected answer event failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => kernel.AnswerAskAsync(trackId, ask.Id, JsonSerializer.Deserialize<JsonElement>("true")));
        Assert.Equal(AskStatus.Open, Assert.Single((await Snapshot(host, trackId)).OpenAsks).Status);
        Assert.Equal("open", await connection.ExecuteScalarAsync<string>("SELECT status FROM asks WHERE id = @Id", ask));
        Assert.Empty(host.AgentRuntime.ApprovalResponses);
        await connection.ExecuteAsync("DROP TRIGGER reject_answer_event;");
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/asks/{ask.Id}/answer", true);
        response.EnsureSuccessStatusCode();
        for (var i = 0; i < 100 && host.AgentRuntime.ApprovalResponses.IsEmpty; i++) await Task.Delay(10);
        Assert.True(Assert.Single(host.AgentRuntime.ApprovalResponses).Approved);
    }

    [Fact]
    public async Task ReopenedHoldKeepsIdentityButCannotAuthorizeOrWakeAnotherTurn()
    {
        var directory = Directory.CreateTempSubdirectory("perga-approval-");
        try
        {
            AskDto ask;
            string trackId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                host.AgentRuntime.SetTurnHandler(ApprovalTurn);
                var created = await host.CreateTrackAsync();
                trackId = created.TrackId;
                await StartTurn(host, created.SessionId);
                ask = await WaitForHold(host, trackId);
            }
            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var snapshot = await Snapshot(reopened, trackId);
            var restored = Assert.Single(snapshot.OpenAsks);
            Assert.Equal(ask.Id, restored.Id);
            Assert.Equal(AskDelivery.Hold, restored.Delivery);
            Assert.Equal(ask.SessionId, restored.SessionId);
            Assert.Equal(ask.TurnId, restored.TurnId);
            using var response = await reopened.Client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/asks/{ask.Id}/answer", true);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("conflict", error.RootElement.GetProperty("code").GetString());
            Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
            Assert.Empty(reopened.AgentRuntime.ApprovalResponses);
            Assert.Equal(AskStatus.Open, Assert.Single((await Snapshot(reopened, trackId)).OpenAsks).Status);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task InterruptedTurnExpiresApprovalAndAnonymousQuestionCannotImpersonateIt()
    {
        await using var host = new PergaHostTestCluster();
        host.AgentRuntime.SetTurnHandler(ApprovalTurn);
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await StartTurn(host, sessionId);
        var hold = await WaitForHold(host, trackId);
        using var askCancellation = new CancellationTokenSource();
        var questionTask = kernel.AskUserAsync(trackId, null,
            [new AskQuestionDto("Approve tool 'shell'?", ["Approve", "Deny"])], askCancellation.Token);
        AskDto? question = null;
        for (var i = 0; i < 100 && question is null; i++)
        {
            question = (await Snapshot(host, trackId)).OpenAsks.FirstOrDefault(a => a.Id != hold.Id);
            if (question is null) await Task.Delay(10);
        }
        Assert.NotNull(question);
        Assert.Equal(AskDelivery.Wake, question.Delivery);
        Assert.Null(question.SessionId);
        Assert.Null(question.TurnId);
        askCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => questionTask);

        using var interrupted = await host.Client.PostAsync($"/api/v1/sessions/{sessionId}/interrupt", null);
        interrupted.EnsureSuccessStatusCode();
        using var expired = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/asks/{hold.Id}/answer", true);
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.Empty(host.AgentRuntime.ApprovalResponses);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
    }

    private static async Task StartTurn(PergaHostTestCluster host, string sessionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/messages")
        {
            Content = JsonContent.Create(new { content = "run" })
        };
        request.Headers.Add("Idempotency-Key", "approval");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<TrackAggregateSnapshot> Snapshot(PergaHostTestCluster host, string trackId) =>
        (await host.Client.GetFromJsonAsync<TrackAggregateSnapshot>($"/api/v1/tracks/{trackId}", PergaJsonDefaults.ApiOptions))!;

    private static async Task<AskDto> WaitForHold(PergaHostTestCluster host, string trackId)
    {
        for (var i = 0; i < 100; i++)
        {
            var ask = (await Snapshot(host, trackId)).OpenAsks.FirstOrDefault(a => a.Delivery == AskDelivery.Hold);
            if (ask is not null) return ask;
            await Task.Delay(10);
        }
        throw new TimeoutException("Provider approval was not published.");
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> ApprovalTurn(
        HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        _ = spec;
        _ = input;
        ct.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return new UnifiedAgentEvent.ApprovalRequested("approval-request", "shell", JsonSerializer.Deserialize<JsonElement>("{}"));
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1, 1, 0, 1000));
    }
}
