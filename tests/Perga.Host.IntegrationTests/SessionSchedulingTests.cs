using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class SessionSchedulingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DrainKeepsWakeAndQueuedMessagePendingUntilResume()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        kernel.DispatchAdmission.SetDraining(true);
        await store.SessionMessages!.InsertAsync(new("drained-queue", lead, null, "owner:installation", "drained-key", "hash",
            null, null, "queued during drain", "queued", 1, "queue"), default);
        await store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(trackId, "mail", "mail:drained", "wake during drain"), default);
        await kernel.Actors.GetOrActivate(trackId).PokeLeadAsync();
        Assert.Equal("pending", Assert.Single(await store.TrackWakes.ListAsync(trackId, default)).State);
        Assert.Null((await store.SessionMessages.GetAsync(lead, false, "drained-queue", default))!.TurnId);
        Assert.Empty(host.AgentRuntime.ExecutedTurns);
        Assert.Equal(0, kernel.DispatchAdmission.ActiveDispatches);
        kernel.DispatchAdmission.SetDraining(false);
        // Wait until the resume callback has scheduled its first durable turn.
        using var deadline = new CancellationTokenSource(Deadline);
        while (host.AgentRuntime.ExecutedTurns.Count < 2)
            await Task.Delay(10, deadline.Token);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Equal(new[] { "queued during drain", "wake during drain" }, host.AgentRuntime.ExecutedTurns.Select(t => t.Input.Prompt));
        Assert.Equal("done", Assert.Single(await store.TrackWakes.ListAsync(trackId, default)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WakeConsumesAlreadyQueuedMessagesFirst(bool background)
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options => options.DispatchWakesInBackground = background);
        var (trackId, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var store = host.Services.GetRequiredService<ITrackStore>();
        // Pause at the persisted queue boundary, before any actor Poke can claim it.
        await store.SessionMessages!.InsertAsync(new("queued-message", lead, null, "owner:installation", "queued-key", "hash",
            null, null, "queued human message", "queued", 1, "queue"), default);
        await WakeAsync(host, trackId, "system wake");
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Equal(new[] { "queued human message", "system wake" }, host.AgentRuntime.ExecutedTurns.Select(t => t.Input.Prompt));
    }

    [Fact]
    public async Task WakeWaitsForSteeringAfterTheTargetTurnSettles()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, lead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var turnStarted = Signal();
        var releaseTurn = Signal();
        var steeringStarted = Signal();
        var releaseSteering = Signal();
        host.AgentRuntime.SetTurnHandler((_, input, ct) => Stream(input.Prompt == "first" ? releaseTurn.Task : Task.CompletedTask, turnStarted, ct));
        host.AgentRuntime.SetSteerHandler(async _ =>
        {
            steeringStarted.TrySetResult();
            await releaseSteering.Task;
            return true;
        });
        var firstWake = WakeAsync(host, trackId, "first");
        try
        {
            await turnStarted.Task.WaitAsync(Deadline);
            var turn = (await kernel.GetSessionItemsAsync(lead)).ActiveTurn!;
            var steering = kernel.SteerMessageAsync(lead, turn.Id, "steer", default, "steer-key");
            await steeringStarted.Task.WaitAsync(Deadline);
            releaseTurn.TrySetResult();
            await firstWake.WaitAsync(Deadline);
            await WakeAsync(host, trackId, "after steering");
            Assert.Single(host.AgentRuntime.ExecutedTurns);
            releaseSteering.TrySetResult();
            Assert.Equal("accepted", (await steering.WaitAsync(Deadline)).Delivery);
            await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
            Assert.Equal(2, host.AgentRuntime.ExecutedTurns.Count);
            Assert.Equal("after steering", host.AgentRuntime.ExecutedTurns.Last().Input.Prompt);
        }
        finally { releaseTurn.TrySetResult(); releaseSteering.TrySetResult(); }
    }

    [Fact]
    public async Task PendingTrackWakeFollowsTheNewLead()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, oldLead) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var newLead = await kernel.CreateSubagentSessionAsync(trackId, new());
        var started = Signal();
        var release = Signal();
        var wakeStarted = Signal();
        host.AgentRuntime.SetTurnHandler((_, input, ct) => Stream(input.Prompt == "first" ? release.Task : Task.CompletedTask,
            input.Prompt == "first" ? started : wakeStarted, ct));
        await kernel.AcceptMessageAsync(oldLead, new("first", "first"), default);
        try
        {
            await started.Task.WaitAsync(Deadline);
            await WakeAsync(host, trackId, "pending track wake");
            Assert.Single(host.AgentRuntime.ExecutedTurns);
            await kernel.PatchTrackAsync(trackId, new(LeadSessionId: newLead.Id));
            await wakeStarted.Task.WaitAsync(Deadline);
            var wake = Assert.Single(host.AgentRuntime.ExecutedTurns, t => t.Input.Prompt == "pending track wake");
            Assert.Equal(newLead.Id, wake.Spec.StateKey);
        }
        finally { release.TrySetResult(); }
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Equal(2, host.AgentRuntime.ExecutedTurns.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleWakeHonorsBackgroundDispatchOption(bool background)
    {
        await using var host = new PergaHostTestCluster(configureHostOptions: options => options.DispatchWakesInBackground = background);
        var (trackId, _) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var started = Signal();
        var release = Signal();
        host.AgentRuntime.SetTurnHandler((_, _, ct) => Stream(release.Task, started, ct));
        var wake = WakeAsync(host, trackId, "wake");
        try
        {
            await started.Task.WaitAsync(Deadline);
            if (background) await wake.WaitAsync(Deadline);
            else Assert.False(wake.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await wake.WaitAsync(Deadline);
        await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(Deadline);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
    }

    private static async Task WakeAsync(PergaHostTestCluster host, string trackId, string prompt)
    {
        await host.Services.GetRequiredService<ITrackStore>().TrackWakes.EnqueueAsync(
            TrackWakeRequests.Prompt(trackId, "boot", null, prompt), default);
        await host.Services.GetRequiredService<IPergaKernel>().Actors.GetOrActivate(trackId).PokeLeadAsync();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async IAsyncEnumerable<UnifiedAgentEvent> Stream(Task release, TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new UnifiedAgentEvent.TurnAccepted("provider-turn");
        started.TrySetResult();
        await release.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }
}
