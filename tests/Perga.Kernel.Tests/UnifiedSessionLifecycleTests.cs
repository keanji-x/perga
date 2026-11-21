using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Events;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class UnifiedSessionLifecycleTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    private static async Task<SessionItemsPageDto> WaitForPage(KernelTestContext ctx, string id,
        Func<SessionItemsPageDto, bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var page = await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), deadline.Token);
            if (ready(page)) return page;
            await Task.Delay(5, deadline.Token);
        }
    }

    private static InteropPrincipal.Session Caller(KernelTestContext ctx, AgentSessionDto session, string turnId)
    {
        var registration = ctx.Kernel.McpSessionBridge.RegisterSession(session.TrackId, session.Id, session.IsLead,
            session.BoundTaskId, session.BoundAttemptId, []);
        return new(session.TrackId, session.Id, session.IsLead, session.BoundAttemptId, session.SpawnDepth,
            registration.Principal.Token, new(turnId));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> Hold(Task release,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.TextDelta("started");
        await release.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TextDelta("finished");
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

    [Fact]
    public async Task ExplicitSteerDeclinedByCapableRuntimeNeverExecutesOnNextTurn()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.OnOpenedSession = s => s.SteerResult = SteerDelivery.Declined;
        ctx.AgentRuntime.TurnScript = (_, _, ct) => Hold(release.Task, ct);
        await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "running", RequestKey: "running"), Human, default);
        var page = await WaitForPage(ctx, id, p => p.ActiveTurn is not null);
        try
        {
            Assert.True(Assert.Single(ctx.AgentRuntime.OpenedSessions).SupportsNativeSteer);
            var receipt = await ctx.Kernel.SendSessionMessageAsync(id,
                new(Content: "declined", RequestKey: "declined", Mode: "steer", TurnId: page.ActiveTurn!.Id), Human, default);
            Assert.Equal("rejected", receipt.Delivery);
            Assert.Equal("rejected", (await ctx.Store.SessionMessages.GetAsync(id, false, receipt.MessageId, default))!.Delivery);
        }
        finally { release.TrySetResult(); }
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        await ctx.SendAndWaitForTurnAsync(id, new("next", "next"));
        Assert.DoesNotContain(ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs), i => i.Prompt == "declined");
    }

    [Fact]
    public async Task CancelledLazyConfigurationUpdateLeavesNoReservation()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        using var caller = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.Store.BeforeEventPublication = async events =>
            {
                if (!events.Any(e => e.Kind == KernelEventKinds.SessionUpdated)) return;
                entered.TrySetResult();
                await release.Task;
            };
        try
        {
            var update = Task.Run(() => ctx.Kernel.UpdateSessionAsync(id, new(Model: new("new-model")), Human, caller.Token));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
        }
        finally { release.TrySetResult(); ctx.Store.BeforeEventPublication = null; }
        // Flush the preparation callback before observing the background operation.
        await ctx.Kernel.ListSessionsAsync(snapshot.Track.Id, new(), Human, default);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id).WaitAsync(TimeSpan.FromSeconds(3));
        await ctx.SendAndWaitForTurnAsync(id, new("after cancellation", "after-cancellation"));
        Assert.Contains(ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs), i => i.Prompt == "after cancellation");
        Assert.Equal("new-model", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.Model);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActorShutdownJoinsLazyRebindBeforeOwnershipCleanup(bool duringConfigure)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        void Pause()
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }
        if (duringConfigure)
            agent.ConfigureHandler = _ =>
            {
                Pause();
                return new SessionConfigurationOutcome.RequiresReopen("Launch-only model.");
            };
        else
            ctx.AgentRuntime.BeforeOpenAsync = () => { Pause(); return Task.CompletedTask; };
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("new-model")), Human, default);
        Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var update = Task.Run(() => ctx.SendAndWaitForTurnAsync(id, new("next", "lazy-next")));
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            shutdown = ctx.Kernel.Actors.GetOrActivate(snapshot.Track.Id).DisposeAsync().AsTask();
            Assert.NotSame(shutdown, await Task.WhenAny(shutdown, Task.Delay(100)));
            if (duringConfigure) Assert.False(agent.Disposed);
        }
        finally
        {
            release.Set();
            await Assert.ThrowsAnyAsync<Exception>(() => update);
            if (shutdown is not null) await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(agent.Disposed);
        Assert.Equal(1, agent.DisposeCalls);
        Assert.All(ctx.AgentRuntime.OpenedSessions, s =>
        {
            Assert.True(s.ShutdownConfirmed);
            Assert.True(s.Disposed);
            Assert.Equal(1, s.DisposeCalls);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSteerRejectsUnsupportedRuntimeBeforeAdmission(bool queuedMessage)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.OnOpenedSession = s => s.SupportsNativeSteer = false;
        ctx.AgentRuntime.TurnScript = (_, _, ct) => Hold(release.Task, ct);
        await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "running", RequestKey: "running"), Human, default);
        var page = await WaitForPage(ctx, id, p => p.ActiveTurn is not null);
        var queued = queuedMessage ? await ctx.Kernel.SendSessionMessageAsync(id,
            new(Content: "waiting", RequestKey: "waiting", Mode: "queue"), Human, default) : null;
        try
        {
            var failure = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.SendSessionMessageAsync(id,
                new(Content: queuedMessage ? null : "unsupported", RequestKey: "unsupported", Mode: "steer",
                    TurnId: page.ActiveTurn!.Id, MessageId: queued?.MessageId), Human, default));
            Assert.Equal(KernelErrorCode.PreconditionFailed, failure.Code);
            if (queued is not null)
                Assert.Equal("queued", (await ctx.Store.SessionMessages.GetAsync(id, false, queued.MessageId, default))!.Delivery);
            else
                Assert.Null(await ctx.Store.SessionMessages.FindAsync(id, false, "owner:installation", "unsupported", default));
        }
        finally { release.TrySetResult(); }
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        Assert.DoesNotContain(ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs), i => i.Prompt == "unsupported");
    }

    [Theory]
    [InlineData("reset_context", false)]
    [InlineData("reset_context", true)]
    public async Task InterruptedWorkerRecoveryMustConfirmRetainedHandleShutdown(string action, bool disposalThrows)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        ctx.AgentRuntime.SupportsSessionResume = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (s, _, ct) => BoundHold(s, release.Task, ct);
        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: "worker", Task: new()), "worker", Human, default);
        var id = created.Receipt.Session.Id;
        var page = await WaitForPage(ctx, id, p => p.Activity?.Text == "started");
        var agent = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        agent.FailDisposal = disposalThrows;
        agent.ShutdownConfirmed = false;
        await ctx.Kernel.ControlSessionAsync(id, new("stop", TurnId: page.ActiveTurn!.Id), Human, default);
        await WaitForPage(ctx, id, p => p.ActiveTurn is null);
        var before = (await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).Session;
        try
        {
            var failure = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.ControlSessionAsync(id, new(action), Human, default));
            Assert.Equal(KernelErrorCode.PreconditionFailed, failure.Code);
            Assert.Equal(before.Epoch, (await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).Session.Epoch);
            Assert.Single(ctx.AgentRuntime.OpenedSessions);
            await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.SendSessionMessageAsync(id,
                new(Content: "blocked", RequestKey: "blocked"), Human, default));
        }
        finally { agent.FailDisposal = false; agent.ShutdownConfirmed = true; release.TrySetResult(); }
        if (action == "reset_context")
        {
            var recovered = await ctx.Kernel.ControlSessionAsync(id, new(action), Human, default);
            Assert.Equal(before.Epoch + 1, recovered.Epoch);
            Assert.True(agent.DisposeCalls >= 2);
            Assert.Equal(before.BoundAttemptId, recovered.BoundAttemptId);
        }
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> BoundHold(ScriptableFakeAgentSession session, Task release,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ThreadBound(session.NativeThreadId!);
        await foreach (var evt in Hold(release, ct)) yield return evt;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LazyConfigurationReopensOnNextTurnOutsideMailboxAndKeepsItResponsive(bool worker, bool slowDisposal)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: "first", Task: worker ? new() : null), "create", Human, default);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        var id = created.Receipt.Session.Id;
        var turnRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => Hold(turnRelease.Task, ct);
        var leadId = snapshot.Track.LeadSessionId!;
        await ctx.Kernel.SendSessionMessageAsync(leadId, new(Content: "running", RequestKey: "running"), Human, default);
        var running = await WaitForPage(ctx, leadId, p => p.Activity?.Text == "started");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Pause() { entered.TrySetResult(); return release.Task; }
        if (slowDisposal) ctx.AgentRuntime.OpenedSessions.Single(s => s.LaunchSpec.StateKey == id).DisposeHandler = Pause;
        else ctx.AgentRuntime.BeforeOpenAsync = Pause;
        try
        {
            var updated = await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("new-model")), Human, default);
            Assert.Equal("new-model", updated.Model);
            var update = Task.Run(async () =>
            {
                await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "next", RequestKey: "lazy-next"), Human, default);
                await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
            });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(update.IsCompleted);
            await ctx.Kernel.ListSessionsAsync(snapshot.Track.Id, new(), Human, default).WaitAsync(TimeSpan.FromSeconds(2));
            // Control on another session must also stay responsive while this provider is opening.
            await ctx.Kernel.ControlSessionAsync(leadId, new("stop", TurnId: running.ActiveTurn!.Id), Human, default)
                .WaitAsync(TimeSpan.FromSeconds(2));
            release.TrySetResult();
            turnRelease.TrySetResult();
            await update.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(); turnRelease.TrySetResult(); }
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        Assert.Equal("new-model", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueSteerAndAuthorReceiptsShareAdmission(bool worker)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var lead = snapshot.Sessions.Single();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => Hold(release.Task, ct);
        await ctx.Kernel.SendSessionMessageAsync(lead.Id, new(Content: "caller", RequestKey: "caller"), Human, default);
        var parentPage = await WaitForPage(ctx, lead.Id, p => p.ActiveTurn is not null);
        var caller = Caller(ctx, lead, parentPage.ActiveTurn!.Id);
        var created = await ctx.Kernel.CreateSessionAsync(lead.TrackId,
            new(Prompt: "target", Task: worker ? new() : null), "child", caller, default);
        Assert.NotNull(created.Receipt.FirstInput);
        var id = created.Receipt.Session.Id;
        var page = await WaitForPage(ctx, id, p => p.ActiveTurn is not null);
        var request = new SendSessionMessageRequest(Content: "queued", RequestKey: "shared-key", Mode: "queue");
        var queued = await ctx.Kernel.SendSessionMessageAsync(id, request, caller, default);
        var replay = await ctx.Kernel.SendSessionMessageAsync(id, request, caller, default);
        Assert.Equal(queued.MessageId, replay.MessageId);
        Assert.True(replay.Replay);
        var stored = await ctx.Store.SessionMessages.GetAsync(id, worker, queued.MessageId, default);
        Assert.Equal(lead.Id, stored!.AuthorId);
        var human = await ctx.Kernel.SendSessionMessageAsync(id, request, Human, default);
        Assert.NotEqual(queued.MessageId, human.MessageId);
        var steer = new SendSessionMessageRequest(MessageId: queued.MessageId, Mode: "steer", TurnId: page.ActiveTurn!.Id);
        Assert.Equal("delivered", (await ctx.Kernel.SendSessionMessageAsync(id, steer, caller, default)).Delivery);
        Assert.True((await ctx.Kernel.SendSessionMessageAsync(id, steer, caller, default)).Replay);
        Assert.Single(ctx.AgentRuntime.OpenedSessions.Single(s => s.LaunchSpec.StateKey == id).SteeredMessages);
        Assert.Equal("delivered", (await ctx.Store.SessionMessages.GetAsync(id, worker, queued.MessageId, default))!.Delivery);
        var stale = caller with { ExecutionOrigin = new("old-turn") };
        Assert.Equal(KernelErrorCode.Conflict, (await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.SendSessionMessageAsync(id, request, stale, default))).Code);
        release.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(lead.TrackId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitMsWaitsForActivityOnEverySession(bool worker)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => Hold(release.Task, ct);
        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: "target", Task: worker ? new() : null), "child", Human, default);
        var id = created.Receipt.Session.Id;
        var page = await WaitForPage(ctx, id, p => p.Activity?.Text == "started");
        var read = ctx.Kernel.GetSessionItemsAsync(id,
            new(ActivityTurnId: page.ActiveTurn!.Id, AfterSequence: page.Activity!.Sequence, WaitMs: 1000), Human, default);
        await Task.Delay(25);
        Assert.False(read.IsCompleted);
        release.TrySetResult();
        var updated = await read.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(updated.ActiveTurn is null || updated.Activity!.Sequence > page.Activity.Sequence);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopCancelsProviderStartupThroughMailbox(bool worker)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.BeforeOpenAsync = () => { entered.TrySetResult(); return release.Task; };
        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: "target", Task: worker ? new() : null), "child", Human, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(created.Receipt.FirstInput);
        var id = created.Receipt.Session.Id;
        try
        {
            await ctx.Kernel.ControlSessionAsync(id, new("stop", TurnId: created.Receipt.FirstInput!.TurnId), Human, default)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { release.TrySetResult(); }
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        Assert.Equal("interrupted", Assert.Single(ctx.Store.GetTurnsForSession(id)).Status);
        if (worker) Assert.True((await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).WorkerPaused);
        Assert.All(ctx.AgentRuntime.OpenedSessions, s => Assert.Empty(s.RecordedInputs));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("reset_context")]
    [InlineData("compact")]
    [InlineData("archive")]
    [InlineData("provider")]
    public async Task LiveCallerCannotDestroyItself(string action)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var lead = snapshot.Sessions.Single();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => Hold(release.Task, ct);
        await ctx.Kernel.SendSessionMessageAsync(lead.Id, new(Content: "caller", RequestKey: "caller"), Human, default);
        var page = await WaitForPage(ctx, lead.Id, p => p.ActiveTurn is not null);
        var caller = Caller(ctx, lead, page.ActiveTurn!.Id);
        var failure = await Assert.ThrowsAsync<KernelException>(() => action switch
        {
            "archive" => ctx.Kernel.UpdateSessionAsync(lead.Id, new(Archived: true), caller, default),
            "provider" => ctx.Kernel.UpdateSessionAsync(lead.Id, new(Provider: "claude"), caller, default),
            _ => ctx.Kernel.ControlSessionAsync(lead.Id, new(action, TurnId: page.ActiveTurn.Id), caller, default)
        });
        Assert.Equal(KernelErrorCode.Forbidden, failure.Code);
        Assert.Equal(page.ActiveTurn.Id, (await ctx.Kernel.GetSessionItemsAsync(lead.Id, new(), new InteropPrincipal.Human(), default)).ActiveTurn!.Id);
        release.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(lead.TrackId);
    }
    public static IEnumerable<object[]> GovernanceCases =>
        from action in new[] { "cancel", "accept", "reject" }
        from role in new[] { "human", "parent", "lead", "peer", "self" }
        select new object[] { action, role };

    [Theory]
    [MemberData(nameof(GovernanceCases))]
    public async Task TaskGovernanceUsesTheTaskSessionOwnershipTree(string action, string role)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var parent = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(), "parent", Human, default)).Receipt.Session;
        var peer = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(), "peer", Human, default)).Receipt.Session;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (s, _, ct) => s.LaunchSpec.StateKey == parent.Id ? Hold(release.Task, ct) : Completed();
        await ctx.Kernel.SendSessionMessageAsync(parent.Id, new(Content: "parent", RequestKey: "parent"), Human, default);
        var page = await WaitForPage(ctx, parent.Id, p => p.ActiveTurn is not null);
        var child = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(Prompt: "child", Task: new()),
            "child", Caller(ctx, parent, page.ActiveTurn!.Id), default)).Receipt;
        await WaitForPage(ctx, child.Session.Id, p => p.LastTurn?.Status == "completed");
        await ctx.Kernel.CompleteAttemptDoneAsync(snapshot.Track.Id, child.Session.Id);
        await ctx.Kernel.GuardMcpToolCallAsync(snapshot.Track.Id, parent.Id, "perga", "task_" + action,
            TestJson.FromObject(new { taskId = child.Task!.Id }));
        var caller = role switch { "human" => null, "parent" => parent.Id, "lead" => snapshot.Track.LeadSessionId,
            "peer" => peer.Id, _ => child.Session.Id };
        Task<TaskDto> Govern() => action switch
        {
            "cancel" => ctx.Kernel.CancelTaskAsync(snapshot.Track.Id, caller, child.Task!.Id),
            "accept" => ctx.Kernel.AcceptTaskAsync(snapshot.Track.Id, caller, child.Task!.Id),
            _ => ctx.Kernel.RejectTaskAsync(snapshot.Track.Id, caller, child.Task!.Id, "review")
        };
        if (role is "peer" or "self")
            Assert.Equal(KernelErrorCode.Forbidden, (await Assert.ThrowsAsync<KernelException>(Govern)).Code);
        else await Govern();
        release.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> Completed()
    {
        await Task.Yield();
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

    [Theory]
    [InlineData("turn_end", false, false, 2)]
    [InlineData("task_settled", true, false, 2)]
    [InlineData("none", false, false, 1)]
    [InlineData("none", true, false, 1)]
    [InlineData("turn_end", false, true, 1)]
    [InlineData("task_settled", true, true, 1)]
    public async Task NotifyParentTargetsOnlyLiveParents(string notify, bool worker, bool archived, int turns)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var parent = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(), "parent", Human, default)).Receipt.Session;
        var parentRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var childRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (s, _, ct) => Hold(s.LaunchSpec.StateKey == parent.Id ? parentRelease.Task : childRelease.Task, ct);
        await ctx.Kernel.SendSessionMessageAsync(parent.Id, new(Content: "parent", RequestKey: "parent"), Human, default);
        var parentPage = await WaitForPage(ctx, parent.Id, p => p.ActiveTurn is not null);
        var child = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Prompt: "child", NotifyParent: notify, Task: worker ? new() : null), "child",
            Caller(ctx, parent, parentPage.ActiveTurn!.Id), default)).Receipt.Session;
        await WaitForPage(ctx, child.Id, p => p.ActiveTurn is not null);
        parentRelease.TrySetResult();
        await WaitForPage(ctx, parent.Id, p => p.State == "idle");
        if (archived) await ctx.Kernel.UpdateSessionAsync(parent.Id, new(Archived: true), Human, default);
        childRelease.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        if (worker)
        {
            await ctx.Kernel.CompleteAttemptDoneAsync(snapshot.Track.Id, child.Id);
            await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
        }
        Assert.Equal(turns, ctx.Store.GetTurnsForSession(parent.Id).Count);
        Assert.Empty(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    public async Task ApprovalDeliveryAbortsWhenCallerStopsDuringAudit(string option)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var parent = snapshot.Sessions.Single();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (s, _, ct) => s.LaunchSpec.StateKey == parent.Id
            ? Hold(release.Task, ct) : Approval(release.Task, ct);
        await ctx.Kernel.SendSessionMessageAsync(parent.Id, new(Content: "parent", RequestKey: "parent"), Human, default);
        var parentPage = await WaitForPage(ctx, parent.Id, p => p.ActiveTurn is not null);
        var caller = Caller(ctx, parent, parentPage.ActiveTurn!.Id);
        var child = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(Prompt: "child"), "child", caller, default)).Receipt.Session;
        var childPage = await WaitForPage(ctx, child.Id, p => p.PendingInteractions?.Count == 1);
        var approval = Assert.Single(childPage.PendingInteractions!);
        Assert.True(approval.CanRespond);
        Assert.Equal("approval", approval.Kind);
        Assert.Equal(["approve", "deny"], approval.Options!.Select(o => o.Id));
        var self = caller with { SessionId = child.Id, IsLead = false };
        Assert.False(Assert.Single((await ctx.Kernel.GetSessionItemsAsync(child.Id, new SessionReadQuery(), self, default))
            .PendingInteractions!).CanRespond);
        ctx.Store.BeforeEventAppend = async evt =>
        {
            if (evt.Kind != KernelEventKinds.ApprovalDecided) return;
            await ctx.Kernel.ControlSessionAsync(parent.Id, new("stop", TurnId: parentPage.ActiveTurn.Id), Human, default);
        };
        var failure = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.RespondInteractionAsync(child.Id,
            approval.Id, new(approval.ExecutionId, option), caller, default));
        Assert.Equal(KernelErrorCode.Conflict, failure.Code);
        Assert.Single((await ctx.Kernel.GetSessionItemsAsync(child.Id, new(), new InteropPrincipal.Human(), default)).PendingInteractions!);
        Assert.Empty(ctx.AgentRuntime.OpenedSessions.Single(s => s.LaunchSpec.StateKey == child.Id).Approvals);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ctx.Store.PersistedEvents.Any(e => e.Kind == KernelEventKinds.ApprovalDelivery))
            await Task.Delay(5, deadline.Token);
        var audit = Assert.Single(ctx.Store.PersistedEvents, e => e.Kind == KernelEventKinds.ApprovalDelivery);
        Assert.Contains("aborted", audit.Payload.ToString());
        ctx.Store.BeforeEventAppend = null;
        await ctx.Kernel.RespondInteractionAsync(child.Id, approval.Id, new(approval.ExecutionId, option), Human, default);
        Assert.Empty((await ctx.Kernel.GetSessionItemsAsync(child.Id, new(), new InteropPrincipal.Human(), default)).PendingInteractions!);
        release.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> Approval(Task release,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ApprovalRequested("approval", "shell", TestJson.Parse("{}"));
        await release.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

}
