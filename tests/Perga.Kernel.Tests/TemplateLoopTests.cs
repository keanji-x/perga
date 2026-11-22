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
    public async Task FlowDispatchWaitsForApprovalBeforeLaunchingWorker()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var caller = new McpSessionPrincipal("token", snap.Track.Id, snap.Track.LeadSessionId!, true, null, null, []);
        const string asking = """
            export default {id:'ask',title:'Ask',before(a,ctx) {
              return a.type === 'task.dispatch' ? AskHuman('Approve dispatch?',{approve:'Approve',deny:'Reject'}) : Allow();
            }};
            """;
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source = asking }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await FlowFixture.AddAsync(ctx.Kernel, snap.Track.Id, FlowFixture.Worker("ask", "Inspect", role: "review"));
        ApprovalDto? ask = null;
        while (ask is null)
        {
            ask = (await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id, timeout.Token)).PendingApprovals.FirstOrDefault();
            if (ask is null) await Task.Delay(10, timeout.Token);
        }
        var pending = Assert.Single(await ctx.Kernel.ListTasksAsync(snap.Track.Id, timeout.Token));
        Assert.Equal("review", pending.Role);
        Assert.Equal(TaskRunStatus.Pending, pending.Status);
        Assert.Null(pending.SessionId);
        await ctx.Kernel.RespondInteractionAsync(ask.SessionId, ask.Id, new(ExecutionId: ask.ExecutionId,
            OptionId: ask.Options.Single(o => o.IsApproval).Id), new InteropPrincipal.Human(), timeout.Token);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id, timeout.Token);
        Assert.NotNull((await ctx.Kernel.ListTasksAsync(snap.Track.Id, timeout.Token)).Single().SessionId);

        const string rewriting = """
            export default {id:'route',title:'Route',tasks:{routing:[{when:{},provider:'codex',model:'gpt-5'}]}};
            """;
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source = rewriting }));
        var rewritten = await FlowFixture.StartWorkerAsync(ctx.Kernel, snap.Track.Id, FlowFixture.Worker("rewrite", "Inspect again"));
        var worker = rewritten.Session;
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
              return AskHuman('Approve turn?',{approve:'Approve',deny:'Reject'});
            }};
            """;
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wake = ctx.SendAndWaitForTurnAsync(caller.SessionId, new SendSessionMessageRequest("Review context", "policy-turn"), timeout.Token);
        ApprovalDto? ask = null;
        while (ask is null)
        {
            ask = (await ctx.Kernel.GetTrackAggregateAsync(snapshot.Track.Id, timeout.Token)).PendingApprovals.FirstOrDefault();
            if (ask is null) await Task.Delay(10, timeout.Token);
        }
        Assert.DoesNotContain(ctx.AgentRuntime.OpenedSessions.SelectMany(session => session.RecordedInputs), input => input.Prompt.Contains("Review context", StringComparison.Ordinal));
        await ctx.Kernel.RespondInteractionAsync(ask.SessionId, ask.Id, new(ExecutionId: ask.ExecutionId, OptionId: ask.Options.Single(o => o.IsApproval).Id), new InteropPrincipal.Human(), timeout.Token);
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
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        await FlowFixture.AddAsync(ctx.Kernel, snapshot.Track.Id, FlowFixture.Worker("pending", "Inspect", role: "review"));
        Assert.Equal(TaskRunStatus.Pending, Assert.Single(await ctx.Kernel.ListTasksAsync(snapshot.Track.Id)).Status);
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(snapshot.Track.Id);
        ctx.Kernel.DispatchAdmission.SetDraining(false);
        await ctx.Kernel.Actors.GetOrActivate(snapshot.Track.Id).TriggerSchedulerAsync();
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var failed = Assert.Single(await ctx.Kernel.ListTasksAsync(snapshot.Track.Id));
        Assert.Equal(TaskRunStatus.Failed, failed.Status);
        Assert.Equal("Blocked after restart", failed.StatusDetail);
        Assert.DoesNotContain(await ctx.Kernel.ListSessionsAsync(snapshot.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default), summary => summary.TaskId == failed.Id);
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
        Assert.Equal("tool.track_rename", current.Track.TemplateState.GetProperty("last").GetString());
        var before = current.Track.TemplateState.GetRawText();
        await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new { source, dryRun = true }));
        Assert.Equal(before, (await ctx.Kernel.GetTrackAggregateAsync(snap.Track.Id)).Track.TemplateState.GetRawText());
        Assert.Contains(KernelMcpCatalog.GetToolsForSession(null, true), tool => tool.Name == KernelMcpToolNames.TemplateUpdateInputs);
        Assert.DoesNotContain(KernelMcpCatalog.GetToolsForSession("attempt", false), tool => tool.Name == KernelMcpToolNames.TemplateUpdateInputs);
    }
}
