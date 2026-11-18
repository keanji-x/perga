using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class TrackMailRecoveryTests
{
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
        Assert.Empty(await host.Services.GetRequiredService<ITrackStore>().ListPendingMailWakesAsync(default));
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
