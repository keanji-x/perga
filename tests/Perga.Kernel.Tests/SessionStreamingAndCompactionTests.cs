using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests;

public sealed class SessionStreamingAndCompactionTests
{
    [Fact]
    public async Task AutomaticHandoverReleasesDurableDispatchThenDrainsFollowupAndWake()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession agent, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) { entered.SetResult(); await release.Task.WaitAsync(ct); }
            yield return new UnifiedAgentEvent.ThreadBound(agent.NativeThreadId!);
            yield return new UnifiedAgentEvent.TextDelta("summary");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(call == 1 ? 85_000 : 100, 0, 0, 100_000));
        }
        ctx.AgentRuntime.TurnScript = Stream;
        await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "first", RequestKey: "first"), new InteropPrincipal.Human(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "followup", RequestKey: "followup", Mode: "queue"), new InteropPrincipal.Human(), default);
        await ctx.Store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(snap.Track.Id, "mail", "mail:handover", "wake"), default);
        release.SetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, calls); // original, summary, queued followup, wake
        Assert.All((await ctx.Store.TrackWakes.ListAsync(snap.Track.Id, default)), wake => Assert.Equal("done", wake.State));
        Assert.Equal(2, (await ctx.Kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default)).Session.Epoch);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedInputConfigurationFailureSettlesWithoutDispatchOrReopen(bool unsupported)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "hold", RequestKey: "configuration-hold"), new InteropPrincipal.Human(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.ConfigureHandler = _ => unsupported
            ? new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Effort, "unavailable")
            : new SessionConfigurationOutcome.Rejected(ConfigurationFailureCode.InvalidAcknowledgement, "partial failure", new("B", null, false));
        try
        {
            await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("B"), ReasoningEffort: new("high")), new InteropPrincipal.Human(), default);
            Assert.Empty(original.Configurations);
            await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "blocked", RequestKey: "configuration-queued", Mode: "queue"), new InteropPrincipal.Human(), default);
        }
        finally { release.TrySetResult(); }
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal("hold", Assert.Single(original.RecordedInputs).Prompt);
        Assert.True(original.Disposed);
        var failed = ctx.Store.GetTurnsForSession(id).Last();
        Assert.Equal("failed", failed.Status);
        var desired = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single();
        Assert.Equal("B", desired.Model);
        Assert.Equal("high", desired.ReasoningEffort);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermissionReopenCarriesDefaultModelAndExplicitEffortWithOriginalIdentity(bool afterCompaction)
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers = [new("acp:custom", true, "1", null, [], PermissionModes:
            [new("plan", "Plan", "", IsDefault: true), new("execute", "Execute", "")])];
        var area = await ctx.Kernel.CreateAreaAsync(new("Configuration modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom", PermissionMode: "plan"));
        var id = snap.Track.LeadSessionId!;
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new(null), ReasoningEffort: new("high")), new InteropPrincipal.Human(), default);
        await ctx.SendAndWaitForTurnAsync(id, new("first", "mode-effort-first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        if (afterCompaction)
        {
            await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
            original = ctx.AgentRuntime.OpenedSessions.Last();
            Assert.Null(((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single().NativeThreadId);
        }
        var opens = ctx.AgentRuntime.OpenedSessions.Count;
        await ctx.Kernel.UpdateSessionAsync(id, new(PermissionMode: new("execute")), new InteropPrincipal.Human(), default);
        await ctx.SendAndWaitForTurnAsync(id, new("next", "mode-effort-next"));
        Assert.Equal(opens + 1, ctx.AgentRuntime.OpenedSessions.Count);
        var replacement = ctx.AgentRuntime.OpenedSessions.Last();
        Assert.True(original.Disposed);
        Assert.Null(replacement.LaunchSpec.Model);
        Assert.Equal("high", replacement.LaunchSpec.ReasoningEffort);
        Assert.Equal("execute", replacement.LaunchSpec.PermissionMode);
        Assert.Equal(original.NativeThreadId, replacement.LaunchSpec.ResumeNativeThreadId);
        Assert.Equal(SessionConfiguration.FromNullable(null, "high", "execute"), Assert.Single(original.Configurations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationFailureDoesNotReopenOrDispatchAndKeepsDesiredSelection(bool unsupported)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "configuration-first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.ConfigureHandler = _ => unsupported
            ? new SessionConfigurationOutcome.Unsupported(ConfigurationDimension.Effort, "effort unavailable")
            : new SessionConfigurationOutcome.Rejected(ConfigurationFailureCode.InvalidAcknowledgement, "effort rejected",
                new("new-model", null, false));
        var inputs = original.RecordedInputs.Count;
        var error = await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.UpdateSessionAsync(id, new(Model: new("new-model"), ReasoningEffort: new("high")), new InteropPrincipal.Human(), default));
        Assert.Equal(unsupported ? KernelErrorCode.InvalidArgument : KernelErrorCode.PreconditionFailed, error.Code);
        Assert.Single(ctx.AgentRuntime.OpenedSessions);
        Assert.True(original.Disposed);
        Assert.Equal(inputs, original.RecordedInputs.Count);
        var desired = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(session => session.Id == id);
        Assert.Equal("new-model", desired.Model);
        Assert.Equal("high", desired.ReasoningEffort);
        Assert.Null(desired.ContextUsage);
        Assert.Equal(SessionConfiguration.FromNullable("new-model", "high", desired.PermissionMode), Assert.Single(original.Configurations));
    }

    [Fact]
    public async Task UnconfirmedLaunchCannotDispatch()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        ctx.AgentRuntime.OnOpenedSession = session => session.ConfigurationState = null;
        await ctx.Kernel.SendSessionMessageAsync(id, new(Content: "must not dispatch", RequestKey: "unconfirmed-launch"), new InteropPrincipal.Human(), default);
        await ctx.Kernel.WaitForTrackIdleAsync(snap.Track.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(ctx.AgentRuntime.OpenedSessions);
        Assert.All(ctx.AgentRuntime.OpenedSessions, session =>
        {
            Assert.Empty(session.RecordedInputs);
            Assert.True(session.Disposed);
        });
    }

    [Fact]
    public async Task ColdPermissionUpdatesPersistWithoutDiscoveryMountOrAdmission()
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        ctx.Kernel.DispatchAdmission.SetDraining(true);
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom", PermissionMode: " unknown "));
        var sub = (await ctx.Kernel.CreateSessionAsync(snap.Track.Id, new(Provider: "acp:custom", PermissionMode: "unknown"), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default)).Receipt.Session;
        await ctx.Kernel.UpdateSessionAsync(sub.Id, new(PermissionMode: new(" another ")), new InteropPrincipal.Human(), default);
        Assert.Equal("unknown", ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.IsLead).PermissionMode);
        Assert.Equal("another", ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == sub.Id).PermissionMode);
        Assert.Empty(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal(0, ctx.AgentRuntime.ProviderProbeCalls);
        Assert.Equal(0, ctx.Kernel.DispatchAdmission.ActiveDispatches);
        ctx.Kernel.DispatchAdmission.SetDraining(false);
    }

    [Fact]
    public async Task RuntimePermissionModesApplyBeforeBootstrapToTracksSessionsAndLaunch()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers = [new("acp:custom", true, "1", null, [], PermissionModes:
            [new("inspect", "Inspect", "", "neutral"), new("act", "Act", "", "warning", IsDefault: true)])];
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom"));
        var lead = ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single();
        Assert.Null(lead.PermissionMode);
        var sub = (await ctx.Kernel.CreateSessionAsync(snap.Track.Id, new(Provider: "acp:custom", PermissionMode: "inspect"), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default)).Receipt.Session;
        Assert.Equal("inspect", sub.PermissionMode);
        var inherited = (await ctx.Kernel.CreateSessionAsync(snap.Track.Id, new(Provider: "acp:custom"), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default)).Receipt.Session;
        Assert.Null(inherited.PermissionMode);
        await ctx.Kernel.UpdateSessionAsync(sub.Id, new(Provider: "codex"), new InteropPrincipal.Human(), default);
        var switched = await ctx.Kernel.UpdateSessionAsync(sub.Id, new(Provider: "acp:custom"), new InteropPrincipal.Human(), default);
        Assert.Null(switched.PermissionMode);
        await ctx.Kernel.UpdateSessionAsync(lead.Id, new(PermissionMode: new("inspect")), new InteropPrincipal.Human(), default);
        await ctx.SendAndWaitForTurnAsync(lead.Id, new("go", "mode-input"));
        Assert.Equal("inspect", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.PermissionMode);
        ctx.AgentRuntime.OpenedSessions.Last().AvailablePermissionModes = ctx.AgentRuntime.Providers.Single().PermissionModes!;
        await Assert.ThrowsAsync<KernelException>(() => ctx.Kernel.UpdateSessionAsync(lead.Id, new(PermissionMode: new("invented")), new InteropPrincipal.Human(), default));
        await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom", PermissionMode: "invented"));
        await ctx.Kernel.CreateSessionAsync(snap.Track.Id, new(Provider: "acp:custom", PermissionMode: "invented"), Guid.NewGuid().ToString("N"), new InteropPrincipal.Human(), default);
    }

    [Fact]
    public async Task ColdInvalidModeFailsAtOpenAndCanBeCorrectedForRetry()
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom", PermissionMode: "invalid"));
        var id = snap.Track.LeadSessionId!;
        ctx.AgentRuntime.OnBeforeOpenSession = spec =>
        {
            if (spec.PermissionMode == "invalid") throw new ArgumentException("Unsupported mode");
        };
        Assert.Equal("failed", (await ctx.SendAndWaitForTurnAsync(id, new("fail", "fail"))).Status);
        Assert.Empty(ctx.AgentRuntime.OpenedSessions);
        Assert.Equal("invalid", ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single().PermissionMode);
        await ctx.Kernel.UpdateSessionAsync(id, new(PermissionMode: new("plan")), new InteropPrincipal.Human(), default);
        Assert.Equal("completed", (await ctx.SendAndWaitForTurnAsync(id, new("retry", "retry"))).Status);
        Assert.Equal("plan", Assert.Single(ctx.AgentRuntime.OpenedSessions).LaunchSpec.PermissionMode);
        Assert.Equal(0, ctx.AgentRuntime.ProviderProbeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistedAcpModeSurvivesDiscoveryLossAndModelReopen(bool supportsSwitch)
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers = [new("acp:custom", true, "1", null, [], PermissionModes:
            [new("project-plan", "Plan", "", "neutral", true)])];
        var area = await ctx.Kernel.CreateAreaAsync(new("Modes"));
        var snap = await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom", PermissionMode: "project-plan"));
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.SupportsModelSwitch = supportsSwitch;
        ctx.AgentRuntime.Providers = [new("acp:custom", false, null, "discovery unavailable", [], PermissionModes: [])];
        var probes = ctx.AgentRuntime.ProviderProbeCalls;
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("next-model")), new InteropPrincipal.Human(), default);
        await ctx.SendAndWaitForTurnAsync(id, new("next", "next"));
        Assert.Equal(supportsSwitch ? 1 : 2, ctx.AgentRuntime.OpenedSessions.Count);
        Assert.All(ctx.AgentRuntime.OpenedSessions, opened => Assert.Equal("project-plan", opened.LaunchSpec.PermissionMode));
        if (!supportsSwitch)
            Assert.Equal(original.NativeThreadId, ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.ResumeNativeThreadId);
        await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
        Assert.Equal("project-plan", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.PermissionMode);
        Assert.Equal(probes, ctx.AgentRuntime.ProviderProbeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModelUpdatePersistsEmitsAndReconcilesCachedSession(bool supportsSwitch)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.SupportsModelSwitch = supportsSwitch;

        var updated = await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("new-model")), new InteropPrincipal.Human(), default);
        Assert.Equal("new-model", updated.Model);
        Assert.Equal("new-model", ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id).Model);
        Assert.Contains(ctx.Store.PersistedEvents, e => e.Kind == Perga.Kernel.Events.KernelEventKinds.SessionUpdated &&
            e.Payload.GetProperty("id").GetString() == id && e.Payload.TryGetProperty("model", out var model) && model.GetString() == "new-model");
        Assert.Equal(new[] { "new-model" }, original.ModelSwitches);
        Assert.Equal(!supportsSwitch, original.Disposed);
        var opened = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Equal(supportsSwitch ? 1 : 2, opened.Length);
        if (!supportsSwitch)
        {
            Assert.Equal("new-model", opened[1].LaunchSpec.Model);
            Assert.Equal(original.NativeThreadId, opened[1].LaunchSpec.ResumeNativeThreadId);
        }
        await ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("next", "next"));
        Assert.Equal(opened.Length, ctx.AgentRuntime.OpenedSessions.Count);
        await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("new-model")), new InteropPrincipal.Human(), default);
        Assert.Single(original.ModelSwitches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReopenKeepsDesiredModelAndNextTurnRetries(bool afterCompaction)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("first", "first"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        if (afterCompaction)
        {
            await ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default);
            original = ctx.AgentRuntime.OpenedSessions.Last();
        }
        var opens = ctx.AgentRuntime.OpenedSessions.Count;
        ctx.AgentRuntime.OnBeforeOpenSession = _ => throw new InvalidOperationException("provider unavailable");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.Kernel.UpdateSessionAsync(id, new(Model: new("retry-model")), new InteropPrincipal.Human(), default));
        Assert.True(original.Disposed);
        Assert.Equal("retry-model", ((await ctx.Kernel.ListSessionsAsync(snap.Track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray()).Single(s => s.Id == id).Model);
        ctx.AgentRuntime.OnBeforeOpenSession = null;
        await ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("retry", "retry"));
        Assert.Equal("retry-model", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.Model);
        Assert.Equal(original.NativeThreadId, ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.ResumeNativeThreadId);
        Assert.Equal(opens + 1, ctx.AgentRuntime.OpenedSessions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelUpdateDuringTurnOrCompactionDefersSwitch(bool compacting)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        Task turn = compacting ? ctx.Kernel.ControlSessionAsync(id, new("compact"), new InteropPrincipal.Human(), default) :
            ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("first", "first"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.SupportsModelSwitch = true;
        try
        {
            await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("later-model")), new InteropPrincipal.Human(), default);
            Assert.Empty(original.ModelSwitches);
            Assert.False(original.Disposed);
        }
        finally
        {
            release.TrySetResult();
        }
        await turn;
        await ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("next", "next"));
        if (compacting)
        {
            Assert.Empty(original.ModelSwitches);
            Assert.True(original.Disposed);
            Assert.Equal("later-model", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.Model);
            Assert.Equal(2, ctx.AgentRuntime.OpenedSessions.Count);
        }
        else
        {
            Assert.Equal(new[] { "later-model" }, original.ModelSwitches);
            Assert.Single(ctx.AgentRuntime.OpenedSessions);
        }
    }

    [Fact]
    public async Task PermissionAndModelChangesDuringTurnReopenTogetherBeforeNextDispatch()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var id = snap.Track.LeadSessionId!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = Stream;
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(ScriptableFakeAgentSession session, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        var turn = ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("first", "first"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);
        original.SupportsModelSwitch = true;
        try
        {
            await ctx.Kernel.UpdateSessionAsync(id, new(PermissionMode: new("full")), new InteropPrincipal.Human(), default);
            await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("next-model"), ReasoningEffort: new("high")), new InteropPrincipal.Human(), default);
            Assert.False(original.Disposed);
            Assert.Empty(original.ModelSwitches);
        }
        finally { release.TrySetResult(); }
        await turn;
        await ctx.SendAndWaitForTurnAsync(id, new SendSessionMessageRequest("next", "next"));
        Assert.True(original.Disposed);
        Assert.Empty(original.ModelSwitches);
        Assert.Equal(2, ctx.AgentRuntime.OpenedSessions.Count);
        var reopened = ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec;
        Assert.Equal("full", reopened.PermissionMode);
        Assert.Equal("next-model", reopened.Model);
        Assert.Equal("high", reopened.ReasoningEffort);
    }

    [Fact]
    public async Task EpochHandoverCompaction_ManualAndAutoAt80Percent_SummarizesDisposesBumpsEpochAndSeedsFreshSession()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        // Update report with distinctive content so we can verify fresh Epoch session receives latest Report markdown!
        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Prose, "# Architecture\n\nDISTILLATION_MARKER_V1", null)]));

        // Configure turn script: first regular turn hits 85% context usage (>= 0.80), triggering AUTO epoch handover compaction!
        var turnCallNum = 0;
        ctx.AgentRuntime.TurnScript = (session, input, ct) =>
            StreamScriptedTurn(session, input, Interlocked.Increment(ref turnCallNum), ct);

        var turn1 = await ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("Analyze repo", "idem-auto-compact"));
        Assert.Equal("completed", turn1.Status);
        Assert.Equal(1, turn1.Epoch);

        // Auto-compaction ran automatically after turn1 because 85_000 / 100_000 == 0.85 >= 0.80!
        var sessionsAfterAuto = (await ctx.Kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray();
        var leadAfterAuto = sessionsAfterAuto.Single(s => s.Id == leadId);
        Assert.Equal(2, leadAfterAuto.Epoch);
        Assert.Null(leadAfterAuto.NativeThreadId);
        Assert.Null(leadAfterAuto.ContextUsage);

        var openedArray = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Equal(2, openedArray.Length);

        // Old Epoch 1 IAgentSession was asked for structured handover summary and then DISPOSED!
        var epoch1Session = openedArray[0];
        Assert.True(epoch1Session.Disposed);
        Assert.Equal(2, epoch1Session.RecordedInputs.Count); // 1 user turn + 1 handover summary turn

        // Fresh Epoch 2 IAgentSession was opened with ResumeNativeThreadId == null and SystemPrompt containing BOTH latest Report + Handover Summary!
        var epoch2Session = openedArray[1];
        Assert.False(epoch2Session.Disposed);
        Assert.Null(epoch2Session.LaunchSpec.ResumeNativeThreadId);
        Assert.Contains("DISTILLATION_MARKER_V1", epoch2Session.LaunchSpec.SystemPrompt);
        Assert.Contains("HANDOVER_SUMMARY_EPOCH_1", epoch2Session.LaunchSpec.SystemPrompt);

        // Now test MANUAL CompactSessionAsync from Epoch 2 -> Epoch 3!
        var manuallyCompacted = await ctx.Kernel.ControlSessionAsync(leadId, new("compact"), new InteropPrincipal.Human(), default);
        Assert.Equal(3, manuallyCompacted.Epoch);
        Assert.Null(manuallyCompacted.NativeThreadId);
        Assert.Null(manuallyCompacted.ContextUsage);
        Assert.True(epoch2Session.Disposed);

        var allOpened = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Equal(3, allOpened.Length);
        var epoch3Session = allOpened[2];
        Assert.Null(epoch3Session.LaunchSpec.ResumeNativeThreadId);
        Assert.Contains("DISTILLATION_MARKER_V1", epoch3Session.LaunchSpec.SystemPrompt);
        Assert.Contains("HANDOVER_SUMMARY_EPOCH_2", epoch3Session.LaunchSpec.SystemPrompt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCompaction_ManualAndAutoAt80Percent_PreservesNativeThreadEpochAndUpdatesUsage(bool reportsUsage)
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        ctx.AgentRuntime.TurnScript = (session, _, ct) =>
        {
            session.SupportsNativeCompact = true;
            session.CompactHandler = (_, _) => Task.FromResult(
                new NativeCompactResult(reportsUsage ? new TokenUsage(10_000, 0, 0, 100_000, UsedTokens: 20_000) : null, "Native compact summary."));
            return StreamScriptedTurn(session, new TurnInput("Analyze repo", "idem"), 1, ct);
        };

        var turn1 = await ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("Analyze repo", "idem-native-auto"));
        Assert.Equal("completed", turn1.Status);

        var sessionsAfterAuto = (await ctx.Kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray();
        var leadAfterAuto = sessionsAfterAuto.Single(s => s.Id == leadId);
        Assert.Equal(1, leadAfterAuto.Epoch);
        Assert.Equal("thr_epoch_1", leadAfterAuto.NativeThreadId);
        Assert.Equal(reportsUsage ? 0.2 : (double?)null, leadAfterAuto.ContextUsage?.Ratio);
        Assert.Equal(reportsUsage ? 20_000L : (long?)null, leadAfterAuto.ContextUsage?.UsedTokens);
        Assert.Equal(reportsUsage, leadAfterAuto.ContextUsage?.ObservedAtMs is not null);
        Assert.Equal(reportsUsage ? 100_000L : (long?)null, leadAfterAuto.ContextUsage?.WindowTokens);

        var opened = ctx.AgentRuntime.OpenedSessions.ToArray();
        Assert.Single(opened);
        Assert.False(opened[0].Disposed);
        Assert.Equal(1, opened[0].CompactCalls);

        var manual = await ctx.Kernel.ControlSessionAsync(leadId, new("compact"), new InteropPrincipal.Human(), default);
        Assert.Equal(1, manual.Epoch);
        Assert.Equal("thr_epoch_1", manual.NativeThreadId);
        Assert.Equal(reportsUsage ? 0.2 : (double?)null, manual.ContextUsage?.Ratio);
        Assert.Equal(reportsUsage ? 20_000L : (long?)null, manual.ContextUsage?.UsedTokens);
        Assert.Equal(reportsUsage, manual.ContextUsage?.ObservedAtMs is not null);

        Assert.Equal(2, opened[0].CompactCalls);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamScriptedTurn(
        ScriptableFakeAgentSession session,
        TurnInput input,
        int callNumber,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        if (callNumber == 1)
        {
            yield return new UnifiedAgentEvent.ThreadBound("thr_epoch_1");
            yield return new UnifiedAgentEvent.TextDelta("Analysis complete.");
            // 85% context usage triggers auto-compaction (>= 0.80)
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(80_000, 5_000, 0, 100_000));
            yield break;
        }

        if (callNumber == 2)
        {
            // Auto-compaction handover summary turn on Epoch 1 session
            yield return new UnifiedAgentEvent.TextDelta("HANDOVER_SUMMARY_EPOCH_1: Verified parser & storage.");
            yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1_000, 200, 0, 100_000));
            yield break;
        }

        // Manual compaction handover summary turn on Epoch 2 session
        yield return new UnifiedAgentEvent.TextDelta("HANDOVER_SUMMARY_EPOCH_2: Ready for final ship.");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(1_000, 200, 0, 100_000));
    }

    [Fact]
    public async Task SendMessage_SupportsIdempotencyDedup_ExplicitSteer_Stop_AndApprovalBridge()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        // 1. Idempotency-Key deduplication
        var t1 = await ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("Hello once", "same-key-1"));
        var t2 = await ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("Hello once", "same-key-1"));
        Assert.Equal(t1.Id, t2.Id);
        Assert.Single(ctx.Store.GetTurnsForSession(leadId));

        // 2. ApprovalRequested bridged to AskDto + explicit steering while turn is in-flight!
        var releaseTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => StreamWithApprovalAndGate(releaseTurn.Task, ct);

        var inFlightTurnTask = ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("Run guarded shell", "key-approval"));

        // Wait for ApprovalRequested to materialize as an OpenAsk on the track
        ApprovalDto? approvalAsk = null;
        for (var i = 0; i < 50 && approvalAsk is null; i++)
        {
            var agg = await ctx.Kernel.GetTrackAggregateAsync(trackId);
            approvalAsk = agg.PendingApprovals.FirstOrDefault();
            if (approvalAsk is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(approvalAsk);

        // Steer while turn is running!
        var steeredTurn = await ctx.SendAndWaitForTurnAsync(
            leadId,
            new SendSessionMessageRequest("Use --release flag", "key-steer", Mode: "steer", TurnId: (await ctx.Kernel.GetSessionItemsAsync(leadId, new(), new InteropPrincipal.Human(), default)).ActiveTurn!.Id));
        Assert.Equal("running", steeredTurn.Status);

        var activeFakeSession = ctx.AgentRuntime.OpenedSessions.Last();
        Assert.Contains("Use --release flag", activeFakeSession.SteeredMessages);

        // Answer the approval Ask -> unblocks RespondApprovalAsync on IAgentSession without extra duplicate wake turn!
        await ctx.Kernel.RespondInteractionAsync(approvalAsk!.SessionId, approvalAsk!.Id, new(ExecutionId: approvalAsk!.ExecutionId, OptionId: approvalAsk.Options.Single(o => o.IsApproval).Id), new InteropPrincipal.Human(), default);
        releaseTurn.TrySetResult();

        var finishedTurn = await inFlightTurnTask;
        Assert.Equal("completed", finishedTurn.Status);
        Assert.Single(activeFakeSession.Approvals, a => a.RequestId == "req_42" && a.Approved);

        // Verify MCP token registration format (pg_<guid>)
        Assert.True(activeFakeSession.LaunchSpec.McpServers.TryGetValue("perga", out var pergaMcp));
        var httpMcp = Assert.IsType<McpServerConfig.HttpMcpServer>(pergaMcp);
        Assert.StartsWith("Bearer pg_", httpMcp.Headers["Authorization"], StringComparison.Ordinal);

        // 3. InterruptSessionAsync cancels an active running turn and marks it "interrupted"
        var neverEnding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => StreamBlockingUntilCanceled(neverEnding.Task, ct);

        var interruptedTurnTask = ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("Long job", "key-interrupt"));
        for (var i = 0; i < 50; i++)
        {
            var page = await ctx.Kernel.GetSessionItemsAsync(leadId, new(), new InteropPrincipal.Human(), default);
            if (page.ActiveTurn is not null)
            {
                break;
            }

            await Task.Delay(10);
        }

        await ctx.Kernel.ControlSessionAsync(leadId, new("stop", TurnId: (await ctx.Kernel.GetSessionItemsAsync(leadId, new(), new InteropPrincipal.Human(), default)).ActiveTurn!.Id), new InteropPrincipal.Human(), default);
        var interruptedTurn = await interruptedTurnTask;
        Assert.Equal("interrupted", interruptedTurn.Status);
        Assert.True(activeFakeSession.CancelCalled);
    }

    [Theory]
    [InlineData("codex", false)]
    [InlineData("codex", true)]
    [InlineData("acp:custom", false)]
    public async Task TaskWorkerSession_ReceivesDefaultPermissionModeOnCreateAndLaunch(string provider, bool dynamicModes)
    {
        await using var ctx = new KernelTestContext();
        if (dynamicModes)
            ctx.AgentRuntime.Providers = [new("codex", true, "1", null, [], PermissionModes:
                [new("runtime-worker", "Worker", "", "neutral", true)])];
        if (provider != "codex")
            ctx.AgentRuntime.Providers = [.. ctx.AgentRuntime.Providers, new(provider, true, "1", null, [])];
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        var openedWorker = new TaskCompletionSource<ScriptableFakeAgentSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (session, input, ct) =>
        {
            if (session.LaunchSpec.SystemPrompt?.Contains("BoundAttemptId=", StringComparison.Ordinal) == true)
                openedWorker.TrySetResult(session);
            return StreamScriptedTurn(session, input, 1, ct);
        };

        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [new BlockOp.UpsertBlockOp("b_worker_perm", ReportBlockKind.Task, null, TestJson.FromObject(new
            {
                key = "worker-perm",
                kind = "codex",
                provider,
                goal = "Run worker task",
                ready = true,
                declared_by = "spec",
                no_gate_reason = "test"
            }))]));

        var workerSession = await openedWorker.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(provider.StartsWith("acp:") ? null : "never", workerSession.LaunchSpec.PermissionMode);

        var sessions = (await ctx.Kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray();
        var bound = Assert.Single(sessions, s => s.BoundTaskId is not null);
        var mode = provider.StartsWith("acp:") ? "plan" : provider == "codex" ? "full" : "plan";
        var updated = await ctx.Kernel.UpdateSessionAsync(bound.Id, new(PermissionMode: new(mode)), new InteropPrincipal.Human(), default);
        Assert.Equal(mode, updated.PermissionMode);
        Assert.Equal(bound.ConfigVersion + 1, updated.ConfigVersion);
    }

    [Fact]
    public async Task PatchSession_ProviderChange_ResetsPermissionModeToNewProviderDefault()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var leadId = snap.Track.LeadSessionId!;

        await ctx.Kernel.UpdateSessionAsync(leadId, new(PermissionMode: new("full")), new InteropPrincipal.Human(), default);
        var patched = await ctx.Kernel.UpdateSessionAsync(leadId, new(Provider: "claude"), new InteropPrincipal.Human(), default);
        Assert.Equal("claude", patched.Provider);
        Assert.Equal("dontAsk", patched.PermissionMode);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamBlockingUntilCanceled(
        Task blocker,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.TextDelta("Starting long work...");
        try { await blocker.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        if (ct.IsCancellationRequested)
        {
            yield return new UnifiedAgentEvent.TurnInterrupted();
            yield break;
        }
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamWithApprovalAndGate(
        Task gateTask,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ToolCallStarted("call_1", "bash", TestJson.Parse("""{"cmd":"rm -rf tmp"}"""));
        yield return new UnifiedAgentEvent.ApprovalRequested("req_42", "bash", TestJson.Parse("""{"cmd":"rm -rf tmp"}"""));
        await gateTask.WaitAsync(ct);
        yield return new UnifiedAgentEvent.ToolCallCompleted("call_1", "ok", IsError: false);
        yield return new UnifiedAgentEvent.TextDelta("Completed approved command.");
        yield return new UnifiedAgentEvent.TurnCompleted(new TokenUsage(200, 50, 0, 100_000));
    }

    [Fact]
    public async Task ApprovalRequested_WithProviderOptions_ExposesAllOptionsAndForwardsSelectedOptionId()
    {
        await using var ctx = new KernelTestContext();
        var area = await ctx.Kernel.CreateAreaAsync(new CreateAreaRequest("Approval Options Area"));
        var track = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(AreaId: area.Id, Title: "Approval Options Track"));
        var leadId = track.Track.LeadSessionId!;

        var providerOptions = new ApprovalOption[]
        {
            new("opt_allow_once", "Allow once", true),
            new("opt_allow_always", "Always allow", true),
            new("opt_reject_once", "Reject once", false)
        };

        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, ct) => StreamWithProviderOptions(providerOptions, allowCompletion.Task, ct);

        var turnTask = ctx.SendAndWaitForTurnAsync(leadId, new SendSessionMessageRequest("Run command", "key-opts"));

        ApprovalDto? approvalAsk = null;
        for (var i = 0; i < 50 && approvalAsk is null; i++)
        {
            var agg = await ctx.Kernel.GetTrackAggregateAsync(track.Track.Id);
            approvalAsk = agg.PendingApprovals.FirstOrDefault();
            if (approvalAsk is null)
            {
                await Task.Delay(10);
            }
        }

        Assert.NotNull(approvalAsk);
        Assert.Equal(["opt_allow_once", "opt_allow_always", "opt_reject_once"], approvalAsk!.Options.Select(o => o.Id));
        Assert.Equal(["Allow once", "Always allow", "Reject once"], approvalAsk.Options.Select(o => o.Label));

        // User chooses "opt_allow_always" ("Always allow") directly by structured optionId
        await ctx.Kernel.RespondInteractionAsync(approvalAsk.SessionId, approvalAsk.Id, new(ExecutionId: approvalAsk.ExecutionId, OptionId: "opt_allow_always"), new InteropPrincipal.Human(), default);

        allowCompletion.TrySetResult();
        var finishedTurn = await turnTask;
        Assert.Equal("completed", finishedTurn.Status);

        var activeFakeSession = ctx.AgentRuntime.OpenedSessions.Last();
        Assert.Single(activeFakeSession.DetailedApprovals, a =>
            a.RequestId == "req_opts" &&
            a.Approved &&
            a.SelectedOptionId == "opt_allow_always");
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> StreamWithProviderOptions(
        IReadOnlyList<ApprovalOption> options, Task allowCompletion,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.ApprovalRequested("req_opts", "run_command", TestJson.Parse("""{"cmd":"ls"}"""), options);
        await Task.Yield();
        yield return new UnifiedAgentEvent.TextDelta("Done");
        await allowCompletion.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }
}
