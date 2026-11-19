using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Templates;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

[Collection("Template policy sandbox")]
public sealed class TemplateLoopTests
{
    [Fact]
    public void DirectoryInheritance_IsOrderIndependentAndSnapshotsAreFrozen()
    {
        var directory = Directory.CreateTempSubdirectory("perga-inherit-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "a-child.json"), """{"id":"child","title":"Child","extends":"base","plugins":["child"]}""");
            File.WriteAllText(Path.Combine(directory.FullName, "z-base.perga.js"), "export default {id:'base',title:'Base',plugins:['base']};");
            var vm = new global::Perga.PolicyVm.PolicyVm();
            var catalog = new TemplateCatalog(vm, directory.FullName);
            var policy = catalog.GetRequiredPolicy("child");
            Assert.Equal(["base", "child"], policy.Manifest.DefaultPlugins);
            catalog.RegisterTemplate("export default {id:'base',title:'Changed',plugins:['changed']};");
            var reloaded = new TemplateCatalog(vm).GetOrCompileSnapshot(policy.JsSource);
            Assert.Equal(["base", "child"], reloaded.Manifest.DefaultPlugins);
            File.WriteAllText(Path.Combine(directory.FullName, "z-base.perga.js"), "export default {id:'base',title:'Base',extends:'child'};");
            Assert.Throws<InvalidOperationException>(() => new TemplateCatalog(vm, directory.FullName));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task DispatchBefore_AskHumanSuspendsWithoutCreatingTask_AndRoutingFillsDefaults()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = new McpSessionPrincipal("token", snap.Track.Id, snap.Track.LeadSessionId!, true, null, null, []);
        const string asking = """
            export default {id:'ask',title:'Ask',before(a,ctx) {
              return a.type === 'task.dispatch' ? AskHuman('Approve dispatch?',['Approve','Reject']) : Allow();
            }};
            """;
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source = asking }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatch = ctx.Kernel.DispatchWorkerAsync(snap.Track.Id, new("Inspect", Role: "review"), "ask", null, timeout.Token);
        ApprovalDto? ask = null;
        while (ask is null)
        {
            ask = (await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id, timeout.Token)).PendingApprovals.FirstOrDefault();
            if (ask is null)
            {
                if (dispatch.IsCompleted) await dispatch;
                await Task.Delay(10, timeout.Token);
            }
        }
        Assert.False(dispatch.IsCompleted);
        Assert.Empty(await ctx.Kernel.ListTasksAsync(snap.Track.Id, timeout.Token));
        await ctx.Kernel.DecideApprovalAsync(snap.Track.Id, ask.ExecutionId, ask.Id, TestJson.FromObject(new { answers = new[] { "Approve" } }), timeout.Token);
        Assert.Equal("review", (await dispatch).Task.Role);

        const string rewriting = """
            export default {id:'route',title:'Route',tasks:{routing:[{when:{},provider:'codex',model:'gpt-5'}]}};
            """;
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source = rewriting }));
        var rewritten = await ctx.Kernel.DispatchWorkerAsync(snap.Track.Id, new("Inspect again"), "rewrite", null, timeout.Token);
        var worker = (await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single(session => session.BoundTaskId == rewritten.Task.Id);
        Assert.Equal("codex", worker.Provider);
        Assert.Equal("gpt-5", worker.Model);
        Assert.Null(rewritten.Task.Provider);
    }

    [Fact]
    public async Task TurnBefore_AskHumanWaitsOutsideMailboxAndPreservesContext()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = new McpSessionPrincipal("token", snapshot.Track.Id, snapshot.Track.LeadSessionId!, true, null, null, []);
        const string source = """
            export default {id:'turn',title:'Turn',turnContext:()=> 'Policy context',before(a,ctx){
              if(a.type!=='turn.before') return Allow();
              ctx.state.calls=(ctx.state.calls||0)+1;
              return AskHuman('Approve turn?',['Approve','Reject']);
            }};
            """;
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wake = ctx.SendAndWaitForTurnAsync(caller.SessionId, new SendMessageRequest("Review context", "policy-turn"), timeout.Token);
        ApprovalDto? ask = null;
        while (ask is null)
        {
            ask = (await ctx.Kernel.GetTrackAggregateAsync(snapshot.Track.Id, timeout.Token)).PendingApprovals.FirstOrDefault();
            if (ask is null) await Task.Delay(10, timeout.Token);
        }
        Assert.DoesNotContain(ctx.AgentRuntime.OpenedSessions.SelectMany(session => session.RecordedInputs), input => input.Prompt.Contains("Review context", StringComparison.Ordinal));
        await ctx.Kernel.DecideApprovalAsync(snapshot.Track.Id, ask.ExecutionId, ask.Id, TestJson.FromObject(new { answers = new[] { "Approve" } }), timeout.Token);
        await wake;
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id, timeout.Token);
        var input = Assert.Single(ctx.AgentRuntime.OpenedSessions.SelectMany(session => session.RecordedInputs), input => input.Prompt.Contains("Review context", StringComparison.Ordinal));
        Assert.Contains("Policy context", input.Prompt);
        Assert.Equal(1, (await ctx.Kernel.GetTrackAggregateAsync(snapshot.Track.Id)).Track.TemplateState.GetProperty("calls").GetInt32());
    }

    [Fact]
    public async Task PendingTaskAfterRestart_RechecksCurrentDispatchPolicy()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = new McpSessionPrincipal("token", snapshot.Track.Id, snapshot.Track.LeadSessionId!, true, null, null, []);
        const string source = "export default {id:'deny',title:'Deny',before:a=>a.type==='task.dispatch'?Deny('Blocked after restart'):Allow()};";
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source }));
        var report = await ctx.Kernel.ReadReportAsync(snapshot.Track.Id);
        await ctx.Kernel.CommitReportAsync(snapshot.Track.Id, caller.SessionId, new(report.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Task, null, TestJson.FromObject(new {
                task_key = "pending", role = "review", ready = false, access = "read_only", goal_or_cmd = "Inspect" }))]));
        var draft = Assert.Single(await ctx.Kernel.ListTasksAsync(snapshot.Track.Id));
        await ctx.Store.UpsertTaskAndAttemptAsync(draft with { Ready = true, Status = TaskRunStatus.Pending }, null, null, null, default);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snapshot.Track.Id);
        await ctx.Kernel.RejectTaskAsync(snapshot.Track.Id, caller.SessionId, draft.TaskKey, "Retry after restart", reQueue: true);
        var failed = Assert.Single(await ctx.Kernel.ListTasksAsync(snapshot.Track.Id));
        Assert.Equal(TaskRunStatus.Failed, failed.Status);
        Assert.Equal("Blocked after restart", failed.StatusDetail);
        Assert.DoesNotContain(await ctx.Kernel.ListSessionsAsync(snapshot.Track.Id), session => session.BoundTaskId == failed.Id);
    }

    [Fact]
    public async Task UnifiedToolBefore_AllowsOriginalArguments_AndAfterUpdatesState()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = new McpSessionPrincipal("token", snap.Track.Id, snap.Track.LeadSessionId!, true, null, null, []);
        const string source = """
            export default {id:'tools',title:'Tools',before(a) {
              return Allow();
            },after(e,ctx){ctx.state.last=e.type;}};
            """;
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source }));
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackRename, TestJson.FromObject(new { title = "Original" }));
        var current = await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id);
        Assert.Equal("Original", current.Track.Title);
        Assert.Equal("tool.perga_track_rename", current.Track.TemplateState.GetProperty("last").GetString());
        var before = current.Track.TemplateState.GetRawText();
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source, dryRun = true }));
        Assert.Equal(before, (await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id)).Track.TemplateState.GetRawText());
        Assert.Contains(KernelMcpCatalog.GetToolsForSession(null, true), tool => tool.Name == KernelMcpToolNames.TemplateUpdateInputs);
        Assert.DoesNotContain(KernelMcpCatalog.GetToolsForSession("attempt", false), tool => tool.Name == KernelMcpToolNames.TemplateUpdateInputs);
    }
}
