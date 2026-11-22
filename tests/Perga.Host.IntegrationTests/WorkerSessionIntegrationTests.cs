using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Events;
using Perga.Kernel.Ports;
using Perga.ReportDoc;
using static Perga.Host.IntegrationTests.McpKernelAndVcsIntegrationTests;

namespace Perga.Host.IntegrationTests;

public sealed class WorkerSessionIntegrationTests
{
    private static async Task<(string Text, bool Error)> CallFromRunningLeadAsync(PergaHostTestCluster host,
        string path, string sessionId, string tool, object arguments, CancellationToken ct)
    {
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var session = (await kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), ct)).Session;
        var prompt = "MCP caller " + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeTurn = async (input, turnCt) =>
        {
            if (input.Prompt != prompt) return;
            started.TrySetResult();
            await release.Task.WaitAsync(turnCt);
        };
        // The preceding turn can still be releasing its dispatch slot after its state becomes idle.
        await kernel.SendSessionMessageAsync(sessionId, new(Content: prompt, RequestKey: prompt, Mode: "queue"),
            new InteropPrincipal.Human(), ct);
        await started.Task.WaitAsync(ct);
        try
        {
            var registration = kernel.McpSessionBridge.RegisterSession(session.TrackId, sessionId, true, null, null, []);
            return await CallToolAsync(host, path, registration.Principal.Token, tool, arguments, ct);
        }
        finally
        {
            release.TrySetResult();
            while ((await kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), ct)).State == "running")
                await Task.Delay(5, ct);
            host.AgentRuntime.BeforeTurn = null;
        }
    }

    [Fact]
    public async Task WorkerSettingsUseSessionConfigurationVersion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, _) = await host.CreateTrackAsync();
        var dispatched = await FlowFixture.StartWorkerAsync(kernel, trackId, "settings", "Inspect the report", ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId == dispatched.Task.Id).Session;
        var saved = await kernel.UpdateSessionAsync(worker.Id, new(ExpectedConfigVersion: 0, Model: new("gpt-5.4"), ReasoningEffort: new("high")), new InteropPrincipal.Human(), ct);
        var stored = Assert.Single((await host.Services.GetRequiredService<ITrackStore>().LoadTrackAggregateAsync(trackId, ct))!.Sessions,
            session => session.Id == worker.Id);
        Assert.Equal(saved.ConfigVersion, stored.ConfigVersion);
        Assert.Equal(saved.Model, stored.Model);
        Assert.Equal(saved.ReasoningEffort, stored.ReasoningEffort);
        await Assert.ThrowsAsync<KernelException>(() => kernel.UpdateSessionAsync(worker.Id, new(ExpectedConfigVersion: 0, Model: new(null), ReasoningEffort: new(null)), new InteropPrincipal.Human(), ct));
    }

    [Fact]
    public async Task WorkerModelChangeClearsThePreviousWindow()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, _) = await host.CreateTrackAsync();
        host.AgentRuntime.SetTurnHandler(Stream);
        var dispatched = await FlowFixture.StartWorkerAsync(kernel, trackId, "worker-context", "Measure context", ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId == dispatched.Task.Id).Session;
        Assert.Equal(1000, worker.ContextUsage?.WindowTokens);
        using var patch = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/sessions/{worker.Id}",
            new { expectedConfigVersion = 0, model = "gpt-5.4-mini", reasoningEffort = "low" }, ct);
        patch.EnsureSuccessStatusCode();
        var cleared = await kernel.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), ct);
        Assert.Null(cleared.Session.ContextUsage);

        await kernel.SendSessionMessageAsync(worker.Id, new(Content: "After model switch", RequestKey: "after-model-switch", Mode: "default", TurnId: null, ExpectedEpoch: worker.Epoch), new InteropPrincipal.Human(), ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var updated = await kernel.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), ct);
        var usage = Assert.IsType<SessionContextUsage>(updated.Session.ContextUsage);
        Assert.Equal(900, usage.UsedTokens);
        Assert.Null(usage.WindowTokens);
        Assert.Null(usage.Ratio);

        static async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var switched = input.Model == "gpt-5.4-mini";
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(switched ? 900 : 150, 0, 0, switched ? null : 1000));
        }
    }

    [Fact]
    public async Task ExplicitQueueOnANativeWorkerWaitsAndReplaysTheSameReceipt()
    {
        await using var host = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, leadId) = await host.CreateTrackAsync();
        var lead = kernel.McpSessionBridge.RegisterSession(trackId, leadId, true, null, null, []);
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var steers = 0;
        host.AgentRuntime.SetSteerHandler(_ => { Interlocked.Increment(ref steers); return Task.FromResult(true); });
        host.AgentRuntime.SetTurnHandler(Turns);

        var dispatched = await FlowFixture.StartWorkerAsync(kernel, trackId, "dispatch", "Inspect the report", ct);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId == dispatched.Task.Id).Session;
        var leadQueue = new { sessionId = worker.Id, content = "Lead follow-up", mode = "queue", requestKey = "lead-queue" };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{worker.Id}/messages")
            {
                Content = JsonContent.Create(new { content = "Look at dates", mode = "queue" })
            };
            request.Headers.Add("Idempotency-Key", "queued-request");
            using var response = await host.AuthenticatedClient.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var receipt = (await response.Content.ReadFromJsonAsync<SessionMessageReceipt>(PergaJsonDefaults.ApiOptions, ct))!;
            Assert.Equal("queued", receipt.Delivery);
            Assert.Equal(0, steers);
            var turnId = await started.Task.WaitAsync(ct);

            var (replayed, replayError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionSend,
                leadQueue, ct);
            Assert.False(replayError, replayed);
            var (again, againError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionSend,
                leadQueue, ct);
            Assert.False(againError, again);
            Assert.Equal(JsonSerializer.Deserialize<SessionMessageReceipt>(replayed, PergaJsonDefaults.ApiOptions)!.MessageId,
                JsonSerializer.Deserialize<SessionMessageReceipt>(again, PergaJsonDefaults.ApiOptions)!.MessageId);
            Assert.Equal(0, steers);
            Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt.Contains("Look at dates", StringComparison.Ordinal));
            var extra = new { sessionId = worker.Id, content = "Withdraw this", mode = "queue", requestKey = "withdraw" };
            var (extraText, extraError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionSend, extra, ct);
            Assert.False(extraError, extraText);
            var extraReceipt = JsonSerializer.Deserialize<SessionMessageReceipt>(extraText, PergaJsonDefaults.ApiOptions)!;
            var queuePath = $"/api/v1/sessions/{worker.Id}/messages/{extraReceipt.MessageId}";
            var workerPage = await kernel.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), ct);
            Assert.Contains(workerPage.QueuedMessages, entry => entry.Id == extraReceipt.MessageId);
            using var withdrawn = await host.AuthenticatedClient.DeleteAsync(queuePath, ct);
            Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
            var (withdrawAgain, withdrawError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionWithdraw,
                new { sessionId = worker.Id, messageId = extraReceipt.MessageId }, ct);
            Assert.False(withdrawError, withdrawAgain);
            var (replayWithdrawal, withdrawalError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionSend, extra, ct);
            Assert.False(withdrawalError, replayWithdrawal);
            Assert.Equal("withdrawn", JsonSerializer.Deserialize<SessionMessageReceipt>(replayWithdrawal, PergaJsonDefaults.ApiOptions)!.Delivery);
            var (wrongTarget, wrongTargetError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionSend,
                new { sessionId = worker.Id, turnId, content = "Wrong queue target", mode = "queue", requestKey = "bad-queue" }, ct);
            Assert.True(wrongTargetError);
            Assert.Contains("valid delivery mode", wrongTarget);
            var (stale, staleError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionSend,
                new { sessionId = worker.Id, turnId = "old-turn", content = "Stale steer", mode = "steer", requestKey = "stale" }, ct);
            Assert.True(staleError);
            Assert.Contains("not_found", stale);
        }
        finally
        {
            release.TrySetResult();
        }
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "Look at dates\n\nLead follow-up");
        Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt.Contains("Withdraw this", StringComparison.Ordinal));
        var (delivered, deliveredError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionSend, leadQueue, ct);
        Assert.False(deliveredError, delivered);
        Assert.Equal("delivered", JsonSerializer.Deserialize<SessionMessageReceipt>(delivered, PergaJsonDefaults.ApiOptions)!.Delivery);
        var persisted = await kernel.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), ct);
        var followup = Assert.Single(persisted.Items, item => item.Kind == "user" &&
            item.Payload.GetProperty("content").GetString() == "Lead follow-up");
        Assert.Equal("delivered", followup.InputDelivery);
        await kernel.CancelTaskAsync(trackId, leadId, dispatched.Task.TaskKey, ct: ct);
        var (afterCancel, afterCancelError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionSend,
            new { sessionId = worker.Id, turnId = "old-turn", content = "Stale steer", mode = "steer", requestKey = "stale" }, ct);
        Assert.True(afterCancelError);
        Assert.Contains("not_found", afterCancel);

        async IAsyncEnumerable<UnifiedAgentEvent> Turns(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken token)
        {
            if (input.Prompt.Contains("Execute contract task", StringComparison.Ordinal))
            {
                started.TrySetResult(input.CorrelationId);
                await release.Task.WaitAsync(token);
            }
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
    }

    [Fact]
    public async Task RestartInterruptsAcceptedInputToAnInterruptedTaskSession()
    {
        var directory = Directory.CreateTempSubdirectory("perga-legacy-worker-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        try
        {
            string trackId;
            SessionInputReceipt input;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var track = await host.CreateTrackAsync();
                trackId = track.TrackId;
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var receipt = await FlowFixture.StartWorkerAsync(kernel, trackId, "task-restart", "Inspect the report", ct);
                await kernel.WaitForTrackIdleAsync(trackId, ct);
                var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId == receipt.Task.Id).Session;
                var store = host.Services.GetRequiredService<ITrackStore>();
                var evt = new EventEnvelopeDto(null, null, trackId, KernelEventKinds.SessionUpdated,
                    JsonSerializer.SerializeToElement(worker), worker.UpdatedAtMs);
                input = (await store.SessionInputs.AcceptAsync(worker with { Status = "queued" },
                    new SessionInputRequest("legacy-input", "request-hash", "Do not run after restart"), evt, ct)).Receipt;
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var resumed = reopened.Services.GetRequiredService<IPergaKernel>();
            await resumed.WaitForTrackIdleAsync(trackId, ct);
            var outcome = (await reopened.Services.GetRequiredService<ITrackStore>().SessionInputs.FindByIdAsync(input.SessionId, input.InputId, ct))!.Receipt;
            Assert.Equal("interrupted", outcome.DeliveryState);
            Assert.DoesNotContain(reopened.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "Do not run after restart");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task HumanAndLeadShareLiveActivityAndCanSteerInterruptAndResumeTheSameAttempt()
    {
        await using var host = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Worker control", DefaultCwd: host.DataDir), ct);
        var track = (await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Cwd: host.DataDir), ct)).Track;
        var leadId = track.LeadSessionId!;
        var lead = kernel.McpSessionBridge.RegisterSession(track.Id, leadId, true, null, null, []);
        var firstTurn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var continued = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var steers = new List<string>();
        host.AgentRuntime.SetSteerHandler(message =>
        {
            steers.Add(message);
            return Task.FromResult(true);
        });
        host.AgentRuntime.SetTurnHandler(WorkerTurns);

        await FlowFixture.AddAsync(kernel, track.Id, new FlowNodeSpecDto("inspect",
            new("run", Prompt: "Inspect the report", Access: "read_only")));
        var turnId = await firstTurn.Task.WaitAsync(ct);
        var workerSummary = Assert.Single(await kernel.ListSessionsAsync(track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId is not null);
        var worker = workerSummary.Session;
        var taskId = workerSummary.TaskId!;
        var sessionPath = $"/api/v1/sessions/{worker.Id}";
        var httpPage = await ReadPage();
        Assert.Equal(turnId, httpPage.ActiveTurn!.Id);
        Assert.Contains(httpPage.Activity!.Events, evt => evt.Payload.ToString().Contains("Reading the report", StringComparison.Ordinal));

        var (readText, readError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId,
            KernelMcpToolNames.SessionRead, new { sessionId = worker.Id }, ct);
        Assert.False(readError, readText);
        var leadPage = JsonSerializer.Deserialize<SessionItemsPageDto>(readText, PergaJsonDefaults.ApiOptions)!;
        Assert.Equal(httpPage.Activity!.Sequence, leadPage.Activity!.Sequence);
        Assert.Equal(taskId, leadPage.TaskBinding!.Task.Id);

        var request = new { sessionId = worker.Id, turnId, mode = "steer", content = "Check the dates", requestKey = "lead-steer" };
        for (var index = 0; index < 2; index++)
        {
            var (receiptText, receiptError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId,
                KernelMcpToolNames.SessionSend, request, ct);
            Assert.False(receiptError, receiptText);
            Assert.Equal("delivered", JsonSerializer.Deserialize<SessionMessageReceipt>(receiptText, PergaJsonDefaults.ApiOptions)!.Delivery);
        }
        Assert.Equal(["Check the dates"], steers);
        Assert.Contains((await ReadPage()).Items, item => item.Kind == "user" && item.Payload.ToString().Contains("Check the dates", StringComparison.Ordinal));

        await kernel.UpdateSessionAsync(worker.Id, new(ExpectedConfigVersion: 0, Model: new("gpt-5.4"), ReasoningEffort: new("high")), new InteropPrincipal.Human(), ct);
        Assert.Equal(1, (await ReadPage()).Session.ConfigVersion);

        using var stale = await host.AuthenticatedClient.PostAsJsonAsync($"{sessionPath}/control", new { action = "stop", turnId = "stale" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var stop = await host.AuthenticatedClient.PostAsJsonAsync($"{sessionPath}/control", new { action = "stop", turnId }, ct);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        while ((await ReadPage()) is not { State: "idle", TaskBinding.Phase: "paused" })
            await Task.Delay(10, ct);

        using var resume = new HttpRequestMessage(HttpMethod.Post, $"{sessionPath}/messages")
        {
            Content = JsonContent.Create(new { content = "Continue the inspection" })
        };
        resume.Headers.Add("Idempotency-Key", "human-resume");
        using var resumed = await host.AuthenticatedClient.SendAsync(resume, ct);
        Assert.True(resumed.StatusCode == HttpStatusCode.OK, await resumed.Content.ReadAsStringAsync(ct));
        Assert.NotEqual(turnId, await continued.Task.WaitAsync(ct));
        Assert.Equal(taskId, (await ReadPage()).TaskBinding!.Task.Id);
        var resumedTurn = Assert.Single(host.AgentRuntime.ExecutedTurns, item => item.Input.Prompt == "Continue the inspection");
        Assert.Equal("gpt-5.4", resumedTurn.Input.Model);
        Assert.Equal("high", resumedTurn.Input.ReasoningEffort);

        async Task<SessionItemsPageDto> ReadPage() => (await host.AuthenticatedClient.GetFromJsonAsync<SessionItemsPageDto>(
            $"{sessionPath}/items?latest=true", PergaJsonDefaults.ApiOptions, ct))!;

        async IAsyncEnumerable<UnifiedAgentEvent> WorkerTurns(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken token)
        {
            if (input.Prompt.Contains("Execute contract task", StringComparison.Ordinal))
            {
                yield return new UnifiedAgentEvent.TextDelta("Reading the report");
                firstTurn.TrySetResult(input.CorrelationId);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else if (input.Prompt == "Continue the inspection")
                continued.TrySetResult(input.CorrelationId);

            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
    }
}
