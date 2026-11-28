using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Settings;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class AppSettingsServiceTests
{
    [Fact]
    public async Task PreservesExactKernelDefaultsAndOnlyPersistsExplicitOverrides()
    {
        var defaults = new KernelOptions { SessionIdleTimeout = TimeSpan.Zero, AutoCompactionThreshold = 0.834 };
        var store = new MemoryStore();
        using var service = new AppSettingsService(new(defaults, ["codex", "claude"]), store);

        var initial = await service.ReloadAsync();
        Assert.Equal(TimeSpan.Zero, initial.SessionIdleTimeout);
        Assert.Equal(0.834, initial.AutoCompactionThreshold);
        Assert.Equal(83, initial.Contract.Values["sessions.auto_compaction_percent"].GetInt32());
        Assert.Empty(store.Values);

        var updated = await service.PatchAsync(Patch(initial, "planners.default_provider", "claude"));
        Assert.Equal("claude", updated.DefaultProvider);
        Assert.Equal(TimeSpan.Zero, updated.SessionIdleTimeout);
        Assert.Equal(0.834, updated.AutoCompactionThreshold);
        Assert.Equal("claude", store.Values["planners.default_provider"]);
        Assert.Single(store.Values);
        Assert.Equal(initial.Contract.Rev + 1, updated.Contract.Rev);
        Assert.Equal("codex", initial.DefaultProvider);
        Assert.Same(updated, await service.ReloadAsync());
    }

    [Fact]
    public async Task WriteFailureLeavesRevisionAndSettingsAvailableForRetry()
    {
        var store = new MemoryStore();
        using var service = new AppSettingsService(new(new KernelOptions(), ["codex", "claude"]), store);
        var initial = await service.ReloadAsync();
        var request = Patch(initial, "planners.default_provider", "claude");

        store.WriteError = new IOException("Write failed.");
        await Assert.ThrowsAsync<IOException>(() => service.PatchAsync(request));
        Assert.Same(initial, service.Current);
        Assert.Empty(store.Values);

        store.WriteError = null;
        Assert.Equal("claude", (await service.PatchAsync(request)).DefaultProvider);
    }

    [Fact]
    public async Task IdleCloseAdmissionUsesTheTimeoutSavedAfterCandidateSelection()
    {
        var options = new KernelOptions
        {
            ActorIdleTimeout = TimeSpan.Zero,
            ActorEvictionSweepInterval = TimeSpan.Zero,
            SessionIdleTimeout = TimeSpan.FromMinutes(5)
        };
        using var settings = new AppSettingsService(new(options, ["codex"]), new MemoryStore());
        await using var ctx = new KernelTestContext(options, appSettings: settings);
        ctx.AgentRuntime.SupportsSessionResume = true;
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var actor = ctx.Kernel.Actors.GetOrActivate(snapshot.Track.Id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        await ctx.SendAndWaitForTurnAsync(snapshot.Track.LeadSessionId!, new("first", "first"), ct);
        await ctx.WaitForActorQuiescenceAsync(snapshot.Track.Id, ct);
        ctx.Time.Advance(TimeSpan.FromMinutes(5));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (sweep, blocker) = await actor.EnqueueAsync(current =>
        {
            var pendingSweep = current.EvictIdleSessionRuntimesAsync(ct);
            // This mailbox item runs after selection and before the close admission.
            var pendingBlocker = current.EnqueueAsync(async _ =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }, ct, touchActivity: false);
            return Task.FromResult((pendingSweep, pendingBlocker));
        }, ct);

        try
        {
            await entered.Task.WaitAsync(ct);
            await settings.PatchAsync(new(settings.Current.Contract.Rev, new Dictionary<string, JsonElement>
            {
                ["sessions.idle_timeout_minutes"] = JsonSerializer.SerializeToElement(15)
            }), ct);
        }
        finally
        {
            release.TrySetResult();
        }

        await blocker;
        Assert.Equal(0, await sweep);
        Assert.False(Assert.Single(ctx.AgentRuntime.OpenedSessions).Disposed);
        ctx.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await actor.EvictIdleSessionRuntimesAsync(ct));
    }

    private static PatchSettingsRequestDto Patch(AppSettingsSnapshot snapshot, string key, string value) =>
        new(snapshot.Contract.Rev, new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement(value) });

    private sealed class MemoryStore : IAppSettingsStore
    {
        public IReadOnlyDictionary<string, object> Values { get; private set; } = new Dictionary<string, object>();
        public IOException? WriteError { get; set; }

        public Task<IReadOnlyDictionary<string, object>> ReadAsync(CancellationToken ct) => Task.FromResult(Values);

        public Task WriteAsync(IReadOnlyDictionary<string, object> values, CancellationToken ct)
        {
            if (WriteError is not null) throw WriteError;
            Values = new Dictionary<string, object>(values);
            return Task.CompletedTask;
        }
    }
}
