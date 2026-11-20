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
public sealed class UnifiedTemplateScheduleTests
{
    private const string Source = """
        export default {id:'unified', title:'Unified', inputs:{target:{type:'string',default:'first'}},
          initialState:()=>({fires:0}),
          after(event,ctx) {
            if(event.type !== 'schedule.fired') return;
            ctx.state.fires++;
            if(ctx.inputs.target === 'suppress') return ' ';
            return event.sourceKind + ':' + event.prompt;
          },
          schedules:[{id:'pulse', title:'Pulse', cron:'* * * * *', timezone:'UTC', prompt:'Check ${inputs.target}'}]
        };
        """;

    [Fact]
    public async Task CreationAndInputPatchMaterializeAndPreserveProgressAcrossRestart()
    {
        var directory = Directory.CreateTempSubdirectory("perga-unified-").FullName;
        string id;
        TrackScheduleDto expected;
        try
        {
            await using (var host = Host(directory))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                kernel.Templates.RegisterTemplate(Source);
                var area = await kernel.CreateAreaAsync(new("Unified", DefaultCwd: directory));
                var created = await kernel.CreateTrackAsync(new(area.Id, TemplateId:"unified"));
                id = created.Track.Id;
                var row = Assert.Single(await kernel.ListTrackSchedulesAsync(id));
                Assert.Equal("template", row.SourceKind);
                Assert.Equal("Check first", row.Prompt);
                host.TimeProvider.Advance(TimeSpan.FromMinutes(2));
                Assert.Equal(1, await host.Services.GetRequiredService<CronosScheduleHostedService>().EvaluateDueSchedulesOnceAsync());
                await kernel.WaitForTrackIdleAsync(id);
                var fired = Assert.Single(await kernel.ListTrackSchedulesAsync(id));
                Assert.Equal(1, fired.FiredCount);
                using var patch = await host.Client.PatchAsJsonAsync($"/api/v1/tracks/{id}/template/inputs", new {target="second"});
                patch.EnsureSuccessStatusCode();
                expected = Assert.Single(await kernel.ListTrackSchedulesAsync(id));
                Assert.Equal(fired with {Prompt="Check second", UpdatedAtMs=expected.UpdatedAtMs}, expected);
                Assert.Equal(1, (await kernel.GetTrackAggregateAsync(id)).Track.TemplateState.GetProperty("fires").GetInt32());
                Assert.Contains(host.AgentRuntime.ExecutedTurns, t=>t.Input.Prompt.Contains("template:Check first", StringComparison.Ordinal));
                var events = await host.Services.GetRequiredService<ITrackReadStore>().ListEventsSinceAsync(0, id);
                Assert.Contains(events, e=>e.Kind == KernelEventKinds.TrackSchedulesUpdated);
                Assert.Contains(events, e=>e.Kind == KernelEventKinds.TrackUpdated && e.Payload.TryGetProperty("templateState", out var state) && state.TryGetProperty("fires", out var fires) && fires.GetInt32()==1);
            }
            await using (var host = Host(directory))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                Assert.Equal(expected, Assert.Single(await kernel.ListTrackSchedulesAsync(id)));
                var snapshot = await kernel.GetTrackSchedulesAsync(id);
                Assert.Single(snapshot.RecentRuns);
                Assert.Equal("done", snapshot.RecentRuns[0].State);
                Assert.Equal(expected.Id, snapshot.RecentRuns[0].ScheduleId);
            }
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public async Task HttpScheduleCrudTriggerAndSuppressionUseSamePolicyAndWakeJournal()
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        kernel.Templates.RegisterTemplate(Source);
        var area = await kernel.CreateAreaAsync(new("Unified", DefaultCwd:host.DataDir));
        var created = await kernel.CreateTrackAsync(new(area.Id, TemplateId:"unified"));
        var id = created.Track.Id;
        using var subscription = kernel.EventBus.Subscribe(id);
        var url = $"/api/v1/tracks/{id}/schedules";
        using var posted = await host.Client.PostAsJsonAsync(url,new {id="user",title="User",prompt="User prompt",runAtMs=long.MaxValue});
        posted.EnsureSuccessStatusCode();
        Assert.Equal("user", (await posted.Content.ReadFromJsonAsync<TrackScheduleDto>())!.SourceKind);
        using var triggered = await host.Client.PostAsync(url+"/user/trigger",null);
        Assert.Equal(HttpStatusCode.Accepted,triggered.StatusCode);
        await kernel.WaitForTrackIdleAsync(id);
        Assert.Contains(host.AgentRuntime.ExecutedTurns,t=>t.Input.Prompt.Contains("user:User prompt",StringComparison.Ordinal));
        Assert.Equal(0,(await kernel.ListTrackSchedulesAsync(id)).Single(s=>s.Id=="user").FiredCount);
        using var patch = await host.Client.PatchAsJsonAsync($"/api/v1/tracks/{id}/template/inputs",new {target="suppress"});
        patch.EnsureSuccessStatusCode();
        var turns = host.AgentRuntime.ExecutedTurns.Count;
        using var suppressed = await host.Client.PostAsync(url+"/user/trigger",null);
        suppressed.EnsureSuccessStatusCode();
        await kernel.WaitForTrackIdleAsync(id);
        Assert.Equal(turns,host.AgentRuntime.ExecutedTurns.Count);
        using var runsResponse = await host.Client.GetAsync(url+"?runsLimit=1");
        var runs = await runsResponse.Content.ReadFromJsonAsync<JsonElement>();
        var run = Assert.Single(runs.GetProperty("recentRuns").EnumerateArray());
        Assert.Equal("dropped", run.GetProperty("state").GetString());
        Assert.Equal(new[] { "createdAtMs", "reason", "scheduleId", "state", "turnId", "updatedAtMs", "wakeId" },
            run.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("user", run.GetProperty("scheduleId").GetString());
        using var canceled = await host.Client.DeleteAsync(url+"/user");
        Assert.Equal(HttpStatusCode.NoContent,canceled.StatusCode);
        using var invalidTrigger = await host.Client.PostAsync(url+"/user/trigger",null);
        Assert.Equal(HttpStatusCode.Conflict,invalidTrigger.StatusCode);
        using var rearmed = await host.Client.PostAsJsonAsync(url,new {id="user",title="Rearmed",prompt="Again",runAtMs=long.MaxValue});
        rearmed.EnsureSuccessStatusCode();
        Assert.Equal("active",(await rearmed.Content.ReadFromJsonAsync<TrackScheduleDto>())!.State);
        using var invalidLimit = await host.Client.GetAsync(url+"?runsLimit=201");
        Assert.Equal(HttpStatusCode.BadRequest,invalidLimit.StatusCode);
        var live = new List<EventEnvelopeDto>();
        while (subscription.Reader.TryRead(out var evt)) live.Add(evt);
        var scheduleEvents = live.Where(e => e.Kind == KernelEventKinds.TrackSchedulesUpdated).ToArray();
        Assert.True(scheduleEvents.Length >= 5);
        Assert.All(scheduleEvents, e => { Assert.NotNull(e.EventId); Assert.True(KernelEventKinds.IsPersistent(e.Kind)); });
        var persisted = await host.Services.GetRequiredService<ITrackReadStore>().ListEventsSinceAsync(0,id);
        Assert.All(scheduleEvents, e => Assert.Contains(persisted, stored => stored.EventId == e.EventId && stored.Kind == e.Kind));

    }

    [Fact]
    public async Task BindingConflictRollsBackAndClearCancelsOnlyTemplateRowsAndWakes()
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id,_) = await host.CreateTrackAsync();
        var original = (await kernel.GetTrackAggregateAsync(id)).Track;
        await kernel.SetTrackScheduleAsync(id,new("User pulse","Preserve",Id:"pulse",RunAtMs:long.MaxValue));
        var principal = new McpSessionPrincipal("token",id,original.LeadSessionId!,true,null,null,[]);
        var error = await Assert.ThrowsAsync<KernelException>(()=>KernelMcpDispatcher.ExecuteGuardedAsync(kernel,principal,KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new {source=Source})));
        Assert.Equal(KernelErrorCode.Conflict,error.Code);
        Assert.Equal(original.TemplateId,(await kernel.GetTrackAggregateAsync(id)).Track.TemplateId);
        Assert.Equal("agent",Assert.Single(await kernel.ListTrackSchedulesAsync(id)).SourceKind);
        await kernel.CancelTrackScheduleAsync(id,"pulse");
        await kernel.SetTrackScheduleAsync(id,new("Independent","Keep",Id:"independent",RunAtMs:long.MaxValue));
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel,principal,KernelMcpToolNames.TrackSet,JsonSerializer.SerializeToElement(new {source=Source}));
        kernel.DispatchAdmission.SetDraining(true);
        host.TimeProvider.Advance(TimeSpan.FromMinutes(2));
        await kernel.FireDueTrackSchedulesAsync(host.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        using var cleared = await host.Client.DeleteAsync($"/api/v1/tracks/{id}/template");
        cleared.EnsureSuccessStatusCode();
        Assert.Equal("independent",Assert.Single(await kernel.ListTrackSchedulesAsync(id)).Id);
        var all = await kernel.GetTrackSchedulesAsync(id,true);
        Assert.Equal("canceled",all.Schedules.Single(s=>s.Id=="pulse").State);
        Assert.Equal("template_changed",Assert.Single(all.RecentRuns).Reason);
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
        Assert.Equal("Check first",Assert.Single(await kernel.ListTrackSchedulesAsync(id)).Prompt);
    }

    [Theory]
    [InlineData("template")]
    [InlineData("plugin")]
    [InlineData("agent")]
    [InlineData("")]
    public async Task HttpScheduleRejectsForgedSourceKind(string sourceKind)
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, _) = await host.CreateTrackAsync();
        using var response = await host.Client.PostAsJsonAsync($"/api/v1/tracks/{id}/schedules",
            new { id = "forged", title = "Forged", prompt = "Prompt", runAtMs = long.MaxValue, sourceKind });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_argument", error.GetProperty("code").GetString());
        Assert.Empty(await kernel.ListTrackSchedulesAsync(id));
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
