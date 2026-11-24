using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using static Perga.Host.IntegrationTests.McpKernelAndVcsIntegrationTests;

namespace Perga.Host.IntegrationTests;

public sealed class FlowTrackIntegrationTests
{
    private const string Prompt = "Investigate the cache design";
    private const string Flow = """
        {"rev":0,"nodes":[{
          "id":"research","do":{"kind":"track","title":"Cache Research","prompt":"Investigate the cache design"}
        }]}
        """;

    [Fact]
    public async Task LeadMcpCommitSpawnsTrackAndDiscoveryReturnsLineageAfterGc()
    {
        await using var host = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var parent = await CreateParentAsync(host, kernel, ct);
        var parentId = parent.Track.Id;
        var caller = await kernel.OpenMcpCallerAsync(parent.Track.LeadSessionId!, ct);

        var preview = await CallAsync<FlowCommitResultDto>(host, caller.Token, KernelMcpToolNames.FlowCommit,
            new { content = Flow, dryRun = true, expectedFlowRev = 0 }, ct);
        Assert.True(preview.Ok);
        Assert.Single(await kernel.ListTracksAsync(ct: ct));
        var commit = await CallAsync<FlowCommitResultDto>(host, caller.Token, KernelMcpToolNames.FlowCommit,
            new { content = Flow, expectedFlowRev = 0 }, ct);
        Assert.True(commit.Ok, string.Join(Environment.NewLine, commit.Diagnostics));
        await kernel.WaitForTrackIdleAsync(parentId, ct);
        var archive = Assert.Single((await kernel.GetTrackFlowAsync(parentId, ct)).Projection.Archived);
        Assert.Equal(("research", "track", "done", "ok", 1),
            (archive.Id, archive.DoKind, archive.Status, archive.Outcome, archive.RunCount));

        var discovery = await CallAsync<TrackDiscoveryResult>(host, caller.Token, KernelMcpToolNames.TrackList,
            new { query = "Cache Research" }, ct);
        var child = Assert.Single(discovery.Tracks);
        Assert.Equal(archive.ChildTrackId, child.Id);
        Assert.Equal(parentId, child.OriginTrackId);
        Assert.Equal("Cache Research", child.Title);
        await kernel.WaitForTrackIdleAsync(child.Id, ct);

        var launch = Assert.Single(host.AgentRuntime.ExecutedTurns, turn => turn.Input.Prompt == Prompt);
        Assert.Contains(parentId, launch.Spec.SystemPrompt);
        Assert.Contains("report_read(trackId:", launch.Spec.SystemPrompt);
        Assert.Contains("mail_send(toTrackId:", launch.Spec.SystemPrompt);
    }

    [Fact]
    public async Task FirstInputFailureRollsBackTrackAndCreationReceipt()
    {
        await using var host = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var parent = await CreateParentAsync(host, kernel, ct);
        var spawner = (IFlowTrackSpawner)kernel;
        await using var db = OpenDatabase(host.DataDir);
        await db.ExecuteAsync("""
            CREATE TRIGGER reject_flow_first_input BEFORE INSERT ON session_messages
            WHEN NEW.request_key GLOB 'flow_track_prompt:*'
            BEGIN SELECT RAISE(ABORT, 'injected initial prompt failure'); END;
            """);

        var action = new FlowDoSpecDto("track", Prompt: Prompt);
        var failure = await Assert.ThrowsAsync<SqliteException>(() =>
            spawner.SpawnAsync(parent.Track, Assert.Single(parent.Sessions), "research", 1, action, ct));
        Assert.Contains("injected initial prompt failure", failure.Message);
        Assert.Equal(parent.Track.Id, Assert.Single(await kernel.ListTracksAsync(ct: ct)).Id);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM creation_receipts WHERE operation = 'flow_track'"));
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM agent_sessions"));
    }

    [Fact]
    public async Task BootDispatchesTheOriginalAtomicFirstInputExactlyOnce()
    {
        var directory = Directory.CreateTempSubdirectory("perga-flow-track-recovery-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        try
        {
            TrackAggregateSnapshot parent;
            TrackDto child;
            PersistedSessionInput input;
            const string taskId = "research";
            var action = new FlowDoSpecDto("track", Prompt: Prompt);
            string inputKey;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var store = host.Services.GetRequiredService<ITrackStore>();
                parent = await CreateParentAsync(host, kernel, ct);

                // Hold provider dispatch so shutdown leaves the real creation transaction committed
                // with its first input still unclaimed.
                kernel.DispatchAdmission.SetDraining(true);
                child = await ((IFlowTrackSpawner)kernel).SpawnAsync(parent.Track, Assert.Single(parent.Sessions),
                    taskId, 1, action, ct);
                var runKey = $"{parent.Track.Id}:{taskId}:r1";
                var creationKey = $"flow_track:{runKey}";
                inputKey = $"flow_track_prompt:{runKey}";
                input = Assert.IsType<PersistedSessionInput>(
                    await store.SessionInputs.FindByKeyAsync(child.LeadSessionId!, inputKey, ct));
                Assert.Equal(("accepted", "queued", Prompt), (input.Receipt.DeliveryState, input.Receipt.TurnStatus, input.Content));
                Assert.Empty(host.AgentRuntime.ExecutedTurns);
                await using var db = OpenDatabase(directory.FullName);
                Assert.Equal((child.Id, input.Receipt.InputId), await db.QuerySingleAsync<(string, string)>(
                    "SELECT resource_id, first_input_id FROM creation_receipts WHERE request_key = @creationKey",
                    new { creationKey }));
            }

            await using var reopened = new PergaHostTestCluster(directory.FullName);
            var restarted = reopened.Services.GetRequiredService<IPergaKernel>();
            var reopenedStore = reopened.Services.GetRequiredService<ITrackStore>();
            await restarted.WaitForTrackIdleAsync(child.Id, ct);

            var execution = Assert.Single(reopened.AgentRuntime.ExecutedTurns);
            Assert.Equal((Prompt, input.Receipt.TurnId), (execution.Input.Prompt, execution.Input.CorrelationId));
            var replay = await ((IFlowTrackSpawner)restarted).SpawnAsync(parent.Track, Assert.Single(parent.Sessions),
                taskId, 1, action, ct);
            Assert.Equal(child.Id, replay.Id);
            await restarted.WaitForTrackIdleAsync(child.Id, ct);

            var completed = Assert.IsType<PersistedSessionInput>(
                await reopenedStore.SessionInputs.FindByKeyAsync(child.LeadSessionId!, inputKey, ct));
            Assert.Equal((input.Receipt.InputId, "completed"), (completed.Receipt.InputId, completed.Receipt.TurnStatus));
            Assert.Single(reopened.AgentRuntime.ExecutedTurns);
            Assert.Equal(child.Id, Assert.Single(await restarted.ListTracksAsync(ct: ct), candidate => candidate.OriginTrackId == parent.Track.Id).Id);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FlowTrackUsesIsolatedWorkspaceByDefaultAndPreservesExplicitDirect()
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options => options.DefaultWorkspaceDriver = "direct");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var parent = await CreateParentAsync(host, kernel, ct);
        var source = parent.Track.Cwd;
        await File.WriteAllTextAsync(Path.Combine(source, "baseline.txt"), "source file", ct);

        var commit = await kernel.CommitTrackFlowAsync(parent.Track.Id, false, 0, new InteropPrincipal.Human(), ct, content: """
            {"rev":0,"nodes":[
                {"id":"isolated","do":{"kind":"track","prompt":"Use an isolated copy"}},
                {"id":"direct","do":{"kind":"track","prompt":"Use the source folder","workspaceDriver":"direct"}}
            ]}
            """);
        Assert.True(commit.Ok, string.Join(Environment.NewLine, commit.Diagnostics));
        await kernel.WaitForTrackIdleAsync(parent.Track.Id, ct);
        var archived = (await kernel.GetTrackFlowAsync(parent.Track.Id, ct)).Projection.Archived;

        var isolated = await ReadChildAsync("isolated");
        Assert.Equal("vcs-workspace", isolated.WorkspaceDriver);
        Assert.NotEqual(source, isolated.Cwd);
        Assert.Equal("source file", await File.ReadAllTextAsync(Path.Combine(isolated.Cwd, "baseline.txt"), ct));

        var direct = await ReadChildAsync("direct");
        Assert.Equal(("direct", source), (direct.WorkspaceDriver, direct.Cwd));

        async Task<TrackDto> ReadChildAsync(string nodeId)
        {
            var node = Assert.Single(archived, candidate => candidate.Id == nodeId);
            Assert.Equal("ok", node.Outcome);
            await kernel.WaitForTrackIdleAsync(node.ChildTrackId!, ct);
            return (await kernel.GetTrackAggregateAsync(node.ChildTrackId!, ct)).Track;
        }
    }

    [Fact]
    public async Task DeletingParentClearsTheHydratedChildAndPublishesItsUpdatedTrack()
    {
        await using var host = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var parent = await CreateParentAsync(host, kernel, ct);
        var child = await ((IFlowTrackSpawner)kernel).SpawnAsync(parent.Track, Assert.Single(parent.Sessions),
            "research", 1, new("track", Prompt: Prompt), ct);
        await kernel.WaitForTrackIdleAsync(child.Id, ct);

        var childActor = kernel.Actors.GetOrActivate(child.Id);
        Assert.Equal(parent.Track.Id, (await childActor.GetAggregateSnapshotAsync(ct)).Track.OriginTrackId);
        var cursor = await store.GetEventTipIdAsync(ct);
        using var socket = await host.ConnectEventsWebSocketAsync($"?since={cursor}&trackId={child.Id}", ct);
        using var ready = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, ct);
        Assert.Equal("_replay_complete", ready.RootElement.GetProperty("kind").GetString());

        await kernel.DeleteTrackAsync(parent.Track.Id, ct);

        Assert.Null((await store.GetTrackAsync(child.Id, ct))!.OriginTrackId);
        Assert.Same(childActor, kernel.Actors.GetOrActivate(child.Id));
        Assert.Null((await childActor.GetAggregateSnapshotAsync(ct)).Track.OriginTrackId);
        using var frame = await PergaHostTestCluster.ReceiveTextJsonFrameAsync(socket, ct);
        Assert.Equal(KernelEventKinds.TrackUpdated, frame.RootElement.GetProperty("kind").GetString());
        var updated = frame.RootElement.GetProperty("payload").Deserialize<TrackDto>(PergaJsonDefaults.ApiOptions)!;
        Assert.Equal(child.Id, updated.Id);
        Assert.Null(updated.OriginTrackId);
    }

    private static async Task<TrackAggregateSnapshot> CreateParentAsync(PergaHostTestCluster host, IPergaKernel kernel, CancellationToken ct)
    {
        var source = Directory.CreateDirectory(Path.Combine(host.DataDir, "source")).FullName;
        var area = await kernel.CreateAreaAsync(new("Flow source", DefaultCwd: source), ct);
        var parent = await kernel.CreateTrackAsync(new(area.Id), ct);
        await kernel.WaitForTrackIdleAsync(parent.Track.Id, ct);
        return await kernel.GetTrackAggregateAsync(parent.Track.Id, ct);
    }

    private static SqliteConnection OpenDatabase(string directory) =>
        new($"Data Source={Path.Combine(directory, "perga.db")};Pooling=False");

    private static async Task<T> CallAsync<T>(PergaHostTestCluster host, string token, string tool, object args, CancellationToken ct)
    {
        var (result, error) = await CallToolAsync(host, "/mcp/kernel", token, tool, args, ct);
        Assert.False(error, result);
        return JsonSerializer.Deserialize<T>(result, PergaJsonDefaults.ApiOptions)!;
    }

}
