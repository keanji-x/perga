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
                      inputs:{unit:{type:'string',default:'steps'},keep:{type:'string',optional:true}},
                      data:{initial:{count:7}},
                      headerView:ctx=>({metrics:[{label:'Count',value:ctx.data.count+' '+ctx.inputs.unit}]}),
                      workspace:{driver:'direct'},
                      tasks:{routing:[{when:{role:'missing'},provider:'missing',model:'unavailable'}]}}
                    """;
                kernel.Templates.RegisterTemplate(source);
                var summaries = await Call(KernelMcpToolNames.TemplateList, new { });
                var summary = Assert.Single(summaries.EnumerateArray(), p => p.GetProperty("id").GetString() == "loop");
                Assert.Equal(7, summary.EnumerateObject().Count());
                Assert.True(summary.GetProperty("hasHeaderView").GetBoolean());
                Assert.Equal(["unit", "keep"], summary.GetProperty("inputKeys").EnumerateArray().Select(key => key.GetString()));
                var preview = await Call(KernelMcpToolNames.TrackSet, new { templateId = "loop", dryRun = true });
                Assert.Equal(2, preview.GetProperty("warnings").GetArrayLength());
                Assert.Null((await kernel.GetTrackAggregateAsync(trackId)).Track.TemplateId);
                var applied = await Call(KernelMcpToolNames.TrackSet, new { templateId = "loop", inputs = new { unit = "km", keep = "retained" } });
                Assert.Equal("loop", applied.GetProperty("templateId").GetString());
                await Call(KernelMcpToolNames.TemplateUpdateInputs, new { inputs = new { unit = "m" } });
                var current = await Call(KernelMcpToolNames.TemplateRead, new { templateId = "__current__", format = "full" });
                Assert.Equal(source, current.GetProperty("snapshot").GetString());
                Assert.Equal("retained", current.GetProperty("inputs").GetProperty("keep").GetString());
                Assert.Equal(7, current.GetProperty("data").GetProperty("count").GetInt32());
                Assert.Equal("7 m", current.GetProperty("headerView").GetProperty("metrics")[0].GetProperty("value").GetString());
                var stored = await host.Services.GetRequiredService<ITrackStore>().LoadTrackAggregateAsync(trackId, default);
                Assert.Equal(7, stored!.Track.Data.GetProperty("count").GetInt32());
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
                Assert.Equal(7, persisted.Track.Data.GetProperty("count").GetInt32());
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
        var receipt = await FlowFixture.StartWorkerAsync(kernel, trackId, "review", "Review this", role: "review");
        await kernel.WaitForTrackIdleAsync(trackId);
        Assert.Equal("review", receipt.Task.Role);
        Assert.Equal("claude", receipt.Session.Provider);
        Assert.Equal("claude-sonnet-4-6", receipt.Session.Model);
        var before = await kernel.ReadReportAsync(trackId);
        var denied = await FlowFixture.AppendAsync(kernel, trackId, new FlowNodeSpecDto("conflict",
            new("run", Prompt: "Conflict", Model: "gpt-5.4", Role: "review", Access: "read_only")));
        Assert.False(denied.Ok);
        Assert.Equal(before.DocRev, (await kernel.ReadReportAsync(trackId)).DocRev);
        var stored = await host.Services.GetRequiredService<ITrackStore>().LoadTrackAggregateAsync(trackId, default);
        Assert.All(stored!.Tasks, task => Assert.Equal("review", task.Role));
    }
}

// Real policy execution uses a wall-clock sandbox budget. Keep these scenarios
// apart from concurrent test workloads without relaxing production limits.
[CollectionDefinition("Template policy sandbox", DisableParallelization = true)]
public sealed class TemplatePolicySandboxCollection;
