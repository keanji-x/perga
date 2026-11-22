using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Actors;
using Perga.Kernel.Mcp;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionRuntimeEvictionTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly KernelOptions Options = new()
    {
        ActorIdleTimeout = TimeSpan.Zero,
        ActorEvictionSweepInterval = TimeSpan.Zero,
        SessionIdleTimeout = IdleTimeout
    };

    [Fact]
    public async Task IdleSweepPreservesStoredSessionAndResumesOnFollowUp()
    {
        await using var ctx = new KernelTestContext(Options);
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var sessionId = snapshot.Track.LeadSessionId!;
        var actor = ctx.Kernel.Actors.GetOrActivate(trackId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "first"), ct);
        await ctx.WaitForActorQuiescenceAsync(trackId, ct);
        var before = Assert.Single((await ctx.Store.LoadTrackAggregateAsync(trackId, ct))!.Sessions);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var lastActivity = actor.LastActivityAtMs;
        Assert.NotNull(before.NativeThreadId);
        Assert.NotNull(before.ContextUsage);

        ctx.Time.Advance(IdleTimeout - TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
        Assert.False(provider.Disposed);
        ctx.Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleActorsAsync(ct));
        Assert.True(provider.Disposed);
        Assert.True(ctx.Kernel.Actors.IsTrackActiveInMemory(trackId));
        Assert.Equal(lastActivity, actor.LastActivityAtMs);
        var after = Assert.Single((await ctx.Store.LoadTrackAggregateAsync(trackId, ct))!.Sessions);
        Assert.Equal(before, after);

        Assert.Equal("completed", (await ctx.SendAndWaitForTurnAsync(sessionId, new("follow-up", "follow-up"), ct)).Status);
        var reopened = ctx.AgentRuntime.OpenedSessions.Last();
        Assert.NotSame(provider, reopened);
        Assert.Equal(before.NativeThreadId, reopened.LaunchSpec.ResumeNativeThreadId);
        Assert.Equal(before.Epoch, (await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), Human, ct)).Session.Epoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeActivityAndMcpCallsRefreshIdleDeadline(bool mcp)
    {
        await using var ctx = new KernelTestContext(Options);
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        var caller = await ctx.OpenMcpCallerAsync(sessionId);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        ctx.Time.Advance(IdleTimeout);
        if (mcp)
            await ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackRename,
                TestJson.FromObject(new { title = "Background MCP call" }));
        else
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.AgentStatusChanged(new("idle")));

        Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync());
        Assert.False(provider.Disposed);
        ctx.Time.Advance(IdleTimeout);
        Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync());
        Assert.Null(ctx.Kernel.McpSessionBridge.ResolveToken(caller.Token));
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("no-native-id")]
    [InlineData("running")]
    [InlineData("pending")]
    public async Task IdleSweepKeepsUnresumableHandlesAndBackgroundWork(string reason)
    {
        await using var ctx = new KernelTestContext(Options);
        ctx.AgentRuntime.SupportsSessionResume = reason != "unsupported";
        ctx.AgentRuntime.OnOpenedSession = provider =>
        {
            if (reason == "no-native-id") provider.NativeThreadId = null;
            if (reason is "running" or "pending")
                provider.PendingRuntimeActivity = [new UnifiedAgentEvent.TaskStarted(new("background", Background: true, Status: reason))];
        };
        ctx.AgentRuntime.TurnScript = (_, _, ct) => CompleteAfterAsync(Task.CompletedTask, null, ct);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var sessionId = snapshot.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "first"), ct);
        await ctx.WaitForActorQuiescenceAsync(snapshot.Track.Id, ct);
        if (reason == "no-native-id")
            Assert.Null((await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), Human, ct)).Session.NativeThreadId);

        ctx.Time.Advance(IdleTimeout);
        Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.False(provider.Disposed);
        if (reason is "running" or "pending")
        {
            provider.EmitRuntimeActivity(new UnifiedAgentEvent.TaskEnded(new("background", Status: "completed")));
            Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
            ctx.Time.Advance(IdleTimeout);
            Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
        }
    }

    [Fact]
    public async Task IdleSweepKeepsActiveTurnAndMeasuresIdleTimeFromSettlement()
    {
        await using var ctx = new KernelTestContext(Options);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var enter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.SupportsSessionResume = true;
        ctx.AgentRuntime.TurnScript = (_, _, ct) => CompleteAfterAsync(release.Task, enter, ct);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var turn = ctx.SendAndWaitForTurnAsync(snapshot.Track.LeadSessionId!, new("first", "first"), ct);
        try
        {
            await enter.Task.WaitAsync(ct);
            ctx.Time.Advance(IdleTimeout);
            Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
            Assert.False(Assert.Single(ctx.AgentRuntime.OpenedSessions).Disposed);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("completed", (await turn).Status);
        Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
        ctx.Time.Advance(IdleTimeout);
        Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
    }

    [Fact]
    public async Task CombinedSweepHonorsSessionTimeoutAndProtectsOpenHandlesFromActorEviction()
    {
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            ActorIdleTimeout = TimeSpan.FromMinutes(10),
            SessionIdleTimeout = TimeSpan.FromMinutes(15),
            ActorEvictionSweepInterval = TimeSpan.Zero
        });
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, resumable) = await ctx.CreateDefaultTrackAsync();
        var (_, unresumable) = await ctx.CreateDefaultTrackAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await ctx.SendAndWaitForTurnAsync(resumable.Track.LeadSessionId!, new("first", "first"), ct);
        ctx.AgentRuntime.SupportsSessionResume = false;
        await ctx.SendAndWaitForTurnAsync(unresumable.Track.LeadSessionId!, new("first", "first"), ct);
        await ctx.WaitForActorQuiescenceAsync(resumable.Track.Id, ct);
        await ctx.WaitForActorQuiescenceAsync(unresumable.Track.Id, ct);

        ctx.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleActorsAsync(ct));
        Assert.True(ctx.Kernel.Actors.IsTrackActiveInMemory(resumable.Track.Id));
        Assert.True(ctx.Kernel.Actors.IsTrackActiveInMemory(unresumable.Track.Id));
        Assert.All(ctx.AgentRuntime.OpenedSessions, provider => Assert.False(provider.Disposed));

        ctx.Time.Advance(TimeSpan.FromMinutes(5));
        var opened = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleActorsAsync(ct));
        Assert.False(ctx.Kernel.Actors.IsTrackActiveInMemory(resumable.Track.Id));
        Assert.True(ctx.Kernel.Actors.IsTrackActiveInMemory(unresumable.Track.Id));
        Assert.True(opened[0].Disposed);
        Assert.False(opened[1].Disposed);
    }

    [Fact]
    public async Task NonPositiveSessionIdleTimeoutDisablesSessionEviction()
    {
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            ActorIdleTimeout = TimeSpan.Zero,
            SessionIdleTimeout = TimeSpan.Zero,
            ActorEvictionSweepInterval = TimeSpan.Zero
        });
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await ctx.SendAndWaitForTurnAsync(snapshot.Track.LeadSessionId!, new("first", "first"), ct);
        ctx.Time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
        Assert.False(Assert.Single(ctx.AgentRuntime.OpenedSessions).Disposed);

        ctx.Kernel.McpSessionBridge.RevokeSession(snapshot.Track.LeadSessionId!);
        Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
        Assert.True(Assert.Single(ctx.AgentRuntime.OpenedSessions).Disposed);
    }

    [Fact]
    public async Task ArchiveAndUnarchiveDuringProviderStartupBlocksRevokedTokenActivation()
    {
        await using var ctx = new KernelTestContext(Options);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var sessionId = snapshot.Track.LeadSessionId!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.BeforeOpenAsync = () => { opening.TrySetResult(); return release.Task; };

        await ctx.Kernel.SendSessionMessageAsync(sessionId, new("hello", "hello"), Human, ct);
        try
        {
            await opening.Task.WaitAsync(ct);
            await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: true), Human, ct);
            await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: false), Human, ct);
        }
        finally { release.TrySetResult(); }

        await ctx.Kernel.WaitForTrackIdleAsync(trackId, ct);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Empty(provider.RecordedInputs);
        Assert.True(provider.Disposed);
    }

    [Fact]
    public async Task FollowUpAcceptedDuringUnconfirmedIdleShutdownSettlesCleanly()
    {
        await using var ctx = new KernelTestContext(Options);
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var sessionId = snapshot.Track.LeadSessionId!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "first"), ct);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.ShutdownConfirmed = false;
        provider.DisposeHandler = () => { disposing.TrySetResult(); return release.Task; };

        ctx.Time.Advance(IdleTimeout);
        var sweep = ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct);
        try
        {
            await disposing.Task.WaitAsync(ct);
            await ctx.Kernel.SendSessionMessageAsync(sessionId, new("follow-up", "follow-up"), Human, ct);
        }
        finally { release.TrySetResult(); }

        Assert.Equal(0, await sweep);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId, ct);
        var page = await ctx.Kernel.GetSessionItemsAsync(sessionId, new(), Human, ct);
        Assert.Equal("unknown", page.Session.Status);
        Assert.Equal("shutdown_unconfirmed", page.Session.UnknownReason);
        Assert.Equal("failed", page.LastTurn?.Status);
        provider.ShutdownConfirmed = true;
    }

    [Fact]
    public async Task ArchiveDuringReopenDisposalPreventsRegisteringNewTokenOrProvider()
    {
        await using var ctx = new KernelTestContext(Options);
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var sessionId = snapshot.Track.LeadSessionId!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "first"), ct);
        var first = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        await ctx.Kernel.UpdateSessionAsync(sessionId, new(Model: new("gpt-5.4")), Human, ct);

        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.DisposeHandler = () => { disposing.TrySetResult(); return release.Task; };
        await ctx.Kernel.SendSessionMessageAsync(sessionId, new("reopen", "reopen"), Human, ct);
        try
        {
            await disposing.Task.WaitAsync(ct);
            await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: true), Human, ct);
        }
        finally { release.TrySetResult(); }

        await ctx.Kernel.WaitForTrackIdleAsync(trackId, ct);
        Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Null(ctx.Kernel.McpSessionBridge.GetTokenForSession(sessionId));
    }

    [Fact]
    public async Task StaleCloseCannotDisposeReopenedGeneration()
    {
        var clock = new FakeManualTimeProvider();
        var provider = new ScriptableFakeAgentRuntime();
        var owner = new SessionRuntime(clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var spec = new HarnessLaunchSpec("codex", null, "/workspace/demo", null, null, new Dictionary<string, McpServerConfig>());
        var selection = SessionConfiguration.FromLaunch(spec);
        var first = await owner.EnsureOpenAsync(provider, selection, spec.Provider, _ => Task.FromResult(spec), _ => { },
            TimeSpan.FromSeconds(10), ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.BeforeOpenAsync = () => { entered.TrySetResult(); return release.Task; };
        var reopening = owner.EnsureOpenAsync(provider, selection, spec.Provider, _ => Task.FromResult(spec), _ => { },
            TimeSpan.FromSeconds(10), ct, forceReopen: true);
        Task<bool> staleClose;
        try
        {
            await entered.Task.WaitAsync(ct);
            staleClose = owner.CloseAsync(TimeSpan.FromSeconds(10), ct, shouldClose: () => true, expectedGeneration: first.Generation);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        finally { release.TrySetResult(); }
        var reopened = await reopening;
        Assert.False(await staleClose);
        Assert.True(Assert.IsType<ScriptableFakeAgentSession>(first.Agent).Disposed);
        Assert.False(Assert.IsType<ScriptableFakeAgentSession>(reopened.Agent).Disposed);
        Assert.Equal(first.Generation + 1, reopened.Generation);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), reopened.LastUsedAtMs);
        Assert.True(await owner.CloseAsync(TimeSpan.FromSeconds(10), ct, expectedGeneration: reopened.Generation));
    }

    [Fact]
    public async Task UnconfirmedIdleShutdownMarksSessionUnknown()
    {
        await using var ctx = new KernelTestContext(Options);
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await ctx.SendAndWaitForTurnAsync(snapshot.Track.LeadSessionId!, new("first", "first"), ct);
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        provider.ShutdownConfirmed = false;
        try
        {
            ctx.Time.Advance(IdleTimeout);
            Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
            var session = Assert.Single((await ctx.Store.LoadTrackAggregateAsync(trackId, ct))!.Sessions);
            Assert.Equal("unknown", session.Status);
            Assert.Equal("shutdown_unconfirmed", session.UnknownReason);
            Assert.Equal(provider.NativeThreadId, session.NativeThreadId);
            Assert.Equal(0, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
            Assert.Equal(1, provider.DisposeCalls);
        }
        finally { provider.ShutdownConfirmed = true; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveRevokesTokenAndClosesProviderAfterAnyActiveTurn(bool active)
    {
        await using var ctx = new KernelTestContext(Options);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var sessionId = snapshot.Track.LeadSessionId!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var enter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (active) ctx.AgentRuntime.TurnScript = (_, _, token) => CompleteAfterAsync(release.Task, enter, token);
        var turn = ctx.SendAndWaitForTurnAsync(sessionId, new("first", "first"), ct);
        ScriptableFakeAgentSession provider;
        try
        {
            if (active) await enter.Task.WaitAsync(ct);
            else await turn;
            provider = Assert.Single(ctx.AgentRuntime.OpenedSessions);
            Assert.NotNull(ctx.Kernel.McpSessionBridge.GetTokenForSession(sessionId));
            Assert.NotNull((await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: true), Human, ct)).ArchivedAtMs);
            Assert.Null(ctx.Kernel.McpSessionBridge.GetTokenForSession(sessionId));
            Assert.Equal(!active, provider.Disposed);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("completed", (await turn).Status);
        Assert.True(provider.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingDuringNativeCompactionRetiresProviderWhenCompactionFinishes(bool archive)
    {
        await using var ctx = new KernelTestContext(Options);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var sessionId = snapshot.Track.LeadSessionId!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var enter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.OnOpenedSession = provider =>
        {
            provider.SupportsNativeCompact = true;
            provider.CompactHandler = async (_, token) =>
            {
                enter.TrySetResult();
                await release.Task.WaitAsync(token);
                return new NativeCompactResult(null, "Compacted.");
            };
        };
        await ctx.SendAndWaitForTurnAsync(sessionId, new("first", "first"), ct);
        var handle = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var compaction = ctx.Kernel.ControlSessionAsync(sessionId, new("compact"), Human, ct);
        try
        {
            await enter.Task.WaitAsync(ct);
            if (archive)
                await ctx.Kernel.UpdateSessionAsync(sessionId, new(Archived: true), Human, ct);
            else
                await ctx.Kernel.PatchTrackAsync(trackId, new(Closed: true), ct);
            Assert.Null(ctx.Kernel.McpSessionBridge.GetTokenForSession(sessionId));
            Assert.False(handle.Disposed);
        }
        finally { release.TrySetResult(); }
        await compaction;
        await ctx.Kernel.WaitForTrackIdleAsync(trackId, ct);
        Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync(ct));
        Assert.True(handle.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrackCloseDisposesIdleProviderAndLetsLeadFinish(bool fromLead)
    {
        await using var ctx = new KernelTestContext(Options);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var leadId = snapshot.Track.LeadSessionId!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var child = (await ctx.Kernel.CreateSessionAsync(trackId, new(), "child", Human, ct)).Receipt.Session;
        await ctx.SendAndWaitForTurnAsync(child.Id, new("first", "first"), ct);
        var idle = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        var idleClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        idle.DisposeHandler = () => { idleClosed.TrySetResult(); return Task.CompletedTask; };
        var enter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, token) => CompleteAfterAsync(release.Task, enter, token);
        var turn = ctx.SendAndWaitForTurnAsync(leadId, new("close track", "close"), ct);
        ScriptableFakeAgentSession lead;
        try
        {
            await enter.Task.WaitAsync(ct);
            lead = ctx.AgentRuntime.OpenedSessions.Last();
            var token = ctx.Kernel.McpSessionBridge.GetTokenForSession(leadId)!;
            if (fromLead)
            {
                var caller = ctx.Kernel.McpSessionBridge.ResolveToken(token)!;
                var closed = await ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackClose, TestJson.Parse("{}"), ct);
                Assert.True(closed.GetProperty("closedAtMs").GetInt64() > 0);
            }
            else
                Assert.NotNull((await ctx.Kernel.PatchTrackAsync(trackId, new(Closed: true), ct)).ClosedAtMs);
            await idleClosed.Task.WaitAsync(ct);
            Assert.Null(ctx.Kernel.McpSessionBridge.GetTokenForSession(child.Id));
            Assert.Null(ctx.Kernel.McpSessionBridge.ResolveToken(token));
            Assert.False(lead.Disposed);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("completed", (await turn).Status);
        Assert.True(idle.Disposed);
        Assert.True(lead.Disposed);
        Assert.Null(ctx.Kernel.McpSessionBridge.GetTokenForSession(leadId));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> CompleteAfterAsync(Task release, TaskCompletionSource? entered,
        [EnumeratorCancellation] CancellationToken ct)
    {
        entered?.TrySetResult();
        await release.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }
}
