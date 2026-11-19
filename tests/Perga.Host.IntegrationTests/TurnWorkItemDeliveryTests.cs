using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class TurnWorkItemDeliveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MailAcknowledgesDeliveryBeforeSettlementButKeepsBeforePromptRejection(bool beforePrompt)
    {
        await using var host = new PergaHostTestCluster();
        var (sender, senderLead) = await host.CreateTrackAsync();
        var (target, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler((_, _, ct) => Delivery(ct));
        async IAsyncEnumerable<UnifiedAgentEvent> Delivery([EnumeratorCancellation] CancellationToken ct)
        {
            if (beforePrompt)
                yield return new UnifiedAgentEvent.TurnError("rejected", "Before prompt", false, IsConclusive: true, BeforePrompt: true);
            else
                yield return new UnifiedAgentEvent.TurnAccepted("accepted");
            // The runner has processed delivery evidence before it requests the next event.
            observed.TrySetResult();
            await release.Task.WaitAsync(ct);
            if (!beforePrompt) yield return new UnifiedAgentEvent.TurnCompleted(null);
        }
        var send = kernel.SendMailAsync(sender, senderLead, target, "Notice", "Body");
        try
        {
            await observed.Task.WaitAsync(Deadline);
            var pending = (await store.TrackWakes.ListAsync(target, default)).Where(w => w.Kind == "mail" && w.State is "pending" or "delivering");
            Assert.Equal(beforePrompt ? 1 : 0, pending.Count());
            Assert.False(send.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await send.WaitAsync(Deadline);
    }

    [Fact]
    public async Task InterruptedMailAcknowledgesEvenWhenProviderHasNotConfirmedDelivery()
    {
        await using var host = new PergaHostTestCluster();
        var (sender, senderLead) = await host.CreateTrackAsync();
        var (target, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler((_, _, ct) => NoDelivery(ct));
        async IAsyncEnumerable<UnifiedAgentEvent> NoDelivery([EnumeratorCancellation] CancellationToken ct)
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            yield break;
        }
        var send = kernel.SendMailAsync(sender, senderLead, target, "Notice", "Body");
        await started.Task.WaitAsync(Deadline);
        Assert.Single(await store.TrackWakes.ListAsync(target, default), w => w.Kind == "mail" && w.State is "pending" or "delivering");
        await kernel.InterruptSessionAsync(lead);
        await send.WaitAsync(Deadline);
        Assert.DoesNotContain(await store.TrackWakes.ListAsync(target, default), w => w.Kind == "mail" && w.State is "pending" or "delivering");
    }
}
