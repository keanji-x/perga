using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
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
        var (parentId, leadId) = await host.CreateTrackAsync();
        var caller = await kernel.OpenMcpCallerAsync(leadId, ct);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootReplaysInterruptedTrackSpawnWithOriginalTrackAndPromptKeys(bool promptAlreadyDurable)
    {
        var directory = Directory.CreateTempSubdirectory("perga-flow-track-recovery-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var connectionString = $"Data Source={Path.Combine(directory.FullName, "perga.db")};Pooling=False";
        try
        {
            string parentId;
            string childId;
            string childLeadId;
            string promptKey;
            string? priorMessageId;
            await using (var host = NewHost(directory.FullName))
            {
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var store = host.Services.GetRequiredService<ITrackStore>();
                (parentId, _) = await host.CreateTrackAsync();

                await using var db = new SqliteConnection(connectionString);
                await db.OpenAsync(ct);
                await db.ExecuteAsync("""
                    CREATE TRIGGER stop_before_parent_settlement BEFORE UPDATE OF status ON tasks
                    WHEN NEW.kind = 'flow_track' AND NEW.status IN ('done', 'failed')
                    BEGIN SELECT RAISE(ABORT, 'injected parent settlement failure'); END;
                    """);
                if (!promptAlreadyDurable)
                {
                    await db.ExecuteAsync("""
                        CREATE TRIGGER stop_before_initial_prompt BEFORE INSERT ON session_messages
                        WHEN substr(NEW.request_key, 1, 18) = 'flow_track_prompt:'
                        BEGIN SELECT RAISE(ABORT, 'injected initial prompt failure'); END;
                        """);
                }

                var commit = await kernel.CommitTrackFlowAsync(parentId, false, 0, new InteropPrincipal.Human(), ct, content: Flow);
                Assert.True(commit.Ok, string.Join(Environment.NewLine, commit.Diagnostics));
                try
                {
                    await kernel.WaitForTrackIdleAsync(parentId, ct);
                }
                catch (SqliteException e) when (e.Message.Contains("injected parent settlement failure", StringComparison.Ordinal)) { }

                var parent = await store.LoadTrackAggregateAsync(parentId, ct);
                var interrupted = Assert.Single(parent!.Tasks);
                Assert.Equal(TaskRunStatus.Running, interrupted.Status);
                Assert.Equal(1, interrupted.RunCount);
                var child = Assert.Single(await kernel.ListTracksAsync(ct: ct), candidate => candidate.OriginTrackId == parentId);
                childId = child.Id;
                childLeadId = child.LeadSessionId!;
                var creationKey = $"flow_track:{parentId}:{interrupted.Id}:r1";
                promptKey = $"flow_track_prompt:{parentId}:{interrupted.Id}:r1";
                Assert.Equal(childId, await db.QuerySingleAsync<string>(
                    "SELECT resource_id FROM creation_receipts WHERE request_key = @creationKey", new { creationKey }));
                await kernel.WaitForTrackIdleAsync(childId, ct);
                priorMessageId = await db.QuerySingleOrDefaultAsync<string>(
                    "SELECT id FROM session_messages WHERE session_id = @childLeadId AND request_key = @promptKey",
                    new { childLeadId, promptKey });
                Assert.Equal(promptAlreadyDurable, priorMessageId is not null);
                Assert.Equal(promptAlreadyDurable ? 1 : 0, host.AgentRuntime.ExecutedTurns.Count(turn => turn.Input.Prompt == Prompt));

                await db.ExecuteAsync("DROP TRIGGER stop_before_parent_settlement; DROP TRIGGER IF EXISTS stop_before_initial_prompt;");
            }

            await using var reopened = NewHost(directory.FullName);
            var restarted = reopened.Services.GetRequiredService<IPergaKernel>();
            await restarted.WaitForTrackIdleAsync(parentId, ct);
            var archived = Assert.Single((await restarted.GetTrackFlowAsync(parentId, ct)).Projection.Archived);
            Assert.Equal((childId, "ok", 1), (archived.ChildTrackId, archived.Outcome, archived.RunCount));
            Assert.Equal(childId, Assert.Single(await restarted.ListTracksAsync(ct: ct), candidate => candidate.OriginTrackId == parentId).Id);

            await restarted.WaitForTrackIdleAsync(childId, ct);
            Assert.Equal(promptAlreadyDurable ? 0 : 1, reopened.AgentRuntime.ExecutedTurns.Count(turn => turn.Input.Prompt == Prompt));
            await using var reopenedDb = new SqliteConnection(connectionString);
            var messageId = await reopenedDb.QuerySingleAsync<string>(
                "SELECT id FROM session_messages WHERE session_id = @childLeadId AND request_key = @promptKey",
                new { childLeadId, promptKey });
            if (promptAlreadyDurable)
                Assert.Equal(priorMessageId, messageId);
            Assert.Equal(1, await reopenedDb.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM session_messages WHERE session_id = @childLeadId AND content = @prompt",
                new { childLeadId, prompt = Prompt }));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<T> CallAsync<T>(PergaHostTestCluster host, string token, string tool, object args, CancellationToken ct)
    {
        var (result, error) = await CallToolAsync(host, "/mcp/kernel", token, tool, args, ct);
        Assert.False(error, result);
        return JsonSerializer.Deserialize<T>(result, PergaJsonDefaults.ApiOptions)!;
    }

    private static PergaHostTestCluster NewHost(string directory) =>
        new(explicitDataDir: directory, configureHostOptions: options =>
        {
            options.EnableScheduleService = false;
            options.EnableMaintenanceService = false;
        });
}
