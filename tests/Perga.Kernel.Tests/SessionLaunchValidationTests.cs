using Perga.Contracts;
using Perga.AgentKit;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionLaunchValidationTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    [Theory]
    [InlineData(null)]
    [InlineData("Inspect workspace")]
    public async Task SessionCreationTrimsExplicitModelAndRejectsBlankModel(string? prompt)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var request = new CreateSessionRequest(Provider: "codex", Prompt: prompt);

        var error = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CreateSessionAsync(trackId, request with { Model = " \t " }, "blank", Human, default));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
        Assert.Equal("Model must not be blank.", error.Message);

        var created = await ctx.Kernel.CreateSessionAsync(trackId, request with { Model = " gpt-5 " }, "trimmed", Human, default);
        Assert.Equal("gpt-5", created.Receipt.Session.Model);
    }

    [Fact]
    public async Task WorkerAdmissionChecksTheModelThatWillLaunch()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5"])];
        ctx.Kernel.Templates.RegisterTemplate("""
            export default defineTemplate({
              id: "guarded", title: "Guarded",
              tasks: { routing: [{ when: {}, model: " gpt-5 " }] },
              before: call => call.type === "task.dispatch" && call.model === "gpt-5" ? Deny("Model blocked") : Allow()
            });
            """);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync(templateId: "guarded");
        var trackId = snapshot.Track.Id;

        await FlowFixture.AddAsync(ctx.Kernel, trackId, FlowFixture.Worker("blocked", "Inspect workspace"));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);
        var task = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId));
        Assert.Equal(TaskRunStatus.Failed, task.Status);
        Assert.Contains("Model blocked", task.StatusDetail);
        Assert.Single(await ctx.Kernel.ListSessionsAsync(trackId, new(), Human, default));
    }

    [Theory]
    [InlineData(null, "ultra")]
    [InlineData("gpt-6.2-sol", "ultra")]
    [InlineData(" gpt-6.2-sol ", "ultra")]
    [InlineData("gpt-5.6-luna", null)]
    public async Task ChildOnlyInheritsReasoningEffortForTheParentsModel(string? model, string? expectedEffort)
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers =
        [
            new("codex", true, "1", null, ["gpt-6.2-sol", "gpt-5.6-luna"],
                ModelCatalog:
                [
                    new("gpt-6.2-sol", "Sol", "", true, "ultra", [new("ultra", "")]),
                    new("gpt-5.6-luna", "Luna", "", false, "medium", [new("medium", "")])
                ])
        ];
        var area = await ctx.Kernel.CreateAreaAsync(new("Effort inheritance"));
        var track = (await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "codex",
            Model: "gpt-6.2-sol", ReasoningEffort: "ultra"))).Track;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var leadId = track.LeadSessionId!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, _, token) => HoldTurnAsync(token);
        await ctx.Kernel.SendSessionMessageAsync(leadId, new("hold", "hold"), Human, ct);
        await started.Task.WaitAsync(ct);
        var parent = ctx.RuntimeCaller(leadId);

        var receipt = await ctx.Kernel.InvokeKernelMcpToolAsync(parent, KernelMcpToolNames.SessionCreate,
            TestJson.FromObject(model is null ? new { requestKey = "child" } : (object)new { requestKey = "child", model }), ct);
        var session = receipt.GetProperty("session");

        Assert.Equal(model?.Trim() ?? "gpt-6.2-sol", session.GetProperty("model").GetString());
        Assert.Equal(expectedEffort, session.TryGetProperty("reasoningEffort", out var effort) ? effort.GetString() : null);

        async IAsyncEnumerable<UnifiedAgentEvent> HoldTurnAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }
    }

    [Fact]
    public async Task DiscoveryUsesProbeModesUntilRuntimeFinishesInitializing()
    {
        await using var ctx = new KernelTestContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        ctx.AgentRuntime.Providers =
        [
            new("acp:custom", true, "1", null, [], PermissionModes: [new("preview", "Preview", "")])
        ];
        var area = await ctx.Kernel.CreateAreaAsync(new("Runtime discovery"));
        var track = (await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "acp:custom"))).Track;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.OnOpenedSession = session => session.AvailablePermissionModes = [new("live", "Live", "")];
        ctx.AgentRuntime.BeforeOpenAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var turn = ctx.SendAndWaitForTurnAsync(track.LeadSessionId!, new("start", "start"), ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            var discovery = await ctx.Kernel.ListProvidersForTrackAsync(track.Id, "acp:custom", ct: ct);
            Assert.Equal("preview", Assert.Single(Assert.Single(discovery.Providers).PermissionModes).Id);
        }
        finally
        {
            release.TrySetResult();
            await turn.WaitAsync(ct);
        }

        var ready = await ctx.Kernel.ListProvidersForTrackAsync(track.Id, "acp:custom", ct: ct);
        Assert.Equal("live", Assert.Single(Assert.Single(ready.Providers).PermissionModes).Id);
    }

    [Fact]
    public async Task SameProviderModelUpdateLeavesTheOwnedProcessUntilNextTurn()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5", "next-model"])];
        var area = await ctx.Kernel.CreateAreaAsync(new("Runtime configuration"));
        var track = (await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "codex", Model: "gpt-5"))).Track;
        var id = track.LeadSessionId!;
        await ctx.SendAndWaitForTurnAsync(id, new("start", "start"));
        var original = Assert.Single(ctx.AgentRuntime.OpenedSessions);

        var updated = await ctx.Kernel.UpdateSessionAsync(id, new(Model: new("next-model")), Human, default);
        Assert.Equal("next-model", updated.Model);
        Assert.Same(original, Assert.Single(ctx.AgentRuntime.OpenedSessions));
        Assert.False(original.Disposed);
        Assert.Equal("gpt-5", original.ConfigurationState?.Selection.Model.ValueOrNull);

        await ctx.SendAndWaitForTurnAsync(id, new("continue", "continue"));
        Assert.True(original.Disposed);
        Assert.Equal(2, ctx.AgentRuntime.OpenedSessions.Count);
        Assert.Equal("next-model", ctx.AgentRuntime.OpenedSessions.Last().LaunchSpec.Model);
    }

    [Fact]
    public async Task DiscoveryReportsTrackDefaultAndPrefersLiveAcpModes()
    {
        await using var ctx = new KernelTestContext();
        ctx.AgentRuntime.Providers =
        [
            new("acp:antigravity", true, "1", null, ["legacy", "gemini"],
                ModelCatalog: [new("gemini", "Gemini", "", true, "high", [new("high", "")])],
                PermissionModes: [new("preview", "Preview", "")]),
            new("codex", false, null, "offline", [])
        ];
        var area = await ctx.Kernel.CreateAreaAsync(new("Providers"));
        var track = (await ctx.Kernel.CreateTrackAsync(new(AreaId: area.Id, Provider: "agy", Model: "gemini"))).Track;
        var first = await ctx.Kernel.ListProvidersForTrackAsync(track.Id);
        Assert.Equal("acp:antigravity", first.DefaultProvider);
        var preview = Assert.Single(first.Providers);
        Assert.Equal("dynamic", preview.PermissionCatalogSource);
        Assert.Equal("gemini", preview.DefaultModel);
        Assert.Equal("Gemini", Assert.Single(preview.Models).DisplayName);
        Assert.Equal("high", Assert.Single(Assert.Single(preview.Models).ReasoningEfforts));
        Assert.Equal("preview", Assert.Single(preview.PermissionModes).Id);
        Assert.False(Assert.Single((await ctx.Kernel.ListProvidersForTrackAsync(track.Id, "codex")).Providers).Available);

        await ctx.SendAndWaitForTurnAsync(track.LeadSessionId!, new("start", "start"));
        Assert.Single(ctx.AgentRuntime.OpenedSessions).AvailablePermissionModes = [new("live", "Live", "")];
        var live = await ctx.Kernel.ListProvidersForTrackAsync(track.Id, "agy");
        Assert.Equal("live", Assert.Single(Assert.Single(live.Providers).PermissionModes).Id);
        Assert.Equal(first.ObservedAtMs, live.ObservedAtMs);
        Assert.Equal(1, ctx.AgentRuntime.ProviderProbeCalls);

        var child = await ctx.Kernel.CreateSessionAsync(track.Id, new(PermissionMode: "live"), "child", Human, default);
        Assert.Equal("acp:antigravity", child.Receipt.Session.Provider);
        Assert.Equal("gemini", child.Receipt.Session.Model);
        Assert.Equal("live", child.Receipt.Session.PermissionMode);

        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Single(ctx.AgentRuntime.OpenedSessions).DisposeHandler = () =>
        {
            disposing.TrySetResult();
            return release.Task;
        };
        var switched = ctx.Kernel.UpdateSessionAsync(track.LeadSessionId!, new(Provider: "codex"), Human, default);
        try
        {
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var afterSwitch = await ctx.Kernel.ListProvidersForTrackAsync(track.Id, "agy");
            Assert.Equal("codex", afterSwitch.DefaultProvider);
            Assert.Equal("preview", Assert.Single(Assert.Single(afterSwitch.Providers).PermissionModes).Id);
        }
        finally
        {
            release.TrySetResult();
            await switched.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task HumanCanCreateAndUpdateAnIdleDraftForAnUnavailableProvider()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        ctx.AgentRuntime.Providers =
        [
            new("codex", false, null, "offline", []),
            new("claude", false, null, "offline", [])
        ];

        var created = await ctx.Kernel.CreateSessionAsync(snapshot.Track.Id,
            new(Provider: "codex", Model: "future-model"), "draft", Human, default);
        Assert.Equal("idle", created.Receipt.Session.Status);
        Assert.Equal("future-model", created.Receipt.Session.Model);
        Assert.Null(created.Receipt.FirstInput);
        var updated = await ctx.Kernel.UpdateSessionAsync(created.Receipt.Session.Id, new(Provider: "claude"), Human, default);
        Assert.Equal("claude", updated.Provider);
        Assert.Null(updated.Model);
        Assert.Empty(ctx.AgentRuntime.OpenedSessions);
    }

    [Theory]
    [InlineData("conversation")]
    [InlineData("update")]
    public async Task BlockedProviderProbeLeavesMailboxAvailable(string operation)
    {
        await using var ctx = new KernelTestContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var track = snapshot.Track;
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.ProbeScript = async token =>
        {
            probing.TrySetResult();
            await release.Task.WaitAsync(token);
            return ctx.AgentRuntime.Providers;
        };

        Task mutation = operation switch
        {
            "update" => ctx.Kernel.UpdateSessionAsync(track.LeadSessionId!, new(Model: new("gpt-5")), Human, ct),
            _ => ctx.Kernel.CreateSessionAsync(track.Id, new(Provider: "codex"), "conversation", Human, ct)
        };
        try
        {
            await probing.Task.WaitAsync(ct);
            Assert.Single(await ctx.Kernel.ListSessionsAsync(track.Id, new(), Human, ct));
            Assert.False(mutation.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await mutation.WaitAsync(ct);
        }

        Assert.Equal(1, ctx.AgentRuntime.ProviderProbeCalls);
    }

    [Fact]
    public async Task CreationReplayDoesNotProbeEvenWhenProviderGoesOffline()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var request = new CreateSessionRequest(Provider: "codex", Prompt: "inspect");
        var (receipt, replay) = await ctx.Kernel.CreateSessionAsync(trackId, request, "replay", Human, default);
        Assert.False(replay);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        ctx.AgentRuntime.Providers = [new("codex", false, null, "offline", [])];
        var probes = ctx.AgentRuntime.ProviderProbeCalls;
        var result = await ctx.Kernel.CreateSessionAsync(trackId, request, "replay", Human, default);

        Assert.True(result.Replay);
        Assert.Equal(receipt.Session.Id, result.Receipt.Session.Id);
        Assert.Equal(probes, ctx.AgentRuntime.ProviderProbeCalls);
        var error = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CreateSessionAsync(trackId, request, "new-key", Human, default));
        Assert.Equal(KernelErrorCode.PreconditionFailed, error.Code);
        Assert.Contains("offline", error.Message);
    }
}
