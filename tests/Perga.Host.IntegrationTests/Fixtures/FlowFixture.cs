using System.Text.Json;
using System.Text.Json.Nodes;
using Perga.Contracts;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.Host.Serialization;

namespace Perga.Host.IntegrationTests.Fixtures;

internal static class FlowFixture
{
    private static readonly InteropPrincipal Human = new InteropPrincipal.Human();

    internal static FlowNodeSpecDto Wake(string id, string prompt, FlowWhenSpecDto when, string? title = null) =>
        new(id, new("wake", Session: "lead", Prompt: prompt), Title: title, When: when);

    internal static async Task<FlowCommitResultDto> AppendAsync(IPergaKernel kernel, string trackId,
        params FlowNodeSpecDto[] nodes)
    {
        var snapshot = await kernel.GetTrackFlowAsync(trackId);
        var draft = JsonNode.Parse(snapshot.Spec.GetRawText())!.AsObject();
        foreach (var node in nodes)
            draft["nodes"]!.AsArray().Add(JsonSerializer.SerializeToNode(node, PergaJsonDefaults.ApiOptions));
        return await kernel.CommitTrackFlowAsync(trackId, false, snapshot.FlowRev, Human, content: draft.ToJsonString());
    }

    internal static async Task AddAsync(IPergaKernel kernel, string trackId, params FlowNodeSpecDto[] nodes)
    {
        var result = await AppendAsync(kernel, trackId, nodes);
        Assert.True(result.Ok, string.Join(Environment.NewLine, result.Diagnostics));
    }

    internal static async Task<(AgentSessionDto Session, TaskDto Task)> StartWorkerAsync(IPergaKernel kernel,
        string trackId, string id, string prompt, CancellationToken ct = default, string? role = null)
    {
        await AddAsync(kernel, trackId, new FlowNodeSpecDto(id,
            new("run", Prompt: prompt, Access: "read_only", Role: role)));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var task = Assert.Single(await kernel.ListTasksAsync(trackId, deadline.Token), task => task.TaskKey == id);
            if (task.SessionId is { } sessionId)
            {
                var sessions = await kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), Human, deadline.Token);
                return (Assert.Single(sessions, item => item.Session.Id == sessionId).Session, task);
            }
            Assert.Equal(TaskRunStatus.Pending, task.Status);
            await Task.Delay(10, deadline.Token);
        }
    }
}
