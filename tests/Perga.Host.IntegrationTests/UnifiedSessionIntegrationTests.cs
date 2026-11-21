using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class UnifiedSessionIntegrationTests
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskPreallocatesSessionAndRetryReusesIdentityWithEpochBoundary(bool blocked)
    {
        await using var host = new PergaHostTestCluster();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, _) = await host.CreateTrackAsync();
        SessionCreationReceipt? dependency = null;
        if (blocked)
        {
            dependency = (await kernel.CreateSessionAsync(trackId, new(Prompt: "dependency", Task: new()), "dependency", Human, ct)).Receipt;
            await kernel.WaitForTrackIdleAsync(trackId, ct);
        }
        var request = new CreateSessionRequest(Prompt: "inspect", Task: new(DependsOn: dependency is null ? null : [dependency.Task!.TaskKey]));
        var created = await kernel.CreateSessionAsync(trackId, request, "inspect", Human, ct);
        var id = created.Receipt.Session.Id;
        Assert.False(created.Replay);
        Assert.Equal(id, created.Receipt.Task!.SessionId);
        if (blocked)
        {
            Assert.Equal(0, created.Receipt.Task.RunCount);
            var page = await kernel.GetSessionItemsAsync(id, new(), Human, ct);
            Assert.Equal("waiting_dependencies", page.TaskBinding!.WaitingReason);
            await kernel.CompleteTaskDoneAsync(trackId, dependency!.Session.Id, ct: ct);
        }
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var firstPage = await kernel.GetSessionItemsAsync(id, new(), Human, ct);
        var first = firstPage.Session;
        Assert.Equal(1, firstPage.TaskBinding!.Task.RunCount);
        await kernel.CompleteTaskDoneAsync(trackId, id, ct: ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        await kernel.RejectTaskAsync(trackId, null, created.Receipt.Task.Id, "try again", reQueue: true, ct: ct);
        await kernel.WaitForTrackIdleAsync(trackId, ct);
        var retriedPage = await kernel.GetSessionItemsAsync(id, new(), Human, ct);
        var retried = retriedPage.Session;
        Assert.Equal(first.Epoch + 1, retried.Epoch);
        Assert.Equal(2, retriedPage.TaskBinding!.Task.RunCount);
        var replay = await kernel.CreateSessionAsync(trackId, request, "inspect", Human, ct);
        Assert.True(replay.Replay);
        Assert.Equal(id, replay.Receipt.Session.Id);
        Assert.Single(await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default), s => s.TaskId == created.Receipt.Task.Id);
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        Assert.Equal(1, await db.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM session_items WHERE session_id=@Id AND kind='epoch_boundary' AND json_extract(payload_json,'$.reason')='new_run';
            """, new { Id = id }));
    }

    [Fact]
    public async Task SessionInsertFailureRollsBackTaskReportAndCreationReceipt()
    {
        await using var host = new PergaHostTestCluster();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var (trackId, leadId) = await host.CreateTrackAsync();
        var before = await kernel.ReadReportAsync(trackId, leadId);
        await using var db = new SqliteConnection($"Data Source={Path.Combine(host.DataDir, "perga.db")};Pooling=False");
        await db.ExecuteAsync("""
            CREATE TRIGGER reject_task_session BEFORE INSERT ON agent_sessions
            WHEN NEW.is_lead = 0 BEGIN SELECT RAISE(ABORT,'test allocation failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => kernel.CreateSessionAsync(trackId,
            new(Prompt: "inspect", Task: new()), "allocation", Human, default));
        Assert.Empty(await kernel.ListTasksAsync(trackId));
        Assert.Single((await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray());
        Assert.Equal(before.DocRev, (await kernel.ReadReportAsync(trackId, leadId)).DocRev);
        Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT count(*) FROM creation_receipts WHERE request_key='allocation';"));
    }
}
