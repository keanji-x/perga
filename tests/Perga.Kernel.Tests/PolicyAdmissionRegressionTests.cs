using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class PolicyAdmissionRegressionTests
{
    private static McpSessionPrincipal Caller(TrackAggregateSnapshot snapshot) =>
        new("token", snapshot.Track.Id, snapshot.Track.LeadSessionId!, true, null, null, []);

    private static Task<System.Text.Json.JsonElement> SetPolicy(KernelTestContext ctx, McpSessionPrincipal caller, string source) =>
        KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source }));

    [Fact]
    public async Task CancellationWhileAwaitingApproval_ExpiresReportApprovals()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await SetPolicy(ctx, caller, "export default {id:'ask',title:'Ask',before:a=>a.type==='report.commit'?AskHuman('Approve?',{approve:'Approve',deny:'Reject'}):Allow()};");
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        using var cancellation = new CancellationTokenSource();
        var commit = ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("note", ReportBlockKind.Prose, "Note", null)]), cancellation.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        TrackAggregateSnapshot pending;
        do
        {
            await Task.Delay(10, timeout.Token);
            pending = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token);
        } while (pending.PendingApprovals.Count == 0);
        var approval = Assert.Single(pending.PendingApprovals);
        var overlay = Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks);
        Assert.Equal(1, overlay.PendingApprovalCount);
        Assert.Equal(0, overlay.OpenAskCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        Assert.Equal(0, Assert.Single((await ctx.Kernel.GetOverlaysAsync()).Tracks).PendingApprovalCount);
        Assert.Empty((await ctx.Store.LoadTrackAggregateAsync(caller.TrackId, default))!.OpenAsks);
        var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.DecideApprovalAsync(
            caller.TrackId, approval.ExecutionId, approval.Id, approval.Options.Single(o => o.IsApproval).Id));
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
    }

    [Fact]
    public async Task StopExpiresAllTemplateReportApprovalsAndRejectsLateDecisions()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await SetPolicy(ctx, caller, "export default {id:'asks',title:'Asks',before:a=>a.type==='task.dispatch'||a.type==='report.commit'?AskHuman(a.type,{approve:'Approve',deny:'Reject'}):Allow()};");
        ctx.AgentRuntime.TurnScript = (_, _, ct) => BlockUntilStopped(ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = ctx.SendAndWaitForTurnAsync(caller.SessionId, new SendMessageRequest("Wait", "template-stop"), timeout.Token);
        while ((await ctx.Kernel.GetSessionItemsAsync(caller.SessionId, ct: timeout.Token)).ActiveTurn is null)
            await Task.Delay(10, timeout.Token);
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        var commit = ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId, new(report.Anchor, [
            new BlockOp.UpsertBlockOp("task", ReportBlockKind.Task, null, TestJson.FromObject(new {
                task_key = "task", ready = true, access = "read_only", goal_or_cmd = "Inspect" }))]), timeout.Token);
        TrackAggregateSnapshot pending;
        do
        {
            await Task.Delay(10, timeout.Token);
            pending = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token);
        } while (pending.PendingApprovals.Count < 2);
        Assert.All(pending.PendingApprovals, approval => Assert.NotNull(approval.TurnId));
        await ctx.Kernel.InterruptSessionAsync(caller.SessionId, timeout.Token);
        var error = await Assert.ThrowsAsync<KernelException>(() => commit);
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
        foreach (var approval in pending.PendingApprovals)
        {
            var late = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.DecideApprovalAsync(
                caller.TrackId, approval.ExecutionId, approval.Id, approval.Options.Single(o => o.IsApproval).Id, timeout.Token));
            Assert.Equal(KernelErrorCode.Conflict, late.Code);
        }
        await running;
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        Assert.Empty((await ctx.Store.LoadTrackAggregateAsync(caller.TrackId, default))!.OpenAsks);
        Assert.Empty(await ctx.Kernel.ListTasksAsync(caller.TrackId));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> BlockUntilStopped(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    [Theory]
    [InlineData("report-deny")]
    [InlineData("report-throw")]
    [InlineData("task-deny")]
    [InlineData("task-throw")]
    [InlineData("provider")]
    [InlineData("manifest")]
    [InlineData("gate")]
    public async Task RejectedCommit_DoesNotLeaveApprovalAsks(string failure)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        var reportHook = failure == "report-deny" ? "return Deny('report blocked');" :
            failure == "report-throw" ? "throw Error('report failed');" : "return Allow();";
        var secondTaskHook = failure == "task-deny" ? "return Deny('task blocked');" :
            failure == "task-throw" ? "throw Error('task failed');" : "return AskHuman('Task approval');";
        await SetPolicy(ctx, caller, "export default {id:'admission',title:'Admission',report:{maxProseChars:" +
            (failure == "manifest" ? "1" : "10000") + "},tasks:{requireGateForReadWrite:" +
            (failure == "gate" ? "true" : "false") + "},before(a){if(a.type==='report.commit'){" + reportHook +
            "}if(a.type==='task.dispatch'){if(a.role==='second'){" + secondTaskHook +
            "}return AskHuman('Task approval');}return Allow();}};");
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        var error = await Record.ExceptionAsync(() => ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [
                new BlockOp.UpsertBlockOp("first", ReportBlockKind.Task, null, TestJson.FromObject(new {
                    task_key = "first", ready = true, access = "read_only", goal_or_cmd = "First" })),
                new BlockOp.UpsertBlockOp("second", ReportBlockKind.Task, null, TestJson.FromObject(new {
                    task_key = "second", role = "second", ready = true,
                    access = failure == "gate" ? "read_write" : "read_only", goal_or_cmd = "Second",
                    provider = failure == "provider" ? "missing-provider" : null })),
                new BlockOp.UpsertBlockOp("prose", ReportBlockKind.Prose, "Too much prose", null)
            ])));
        Assert.NotNull(error);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        var persisted = (await ctx.Store.LoadTrackAggregateAsync(caller.TrackId, default))!;
        Assert.Empty(persisted.OpenAsks);
        Assert.Empty(persisted.Tasks);
        Assert.DoesNotContain(persisted.Report.Blocks, b => b.Id == "first");
    }

    [Fact]
    public async Task RejectedApproval_CancelsUnansweredSiblingsRegardlessOfOrder()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await SetPolicy(ctx, caller, "export default {id:'asks',title:'Asks',before:a=>a.type==='task.dispatch'||a.type==='report.commit'?AskHuman(a.type,{approve:'Approve',deny:'Reject'}):Allow()};");
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commit = ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId, new(report.Anchor, [
            new BlockOp.UpsertBlockOp("task", ReportBlockKind.Task, null, TestJson.FromObject(new {
                task_key = "task", ready = true, access = "read_only", goal_or_cmd = "Inspect" }))]), timeout.Token);
        TrackAggregateSnapshot pending;
        do
        {
            await Task.Delay(10, timeout.Token);
            pending = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token);
        } while (pending.PendingApprovals.Count < 2);
        // Deny the last ask while the first remains unanswered.
        var last = pending.PendingApprovals.Last();
        await ctx.Kernel.DecideApprovalAsync(caller.TrackId, last.ExecutionId, last.Id,
            last.Options.Single(o => !o.IsApproval).Id, timeout.Token);
        var error = await Assert.ThrowsAsync<KernelException>(() => commit);
        Assert.Equal(KernelErrorCode.PolicyDenied, error.Code);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        Assert.Empty((await ctx.Store.LoadTrackAggregateAsync(caller.TrackId, default))!.OpenAsks);
    }

    [Fact]
    public async Task UnclosedTaskFence_StillRunsDispatchGuardBeforeCommit()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await SetPolicy(ctx, caller, "export default {id:'deny',title:'Deny',before:a=>a.type==='task.dispatch'?Deny('dispatch blocked'):Allow()};");
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("fenced", ReportBlockKind.Task,
                "```json\n{\"task_key\":\"fenced\",\"ready\":true,\"access\":\"read_only\",\"goal_or_cmd\":\"Inspect\"}", null)])));
        Assert.Equal(KernelErrorCode.PolicyDenied, error.Code);
        Assert.Empty(await ctx.Kernel.ListTasksAsync(caller.TrackId));
        Assert.DoesNotContain(await ctx.Kernel.ListSessionsAsync(caller.TrackId), s => s.BoundTaskId is not null);
    }

    [Theory]
    [InlineData("throw Error('after failed');")]
    [InlineData("while(true) {}")]
    [InlineData("ctx.state.calls=(ctx.state.calls||0)+1;")]
    public async Task AfterFailures_DoNotFailToolsReportsOrWedgeTurns(string hook)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await SetPolicy(ctx, caller, "export default {id:'after',title:'After',after(e,ctx){" + hook + "}};");
        var stateBefore = (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.TemplateState.GetRawText();
        ctx.Store.FailTemplateStateUpdate = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackRename,
            TestJson.FromObject(new { title = "Committed title" }), timeout.Token);
        Assert.Equal("Committed title", (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.Title);
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        await ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("note", ReportBlockKind.Prose, "Committed report", null)]), timeout.Token);
        Assert.Contains((await ctx.Kernel.ReadReportAsync(caller.TrackId)).Blocks, b => b.Id == "note");
        await ctx.SendAndWaitForTurnAsync(caller.SessionId, new SendMessageRequest("first", "first"), timeout.Token);
        await ctx.Kernel.WaitForTrackIdleAsync(caller.TrackId, timeout.Token);
        await ctx.SendAndWaitForTurnAsync(caller.SessionId, new SendMessageRequest("second", "second"), timeout.Token);
        await ctx.Kernel.WaitForTrackIdleAsync(caller.TrackId, timeout.Token);
        Assert.Contains(ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs), i => i.Prompt.Contains("second", StringComparison.Ordinal));
        Assert.Equal(stateBefore, (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.TemplateState.GetRawText());
    }

    [Theory]
    [InlineData("throw Error('settled failed');")]
    [InlineData("while(true) {}")]
    [InlineData("ctx.state.changed=true;")]
    public async Task TaskAfterFailures_PreserveCommittedCompletion(string hook)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await SetPolicy(ctx, caller, "export default {id:'settled',title:'Settled',after(e,ctx){if(e.type==='task.settled'){" + hook + "}}};");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receipt = await ctx.Kernel.DispatchWorkerAsync(caller.TrackId, new("Inspect"), "settled", null, timeout.Token);
        await ctx.Kernel.WaitForTrackIdleAsync(caller.TrackId, timeout.Token);
        var worker = Assert.Single(await ctx.Kernel.ListSessionsAsync(caller.TrackId), s => s.BoundTaskId == receipt.Task.Id);
        ctx.Store.FailTemplateStateUpdate = true;
        var completed = await ctx.Kernel.CompleteAttemptDoneAsync(caller.TrackId, worker.Id, TestJson.FromObject(new { summary = "done" }), ct: timeout.Token);
        Assert.Equal(TaskRunStatus.Done, completed.Status);
        var persisted = (await ctx.Store.LoadTrackAggregateAsync(caller.TrackId, timeout.Token))!;
        Assert.Equal(TaskRunStatus.Done, Assert.Single(persisted.Tasks).Status);
        // Settlement state uses the atomic task/attempt/wake write, not UpdateTrackAndEmitAsync.
        Assert.Equal(hook == "ctx.state.changed=true;", persisted.Track.TemplateState.TryGetProperty("changed", out _));
    }

    [Fact]
    public async Task PlainTask_DoesNotProbeLeadFallbackModel()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        await ctx.Kernel.Actors.GetOrActivate(snapshot.Track.Id).UpdateSessionModelAsync(snapshot.Track.LeadSessionId!, new("lead-only-model"));
        ctx.AgentRuntime.ProbeScript = _ => throw new IOException("probe offline");
        var dispatched = await ctx.Kernel.DispatchWorkerAsync(snapshot.Track.Id, new("Inspect"), "plain", null, default);
        Assert.NotEqual(TaskRunStatus.Failed, dispatched.Task.Status);
        Assert.Contains(await ctx.Kernel.ListSessionsAsync(snapshot.Track.Id), s => s.BoundTaskId == dispatched.Task.Id);
    }

    [Fact]
    public async Task RoutedProvider_DoesNotInheritAnotherProvidersLeadModel()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await ctx.Kernel.Actors.GetOrActivate(caller.TrackId).UpdateSessionModelAsync(caller.SessionId, new("lead-only-model"));
        ctx.AgentRuntime.ProbeScript = _ => Task.FromResult<IReadOnlyList<ProviderStatus>>([
            new("codex", true, "1", null, ["gpt-5"]), new("claude", true, "1", null, ["sonnet"])]);
        await SetPolicy(ctx, caller, "export default {id:'route-provider',title:'Route',tasks:{routing:[{when:{},provider:'claude'}]}};");
        var receipt = await ctx.Kernel.DispatchWorkerAsync(caller.TrackId, new("Inspect"), "route-provider", null, default);
        var worker = Assert.Single(await ctx.Kernel.ListSessionsAsync(caller.TrackId), s => s.BoundTaskId == receipt.Task.Id);
        Assert.Equal("claude", worker.Provider);
        Assert.Null(worker.Model);
        Assert.Null(receipt.Task.Provider);
    }

    [Fact]
    public async Task SchedulingRouting_SurvivesReportSync_AndProbeOutageIsRetryable()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        await ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId, new(report.Anchor, [
            new BlockOp.UpsertBlockOp("blocker", ReportBlockKind.Task, null, TestJson.FromObject(new {
                task_key = "blocker", ready = false, access = "read_only", goal_or_cmd = "Block" })),
            new BlockOp.UpsertBlockOp("routed", ReportBlockKind.Task, null, TestJson.FromObject(new {
                task_key = "routed", ready = true, access = "read_only", goal_or_cmd = "Inspect", depends_on = new[] { "blocker" } }))]));
        await SetPolicy(ctx, caller, "export default {id:'route',title:'Route',tasks:{routing:[{when:{},provider:'codex',model:'gpt-5'}]}};");
        var blocker = (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.TaskKey == "blocker");
        await ctx.Store.UpsertTaskAndAttemptAsync(blocker with { Status = TaskRunStatus.Done }, null, null, null, default);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(caller.TrackId);
        ctx.AgentRuntime.ProbeScript = _ => throw new IOException("probe offline");
        await ctx.Kernel.RejectTaskAsync(caller.TrackId, caller.SessionId, "routed", "Retry", reQueue: true);
        var pending = (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.TaskKey == "routed");
        Assert.Equal(TaskRunStatus.Pending, pending.Status);
        ctx.AgentRuntime.ProbeScript = _ => Task.FromResult<IReadOnlyList<ProviderStatus>>([new("codex", false, null, "offline", [])]);
        await ctx.Kernel.RejectTaskAsync(caller.TrackId, caller.SessionId, "routed", "Retry", reQueue: true);
        Assert.Equal(TaskRunStatus.Pending, (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.TaskKey == "routed").Status);
        ctx.AgentRuntime.ProbeScript = null;
        report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        await ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("unrelated", ReportBlockKind.Prose, "Unrelated sync", null)]));
        var worker = Assert.Single(await ctx.Kernel.ListSessionsAsync(caller.TrackId), s => s.BoundTaskId == pending.Id);
        Assert.Equal("codex", worker.Provider);
        Assert.Equal("gpt-5", worker.Model);
    }

    [Fact]
    public async Task PreviewAskHuman_ReturnsPromptlyWithoutPersistingAskOrState()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await SetPolicy(ctx, caller, "export default {id:'ask',title:'Ask',before(a,ctx){if(a.type==='tool.perga_track_set'){ctx.state.changed=true;return AskHuman('Approve?',{approve:'Approve',deny:'Reject'});}return Allow();}};");
        var before = (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.TemplateState.GetRawText();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller,
            KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source = "export default {id:'next',title:'Next'};", dryRun = true }), timeout.Token));
        Assert.Equal(KernelErrorCode.PolicyDenied, error.Code);
        var after = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId);
        Assert.Empty(after.PendingApprovals);
        Assert.Equal(before, after.Track.TemplateState.GetRawText());
    }

    [Theory]
    [InlineData("dryRun")]
    [InlineData("clear")]
    public async Task TemplateBooleanArguments_AreValidatedBeforeGuardSideEffects(string name)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await SetPolicy(ctx, caller, "export default {id:'bool',title:'Bool',before(a,ctx){if(a.type==='tool.perga_track_set')ctx.state.changed=true;return Allow();}};");
        var before = (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.TemplateState.GetRawText();
        var error = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller,
            KernelMcpToolNames.TrackSet, TestJson.FromObject(new Dictionary<string, object> { [name] = "true", ["clear"] = name == "clear" ? "true" : false })));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
        Assert.Equal(before, (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.TemplateState.GetRawText());
    }
    [Fact]
    public async Task SchedulingAsk_UnrelatedReportSyncPreservesApprovalForSameContract()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        await ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId, new(report.Anchor, [
            new BlockOp.UpsertBlockOp("blocker", ReportBlockKind.Task, null, TestJson.FromObject(new {
                task_key = "blocker", ready = false, access = "read_only", goal_or_cmd = "Block" })),
            new BlockOp.UpsertBlockOp("routed", ReportBlockKind.Task, null, TestJson.FromObject(new {
                task_key = "routed", ready = true, access = "read_only", goal_or_cmd = "Inspect", depends_on = new[] { "blocker" } }))]));
        await SetPolicy(ctx, caller, "export default {id:'ask',title:'Ask',tasks:{routing:[{when:{},provider:'codex',model:'gpt-5'}]},before:a=>a.type==='task.dispatch'?AskHuman('Approve?',{approve:'Approve',deny:'Reject'}):Allow()};");
        var blocker = (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.TaskKey == "blocker");
        await ctx.Store.UpsertTaskAndAttemptAsync(blocker with { Status = TaskRunStatus.Done }, null, null, null, default);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(caller.TrackId);
        await ctx.Kernel.RejectTaskAsync(caller.TrackId, caller.SessionId, "routed", "Retry", reQueue: true);
        var first = Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        await ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp("unrelated", ReportBlockKind.Prose, "Sync", null)]));
        await ctx.Kernel.DecideApprovalAsync(caller.TrackId, first.ExecutionId, first.Id, first.Options.Single(o => o.IsApproval).Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ctx.Kernel.WaitForTrackIdleAsync(caller.TrackId, timeout.Token);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        var worker = Assert.Single(await ctx.Kernel.ListSessionsAsync(caller.TrackId), session => session.BoundTaskId is not null);
        Assert.Equal("gpt-5", worker.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchedulingAsk_CloseThenReopenRequestsFreshApproval(bool cancelSchedulingCaller)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        var report = await ctx.Kernel.ReadReportAsync(caller.TrackId);
        await ctx.Kernel.CommitReportAsync(caller.TrackId, caller.SessionId, new(report.Anchor, [
            new BlockOp.UpsertBlockOp("blocker", ReportBlockKind.Task, null, TestJson.FromObject(new {
                task_key = "blocker", ready = false, access = "read_only", goal_or_cmd = "Block" })),
            new BlockOp.UpsertBlockOp("pending", ReportBlockKind.Task, null, TestJson.FromObject(new {
                task_key = "pending", ready = true, access = "read_only", goal_or_cmd = "Inspect", depends_on = new[] { "blocker" } }))]));
        await SetPolicy(ctx, caller, "export default {id:'ask',title:'Ask',before:a=>a.type==='task.dispatch'?AskHuman('Approve?',{approve:'Approve',deny:'Reject'}):Allow()};");
        var blocker = (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.TaskKey == "blocker");
        await ctx.Store.UpsertTaskAndAttemptAsync(blocker with { Status = TaskRunStatus.Done }, null, null, null, default);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(caller.TrackId);

        using var schedulingCaller = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ctx.Kernel.RejectTaskAsync(caller.TrackId, caller.SessionId, "pending", "Retry", reQueue: true, ct: schedulingCaller.Token);
        var first = Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        var pending = (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.TaskKey == "pending");
        Assert.Equal(TaskRunStatus.Pending, pending.Status);
        if (cancelSchedulingCaller)
        {
            schedulingCaller.Cancel();
            Assert.Equal(first.Id, Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals).Id);
        }

        await ctx.Kernel.PatchTrackAsync(caller.TrackId, new(Closed: true), timeout.Token);
        await ctx.Kernel.WaitForTrackIdleAsync(caller.TrackId, timeout.Token);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        Assert.Equal(TaskRunStatus.Pending, (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.Id == pending.Id).Status);
        await ctx.Kernel.PatchTrackAsync(caller.TrackId, new(Closed: false), timeout.Token);
        TrackAggregateSnapshot reopened;
        do
        {
            await Task.Delay(10, timeout.Token);
            reopened = await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId, timeout.Token);
        } while (reopened.PendingApprovals.Count == 0);
        var fresh = Assert.Single(reopened.PendingApprovals);
        Assert.NotEqual(first.Id, fresh.Id);
        await ctx.Kernel.DecideApprovalAsync(caller.TrackId, fresh.ExecutionId, fresh.Id,
            fresh.Options.Single(o => o.IsApproval).Id, timeout.Token);
        await ctx.Kernel.WaitForTrackIdleAsync(caller.TrackId, timeout.Token);
        Assert.Empty((await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).PendingApprovals);
        Assert.Single(await ctx.Kernel.ListSessionsAsync(caller.TrackId), session => session.BoundTaskId == pending.Id);
        Assert.Equal(TaskRunStatus.Running, (await ctx.Kernel.ListTasksAsync(caller.TrackId)).Single(t => t.Id == pending.Id).Status);
    }

    [Fact]
    public async Task UnsupportedRewrite_CannotTurnDryRunIntoMutation()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = Caller(snapshot);
        await SetPolicy(ctx, caller, "export default {id:'preview',title:'Preview',before:a=>a.type==='tool.perga_track_set'?Rewrite({arguments:{clear:true,dryRun:false}}):Allow()};");
        await Assert.ThrowsAsync<InvalidOperationException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet,
            TestJson.FromObject(new { clear = true, dryRun = true })));
        Assert.Equal("preview", (await ctx.Kernel.GetTrackAggregateAsync(caller.TrackId)).Track.TemplateId);
    }

}

// Real policy execution uses a wall-clock sandbox budget. Keep these scenarios
// apart from concurrent test workloads without relaxing production limits.
[CollectionDefinition("Template policy sandbox", DisableParallelization = true)]
public sealed class TemplatePolicySandboxCollection;
