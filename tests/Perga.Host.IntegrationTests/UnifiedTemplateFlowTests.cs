using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

[Collection("Template policy sandbox")]
public sealed class UnifiedTemplateFlowTests
{
    private const string Source = """
        export default {id:'unified', title:'Unified', inputs:{target:{type:'string',default:'first'}},
          initialState:()=>({started:true}),
          data:{initial:()=>({business:"untouched"})},
          setup() {
            return {flow:[{id:'pulse', title:'Pulse', when:{cron:'* * * * *',tz:'UTC'},
              do:{kind:'wake',session:'lead',prompt:'Check ${inputs.target}'}}]};
          }
        };
        """;

    [Fact]
    public async Task TemplateBlueprintAndClockProgressSurviveInputPatchAndRestartWithoutOverwritingDraft()
    {
        var directory = Directory.CreateTempSubdirectory("perga-template-flow-").FullName;
        string id;
        string draftPath;
        const string draft = "local draft";
        long revision;
        long? nextFire;
        try
        {
            await using (var host = Host(directory))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                kernel.Templates.RegisterTemplate(Source);
                var area = await kernel.CreateAreaAsync(new("Unified", DefaultCwd: directory));
                var created = await kernel.CreateTrackAsync(new(area.Id, TemplateId:"unified"));
                id = created.Track.Id;
                var flow = await kernel.GetTrackFlowAsync(id);
                var initial = Assert.Single(flow.Projection.Nodes);
                Assert.Equal("template", initial.Source);
                Assert.Equal("Check first", initial.Prompt);
                Assert.Contains("Check ${inputs.target}", flow.Spec.GetRawText());

                host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(initial.NextFireAtMs!.Value));
                Assert.Equal(1, await host.Services.GetRequiredService<CronosScheduleHostedService>().EvaluateDueSchedulesOnceAsync());
                await kernel.WaitForTrackIdleAsync(id);
                Assert.Contains(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt.Contains("Check first"));
                var fired = await kernel.GetTrackFlowAsync(id);
                revision = fired.FlowRev;
                nextFire = Assert.Single(fired.Projection.Nodes).NextFireAtMs;
                Assert.Equal(1, Assert.Single(fired.Projection.Nodes).FiredCount);

                draftPath = fired.FilePath!;
                await File.WriteAllTextAsync(draftPath, draft);
                using var patch = await host.Client.PatchAsJsonAsync($"/api/v1/tracks/{id}/template/inputs", new { target = "second" });
                patch.EnsureSuccessStatusCode();
                var refreshed = await kernel.GetTrackFlowAsync(id);
                Assert.Equal(revision, refreshed.FlowRev);
                Assert.Equal(fired.Spec.GetRawText(), refreshed.Spec.GetRawText());
                Assert.Equal(draft, await File.ReadAllTextAsync(draftPath));
                var clock = Assert.Single(refreshed.Projection.Nodes);
                Assert.Equal("Check second", clock.Prompt);
                var store = host.Services.GetRequiredService<ITrackScheduleStore>();
                Assert.Equal("Check second", (await store.GetAsync(id, "flow:pulse", default))!.Prompt);
                Assert.Equal(1, clock.FiredCount);
                Assert.Equal(nextFire, clock.NextFireAtMs);
                var persisted = (await kernel.GetTrackAggregateAsync(id)).Track;
                Assert.True(JsonElement.DeepEquals(created.Track.Data, persisted.Data));
                Assert.True(persisted.TemplateState.GetProperty("started").GetBoolean());
            }

            await using var reopened = Host(directory);
            var restarted = reopened.Services.GetRequiredService<IPergaKernel>();
            var flowAfterRestart = await restarted.GetTrackFlowAsync(id);
            Assert.Equal(revision, flowAfterRestart.FlowRev);
            Assert.Equal(draft, await File.ReadAllTextAsync(draftPath));
            Assert.Equal(nextFire, Assert.Single(flowAfterRestart.Projection.Nodes).NextFireAtMs);
            Assert.Equal(1, Assert.Single(flowAfterRestart.Projection.Nodes).FiredCount);
            reopened.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(nextFire!.Value));
            Assert.Equal(1, await reopened.Services.GetRequiredService<CronosScheduleHostedService>().EvaluateDueSchedulesOnceAsync());
            await restarted.WaitForTrackIdleAsync(id);
            Assert.Contains(reopened.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt.Contains("Check second"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ApplyingTemplateAddsBlueprintWithoutChangingExistingNodesOrDirtyDraft()
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        await FlowFixture.AddAsync(kernel, id, new FlowNodeSpecDto("existing",
            new("run", Prompt: "Existing"), Source: "user", When: new(At: "2099-01-01T00:00:00Z")));
        var before = await kernel.GetTrackFlowAsync(id);
        const string draft = "uncommitted";
        await File.WriteAllTextAsync(before.FilePath!, draft);
        var principal = new McpSessionPrincipal("token", id, leadId, true, null, null, []);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = Source, dryRun = true }));
        Assert.Equal(before.FlowRev, (await kernel.GetTrackFlowAsync(id)).FlowRev);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = Source }));
        var after = await kernel.GetTrackFlowAsync(id);
        Assert.Equal(before.FlowRev + 1, after.FlowRev);
        Assert.Equal(draft, await File.ReadAllTextAsync(before.FilePath!));
        Assert.Equal("user", Assert.Single(after.Projection.Nodes, node => node.Id == "existing").Source);
        Assert.Equal("template", Assert.Single(after.Projection.Nodes, node => node.Id == "pulse").Source);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = Source }));
        Assert.Equal(after.FlowRev, (await kernel.GetTrackFlowAsync(id)).FlowRev);
    }

    [Fact]
    public async Task InputValidationIsSharedByCreateMcpBindingAndBothInputPatchEntrypoints()
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate(Source);
        var area = await kernel.CreateAreaAsync(new("Inputs",DefaultCwd:host.DataDir));
        using var badCreate = await host.Client.PostAsJsonAsync("/api/v1/tracks",new {areaId=area.Id,templateId="unified",templateInputs=new {target=42}});
        Assert.Equal(HttpStatusCode.BadRequest,badCreate.StatusCode);
        var created = await kernel.CreateTrackAsync(new(area.Id));
        var id = created.Track.Id;
        var principal = new McpSessionPrincipal("token",id,created.Track.LeadSessionId!,true,null,null,[]);
        var badBind = await Assert.ThrowsAsync<KernelException>(()=>KernelMcpDispatcher.ExecuteGuardedAsync(kernel,principal,KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new {source=Source,inputs=new {target=false}})));
        Assert.Equal(KernelErrorCode.InvalidArgument,badBind.Code);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel,principal,KernelMcpToolNames.TrackSet,JsonSerializer.SerializeToElement(new {source=Source}));
        var badMcpPatch = await Assert.ThrowsAsync<KernelException>(()=>KernelMcpDispatcher.ExecuteGuardedAsync(kernel,principal,KernelMcpToolNames.TemplateUpdateInputs,
            JsonSerializer.SerializeToElement(new {inputs=new {target=1}})));
        Assert.Equal(KernelErrorCode.InvalidArgument,badMcpPatch.Code);
        using var badPatch = await host.Client.PatchAsJsonAsync($"/api/v1/tracks/{id}/template/inputs",new {target=1});
        Assert.Equal(HttpStatusCode.BadRequest,badPatch.StatusCode);
        Assert.Equal("Check first",Assert.Single((await kernel.GetTrackFlowAsync(id)).Projection.Nodes).Prompt);
    }

    [Fact]
    public async Task NullInputPatchRemovesOptionalAndDefaultedKeysButRejectsRequiredRemoval()
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate("""
            export default {id:'clear-inputs',title:'Clear inputs',inputs:{
              optional:{type:'string',optional:true}, required:{type:'string'},
              defaulted:{type:'string',default:'fallback'}
            }};
            """);
        var area = await kernel.CreateAreaAsync(new("Inputs", DefaultCwd: host.DataDir));
        var created = await kernel.CreateTrackAsync(new(area.Id, TemplateId: "clear-inputs",
            TemplateInputs: JsonSerializer.SerializeToElement(new { optional = "clear", required = "keep", defaulted = "override" })));
        var id = created.Track.Id;
        var url = $"/api/v1/tracks/{id}/template/inputs";
        using var cleared = await host.Client.PatchAsJsonAsync(url, new Dictionary<string, object?> { ["optional"] = null, ["defaulted"] = null });
        cleared.EnsureSuccessStatusCode();
        var inputs = (await kernel.GetTrackAggregateAsync(id)).Track.TemplateInputs;
        Assert.False(inputs.TryGetProperty("optional", out _));
        Assert.False(inputs.TryGetProperty("defaulted", out _));
        Assert.Equal("keep", inputs.GetProperty("required").GetString());
        using var rejected = await host.Client.PatchAsJsonAsync(url, new Dictionary<string, object?> { ["required"] = null });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(inputs.GetRawText(), (await kernel.GetTrackAggregateAsync(id)).Track.TemplateInputs.GetRawText());
    }

    private static PergaHostTestCluster Host(string? directory=null) => new(explicitDataDir:directory,configureHostOptions:o=>
    {
        o.EnableScheduleService=false; o.EnableMaintenanceService=false;
    });
}
