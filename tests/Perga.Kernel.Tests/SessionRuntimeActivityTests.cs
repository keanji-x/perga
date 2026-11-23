using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionRuntimeActivityTests
{
    [Fact]
    public async Task BackgroundOutputDuringActivationPrecedesTheNewTurnsReply()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        var human = new InteropPrincipal.Human();
        await ctx.OpenMcpCallerAsync(id);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var emitted = false;
        // Only the activation upsert writes a running session through this store method.
        // The durable input claim uses SessionInputs.ClaimAsync.
        ctx.Store.OnBeforeUpsertSession = session =>
        {
            if (session.Id != id || session.Status != "running" || emitted) return;
            emitted = true;
            Assert.Single(provider.RecordedInputs);
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.ToolCallStarted("in-activation", "tool", TestJson.Parse("{}")));
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.TextDelta("trailing activity before prompt"));
        };

        var next = await ctx.SendAndWaitForTurnAsync(id, new("next", "activation-next"));
        var page = await ctx.Kernel.GetSessionItemsAsync(id, new(), human, default);
        var reply = Assert.Single(page.Items, item => item.TurnId == next.Id && item.Kind == "assistant_text");
        Assert.True(emitted);
        var background = page.Items.Where(item => item.TurnId is null).ToArray();
        Assert.Equal(["tool_call", "assistant_text"], background.Select(item => item.Kind));
        Assert.All(background, item => Assert.True(item.Id < reply.Id));
    }

    [Fact]
    public async Task AbandonedWakeTurnDrainsAcceptedRuntimeOutputBeforeReturningToIdle()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        var trackId = snapshot.Track.Id;
        var human = new InteropPrincipal.Human();
        await ctx.OpenMcpCallerAsync(id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession provider, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.ToolCallStarted("before-abandon", "tool", TestJson.Parse("{}")));
            var page = await ctx.Kernel.GetSessionItemsAsync(id, new(), human, ct);
            Assert.NotNull(page.ActiveTurn);
            Assert.DoesNotContain(page.Items, item => item.TurnId is null);
            buffered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }

        await ctx.Store.TrackWakes.EnqueueAsync(
            Perga.Kernel.Ports.TrackWakeRequests.Prompt(trackId, "test", "abandoned-wake", "Wake") with { TargetSessionId = id },
            deadline.Token);
        var wake = ctx.Kernel.Actors.GetOrActivate(trackId).PokePendingWakesAsync(abandon.Token);
        try { await buffered.Task.WaitAsync(deadline.Token); }
        finally { abandon.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wake.WaitAsync(deadline.Token));
        var idle = await ctx.Kernel.GetSessionItemsAsync(id, new(), human, deadline.Token);
        Assert.Equal("idle", idle.Session.Status);
        Assert.Null(idle.ActiveTurn);
        var tool = Assert.Single(idle.Items, item => item.TurnId is null);
        Assert.Equal("before-abandon", tool.Payload.GetProperty("callId").GetString());
    }

    [Fact]
    public async Task SlowContinuousBackgroundTextFlushesAfterFiveSeconds()
    {
        var time = new ManualOutputClock();
        await using var ctx = new KernelTestContext(timeProvider: time);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        await ctx.OpenMcpCallerAsync(id);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var human = new InteropPrincipal.Human();
        for (var index = 0; index < 10; index++)
        {
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.ThinkingDelta("x"));
            var page = await ctx.Kernel.GetSessionItemsAsync(id, new(), human, default);
            if (index < 9) Assert.DoesNotContain(page.Items, item => item.TurnId is null);
            else Assert.Equal(new string('x', 10), Assert.Single(page.Items, item => item.TurnId is null).Payload.GetProperty("text").GetString());
            time.Advance(TimeSpan.FromMilliseconds(600));
        }
    }

    private sealed class ManualOutputClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    [Fact]
    public async Task ImmediateBackgroundOutputFollowsFinalTurnReplyInStorage()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        var human = new InteropPrincipal.Human();
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession provider, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.TextDelta("final reply");
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.TextDelta("background text"));
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.ToolCallStarted("late-tool", "perga:flow_commit", TestJson.Parse("{}")));
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.ToolCallCompleted("late-tool", "finished", false));
            var beforeSettle = await ctx.Kernel.GetSessionItemsAsync(id, new(), human, ct);
            Assert.NotNull(beforeSettle.ActiveTurn);
            Assert.DoesNotContain(beforeSettle.Items, item => item.TurnId is null);
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }

        var initial = await ctx.SendAndWaitForTurnAsync(id, new("go", "go"));
        var page = await ctx.Kernel.GetSessionItemsAsync(id, new(), human, default);
        Assert.Equal("completed", initial.Status);
        var reply = Assert.Single(page.Items, item => item.TurnId == initial.Id && item.Kind == "assistant_text");
        var background = page.Items.Where(item => item.TurnId is null).ToArray();
        Assert.Equal(["assistant_text", "tool_call", "tool_result"], background.Select(item => item.Kind));
        Assert.All(background, item => Assert.True(item.Id > reply.Id));
        Assert.Equal("background text", background[0].Payload.GetProperty("text").GetString());
    }

    [Fact]
    public async Task LargeBackgroundBufferFlushesWithoutWaitingForQuiet()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        await ctx.OpenMcpCallerAsync(id);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var delta = new string('x', 1024);
        for (var i = 0; i < 16; i++)
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.TextDelta(delta));

        var page = await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        var item = Assert.Single(page.Items, item => item.TurnId is null);
        Assert.Equal("assistant_text", item.Kind);
        Assert.Equal(16 * 1024, item.Payload.GetProperty("text").GetString()!.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscriptFlushFailureDoesNotSkipProviderClose(bool publicationFails)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        var caller = await ctx.OpenMcpCallerAsync(id);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TextDelta("buffered output"));
        if (publicationFails)
        {
            var publications = 0;
            ctx.Store.BeforeEventPublication = _ => ++publications == 1
                ? Task.FromException(new IOException("Publication failed after transcript write.")) : Task.CompletedTask;
            // Recovery has no durable status event before the final transcript flush.
            await ctx.Kernel.ControlSessionAsync(id, new("reset_context"), new InteropPrincipal.Human(), default);
            Assert.True(publications >= 2);
        }
        else
        {
            ctx.Store.FailAppendSessionItems = true;
            await ctx.Kernel.UpdateSessionAsync(id, new(Archived: true), new InteropPrincipal.Human(), default);
        }
        Assert.True(provider.Disposed);
        Assert.Equal(1, provider.DisposeCalls);
        Assert.Null(ctx.Kernel.McpSessionBridge.ResolveToken(caller.Token));
    }

    [Fact]
    public async Task BackgroundTranscriptFlushesAtKindAndToolBoundariesWithoutCreatingATurn()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "background-first"));
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.ThinkingDelta("checking "));
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.ThinkingDelta("progress"));
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TextDelta("working"));
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.ToolCallStarted("bg-call", "perga:flow_commit", TestJson.FromObject(new { rev = 1 })));
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.ToolCallCompleted("bg-call", "committed", false));

        var page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Collection(page.Items.Where(item => item.TurnId is null),
            item => Assert.Equal(("thinking", "checking progress"), (item.Kind, item.Payload.GetProperty("text").GetString())),
            item => Assert.Equal(("assistant_text", "working"), (item.Kind, item.Payload.GetProperty("text").GetString())),
            item => Assert.Equal(("tool_call", "bg-call"), (item.Kind, item.Payload.GetProperty("callId").GetString())),
            item => Assert.Equal(("tool_result", "committed"), (item.Kind, item.Payload.GetProperty("output").GetString())));
        Assert.Equal("idle", page.Session.Status);
        Assert.Null(page.ActiveTurn);
        Assert.Equal("completed", Assert.Single(await ctx.Store.ListSessionTurnsAsync(sessionId, default)).Status);
    }

    [Fact]
    public async Task LeadCanCreateASessionAfterItsTurnCompletes()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "governance-first"));
        var bridge = ctx.Kernel.McpSessionBridge;
        var caller = bridge.ResolveToken(bridge.GetTokenForSession(sessionId)!)!;
        await ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.SessionCreate,
            TestJson.FromObject(new { title = "continued", requestKey = "after-turn-create" }));
        Assert.Contains((await ctx.Store.LoadTrackAggregateAsync(snapshot.Track.Id, default))!.Sessions,
            session => session.ParentSessionId == sessionId && session.Title == "continued");
    }

    [Theory]
    [InlineData("quiet")]
    [InlineData("next-turn")]
    [InlineData("exit")]
    [InlineData("close")]
    public async Task TrailingOutputSurvivesRuntimeBoundaries(string boundary)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        var human = new InteropPrincipal.Human();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var caller = await ctx.OpenMcpCallerAsync(sessionId, ct);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TextDelta("trailing"));

        switch (boundary)
        {
            case "next-turn":
                await ctx.SendAndWaitForTurnAsync(sessionId, new("next", "background-next"), ct);
                break;
            case "exit":
                provider.EmitRuntimeActivity(new UnifiedAgentEvent.RuntimeExited());
                provider.EmitRuntimeActivity(new UnifiedAgentEvent.TextDelta("too late"));
                break;
            case "close":
                await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: true), human, ct);
                provider.EmitRuntimeActivity(new UnifiedAgentEvent.TextDelta("too late"));
                break;
        }

        var page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), human, ct);
        while (boundary == "quiet" && page.Items.All(item => item.TurnId is not null))
        {
            await Task.Delay(10, ct);
            page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), human, ct);
        }
        var background = Assert.Single(page.Items, item => item.TurnId is null);
        Assert.Equal("assistant_text", background.Kind);
        Assert.Equal("trailing", background.Payload.GetProperty("text").GetString());
        Assert.Null(page.ActiveTurn);
        if (boundary == "next-turn")
        {
            Assert.Contains(page.Items, item => item.TurnId == page.LastTurn!.Id && item.Kind == "assistant_text" && item.Id > background.Id);
        }
        else
        {
            Assert.Single(await ctx.Store.ListSessionTurnsAsync(sessionId, ct));
        }
        if (boundary is "exit" or "close")
            Assert.Null(ctx.Kernel.McpSessionBridge.ResolveToken(caller.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerCanSettleAfterPromptCompletes(bool fail)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var receipt = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("background", "Perform work"));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        var worker = ctx.RuntimeCaller(receipt.Session.Id);
        var page = await ctx.Kernel.GetSessionItemsAsync(receipt.Session.Id, new(), new InteropPrincipal.Human(), default);
        Assert.Null(page.ActiveTurn);
        Assert.Equal(TaskRunStatus.Running, Assert.Single(await ctx.Kernel.ListTasksAsync(trackId)).Status);

        var tool = fail ? KernelMcpToolNames.TaskFail : KernelMcpToolNames.TaskDone;
        var args = fail ? TestJson.FromObject(new { reason = "background failed" }) : TestJson.Parse("{}");
        var stale = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.InvokeKernelMcpToolAsync(worker with { TaskRunCount = worker.TaskRunCount - 1 }, tool, args));
        Assert.Equal(KernelErrorCode.Conflict, stale.Code);
        await ctx.Kernel.InvokeKernelMcpToolAsync(worker, tool, args);
        Assert.Equal(fail ? TaskRunStatus.Failed : TaskRunStatus.Done, Assert.Single(await ctx.Kernel.ListTasksAsync(trackId)).Status);
        Assert.Null(ctx.Kernel.McpSessionBridge.ResolveToken(worker.Token));
    }

    [Theory]
    [InlineData("running", 0)]
    [InlineData("pending", 0)]
    [InlineData("idle-activity", 0)]
    [InlineData("completed", 1)]
    public async Task AutoCompactionWaitsForBackgroundActivity(string state, int expectedCompactCalls)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        ctx.AgentRuntime.OnOpenedSession = provider => provider.SupportsNativeCompact = true;
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession provider, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            provider.EmitRuntimeActivity(state == "idle-activity"
                ? new UnifiedAgentEvent.AgentStatusChanged(new("idle", IdleActivity: new(1, 2)))
                : new UnifiedAgentEvent.TaskStarted(new("background", Background: true, Status: state)));
            await Task.Yield();
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(85_000, 0, 0, 100_000, UsedTokens: 85_000));
        }

        await ctx.SendAndWaitForTurnAsync(sessionId, new("prepare", "prepare"));
        Assert.Equal(expectedCompactCalls, Assert.Single(ctx.AgentRuntime.OpenedSessions).CompactCalls);
    }

    [Fact]
    public async Task ReplacedAndUnboundTokensCannotMutateTheSession()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        var human = new InteropPrincipal.Human();
        var bridge = ctx.Kernel.McpSessionBridge;
        var unbound = bridge.RegisterSession(snapshot.Track.Id, sessionId, true, null, null, []).Principal;
        await AssertDenied(unbound);
        Assert.Equal("completed", (await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "replace-first"))).Status);
        var original = ctx.RuntimeCaller(sessionId);
        await ctx.Kernel.ControlSessionAsync(sessionId, new("reset_context"), human, default);
        await AssertDenied(original);
        var current = await ctx.OpenMcpCallerAsync(sessionId);
        Assert.NotEqual(original.RuntimeBindingId, current.RuntimeBindingId);
        bridge.RevokeSession(sessionId, original.RuntimeBindingId);
        Assert.NotNull(bridge.ResolveToken(current.Token));
        await AssertDenied(original);
        await AssertDenied(current with { RuntimeBindingId = original.RuntimeBindingId });
        var renamed = await ctx.Kernel.InvokeKernelMcpToolAsync(current, KernelMcpToolNames.TrackRename,
            TestJson.FromObject(new { title = "Current runtime" }));
        Assert.Equal("Current runtime", renamed.GetProperty("title").GetString());
        var mismatched = bridge.RegisterSession(snapshot.Track.Id, sessionId, true, null, null, [],
            runtimeBindingId: original.RuntimeBindingId).Principal;
        await AssertDenied(mismatched);

        async Task AssertDenied(Perga.Kernel.Ports.McpSessionPrincipal caller)
        {
            var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.InvokeKernelMcpToolAsync(
                caller, KernelMcpToolNames.TrackRename, TestJson.FromObject(new { title = "Unauthorized" })));
            Assert.Equal(KernelErrorCode.Conflict, error.Code);
        }
    }

    [Fact]
    public async Task RevocationDuringProviderProbePreventsSessionCreation()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        var caller = await ctx.OpenMcpCallerAsync(sessionId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.ProbeScript = async token =>
        {
            probing.TrySetResult();
            await proceed.Task.WaitAsync(token);
            return ctx.AgentRuntime.Providers;
        };
        var create = ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.SessionCreate,
            TestJson.FromObject(new { title = "Too late", requestKey = "revoked-create" }), ct);

        try
        {
            await probing.Task.WaitAsync(ct);
            await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: true), new InteropPrincipal.Human(), ct);
        }
        finally
        {
            proceed.TrySetResult();
        }

        var error = await Assert.ThrowsAsync<KernelException>(() => create.WaitAsync(ct));
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
        Assert.Single((await ctx.Store.LoadTrackAggregateAsync(snapshot.Track.Id, ct))!.Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationExpiresIdleInteractionBeforeProviderShutdown(bool policyApproval)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        var caller = await ctx.OpenMcpCallerAsync(sessionId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        if (policyApproval)
            await ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new
            {
                source = "export default {id:'confirm',title:'Confirm',before: a=>a.toolName==='track_rename'?AskHuman('Rename?'):Allow()};"
            }), ct);

        var pending = policyApproval
            ? ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackRename, TestJson.FromObject(new { title = "new" }), ct)
            : ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.UserAsk, TestJson.FromObject(new { questions = new[] { new { question = "Continue?", options = new[] { "Yes", "No" } } } }), ct);
        var aggregate = await ctx.Kernel.GetTrackAggregateAsync(snapshot.Track.Id, ct);
        while (aggregate.OpenAsks.Count + aggregate.PendingApprovals.Count == 0)
        {
            await Task.WhenAny(pending, Task.Delay(10, ct));
            if (pending.IsCompleted) await pending;
            aggregate = await ctx.Kernel.GetTrackAggregateAsync(snapshot.Track.Id, ct);
        }
        if (policyApproval) Assert.Null(Assert.Single(aggregate.PendingApprovals).TurnId);

        var shutdownStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Single(ctx.AgentRuntime.OpenedSessions).DisposeHandler = async () =>
        {
            shutdownStarted.TrySetResult();
            await finishShutdown.Task.WaitAsync(ct);
        };
        var closing = ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: true), new InteropPrincipal.Human(), ct);
        try
        {
            await shutdownStarted.Task.WaitAsync(ct);
            Assert.False(closing.IsCompleted);
            Assert.Null(ctx.Kernel.McpSessionBridge.ResolveToken(caller.Token));
            var error = await Assert.ThrowsAsync<KernelException>(() => pending.WaitAsync(ct));
            Assert.Equal(KernelErrorCode.Conflict, error.Code);
            aggregate = await ctx.Kernel.GetTrackAggregateAsync(snapshot.Track.Id, ct);
            Assert.Empty(aggregate.PendingApprovals);
            Assert.Equal(policyApproval ? 0 : 1, aggregate.OpenAsks.Count);
        }
        finally
        {
            finishShutdown.TrySetResult();
        }
        await closing;
    }

    [Fact]
    public async Task RuntimeTasksMergeDuringAndAfterTurnsWithoutHoldingTurnAdmission()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        ctx.AgentRuntime.OnOpenedSession = session => session.PendingRuntimeActivity =
            [new UnifiedAgentEvent.TaskStarted(new("background", "shell", Background: true))];
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            session.EmitRuntimeActivity(new UnifiedAgentEvent.TaskUpdated(new("background", Description: "progress")));
            session.EmitRuntimeActivity(new UnifiedAgentEvent.AgentStatusChanged(new("running", Phase: "thinking", SinceSeq: 3)));
            await Task.Yield();
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "runtime-first"));
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.AgentStatusChanged(new("idle", IdleActivity: new(10, 20))));
        var page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("idle", page.Session.Status);
        Assert.Equal(20, page.RuntimeStatus?.IdleActivity?.LastEventAt);
        var task = Assert.Single(page.RuntimeTasks!);
        Assert.Equal("progress", task.Description);
        Assert.Equal("shell", task.TaskType);
        Assert.True(task.Background);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskEnded(new("background", Status: "completed", Summary: "done", OutputFile: "output")));
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskUpdated(new("background", Status: "running")));
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Null(Assert.Single(page.RuntimeTasks!).Summary);
        Assert.Null(Assert.Single(page.RuntimeTasks!).OutputFile);
        for (var index = 0; index < 70; index++)
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskEnded(new($"ended-{index}", Status: "completed")));
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Equal(65, page.RuntimeTasks!.Count);
        await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: true), new InteropPrincipal.Human(), default);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskEnded(new("background", Status: "completed", Summary: "finished while archived")));
        await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: false), new InteropPrincipal.Human(), default);
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.True(provider.Disposed);
        Assert.Null(page.RuntimeTasks);
        await ctx.SendAndWaitForTurnAsync(sessionId, new("second", "runtime-second"));
        provider = ctx.AgentRuntime.OpenedSessions.Last();
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.AgentStatusChanged(new("idle", Released: true)));
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Empty(page.RuntimeTasks!);
        await ctx.Kernel.ControlSessionAsync(sessionId, new("reset_context"), new InteropPrincipal.Human(), default);
        provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskStarted(new("stale", "shell")));
        page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), new InteropPrincipal.Human(), default);
        Assert.Null(page.RuntimeStatus);
        Assert.Null(page.RuntimeTasks);
    }
}
