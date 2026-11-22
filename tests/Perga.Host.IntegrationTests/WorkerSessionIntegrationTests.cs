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
        var dispatched = await kernel.CreateSessionAsync(trackId, new(Prompt: "Inspect the report", Task: new()), "settings", new InteropPrincipal.Human(), ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId == dispatched.Receipt.Task!.Id).Session;
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
        var dispatched = await kernel.CreateSessionAsync(trackId, new(Prompt: "Measure context", Task: new()), "worker-context", new InteropPrincipal.Human(), ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId == dispatched.Receipt.Task!.Id).Session;
        Assert.Equal(1000, worker.ContextUsage?.WindowTokens);
        using var patch = await host.Client.PatchAsJsonAsync($"/api/v1/sessions/{worker.Id}",
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

        var dispatched = await kernel.CreateSessionAsync(trackId, new(Prompt: "Inspect the report", Task: new()), "dispatch", new InteropPrincipal.Human(), ct);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId == dispatched.Receipt.Task!.Id).Session;
        var leadQueue = new { sessionId = worker.Id, content = "Lead follow-up", mode = "queue", requestKey = "lead-queue" };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{worker.Id}/messages")
            {
                Content = JsonContent.Create(new { content = "Look at dates", mode = "queue" })
            };
            request.Headers.Add("Idempotency-Key", "queued-request");
            using var response = await host.Client.SendAsync(request, ct);
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
            using var withdrawn = await host.Client.DeleteAsync(queuePath, ct);
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
        await kernel.CancelTaskAsync(trackId, leadId, dispatched.Receipt.Task!.TaskKey, ct: ct);
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
    public async Task RestartInterruptedWorkerRequeueReusesSessionAndAdvancesEpoch()
    {
        var directory = Directory.CreateTempSubdirectory("perga-worker-requeue-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        try
        {
            AgentSessionDto worker;
            string taskId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var (trackId, _) = await host.CreateTrackAsync();
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var receipt = await kernel.CreateSessionAsync(trackId, new(Prompt: "Inspect", Task: new()), "restart-requeue", new InteropPrincipal.Human(), ct);
                taskId = receipt.Receipt.Task!.Id;
                await kernel.WaitForTrackIdleAsync(trackId, ct);
                var session = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), s => s.TaskId == taskId).Session;
                var page = await kernel.GetSessionItemsAsync(session.Id, new(), new InteropPrincipal.Human(), ct);
                worker = page.Session;
                // Leave exactly the durable in-flight state a crash would preserve, without a live provider.
                var store = host.Services.GetRequiredService<ITrackStore>();
                await store.RecordTurnAndItemsAsync(page.LastTurn! with { Status = "running", CompletedAtMs = null },
                    [], worker with { Status = "running" }, ct);
            }
            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var resumed = reopened.Services.GetRequiredService<IPergaKernel>();
            var orphan = (await resumed.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), ct)).Session;
            Assert.Equal("unknown", orphan.Status);
            Assert.NotNull(orphan.ArchivedAtMs);
            var interruptedTask = Assert.Single(await resumed.ListTasksAsync(worker.TrackId, ct), t => t.Id == taskId);
            Assert.Equal(TaskRunStatus.Failed, interruptedTask.Status);
            await resumed.RejectTaskAsync(worker.TrackId, null, taskId, "Retry after restart", reQueue: true, ct: ct);
            await resumed.WaitForTrackIdleAsync(worker.TrackId, ct);
            var reused = await resumed.GetSessionItemsAsync(worker.Id, new(), new InteropPrincipal.Human(), ct);
            Assert.Equal(worker.Epoch + 1, reused.Session.Epoch);
            Assert.Null(reused.Session.ArchivedAtMs);
            Assert.Equal("completed", reused.LastTurn!.Status);
            var retriedTask = Assert.Single(await resumed.ListTasksAsync(worker.TrackId, ct), t => t.Id == taskId);
            Assert.Equal(2, retriedTask.RunCount);
            Assert.Contains(reused.Items, item => item.Kind == "epoch_boundary" &&
                item.Payload.GetProperty("reason").GetString() == "new_run");
            Assert.Single(await resumed.ListSessionsAsync(worker.TrackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), s => s.TaskId == taskId);
        }
        finally { directory.Delete(recursive: true); }
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
                var receipt = await kernel.CreateSessionAsync(trackId, new(Prompt: "Inspect the report", Task: new()), "task-restart", new InteropPrincipal.Human(), ct);
                await kernel.WaitForTrackIdleAsync(trackId, ct);
                var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId == receipt.Receipt.Task!.Id).Session;
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
    public async Task GoalOnlyDispatchUsesSafeDefaultsAndReplaysTheSameTask()
    {
        await using var host = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Dispatch", DefaultCwd: host.DataDir), ct);
        var track = (await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Cwd: host.DataDir), ct)).Track;
        var lead = kernel.McpSessionBridge.RegisterSession(track.Id, track.LeadSessionId!, true, null, null, []);
        var call = new { prompt = "Inspect the report", task = new { }, requestKey = "dispatch-inspection" };
        var (first, firstError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionCreate, call, ct);
        Assert.False(firstError, first);
        var receipt = JsonSerializer.Deserialize<SessionCreationReceipt>(first, PergaJsonDefaults.ApiOptions)!;
        Assert.Equal(Perga.Contracts.AccessMode.ReadOnly, receipt.Task!.Access);
        Assert.True(receipt.Task.Ready);
        Assert.Equal("Inspect the report", receipt.Task.GoalOrCmd);
        await kernel.WaitForTrackIdleAsync(track.Id, ct);
        Assert.Single(await kernel.ListSessionsAsync(track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), ct), summary => summary.TaskId == receipt.Task.Id);

        var (replay, replayError) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionCreate, call, ct);
        Assert.False(replayError, replay);
        Assert.Equal(receipt.Task.Id, JsonSerializer.Deserialize<SessionCreationReceipt>(replay, PergaJsonDefaults.ApiOptions)!.Task!.Id);
        var (_, conflict) = await CallFromRunningLeadAsync(host, "/mcp/kernel", lead.Principal.SessionId, KernelMcpToolNames.SessionCreate,
            new { prompt = "Different goal", task = new { }, call.requestKey }, ct);
        Assert.True(conflict);
        Assert.Single(await kernel.ListTasksAsync(track.Id, ct));
    }

    [Fact]
    public async Task GoalOnlyDispatchUsesDevelopmentTemplateSectionAndDefaultValidationCommand()
    {
        await using var host = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var repo = AppContext.BaseDirectory;
        var area = await kernel.CreateAreaAsync(new CreateAreaRequest("Development dispatch", DefaultCwd: repo), ct);
        var track = (await kernel.CreateTrackAsync(new CreateTrackRequest(area.Id, Cwd: repo, WorkspaceDriver: "direct", TemplateId: "dev"), ct)).Track;
        Assert.Empty((await kernel.ReadReportAsync(track.Id, ct: ct)).Blocks);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tracks/{track.Id}/sessions")
        { Content = JsonContent.Create(new { prompt = "Check the test setup", task = new { } }) };
        request.Headers.Add("Idempotency-Key", "development-task");
        using var response = await host.Client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = (await response.Content.ReadFromJsonAsync<SessionCreationReceipt>(PergaJsonDefaults.ApiOptions, ct))!;
        Assert.Equal(Perga.Contracts.AccessMode.ReadWrite, receipt.Task!.Access);
        Assert.Equal("dotnet test", Assert.Single(receipt.Task.Gate!.Steps).Cmd);
        var report = await kernel.ReadReportAsync(track.Id, ct: ct);
        Assert.Equal("Tasks", Assert.Single(report.Blocks, block => block.Id == receipt.Task.BlockId).SectionH1);
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

        var report = await kernel.ReadReportAsync(track.Id, leadId, ct);
        await kernel.CommitReportAsync(track.Id, leadId, new ReportCommitRequest(report.Anchor,
        [
            new BlockOp.UpsertBlockOp(null, ReportBlockKind.Task, null,
                JsonSerializer.SerializeToElement(new { task_key = "inspect", ready = true, access = "read_only", goal_or_cmd = "Inspect the report" }))
        ]), ct);
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

        using var stale = await host.Client.PostAsJsonAsync($"{sessionPath}/control", new { action = "stop", turnId = "stale" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var stop = await host.Client.PostAsJsonAsync($"{sessionPath}/control", new { action = "stop", turnId }, ct);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        while ((await ReadPage()) is not { State: "idle", TaskBinding.Phase: "paused" })
            await Task.Delay(10, ct);

        using var resume = new HttpRequestMessage(HttpMethod.Post, $"{sessionPath}/messages")
        {
            Content = JsonContent.Create(new { content = "Continue the inspection" })
        };
        resume.Headers.Add("Idempotency-Key", "human-resume");
        using var resumed = await host.Client.SendAsync(resume, ct);
        Assert.True(resumed.StatusCode == HttpStatusCode.OK, await resumed.Content.ReadAsStringAsync(ct));
        Assert.NotEqual(turnId, await continued.Task.WaitAsync(ct));
        Assert.Equal(taskId, (await ReadPage()).TaskBinding!.Task.Id);
        var resumedTurn = Assert.Single(host.AgentRuntime.ExecutedTurns, item => item.Input.Prompt == "Continue the inspection");
        Assert.Equal("gpt-5.4", resumedTurn.Input.Model);
        Assert.Equal("high", resumedTurn.Input.ReasoningEffort);

        async Task<SessionItemsPageDto> ReadPage() => (await host.Client.GetFromJsonAsync<SessionItemsPageDto>(
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
