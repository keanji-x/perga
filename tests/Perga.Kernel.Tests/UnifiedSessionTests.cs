using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class UnifiedSessionTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

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
        var session = snapshot.Sessions.Single() with { SpawnDepth = depth, BoundAttemptId = bound ? "attempt" : null };
        var principal = new InteropPrincipal.Session(session.TrackId, session.Id, true, session.BoundAttemptId, depth, "", null);
        if (allowed) SessionPolicy.RequireCreate(principal, session, session.TrackId);
        else Assert.Throws<KernelException>(() => SessionPolicy.RequireCreate(principal, session, session.TrackId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigVersionConflictsAndPreservesOmittedFields(bool worker)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: worker ? "inspect" : null, Task: worker ? new() : null), "create", Human, default);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var id = created.Receipt.Session.Id;
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
    public async Task WorkerRecoveryRetainsAttemptAndLease(string action)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        ctx.AgentRuntime.SupportsSessionResume = true;
        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(Prompt: "inspect", Task: new()), "task", Human, default);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var id = created.Receipt.Session.Id;
        var before = await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        if (action == "reconnect")
        {
            // The live actor owns the lease, so induce an unknown stream in its next turn.
            ctx.AgentRuntime.TurnScript = Inconclusive;
            await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "lose stream", RequestKey: "lose"), Human, default);
            await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        }
        var attempts = await ctx.Kernel.ListTaskAttemptsAsync(snapshot.Track.Id, created.Receipt.Task!.Id);
        var lease = Assert.Single(attempts).WorkspaceLease;
        var recovered = await ctx.Kernel.ControlSessionAsync(id, new(action), Human, default);
        Assert.Equal(before.Session.BoundAttemptId, recovered.BoundAttemptId);
        Assert.Equal(lease, Assert.Single(await ctx.Kernel.ListTaskAttemptsAsync(snapshot.Track.Id, created.Receipt.Task.Id)).WorkspaceLease);
        Assert.Equal(action == "reset_context" ? before.Session.Epoch + 1 : before.Session.Epoch, recovered.Epoch);
        Assert.Equal("idle", recovered.Status);
        if (action == "reset_context") Assert.Null(recovered.NativeThreadId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationPreservesCommittedSessionConfiguration(bool blocked)
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers = [new("claude", true, "1", null, ["model-a", "model-b"])];
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var blocker = blocked ? (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: "blocker", Provider: "claude", Task: new()), "blocker", Human, default)).Receipt : null;
        if (blocked) await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var created = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: "inspect", Provider: "claude", Model: "model-a", PermissionMode: "plan",
                Task: new(DependsOn: blocker is null ? null : [blocker.Task!.TaskKey])), "config", Human, default)).Receipt;
        var id = created.Session.Id;
        if (blocked)
        {
            Assert.Null(created.Session.BoundAttemptId);
            await ctx.Kernel.UpdateSessionAsync(id, new(ExpectedConfigVersion: 0, Model: new("model-b"),
                ReasoningEffort: new("high")), Human, default);
            await ctx.Kernel.CompleteAttemptDoneAsync(snapshot.Track.Id, blocker!.Session.Id);
            await ctx.Kernel.AcceptTaskAsync(snapshot.Track.Id, null, blocker.Task!.Id);
        }
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var session = (await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).Session;
        Assert.NotNull(session.BoundAttemptId);
        Assert.Equal("claude", session.Provider);
        Assert.Equal("plan", session.PermissionMode);
        Assert.Equal(blocked ? "model-b" : "model-a", session.Model);
        Assert.Equal(blocked ? "high" : null, session.ReasoningEffort);
        Assert.Equal(blocked ? 1L : 0L, session.ConfigVersion);
        var launch = Assert.Single(ctx.AgentRuntime.OpenedSessions, s => s.LaunchSpec.StateKey == id).LaunchSpec;
        Assert.Equal(session.Model, launch.Model);
        Assert.Equal("plan", launch.PermissionMode);
        Assert.Equal(session.ReasoningEffort, launch.ReasoningEffort);
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
