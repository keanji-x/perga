using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed partial class TrackFlowTests
{
    [Theory]
    [InlineData("\"cmd\":\"poll\",\"repeat\":true", "never", true)]
    [InlineData("\"cmd\":\"poll\",\"repeat\":false", "60m", false)]
    [InlineData("\"cmd\":\"poll\",\"timeout\":\"7d\"", "7d", false)]
    [InlineData("\"prChecks\":13,\"timeout\":\"never\"", "never", false)]
    public void WatchTimeoutDefaultsAndCanonicalRepeat(string options, string timeout, bool repeats)
    {
        var (spec, errors) = TrackFlowParser.Parse($$$"""
            {"rev":0,"nodes":[{"id":"poll","do":{"kind":"watch",{{{options}}}}}]}
            """);
        Assert.Empty(errors);
        var action = Assert.Single(spec!.Nodes).Do;
        Assert.Equal(timeout, FlowSyntax.WatchTimeout(action));
        if (timeout == "7d")
            Assert.Equal(TimeSpan.FromDays(7).TotalSeconds, FlowConstants.WatchTimeoutSeconds(action));

        using var canonical = JsonDocument.Parse(TrackFlowSerializer.SerializeCanonical(spec));
        var saved = canonical.RootElement.GetProperty("nodes")[0].GetProperty("do");
        Assert.Equal(repeats, saved.TryGetProperty("repeat", out var repeat));
        if (repeats)
            Assert.True(repeat.GetBoolean());
    }

    [Theory]
    [InlineData("""{"kind":"watch","prChecks":13,"repeat":true}""", "repeat")]
    [InlineData("""{"kind":"run","cmd":"poll","repeat":true}""", "repeat")]
    [InlineData("""{"kind":"watch","cmd":"poll","repeat":"true"}""", "repeat")]
    [InlineData("""{"kind":"watch","cmd":"poll","reasoningEffort":"high"}""", "reasoningEffort")]
    [InlineData("""{"kind":"wake","session":"lead","prompt":"Review","reasoningEffort":"high"}""", "reasoningEffort")]
    [InlineData("""{"kind":"run","cmd":"poll","reasoningEffort":"high"}""", "reasoningEffort")]
    [InlineData("""{"kind":"run","prompt":"Review","reasoningEffort":" "}""", "reasoningEffort")]
    public void ParserRejectsRepeatAndEffortOnUnsupportedActions(string action, string field)
    {
        var (spec, errors) = TrackFlowParser.Parse($$"""
            {"rev":0,"nodes":[{"id":"node","do":{{action}}}]}
            """);
        Assert.Null(spec);
        Assert.Equal($"$.nodes[0].do.{field}", Assert.Single(errors).Path);
    }

    [Fact]
    public async Task RepeatingWatchWaitsForTransitiveDownstreamAndRerunsTheNextWave()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var tailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tailRuns = 0;
        ctx.CommandRunner.Handler = async (request, ct) =>
        {
            if (request.Command == "tail" && Interlocked.Increment(ref tailRuns) == 1)
            {
                tailStarted.TrySetResult();
                await releaseTail.Task.WaitAsync(ct);
            }
            return new ShellCommandResult(0, "", "", false, 1);
        };

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {"rev":0,"nodes":[
              {"id":"watch","do":{"kind":"watch","cmd":"probe","repeat":true,"interval":"5s"}},
              {"id":"worker","when":{"after":["watch"]},"do":{"kind":"run","cmd":"work"}},
              {"id":"tail","when":{"after":["worker"]},"do":{"kind":"run","cmd":"tail"}},
              {"id":"triage","when":{"after":[{"node":"worker","on":"attention"}]},"do":{"kind":"run","cmd":"triage"}},
              {"id":"followup","when":{"after":["triage"]},"do":{"kind":"run","cmd":"followup"}}
            ]}
            """);
        Assert.True(commit.Ok, JsonSerializer.Serialize(commit.Diagnostics));
        await tailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromSeconds(10));
        await ctx.Kernel.Actors.GetOrActivate(trackId).TriggerSchedulerAsync();
        var blocked = await ctx.Kernel.GetTrackFlowAsync(trackId);
        var watch = Assert.Single(blocked.Projection.Nodes, node => node.Id == "watch");
        Assert.Equal("done", watch.Status);
        Assert.Equal(1, watch.RunCount);
        Assert.True(watch.Repeat);
        Assert.Null(watch.WatchProgress?.TimeoutAtMs);
        Assert.Single(ctx.CommandRunner.ExecutedCommands, command => command.Command == "probe");

        releaseTail.SetResult();
        await WaitUntilAsync(async () =>
            (await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes
                .Any(node => node.Id == "tail" && node.RunCount == 2 && node.Status == "done"));
        var finished = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Equal(commit.FlowRev, finished.FlowRev);
        Assert.Empty(finished.Projection.Archived);
        Assert.Equal(5, finished.Projection.Nodes.Count);
        Assert.All(finished.Projection.Nodes.Where(node => node.Id is "watch" or "worker" or "tail"),
            node => Assert.Equal(2, node.RunCount));
        Assert.Equal(new[] { "probe", "work", "tail", "probe", "work", "tail" },
            ctx.CommandRunner.ExecutedCommands.Select(command => command.Command));
    }

    [Fact]
    public async Task RepeatingWatchWithoutDependentsRespectsIntervalThenPausesOnAttention()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var actor = ctx.Kernel.Actors.GetOrActivate(trackId);
        var probes = 0;
        ctx.CommandRunner.Handler = (_, _) => Task.FromResult(new ShellCommandResult(
            Interlocked.Increment(ref probes) == 1 ? 0 : 2, "", "", false, 1));
        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {"rev":0,"nodes":[{"id":"watch","do":{"kind":"watch","cmd":"probe","repeat":true,"interval":"5s"}}]}
            """);
        Assert.True(commit.Ok);
        await WaitUntilAsync(async () =>
            Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes).Outcome == "ok");
        var nextProbe = clock.GetUtcNow().AddSeconds(5);
        await WaitUntilAsync(() => Task.FromResult(clock.HasTimerDueAt(nextProbe)));

        var dropped = await ctx.Kernel.CommitTrackFlowAsync(trackId, true, commit.FlowRev, Human,
            content: """{"rev":1,"nodes":[]}""");
        Assert.False(dropped.Ok);
        Assert.Contains("active repeating watch", Assert.Single(dropped.Diagnostics).Message);

        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Single(ctx.CommandRunner.ExecutedCommands);
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(async () =>
            Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes).Outcome == "attention");
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromMinutes(1));
        await actor.TriggerSchedulerAsync();
        var stopped = Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes);
        Assert.Equal("done", stopped.Status);
        Assert.Equal("attention", stopped.Outcome);
        Assert.Equal(2, stopped.RunCount);
        Assert.Equal(2, probes);
        Assert.Single(ctx.AgentRuntime.OpenedSessions.SelectMany(session => session.RecordedInputs),
            input => input.Prompt.Contains("watch' settled as attention", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsatisfiedWatchUpdatesLiveProgressWithoutWritingEveryProbeOrBlockingOtherTasks()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var detail = "waiting";
        ctx.CommandRunner.Handler = (request, _) => Task.FromResult(request.Command == "probe"
            ? new ShellCommandResult(75, Volatile.Read(ref detail), "", false, 1)
            : new ShellCommandResult(0, "", "", false, 1));
        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {"rev":0,"nodes":[{"id":"watch","do":{"kind":"watch","cmd":"probe","repeat":true,"interval":"5s"}}]}
            """);
        Assert.True(commit.Ok);

        async Task WaitForCheckAsync() => await WaitUntilAsync(async () =>
            Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes, node => node.Id == "watch")
                .WatchProgress?.LastCheckedAtMs == clock.GetUtcNow().ToUnixTimeMilliseconds());

        async Task<long?> StoredCheckAsync() => TrackFlowProjector.ExtractPersistedWatchProgress(
            Assert.Single((await ctx.Store.LoadTrackAggregateAsync(trackId, default))!.Tasks, task => task.TaskKey == "watch"))
            ?.LastCheckedAtMs;

        async Task AdvanceNextProbeAsync(TimeSpan step)
        {
            await WaitUntilAsync(() => Task.FromResult(clock.HasTimerDueAt(clock.GetUtcNow() + TimeSpan.FromSeconds(5))));
            clock.Advance(step);
            await WaitForCheckAsync();
        }

        await WaitForCheckAsync();
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(ctx.CommandRunner.ExecutedCommands).Timeout);
        var firstCheck = await StoredCheckAsync();
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), firstCheck);

        await FlowFixture.AddAsync(ctx.Kernel, trackId, new FlowNodeSpecDto("independent", new("run", Cmd: "work")));
        await WaitUntilAsync(async () =>
            (await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Archived.Any(node => node.Id == "independent"));
        await AdvanceNextProbeAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(firstCheck, await StoredCheckAsync());
        await AdvanceNextProbeAsync(TimeSpan.FromSeconds(55));
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), await StoredCheckAsync());
        Volatile.Write(ref detail, "waiting for release");
        await AdvanceNextProbeAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), await StoredCheckAsync());
    }

    [Fact]
    public async Task WorkerEffortUsesTheResolvedModelAndMatchesTheReusedSession()
    {
        await using var ctx = new KernelTestContext(options: new KernelOptions { DefaultModel = "worker-model" });
        ctx.AgentRuntime.Providers =
        [
            new("codex", true, null, null, ["worker-model"],
                [new("worker-model", "Worker", "", true, "low", [new("low", ""), new("high", "")])])
        ];
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var rejected = await ctx.Kernel.CommitTrackFlowAsync(trackId, true, 0, Human, content: """
            {"rev":0,"nodes":[{"id":"worker","do":{"kind":"run","prompt":"Review","reasoningEffort":"unsupported"}}]}
            """);
        var unsupported = Assert.Single(rejected.Diagnostics);
        Assert.Equal("$.nodes[0].do.reasoningEffort", unsupported.Path);
        Assert.Contains("worker-model", unsupported.Message);

        var idle = (await ctx.Kernel.CreateSessionAsync(trackId, new(ReasoningEffort: "low"), "reuse-test", Human, default))
            .Receipt.Session;
        var reuse = await ctx.Kernel.CommitTrackFlowAsync(trackId, true, 0, Human, content: $$$"""
            {"rev":0,"nodes":[{"id":"worker","do":{"kind":"run","prompt":"Review","session":"{{{idle.Id}}}","reasoningEffort":"high"}}]}
            """);
        var mismatch = Assert.Single(reuse.Diagnostics);
        Assert.Equal("$.nodes[0].do.reasoningEffort", mismatch.Path);
        Assert.Contains("target session", mismatch.Message);

        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId,
            new("worker", new("run", Access: "read_only", Prompt: "Review", ReasoningEffort: "high")));
        Assert.Equal("high", worker.Session.ReasoningEffort);
        Assert.Equal("high", worker.Task.Context!.Value.GetProperty("initialReasoningEffort").GetString());
        await WaitUntilAsync(() => Task.FromResult(ctx.AgentRuntime.OpenedSessions.Any(s => s.LaunchSpec.StateKey == worker.Session.Id)));
        Assert.Equal("high", Assert.Single(ctx.AgentRuntime.OpenedSessions, s => s.LaunchSpec.StateKey == worker.Session.Id)
            .LaunchSpec.ReasoningEffort);
        Assert.Equal("high", Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes).ReasoningEffort);
    }

    [Fact]
    public async Task ExplicitEffortFailsClosedWhenModelAdvertisesNoSupportedEfforts()
    {
        await using var ctx = new KernelTestContext(options: new KernelOptions { DefaultModel = "bare-model" });
        ctx.AgentRuntime.Providers =
        [
            new("codex", true, null, null, ["bare-model"], [new("bare-model", "Bare", "", true, null, [])])
        ];
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var rejected = await ctx.Kernel.CommitTrackFlowAsync(trackId, true, 0, Human, content: """
            {"rev":0,"nodes":[{"id":"worker","do":{"kind":"run","prompt":"Review","reasoningEffort":"high"}}]}
            """);
        Assert.False(rejected.Ok);
        var diagnostic = Assert.Single(rejected.Diagnostics);
        Assert.Equal("$.nodes[0].do.reasoningEffort", diagnostic.Path);
        Assert.Contains("cannot be verified", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OneShotNeverCommandWatchDoesNotBlockIndependentWorkspaceWritersWhileSleeping()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var markerWritten = 0;
        ctx.CommandRunner.Handler = (request, _) =>
        {
            if (request.Command == "write-marker")
            {
                Volatile.Write(ref markerWritten, 1);
                return Task.FromResult(new ShellCommandResult(0, "written", "", false, 1));
            }
            var code = Volatile.Read(ref markerWritten) == 1 ? 0 : 75;
            return Task.FromResult(new ShellCommandResult(code, code == 0 ? "found" : "waiting", "", false, 1));
        };

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {
              "rev": 0,
              "nodes": [
                {"id": "watch", "do": {"kind": "watch", "cmd": "check-marker", "interval": "5s", "timeout": "never"}},
                {"id": "writer", "do": {"kind": "run", "cmd": "write-marker"}}
              ]
            }
            """);
        Assert.True(commit.Ok);

        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref markerWritten) == 1));
        await WaitUntilAsync(() => Task.FromResult(clock.HasTimerDueAt(clock.GetUtcNow() + TimeSpan.FromSeconds(5))));
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(async () =>
            (await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Archived.Any(n => n.Id == "watch" && n.Outcome == "ok"));
    }

    [Fact]
    public async Task PausedRepeatingWatchResumesAfterFixingAndRecommittingSameNode()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        ctx.CommandRunner.Handler = (request, _) => Task.FromResult(request.Command == "broken"
            ? new ShellCommandResult(2, "", "bad probe", false, 1)
            : new ShellCommandResult(0, "fixed", "", false, 1));

        var first = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {"rev":0,"nodes":[{"id":"watch","do":{"kind":"watch","cmd":"broken","repeat":true,"interval":"5s"}}]}
            """);
        Assert.True(first.Ok);
        await WaitUntilAsync(async () =>
            Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes, n => n.Id == "watch").Outcome == "attention");

        // Removing repeat while uncanceled is rejected.
        var stripRepeat = await ctx.Kernel.CommitTrackFlowAsync(trackId, true, first.FlowRev, Human, content: $$$"""
            {"rev":{{{first.FlowRev}}},"nodes":[{"id":"watch","do":{"kind":"watch","cmd":"fixed","interval":"5s"}}]}
            """);
        Assert.False(stripRepeat.Ok);
        Assert.Contains(stripRepeat.Diagnostics, d => d.Path == "$.nodes[0].do.repeat");

        // Fixing cmd and recommitting resumes patrolling and settles ok.
        var resumed = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, first.FlowRev, Human, content: $$$"""
            {"rev":{{{first.FlowRev}}},"nodes":[{"id":"watch","do":{"kind":"watch","cmd":"fixed","repeat":true,"interval":"5s"}}]}
            """);
        Assert.True(resumed.Ok);
        await WaitUntilAsync(async () =>
            Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes, n => n.Id == "watch").Outcome == "ok");
    }

    [Fact]
    public async Task UnreachableNonDescendantPrerequisiteDoesNotPinRepeatingWatch()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var hits = 0;
        ctx.CommandRunner.Handler = (request, _) =>
        {
            if (request.Command == "probe")
            {
                var n = Interlocked.Increment(ref hits);
                return Task.FromResult(new ShellCommandResult(n <= 2 ? 0 : 75, $"hit {n}", "", false, 1));
            }
            return Task.FromResult(new ShellCommandResult(0, "ok", "", false, 1));
        };

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {
              "rev": 0,
              "nodes": [
                {"id": "watch", "do": {"kind": "watch", "cmd": "probe", "repeat": true, "interval": "5s"}},
                {"id": "setup", "do": {"kind": "run", "cmd": "init"}},
                {"id": "on-err", "when": {"after": [{"node": "setup", "on": "attention"}]}, "do": {"kind": "run", "cmd": "noop"}},
                {"id": "join", "when": {"after": ["watch", "on-err"]}, "do": {"kind": "run", "cmd": "noop"}}
              ]
            }
            """);
        Assert.True(commit.Ok);

        await WaitUntilAsync(async () =>
            Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes, n => n.Id == "watch").RunCount == 1);
        await WaitUntilAsync(() => Task.FromResult(clock.HasTimerDueAt(clock.GetUtcNow() + TimeSpan.FromSeconds(5))));
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(async () =>
            Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Nodes, n => n.Id == "watch").RunCount == 2);
    }

    [Fact]
    public async Task LaterWaveNonReuseWorkerAppliesUpdatedModelAndClearedEffort()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock, options: new KernelOptions { DefaultModel = "model-a" });
        ctx.AgentRuntime.Providers =
        [
            new("codex", true, null, null, ["model-a", "model-b"],
            [
                new("model-a", "Model A", "", true, "low", [new("low", ""), new("high", "")]),
                new("model-b", "Model B", "", false, "low", [new("low", ""), new("high", "")])
            ])
        ];
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var probeHits = 0;
        ctx.CommandRunner.Handler = (_, _) =>
        {
            var n = Interlocked.Increment(ref probeHits);
            return Task.FromResult(new ShellCommandResult(n <= 2 ? 0 : 75, $"wave {n}", "", false, 1));
        };

        var commit1 = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {
              "rev": 0,
              "nodes": [
                {"id": "watch", "do": {"kind": "watch", "cmd": "probe", "repeat": true, "interval": "5s"}},
                {"id": "worker", "when": {"after": ["watch"]}, "do": {"kind": "run", "access": "read_only", "model": "model-a", "reasoningEffort": "high", "prompt": "Wave"}}
              ]
            }
            """);
        Assert.True(commit1.Ok);
        await WaitUntilAsync(() => Task.FromResult(ctx.AgentRuntime.OpenedSessions.Any(s => s.LaunchSpec.Model == "model-a")));
        var wave1Task = Assert.Single((await ctx.Store.LoadTrackAggregateAsync(trackId, default))!.Tasks, t => t.TaskKey == "worker");
        Assert.Equal("high", ctx.AgentRuntime.OpenedSessions.Last(s => s.LaunchSpec.StateKey == wave1Task.SessionId).LaunchSpec.ReasoningEffort);
        await ctx.Kernel.CompleteTaskDoneAsync(trackId, wave1Task.SessionId!, outcome: FlowOutcome.Ok, summary: "done 1");
        await WaitUntilAsync(async () =>
            Assert.Single((await ctx.Store.LoadTrackAggregateAsync(trackId, default))!.Tasks, t => t.TaskKey == "worker").Status == TaskRunStatus.Done);

        var rev = (await ctx.Kernel.GetTrackFlowAsync(trackId)).FlowRev;
        var commit2 = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, rev, Human, content: $$$"""
            {
              "rev": {{{rev}}},
              "nodes": [
                {"id": "watch", "do": {"kind": "watch", "cmd": "probe", "repeat": true, "interval": "5s"}},
                {"id": "worker", "when": {"after": ["watch"]}, "do": {"kind": "run", "access": "read_only", "model": "model-b", "prompt": "Wave"}}
              ]
            }
            """);
        Assert.True(commit2.Ok);
        await WaitUntilAsync(() => Task.FromResult(clock.HasTimerDueAt(clock.GetUtcNow() + TimeSpan.FromSeconds(5))));
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => Task.FromResult(ctx.AgentRuntime.OpenedSessions.Any(s => s.LaunchSpec.Model == "model-b")));
        var wave2Launch = ctx.AgentRuntime.OpenedSessions.Last(s => s.LaunchSpec.Model == "model-b").LaunchSpec;
        Assert.Equal("model-b", wave2Launch.Model);
        Assert.Null(wave2Launch.ReasoningEffort);
    }

    [Fact]
    public async Task FiniteCommandWatchDoesNotRunProbeAfterDeadlineWhileWaitingOnMailbox()
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        var trackId = (await ctx.CreateDefaultTrackAsync()).TrackSnap.Track.Id;
        var probeRuns = 0;
        ctx.CommandRunner.Handler = (_, _) =>
        {
            Interlocked.Increment(ref probeRuns);
            return Task.FromResult(new ShellCommandResult(75, "wait", "", false, 1));
        };

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {
              "rev": 0,
              "nodes": [
                {"id": "watch", "do": {"kind": "watch", "cmd": "probe", "interval": "5s", "timeout": "10s"}}
              ]
            }
            """);
        Assert.True(commit.Ok);
        await WaitUntilAsync(() => Task.FromResult(probeRuns == 1 && clock.HasTimerDueAt(clock.GetUtcNow() + TimeSpan.FromSeconds(5))));

        var actor = ctx.Kernel.Actors.GetOrActivate(trackId);
        var mailboxEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMailbox = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = actor.EnqueueAsync(async _ =>
        {
            mailboxEntered.TrySetResult();
            await releaseMailbox.Task;
        });
        await mailboxEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => Task.FromResult(ctx.Kernel.DispatchAdmission.ActiveDispatches > 0));
            clock.Advance(TimeSpan.FromSeconds(6));
        }
        finally
        {
            releaseMailbox.TrySetResult();
        }
        await hold;

        await WaitUntilAsync(async () =>
        {
            var t = Assert.Single((await ctx.Store.LoadTrackAggregateAsync(trackId, default))!.Tasks, x => x.TaskKey == "watch");
            return t.Status == TaskRunStatus.Done && t.Outcome == FlowOutcome.Blocked;
        });
        Assert.Equal(1, probeRuns);
    }
}
