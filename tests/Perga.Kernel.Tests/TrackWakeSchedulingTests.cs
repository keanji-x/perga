using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.PolicyVm;

namespace Perga.Kernel.Tests;

public sealed class TrackWakeSchedulingTests
{
    [Fact]
    public async Task OwnerDispatchDoesNotWakeLeadButHumanReportEditDoes()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        await ctx.Kernel.DispatchWorkerAsync(id, new("Inspect report"), "report-wake", null, default);
        Assert.DoesNotContain(await ctx.Store.TrackWakes.ListAsync(id, default), wake => wake.Kind == "report_edited");

        var report = await ctx.Kernel.ReadReportAsync(id);
        var block = report.Blocks[0];
        await ctx.Kernel.UpdateReportBlockByHumanAsync(id, block.Id,
            new HumanBlockUpdateRequest(report.DocRev, block.Rev, Markdown: "Human report edit"));
        Assert.Equal("pending", Assert.Single(await ctx.Store.TrackWakes.ListAsync(id, default), wake => wake.Kind == "report_edited").State);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "", false)]
    [InlineData(null, " \t\n", false)]
    [InlineData("", null, false)]
    [InlineData(" \t", "", false)]
    [InlineData("", " \n", true)]
    [InlineData("static prompt", "", false)]
    [InlineData("", "dynamic prompt", false)]
    public async Task BlankScheduleSegmentsSettleWithoutEmptyTurns(string? staticPrompt, string? output, bool withMail)
    {
        await using var ctx = new KernelTestContext();
        var manifest = ctx.PolicyVm.Compile("export default defineTemplate({id:'schedule-test',title:'Schedule'});").Manifest;
        ctx.PolicyVm.CustomManifestFactory = _ => manifest with
        { Schedules = [new ScheduleSpec("daily", "0 9 * * *", "UTC", staticPrompt)] };
        ctx.PolicyVm.OnBuildScheduleWake = (_, _, _, state) => new(state, output);
        ctx.Kernel.Templates.RegisterTemplate("export default defineTemplate({id:'schedule-test',title:'Schedule'});");
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync(templateId: "schedule-test");
        var id = snapshot.Track.Id;
        await ctx.Store.TrackWakes.EnqueueAsync(new(id, "schedule", "schedule:daily", TestJson.FromObject(new { scheduleId = "daily" })), default);
        if (withMail)
            await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", "mail:test", "mail prompt", "system_mail"), default);
        await ctx.Kernel.Actors.GetOrActivate(id).PokeLeadAsync();
        await ctx.Kernel.WaitForTrackIdleAsync(id);
        var expected = !string.IsNullOrWhiteSpace(output) ? output : staticPrompt ?? output ?? "Scheduled wake 'daily' triggered.";
        var suppressed = string.IsNullOrWhiteSpace(expected);
        var turns = ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!);
        if (suppressed && !withMail)
            Assert.Empty(turns);
        else
            Assert.Equal(withMail ? "mail prompt" : expected, Assert.Single(turns).Input.GetProperty("prompt").GetString());
        if (withMail)
        {
            Assert.Equal("wake", Assert.Single(turns).TriggerKind);
            var user = Assert.Single(await ctx.Store.ListSessionItemsAsync(snapshot.Track.LeadSessionId!), i => i.Kind == "user");
            Assert.Equal("system_mail", user.Payload.GetProperty("presentation").GetString());
        }
        var wakes = await ctx.Store.TrackWakes.ListAsync(id, default);
        Assert.Equal(suppressed ? ("dropped", "canceled") : ("done", "delivered"), (wakes[0].State, wakes[0].Reason));
        if (suppressed) Assert.Null(wakes[0].TurnId);
        await ctx.Kernel.Actors.GetOrActivate(id).PokeLeadAsync();
        Assert.Equal(suppressed && !withMail ? 0 : 1, ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!).Count);
    }

    [Fact]
    public async Task WakeSetupConflictStillKeepsFailedTurnIdentity()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        ctx.AgentRuntime.OnBeforeOpenSession = _ => throw KernelException.Conflict("Setup refused");
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", "mail:test", "notice"), default);
        var ex = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.Actors.GetOrActivate(id).PokeLeadAsync());
        Assert.Equal(KernelErrorCode.Conflict, ex.Code);
        var turn = Assert.Single(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));
        Assert.Equal("failed", turn.Status);
        Assert.Equal(turn.Id, Assert.Single(await ctx.Store.TrackWakes.ListAsync(id, default)).TurnId);
    }

    [Fact]
    public async Task NonWakeSetupConflictDoesNotCreateGhostTurn()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        ctx.AgentRuntime.OnBeforeOpenSession = spec =>
        {
            if (spec.ConfigureWorker) throw KernelException.Conflict("Setup refused");
        };
        var dispatch = await ctx.Kernel.DispatchWorkerAsync(snapshot.Track.Id, new("Inspect report"), "worker", null, default);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var worker = Assert.Single(await ctx.Kernel.ListSessionsAsync(snapshot.Track.Id), s => s.BoundTaskId == dispatch.Task.Id);
        Assert.Empty(ctx.Store.GetTurnsForSession(worker.Id));
        Assert.Empty(await ctx.Store.ListSessionItemsAsync(worker.Id));
    }

    [Fact]
    public async Task InMemoryPromotionPreservesSessionAndSourceIdentityForSameIds()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var lead = Assert.Single(snapshot.Sessions);
        var other = await ctx.Kernel.CreateSubagentSessionAsync(snapshot.Track.Id, new CreateSessionRequest());
        var first = new SessionMessage("first", lead.Id, null, "owner:installation", "first", "hash",
            null, null, "First", "queued", 1, "queue");
        foreach (var message in new[] { first, first with { Id = "second", RequestKey = "second", Content = "Second" },
                     first with { SessionId = other.Id }, first with { AttemptId = "attempt", Content = "Worker source" } })
            await ctx.Store.SessionMessages.InsertAsync(message, default);
        var input = (await ctx.Store.SessionInputs.PromoteQueuedBatchAsync(lead with { Status = "queued" }, first,
            new(null, null, snapshot.Track.Id, "session.updated", TestJson.FromObject(lead), 1), default))!;
        Assert.Equal("First\n\nSecond", input.Content);
        Assert.Null((await ctx.Store.SessionMessages.GetAsync(other.Id, false, "first", default))!.TurnId);
        Assert.Null((await ctx.Store.SessionMessages.GetAsync(lead.Id, true, "first", default))!.TurnId);
        Assert.NotNull(await ctx.Store.SessionInputs.FindByIdAsync(lead.Id, "first", default));
        Assert.Null(await ctx.Store.SessionInputs.FindByIdAsync(other.Id, "first", default));
        var otherInput = await ctx.Store.SessionInputs.PromoteQueuedBatchAsync(other with { Status = "queued" }, first with { SessionId = other.Id },
            new(null, null, snapshot.Track.Id, "session.updated", TestJson.FromObject(other), 1), default);
        Assert.NotNull(otherInput);
        Assert.NotEqual(input.Receipt.TurnId, otherInput.Receipt.TurnId);
    }

    [Fact]
    public async Task LeadSwitchRoutesPendingDynamicWakesToNewLead()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        var next = await ctx.Kernel.CreateSubagentSessionAsync(id, new CreateSessionRequest(Title: "Next Lead"));
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", "mail:test", "notice"), default);
        await ctx.Kernel.PatchTrackAsync(id, new PatchTrackRequest(LeadSessionId: next.Id));
        await ctx.Kernel.WaitForTrackIdleAsync(id);
        Assert.Single(ctx.Store.GetTurnsForSession(next.Id));
        Assert.Empty(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));
        Assert.Equal("done", Assert.Single(await ctx.Store.TrackWakes.ListAsync(id, default)).State);
    }

    [Theory]
    [InlineData("mail", "delivering", null)]
    [InlineData("schedule", "dropped", "runtime_failed")]
    public async Task UndeliveredFailureReturnsIdleWithoutRetrying(string kind, string state, string? reason)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        ctx.AgentRuntime.TurnScript = Fail;
        var payload = kind == "schedule" ? TestJson.FromObject(new { scheduleId = "daily" }) : TestJson.FromObject(new { prompt = "notice" });
        await ctx.Store.TrackWakes.EnqueueAsync(new(id, kind, null, payload), default);
        await ctx.Kernel.Actors.GetOrActivate(id).PokeLeadAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ctx.Kernel.WaitForTrackIdleAsync(id, deadline.Token);
        var wake = Assert.Single(await ctx.Store.TrackWakes.ListAsync(id, default));
        Assert.Equal((state, reason), (wake.State, wake.Reason));
        Assert.NotNull(wake.TurnId);
        Assert.Equal(1, wake.Attempts);
        Assert.Equal("idle", (await ctx.Store.LoadTrackAggregateAsync(id, default))!.Sessions.Single(s => s.IsLead).Status);
        await ctx.Kernel.Actors.GetOrActivate(id).PokeLeadAsync();
        Assert.Single(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));

        static async IAsyncEnumerable<UnifiedAgentEvent> Fail(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new UnifiedAgentEvent.TurnError("runtime", "failed", false);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreStreamFailureDropsEphemeralAndLeavesCriticalDormant(bool duringPreparation)
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate("export default defineTemplate({id:'schedule-test',title:'Schedule'});");
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync(templateId: "schedule-test");
        var id = snapshot.Track.Id;
        if (duringPreparation)
            ctx.PolicyVm.OnBuildScheduleWake = (_, _, _, _) => throw new InvalidOperationException("broken schedule JS");
        else
            ctx.AgentRuntime.OnBeforeOpenSession = _ => throw new InvalidOperationException("provider open failed");
        await ctx.Store.TrackWakes.EnqueueAsync(new(id, "schedule", "schedule:daily", TestJson.FromObject(new { scheduleId = "daily" })), default);
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", "mail:test", "mail"), default);
        var actor = ctx.Kernel.Actors.GetOrActivate(id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.PokeLeadAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ctx.Kernel.WaitForTrackIdleAsync(id, deadline.Token);
        var rows = await ctx.Store.TrackWakes.ListAsync(id, default);
        Assert.Equal(("dropped", "runtime_failed"), (rows[0].State, rows[0].Reason));
        Assert.Equal("delivering", rows[1].State);
        Assert.Equal("idle", (await ctx.Store.LoadTrackAggregateAsync(id, default))!.Sessions.Single(s => s.IsLead).Status);
        await actor.PokeLeadAsync();
        Assert.All(await ctx.Store.TrackWakes.ListAsync(id, default), w => Assert.Equal(1, w.Attempts));
    }

    [Fact]
    public async Task ScheduleEvaluatesOnlyAtClaimAndBatchesInOrdinalOrder()
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate("export default defineTemplate({id:'schedule-test',title:'Schedule'});");
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync(templateId: "schedule-test");
        var id = snapshot.Track.Id;
        var actor = ctx.Kernel.Actors.GetOrActivate(id);
        var evaluations = 0;
        ctx.PolicyVm.OnBuildScheduleWake = (_, _, _, state) => { evaluations++; return new PolicyEvalResult<string?>(state, "schedule evaluated"); };
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", "mail:test", "first notice"), default);
        await ctx.Store.TrackWakes.EnqueueAsync(new(id, "schedule", "schedule:daily", TestJson.FromObject(new { scheduleId = "daily" })), default);
        Assert.Equal(0, evaluations);
        await actor.PokeLeadAsync();
        Assert.Equal(1, evaluations);
        var turn = Assert.Single(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));
        Assert.Equal("system", turn.TriggerKind);
        Assert.Equal("first notice\n\nschedule evaluated", turn.Input.GetProperty("prompt").GetString());
        Assert.All(await ctx.Store.TrackWakes.ListAsync(id, default), w => Assert.Equal(("done", "delivered"), (w.State, w.Reason)));
        await actor.PokeLeadAsync();
        Assert.Equal(1, evaluations);
    }
}
