using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;

namespace Perga.Kernel.Tests;

public sealed class McpAnnotationsAndPermissionGuidanceTests
{
    [Fact]
    public void EveryKernelMcpToolDeclaresExplicitAnnotations()
    {
        var all = KernelMcpCatalog.GetAllTools();
        Assert.Equal(KernelMcpToolNames.All.Count, all.Count);

        foreach (var tool in all)
        {
            Assert.NotNull(tool.Annotations);
            Assert.NotNull(tool.Annotations.ReadOnlyHint);
            Assert.NotNull(tool.Annotations.DestructiveHint);
            Assert.NotNull(tool.Annotations.IdempotentHint);
            Assert.NotNull(tool.Annotations.OpenWorldHint);
        }

        var byName = all.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Assert.Equal(false, byName[KernelMcpToolNames.TaskDone].Annotations!.ReadOnlyHint);
        Assert.Equal(false, byName[KernelMcpToolNames.TaskDone].Annotations!.DestructiveHint);
        Assert.Equal(false, byName[KernelMcpToolNames.TaskDone].Annotations!.OpenWorldHint);

        Assert.Equal(false, byName[KernelMcpToolNames.TaskFail].Annotations!.ReadOnlyHint);
        Assert.Equal(false, byName[KernelMcpToolNames.TaskFail].Annotations!.DestructiveHint);
        Assert.Equal(false, byName[KernelMcpToolNames.TaskFail].Annotations!.OpenWorldHint);

        Assert.Equal(true, byName[KernelMcpToolNames.SystemUpgradeApply].Annotations!.DestructiveHint);
        Assert.Equal(true, byName[KernelMcpToolNames.SystemUpgradeApply].Annotations!.OpenWorldHint);
        Assert.Equal(true, byName[KernelMcpToolNames.SystemUpgradeRollback].Annotations!.DestructiveHint);
        Assert.Equal(true, byName[KernelMcpToolNames.TrackClose].Annotations!.DestructiveHint);
        Assert.Equal(true, byName[KernelMcpToolNames.TerminalOpen].Annotations!.DestructiveHint);
        Assert.Equal(true, byName[KernelMcpToolNames.TerminalOpen].Annotations!.OpenWorldHint);
        Assert.Equal(false, byName[KernelMcpToolNames.TerminalOpen].Annotations!.IdempotentHint);
        Assert.Equal(true, byName[KernelMcpToolNames.TerminalInput].Annotations!.DestructiveHint);
        Assert.Equal(true, byName[KernelMcpToolNames.TerminalInput].Annotations!.OpenWorldHint);
    }

    [Fact]
    public async Task BoundWorkersAndChildSubagentsReceivePermissionEscalationGuidanceInPrompts()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();

        await ctx.Kernel.SendSessionMessageAsync(
            snapshot.Track.LeadSessionId!,
            new(Content: "Check status", RequestKey: "req-lead-guidance"),
            new InteropPrincipal.Human(),
            CancellationToken.None);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);

        var leadLaunch = ctx.AgentRuntime.OpenedSessions.Single(s =>
            s.LaunchSpec.SystemPrompt?.Contains("Lead Session", StringComparison.Ordinal) == true);
        Assert.DoesNotContain("If blocked by missing permissions", leadLaunch.LaunchSpec.SystemPrompt!, StringComparison.Ordinal);

        await FlowFixture.AddAsync(ctx.Kernel, snapshot.Track.Id, FlowFixture.Worker("perm-worker", "Run checks"));

        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);

        var workerLaunch = ctx.AgentRuntime.OpenedSessions.Single(s =>
            s.LaunchSpec.SystemPrompt?.Contains("Contract Subagent", StringComparison.Ordinal) == true);
        Assert.Contains("user_ask", workerLaunch.LaunchSpec.SystemPrompt!, StringComparison.Ordinal);
        Assert.Contains("permissionMode", workerLaunch.LaunchSpec.SystemPrompt!, StringComparison.Ordinal);
        Assert.Contains("settles the task", workerLaunch.LaunchSpec.SystemPrompt!, StringComparison.Ordinal);
        var workerTurnPrompt = Assert.Single(workerLaunch.RecordedInputs).Prompt;
        Assert.Contains("user_ask", workerTurnPrompt, StringComparison.Ordinal);
        Assert.Contains("settles the task", workerTurnPrompt, StringComparison.Ordinal);

        await ctx.Kernel.CreateSessionAsync(
            snapshot.Track.Id,
            new CreateSessionRequest(Title: "Helper", Prompt: "Inspect repository"),
            requestKey: "req-helper-guidance",
            caller: new InteropPrincipal.Human(),
            ct: CancellationToken.None);
        await ctx.Kernel.WaitForTrackIdleAsync(snapshot.Track.Id);

        var childLaunch = ctx.AgentRuntime.OpenedSessions.Single(s =>
            s.LaunchSpec.SystemPrompt?.Contains("Collaborative Subagent", StringComparison.Ordinal) == true);
        Assert.Contains("user_ask", childLaunch.LaunchSpec.SystemPrompt!, StringComparison.Ordinal);
        Assert.Contains("permissionMode", childLaunch.LaunchSpec.SystemPrompt!, StringComparison.Ordinal);
    }
}
