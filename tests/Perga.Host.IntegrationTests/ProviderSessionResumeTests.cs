using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;

namespace Perga.Host.IntegrationTests;

public sealed class ProviderSessionResumeTests
{
    [Fact]
    public async Task ResumeRequiresATargetTurn()
    {
        await using var host = new PergaHostTestCluster();
        var (_, sessionId) = await host.CreateTrackAsync();
        using var response = await host.Client.PostAsJsonAsync(
            $"/api/v1/sessions/{sessionId}/resume", new { turnId = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("terminal", false)]
    [InlineData("scoped-close", false)]
    [InlineData("scoped-close", true)]
    [InlineData("unexpected-end", true)]
    [InlineData("unconfirmed", false)]
    public async Task RecoveryRebindsOnlyAfterStopEvidenceAndNeverResendsTheOldPrompt(string stop, bool queuedFollowUp)
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, sessionId) = await host.CreateTrackAsync();
        var primeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPrime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldTurnStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        host.AgentRuntime.SetTurnHandler(StreamAsync);
        host.AgentRuntime.CancelHandler = token =>
        {
            if (stop == "terminal")
                endOld.TrySetResult();
            return stop == "unconfirmed" ? Task.Delay(Timeout.InfiniteTimeSpan, token) : Task.CompletedTask;
        };
        if (stop is "scoped-close" or "unexpected-end")
            host.AgentRuntime.CloseHandler = _ => Task.FromResult(true);

        async IAsyncEnumerable<UnifiedAgentEvent> StreamAsync(
            HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.TextDelta("provided evidence");
            if (input.Prompt == "prime")
            {
                primeStarted.TrySetResult();
                await finishPrime.Task.WaitAsync(ct);
                yield return new UnifiedAgentEvent.TurnCompleted(null);
            }
            else if (input.Prompt == "old prompt")
            {
                oldStarted.TrySetResult();
                await endOld.Task.WaitAsync(ct);
                oldTurnStopped.TrySetResult();
                yield return stop == "unexpected-end"
                    ? new UnifiedAgentEvent.TurnError("connection_lost", "Provider disconnected.", IsRetryable: false)
                    : new UnifiedAgentEvent.TurnInterrupted();
            }
            else
                yield return new UnifiedAgentEvent.TurnCompleted(null);
        }

        var path = $"/api/v1/sessions/{sessionId}";
        async Task<HttpResponseMessage> PostAsync(string key, string text)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path + "/messages")
            {
                Content = JsonContent.Create(new { content = text, queueIfBusy = true }),
            };
            request.Headers.Add("Idempotency-Key", key);
            return await host.Client.SendAsync(request);
        }

        try
        {
            if (queuedFollowUp)
            {
                using var primed = await PostAsync("prime", "prime");
                primed.EnsureSuccessStatusCode();
                await primeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            using var sent = await PostAsync("old", "old prompt");
            Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
            var receipt = queuedFollowUp ? null :
                await sent.Content.ReadFromJsonAsync<SessionInputReceipt>(PergaJsonDefaults.ApiOptions);
            Assert.Equal(!queuedFollowUp, sent.Headers.Location is not null);
            finishPrime.TrySetResult();
            await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var original = await kernel.GetSessionItemsAsync(sessionId);
            Assert.Equal("available", original.Resume?.State);
            var turnId = original.Resume!.TurnId;
            var nativeId = Assert.Single(host.AgentRuntime.OpenedSessions).NativeThreadId;
            Assert.False(string.IsNullOrWhiteSpace(nativeId));

            using var queued = await PostAsync("next", "queued prompt");
            Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
            var action = path + "/resume";
            if (stop == "unexpected-end")
            {
                endOld.TrySetResult();
                await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
                var paused = await kernel.GetSessionItemsAsync(sessionId);
                Assert.Equal("paused", paused.Resume?.State);
                Assert.Single(paused.QueuedMessages);
            }

            using var answer = await host.Client.PostAsJsonAsync(action, new { turnId });
            if (stop == "unconfirmed")
            {
                Assert.Equal(HttpStatusCode.Conflict, answer.StatusCode);
                var held = await kernel.GetSessionItemsAsync(sessionId);
                Assert.Equal("paused", held.Resume?.State);
                Assert.Single(held.QueuedMessages);
                Assert.Single(host.AgentRuntime.OpenedSessions);
                Assert.Single(host.AgentRuntime.ExecutedTurns);

                // The old adapter later supplies a terminal event. A new manual attempt can use that evidence.
                endOld.TrySetResult();
                await oldTurnStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Single(host.AgentRuntime.ExecutedTurns);
                using var retry = await host.Client.PostAsJsonAsync(action, new { turnId });
                retry.EnsureSuccessStatusCode();
            }
            else
            {
                answer.EnsureSuccessStatusCode();
                var restored = (await answer.Content.ReadFromJsonAsync<AgentSessionDto>(PergaJsonDefaults.ApiOptions))!;
                Assert.Equal(original.Session.Epoch, restored.Epoch);
                Assert.Equal(nativeId, restored.NativeThreadId);
            }

            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, host.AgentRuntime.OpenedSessions.Count);
            Assert.Equal(nativeId, host.AgentRuntime.OpenedSessions.Last().Spec.ResumeNativeThreadId);
            Assert.Equal(queuedFollowUp ? new[] { "prime", "old prompt", "queued prompt" } : new[] { "old prompt", "queued prompt" },
                host.AgentRuntime.ExecutedTurns.Select(turn => turn.Input.Prompt));
            if (stop == "scoped-close" && receipt is not null)
            {
                var settled = await kernel.GetSessionInputAsync(sessionId, receipt.InputId, CancellationToken.None);
                Assert.Equal("unknown", settled.TurnStatus);
                Assert.Equal("delivered", settled.DeliveryState);
                Assert.NotNull(settled.ResolvedAtMs);
            }
        }
        finally
        {
            finishPrime.TrySetResult();
            endOld.TrySetResult();
        }
    }

    [Fact]
    public async Task UnconfirmedStopFenceSurvivesHostRestart()
    {
        var data = Directory.CreateTempSubdirectory("perga-resume-fence-");
        try
        {
            string path;
            string turnId;
            string inputId;
            await using (var host = new PergaHostTestCluster(data.FullName))
            {
                var (_, sessionId) = await host.CreateTrackAsync();
                path = $"/api/v1/sessions/{sessionId}";
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.AgentRuntime.SetTurnHandler(StreamAsync);
                host.AgentRuntime.CancelHandler = token => Task.Delay(Timeout.InfiniteTimeSpan, token);

                async IAsyncEnumerable<UnifiedAgentEvent> StreamAsync(
                    HarnessLaunchSpec spec, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
                {
                    yield return new UnifiedAgentEvent.TextDelta("waiting for terminal outcome");
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, path + "/messages")
                {
                    Content = JsonContent.Create(new { content = "Uncertain external work" }),
                };
                request.Headers.Add("Idempotency-Key", "initial");
                using var sent = await host.Client.SendAsync(request);
                sent.EnsureSuccessStatusCode();
                var receipt = (await sent.Content.ReadFromJsonAsync<SessionInputReceipt>(PergaJsonDefaults.ApiOptions))!;
                inputId = receipt.InputId;
                turnId = receipt.TurnId;
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var paused = await host.Client.PostAsJsonAsync(path + "/resume", new { turnId });
                Assert.Equal(HttpStatusCode.Conflict, paused.StatusCode);
            }

            await using var reopened = new PergaHostTestCluster(data.FullName);
            var page = await reopened.Client.GetFromJsonAsync<SessionItemsPageDto>(path + "/items", PergaJsonDefaults.ApiOptions);
            Assert.Equal("paused", page?.Resume?.State);
            Assert.Equal(turnId, page?.Resume?.TurnId);
            Assert.False(page?.Resume?.CanStartFresh);
            using var retry = await reopened.Client.PostAsJsonAsync(path + "/resume", new { turnId });
            Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
            using var abandon = await reopened.Client.PostAsync(path + $"/inputs/{inputId}/abandon", null);
            Assert.Equal(HttpStatusCode.Conflict, abandon.StatusCode);
            Assert.Empty(reopened.AgentRuntime.OpenedSessions);
        }
        finally
        {
            data.Delete(recursive: true);
        }
    }
}
