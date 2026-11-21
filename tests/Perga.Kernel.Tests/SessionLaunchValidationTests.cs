using Perga.AgentKit;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class SessionLaunchValidationTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

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
    [InlineData("worker")]
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
            "worker" => ctx.Kernel.CreateSessionAsync(track.Id, new(Provider: "codex", Prompt: "inspect", Task: new()), "worker", Human, ct),
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
    public async Task AcceptedWorkerWaitsWhenProviderGoesOfflineBeforeDependenciesSettle()
    {
        await using var ctx = new KernelTestContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var blocker = (await ctx.Kernel.CreateSessionAsync(trackId,
            new(Prompt: "blocker", Task: new()), "blocker", Human, ct)).Receipt;
        var request = new CreateSessionRequest(Provider: "codex", Prompt: "inspect",
            Task: new(DependsOn: [blocker.Task!.TaskKey]));
        var worker = (await ctx.Kernel.CreateSessionAsync(trackId, request, "worker", Human, ct)).Receipt;
        await ctx.Kernel.WaitForTrackIdleAsync(trackId, ct);

        ctx.AgentRuntime.Providers = [new("codex", false, null, "offline", [])];
        await ctx.Kernel.CompleteAttemptDoneAsync(trackId, blocker.Session.Id, ct: ct);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId, ct);
        Assert.Empty(await ctx.Kernel.ListTaskAttemptsAsync(trackId, worker.Task!.Id, ct));

        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.ProbeScript = async token =>
        {
            probing.TrySetResult();
            await release.Task.WaitAsync(token);
            return ctx.AgentRuntime.Providers;
        };
        try
        {
            var replay = await ctx.Kernel.CreateSessionAsync(trackId, request, "worker", Human, ct);
            Assert.True(replay.Replay);
            Assert.Equal(worker.Session.Id, replay.Receipt.Session.Id);
            await probing.Task.WaitAsync(ct);
        }
        finally
        {
            release.TrySetResult();
        }
        await ctx.Kernel.WaitForTrackIdleAsync(trackId, ct);
        Assert.Empty(await ctx.Kernel.ListTaskAttemptsAsync(trackId, worker.Task.Id, ct));

        ctx.AgentRuntime.ProbeScript = null;
        ctx.AgentRuntime.Providers = [new("codex", true, "1", null, ["gpt-5"])];
        var recovered = await ctx.Kernel.CreateSessionAsync(trackId, request, "worker", Human, ct);
        Assert.True(recovered.Replay);
        Assert.Equal(worker.Session.Id, recovered.Receipt.Session.Id);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId, ct);
        Assert.Single(await ctx.Kernel.ListTaskAttemptsAsync(trackId, worker.Task.Id, ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationReplayDoesNotProbeEvenWhenProviderGoesOffline(bool worker)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var request = new CreateSessionRequest(Provider: "codex", Prompt: "inspect", Task: worker ? new() : null);
        var (receipt, replay) = await ctx.Kernel.CreateSessionAsync(trackId, request, "replay", Human, default);
        Assert.False(replay);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        ctx.AgentRuntime.Providers = [new("codex", false, null, "offline", [])];
        var probes = ctx.AgentRuntime.ProviderProbeCalls;
        var result = await ctx.Kernel.CreateSessionAsync(trackId, request, "replay", Human, default);

        Assert.True(result.Replay);
        Assert.Equal(receipt.Session.Id, result.Receipt.Session.Id);
        Assert.Equal(receipt.Task?.Id, result.Receipt.Task?.Id);
        Assert.Equal(probes, ctx.AgentRuntime.ProviderProbeCalls);
        var error = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CreateSessionAsync(trackId, request, "new-key", Human, default));
        Assert.Equal(KernelErrorCode.PreconditionFailed, error.Code);
        Assert.Contains("offline", error.Message);
    }
}
