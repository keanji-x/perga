using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Tests.Fakes;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests;

public sealed partial class TrackFlowTests
{
    [Fact]
    public void TrackActionCanonicalSerializationPreservesLaunchOptionsAndComparesInputsByValue()
    {
        const string content = """
            {"rev":0,"nodes":[{"id":"new-track","title":"Fallback","do":{"kind":"track","prompt":"Start ${inputs.work}",
              "title":"${inputs.title}","area":"Research","template":"delivery","templateInputs":{"teams":["eng","ops"]},
              "cwd":"${inputs.directory}","workspaceDriver":"direct","executionTargetId":"local","provider":"codex",
              "model":"gpt-5","reasoningEffort":"high","permissionMode":"full"}}]}
            """;
        var (parsed, errors) = TrackFlowParser.Parse(content);
        Assert.Empty(errors);
        var serialized = TrackFlowSerializer.SerializeCanonical(parsed!);
        var (roundTrip, roundTripErrors) = TrackFlowParser.Parse(serialized);
        Assert.Empty(roundTripErrors);
        Assert.True(TrackFlowReconciler.NodeSpecEquals(Assert.Single(parsed!.Nodes), Assert.Single(roundTrip!.Nodes)));
        Assert.True(JsonElement.DeepEquals(TestJson.Parse(content).GetProperty("nodes"), TestJson.Parse(serialized).GetProperty("nodes")));
    }

    [Fact]
    public async Task TrackSpawnsRunConcurrentlyWithParentWriterAndSettleBeforeChildWorkspaceIsReady()
    {
        await using var ctx = new KernelTestContext(policyVm: new Perga.PolicyVm.PolicyVm());
        ctx.Kernel.Templates.RegisterTemplate("""
            export default { id: "gate-required", title: "Gate Required", tasks: { requireGateForReadWrite: true } };
            """);
        var (_, parent) = await ctx.CreateDefaultTrackAsync(templateId: "gate-required", cwd: null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var workerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWorker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChildren = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.ProbeScript = async ct =>
        {
            probeEntered.TrySetResult();
            await releaseProbe.Task.WaitAsync(ct);
            return ctx.AgentRuntime.Providers;
        };
        ctx.CommandRunner.Handler = async (request, ct) =>
        {
            if (request.Command == "write")
            {
                workerEntered.TrySetResult();
                await releaseWorker.Task.WaitAsync(ct);
            }
            return new ShellCommandResult(0, "", "", false, 1);
        };
        ctx.DirectDriver.ProvisionGate = releaseChildren.Task;
        try
        {
            await FlowFixture.AddAsync(ctx.Kernel, parent.Track.Id, new FlowNodeSpecDto("writer", new("run", Cmd: "write", Gate: "true")));
            await workerEntered.Task.WaitAsync(timeout.Token);
            await FlowFixture.AddAsync(ctx.Kernel, parent.Track.Id,
                new("first", new("track", Prompt: "First task"), Title: "First child"),
                new("second", new("track", Prompt: "Second task"), Title: "Second child")).WaitAsync(timeout.Token);
            await probeEntered.Task.WaitAsync(timeout.Token);
            var inFlight = (await ctx.Kernel.GetTrackFlowAsync(parent.Track.Id, timeout.Token)).Projection;
            Assert.Equal(3, inFlight.Nodes.Count);
            Assert.All(inFlight.Nodes, node => Assert.Equal("running", node.Status));
            releaseProbe.TrySetResult();

            await WaitUntilAsync(async () =>
                (await ctx.Kernel.GetTrackFlowAsync(parent.Track.Id, timeout.Token)).Projection.Archived.Count == 2);
            var flow = await ctx.Kernel.GetTrackFlowAsync(parent.Track.Id, timeout.Token);
            Assert.Equal("running", Assert.Single(flow.Projection.Nodes).Status);
            foreach (var archived in flow.Projection.Archived)
            {
                Assert.Equal("track", archived.DoKind);
                Assert.Equal("ok", archived.Outcome);
                Assert.NotNull(archived.ChildTrackId);
                var child = await ctx.Kernel.GetTrackAggregateAsync(archived.ChildTrackId, timeout.Token);
                Assert.Equal(parent.Track.Id, child.Track.OriginTrackId);
                Assert.Equal("pending", child.WorkspacePreparation);
                Assert.Equal(archived.Id == "first" ? "First child" : "Second child", child.Track.Title);
            }
        }
        finally
        {
            releaseProbe.TrySetResult();
            releaseChildren.TrySetResult();
            releaseWorker.TrySetResult();
        }
    }

    [Fact]
    public async Task TrackSpawnInterpolatesItsOptionsAndInheritsCompatibleLeadSettings()
    {
        await using var ctx = new KernelTestContext(policyVm: new Perga.PolicyVm.PolicyVm());
        ctx.AgentRuntime.Providers =
        [
            new("codex", true, "1", null, ["gpt-5"]),
            new("claude", true, "1", null, ["sonnet"])
        ];
        ctx.Kernel.Templates.RegisterTemplate("""
            export default { id: "parent-template", title: "Parent", inputs: {
              label: { type: "string" }, destination: { type: "string" }, template: { type: "string" },
              driver: { type: "string" }, provider: { type: "string" }, target: { type: "string" },
              blank: { type: "string", default: "" }
            } };
            """);
        ctx.Kernel.Templates.RegisterTemplate("""
            export default { id: "child-template", title: "Child template", inputs: { ticket: { type: "string" } } };
            """);

        var area = await ctx.Kernel.CreateAreaAsync(new("Engineering"));
        var research = await ctx.Kernel.CreateAreaAsync(new("Research", DefaultCwd: "/workspace/research"));
        var parent = await ctx.Kernel.CreateTrackAsync(new(area.Id, TemplateId: "parent-template", Provider: "codex",
            Model: "gpt-5", ReasoningEffort: "high", PermissionMode: "full", TemplateInputs: TestJson.Parse("""
                {"label":"Cache","destination":"research","template":"child-template","driver":"cow-overlay","provider":"claude","target":"local"}
                """)));
        await ctx.Kernel.Actors.GetOrActivate(parent.Track.Id).EnsureMountedAsync();

        var blank = await ctx.Kernel.CommitTrackFlowAsync(parent.Track.Id, true, 0, Human, content: """
            {"rev":0,"nodes":[{"id":"blank","do":{"kind":"track","prompt":"${inputs.blank}"}}]}
            """);
        Assert.False(blank.Ok);
        Assert.Equal("$.nodes[0].do.prompt", Assert.Single(blank.Diagnostics).Path);

        await FlowFixture.AddAsync(ctx.Kernel, parent.Track.Id,
            new("inherit", new("track", Prompt: "Explore ${inputs.label}"), Title: "Research ${inputs.label}"),
            new("override", new("track", Prompt: "Try ${inputs.label}", Area: "${inputs.destination}",
                Template: "${inputs.template}", TemplateInputs: TestJson.Parse("""{"ticket":"RFC-1"}"""),
                WorkspaceDriver: "${inputs.driver}", ExecutionTargetId: "${inputs.target}", Provider: "${inputs.provider}")),
            new("keep-active", new("track", Prompt: "Future"), When: new(After: [new("override"), new("later")])),
            new("later", new("run", Cmd: "true"), When: new(At: "2099-01-01T00:00:00Z")));
        await ctx.Kernel.WaitForTrackIdleAsync(parent.Track.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var projection = (await ctx.Kernel.GetTrackFlowAsync(parent.Track.Id)).Projection;
        var inheritedId = Assert.Single(projection.Archived, node => node.Id == "inherit").ChildTrackId!;
        var overriddenId = Assert.Single(projection.Nodes, node => node.Id == "override").ChildTrackId!;
        var inherited = await ctx.Kernel.GetTrackAggregateAsync(inheritedId);
        var overridden = await ctx.Kernel.GetTrackAggregateAsync(overriddenId);
        var inheritedLead = Assert.Single(inherited.Sessions);
        var overriddenLead = Assert.Single(overridden.Sessions);
        Assert.Equal(area.Id, inherited.Track.AreaId);
        Assert.Equal("Research Cache", inherited.Track.Title);
        Assert.Equal(("codex", "gpt-5", "high", "full"),
            (inheritedLead.Provider, inheritedLead.Model, inheritedLead.ReasoningEffort, inheritedLead.PermissionMode));
        Assert.Equal(research.Id, overridden.Track.AreaId);
        Assert.Equal("Child template", overridden.Track.Title);
        Assert.Equal("child-template", overridden.Track.TemplateId);
        Assert.Equal("RFC-1", overridden.Track.TemplateInputs.GetProperty("ticket").GetString());
        Assert.Equal("cow-overlay", overridden.Track.WorkspaceDriver);
        Assert.Equal("local", overridden.Track.ExecutionTargetId);
        Assert.Equal(("claude", "sonnet", null, "bypassPermissions"),
            (overriddenLead.Provider, overriddenLead.Model, overriddenLead.ReasoningEffort, overriddenLead.PermissionMode));

        await ctx.Kernel.WaitForTrackIdleAsync(overriddenId).WaitAsync(TimeSpan.FromSeconds(10));
        var launch = Assert.Single(ctx.AgentRuntime.OpenedSessions, session => session.LaunchSpec.StateKey == overriddenLead.Id);
        Assert.Contains($"report_read(trackId: \"{parent.Track.Id}\")", launch.LaunchSpec.SystemPrompt);
        Assert.Contains($"mail_send(toTrackId: \"{parent.Track.Id}\")", launch.LaunchSpec.SystemPrompt);
        Assert.Equal("/workspace/research", (await ctx.Kernel.GetTrackAggregateAsync(overriddenId)).Track.Cwd);
    }

    [Fact]
    public async Task TrackCronSpawnsOncePerOccurrenceAndCanceledClockNeverSpawns()
    {
        await using var ctx = new KernelTestContext();
        var (_, parent) = await ctx.CreateDefaultTrackAsync(cwd: null);
        var when = new FlowWhenSpecDto(Cron: "* * * * *", Tz: "UTC", MaxOccurrences: 2);
        var recurring = new FlowNodeSpecDto("recurring", new("track", Prompt: "Monthly work"), When: when);
        var canceled = new FlowNodeSpecDto("canceled", new("track", Prompt: "Canceled work"), When: when, Canceled: true);
        await FlowFixture.AddAsync(ctx.Kernel, parent.Track.Id, recurring, canceled);

        var childIds = new HashSet<string>();
        for (var run = 1; run <= 2; run++)
        {
            ctx.Time.Advance(TimeSpan.FromMinutes(1));
            Assert.Single(await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds()));
            await ctx.Kernel.WaitForTrackIdleAsync(parent.Track.Id).WaitAsync(TimeSpan.FromSeconds(10));
            var projected = Assert.Single((await ctx.Kernel.GetTrackFlowAsync(parent.Track.Id)).Projection.Nodes);
            Assert.Equal(run, projected.RunCount);
            Assert.Equal("ok", projected.Outcome);
            Assert.True(childIds.Add(Assert.IsType<string>(projected.ChildTrackId)));
        }

        var children = (await ctx.Kernel.ListTracksAsync()).Where(track => track.OriginTrackId == parent.Track.Id);
        Assert.True(childIds.SetEquals(children.Select(track => track.Id)));
    }

    [Fact]
    public async Task TrackSpawnDepthFuseSettlesAttentionAndFiresTheAttentionBranch()
    {
        await using var ctx = new KernelTestContext(options: new() { MaxTrackSpawnDepth = 1 });
        var (_, parent) = await ctx.CreateDefaultTrackAsync(cwd: null);
        await FlowFixture.AddAsync(ctx.Kernel, parent.Track.Id, new FlowNodeSpecDto("child", new("track", Prompt: "Start")));
        await ctx.Kernel.WaitForTrackIdleAsync(parent.Track.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var childId = Assert.Single((await ctx.Kernel.GetTrackFlowAsync(parent.Track.Id)).Projection.Archived).ChildTrackId!;
        await ctx.Kernel.Actors.GetOrActivate(childId).EnsureMountedAsync();

        await FlowFixture.AddAsync(ctx.Kernel, childId,
            new("too-deep", new("track", Prompt: "Start")),
            new("handle", new("run", Cmd: "handle"), When: new(After: [new("too-deep", "attention")])));
        await ctx.Kernel.WaitForTrackIdleAsync(childId).WaitAsync(TimeSpan.FromSeconds(10));
        var flow = await ctx.Kernel.GetTrackFlowAsync(childId);
        var rejected = Assert.Single(flow.Projection.Archived, node => node.Id == "too-deep");
        Assert.Equal("failed", rejected.Status);
        Assert.Equal("attention", rejected.Outcome);
        Assert.Equal("Track spawn would exceed MaxTrackSpawnDepth (1).", rejected.OutcomeSummary);
        Assert.Null(rejected.ChildTrackId);
        Assert.Equal("ok", Assert.Single(flow.Projection.Archived, node => node.Id == "handle").Outcome);
        Assert.Equal(2, (await ctx.Kernel.ListTracksAsync()).Count);
    }
}
