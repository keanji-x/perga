using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class SessionInputBatchRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshHostRecoversWholeBatchOnceAndUnlocksAllUnknownMembers(bool interrupted)
    {
        var directory = Directory.CreateTempSubdirectory("perga-batch-restart-");
        try
        {
            string trackId;
            string sessionId;
            string turnId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                (trackId, sessionId) = await host.CreateTrackAsync();
                var store = host.Services.GetRequiredService<ITrackStore>();
                var session = Assert.Single((await store.LoadTrackAggregateAsync(trackId, default))!.Sessions);
                var first = new SessionMessage("first", sessionId, "owner:installation", "first", "hash-first",
                    null, null, "First prompt", "queued", 1, "queue");
                await store.SessionMessages!.InsertAsync(first, default);
                await store.SessionMessages.InsertAsync(first with { Id = "second", RequestKey = "second", Content = "Second prompt" }, default);
                var evt = new EventEnvelopeDto(null, null, trackId, KernelEventKinds.SessionUpdated,
                    System.Text.Json.JsonSerializer.SerializeToElement(session), 1);
                // Stop at the actual transaction boundary before dispatch, or after claiming.
                var input = (await store.SessionInputs.PromoteQueuedBatchAsync(session with { Status = "queued" }, first, evt, default))!;
                turnId = input.Receipt.TurnId;
                if (interrupted)
                    Assert.True(await store.SessionInputs.ClaimAsync(input, session with { Status = "running" }, evt, default));
            }
            await using (var reopened = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = reopened.Services.GetRequiredService<IPergaKernel>();
                var store = reopened.Services.GetRequiredService<ITrackStore>();
                await kernel.WaitForTrackIdleAsync(trackId);
                if (interrupted)
                    Assert.Empty(reopened.AgentRuntime.ExecutedTurns);
                else
                {
                    var execution = Assert.Single(reopened.AgentRuntime.ExecutedTurns);
                    Assert.Equal("First prompt\n\nSecond prompt", execution.Input.PromptWithoutReportState());
                    Assert.Equal(turnId, execution.Input.CorrelationId);
                }
                var bubbles = (await kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default)).Items.Where(i => i.Kind == "user").ToArray();
                Assert.Equal(["First prompt", "Second prompt"], bubbles.Select(i => i.Payload.GetProperty("content").GetString()));
                Assert.All(bubbles, bubble => Assert.Equal(turnId, bubble.TurnId));
                Assert.Single(await store.ListSessionTurnsAsync(sessionId, default));
                Assert.Single(await store.ListEventsSinceAsync(0, trackId), e => e.Kind == KernelEventKinds.TurnCompleted);
                foreach (var id in new[] { "first", "second" })
                {
                    var page = (await reopened.AuthenticatedClient.GetFromJsonAsync<SessionItemsPageDto>($"/api/v1/sessions/{sessionId}/items", PergaJsonDefaults.ApiOptions))!;
                    var item = Assert.Single(page.Items, i => i.Kind == "user" && i.Payload.GetProperty("inputId").GetString() == id);
                    Assert.Equal(turnId, item.TurnId);
                    Assert.Equal(interrupted ? "unconfirmed" : "completed", item.MessageStatus);
                }
                if (interrupted)
                {
                    using var unlock = await reopened.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{sessionId}/control", new { action = "reset_context" });
                    Assert.Equal(HttpStatusCode.OK, unlock.StatusCode);
                    foreach (var id in new[] { "first", "second" })
                        Assert.NotNull((await store.SessionInputs.FindByIdAsync(sessionId, id, default))!.Receipt.ResolvedAtMs);
                    Assert.Null(await store.SessionInputs.FindUnresolvedAsync(sessionId, default));
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/messages")
                    { Content = JsonContent.Create(new { content = "After unlock" }) };
                    request.Headers.Add("Idempotency-Key", "after-unlock");
                    using var response = await reopened.AuthenticatedClient.SendAsync(request);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    await kernel.WaitForTrackIdleAsync(trackId);
                    Assert.Equal("After unlock", Assert.Single(reopened.AgentRuntime.ExecutedTurns).Input.PromptWithoutReportState());
                }
            }
            await using var again = new PergaHostTestCluster(directory.FullName);
            await again.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(trackId);
            Assert.Empty(again.AgentRuntime.ExecutedTurns);
        }
        finally { directory.Delete(recursive: true); }
    }
}
