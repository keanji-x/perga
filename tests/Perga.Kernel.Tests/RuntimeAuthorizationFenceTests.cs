using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class RuntimeAuthorizationFenceTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TaskCancellationRechecksAuthorizationInBothBranches(bool flow, bool policyChanges)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        const string key = "pending-task";
        if (flow)
            await FlowFixture.AddAsync(ctx.Kernel, trackId, new FlowNodeSpecDto(key, new("run", Cmd: "later"),
                When: new(At: "2099-01-01T00:00:00Z")));
        else
        {
            await ctx.Store.UpsertTaskAsync(new("tsk_pending", trackId, "block", key, 1, "agent", null,
                AccessMode.ReadOnly, false, [], "later", null, null, null,
                TaskRunStatus.Pending, null, SemanticVerdict.None, null, 1, 1), null, default);
            await ctx.Kernel.Actors.RemoveAndDisposeAsync(trackId);
        }
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        var authorization = await ctx.AuthorizeAsync(caller);
        if (policyChanges)
            await ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackSet,
                TestJson.FromObject(new { source = "export default {id:'fresh',title:'Fresh'};" }));
        else
            ctx.Kernel.McpSessionBridge.RevokeSession(caller.SessionId, caller.RuntimeBindingId);

        var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.CancelTaskAsync(trackId, caller.SessionId,
            key, authorization: authorization));
        Assert.Equal(policyChanges ? KernelErrorCode.PolicyDenied : KernelErrorCode.Conflict, error.Code);
        Assert.Equal(TaskRunStatus.Pending, Assert.Single(await ctx.Kernel.ListTasksAsync(trackId)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MailRechecksSenderAtRecipientsCommit(bool policyChanges)
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, from) = await ctx.CreateDefaultTrackAsync();
        var (_, to) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(from.Track.LeadSessionId!);
        var authorization = await ctx.AuthorizeAsync(caller);
        var sender = ctx.Kernel.Actors.GetOrActivate(from.Track.Id);
        var recipient = ctx.Kernel.Actors.GetOrActivate(to.Track.Id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = recipient.EnqueueAsync(_ => release.Task, ct);
        var send = ctx.Kernel.SendMailAsync(from.Track.Id, caller.SessionId, to.Track.Id, "Subject", "Body", ct,
            authorization: authorization);
        try
        {
            await sender.EnqueueAsync(_ => Task.CompletedTask, ct);
            if (policyChanges)
                await ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackSet,
                    TestJson.FromObject(new { source = "export default {id:'fresh',title:'Fresh'};" }), ct);
            else
                await ctx.Kernel.ControlSessionAsync(caller.SessionId, new("reset_context"), Human, ct);
        }
        finally { release.TrySetResult(); }

        await blocked;
        var error = await Assert.ThrowsAsync<KernelException>(() => send.WaitAsync(ct));
        Assert.Equal(policyChanges ? KernelErrorCode.PolicyDenied : KernelErrorCode.Conflict, error.Code);
        Assert.Empty(await ctx.Kernel.ListMailsAsync(to.Track.Id, ct: ct));
    }

    [Fact]
    public async Task StopRejectsWriteAlreadyQueuedAheadOfStoppingPersistence()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var id = snapshot.Track.LeadSessionId!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, token) => HoldTurnAsync(streaming, finishStream.Task, token);
        await ctx.Kernel.SendSessionMessageAsync(id, new("stay active", "turn"), Human, ct);
        await streaming.Task.WaitAsync(ct);
        var active = (await ctx.Kernel.GetSessionItemsAsync(id, new(), Human, ct)).ActiveTurn!;
        var authorization = await ctx.AuthorizeAsync(ctx.RuntimeCaller(id), ct);
        var actor = ctx.Kernel.Actors.GetOrActivate(snapshot.Track.Id);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = actor.EnqueueAsync(_ => release.Task, ct);
        var stopping = ctx.Kernel.ControlSessionAsync(id, new("stop", TurnId: active.Id), Human, ct);
        var writing = actor.RenameTrackBySessionAsync(id, "Unauthorized", ct, authorization);
        release.TrySetResult();
        try
        {
            await blocked;
            var error = await Assert.ThrowsAsync<KernelException>(() => writing.WaitAsync(ct));
            Assert.Equal(KernelErrorCode.Conflict, error.Code);
            Assert.Equal(snapshot.Track.Title, (await ctx.Kernel.GetTrackAggregateAsync(snapshot.Track.Id, ct)).Track.Title);
        }
        finally { finishStream.TrySetResult(); }
        await stopping;
    }

    [Fact]
    public async Task StopDuringDecisionAuditPreventsPolicyApprovalDelivery()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var id = snapshot.Track.LeadSessionId!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var caller = await ctx.OpenMcpCallerAsync(id, ct);
        await ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackSet, TestJson.FromObject(new
        {
            source = "export default {id:'ask',title:'Ask',before:a=>a.toolName==='track_rename'?AskHuman('Rename?'):Allow()};"
        }), ct);

        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, token) => HoldTurnAsync(streaming, finishStream.Task, token);
        await ctx.Kernel.SendSessionMessageAsync(id, new("stay active", "active"), Human, ct);
        await streaming.Task.WaitAsync(ct);
        var active = (await ctx.Kernel.GetSessionItemsAsync(id, new(), Human, ct)).ActiveTurn!;
        var rename = ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.TrackRename,
            TestJson.FromObject(new { title = "Not delivered" }), ct);
        var approvals = (await ctx.Kernel.GetTrackAggregateAsync(trackId, ct)).PendingApprovals;
        while (approvals.Count == 0)
        {
            await Task.Delay(5, ct);
            approvals = (await ctx.Kernel.GetTrackAggregateAsync(trackId, ct)).PendingApprovals;
        }
        var approval = Assert.Single(approvals);
        Assert.Equal(active.Id, approval.TurnId);

        var auditing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishAudit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Store.BeforeEventAppend = evt =>
        {
            if (evt.Kind != KernelEventKinds.ApprovalDecided) return Task.CompletedTask;
            auditing.TrySetResult();
            return finishAudit.Task.WaitAsync(ct);
        };
        var response = ctx.Kernel.RespondInteractionAsync(id, approval.Id,
            new(ExecutionId: approval.ExecutionId, OptionId: "approve"), Human, ct);
        try
        {
            await auditing.Task.WaitAsync(ct);
            await ctx.Kernel.ControlSessionAsync(id, new("stop", TurnId: active.Id), Human, ct);
            var error = await Assert.ThrowsAsync<KernelException>(() => rename.WaitAsync(ct));
            Assert.Equal(KernelErrorCode.Conflict, error.Code);
        }
        finally
        {
            finishAudit.TrySetResult();
            finishStream.TrySetResult();
        }
        var expired = await Assert.ThrowsAsync<KernelException>(() => response.WaitAsync(ct));
        Assert.Equal(KernelErrorCode.Conflict, expired.Code);
        Assert.Equal(snapshot.Track.Title, (await ctx.Kernel.GetTrackAggregateAsync(trackId, ct)).Track.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactionRechecksCallerBeforePersistingNativeAndHandoverResults(bool native)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!, ct);
        var child = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(), "child", Human, ct)).Receipt.Session;
        await ctx.OpenMcpCallerAsync(child.Id, ct);
        var provider = ctx.AgentRuntime.OpenedSessions.Single(session => session.LaunchSpec.StateKey == child.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.SupportsNativeCompact = native;
        provider.CompactHandler = async (_, token) =>
        {
            entered.SetResult();
            await finish.Task.WaitAsync(token);
            return new(null, "summary");
        };
        ctx.AgentRuntime.TurnScript = (_, _, token) => HoldTurnAsync(entered, finish.Task, token);

        var pending = ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.SessionControl,
            TestJson.FromObject(new { sessionId = child.Id, action = "compact" }), ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            await ctx.Kernel.ControlSessionAsync(caller.SessionId, new("reset_context"), Human, ct);
        }
        finally { finish.TrySetResult(); }
        var error = await Assert.ThrowsAsync<KernelException>(() => pending.WaitAsync(ct));
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
        Assert.Single(await ctx.Store.ListSessionTurnsAsync(child.Id, ct));
        Assert.Equal(child.Epoch, (await ctx.Kernel.GetSessionItemsAsync(child.Id, new(), Human, ct)).Session.Epoch);
    }

    [Fact]
    public async Task QueuedSteerChecksCallerAgainAndHistoricalTurnIdReturnsConflict()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!, ct);
        var child = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(), "child", Human, ct)).Receipt.Session;
        var previous = await ctx.SendAndWaitForTurnAsync(child.Id, new("first", "first"), ct);
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, token) => HoldTurnAsync(streaming, finishStream.Task, token);
        await ctx.Kernel.SendSessionMessageAsync(child.Id, new("next", "next"), Human, ct);
        await streaming.Task.WaitAsync(ct);
        var active = (await ctx.Kernel.GetSessionItemsAsync(child.Id, new(), Human, ct)).ActiveTurn!;
        var stale = await Assert.ThrowsAsync<KernelException>(() => SendSteer("stale", previous.Id));
        Assert.Equal(KernelErrorCode.Conflict, stale.Code);
        var provider = ctx.AgentRuntime.OpenedSessions.Single(session => session.LaunchSpec.StateKey == child.Id);
        var steeringEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSteering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.SteerHandler = async (_, token) =>
        {
            steeringEntered.TrySetResult();
            await finishSteering.Task.WaitAsync(token);
            return SteerDelivery.Accepted;
        };
        var first = SendSteer("first-steer", active.Id);
        try
        {
            await steeringEntered.Task.WaitAsync(ct);
            var authorization = await ctx.AuthorizeAsync(caller, ct);
            var blocked = ctx.Kernel.SendSessionMessageAsync(child.Id, new("second-steer", "second-steer",
                Mode: "steer", TurnId: active.Id), authorization, ct);
            await ctx.Kernel.Actors.GetOrActivate(snapshot.Track.Id).EnqueueAsync(_ => Task.CompletedTask, ct);
            Assert.False(blocked.IsCompleted);
            ctx.Kernel.McpSessionBridge.RevokeSession(caller.SessionId, caller.RuntimeBindingId);
            finishSteering.TrySetResult();
            var error = await Assert.ThrowsAsync<KernelException>(() => blocked.WaitAsync(ct));
            Assert.Equal(KernelErrorCode.Conflict, error.Code);
            await first;
            Assert.Equal("first-steer", Assert.Single(provider.SteeredMessages));
        }
        finally
        {
            finishSteering.TrySetResult();
            finishStream.TrySetResult();
        }

        Task<System.Text.Json.JsonElement> SendSteer(string text, string turnId) => ctx.Kernel.InvokeKernelMcpToolAsync(
            caller, KernelMcpToolNames.SessionSend, TestJson.FromObject(new
            { sessionId = child.Id, content = text, requestKey = text, mode = "steer", turnId }), ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteerCannotBeWrittenAfterCallerLosesAuthorityInProviderQueue(bool stops)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!, ct);
        var child = (await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id, new(), "child", Human, ct)).Receipt.Session;
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, token) => HoldTurnAsync(streaming, finishStream.Task, token);
        await ctx.Kernel.SendSessionMessageAsync(child.Id, new("work", "start"), Human, ct);
        await streaming.Task.WaitAsync(ct);
        string? callerTurnId = null;
        if (stops)
        {
            var callerStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = Assert.Single(ctx.AgentRuntime.OpenedSessions, session => session.LaunchSpec.StateKey == caller.SessionId);
            source.RequiresTerminalStop = true;
            source.OnCancelTurn = () => callerStopped.TrySetResult();
            ctx.AgentRuntime.TurnScript = (_, _, token) => HoldCallerAsync(token);
            async IAsyncEnumerable<UnifiedAgentEvent> HoldCallerAsync([EnumeratorCancellation] CancellationToken token)
            {
                callerStream.TrySetResult();
                await Task.WhenAny(callerStopped.Task, finishStream.Task).WaitAsync(token);
                yield return new UnifiedAgentEvent.TurnInterrupted();
            }
            await ctx.Kernel.SendSessionMessageAsync(caller.SessionId, new("caller work", "caller-work"), Human, ct);
            await callerStream.Task.WaitAsync(ct);
            callerTurnId = (await ctx.Kernel.GetSessionItemsAsync(caller.SessionId, new(), Human, ct)).ActiveTurn!.Id;
        }
        var active = (await ctx.Kernel.GetSessionItemsAsync(child.Id, new(), Human, ct)).ActiveTurn!;
        var provider = Assert.Single(ctx.AgentRuntime.OpenedSessions, session => session.LaunchSpec.StateKey == child.Id);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permitStdin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.WaitForSteerWrite = token =>
        {
            queued.TrySetResult();
            return permitStdin.Task.WaitAsync(token);
        };
        var steer = ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.SessionSend, TestJson.FromObject(new
        {
            sessionId = child.Id, content = "queued steer", requestKey = "queued-steer", mode = "steer", turnId = active.Id
        }), ct);
        try
        {
            await queued.Task.WaitAsync(ct);
            if (stops)
            {
                await ctx.Kernel.ControlSessionAsync(caller.SessionId, new("stop", TurnId: callerTurnId), Human, ct);
                while ((await ctx.Kernel.GetSessionItemsAsync(caller.SessionId, new(), Human, ct)).ActiveTurn is not null)
                    await Task.Delay(5, ct);
                Assert.Equal(caller.Token, ctx.RuntimeCaller(caller.SessionId).Token);
            }
            else
                ctx.Kernel.McpSessionBridge.RevokeSession(caller.SessionId, caller.RuntimeBindingId);
            permitStdin.TrySetResult();
            var error = await Assert.ThrowsAsync<KernelException>(() => steer.WaitAsync(ct));
            Assert.Equal(KernelErrorCode.Conflict, error.Code);
            Assert.Empty(provider.SteeredMessages);
        }
        finally
        {
            permitStdin.TrySetResult();
            finishStream.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AskPublicationFailureDoesNotLeaveAnIdleEvictionWaiter(bool canceled)
    {
        var idleTimeout = TimeSpan.FromMinutes(1);
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            SessionIdleTimeout = idleTimeout, ActorEvictionSweepInterval = TimeSpan.Zero
        });
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        ctx.Store.BeforeEventPublication = events =>
        {
            if (!events.Any(evt => evt.Kind == KernelEventKinds.AskChanged)) return Task.CompletedTask;
            if (!canceled) throw new IOException("Ask publication failed.");
            request.Cancel();
            return Task.FromCanceled(request.Token);
        };
        var ask = ctx.Kernel.InvokeKernelMcpToolAsync(caller, KernelMcpToolNames.UserAsk,
            TestJson.FromObject(new { questions = new[] { new { question = "Continue?", options = new[] { "Yes", "No" } } } }), request.Token);
        if (canceled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask);
        else await Assert.ThrowsAsync<IOException>(() => ask);
        ctx.Store.BeforeEventPublication = null;
        Assert.Single((await ctx.Kernel.GetTrackAggregateAsync(snapshot.Track.Id)).OpenAsks);
        ctx.Time.Advance(idleTimeout);
        Assert.Equal(1, await ctx.Kernel.Actors.EvictIdleSessionRuntimesAsync());
        Assert.True(Assert.Single(ctx.AgentRuntime.OpenedSessions).Disposed);
    }

    [Fact]
    public async Task CanceledLaunchCannotRegisterAfterItsCleanupRan()
    {
        var bridge = new StartupBridge();
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            SessionStartupTimeout = TimeSpan.FromMilliseconds(500), ActorEvictionSweepInterval = TimeSpan.Zero
        }, mcpSessionBridge: bridge);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var id = snapshot.Track.LeadSessionId!;
        var mounted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.DirectDriver.VerifyMountOnAccess = true;
        ctx.DirectDriver.BeforeEnsureMounted = async _ =>
        {
            mounted.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var receipt = await ctx.Kernel.SendSessionMessageAsync(id, new("wait on mount", "mount"), Human, ct);
        try
        {
            await mounted.Task.WaitAsync(ct);
            await bridge.CleanedUp.Task.WaitAsync(ct);
        }
        finally { release.TrySetResult(); }

        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id, ct);
        Assert.Equal(0, bridge.Registrations);
        Assert.Null(bridge.GetTokenForSession(id));
        Assert.Empty(ctx.AgentRuntime.OpenedSessions);
        Assert.NotEqual("running", (await ctx.Store.ListSessionTurnsAsync(id, ct)).Single(turn => turn.Id == receipt.TurnId).Status);
    }

    private sealed class StartupBridge : IMcpSessionBridge
    {
        private readonly InMemoryMcpSessionBridge _inner = new();
        public int Registrations { get; private set; }
        public TaskCompletionSource CleanedUp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public McpSessionRegistration RegisterSession(string trackId, string sessionId, bool isLead, string? boundTaskId,
            int? taskRunCount, IReadOnlyList<string> enabledPlugins, string? runtimeBindingId = null, string? baseUriOverride = null)
        {
            Registrations++;
            return _inner.RegisterSession(trackId, sessionId, isLead, boundTaskId, taskRunCount, enabledPlugins, runtimeBindingId, baseUriOverride);
        }

        public void RevokeSession(string sessionId, string? runtimeBindingId = null)
        {
            _inner.RevokeSession(sessionId, runtimeBindingId);
            if (runtimeBindingId is not null) CleanedUp.TrySetResult();
        }

        public McpSessionPrincipal? ResolveToken(string token) => _inner.ResolveToken(token);
        public string? GetTokenForSession(string sessionId) => _inner.GetTokenForSession(sessionId);
        public void UpdateLeadStatus(string trackId, string? newLeadSessionId) => _inner.UpdateLeadStatus(trackId, newLeadSessionId);
        public void UpdateTrackPlugins(string trackId, IReadOnlyList<string> enabledPlugins) => _inner.UpdateTrackPlugins(trackId, enabledPlugins);
        public void RevokeTrack(string trackId) => _inner.RevokeTrack(trackId);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> HoldTurnAsync(TaskCompletionSource streaming,
        Task finish, [EnumeratorCancellation] CancellationToken ct)
    {
        streaming.TrySetResult();
        await finish.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TextDelta("summary");
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }
}
