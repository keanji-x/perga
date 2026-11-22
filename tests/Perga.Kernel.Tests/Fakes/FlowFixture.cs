using System.Text.Json.Nodes;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Internal;

namespace Perga.Kernel.Tests.Fakes;

internal static class FlowFixture
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    internal static FlowNodeSpecDto Worker(string id, string prompt, string? access = null, string? gate = null,
        string? after = null, string? provider = null, string? model = null, string? role = null, string? session = null) =>
        new(id, new("run", Session: session, Access: access ?? "read_only", Gate: gate, Provider: provider,
            Model: model, Role: role, Prompt: prompt),
            When: after is null ? null : new(After: [new(after)]));

    internal static async Task<FlowCommitResultDto> AppendAsync(IPergaKernel kernel, string trackId,
        params FlowNodeSpecDto[] nodes)
    {
        var snapshot = await kernel.GetTrackFlowAsync(trackId);
        var draft = JsonNode.Parse(snapshot.Spec.GetRawText())!.AsObject();
        foreach (var node in nodes)
            draft["nodes"]!.AsArray().Add(JsonNode.Parse(JsonHelpers.ToElement(node).GetRawText()));
        return await kernel.CommitTrackFlowAsync(trackId, false, snapshot.FlowRev, Human, content: draft.ToJsonString());
    }

    internal static async Task AddAsync(IPergaKernel kernel, string trackId, params FlowNodeSpecDto[] nodes)
    {
        var result = await AppendAsync(kernel, trackId, nodes);
        Assert.True(result.Ok, string.Join(Environment.NewLine, result.Diagnostics));
    }

    internal static async Task<(AgentSessionDto Session, TaskDto Task)> StartWorkerAsync(IPergaKernel kernel, string trackId,
        FlowNodeSpecDto node)
    {
        await AddAsync(kernel, trackId, node);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var task = Assert.Single(await kernel.ListTasksAsync(trackId, timeout.Token), task => task.TaskKey == node.Id);
            if (task.SessionId is { } sessionId)
            {
                var sessions = await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), Human, timeout.Token);
                return (Assert.Single(sessions, item => item.Session.Id == sessionId).Session, task);
            }
            Assert.Equal(TaskRunStatus.Pending, task.Status);
            await Task.Delay(10, timeout.Token);
        }
    }
}
