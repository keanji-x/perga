using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.PolicyVm;

namespace Perga.Kernel.Tests;

public sealed class TrackWakeSchedulingTests
{
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
        ctx.PolicyVm.OnScheduleFired = (_, _, _, state) => new(state, output);
        ctx.Kernel.Templates.RegisterTemplate("export default defineTemplate({id:'schedule-test',title:'Schedule'});");
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync(templateId: "schedule-test");
        var id = snapshot.Track.Id;
        await ctx.Store.TrackWakes.EnqueueAsync(new(id, "schedule", "track_schedule:daily", TestJson.FromObject(new { scheduleId = "daily", sourceKind = "template", prompt = staticPrompt ?? "stored prompt" })), default);
        if (withMail)
            await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", "mail:test", "mail prompt", "system_mail"), default);
        await ctx.Kernel.Actors.GetOrActivate(id).PokePendingWakesAsync();
        await ctx.Kernel.WaitForTrackIdleAsync(id);
        var expected = output ?? staticPrompt ?? "stored prompt";
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
        await ctx.Kernel.Actors.GetOrActivate(id).PokePendingWakesAsync();
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
        var ex = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.Actors.GetOrActivate(id).PokePendingWakesAsync());
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
            if (spec.SystemPrompt?.Contains("BoundTaskId=", StringComparison.Ordinal) == true) throw KernelException.Conflict("Setup refused");
        };
        await FlowFixture.AddAsync(ctx.Kernel, snapshot.Track.Id, FlowFixture.Worker("worker", "Inspect report"));
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var worker = Assert.Single(await ctx.Kernel.ListSessionsAsync(snapshot.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default), s => !s.Session.IsLead).Session;
        Assert.Empty(ctx.Store.GetTurnsForSession(worker.Id));
        Assert.Empty(await ctx.Store.ListSessionItemsAsync(worker.Id));
    }

    [Fact]
    public async Task InMemoryPromotionPreservesSessionIdentityForSameIds()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var lead = Assert.Single(snapshot.Sessions);
        var other = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new CreateSessionRequest(), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default)).Receipt.Session;
        var first = new SessionMessage("first", lead.Id, "owner:installation", "first", "hash",
            null, null, "First", "queued", 1, "queue");
        foreach (var message in new[] { first, first with { Id = "second", RequestKey = "second", Content = "Second" },
                     first with { SessionId = other.Id } })
            await ctx.Store.SessionMessages.InsertAsync(message, default);
        var input = (await ctx.Store.SessionInputs.PromoteQueuedBatchAsync(lead with { Status = "queued" }, first,
            new(null, null, snapshot.Track.Id, "session.updated", TestJson.FromObject(lead), 1), default))!;
        Assert.Equal("First\n\nSecond", input.Content);
        Assert.Null((await ctx.Store.SessionMessages.GetAsync(other.Id, "first", default))!.TurnId);
        Assert.NotNull(await ctx.Store.SessionInputs.FindByIdAsync(lead.Id, "first", default));
        Assert.Null(await ctx.Store.SessionInputs.FindByIdAsync(other.Id, "first", default));
        var otherInput = await ctx.Store.SessionInputs.PromoteQueuedBatchAsync(other with { Status = "queued" }, first with { SessionId = other.Id },
            new(null, null, snapshot.Track.Id, "session.updated", TestJson.FromObject(other), 1), default);
        Assert.NotNull(otherInput);
        Assert.NotEqual(input.Receipt.TurnId, otherInput.Receipt.TurnId);
    }

    [Fact]
    public async Task InMemoryWakeTransitionsStampUpdatedAt()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        ctx.Store.WakeClock = ctx.Time;
        var wakes = ctx.Store.TrackWakes;
        foreach (var key in new[] { "done", "recovered", "canceled" })
            await wakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", $"mail:{key}", key), default);
        var created = (await wakes.ListAsync(id, default)).Where(w => w.State == TrackWakeStates.Pending).ToArray();
        Assert.Equal(3, created.Length);
        var createdAt = created[0].CreatedAtMs;

        async Task<TrackWake> Stamped(TrackWake wake, Func<Task> transition)
        {
            ctx.Time.Advance(TimeSpan.FromSeconds(1));
            await transition();
            var stored = Assert.Single(await wakes.ListAsync(id, default), w => w.Id == wake.Id);
            Assert.Equal(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds(), stored.UpdatedAtMs);
            Assert.Equal(createdAt, stored.CreatedAtMs);
            return stored;
        }

        var canceled = await Stamped(created[2], () => wakes.CancelPendingWakeAsync(id, created[2].Id, default));
        Assert.Equal(TrackWakeStates.Dropped, canceled.State);
        IReadOnlyList<TrackWake> claimed = [];
        var done = await Stamped(created[0], async () => claimed = await wakes.ClaimBatchAsync(id, snapshot.Track.LeadSessionId!, 32, default));
        Assert.Equal(TrackWakeStates.Delivering, done.State);
        Assert.Equal(created[1].UpdatedAtMs + 2000, Assert.Single(claimed, w => w.Id == created[1].Id).UpdatedAtMs);
        done = await Stamped(created[0], () => wakes.LinkTurnAsync([done], "trn_linked", default));
        Assert.Equal("trn_linked", done.TurnId);
        done = await Stamped(created[0], () => wakes.MarkDoneAsync([done], TrackWakeReasons.Delivered, "trn_linked", default));
        Assert.Equal(TrackWakeStates.Done, done.State);
        var recovered = await Stamped(created[1], () => wakes.RecoverOnBootAsync(default));
        Assert.NotEqual(TrackWakeStates.Delivering, recovered.State);
    }

    [Fact]
    public async Task LeadSwitchRoutesPendingDynamicWakesToNewLead()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.Id;
        var next = (await ctx.Kernel.CreateSessionAsync(id, new CreateSessionRequest(Title: "Next Lead"), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default)).Receipt.Session;
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
        var payload = kind == "schedule" ? TestJson.FromObject(new { scheduleId = "daily", sourceKind = "user", prompt = "notice" }) : TestJson.FromObject(new { prompt = "notice" });
        await ctx.Store.TrackWakes.EnqueueAsync(new(id, kind, null, payload), default);
        await ctx.Kernel.Actors.GetOrActivate(id).PokePendingWakesAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ctx.Kernel.WaitForTrackIdleAsync(id, deadline.Token);
        var wake = Assert.Single(await ctx.Store.TrackWakes.ListAsync(id, default));
        Assert.Equal((state, reason), (wake.State, wake.Reason));
        Assert.NotNull(wake.TurnId);
        Assert.Equal(1, wake.Attempts);
        Assert.Equal("idle", (await ctx.Store.LoadTrackAggregateAsync(id, default))!.Sessions.Single(s => s.IsLead).Status);
        await ctx.Kernel.Actors.GetOrActivate(id).PokePendingWakesAsync();
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
        if (!duringPreparation)
            ctx.AgentRuntime.OnBeforeOpenSession = _ => throw new InvalidOperationException("provider open failed");
        await ctx.Store.TrackWakes.EnqueueAsync(new(id, "schedule", "track_schedule:daily", TestJson.FromObject(new { scheduleId = "daily", sourceKind = "template", prompt = duringPreparation ? null : "stored prompt" })), default);
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", "mail:test", "mail"), default);
        var actor = ctx.Kernel.Actors.GetOrActivate(id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => actor.PokePendingWakesAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ctx.Kernel.WaitForTrackIdleAsync(id, deadline.Token);
        var rows = await ctx.Store.TrackWakes.ListAsync(id, default);
        Assert.Equal(("dropped", "runtime_failed"), (rows[0].State, rows[0].Reason));
        Assert.Equal("delivering", rows[1].State);
        Assert.Equal("idle", (await ctx.Store.LoadTrackAggregateAsync(id, default))!.Sessions.Single(s => s.IsLead).Status);
        await actor.PokePendingWakesAsync();
        Assert.All(await ctx.Store.TrackWakes.ListAsync(id, default), w => Assert.Equal(1, w.Attempts));
    }

    [Fact]
    public async Task ThrowingScheduleHookFallsBackAndDeliversCoBatchedMailWithoutChangingState()
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate("export default defineTemplate({id:'schedule-test',title:'Schedule'});");
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync(templateId: "schedule-test");
        var id = snapshot.Track.Id;
        var state = snapshot.Track.TemplateState.GetRawText();
        ctx.PolicyVm.OnScheduleFired = (_, _, _, _) => throw new InvalidOperationException("broken schedule JS");
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.DynamicSchedule(id, "daily", "stored prompt", "template"), default);
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", "mail:test", "mail prompt"), default);
        await ctx.Kernel.Actors.GetOrActivate(id).PokePendingWakesAsync();
        await ctx.Kernel.WaitForTrackIdleAsync(id);
        Assert.Equal("stored prompt\n\nmail prompt", Assert.Single(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!))
            .Input.GetProperty("prompt").GetString());
        Assert.All(await ctx.Store.TrackWakes.ListAsync(id, default), w => Assert.Equal(("done", "delivered"), (w.State, w.Reason)));
        Assert.Equal(state, (await ctx.Kernel.GetTrackAggregateAsync(id)).Track.TemplateState.GetRawText());
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
        ctx.PolicyVm.OnScheduleFired = (_, _, _, state) => { evaluations++; return new PolicyEvalResult<string?>(state, "schedule evaluated"); };
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(id, "mail", "mail:test", "first notice"), default);
        await ctx.Store.TrackWakes.EnqueueAsync(new(id, "schedule", "track_schedule:daily", TestJson.FromObject(new { scheduleId = "daily", sourceKind = "template", prompt = "stored prompt" })), default);
        Assert.Equal(0, evaluations);
        await actor.PokePendingWakesAsync();
        Assert.Equal(1, evaluations);
        var turn = Assert.Single(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));
        Assert.Equal("system", turn.TriggerKind);
        Assert.Equal("first notice\n\nschedule evaluated", turn.Input.GetProperty("prompt").GetString());
        Assert.All(await ctx.Store.TrackWakes.ListAsync(id, default), w => Assert.Equal(("done", "delivered"), (w.State, w.Reason)));
        await actor.PokePendingWakesAsync();
        Assert.Equal(1, evaluations);
    }
}
