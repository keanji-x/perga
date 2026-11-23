using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Lifecycle;
using Perga.Host.Mcp;
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
          data:{initial:()=>({business:"untouched"})},
          setup() {
            return {flow:[{id:'pulse', title:'Pulse ${inputs.target}', when:{cron:'* * * * *',tz:'UTC'},
              do:{kind:'wake',session:'lead',prompt:'Check ${inputs.target}'}}]};
          }
        };
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReapplyRestoresDetachedRecurringNodeAndKeepsManuallyCanceledNode(bool archiveBeforeReapply)
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        var principal = await kernel.OpenMcpCallerAsync(leadId);
        var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../templates/okr-loop"));
        kernel.Templates.RegisterTemplate(Perga.PolicyVm.TemplateBundle.PackDirectory(package));
        var bind = JsonSerializer.SerializeToElement(new { templateId = "okr-loop" });
        var schedules = host.Services.GetRequiredService<CronosScheduleHostedService>();

        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet, bind);
        var initial = await kernel.GetTrackFlowAsync(id);
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(Assert.Single(initial.Projection.Nodes).NextFireAtMs!.Value));
        Assert.Equal(1, await schedules.EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(id);
        var first = Assert.Single(await kernel.ListTasksAsync(id));
        Assert.Equal(TaskRunStatus.Done, first.Status);
        using (var response = await host.AuthenticatedClient.DeleteAsync($"/api/v1/tracks/{id}/template"))
            response.EnsureSuccessStatusCode();
        if (archiveBeforeReapply)
        {
            var detached = await kernel.GetTrackFlowAsync(id);
            Assert.True((await kernel.CommitTrackFlowAsync(id, false, detached.FlowRev, new InteropPrincipal.Human(),
                content: detached.Spec.GetRawText())).Ok);
            Assert.NotNull(Assert.Single(await kernel.ListTasksAsync(id)).ArchivedAtMs);
            await kernel.Actors.RemoveAndDisposeAsync(id);
            principal = await kernel.OpenMcpCallerAsync(leadId);
        }
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet, bind);
        var reapplied = await kernel.GetTrackFlowAsync(id);
        var restored = Assert.Single(reapplied.Projection.Nodes);
        Assert.Equal("daily-ci-check", restored.Id);
        Assert.NotNull(restored.NextFireAtMs);
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(restored.NextFireAtMs.Value));
        Assert.Equal(1, await schedules.EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(id);
        var tasks = await kernel.ListTasksAsync(id);
        var old = Assert.Single(tasks, task => task.Id == first.Id);
        Assert.NotNull(old.ArchivedAtMs);
        Assert.Equal(first.SessionId, old.SessionId);
        Assert.Equal(first.RunCount, old.RunCount);
        var active = Assert.Single(tasks, task => task.ArchivedAtMs is null);
        Assert.Equal(TaskRunStatus.Done, active.Status);
        Assert.Equal(2, host.AgentRuntime.ExecutedTurns.Count(turn => turn.Input.Prompt.Contains("过去 24 小时 CI 指标")));

        await kernel.CancelTaskAsync(id, null, "daily-ci-check");
        var canceled = await kernel.GetTrackFlowAsync(id);
        Assert.True((await kernel.CommitTrackFlowAsync(id, false, canceled.FlowRev, new InteropPrincipal.Human(),
            content: canceled.Spec.GetRawText())).Ok);
        var beforeRebind = await kernel.GetTrackFlowAsync(id);
        principal = await kernel.OpenMcpCallerAsync(leadId);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { templateId = "okr-loop", inputs = new { checkCmd = "new-check" } }));
        var afterRebind = await kernel.GetTrackFlowAsync(id);
        Assert.Equal(beforeRebind.FlowRev, afterRebind.FlowRev);
        Assert.Empty(afterRebind.Projection.Nodes);
        Assert.Equal(TrackScheduleStates.Canceled,
            (await host.Services.GetRequiredService<ITrackScheduleStore>().GetAsync(id, "flow:daily-ci-check", default))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementUsesNewBlueprintAndRearmsRetainedConsumer(bool singleOccurrence)
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        var principal = await kernel.OpenMcpCallerAsync(leadId);
        var schedules = host.Services.GetRequiredService<CronosScheduleHostedService>();
        var oldSource = Source.Replace("target", "oldTarget", StringComparison.Ordinal);
        var newSource = Source.Replace("unified", "replacement", StringComparison.Ordinal)
            .Replace("target", "newTarget", StringComparison.Ordinal);
        if (singleOccurrence)
            newSource = newSource.Replace("tz:'UTC'", "tz:'UTC',maxOccurrences:1", StringComparison.Ordinal);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = oldSource, inputs = new { oldTarget = "old value" } }));
        await FlowFixture.AddAsync(kernel, id, new FlowNodeSpecDto("consumer",
            new("wake", Session: "lead", Prompt: "Consume pulse"), When: new(After: [new("pulse", "ok")])));
        var store = host.Services.GetRequiredService<ITrackReadStore>();
        var before = await kernel.GetTrackFlowAsync(id);
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(
            Assert.Single(before.Projection.Nodes, node => node.Id == "pulse").NextFireAtMs!.Value));
        Assert.Equal(1, await schedules.EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(id);
        var firstWave = await kernel.ListTasksAsync(id);
        var first = Assert.Single(firstWave, task => task.TaskKey == "pulse");
        var consumer = Assert.Single(firstWave, task => task.TaskKey == "consumer");
        Assert.Equal(TaskRunStatus.Done, consumer.Status);
        Assert.Equal(1, consumer.RunCount);
        Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt.Contains("Consume pulse"));
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = newSource, inputs = new { newTarget = "new value" } }));
        var after = await kernel.GetTrackFlowAsync(id);
        Assert.Equal(before.FlowRev + 1, after.FlowRev);
        var node = Assert.Single(after.Projection.Nodes, node => node.Id == "pulse");
        Assert.Equal("Check new value", node.Prompt);
        Assert.Equal(0, node.FiredCount);
        var retained = Assert.Single((await store.LoadTrackAggregateAsync(id, default))!.Tasks, task => task.Id == consumer.Id);
        Assert.Null(retained.TriggerRunKey);
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(node.NextFireAtMs!.Value));
        Assert.Equal(1, await schedules.EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(id);
        var tasks = (await store.LoadTrackAggregateAsync(id, default))!.Tasks;
        var archived = Assert.Single(tasks, task => task.Id == first.Id);
        Assert.NotNull(archived.ArchivedAtMs);
        Assert.Equal(TaskRunStatus.Done, archived.Status);
        Assert.Equal(first.SessionId, archived.SessionId);
        Assert.Equal(TaskRunStatus.Done, Assert.Single(tasks, task => task.TaskKey == "pulse").Status);
        var secondConsumer = Assert.Single(tasks, task => task.TaskKey == "consumer");
        Assert.Equal(consumer.Id, secondConsumer.Id);
        Assert.Equal(TaskRunStatus.Done, secondConsumer.Status);
        Assert.Equal(2, secondConsumer.RunCount);
        Assert.Collection(host.AgentRuntime.ExecutedTurns.Where(turn =>
            turn.Input.Prompt.StartsWith("Check ", StringComparison.Ordinal) || turn.Input.Prompt.Contains("Consume pulse", StringComparison.Ordinal)),
            turn => Assert.Contains("Check old value", turn.Input.Prompt),
            turn => Assert.Contains("Consume pulse", turn.Input.Prompt),
            turn => Assert.Contains("Check new value", turn.Input.Prompt),
            turn => Assert.Contains("Consume pulse", turn.Input.Prompt));
        Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt.Contains("${inputs."));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TemplateCannotClaimIndependentFlowId(bool archived, bool preUpgrade)
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        await FlowFixture.AddAsync(kernel, id, new FlowNodeSpecDto("pulse",
            new("run", Prompt: "Independent"), When: new(At: "2099-01-01T00:00:00Z")));
        var store = host.Services.GetRequiredService<ITrackStore>();
        if (archived)
        {
            await kernel.CancelTaskAsync(id, null, "pulse");
            var canceled = await kernel.GetTrackFlowAsync(id);
            Assert.True((await kernel.CommitTrackFlowAsync(id, false, canceled.FlowRev, new InteropPrincipal.Human(),
                content: canceled.Spec.GetRawText())).Ok);
            Assert.NotNull(Assert.Single(await kernel.ListTasksAsync(id)).ArchivedAtMs);
        }
        if (preUpgrade)
        {
            var row = Assert.Single((await store.LoadTrackAggregateAsync(id, default))!.Tasks);
            await kernel.Actors.RemoveAndDisposeAsync(id);
            await store.UpsertTaskAsync(row with { Context = JsonSerializer.SerializeToElement(new { flowNode = true }) },
                null, default);
        }
        var original = Assert.Single((await store.LoadTrackAggregateAsync(id, default))!.Tasks);
        var before = await kernel.GetTrackFlowAsync(id);
        var principal = await kernel.OpenMcpCallerAsync(leadId);
        var error = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(
            kernel, principal, KernelMcpToolNames.TrackSet, JsonSerializer.SerializeToElement(new { source = Source })));
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
        Assert.Contains("pulse", error.Message);
        if (preUpgrade)
            Assert.Contains("unknown Flow provenance", error.Message);
        Assert.Equal(before.Spec.GetRawText(), (await kernel.GetTrackFlowAsync(id)).Spec.GetRawText());
        Assert.Null((await kernel.GetTrackAggregateAsync(id)).Track.TemplateId);
        var preserved = Assert.Single((await store.LoadTrackAggregateAsync(id, default))!.Tasks);
        Assert.Equal(original.Id, preserved.Id);
        Assert.Equal(original.ArchivedAtMs, preserved.ArchivedAtMs);
        Assert.Equal(original.Context?.GetRawText(), preserved.Context?.GetRawText());
    }

    [Fact]
    public async Task ChangedBlueprintCannotReactivateManuallyCanceledNode()
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        var principal = await kernel.OpenMcpCallerAsync(leadId);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = Source }));
        await kernel.CancelTaskAsync(id, null, "pulse");
        var canceled = await kernel.GetTrackFlowAsync(id);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = Source.Replace("Check", "Verify", StringComparison.Ordinal) }));
        var rebound = await kernel.GetTrackFlowAsync(id);
        Assert.Equal(canceled.Spec.GetRawText(), rebound.Spec.GetRawText());
        Assert.Equal(TaskRunStatus.Canceled, Assert.Single(await kernel.ListTasksAsync(id)).Status);
        Assert.Null(Assert.Single(rebound.Projection.Nodes).NextFireAtMs);
    }

    [Fact]
    public async Task ActiveTemplateWakeCannotClearOrReplaceItsOwnBinding()
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        var principal = await kernel.OpenMcpCallerAsync(leadId);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeTurn = async (input, ct) =>
        {
            if (!input.Prompt.Contains("Check first")) return;
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { canceled.TrySetResult(); }
        };
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = Source }));
        var before = await kernel.GetTrackFlowAsync(id);
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(Assert.Single(before.Projection.Nodes).NextFireAtMs!.Value));
        Assert.Equal(1, await host.Services.GetRequiredService<CronosScheduleHostedService>().EvaluateDueSchedulesOnceAsync());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var arguments in new[]
            {
                JsonSerializer.SerializeToElement(new { clear = true }),
                JsonSerializer.SerializeToElement(new { source = Source.Replace("Check", "Replace", StringComparison.Ordinal) })
            })
            {
                var error = await Assert.ThrowsAsync<KernelException>(() =>
                    KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet, arguments));
                Assert.Equal(KernelErrorCode.Conflict, error.Code);
                Assert.False(canceled.Task.IsCompleted);
                Assert.Equal(before.Spec.GetRawText(), (await kernel.GetTrackFlowAsync(id)).Spec.GetRawText());
                Assert.Equal("unified", (await kernel.GetTrackAggregateAsync(id)).Track.TemplateId);
            }
        }
        finally
        {
            using var response = await host.AuthenticatedClient.DeleteAsync($"/api/v1/tracks/{id}/template");
            response.EnsureSuccessStatusCode();
        }
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await kernel.WaitForTrackIdleAsync(id);
        Assert.Null((await kernel.GetTrackAggregateAsync(id)).Track.TemplateId);
    }

    [Fact]
    public async Task UnboundCollaboratorCannotBindTemplateThatSeedsWork()
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        var collaborator = await kernel.CreateSessionAsync(id, new(), "collaborator", new InteropPrincipal.Human(), default);
        var principal = await kernel.OpenMcpCallerAsync(collaborator.Receipt.Session.Id);
        var before = await kernel.GetTrackFlowAsync(id);
        const string source = """
            export default {id:'immediate', title:'Immediate',
              flow:[{id:'work', do:{kind:'run', prompt:'Start work'}}]};
            """;
        var arguments = JsonSerializer.SerializeToElement(new { source });
        var error = await Assert.ThrowsAsync<KernelException>(() =>
            KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet, arguments));
        Assert.Equal(KernelErrorCode.Forbidden, error.Code);
        var generation = await kernel.GuardMcpToolCallAsync(await kernel.OpenMcpCallerAsync(leadId), "perga", KernelMcpToolNames.TrackSet, arguments);
        await kernel.PatchTrackAsync(id, new(LeadSessionId: principal.SessionId));
        var staleLead = await Assert.ThrowsAsync<KernelException>(() =>
            kernel.SetTrackTemplateAsync(id, leadId, null, source, generation));
        Assert.Equal(KernelErrorCode.Forbidden, staleLead.Code);
        var after = await kernel.GetTrackFlowAsync(id);
        Assert.Equal(before.FlowRev, after.FlowRev);
        Assert.Empty(await kernel.ListTasksAsync(id));
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("replace")]
    [InlineData("http")]
    public async Task OutgoingTemplateClockIsCanceledWithoutChangingIndependentNodesOrDraft(string operation)
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        var principal = await kernel.OpenMcpCallerAsync(leadId);
        var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../templates/okr-loop"));
        kernel.Templates.RegisterTemplate(Perga.PolicyVm.TemplateBundle.PackDirectory(package));
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { templateId = "okr-loop" }));
        await FlowFixture.AddAsync(kernel, id, new FlowNodeSpecDto("independent",
            new("run", Prompt: "Independent"), Source: "agent", When: new(At: "2099-01-01T00:00:00Z")));
        var before = await kernel.GetTrackFlowAsync(id);
        var outgoing = Assert.Single(before.Projection.Nodes, node => node.Id == "daily-ci-check");
        const string draft = "local draft";
        await File.WriteAllTextAsync(before.FilePath!, draft);
        switch (operation)
        {
            case "clear":
                await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
                    JsonSerializer.SerializeToElement(new { clear = true }));
                break;
            case "replace":
                await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
                    JsonSerializer.SerializeToElement(new { source = Source }));
                break;
            case "http":
                using (var response = await host.AuthenticatedClient.DeleteAsync($"/api/v1/tracks/{id}/template"))
                    response.EnsureSuccessStatusCode();
                break;
        }
        var after = await kernel.GetTrackFlowAsync(id);
        Assert.Equal(before.FlowRev + 1, after.FlowRev);
        var outgoingNode = Assert.Single(after.Spec.GetProperty("nodes").EnumerateArray(),
            node => node.GetProperty("id").GetString() == outgoing.Id);
        Assert.True(outgoingNode.GetProperty("canceled").GetBoolean());
        Assert.Equal("agent", Assert.Single(after.Projection.Nodes, node => node.Id == "independent").Source);
        Assert.Equal(draft, await File.ReadAllTextAsync(before.FilePath!));
        var clock = (await host.Services.GetRequiredService<ITrackScheduleStore>().GetAsync(id, "flow:daily-ci-check", default))!;
        Assert.Equal(TrackScheduleStates.Canceled, clock.State);
        Assert.Null(clock.NextFireAtMs);
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(outgoing.NextFireAtMs!.Value));
        await host.Services.GetRequiredService<CronosScheduleHostedService>().EvaluateDueSchedulesOnceAsync();
        await kernel.WaitForTrackIdleAsync(id);
        Assert.DoesNotContain(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt.Contains("过去 24 小时 CI 指标"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReapplyingTemplateRefreshesViewAndCalendarWithoutResettingClock(bool exhausted)
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        var principal = await kernel.OpenMcpCallerAsync(leadId);
        var source = exhausted ? Source.Replace("tz:'UTC'", "tz:'UTC',maxOccurrences:1", StringComparison.Ordinal) : Source;
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source }));
        var initial = await kernel.GetTrackFlowAsync(id);
        var first = Assert.Single(initial.Projection.Nodes).NextFireAtMs!.Value;
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(first));
        Assert.Equal(1, await host.Services.GetRequiredService<CronosScheduleHostedService>().EvaluateDueSchedulesOnceAsync());
        await kernel.WaitForTrackIdleAsync(id);
        var store = host.Services.GetRequiredService<ITrackScheduleStore>();
        var before = (await store.GetAsync(id, "flow:pulse", default))!;
        Assert.Equal(1, before.FiredCount);
        Assert.Equal("Pulse first", before.Title);
        Assert.Equal(exhausted ? TrackScheduleStates.Done : TrackScheduleStates.Active, before.State);

        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source, inputs = new { target = "second" } }));
        var views = await host.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/tracks/{id}/views");
        Assert.Equal(initial.FlowRev, views.GetProperty("flowRev").GetInt64());
        var node = Assert.Single(views.GetProperty("flow").GetProperty("nodes").EnumerateArray());
        Assert.Equal("Pulse second", node.GetProperty("title").GetString());
        var after = (await store.GetAsync(id, "flow:pulse", default))!;
        Assert.Equal(before.LastFiredAtMs, after.LastFiredAtMs);
        Assert.Equal(before.NextFireAtMs, after.NextFireAtMs);
        Assert.Equal(before.FiredCount, after.FiredCount);
        Assert.Equal(before.State, after.State);
        Assert.Equal("Check second", after.Prompt);
        var day = host.TimeProvider.GetUtcNow().ToString("yyyy-MM-dd");
        var calendar = await host.AuthenticatedClient.GetFromJsonAsync<JsonElement>($"/api/v1/today/calendar?start={day}&end={day}");
        var occurrences = calendar.GetProperty("schedules").EnumerateArray()
            .Where(item => item.GetProperty("id").GetString() == "flow:pulse").ToArray();
        Assert.NotEmpty(occurrences);
        Assert.All(occurrences, occurrence => Assert.Equal("Pulse second", occurrence.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task LeadCannotCancelItsActiveWakeButCanCancelItsSettledRecurringNode()
    {
        await using var host = Host();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (id, leadId) = await host.CreateTrackAsync();
        var principal = await kernel.OpenMcpCallerAsync(leadId);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new { source = Source }));
        var flow = await kernel.GetTrackFlowAsync(id);
        host.TimeProvider.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(Assert.Single(flow.Projection.Nodes).NextFireAtMs!.Value));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeTurn = async (input, ct) =>
        {
            if (input.Prompt.Contains("Check first"))
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        Assert.Equal(1, await host.Services.GetRequiredService<CronosScheduleHostedService>().EvaluateDueSchedulesOnceAsync());
        var args = JsonSerializer.SerializeToElement(new { taskKey = "pulse" });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<KernelException>(() =>
                KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TaskCancel, args));
            Assert.Equal(KernelErrorCode.Forbidden, error.Code);
        }
        finally { release.TrySetResult(); }
        await kernel.WaitForTrackIdleAsync(id);
        var settled = Assert.Single(await kernel.ListTasksAsync(id));
        Assert.Equal(TaskRunStatus.Done, settled.Status);
        Assert.Equal(leadId, settled.SessionId);
        var followUpStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFollowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.BeforeTurn = async (_, ct) =>
        {
            followUpStarted.TrySetResult();
            await finishFollowUp.Task.WaitAsync(ct);
        };
        var human = new InteropPrincipal.Human();
        await kernel.SendSessionMessageAsync(leadId, new(Content: "Discuss an unrelated topic", RequestKey: "follow-up"), human, default);
        try
        {
            await followUpStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await KernelMcpDispatcher.ExecuteGuardedAsync(kernel, principal, KernelMcpToolNames.TaskCancel, args);
        }
        finally { finishFollowUp.TrySetResult(); }
        await kernel.WaitForTrackIdleAsync(id);
        Assert.Equal("completed", (await kernel.GetSessionItemsAsync(leadId, new(), human, default)).LastTurn!.Status);
        Assert.Equal(TrackScheduleStates.Canceled,
            (await host.Services.GetRequiredService<ITrackScheduleStore>().GetAsync(id, "flow:pulse", default))!.State);
    }

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
                using var patch = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{id}/template/inputs", new { target = "second" });
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
                Assert.Equal("untouched", persisted.Data.GetProperty("business").GetString());
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
        var principal = await kernel.OpenMcpCallerAsync(leadId);
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
        using var badCreate = await host.AuthenticatedClient.PostAsJsonAsync("/api/v1/tracks",new {areaId=area.Id,templateId="unified",templateInputs=new {target=42}});
        Assert.Equal(HttpStatusCode.BadRequest,badCreate.StatusCode);
        var created = await kernel.CreateTrackAsync(new(area.Id));
        var id = created.Track.Id;
        var principal = await kernel.OpenMcpCallerAsync(created.Track.LeadSessionId!);
        var badBind = await Assert.ThrowsAsync<KernelException>(()=>KernelMcpDispatcher.ExecuteGuardedAsync(kernel,principal,KernelMcpToolNames.TrackSet,
            JsonSerializer.SerializeToElement(new {source=Source,inputs=new {target=false}})));
        Assert.Equal(KernelErrorCode.InvalidArgument,badBind.Code);
        await KernelMcpDispatcher.ExecuteGuardedAsync(kernel,principal,KernelMcpToolNames.TrackSet,JsonSerializer.SerializeToElement(new {source=Source}));
        var badMcpPatch = await Assert.ThrowsAsync<KernelException>(()=>KernelMcpDispatcher.ExecuteGuardedAsync(kernel,principal,KernelMcpToolNames.TemplateUpdateInputs,
            JsonSerializer.SerializeToElement(new {inputs=new {target=1}})));
        Assert.Equal(KernelErrorCode.InvalidArgument,badMcpPatch.Code);
        using var badPatch = await host.AuthenticatedClient.PatchAsJsonAsync($"/api/v1/tracks/{id}/template/inputs",new {target=1});
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
        using var cleared = await host.AuthenticatedClient.PatchAsJsonAsync(url, new Dictionary<string, object?> { ["optional"] = null, ["defaulted"] = null });
        cleared.EnsureSuccessStatusCode();
        var inputs = (await kernel.GetTrackAggregateAsync(id)).Track.TemplateInputs;
        Assert.False(inputs.TryGetProperty("optional", out _));
        Assert.False(inputs.TryGetProperty("defaulted", out _));
        Assert.Equal("keep", inputs.GetProperty("required").GetString());
        using var rejected = await host.AuthenticatedClient.PatchAsJsonAsync(url, new Dictionary<string, object?> { ["required"] = null });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(inputs.GetRawText(), (await kernel.GetTrackAggregateAsync(id)).Track.TemplateInputs.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearTemplatePersistsRequestedDataActionAcrossHostRestart(bool resetData)
    {
        var directory = Directory.CreateTempSubdirectory("perga-template-clear-");
        try
        {
            string id;
            TrackDto original;
            await using (var host = Host(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var created = await host.CreateTrackAsync();
                id = created.TrackId;
                var token = (await kernel.OpenMcpCallerAsync(created.SessionId)).Token;
                var (bound, bindError) = await McpKernelAndVcsIntegrationTests.CallToolAsync(host, "/mcp/kernel", token,
                    KernelMcpToolNames.TrackSet, new { source = Source }, default);
                Assert.False(bindError, bound);
                original = (await kernel.GetTrackAggregateAsync(id)).Track;
                Assert.Equal("untouched", original.Data.GetProperty("business").GetString());

                var (previewText, previewError) = await McpKernelAndVcsIntegrationTests.CallToolAsync(host, "/mcp/kernel", token,
                    KernelMcpToolNames.TrackSet, new { clear = true, resetData, dryRun = true }, default);
                Assert.False(previewError, previewText);
                using var preview = JsonDocument.Parse(previewText);
                Assert.Equal(resetData ? "reset" : "preserve",
                    preview.RootElement.GetProperty("plan").GetProperty("dataAction").GetString());
                var afterPreview = (await kernel.GetTrackAggregateAsync(id)).Track;
                Assert.Equal(original.TemplateId, afterPreview.TemplateId);
                Assert.Equal(original.DataRev, afterPreview.DataRev);

                object clearArgs = resetData ? new { clear = true, resetData = true } : new { clear = true };
                var (cleared, clearError) = await McpKernelAndVcsIntegrationTests.CallToolAsync(host, "/mcp/kernel", token,
                    KernelMcpToolNames.TrackSet, clearArgs, default);
                Assert.False(clearError, cleared);
                if (resetData)
                {
                    var (retry, retryError) = await McpKernelAndVcsIntegrationTests.CallToolAsync(host, "/mcp/kernel", token,
                        KernelMcpToolNames.TrackSet, clearArgs, default);
                    Assert.False(retryError, retry);
                }
            }

            await using var restarted = Host(directory.FullName);
            var track = (await restarted.Services.GetRequiredService<IPergaKernel>().GetTrackAggregateAsync(id)).Track;
            Assert.Null(track.TemplateId);
            Assert.Equal(original.DataRev + (resetData ? 1 : 0), track.DataRev);
            if (resetData)
                Assert.Empty(track.Data.EnumerateObject());
            else
                Assert.True(JsonElement.DeepEquals(original.Data, track.Data));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task TrackSetUsesLiveHostPluginRegistrationForWarnings()
    {
        await using var host = Host();
        var (_, leadId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var token = (await kernel.OpenMcpCallerAsync(leadId)).Token;
        host.Services.GetRequiredService<McpPluginRouter>().RegisterPlugin(
            new McpPluginDefinition("known-test-plugin", "stdio", Command: "unused"));
        const string source = """
            export default { id: "plugins-test", title: "Plugin warnings",
              plugins: ["perga", "vcs", "known-test-plugin", "unregistered-test-plugin"] };
            """;

        foreach (var dryRun in new[] { true, false })
        {
            var (response, error) = await McpKernelAndVcsIntegrationTests.CallToolAsync(host, "/mcp/kernel", token,
                KernelMcpToolNames.TrackSet, new { source, dryRun }, default);
            Assert.False(error, response);
            using var parsed = JsonDocument.Parse(response);
            Assert.Equal("Template plugin 'unregistered-test-plugin' is not configured on this host.",
                Assert.Single(parsed.RootElement.GetProperty("warnings").EnumerateArray()).GetString());
        }
    }

    private static PergaHostTestCluster Host(string? directory=null) => new(explicitDataDir:directory,configureHostOptions:o=>
    {
        o.EnableScheduleService=false; o.EnableMaintenanceService=false;
    });
}
