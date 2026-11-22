using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class UnifiedSessionTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    [Fact]
    public async Task InitialRunPersistenceFailureRetainsPersistedSessionReference()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var rejected = false;
        ctx.Store.OnBeforeUpsertTask = (task, session) =>
        {
            if (session is not null && task.Status == TaskRunStatus.Running)
            {
                rejected = true;
                throw new IOException("initial run persistence failed");
            }
            if (task.Status == TaskRunStatus.Failed) Assert.Null(task.SessionId);
        };
        await FlowFixture.AddAsync(ctx.Kernel, snapshot.Track.Id, FlowFixture.Worker("initial-failure", "inspect"));
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        Assert.True(rejected);
        var failed = Assert.Single((await ctx.Store.LoadTrackAggregateAsync(snapshot.Track.Id, default))!.Tasks);
        Assert.Equal(TaskRunStatus.Failed, failed.Status);
        Assert.Null(failed.SessionId);
        Assert.Equal(0, failed.RunCount);
        Assert.Equal("initial run persistence failed", failed.StatusDetail);
        Assert.NotNull(failed.FinishedAtMs);
    }

    [Theory]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, true, false, true, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, false, false, false, false)]
    public async Task OwnershipIsHumanParentLeadOrReadOnlySelf(bool human, bool parent, bool self, bool write, bool allowed)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var target = snapshot.Sessions.Single();
        var caller = target with { Id = self ? target.Id : "caller", IsLead = !parent && !self && write,
            ParentSessionId = null };
        target = target with { ParentSessionId = parent ? caller.Id : null };
        var principal = human ? Human : new InteropPrincipal.Session(target.TrackId, caller.Id, caller.IsLead, null, 0, "", null);
        Assert.Equal(allowed, SessionPolicy.CanAccess(principal, target, caller, write, activeTurn: true));
        if (allowed) SessionPolicy.RequireAccess(principal, target, caller, write, activeTurn: true);
        else Assert.Equal(KernelErrorCode.Forbidden, Assert.Throws<KernelException>(() =>
            SessionPolicy.RequireAccess(principal, target, caller, write, activeTurn: true)).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchivedParentOrLeadCannotAccessSessions(bool lead)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = snapshot.Sessions.Single() with { ArchivedAtMs = 1, IsLead = lead };
        var target = caller with { Id = "child", ParentSessionId = caller.Id, ArchivedAtMs = null, IsLead = false };
        var principal = new InteropPrincipal.Session(caller.TrackId, caller.Id, lead, null, 0, "", null);
        Assert.False(SessionPolicy.CanAccess(principal, target, caller, write: true));
        Assert.Equal(KernelErrorCode.Forbidden, Assert.Throws<KernelException>(() =>
            SessionPolicy.RequireAccess(principal, target, caller, write: true)).Code);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    [InlineData(2, false, false)]
    [InlineData(0, true, false)]
    public async Task SpawnDepthAndContractWorkersFenceChildren(int depth, bool bound, bool allowed)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var session = snapshot.Sessions.Single() with { SpawnDepth = depth };
        var principal = new InteropPrincipal.Session(session.TrackId, session.Id, true, bound ? "task_1" : null, depth, "", null);
        if (allowed) SessionPolicy.RequireCreate(principal, session, session.TrackId, callerIsTaskOwned: bound);
        else Assert.Throws<KernelException>(() => SessionPolicy.RequireCreate(principal, session, session.TrackId, callerIsTaskOwned: bound));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigVersionConflictsAndPreservesOmittedFields(bool worker)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var session = worker
            ? (await FlowFixture.StartWorkerAsync(ctx.Kernel, snapshot.Track.Id, FlowFixture.Worker("create", "inspect"))).Session
            : (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(), "create", Human, default)).Receipt.Session;
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var id = session.Id;
        var updated = await ctx.Kernel.UpdateSessionAsync(id, new(ExpectedConfigVersion: 0, Model: new("model")), Human, default);
        Assert.Equal(1, updated.ConfigVersion);
        var failure = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.UpdateSessionAsync(id,
            new(ExpectedConfigVersion: 0, ReasoningEffort: new("high")), Human, default));
        Assert.Equal(KernelErrorCode.Conflict, failure.Code);
        var renamed = await ctx.Kernel.UpdateSessionAsync(id, new(Title: "renamed"), Human, default);
        Assert.Equal(("model", 1L), (renamed.Model, renamed.ConfigVersion));
    }

    [Theory]
    [InlineData("reconnect")]
    [InlineData("reset_context")]
    public async Task WorkerRecoveryRetainsActiveTaskRun(string action)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        ctx.AgentRuntime.SupportsSessionResume = true;
        var created = await FlowFixture.StartWorkerAsync(ctx.Kernel, snapshot.Track.Id, FlowFixture.Worker("task", "inspect"));
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var id = created.Session.Id;
        var before = await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        if (action == "reconnect")
        {
            // Induce an unknown stream in its next turn.
            ctx.AgentRuntime.TurnScript = Inconclusive;
            await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "lose stream", RequestKey: "lose"), Human, default);
            await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        }
        var recovered = await ctx.Kernel.ControlSessionAsync(id, new(action), Human, default);
        var taskAfter = Assert.Single(await ctx.Kernel.ListTasksAsync(snapshot.Track.Id), t => t.Id == created.Task!.Id);
        Assert.Equal(id, taskAfter.SessionId);
        Assert.Equal(1, taskAfter.RunCount);
        Assert.Equal(TaskRunStatus.Running, taskAfter.Status);
        Assert.Equal(action == "reset_context" ? before.Session.Epoch + 1 : before.Session.Epoch, recovered.Epoch);
        Assert.Equal("idle", recovered.Status);
        if (action == "reset_context") Assert.Null(recovered.NativeThreadId);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("failed")]
    [InlineData("canceled")]
    public async Task SettledTaskSessionReleasesItsLeaseAndAcceptsFollowUp(string outcome)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var receipt = await FlowFixture.StartWorkerAsync(ctx.Kernel, snapshot.Track.Id, FlowFixture.Worker("settled", "inspect"));
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        if (outcome == "done") await ctx.Kernel.CompleteTaskDoneAsync(snapshot.Track.Id, receipt.Session.Id);
        else if (outcome == "failed") await ctx.Kernel.CompleteTaskFailAsync(snapshot.Track.Id, receipt.Session.Id, "failed");
        else await ctx.Kernel.CancelTaskAsync(snapshot.Track.Id, null, receipt.Task!.Id);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var summary = Assert.Single(await ctx.Kernel.ListSessionsAsync(snapshot.Track.Id, new(), Human, default),
            s => s.Session.Id == receipt.Session.Id);
        Assert.Null(summary.ReadonlyReason);
        Assert.Null((await ctx.Kernel.GetSessionItemsAsync(receipt.Session.Id, new(), Human, default)).ReadonlyReason);
        // Settled task sessions release their active lease and become writable again.
        var followUp = await ctx.Kernel.SendSessionMessageAsync(receipt.Session.Id,
            new(Content: "follow-up after settle", RequestKey: $"post-{outcome}"), Human, default);
        Assert.Equal("accepted", followUp.Delivery);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var replay = await ctx.Kernel.SendSessionMessageAsync(receipt.Session.Id,
            new(Content: "follow-up after settle", RequestKey: $"post-{outcome}"), Human, default);
        Assert.Equal("delivered", replay.Delivery);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> Inconclusive(ScriptableFakeAgentSession session, TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.TextDelta("partial");
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetedWakesRunOnIdleNonLeadSessions(bool archived)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(Title: "parent"), "parent", Human, default);
        var id = created.Receipt.Session.Id;
        if (archived) await ctx.Kernel.UpdateSessionAsync(id, new(Archived: true), Human, default);
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(snapshot.Track.Id, TrackWakeKinds.TaskSettled,
            "child", "child finished") with { TargetSessionId = id }, default);
        await ctx.Kernel.Actors.GetOrActivate(snapshot.Track.Id).PokePendingWakesAsync();
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        Assert.Equal(archived ? 0 : 1, ctx.Store.GetTurnsForSession(id).Count);
        Assert.Empty(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));
    }
}
