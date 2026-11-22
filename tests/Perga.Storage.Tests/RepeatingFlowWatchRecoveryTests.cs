using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.ReportDoc;
using Perga.Storage.Sqlite;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Storage.Tests;

public sealed class RepeatingFlowWatchRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootResumesPatrolOrRearmsAConvergedWave(bool converged)
    {
        await using var harness = await StorageTestHarness.CreateAsync();
        var startedAt = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        string trackId;
        await using (var kernel = NewKernel(harness.Store, harness.DataDir, startedAt, probeExit: converged ? 0 : 75))
        {
            var area = await kernel.CreateAreaAsync(new("Flow area", DefaultCwd: harness.DataDir));
            trackId = (await kernel.CreateTrackAsync(new(area.Id, Cwd: harness.DataDir, WorkspaceDriver: DirectWorkspaceDriver.Id))).Track.Id;
            await kernel.Actors.GetOrActivate(trackId).EnsureMountedAsync();
            var commit = await kernel.CommitTrackFlowAsync(trackId, false, 0, new InteropPrincipal.Human(), content: """
                {"rev":0,"nodes":[
                  {"id":"watch","do":{"kind":"watch","cmd":"probe","repeat":true,"interval":"5m"}},
                  {"id":"consumer","when":{"after":["watch"]},"do":{"kind":"run","cmd":"consume"}}
                ]}
                """);
            Assert.True(commit.Ok, string.Join(Environment.NewLine, commit.Diagnostics));
            await WaitForStoredStateAsync(harness.Store, trackId, (watch, consumer) => converged
                ? watch.Status == TaskRunStatus.Done && watch.RunCount == 1 && consumer.Status == TaskRunStatus.Done
                : watch.Status == TaskRunStatus.Running && watch.RunCount == 0 &&
                    watch.Meta is { } meta && meta.GetProperty("watchProgress").TryGetProperty("lastCheckedAtMs", out _));
        }

        await using var reopened = new SqliteTrackStore(new SqliteStorageOptions { DbPath = harness.DbPath, DataDir = harness.DataDir });
        await reopened.InitializeAsync();
        await using var restarted = NewKernel(reopened, harness.DataDir, startedAt.AddMinutes(10), probeExit: 0);
        var boot = await restarted.BootReconciler.ReconcileAsync();
        Assert.Empty(boot.AffectedTrackIds);
        Assert.Equal(0, boot.WokenTracks);

        var expectedRuns = converged ? 2 : 1;
        await WaitForStoredStateAsync(reopened, trackId, (watch, consumer) =>
            watch.Status == TaskRunStatus.Done && watch.Outcome == FlowOutcome.Ok && watch.RunCount == expectedRuns &&
            consumer.Status == TaskRunStatus.Done && consumer.RunCount == expectedRuns);
        var flow = await restarted.GetTrackFlowAsync(trackId);
        Assert.Equal(1, flow.FlowRev);
        Assert.Equal(2, flow.Projection.Nodes.Count);
        Assert.Empty(flow.Projection.Archived);
        var projected = Assert.Single(flow.Projection.Nodes, node => node.Id == "watch");
        Assert.True(projected.Repeat);
        Assert.Null(projected.WatchProgress?.TimeoutAtMs);
    }

    private static async Task WaitForStoredStateAsync(SqliteTrackStore store, string trackId, Func<TaskDto, TaskDto, bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var snapshot = (await store.LoadTrackAggregateAsync(trackId, timeout.Token))!;
            var watch = Assert.Single(snapshot.Tasks, task => task.TaskKey == "watch");
            var consumer = Assert.Single(snapshot.Tasks, task => task.TaskKey == "consumer");
            if (ready(watch, consumer))
                return;
            await Task.Delay(15, timeout.Token);
        }
    }

    private static PergaKernelService NewKernel(SqliteTrackStore store, string dataDir, DateTimeOffset now, int probeExit) =>
        new(store, new ReportDocEngine(), new Perga.PolicyVm.PolicyVm(), new NoAgentRuntime(),
            new WorkspaceDriverRegistry([new DirectWorkspaceDriver()]), new VcsService(),
            new TerminalBridgeClient(new Uri("http://localhost")),
            options: new KernelOptions { DataDir = dataDir, ActorEvictionSweepInterval = TimeSpan.Zero },
            commandRunner: new ProbeRunner(probeExit), timeProvider: new FixedClock(now));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ProbeRunner(int probeExit) : IShellCommandRunner
    {
        public Task<ShellCommandResult> RunAsync(ShellCommandRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ShellCommandResult(request.Command == "probe" ? probeExit : 0, "", "", false, 1));
    }

    private sealed class NoAgentRuntime : IAgentRuntime
    {
        public Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProviderStatus>>([]);

        public IAgentSession CreateSession(HarnessLaunchSpec spec) =>
            throw new InvalidOperationException("Command watches and command runs require no agent.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
