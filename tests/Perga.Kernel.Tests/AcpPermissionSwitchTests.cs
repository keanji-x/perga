using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class AcpPermissionSwitchTests
{
    private static readonly PermissionModeOption[] Modes =
        [new("plan", "Plan", "", "neutral", true), new("act", "Act", "", "warning")];

    [Theory]
    [InlineData("message")]
    [InlineData("wake")]
    [InlineData("compact")]
    public async Task SuccessfulReconciliationKeepsHandleAndUpdatesAppliedSettings(string operation)
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom", PermissionMode: "plan"));
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.AvailablePermissionModes = Modes;
        original.ConfigureHandler = configuration => new SessionConfigurationOutcome.Applied(new(configuration));
        original.SupportsNativeCompact = true;
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "act");
        Assert.Empty(original.Configurations);
        if (operation == "compact") await ctx.Kernel.CompactSessionAsync(id);
        else if (operation == "wake")
        {
            await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(snap.Track.Id, "mail", "mail:test", "notice"), default);
            await ctx.Kernel.Actors.GetOrActivate(snap.Track.Id).PokeLeadAsync();
            await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(10));
        }
        else await ctx.SendAndWaitForTurnAsync(id, new("next", "next"));
        Assert.Equal("act", Assert.Single(original.Configurations).Permission.ValueOrNull);
        await ctx.SendAndWaitForTurnAsync(id, new("again", "again"));
        Assert.Single(original.Configurations);
        Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.False(original.Disposed);
    }

    [Theory]
    [InlineData("codex", "full")]
    [InlineData("claude", "plan")]
    public async Task NativePermissionPutRetainsIdleProcessUntilNextDispatch(string provider, string mode)
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: provider));
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        await ctx.Kernel.SetSessionPermissionModeAsync(id, mode);
        Assert.False(original.Disposed);
        await ctx.SendAndWaitForTurnAsync(id, new("next", "next"));
        Assert.True(original.Disposed);
        Assert.Equal(mode, ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.PermissionMode);
        Assert.Equal(original.NativeThreadId, ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.ResumeNativeThreadId);
        Assert.Equal(0, ctx.AgentRuntime.ProviderProbeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationFailureRemovesHandleAndNeverDispatchesOldSettings(bool throws)
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom"));
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.AvailablePermissionModes = Modes;
        original.ConfigureHandler = _ => throws ? throw new ArgumentException("invalid mode") : new SessionConfigurationOutcome.RequiresReopen("mode");
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "act");
        ctx.AgentRuntime.OnBeforeOpenSession = _ => throw new IOException("open failed");
        Assert.Equal("failed", (await ctx.SendAndWaitForTurnAsync(id, new("failure", "failure"))).Status);
        Assert.True(original.Disposed);
        Assert.Single(original.RecordedInputs);
        // The old catalogue must disappear with the agent, allowing cold correction.
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "new-catalogue-mode");
        ctx.AgentRuntime.OnBeforeOpenSession = null;
        await ctx.SendAndWaitForTurnAsync(id, new("retry", "retry"));
        Assert.Equal("new-catalogue-mode", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.PermissionMode);
        Assert.Equal(2, ctx.AgentRuntime.OpenedSessions.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HotSwitchValidatesPersistsAndDefersReconciliationUntilNextTurn(bool active, bool reconcile)
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom"));
        var id = snap.Track.LeadSessionId!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            if (active) await release.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.ThreadBound(session.NativeThreadId!);
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        var first = ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!active) await first;
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.AvailablePermissionModes = Modes;
        if (reconcile) original.ConfigureHandler = configuration => new SessionConfigurationOutcome.Applied(new(configuration));
        var probes = ctx.AgentRuntime.ProviderProbeCalls;
        try
        {
            await ctx.Kernel.SetSessionPermissionModeAsync(id, "act");
            await ctx.Kernel.SetSessionPermissionModeAsync(id, "plan");
            await ctx.Kernel.SetSessionPermissionModeAsync(id, " act ");
            var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.SetSessionPermissionModeAsync(id, "invented"));
            Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
            Assert.Equal("act", (await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single().PermissionMode);
            Assert.Equal("act", (await ctx.Store.LoadTrackAggregateAsync(snap.Track.Id, default))!.Sessions.Single().PermissionMode);
            Assert.Equal(probes, ctx.AgentRuntime.ProviderProbeCalls);
            Assert.False(original.Disposed);
            Assert.False(original.CancelCalled);
            Assert.Single(ctx.AgentRuntime.OpenedSessions);
            if (active) await ctx.Kernel.AcceptMessageAsync(id, new("queued", "queued", QueueIfBusy: true), default);
        }
        finally { release.TrySetResult(); }
        await first;
        await ctx.SendAndWaitForTurnAsync(id, new("next", "next"));
        Assert.Equal(!reconcile, original.Disposed);
        Assert.Equal(reconcile ? 1 : 2, ctx.AgentRuntime.OpenedSessions.Count);
        var next = ctx.AgentRuntime.OpenedSessions.Last();
        if (reconcile)
        {
            Assert.Equal("act", Assert.Single(original.Configurations).Permission.ValueOrNull);
            Assert.Equal(active ? 3 : 2, original.RecordedInputs.Count);
        }
        else
        {
            Assert.Equal("act", next.LaunchSpec.PermissionMode);
            Assert.Equal(original.NativeThreadId, next.LaunchSpec.ResumeNativeThreadId);
            Assert.Single(original.RecordedInputs);
            Assert.Equal(active ? 2 : 1, next.RecordedInputs.Count);
        }
    }

    [Theory]
    [InlineData("message")]
    [InlineData("wake")]
    [InlineData("compact")]
    public async Task NextOperationAppliesModeAndFailedOpenNeverDispatchesOldMode(string operation)
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:antigravity"));
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.AvailablePermissionModes = [..Modes, new("bypassPermissions", "Full access", "", "danger")];
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "bypassPermissions");
        Assert.False(original.Disposed);
        ctx.AgentRuntime.OnBeforeOpenSession = spec =>
        {
            Assert.Equal("bypassPermissions", spec.PermissionMode);
            Assert.Equal(original.NativeThreadId, spec.ResumeNativeThreadId);
            throw new InvalidOperationException("new mode failed");
        };
        if (operation == "message")
            Assert.Equal("failed", (await ctx.SendAndWaitForTurnAsync(id, new("rejected", "rejected"))).Status);
        else if (operation == "compact")
            await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Kernel.CompactSessionAsync(id));
        else
        {
            await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(snap.Track.Id, "mail", "mail:test", "notice"), default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Kernel.Actors.GetOrActivate(snap.Track.Id).PokeLeadAsync());
        }
        Assert.True(original.Disposed);
        Assert.Single(original.RecordedInputs);
        Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal("bypassPermissions", (await ctx.Kernel.ListSessionsAsync(snap.Track.Id)).Single().PermissionMode);
        ctx.AgentRuntime.OnBeforeOpenSession = null;
        await ctx.SendAndWaitForTurnAsync(id, new("retry", "retry"));
        Assert.Equal("bypassPermissions", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.PermissionMode);
        Assert.Equal(0, ctx.AgentRuntime.ProviderProbeCalls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("provider")]
    [InlineData("session")]
    [InlineData("cwd")]
    public async Task ColdAndHotSettingsNeverProbeOrBlockDrain(string change)
    {
        await using var ctx = new KernelTestContext(new KernelOptions
        {
            ActorEvictionSweepInterval = TimeSpan.Zero
        });
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom"));
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.AvailablePermissionModes = Modes;
        switch (change)
        {
            case "missing": original.AvailablePermissionModes = []; break;
            case "provider":
                await ctx.Kernel.PatchSessionAsync(id, new(Provider: "acp:other"));
                Assert.True(original.Disposed);
                break;
            case "session":
                id = (await ctx.Kernel.CreateSubagentSessionAsync(snap.Track.Id, new(Provider: "acp:custom"))).Id;
                break;
            case "cwd":
                ctx.DirectDriver.VerifyMountOnAccess = true;
                ctx.DirectDriver.OverrideMountedMergedCwd = "/changed";
                break;
        }
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        try
        {
            await ctx.Kernel.SetSessionPermissionModeAsync(id, "plan");
            Assert.Equal(0, ctx.AgentRuntime.ProviderProbeCalls);
        }
        finally { ctx.Kernel.DispatchAdmission.SetDraining(false); }
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "plan");
        Assert.Equal(0, ctx.AgentRuntime.ProviderProbeCalls);
    }

    [Fact]
    public async Task HotSwitchDuringDrainDefersReopenForQueuedInputAndWake()
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom"));
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.AvailablePermissionModes = Modes;
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        try
        {
            await ctx.Kernel.AcceptMessageAsync(id, new("queued", "queued"), default);
            await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(snap.Track.Id, "mail", "mail:test", "notice"), default);
            await ctx.Kernel.SetSessionPermissionModeAsync(id, "plan");
            await ctx.Kernel.SetSessionPermissionModeAsync(id, "act");
            Assert.False(original.Disposed);
            Assert.Single(original.RecordedInputs);
            Assert.Equal(0, ctx.AgentRuntime.ProviderProbeCalls);
        }
        finally { ctx.Kernel.DispatchAdmission.SetDraining(false); }
        await ctx.Kernel.Actors.GetOrActivate(snap.Track.Id).PokeLeadAsync();
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(original.Disposed);
        var next = ctx.AgentRuntime.OpenedSessions.Last();
        Assert.Equal("act", next.LaunchSpec.PermissionMode);
        Assert.Equal(2, ctx.AgentRuntime.OpenedSessions.Count);
        Assert.Equal(2, next.RecordedInputs.Count);
    }

    [Fact]
    public async Task ResumeRegistersReplacementCatalogueAndAppliesDesiredMode()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.SupportsSessionResume = true;
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom"));
        var id = snap.Track.LeadSessionId!;
        ctx.AgentRuntime.TurnScript = Unknown;
        static async IAsyncEnumerable<UnifiedAgentEvent> Unknown(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new UnifiedAgentEvent.ThreadBound(session.NativeThreadId!);
            yield return new UnifiedAgentEvent.TextDelta("incomplete");
        }
        var turn = await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        Assert.Equal("unknown", turn.Status);
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.SupportsScopedClose = true;
        original.AvailablePermissionModes = Modes;
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "act");
        Assert.False(original.Disposed);
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        original.CloseSessionHandler = ct => { closing.TrySetResult(); return releaseClose.Task.WaitAsync(ct); };
        var resume = ctx.Kernel.RecoverSessionAsync(id, new("reconnect"), default);
        await closing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await ctx.Kernel.SetSessionPermissionModeAsync(id, "during-hold");
            Assert.False(original.Disposed);
        }
        finally { releaseClose.TrySetResult(true); }
        await resume;
        Assert.True(original.Disposed);
        var restored = ctx.AgentRuntime.OpenedSessions.Last();
        Assert.Equal("during-hold", restored.LaunchSpec.PermissionMode);
        Assert.Equal(original.NativeThreadId, restored.LaunchSpec.ResumeNativeThreadId);
        Assert.Empty(restored.RecordedInputs);
        restored.AvailablePermissionModes = [new("restored-only", "Restored", "", "neutral")];
        var stale = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.SetSessionPermissionModeAsync(id, "plan"));
        Assert.Equal(KernelErrorCode.InvalidArgument, stale.Code);
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "restored-only");
        Assert.False(restored.Disposed);
        Assert.Equal(0, ctx.AgentRuntime.ProviderProbeCalls);
    }

    [Fact]
    public async Task SwitchingBackToOpenModeAvoidsReopenAndManualCompactUsesDesiredMode()
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom", PermissionMode: "plan"));
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.AvailablePermissionModes = Modes;
        var probes = ctx.AgentRuntime.ProviderProbeCalls;
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "act");
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "plan");
        await ctx.SendAndWaitForTurnAsync(id, new("same mode", "same"));
        Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.False(original.Disposed);
        await ctx.Kernel.SetSessionPermissionModeAsync(id, "act");
        await ctx.Kernel.CompactSessionAsync(id);
        Assert.True(original.Disposed);
        Assert.All(ctx.AgentRuntime.OpenedSessions.Skip(1), session => Assert.Equal("act", session.LaunchSpec.PermissionMode));
        Assert.Equal(probes, ctx.AgentRuntime.ProviderProbeCalls);
    }
}
