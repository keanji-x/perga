using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class ProviderSessionResumeTests
{
    private static async IAsyncEnumerable<UnifiedAgentEvent> Unknown(HarnessLaunchSpec spec, TurnInput input,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new UnifiedAgentEvent.TextDelta("partial");
        yield return new UnifiedAgentEvent.TurnError("connection_lost", "Provider disconnected.", false);
    }

    [Fact]
    public async Task FailedReconnectPreservesUnknownAndRetryPreservesIdentityWithoutResending()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, id) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        host.AgentRuntime.SetTurnHandler(Unknown);
        var receipt = await kernel.SendSessionMessageAsync(id, new(Content: "original", RequestKey: "original"), new InteropPrincipal.Human(), default);
        await kernel.WaitForTrackIdleAsync(trackId);
        var before = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("unknown", before.State);
        Assert.Equal("unknown", before.LastTurn?.Status);
        Assert.True(before.UnknownDetail?.CanReconnect);
        host.AgentRuntime.LaunchConfirmationHandler = _ => null;
        using var failed = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reconnect" });
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        var held = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("unknown", held.State);
        Assert.NotNull(held.UnknownDetail?.LastError);
        Assert.False(held.UnknownDetail?.Recovering);
        var failedLaunch = host.AgentRuntime.OpenedSessions.Last().Spec;
        var failedMcp = Assert.IsType<McpServerConfig.HttpMcpServer>(failedLaunch.McpServers["perga"]);
        Assert.Null(kernel.McpSessionBridge.ResolveToken(failedMcp.Headers["Authorization"]["Bearer ".Length..]));
        Assert.Equal(before.Session.NativeThreadId, held.Session.NativeThreadId);
        host.AgentRuntime.LaunchConfirmationHandler = null;
        using var retry = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "reconnect" });
        retry.EnsureSuccessStatusCode();
        var restored = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("idle", restored.State);
        Assert.Null(restored.UnknownDetail);
        Assert.Equal(before.Session.NativeThreadId, restored.Session.NativeThreadId);
        Assert.Equal(before.Session.Epoch, restored.Session.Epoch);
        Assert.Equal(before.Session.ContextUsage, restored.Session.ContextUsage);
        Assert.Equal(before.LastTurn?.Id, restored.LastTurn?.Id);
        Assert.Equal("unknown", restored.LastTurn?.Status);
        Assert.NotNull(((await host.Services.GetRequiredService<ITrackStore>().SessionInputs.FindByIdAsync(id, receipt.MessageId, default))!.Receipt).ResolvedAtMs);
        Assert.Single(host.AgentRuntime.ExecutedTurns);
        Assert.All(host.AgentRuntime.OpenedSessions.Skip(1), s => Assert.Equal(before.Session.NativeThreadId, s.Spec.ResumeNativeThreadId));
    }

    [Theory]
    [InlineData("reconnect")]
    [InlineData("reset_context")]
    public async Task RunningTurnsNeverOfferRecovery(string mode)
    {
        await using var host = new PergaHostTestCluster();
        var (_, id) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AgentRuntime.SetTurnHandler(Stream);
        async IAsyncEnumerable<UnifiedAgentEvent> Stream(HarnessLaunchSpec spec, TurnInput input,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new UnifiedAgentEvent.TextDelta("running");
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        await kernel.SendSessionMessageAsync(id, new(Content: "running", RequestKey: "running"), new InteropPrincipal.Human(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var page = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("running", page.State);
        Assert.Null(page.UnknownDetail);
        using var result = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = mode });
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Single(host.AgentRuntime.OpenedSessions);
    }

    [Fact]
    public async Task ArchiveAndUnarchivePreserveUnknownAndLatestTurn()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, id) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        host.AgentRuntime.SetTurnHandler(Unknown);
        await kernel.SendSessionMessageAsync(id, new(Content: "uncertain", RequestKey: "uncertain"), new InteropPrincipal.Human(), default);
        await kernel.WaitForTrackIdleAsync(trackId);
        var before = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        await kernel.UpdateSessionAsync(id, new(Archived: true), new InteropPrincipal.Human(), default);
        var archived = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("unknown", archived.State);
        Assert.Equal("archived", archived.ReadonlyReason);
        Assert.True(archived.CanReopen);
        await kernel.UpdateSessionAsync(id, new(Archived: false), new InteropPrincipal.Human(), default);
        var reopened = await kernel.GetSessionItemsAsync(id, new(), new InteropPrincipal.Human(), default);
        Assert.Equal("unknown", reopened.State);
        Assert.Null(reopened.ReadonlyReason);
        Assert.Equal(before.LastTurn?.Id, reopened.LastTurn?.Id);
        Assert.Equal("unknown", reopened.LastTurn?.Status);
    }

    [Fact]
    public async Task RecoveryModeIsRequiredAndOldRoutesAreRemoved()
    {
        await using var host = new PergaHostTestCluster();
        var (_, id) = await host.CreateTrackAsync();
        using var invalid = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{id}/control", new { action = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var resume = await host.AuthenticatedClient.PostAsJsonAsync($"/api/v1/sessions/{id}/resume", new { action = "stop", turnId = "old" });
        Assert.Equal(HttpStatusCode.NotFound, resume.StatusCode);
        using var abandon = await host.AuthenticatedClient.PostAsync($"/api/v1/sessions/{id}/inputs/old/abandon", null);
        Assert.Equal(HttpStatusCode.NotFound, abandon.StatusCode);
    }
}
