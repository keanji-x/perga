using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class ReportWakeIntegrationTests
{
    [Fact]
    public async Task OwnerDispatchDoesNotWakeLeadAndHumanEditCommitsWakeAtomically()
    {
        await using var host = new PergaHostTestCluster();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        var (trackId, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        kernel.DispatchAdmission.SetDraining(true);

        await kernel.DispatchWorkerAsync(trackId, new("Inspect the report"), "report-wake", null, ct);
        Assert.DoesNotContain(await store.TrackWakes.ListAsync(trackId, ct), wake => wake.Kind == "report_edited");

        var report = await kernel.ReadReportAsync(trackId, ct: ct);
        var block = report.Blocks[0];
        var update = new HumanBlockUpdateRequest(report.DocRev, block.Rev, Markdown: "Human report edit");
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(host.DataDir, "perga.db"), Pooling = false
        }.ToString());
        await db.ExecuteAsync("CREATE TRIGGER reject_report_wake BEFORE INSERT ON track_wakes WHEN NEW.kind = 'report_edited' BEGIN SELECT RAISE(ABORT, 'outbox failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => kernel.UpdateReportBlockByHumanAsync(trackId, block.Id, update, ct));
        Assert.Equal(report.DocRev, (await kernel.ReadReportAsync(trackId, ct: ct)).DocRev);
        Assert.Equal(report.DocRev, (await store.LoadTrackAggregateAsync(trackId, ct))!.Report.DocRev);
        Assert.DoesNotContain(await store.TrackWakes.ListAsync(trackId, ct), wake => wake.Kind == "report_edited");

        await db.ExecuteAsync("DROP TRIGGER reject_report_wake;");
        var edited = await kernel.UpdateReportBlockByHumanAsync(trackId, block.Id, update, ct);
        Assert.Equal(report.DocRev + 1, edited.DocRev);
        Assert.Equal(edited.DocRev, (await store.LoadTrackAggregateAsync(trackId, ct))!.Report.DocRev);
        Assert.Equal("pending", Assert.Single(await store.TrackWakes.ListAsync(trackId, ct), wake => wake.Kind == "report_edited").State);
    }
}
