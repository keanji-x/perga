using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Perga.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class TrackMailRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task VersionThirteenUpgradeWakesOnlyOpenTrackMailOnceWithExactPrompt(bool failUpgrade, bool upgradeMail)
    {
        var directory = Directory.CreateTempSubdirectory("perga-mail-v13-");
        var path = Path.Combine(directory.FullName, "perga.db");
        var mail = new MailDto("mail-'quoted", "sender-'quoted", "open", "Review \"diff\"\nagain",
            "Body with 'single', \"double\", \\ slash\nand newlines\n雪", null, 123);
        if (upgradeMail) mail = mail with { Id = "system-upgrade:legacy", FromTrackId = "system:upgrade" };
        try
        {
            var released = SqliteMigrator.LoadEmbeddedMigrations().Where(m => m.Version <= 13).ToArray();
            await new SqliteMigrator(released).MigrateAsync(path);
            await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await db.OpenAsync();
                await db.ExecuteAsync("""
                    INSERT INTO areas(id,name,color,created_at_ms,updated_at_ms) VALUES('area','Area','#000000',1,1);
                    INSERT INTO tracks(id,area_id,cwd,lead_session_id,closed_at_ms,created_at_ms,updated_at_ms)
                    VALUES('open','area',@Cwd,'lead-open',NULL,1,1),('closed','area',@Cwd,'lead-closed',2,1,1);
                    INSERT INTO agent_sessions(id,track_id,provider,created_at_ms,updated_at_ms)
                    VALUES('lead-open','open','codex',1,1),('lead-closed','closed','codex',1,1);
                    INSERT INTO mails(id,from_track_id,to_track_id,summary,body,wake_pending,created_at_ms)
                    VALUES(@Id,@FromTrackId,@ToTrackId,@Summary,@Body,1,@CreatedAtMs),
                          ('closed-mail',@FromTrackId,'closed',@Summary,@Body,1,@CreatedAtMs);
                    """, new { Cwd = directory.FullName, mail.Id, mail.FromTrackId, mail.ToTrackId, mail.Summary, mail.Body, mail.CreatedAtMs });
            }
            if (upgradeMail)
            {
                await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
                await db.OpenAsync();
                await db.ExecuteAsync("INSERT INTO system_upgrade_receipts(job_id,receipt_json,consumed_at_ms,notification_status) VALUES('legacy','{}',123,'queued');");
            }
            if (failUpgrade)
            {
                await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
                await db.OpenAsync();
                await db.ExecuteAsync("CREATE TRIGGER reject_v14 BEFORE INSERT ON schema_migrations WHEN NEW.version=14 BEGIN SELECT RAISE(ABORT, 'injected receipt failure'); END;");
                await Assert.ThrowsAsync<SqliteException>(() => new SqliteMigrator().MigrateAsync(path));
                Assert.Equal(13, await db.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
                Assert.Equal(2, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mails WHERE wake_pending=1;"));
                Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='track_wakes';"));
                await db.ExecuteAsync("DROP TRIGGER reject_v14;");
            }
            await new SqliteMigrator().MigrateAsync(path);
            await new SqliteMigrator().MigrateAsync(path);
            await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await db.OpenAsync();
                Assert.Equal(25, await db.ExecuteScalarAsync<int>("SELECT MAX(version) FROM schema_migrations;"));
                Assert.Equal(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM mails WHERE wake_pending=1;"));
                var json = await db.QuerySingleAsync<string>("SELECT payload_json FROM track_wakes;");
                using var payload = JsonDocument.Parse(json);
                var expected = TrackWakeRequests.Mail(mail).Payload;
                Assert.Equal(expected.GetProperty("prompt").GetString(), payload.RootElement.GetProperty("prompt").GetString());
                Assert.Equal("system_mail", payload.RootElement.GetProperty("presentation").GetString());
                foreach (var migration in released)
                    Assert.Equal(migration.ChecksumSha256, await db.ExecuteScalarAsync<string>(
                        "SELECT checksum_sha256 FROM schema_migrations WHERE version=@Version;", migration));
            }
            await using (var reopened = new PergaHostTestCluster(directory.FullName))
            {
                var kernel = reopened.Services.GetRequiredService<IPergaKernel>();
                await kernel.WaitForTrackIdleAsync("open");
                await kernel.WaitForTrackIdleAsync("closed");
                Assert.Equal(TrackWakeRequests.Mail(mail).Payload.GetProperty("prompt").GetString(),
                    Assert.Single(reopened.AgentRuntime.ExecutedTurns).Input.Prompt);
                var store = reopened.Services.GetRequiredService<ITrackStore>();
                Assert.Equal("done", Assert.Single(await store.TrackWakes.ListAsync("open", default)).State);
                Assert.Empty(await store.TrackWakes.ListAsync("closed", default));
                if (upgradeMail)
                {
                    Assert.Equal("system", Assert.Single(await store.ListSessionTurnsAsync("lead-open", default)).TriggerKind);
                    await using var db = new SqliteConnection($"Data Source={path};Pooling=False");
                    Assert.Equal("notified", await db.ExecuteScalarAsync<string>("SELECT notification_status FROM system_upgrade_receipts WHERE job_id='legacy';"));
                }
            }
            await using var again = new PergaHostTestCluster(directory.FullName);
            await again.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync("open");
            Assert.Empty(again.AgentRuntime.ExecutedTurns);
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task RestartWakesLeadForCommittedMailOnce()
    {
        var directory = Directory.CreateTempSubdirectory("perga-mail-");
        try
        {
            string target;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var (sender, _) = await host.CreateTrackAsync();
                (target, _) = await host.CreateTrackAsync();
                // Stop at the production persistence boundary, before the actor schedules its wake.
                var mail = new MailDto("mail-restart", sender, target, "Review", "Look at the diff", null, 1);
                var store = host.Services.GetRequiredService<ITrackStore>();
                await store.UpsertMailAsync(mail, new(null, null, target, "mail.received",
                    JsonSerializer.SerializeToElement(mail), 1), CancellationToken.None);
                // Inbox reads must neither acknowledge the wake nor enqueue another.
                await host.Services.GetRequiredService<IPergaKernel>().ReadMailAsync(target, mail.Id);
            }
            await using (var reopened = new PergaHostTestCluster(directory.FullName))
            {
                await reopened.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(target);
                Assert.Contains("mail-restart", Assert.Single(reopened.AgentRuntime.ExecutedTurns).Input.Prompt);
            }
            await using var again = new PergaHostTestCluster(directory.FullName);
            await again.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(target);
            Assert.Empty(again.AgentRuntime.ExecutedTurns);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ReceivingMailWhileClosedDoesNotScheduleAWakeAfterReopen()
    {
        await using var host = new PergaHostTestCluster();
        var (sender, lead) = await host.CreateTrackAsync();
        var (target, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await kernel.PatchTrackAsync(target, new PatchTrackRequest(Closed: true));
        var mail = await kernel.SendMailAsync(sender, lead, target, "Notice", "For later");
        await kernel.PatchTrackAsync(target, new PatchTrackRequest(Closed: false));
        Assert.Equal(mail.Id, Assert.Single(await kernel.ListMailsAsync(target)).Id);
        Assert.DoesNotContain(await host.Services.GetRequiredService<ITrackStore>().TrackWakes.ListAsync(target, default), w => w.Kind == "mail" && w.State is "pending" or "delivering");
        Assert.Empty(host.AgentRuntime.ExecutedTurns);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RestartRetriesOnlyWithoutDeliveryEvidence(bool conclusive)
    {
        var directory = Directory.CreateTempSubdirectory("perga-mail-failure-");
        try
        {
            string target;
            string mailId;
            await using (var host = new PergaHostTestCluster(directory.FullName))
            {
                var (sender, lead) = await host.CreateTrackAsync();
                (target, _) = await host.CreateTrackAsync();
                host.AgentRuntime.SetTurnHandler(FailTurn);
                async IAsyncEnumerable<UnifiedAgentEvent> FailTurn(HarnessLaunchSpec spec, TurnInput input,
                    [EnumeratorCancellation] CancellationToken ct)
                {
                    await Task.Yield();
                    yield return new UnifiedAgentEvent.TurnError("test", "Scripted failure", false, conclusive);
                }
                var kernel = host.Services.GetRequiredService<IPergaKernel>();
                var mail = await kernel.SendMailAsync(sender, lead, target, "Review", "Look at the diff");
                mailId = mail.Id;
                Assert.Single(host.AgentRuntime.ExecutedTurns);
            }
            await using var recovered = new PergaHostTestCluster(directory.FullName);
            await recovered.Services.GetRequiredService<IPergaKernel>().WaitForTrackIdleAsync(target);
            if (conclusive)
                Assert.Empty(recovered.AgentRuntime.ExecutedTurns);
            else
                Assert.Contains(mailId, Assert.Single(recovered.AgentRuntime.ExecutedTurns).Input.Prompt);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
