using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Perga.Contracts;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

public sealed class ReportFlowIntegrationTests
{
    [Fact]
    public async Task ReportCommitRejectsLegacyAuthoringAndPreservesExistingFlowTask()
    {
        await using var host = new PergaHostTestCluster();
        var (trackId, leadId) = await host.CreateTrackAsync();
        var kernel = host.Services.GetRequiredService<IPergaKernel>();
        await FlowFixture.AddAsync(kernel, trackId, new FlowNodeSpecDto("future",
            new("run", Prompt: "Inspect"), When: new(At: "2099-01-01T00:00:00Z")));
        var original = await kernel.ReadReportAsync(trackId);
        var caller = new McpSessionPrincipal("token", trackId, leadId, true, null, null, []);
        var error = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(
            kernel, caller, KernelMcpToolNames.ReportCommit, JsonSerializer.SerializeToElement(new
            {
                anchor = original.Anchor,
                ops = new[] { new { op = "upsert_block", blockKind = "task", payload = new { task_key = "retired", goal = "test" } } }
            })));
        Assert.Equal(KernelErrorCode.InvalidArgument, error.Code);
        Assert.Contains("flow_commit", error.Message);
        Assert.Equal(original.DocRev, (await kernel.ReadReportAsync(trackId)).DocRev);
        Assert.Equal("future", Assert.Single(await kernel.ListTasksAsync(trackId)).TaskKey);
    }
}
