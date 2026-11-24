using Dapper;
using Perga.AgentKit;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Storage.Tests;

public sealed class MemoryTrackStorageTests
{
    [Fact]
    public async Task ConcurrentEnsureReturnsOneTrackPerScopeAndAreaDeletionRemovesItsPlannerOnly()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var kernel = CreateKernel(harness);
        var area = await kernel.CreateAreaAsync(new("Project"), ct);
        var areaPlanners = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => kernel.GetOrEnsureMemoryTrackAsync(area.Id, ct)));
        var globalPlanners = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => kernel.GetOrEnsureMemoryTrackAsync(null, ct)));
        var areaPlanner = Assert.Single(areaPlanners.Distinct());
        var globalPlanner = Assert.Single(globalPlanners.Distinct());
        Assert.NotEqual(areaPlanner.TrackId, globalPlanner.TrackId);
        Assert.Equal(areaPlanner.TrackId, await harness.Store.GetMemoryTrackIdAsync($"area:{area.Id}", ct));
        Assert.Equal(new MemoryTrackBinding($"area:{area.Id}", area.Id, areaPlanner.TrackId),
            await harness.Store.FindMemoryBindingByTrackIdAsync(areaPlanner.TrackId, ct));

        var systemTracks = await harness.Store.ListTracksIncludingSystemAsync(MemoryTrackBinding.SystemAreaId, ct: ct);
        Assert.Equal(2, systemTracks.Count);
        Assert.All(systemTracks, track => Assert.Equal(VcsWorkspaceDriver.Id, track.WorkspaceDriver));
        Assert.Equal(area.Id, Assert.Single(await kernel.ListAreasAsync(ct)).Id);
        Assert.Empty(await kernel.ListTracksAsync(includeClosed: true, ct: ct));
        Assert.Empty((await kernel.GetOverlaysAsync(includeClosed: true, ct: ct)).Tracks);

        var daily = await kernel.GetDailyTrackAsync(null, ct);
        Assert.NotNull(daily);
        Assert.DoesNotContain(daily.TrackId, new[] { areaPlanner.TrackId, globalPlanner.TrackId });
        Assert.Null(await harness.Store.FindMemoryBindingByTrackIdAsync(daily.TrackId, ct));
        Assert.Equal(daily.TrackId, Assert.Single(await harness.Store.ListTracksAsync("sys_daily", ct: ct)).Id);
        Assert.Empty(await harness.Store.ListTracksAsync(MemoryTrackBinding.SystemAreaId, ct: ct));
        Assert.Equal(daily.TrackId, Assert.Single(await harness.Store.ListTrackOverlaysAsync(ct: ct)).TrackId);
        var dailyWorkspace = await kernel.Actors.GetOrActivate(daily.TrackId).EnsureMountedAsync(ct);
        var memoryWorkspace = await kernel.Actors.GetOrActivate(areaPlanner.TrackId).EnsureMountedAsync(ct);
        Assert.Equal(DirectWorkspaceDriver.Id, dailyWorkspace.DriverId);
        Assert.Equal(VcsWorkspaceDriver.Id, memoryWorkspace.DriverId);
        Assert.True(dailyWorkspace.ManagedEmptyWorkspace && memoryWorkspace.ManagedEmptyWorkspace);
        Assert.Null(memoryWorkspace.ProcessOverlayLayers);
        await Assert.ThrowsAsync<KernelException>(() => kernel.GetOrEnsureMemoryTrackAsync("sys_daily", ct));
        await Assert.ThrowsAsync<KernelException>(() => kernel.GetOrEnsureMemoryTrackAsync(MemoryTrackBinding.SystemAreaId, ct));

        await kernel.WaitForTrackIdleAsync(areaPlanner.TrackId, ct);
        await kernel.DeleteAreaAsync(area.Id, ct);
        Assert.Null(await harness.Store.GetAreaAsync(area.Id, ct));
        Assert.Null(await harness.Store.GetTrackAsync(areaPlanner.TrackId, ct));
        Assert.Null(await harness.Store.GetMemoryTrackIdAsync($"area:{area.Id}", ct));
        Assert.False(kernel.Actors.IsTrackActiveInMemory(areaPlanner.TrackId));
        Assert.Equal(globalPlanner, await kernel.GetOrEnsureMemoryTrackAsync(null, ct));
    }

    [Fact]
    public async Task TodayCalendarKeepsUserAndDailyFlowSchedulesAndHidesMemorySchedules()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        await using var kernel = CreateKernel(harness);
        var area = await kernel.CreateAreaAsync(new("Project"));
        var userTrack = await kernel.CreateTrackAsync(new(area.Id, Title: "User"));
        var daily = await kernel.GetDailyTrackAsync(null);
        Assert.NotNull(daily);
        var memory = await kernel.GetOrEnsureMemoryTrackAsync(null);
        using var db = harness.OpenRawInspectConnection();
        await db.ExecuteAsync("""
            INSERT INTO track_schedules (id, track_id, source_kind, schedule_kind, title, prompt, timezone,
                run_at_ms, next_fire_at_ms, state, created_at_ms, updated_at_ms, node_key)
            VALUES ('user-clock', @UserTrack, 'template', 'once', 'User', 'User', 'UTC', 100, 100, 'active', 1, 1, 'user'),
                   ('daily-clock', @DailyTrack, 'template', 'once', 'Daily', 'Daily', 'UTC', 200, 200, 'active', 1, 1, 'daily'),
                   ('memory-clock', @MemoryTrack, 'template', 'once', 'Memory', 'Memory', 'UTC', 300, 300, 'active', 1, 1, 'memory');
            """, new { UserTrack = userTrack.Track.Id, DailyTrack = daily.TrackId, MemoryTrack = memory.TrackId });

        var schedules = await harness.Store.ListCalendarSchedulesInWindowAsync(0, 400, default);
        Assert.Equal([userTrack.Track.Id, daily.TrackId], schedules.Select(schedule => schedule.TrackId));
    }

    [Fact]
    public async Task StaleAreaPatchCannotResurrectADeletedArea()
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        await using var kernel = CreateKernel(harness);
        var stale = await kernel.CreateAreaAsync(new("Project"));
        await kernel.DeleteAreaAsync(stale.Id);

        var patchError = await Assert.ThrowsAsync<KernelException>(() => kernel.PatchAreaAsync(stale.Id, new(Name: "Renamed")));
        Assert.Equal(KernelErrorCode.NotFound, patchError.Code);
        // This is the write phase of a PATCH that already read the Area before the delete.
        var writeError = await Assert.ThrowsAsync<KernelException>(() =>
            harness.Store.UpdateAreaAsync(stale with { Name = "Renamed" }, default));
        Assert.Equal(KernelErrorCode.NotFound, writeError.Code);
        Assert.Null(await harness.Store.GetAreaAsync(stale.Id, default));
    }

    private static PergaKernelService CreateKernel(StorageTestHarness harness)
    {
        var vcs = new VcsService();
        return new(harness.Store, new ReportDocEngine(), new Perga.PolicyVm.PolicyVm(), new NoAgentRuntime(),
            new WorkspaceDriverRegistry([new DirectWorkspaceDriver(), new VcsWorkspaceDriver(vcs)]), vcs,
            new TerminalBridgeClient(new Uri("http://localhost")), new KernelOptions { DataDir = harness.DataDir });
    }

    private sealed class NoAgentRuntime : IAgentRuntime
    {
        public Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderStatus>>([]);

        public IAgentSession CreateSession(HarnessLaunchSpec spec) =>
            throw new InvalidOperationException("Ensuring a Memory Planner requires no agent.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
