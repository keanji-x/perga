using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.ReportDoc;
using static Perga.Host.IntegrationTests.McpKernelAndVcsIntegrationTests;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class TemplateLoopTests
{
    private static async Task<(string TrackId, string LeadId)> CreateTrackAsync(PergaHostTestCluster host, IPergaKernel kernel)
    {
        var area = await kernel.CreateAreaAsync(new("Templates", DefaultCwd: host.DataDir));
        var track = (await kernel.CreateTrackAsync(new(area.Id, Cwd: host.DataDir))).Track;
        await kernel.WaitForTrackIdleAsync(track.Id);
        return (track.Id, track.LeadSessionId!);
    }

    [Fact]
    public async Task PlannerTools_PreviewApplyUpdateInspectAndDetachAcrossRestart()
    {
        var directory = Directory.CreateTempSubdirectory("perga-template-loop-");
        try
        {
            string trackId;
            string leadId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                (trackId, leadId) = await CreateTrackAsync(host, kernel);
                var token = kernel.McpSessionBridge.RegisterSession(trackId, leadId, true, null, null, []).Principal.Token;
                const string source = """
                    export default { id:'loop',title:'Loop',description:'Template loop',
                      inputs:{unit:{type:'string',default:'steps'}},
                      initialState:()=>({count:7}),
                      headerView:ctx=>({metrics:[{label:'Count',value:ctx.state.count+' '+ctx.inputs.unit}]}),
                      workspace:{driver:'direct'},
                      tasks:{routing:[{when:{role:'missing'},provider:'missing',model:'unavailable'}]}}
                    """;
                kernel.Templates.RegisterTemplate(source);
                var summaries = await Call(KernelMcpToolNames.TemplateLs, new { });
                var summary = Assert.Single(summaries.EnumerateArray(), p => p.GetProperty("id").GetString() == "loop");
                Assert.Equal(7, summary.EnumerateObject().Count());
                Assert.True(summary.GetProperty("hasHeaderView").GetBoolean());
                Assert.Equal("unit", Assert.Single(summary.GetProperty("inputKeys").EnumerateArray()).GetString());
                var preview = await Call(KernelMcpToolNames.TrackSet, new { templateId = "loop", dryRun = true });
                Assert.Equal(2, preview.GetProperty("warnings").GetArrayLength());
                Assert.Null((await kernel.GetTrackAggregateAsync(trackId)).Track.TemplateId);
                var applied = await Call(KernelMcpToolNames.TrackSet, new { templateId = "loop", inputs = new { unit = "km", keep = "retained" } });
                Assert.Equal("loop", applied.GetProperty("templateId").GetString());
                await Call(KernelMcpToolNames.TemplateUpdateInputs, new { inputs = new { unit = "m" } });
                var current = await Call(KernelMcpToolNames.TemplateCat, new { templateId = "__current__" });
                Assert.Equal(source, current.GetProperty("snapshot").GetString());
                Assert.Equal("retained", current.GetProperty("inputs").GetProperty("keep").GetString());
                Assert.Equal(7, current.GetProperty("state").GetProperty("count").GetInt32());
                Assert.Equal("7 m", current.GetProperty("headerView").GetProperty("metrics")[0].GetProperty("value").GetString());
                var stored = await host.Services.GetRequiredService<ITrackStore>().LoadTrackAggregateAsync(trackId, default);
                Assert.Equal(7, stored!.Track.TemplateState.GetProperty("count").GetInt32());
                var report = await kernel.ReadReportAsync(trackId);
                await kernel.CommitReportAsync(trackId, leadId, new(report.Anchor,
                    [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "Preserved report", null)]));

                async Task<JsonElement> Call(string name, object args)
                {
                    var (text, error) = await CallToolAsync(host, "/mcp/kernel", token, name, args, default);
                    Assert.False(error, text);
                    return JsonSerializer.Deserialize<JsonElement>(text);
                }
            }
            await using (var reopened = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = reopened.Services.GetRequiredService<IPergaKernel>();
                var token = kernel.McpSessionBridge.RegisterSession(trackId, leadId, true, null, null, []).Principal.Token;
                var persisted = await kernel.GetTrackAggregateAsync(trackId);
                Assert.Equal("m", persisted.Track.TemplateInputs.GetProperty("unit").GetString());
                Assert.Equal(7, persisted.Track.TemplateState.GetProperty("count").GetInt32());
                var (text, error) = await CallToolAsync(reopened, "/mcp/kernel", token, KernelMcpToolNames.TrackSet, new { clear = true }, default);
                Assert.False(error, text);
                Assert.Null((await kernel.GetTrackAggregateAsync(trackId)).Track.TemplateId);
                Assert.Contains("Preserved report", (await kernel.ReadReportAsync(trackId)).RenderedMarkdown);
                await kernel.Actors.RemoveAndDisposeAsync(trackId);
                Assert.Null((await kernel.GetTrackAggregateAsync(trackId)).Track.TemplateId);
            }
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("[]", false)]
    [InlineData("['human']", false)]
    [InlineData("['LEAD']", true)]
    public async Task DeclarativeReportRules_EnforceLimitsAndSectionRolesBeforeHooks(string editableBy, bool allowed)
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, leadId) = await CreateTrackAsync(host, kernel);
        var caller = kernel.McpSessionBridge.RegisterSession(trackId, leadId, true, null, null, []).Principal;
        var report = await kernel.ReadReportAsync(trackId);
        await kernel.CommitReportAsync(trackId, leadId, new(report.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "# Locked\n\nOriginal", null)]));
        var source = "export default {id:'rules',title:'Rules',report:{maxProseChars:100,sections:[{h1:'# locked',editableBy:"
            + editableBy + "}]},before(a,ctx){if(a.type==='report.commit')ctx.state.calls=(ctx.state.calls||0)+1;return Allow();}};";
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, caller, KernelMcpToolNames.TrackSet, JsonSerializer.SerializeToElement(new { source }));
        report = await kernel.ReadReportAsync(trackId);
        var commit = kernel.CommitReportAsync(trackId, leadId, new(report.Anchor,
            [new BlockOp.ReplaceSectionOp("Locked", "# Locked\n\nChanged")]));
        if (allowed) await commit;
        else Assert.Equal(KernelErrorCode.PolicyDenied, (await Assert.ThrowsAsync<KernelException>(() => commit)).Code);
        var state = (await kernel.GetTrackAggregateAsync(trackId)).Track.TemplateState;
        Assert.Equal(allowed, state.TryGetProperty("calls", out _));
        report = await kernel.ReadReportAsync(trackId);
        var oversized = await Assert.ThrowsAsync<KernelException>(() => kernel.CommitReportAsync(trackId, leadId,
            new(report.Anchor, [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, new string('x', 101), null)])));
        Assert.Equal(KernelErrorCode.PolicyDenied, oversized.Code);
        Assert.Equal(report.DocRev, (await kernel.ReadReportAsync(trackId)).DocRev);
    }

    [Fact]
    public async Task Routing_AppliesToWorkersAndTaskBlocks_RejectsConflictsAndUnavailableModels_PersistsRole()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, leadId) = await CreateTrackAsync(host, kernel);
        var caller = kernel.McpSessionBridge.RegisterSession(trackId, leadId, true, null, null, []).Principal;
        var source = """
            {"id":"route","title":"Route","inputs":{"model":{"type":"string","default":"claude-sonnet-4-6"}},
            "tasks":{"routing":[{"when":{"role":"review","access":"read_only"},"provider":"claude","model":"${inputs.model}","enforce":true}]}}
            """;
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, caller, KernelMcpToolNames.TrackSet, JsonSerializer.SerializeToElement(new { source }));
        var receipt = await kernel.CreateSessionAsync(trackId, new(Prompt: "Review this", Task: new(), Role: "review"), "route", new InteropPrincipal.Human(), default);
        await kernel.WaitForTrackIdleAsync(trackId);
        Assert.Equal("review", receipt.Receipt.Task!.Role);
        var worker = Assert.Single((await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray(), s => s.BoundTaskId == receipt.Receipt.Task!.Id);
        Assert.Equal("claude", worker.Provider);
        Assert.Equal("claude-sonnet-4-6", worker.Model);
        var repeated = await kernel.CreateSessionAsync(trackId, new(Prompt: "Review this", Task: new(), Role: "review"), "route", new InteropPrincipal.Human(), default);
        Assert.Equal(receipt.Receipt.Task!.Id, repeated.Receipt.Task!.Id);
        var before = await kernel.ReadReportAsync(trackId);
        var denied = await Assert.ThrowsAsync<KernelException>(() => kernel.CreateSessionAsync(trackId, new(Prompt: "Conflict", Task: new(), Model: "gpt-5.4", Role: "review"), "conflict", new InteropPrincipal.Human(), default));
        Assert.Equal(KernelErrorCode.PolicyDenied, denied.Code);
        Assert.Equal(before.DocRev, (await kernel.ReadReportAsync(trackId)).DocRev);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, caller, KernelMcpToolNames.TemplateUpdateInputs,
            JsonSerializer.SerializeToElement(new { inputs = new { model = "missing-model" } }));
        var unavailable = await Assert.ThrowsAsync<KernelException>(() => kernel.CreateSessionAsync(trackId, new(Prompt: "Unavailable", Task: new(), Role: "review"), "unavailable", new InteropPrincipal.Human(), default));
        Assert.Equal(KernelErrorCode.InvalidArgument, unavailable.Code);
        Assert.Equal(before.DocRev, (await kernel.ReadReportAsync(trackId)).DocRev);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, caller, KernelMcpToolNames.TemplateUpdateInputs,
            JsonSerializer.SerializeToElement(new { inputs = new { model = "claude-sonnet-4-6" } }));
        var report = await kernel.ReadReportAsync(trackId);
        await kernel.CommitReportAsync(trackId, leadId, new(report.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Task, null, JsonSerializer.SerializeToElement(new
                { task_key = "authored", kind = "agent", ready = true, role = "review", access = "read_only", goal_or_cmd = "Inspect" }))]));
        await kernel.WaitForTrackIdleAsync(trackId);
        var authored = Assert.Single(await kernel.ListTasksAsync(trackId), t => t.TaskKey == "authored");
        Assert.Equal("review", authored.Role);
        Assert.Null(authored.Provider);
        var authoredWorker = Assert.Single((await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray(), s => s.BoundTaskId == authored.Id);
        Assert.Equal("claude", authoredWorker.Provider);
        Assert.Equal("claude-sonnet-4-6", authoredWorker.Model);
        var authoredBlock = Assert.Single((await kernel.ReadReportAsync(trackId)).Blocks, b => b.Id == authored.BlockId);
        Assert.False(authoredBlock.Payload!.Value.TryGetProperty("provider", out _));
        var stored = await host.Services.GetRequiredService<ITrackStore>().LoadTrackAggregateAsync(trackId, default);
        Assert.All(stored!.Tasks, task => Assert.Equal("review", task.Role));
    }
}

// Real policy execution uses a wall-clock sandbox budget. Keep these scenarios
// apart from concurrent test workloads without relaxing production limits.
[CollectionDefinition("Template policy sandbox", DisableParallelization = true)]
public sealed class TemplatePolicySandboxCollection;
