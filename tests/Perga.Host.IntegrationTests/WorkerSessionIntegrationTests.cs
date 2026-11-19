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
    [Fact]
    public async Task WorkerSettingsUseReportedDefaultsAndRequireAnOwnerProofToRestoreApprovals()
    {
        var ownerKey = new string('a', 64);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var host = new PergaHostTestCluster(configureHostOptions: options =>
            options.WorkerApprovalAuthorizationSha256 = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(ownerKey))));
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, leadId) = await host.CreateTrackAsync();
        var dispatched = await kernel.DispatchWorkerAsync(trackId, new("Inspect the report"), "settings", null, ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, ct), session => session.BoundTaskId == dispatched.Task.Id);
        var attemptId = worker.BoundAttemptId!;
        var page = await kernel.GetSessionItemsAsync(worker.Id, ct: ct);
        Assert.True(page.Worker!.ApprovalAuthorizationConfigured);
        Assert.Equal("gpt-5.4", page.Worker.ProviderConfiguration!.DefaultModel);
        Assert.Equal(["low"], page.Worker.ProviderConfiguration.GetReasoningEfforts("gpt-5.4-mini"));

        using var incompatible = await UpdateAsync(new(attemptId, 0, "gpt-5.4-mini", "high", "deny"));
        Assert.Equal(HttpStatusCode.BadRequest, incompatible.StatusCode);
        using var denied = await UpdateAsync(new(attemptId, 0, "gpt-5.4", "high", "deny"));
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);

        var restore = new UpdateWorkerSettingsRequest(attemptId, 1, null, null, "ask");
        using var missingProof = await UpdateAsync(restore);
        Assert.Equal(HttpStatusCode.Forbidden, missingProof.StatusCode);
        using var wrongProof = await UpdateAsync(restore, new string('b', 64));
        Assert.Equal(HttpStatusCode.Forbidden, wrongProof.StatusCode);
        using var insecureProof = await UpdateAsync(restore, ownerKey, scheme: "http");
        Assert.Equal(HttpStatusCode.Forbidden, insecureProof.StatusCode);

        var lead = kernel.McpSessionBridge.RegisterSession(trackId, leadId, true, null, null, []);
        var leadSettings = JsonSerializer.SerializeToElement(new { sessionId = worker.Id, attemptId, version = 1,
            model = (string?)null, reasoningEffort = (string?)null, approvalMode = "ask" });
        var (leadReply, leadError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionUpdateSettings,
            leadSettings, ct);
        Assert.True(leadError);
        Assert.Contains("installation owner", leadReply);

        using var authorized = await UpdateAsync(restore, ownerKey);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        var saved = (await authorized.Content.ReadFromJsonAsync<WorkerSettings>(PergaJsonDefaults.ApiOptions, ct))!;
        Assert.Null(saved.Model);
        Assert.Null(saved.ReasoningEffort);
        Assert.Equal("ask", saved.ApprovalMode);
        var stored = await host.Services.GetRequiredService<ITrackStore>().WorkerSettings!.ReadAsync(worker.Id, ct);
        Assert.Equal(saved, stored);

        await kernel.SendWorkerMessageAsync(worker.Id, new(attemptId, "Verify restored defaults", "verify"), null, ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var turn = Assert.Single(host.AgentRuntime.ExecutedTurns, entry => entry.Input.Prompt == "Verify restored defaults");
        Assert.Equal("gpt-5.4", turn.Input.Model);
        Assert.Equal("low", turn.Input.ReasoningEffort);

        async Task<HttpResponseMessage> UpdateAsync(UpdateWorkerSettingsRequest settings, string? key = null, string scheme = "https")
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"{scheme}://localhost/api/v1/sessions/{worker.Id}/settings")
            {
                Content = JsonContent.Create(settings)
            };
            if (key is not null) request.Headers.Add("X-Perga-Worker-Approval-Key", key);
            return await host.Client.SendAsync(request, ct);
        }
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
        var dispatched = await kernel.DispatchWorkerAsync(trackId, new("Measure context"), "worker-context", null, ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, ct), session => session.BoundTaskId == dispatched.Task.Id);
        var attemptId = worker.BoundAttemptId!;
        Assert.Equal(1000, worker.ContextUsage?.WindowTokens);
        using var patch = await host.Client.PatchAsJsonAsync($"/api/v1/sessions/{worker.Id}/settings",
            new UpdateWorkerSettingsRequest(attemptId, 0, "gpt-5.4-mini", "low", "ask"), ct);
        patch.EnsureSuccessStatusCode();
        var cleared = await kernel.GetSessionItemsAsync(worker.Id, ct: ct);
        Assert.Null(cleared.Session.ContextUsage);

        await kernel.SendWorkerMessageAsync(worker.Id, new(attemptId, "After model switch", "after-model-switch"), null, ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var updated = await kernel.GetSessionItemsAsync(worker.Id, ct: ct);
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

        var dispatched = await kernel.DispatchWorkerAsync(trackId, new("Inspect the report"), "dispatch", null, ct);
        var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, ct), session => session.BoundTaskId == dispatched.Task.Id);
        var leadQueue = new { sessionId = worker.Id, attemptId = worker.BoundAttemptId, content = "Lead follow-up", mode = "queue", requestKey = "lead-queue" };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{worker.Id}/messages")
            {
                Content = JsonContent.Create(new { attemptId = worker.BoundAttemptId, content = "Look at dates", mode = "queue" })
            };
            request.Headers.Add("Idempotency-Key", "queued-request");
            using var response = await host.Client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var receipt = (await response.Content.ReadFromJsonAsync<WorkerMessageReceipt>(PergaJsonDefaults.ApiOptions, ct))!;
            Assert.Equal("queued", receipt.Delivery);
            Assert.Equal(0, steers);
            var turnId = await started.Task.WaitAsync(ct);

            var (replayed, replayError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionSend,
                leadQueue, ct);
            Assert.False(replayError, replayed);
            var (again, againError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionSend,
                leadQueue, ct);
            Assert.False(againError, again);
            Assert.Equal(replayed, again);
            Assert.Equal(0, steers);
            Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "Look at dates");
            var extra = new { sessionId = worker.Id, attemptId = worker.BoundAttemptId, content = "Withdraw this", mode = "queue", requestKey = "withdraw" };
            var (extraText, extraError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionSend, extra, ct);
            Assert.False(extraError, extraText);
            var extraReceipt = JsonSerializer.Deserialize<WorkerMessageReceipt>(extraText, PergaJsonDefaults.ApiOptions)!;
            var queuePath = $"/api/v1/sessions/{worker.Id}/messages/queue/{extraReceipt.InputId}";
            var workerPage = await kernel.GetSessionItemsAsync(worker.Id, ct: ct);
            Assert.Contains(workerPage.QueuedMessages, entry => entry.Id == extraReceipt.InputId);
            using var withdrawn = await host.Client.DeleteAsync(queuePath, ct);
            Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
            var (withdrawAgain, withdrawError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionWithdraw,
                new { sessionId = worker.Id, attemptId = worker.BoundAttemptId, inputId = extraReceipt.InputId }, ct);
            Assert.False(withdrawError, withdrawAgain);
            var (replayWithdrawal, withdrawalError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionSend, extra, ct);
            Assert.False(withdrawalError, replayWithdrawal);
            Assert.Equal("withdrawn", JsonSerializer.Deserialize<WorkerMessageReceipt>(replayWithdrawal, PergaJsonDefaults.ApiOptions)!.Delivery);
            var (wrongTarget, wrongTargetError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionSend,
                new { sessionId = worker.Id, attemptId = worker.BoundAttemptId, turnId, content = "Wrong queue target", mode = "queue", requestKey = "bad-queue" }, ct);
            Assert.True(wrongTargetError);
            Assert.Contains("Omit turnId", wrongTarget);
            var (stale, staleError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionSend,
                new { sessionId = worker.Id, attemptId = worker.BoundAttemptId, turnId = "old-turn", content = "Stale steer", mode = "steer", requestKey = "stale" }, ct);
            Assert.True(staleError);
            Assert.Contains("worker_turn_changed", stale);
        }
        finally
        {
            release.TrySetResult();
        }
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "Look at dates");
        Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "Withdraw this");
        Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "Lead follow-up");
        var (delivered, deliveredError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionSend, leadQueue, ct);
        Assert.False(deliveredError, delivered);
        Assert.Equal("delivered", JsonSerializer.Deserialize<WorkerMessageReceipt>(delivered, PergaJsonDefaults.ApiOptions)!.Delivery);
        Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == "Lead follow-up");
        var persisted = await kernel.GetSessionItemsAsync(worker.Id, ct: ct);
        var followup = Assert.Single(persisted.Items, item => item.Kind == "user" &&
            item.Payload.GetProperty("content").GetString() == "Lead follow-up");
        Assert.Equal("delivered", followup.InputDelivery);
        await kernel.CancelTaskAsync(trackId, leadId, dispatched.Task.TaskKey, ct: ct);
        var (afterCancel, afterCancelError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionSend,
            new { sessionId = worker.Id, attemptId = worker.BoundAttemptId, turnId = "old-turn", content = "Stale steer", mode = "steer", requestKey = "stale" }, ct);
        Assert.True(afterCancelError);
        Assert.Contains("worker_turn_changed", afterCancel);

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
    public async Task RestartRejectsAnAcceptedLegacyInputToAnInterruptedWorker()
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
                var receipt = await kernel.DispatchWorkerAsync(trackId, new DispatchWorkerRequest("Inspect the report"), "task-restart", caller: null, ct);
                await kernel.WaitForTrackIdleAsync(trackId, ct);
                var worker = Assert.Single(await kernel.ListSessionsAsync(trackId, ct), session => session.BoundTaskId == receipt.Task.Id);
                var store = host.Services.GetRequiredService<ITrackStore>();
                var evt = new EventEnvelopeDto(null, null, trackId, KernelEventKinds.SessionUpdated,
                    JsonSerializer.SerializeToElement(worker), worker.UpdatedAtMs);
                input = (await store.SessionInputs.AcceptAsync(worker with { Status = "queued" },
                    new SessionInputRequest("legacy-input", "request-hash", "Do not run after restart"), evt, ct)).Receipt;
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var resumed = reopened.Services.GetRequiredService<IPergaKernel>();
            await resumed.WaitForTrackIdleAsync(trackId, ct);
            var outcome = await resumed.GetSessionInputAsync(input.SessionId, input.InputId, ct);
            Assert.Equal("rejected", outcome.DeliveryState);
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
        var call = new { goal = "Inspect the report", requestKey = "dispatch-inspection" };
        var (first, firstError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.TaskDispatch, call, ct);
        Assert.False(firstError, first);
        var receipt = JsonSerializer.Deserialize<WorkerDispatchReceipt>(first, PergaJsonDefaults.ApiOptions)!;
        Assert.Equal(Perga.Contracts.AccessMode.ReadOnly, receipt.Task.Access);
        Assert.True(receipt.Task.Ready);
        Assert.Equal("Inspect the report", receipt.Task.GoalOrCmd);
        await kernel.WaitForTrackIdleAsync(track.Id, ct);
        Assert.Single(await kernel.ListSessionsAsync(track.Id, ct), session => session.BoundTaskId == receipt.Task.Id);

        var (replay, replayError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.TaskDispatch, call, ct);
        Assert.False(replayError, replay);
        Assert.Equal(receipt.Task.Id, JsonSerializer.Deserialize<WorkerDispatchReceipt>(replay, PergaJsonDefaults.ApiOptions)!.Task.Id);
        var (_, conflict) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.TaskDispatch,
            new { goal = "Different goal", call.requestKey }, ct);
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
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{track.Id}/tasks/dispatch", new { goal = "Check the test setup" }, ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = (await response.Content.ReadFromJsonAsync<WorkerDispatchReceipt>(PergaJsonDefaults.ApiOptions, ct))!;
        Assert.Equal(Perga.Contracts.AccessMode.ReadWrite, receipt.Task.Access);
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
        var worker = Assert.Single(await kernel.ListSessionsAsync(track.Id, ct), session => session.BoundAttemptId is not null);
        var attemptId = worker.BoundAttemptId!;
        var sessionPath = $"/api/v1/sessions/{worker.Id}";
        var httpPage = await ReadPage();
        Assert.Equal(turnId, httpPage.ActiveTurn!.Id);
        Assert.Contains(httpPage.Worker!.Events, evt => evt.Payload.ToString().Contains("Reading the report", StringComparison.Ordinal));

        var (readText, readError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token,
            KernelMcpToolNames.SessionRead, new { sessionId = worker.Id }, ct);
        Assert.False(readError, readText);
        var leadPage = JsonSerializer.Deserialize<SessionItemsPageDto>(readText, PergaJsonDefaults.ApiOptions)!;
        Assert.Equal(httpPage.Worker.Sequence, leadPage.Worker!.Sequence);
        Assert.Equal(attemptId, leadPage.Worker.AttemptId);

        var request = new { sessionId = worker.Id, attemptId, turnId, content = "Check the dates", requestKey = "lead-steer" };
        for (var index = 0; index < 2; index++)
        {
            var (receiptText, receiptError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token,
                KernelMcpToolNames.SessionSend, request, ct);
            Assert.False(receiptError, receiptText);
            Assert.Equal("delivered", JsonSerializer.Deserialize<WorkerMessageReceipt>(receiptText, PergaJsonDefaults.ApiOptions)!.Delivery);
        }
        Assert.Equal(["Check the dates"], steers);
        Assert.Contains((await ReadPage()).Worker!.Messages, message => message.Content == "Check the dates" && message.Author == leadId);

        var (updated, updateError) = await CallToolAsync(host, "/mcp/kernel", lead.Principal.Token, KernelMcpToolNames.SessionUpdateSettings,
            new { sessionId = worker.Id, attemptId, version = 0, model = "gpt-5.4", reasoningEffort = "high", approvalMode = "deny" }, ct);
        Assert.False(updateError, updated);
        var settingsPage = (await ReadPage()).Worker!;
        Assert.Equal("ask", settingsPage.ActiveSettings!.ApprovalMode);
        Assert.Equal("deny", settingsPage.NextSettings.ApprovalMode);
        Assert.Equal(1, settingsPage.NextSettings.Version);

        using var stale = await host.Client.PostAsJsonAsync($"{sessionPath}/interrupt", new { attemptId, turnId = "stale" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var stop = await host.Client.PostAsJsonAsync($"{sessionPath}/interrupt", new { attemptId, turnId }, ct);
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        while ((await ReadPage()).Worker!.Phase != "paused")
            await Task.Delay(10, ct);

        using var resume = new HttpRequestMessage(HttpMethod.Post, $"{sessionPath}/messages")
        {
            Content = JsonContent.Create(new { attemptId, content = "Continue the inspection" })
        };
        resume.Headers.Add("Idempotency-Key", "human-resume");
        using var resumed = await host.Client.SendAsync(resume, ct);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.NotEqual(turnId, await continued.Task.WaitAsync(ct));
        Assert.Equal(attemptId, (await ReadPage()).Worker!.AttemptId);
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
