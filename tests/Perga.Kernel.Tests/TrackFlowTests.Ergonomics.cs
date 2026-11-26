using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed partial class TrackFlowTests
{
    [Fact]
    public async Task McpFlowReadFormatsPreserveStatusAndKeepFullOutputOnRequest()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var prompt = new string('p', 4096);
        var output = new string('g', 2048);
        ctx.CommandRunner.Handler = (_, _) => Task.FromResult(new ShellCommandResult(0, output, "", false, 20));
        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId,
            FlowFixture.Worker("review", prompt, gate: "verify"));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new("future", new("run", Cmd: "future"), When: new(At: "2099-01-01T00:00:00Z")),
            new FlowNodeSpecDto("join", new("run", Cmd: "join"), When: new(After: [new("review"), new("future")])));
        var workerResult = TestJson.FromObject(new { message = new string('r', 4096) });
        var settled = await ctx.Kernel.CompleteTaskDoneAsync(trackId, worker.Session.Id, workerResult);
        Assert.NotNull(settled.WorkerResult);
        Assert.NotNull(settled.GateVerdict);
        Assert.Equal(4096, settled.OutcomeSummary!.Length);
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);

        var summary = await Read(new { });
        var spec = await Read(new { format = "spec" });
        var full = await Read(new { format = "full" });
        Assert.True(JsonElement.DeepEquals(summary, await Read(new { format = "summary" })));
        Assert.False(summary.TryGetProperty("spec", out _));
        Assert.False(spec.TryGetProperty("projection", out _));
        Assert.Equal(full.GetProperty("flowRev").GetInt64(), summary.GetProperty("flowRev").GetInt64());
        Assert.Equal(full.GetProperty("flowPath").GetString(), spec.GetProperty("flowPath").GetString());
        Assert.True(JsonElement.DeepEquals(full.GetProperty("spec"), spec.GetProperty("spec")));

        var slimNode = ReviewNode(summary);
        var fullNode = ReviewNode(full);
        Assert.Equal(prompt, fullNode.GetProperty("prompt").GetString());
        Assert.Equal("done", slimNode.GetProperty("status").GetString());
        Assert.Equal("ok", slimNode.GetProperty("outcome").GetString());
        Assert.Equal(1, slimNode.GetProperty("runCount").GetInt32());
        Assert.False(slimNode.TryGetProperty("prompt", out _));
        Assert.Equal(KernelMcpReportSupport.McpSummaryMaxChars, slimNode.GetProperty("outcomeSummary").GetString()!.Length);
        Assert.EndsWith("...", slimNode.GetProperty("outcomeSummary").GetString());
        Assert.Equal(4096, fullNode.GetProperty("outcomeSummary").GetString()!.Length);
        var slimGate = Assert.Single(slimNode.GetProperty("gates").EnumerateArray());
        var fullGate = Assert.Single(fullNode.GetProperty("gates").EnumerateArray());
        Assert.Equal(fullGate.GetProperty("label").GetString(), slimGate.GetProperty("label").GetString());
        Assert.Equal("pass", slimGate.GetProperty("status").GetString());
        Assert.Equal(output, fullGate.GetProperty("output").GetString());
        Assert.False(slimGate.TryGetProperty("output", out _));

        var report = await Call(KernelMcpToolNames.ReportRead, new { });
        var reportTask = Assert.Single(report.GetProperty("tasks").EnumerateArray(),
            task => task.GetProperty("id").GetString() == settled.Id);
        Assert.False(reportTask.TryGetProperty("workerResult", out _));
        Assert.False(reportTask.TryGetProperty("gateVerdict", out _));
        Assert.Equal(KernelMcpReportSupport.McpSummaryMaxChars, reportTask.GetProperty("outcomeSummary").GetString()!.Length);
        var commitReceipt = await Call(KernelMcpToolNames.ReportCommit, new
        {
            anchor = report.GetProperty("anchor"),
            ops = new[] { new { op = "upsert", blockKind = "prose", markdown = "# Summary\n\nUpdated" } }
        });
        var receiptTask = Assert.Single(commitReceipt.GetProperty("tasks").EnumerateArray(),
            task => task.GetProperty("id").GetString() == settled.Id);
        Assert.Equal(KernelMcpReportSupport.McpSummaryMaxChars, receiptTask.GetProperty("outcomeSummary").GetString()!.Length);
        var taskList = await Call(KernelMcpToolNames.TaskList, new { });
        var fullTask = Assert.Single(taskList.EnumerateArray(), task => task.GetProperty("id").GetString() == settled.Id);
        Assert.Equal(4096, fullTask.GetProperty("outcomeSummary").GetString()!.Length);

        foreach (var invalid in new object[] { new { format = "status" }, new { format = 1 }, new { format = (string?)null } })
            Assert.Equal(KernelErrorCode.InvalidArgument, (await Assert.ThrowsAsync<KernelException>(() => Read(invalid))).Code);
        using var dupFormat = JsonDocument.Parse("""{"format":42,"format":"full"}""");
        var dupError = await Assert.ThrowsAsync<KernelException>(() => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel,
            caller, KernelMcpToolNames.FlowRead, dupFormat.RootElement));
        Assert.Equal(KernelErrorCode.InvalidArgument, dupError.Code);

        Task<JsonElement> Read(object args) => Call(KernelMcpToolNames.FlowRead, args);

        Task<JsonElement> Call(string tool, object args) => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel,
            caller, tool, TestJson.FromObject(args));

        static JsonElement ReviewNode(JsonElement response) => Assert.Single(
            response.GetProperty("projection").GetProperty("nodes").EnumerateArray(),
            node => node.GetProperty("id").GetString() == "review");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task McpFlowCommitOmitsArchivedNodesAndSlimsActiveNodes(bool dryRun)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        await FlowFixture.AddAsync(ctx.Kernel, trackId, new FlowNodeSpecDto("finished", new("run", Cmd: "once")));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        ctx.CommandRunner.Handler = (_, _) => Task.FromResult(new ShellCommandResult(0, "Gate output", "", false, 20));
        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId,
            FlowFixture.Worker("review", "Review prompt", gate: "verify"));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new("future", new("run", Cmd: "future"), When: new(At: "2099-01-01T00:00:00Z")),
            new FlowNodeSpecDto("join", new("run", Cmd: "join"), When: new(After: [new("review"), new("future")])));
        await ctx.Kernel.CompleteTaskDoneAsync(trackId, worker.Session.Id,
            TestJson.FromObject(new { message = new string('r', 4096) }));
        var before = await ctx.Kernel.GetTrackFlowAsync(trackId);
        var archived = Assert.Single(before.Projection.Archived);
        Assert.Equal("finished", archived.Id);
        var caller = await ctx.OpenMcpCallerAsync(snapshot.Track.LeadSessionId!);

        var receipt = await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.FlowCommit,
            TestJson.FromObject(new { dryRun, expectedFlowRev = before.FlowRev, content = before.Spec.GetRawText() }));

        Assert.True(receipt.GetProperty("ok").GetBoolean());
        Assert.Equal(dryRun, receipt.GetProperty("dryRun").GetBoolean());
        var expectedRev = dryRun ? before.FlowRev : before.FlowRev + 1;
        Assert.Equal(expectedRev, receipt.GetProperty("flowRev").GetInt64());
        Assert.Empty(receipt.GetProperty("plan").GetProperty("added").EnumerateArray());
        Assert.Empty(receipt.GetProperty("diagnostics").EnumerateArray());
        var flow = receipt.GetProperty("flow");
        Assert.False(flow.TryGetProperty("archived", out _));
        Assert.Equal(expectedRev, flow.GetProperty("rev").GetInt64());
        Assert.Equal(before.FilePath, flow.GetProperty("filePath").GetString());
        var review = Assert.Single(flow.GetProperty("nodes").EnumerateArray(), node => node.GetProperty("id").GetString() == "review");
        Assert.False(review.TryGetProperty("prompt", out _));
        Assert.Equal(KernelMcpReportSupport.McpSummaryMaxChars, review.GetProperty("outcomeSummary").GetString()!.Length);
        Assert.False(Assert.Single(review.GetProperty("gates").EnumerateArray()).TryGetProperty("output", out _));

        var full = await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.FlowRead,
            TestJson.FromObject(new { format = "full" }));
        var fullArchive = Assert.Single(full.GetProperty("projection").GetProperty("archived").EnumerateArray());
        Assert.Equal(archived.Id, fullArchive.GetProperty("id").GetString());
        Assert.Equal(archived, Assert.Single((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Archived));

        var rejected = await KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel, caller, KernelMcpToolNames.FlowCommit,
            TestJson.FromObject(new
            {
                dryRun,
                expectedFlowRev = expectedRev,
                content = $$$"""{"rev":{{{expectedRev}}},"nodes":[{"id":"finished","do":{"kind":"run","cmd":"dup"}}]}"""
            }));
        Assert.False(rejected.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(rejected.GetProperty("diagnostics").EnumerateArray());
        Assert.False(rejected.GetProperty("flow").TryGetProperty("archived", out _));
    }

    [Fact]
    public async Task ExplicitLeadWakeStillDeliversTaskSettlementToNonLeadParent()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var parent = (await ctx.Kernel.CreateSessionAsync(trackId, new(), "parent", Human, default)).Receipt.Session;
        var child = (await ctx.Kernel.CreateSessionAsync(trackId, new(), "child", Human, default)).Receipt.Session;
        // Seed the stored task-settlement notification policy used by the task repository.
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(trackId);
        await ctx.Store.UpsertSessionAsync(child with
        {
            ParentSessionId = parent.Id, SpawnDepth = 1, NotifyParent = "task_settled"
        }, null, default);

        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            FlowFixture.Worker("review", "Review", session: child.Id),
            new("notify", new("wake", Session: "lead", Prompt: "Review finished"), When: new(After: [new("review", "any")])));
        await WaitUntilAsync(async () => (await ctx.Kernel.ListTasksAsync(trackId))
            .Any(task => task.TaskKey == "review" && task.Status == TaskRunStatus.Running));
        var settled = await ctx.Kernel.CompleteTaskDoneAsync(trackId, child.Id, outcome: FlowOutcome.Attention);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        var wake = Assert.Single(await ctx.Store.TrackWakes.ListAsync(trackId, default));
        Assert.Equal(TrackWakeRequests.TaskSettled(settled).CoalesceKey, wake.CoalesceKey);
        Assert.Equal(parent.Id, wake.TargetSessionId);
        Assert.Single(ctx.Store.GetTurnsForSession(parent.Id));
        Assert.Single(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));
    }

    [Theory]
    [InlineData(true, 2, "attention")]
    [InlineData(true, 75, "blocked")]
    [InlineData(false, 75, "blocked")]
    public async Task WatchSettlementWithExplicitLeadWakeDeliversOneNotification(bool repeat, int exitCode, string outcome)
    {
        var clock = new ControllableTimeProvider();
        await using var ctx = new KernelTestContext(timeProvider: clock);
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        ctx.CommandRunner.Handler = (_, _) => Task.FromResult(new ShellCommandResult(exitCode, "Probe result", "", false, 1));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("watch", new("watch", Cmd: "probe", Interval: "5s", Timeout: "10s", Repeat: repeat)),
            new("notify", new("wake", Session: "lead", Prompt: "Watch settled"), When: new(After: [new("watch", outcome)])));
        if (exitCode == 75)
        {
            var nextProbe = clock.GetUtcNow().AddSeconds(5);
            await WaitUntilAsync(() => Task.FromResult(clock.HasTimerDueAt(nextProbe)));
            clock.Advance(TimeSpan.FromSeconds(11));
        }
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        var turn = Assert.Single(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!));
        AssertSuperseded(Assert.Single(await ctx.Store.TrackWakes.ListAsync(trackId, default)), turn.Id);
        Assert.Single(ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs), input => input.Prompt == "Watch settled");
    }

    [Theory]
    [InlineData("any", "any", false, false, true)]
    [InlineData("ok", "any", false, false, false)]
    [InlineData("ok", "any", true, false, false)]
    [InlineData("any", "ok", false, false, false)]
    [InlineData("any", "ok", false, true, false)]
    public async Task ExplicitLeadWakeDefersOnlyOutcomesThatCanReachIt(
        string firstCondition, string secondCondition, bool failFirst, bool transitive, bool expectExplicitWake)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var leadId = snapshot.Track.LeadSessionId!;
        var first = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review-a", "First review"));
        var second = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review-b", "Second review"));
        if (transitive)
            await FlowFixture.AddAsync(ctx.Kernel, trackId,
                new FlowNodeSpecDto("follow-up", new("run", Cmd: "follow-up"), When: new(After: [new("review-b")])));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("notify", new("wake", Session: "lead", Prompt: "Both reviews finished"),
                When: new(After: [new("review-a", firstCondition), new(transitive ? "follow-up" : "review-b", secondCondition)])));

        if (secondCondition == "ok")
            await ctx.Kernel.CompleteTaskFailAsync(trackId, second.Session.Id, "Sibling failed");

        if (failFirst)
            await ctx.Kernel.CompleteTaskFailAsync(trackId, first.Session.Id, "Reviewer failed");
        else
            await ctx.Kernel.CompleteTaskDoneAsync(trackId, first.Session.Id, outcome: FlowOutcome.Attention);

        var firstWake = await ReadTaskWakeAsync(ctx, first.Task);
        if (expectExplicitWake)
        {
            Assert.Equal(TrackWakeStates.Pending, firstWake.State);
            Assert.Equal(0, firstWake.Attempts);
            Assert.Empty(ctx.Store.GetTurnsForSession(leadId));
        }

        if (secondCondition == "any")
            await ctx.Kernel.CompleteTaskDoneAsync(trackId, second.Session.Id);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        var explicitInputs = ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs)
            .Where(input => input.Prompt == "Both reviews finished");
        if (expectExplicitWake)
        {
            Assert.Single(explicitInputs);
            var turn = Assert.Single(ctx.Store.GetTurnsForSession(leadId));
            AssertSuperseded(await ReadTaskWakeAsync(ctx, first.Task), turn.Id);
            AssertSuperseded(await ReadTaskWakeAsync(ctx, second.Task), turn.Id);
        }
        else
        {
            Assert.Empty(explicitInputs);
            Assert.Equal(TrackWakeStates.Done, (await ReadTaskWakeAsync(ctx, first.Task)).State);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FlowCommitAcceptsGcOnlyDriftWithoutRecreatingArchivedNodes(bool dryRun, bool keepArchivedCopy)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        const string oneShot = """{"id":"one-shot","do":{"kind":"run","cmd":"once"}}""";
        var first = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human,
            content: $$"""{"rev":0,"nodes":[{{oneShot}}]}""");
        Assert.True(first.Ok);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        var afterGc = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Equal(2, afterGc.FlowRev);
        Assert.Equal("one-shot", Assert.Single(afterGc.Projection.Archived).Id);

        var archivedCopy = keepArchivedCopy ? oneShot + "," : "";
        var committed = await ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun, 1, Human,
            content: $$$"""{"rev":1,"nodes":[{{{archivedCopy}}}{"id":"future","when":{"at":"2099-01-01T00:00:00Z"},"do":{"kind":"run","cmd":"later"}}]}""");
        Assert.True(committed.Ok, string.Join(Environment.NewLine, committed.Diagnostics));
        Assert.Equal("future", Assert.Single(committed.Flow.Nodes).Id);
        Assert.Equal("future", Assert.Single(committed.Plan.Added));
        Assert.Equal("once", Assert.Single(ctx.CommandRunner.ExecutedCommands).Command);
        var current = await ctx.Kernel.GetTrackFlowAsync(trackId);
        Assert.Equal(dryRun ? 2 : 3, current.FlowRev);
        Assert.Equal(dryRun ? 0 : 1, current.Projection.Nodes.Count);
        Assert.Equal("one-shot", Assert.Single(current.Projection.Archived).Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FlowCommitRejectsArchivedDependenciesAndSemanticDrift(bool dryRun, bool archivedDependency)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        if (archivedDependency)
        {
            await FlowFixture.AddAsync(ctx.Kernel, trackId, new FlowNodeSpecDto("finished", new("run", Cmd: "once")));
            await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            await FlowFixture.AddAsync(ctx.Kernel, trackId,
                new FlowNodeSpecDto("clock", new("run", Cmd: "clock"), When: new(Cron: "0 * * * *", Tz: "UTC")));
            await FlowFixture.AddAsync(ctx.Kernel, trackId,
                new FlowNodeSpecDto("other", new("run", Cmd: "other"), When: new(At: "2099-01-01T00:00:00Z")));
        }
        Assert.Equal(2, (await ctx.Kernel.GetTrackFlowAsync(trackId)).FlowRev);

        var draft = archivedDependency
            ? """{"rev":1,"nodes":[{"id":"finished","do":{"kind":"run","cmd":"once"}},{"id":"next","when":{"after":["finished"]},"do":{"kind":"run","cmd":"next"}}]}"""
            : """{"rev":1,"nodes":[]}""";
        var error = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitTrackFlowAsync(trackId, dryRun, 1, Human, content: draft));
        Assert.Equal(KernelErrorCode.Conflict, error.Code);
        Assert.Contains("Flow revision conflict: expected 1, current 2", error.Message);
    }

    [Theory]
    [InlineData(FlowOutcome.Attention, TrackWakeStates.Done)]
    [InlineData(FlowOutcome.Ok, TrackWakeStates.Dropped)]
    public async Task ConditionalSiblingDefersUntilItsOutcomeIsKnown(FlowOutcome siblingOutcome, string expectedState)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var leadId = snapshot.Track.LeadSessionId!;
        var first = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review-a", "First review"));
        var second = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review-b", "Second review"));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("notify", new("wake", Session: "lead", Prompt: "Join wake"),
                When: new(After: [new("review-a", "any"), new("review-b", "ok")])));

        await ctx.Kernel.CompleteTaskDoneAsync(trackId, first.Session.Id, outcome: FlowOutcome.Attention);
        var firstKey = TrackWakeRequests.TaskSettled(first.Task).CoalesceKey;
        var deferred = Assert.Single(await ctx.Store.TrackWakes.ListAsync(trackId, default), w => w.CoalesceKey == firstKey);
        Assert.Equal(TrackWakeStates.Pending, deferred.State);
        Assert.Equal(0, deferred.Attempts);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(ctx.Store.GetTurnsForSession(leadId));

        await ctx.Kernel.CompleteTaskDoneAsync(trackId, second.Session.Id, outcome: siblingOutcome);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        var wakes = await ctx.Store.TrackWakes.ListAsync(trackId, default);
        var firstWake = Assert.Single(wakes, w => w.Id == deferred.Id);
        var secondWake = Assert.Single(wakes, w => w.CoalesceKey == TrackWakeRequests.TaskSettled(second.Task).CoalesceKey);
        Assert.Equal(expectedState, firstWake.State);
        Assert.Equal(expectedState, secondWake.State);
        var joinInputs = ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs).Where(input => input.Prompt == "Join wake");
        if (siblingOutcome == FlowOutcome.Ok)
        {
            Assert.Single(joinInputs);
            var turn = Assert.Single(ctx.Store.GetTurnsForSession(leadId));
            AssertSuperseded(firstWake, turn.Id);
            AssertSuperseded(secondWake, turn.Id);
        }
        else
            Assert.Empty(joinInputs);
    }

    [Fact]
    public async Task ExplicitLeadSessionIdWakeDeduplicatesDefaultWake()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var leadId = snapshot.Track.LeadSessionId!;
        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review", "Review"));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("notify", new("wake", Session: leadId, Prompt: "Direct Lead wake"),
                When: new(After: [new("review", "any")])));

        await ctx.Kernel.CompleteTaskDoneAsync(trackId, worker.Session.Id, outcome: FlowOutcome.Attention);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        var turn = Assert.Single(ctx.Store.GetTurnsForSession(leadId));
        AssertSuperseded(await ReadTaskWakeAsync(ctx, worker.Task), turn.Id);
        Assert.Single(ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs),
            input => input.Prompt == "Direct Lead wake");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelingPendingJoinSiblingOrWakeReleasesDeferredWakeAfterRehydration(bool cancelWake)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var leadId = snapshot.Track.LeadSessionId!;
        var first = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review-a", "First"));
        var second = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review-b", "Second"));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("notify", new("wake", Session: "lead", Prompt: "Join wake"),
                When: new(After: [new("review-a", "any"), new("review-b", "any")])));

        await ctx.Kernel.CompleteTaskDoneAsync(trackId, first.Session.Id, outcome: FlowOutcome.Attention);
        var pending = await ReadTaskWakeAsync(ctx, first.Task);
        Assert.Equal(TrackWakeStates.Pending, pending.State);
        Assert.Empty(ctx.Store.GetTurnsForSession(leadId));

        // Evict and rehydrate the actor while the original wake is pending.
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(trackId);
        await ctx.Kernel.CancelTaskAsync(trackId, leadId, cancelWake ? "notify" : "review-b", "Aborted", default);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        var delivered = await ReadTaskWakeAsync(ctx, first.Task);
        Assert.Equal(pending.Id, delivered.Id);
        Assert.Equal(TrackWakeStates.Done, delivered.State);
        Assert.Single(ctx.Store.GetTurnsForSession(leadId));
        Assert.DoesNotContain(ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs),
            input => input.Prompt == "Join wake");
    }

    [Fact]
    public async Task FinalCronOccurrenceDisarmsPendingJoinAndReleasesDeferredWake()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var leadId = snapshot.Track.LeadSessionId!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, input, ct) => RunScript(input, release.Task, ct);
        var occurrences = 0;
        ctx.CommandRunner.Handler = (_, _) =>
            Task.FromResult(new ShellCommandResult(Interlocked.Increment(ref occurrences) == 1 ? 0 : 1, "", "", false, 1));

        var commit = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human, content: """
            {
              "rev": 0,
              "nodes": [
                { "id": "clock", "when": { "cron": "0 * * * *", "tz": "UTC", "maxOccurrences": 2 }, "do": { "kind": "run", "cmd": "tick", "access": "read_only" } },
                { "id": "worker", "do": { "kind": "run", "prompt": "work", "access": "read_only" } },
                { "id": "notify", "when": { "after": [{ "node": "clock", "on": "ok" }, { "node": "worker", "on": "any" }] }, "do": { "kind": "wake", "session": "lead", "prompt": "Cron join" } }
              ]
            }
            """);
        Assert.True(commit.Ok, string.Join(Environment.NewLine, commit.Diagnostics));
        await ctx.Kernel.SendSessionMessageAsync(leadId, new("Hold lead", "hold-lead"), Human, default);
        TaskDto workerTask;
        TrackWake deferred;
        try
        {
            await WaitUntilAsync(async () => (await ctx.Kernel.GetSessionItemsAsync(leadId, new(), Human, default)).ActiveTurn is not null);
            await FireAsync();
            await WaitUntilAsync(async () => (await ctx.Kernel.ListTasksAsync(trackId))
                .Any(t => t.TaskKey == "clock" && t.Outcome == FlowOutcome.Ok));
            workerTask = await WaitForWorkerRunningAsync(ctx.Kernel, trackId, "worker");
            await ctx.Kernel.CompleteTaskDoneAsync(trackId, workerTask.SessionId!, outcome: FlowOutcome.Attention);
            Assert.Contains(await ctx.Kernel.ListTasksAsync(trackId), task => task.TaskKey == "notify" && task.TriggerRunKey == "clock#1,worker#1");
            deferred = await ReadTaskWakeAsync(ctx, workerTask);
            Assert.Equal(TrackWakeStates.Pending, deferred.State);

            await FireAsync();
            await WaitUntilAsync(async () => (await ctx.Kernel.ListTasksAsync(trackId))
                .Any(t => t.TaskKey == "clock" && t.RunCount == 2 && t.Outcome == FlowOutcome.Attention));
            Assert.Contains(await ctx.Kernel.ListTasksAsync(trackId), task => task.TaskKey == "notify" && task.TriggerRunKey is null);
            Assert.Equal(TrackScheduleStates.Done, (await ctx.Store.GetAsync(trackId, "flow:clock", default))!.State);
        }
        finally
        {
            release.TrySetResult();
        }

        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        var delivered = await ReadTaskWakeAsync(ctx, workerTask);
        Assert.Equal(deferred.Id, delivered.Id);
        Assert.Equal(TrackWakeStates.Done, delivered.State);
        Assert.DoesNotContain(ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs), input => input.Prompt == "Cron join");

        async Task FireAsync()
        {
            ctx.Time.Advance(TimeSpan.FromHours(1));
            Assert.Single(await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds()));
        }
    }

    [Fact]
    public async Task ConsecutiveCronStartupPersistenceFailuresDeliverDistinctWakes()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap2) = await ctx.CreateDefaultTrackAsync();
        var track2 = snap2.Track.Id;
        var commit2 = await ctx.Kernel.CommitTrackFlowAsync(track2, false, 0, Human, content: """
            {
              "rev": 0,
              "nodes": [
                { "id": "ticker", "when": { "cron": "0 * * * *", "tz": "UTC" }, "do": { "kind": "run", "prompt": "tick" } },
                { "id": "notify2", "when": { "after": [{ "node": "ticker", "on": "any" }] }, "do": { "kind": "wake", "session": "lead", "prompt": "Ticked" } }
              ]
            }
            """);
        Assert.True(commit2.Ok, string.Join(Environment.NewLine, commit2.Diagnostics));
        ctx.Time.Advance(TimeSpan.FromHours(1));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        var tickerRun1 = await WaitForWorkerRunningAsync(ctx.Kernel, track2, "ticker");
        await ctx.Kernel.CompleteTaskDoneAsync(track2, tickerRun1.SessionId!);
        await ctx.Kernel.WaitForTrackIdleAsync(track2).WaitAsync(TimeSpan.FromSeconds(5));
        var initialTurn = Assert.Single(ctx.Store.GetTurnsForSession(snap2.Track.LeadSessionId!));
        AssertSuperseded(await ReadTaskWakeAsync(ctx, tickerRun1), initialTurn.Id);

        var failRemainingStarts = 2;
        ctx.Store.OnBeforeUpsertTask = (task, _) =>
        {
            if (failRemainingStarts > 0 && task.TaskKey == "ticker" && task.Status == TaskRunStatus.Running)
            {
                failRemainingStarts--;
                throw new IOException("simulated start persistence failure");
            }
        };
        ctx.Time.Advance(TimeSpan.FromHours(1));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await ctx.Kernel.WaitForTrackIdleAsync(track2).WaitAsync(TimeSpan.FromSeconds(5));
        ctx.Time.Advance(TimeSpan.FromHours(1));
        await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds());
        await ctx.Kernel.WaitForTrackIdleAsync(track2).WaitAsync(TimeSpan.FromSeconds(5));
        var startFailureWakes = (await ctx.Store.TrackWakes.ListAsync(track2, default))
            .Where(w => w.Kind == TrackWakeKinds.TaskSettled && w.CoalesceKey is not null && w.CoalesceKey.StartsWith($"task:{tickerRun1.Id}:1:start:", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, startFailureWakes.Length);
        Assert.Equal(2, startFailureWakes.Select(wake => wake.CoalesceKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(startFailureWakes, wake => Assert.Equal(TrackWakeStates.Done, wake.State));
        Assert.Equal(1, Assert.Single(await ctx.Kernel.ListTasksAsync(track2), task => task.TaskKey == "ticker").RunCount);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("rpc_error")]
    [InlineData("interrupted")]
    public async Task LeadWakeFailureBeforeDeliveryLeavesSettlementWakePending(string failure)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var leadId = snapshot.Track.LeadSessionId!;
        var startupFailure = failure == "startup";
        var attempts = 0;
        ctx.AgentRuntime.OnBeforeOpenSession = spec =>
        {
            if (spec.StateKey == leadId && Interlocked.Increment(ref attempts) == 1 && startupFailure)
                throw new IOException("Could not start the explicit wake");
        };
        if (!startupFailure)
            ctx.AgentRuntime.TurnScript = (_, input, _) => ReplyAsync(input, failure);
        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review", "Review"));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("notify", new("wake", Session: "lead", Prompt: "Explicit wake"),
                When: new(After: [new("review", "any")])));

        await ctx.Kernel.CompleteTaskDoneAsync(trackId, worker.Session.Id, outcome: FlowOutcome.Attention);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        var wake = await ReadTaskWakeAsync(ctx, worker.Task);
        Assert.Equal(TrackWakeStates.Done, wake.State);
        Assert.Equal(TrackWakeReasons.Delivered, wake.Reason);
        Assert.Equal(1, wake.Attempts);
        var leadInputs = ctx.AgentRuntime.OpenedSessions.Where(s => s.LaunchSpec.StateKey == leadId)
            .SelectMany(s => s.RecordedInputs).ToArray();
        if (startupFailure)
            Assert.Equal(2, attempts);
        else
            Assert.Single(leadInputs, input => input.Prompt == "Explicit wake");
        Assert.Contains("Task 'review'", Assert.Single(leadInputs, input => input.Prompt != "Explicit wake").Prompt);
        Assert.Contains((await ctx.Kernel.GetTrackFlowAsync(trackId)).Projection.Archived,
            node => node.Id == "notify" && node.Status == "failed");

        static async IAsyncEnumerable<UnifiedAgentEvent> ReplyAsync(TurnInput input, string failure)
        {
            await Task.Yield();
            if (input.Prompt != "Explicit wake")
                yield return new UnifiedAgentEvent.TurnCompleted(null);
            else if (failure == "interrupted")
                yield return new UnifiedAgentEvent.TurnInterrupted();
            else
                yield return new UnifiedAgentEvent.TurnError("OAR_RPC_ERROR", "Process exited before writing the prompt", false,
                    IsConclusive: true, BeforePrompt: false);
        }
    }

    [Fact]
    public async Task ReplacingTemplateTaskReleasesItsWakeEvenWhenTheNodeIdIsReused()
    {
        await using var ctx = new KernelTestContext(policyVm: new global::Perga.PolicyVm.PolicyVm());
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var leadId = snapshot.Track.LeadSessionId!;
        var caller = await ctx.OpenMcpCallerAsync(leadId);
        var initialTurn = Assert.Single(ctx.Store.GetTurnsForSession(leadId));
        await SetTemplateAsync("{id:'review', do:{kind:'run', prompt:'Review', access:'read_only'}}");
        var original = await WaitForWorkerRunningAsync(ctx.Kernel, trackId, "review");
        await ctx.Kernel.CompleteTaskDoneAsync(trackId, original.SessionId!, outcome: FlowOutcome.Attention);
        var pending = await ReadTaskWakeAsync(ctx, original);
        Assert.Equal(TrackWakeStates.Pending, pending.State);
        Assert.Equal(initialTurn.Id, Assert.Single(ctx.Store.GetTurnsForSession(leadId)).Id);

        await SetTemplateAsync("{id:'review', when:{at:'2099-01-01T00:00:00Z'}, do:{kind:'run', cmd:'Replacement'}}");
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));

        var tasks = await ctx.Kernel.ListTasksAsync(trackId);
        Assert.NotNull(Assert.Single(tasks, task => task.Id == original.Id).ArchivedAtMs);
        var replacement = Assert.Single(tasks, task => task.TaskKey == "review");
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.Equal(0, replacement.RunCount);
        var delivered = await ReadTaskWakeAsync(ctx, original);
        Assert.Equal(pending.Id, delivered.Id);
        Assert.Equal(TrackWakeStates.Done, delivered.State);
        Assert.Equal(2, ctx.Store.GetTurnsForSession(leadId).Count);

        Task<JsonElement> SetTemplateAsync(string review) => KernelMcpDispatcher.ExecuteGuardedAsync(ctx.Kernel,
            caller, KernelMcpToolNames.TrackSet,
            TestJson.FromObject(new { source = $$$"""
                export default {id:'review-template', title:'Review template', flow:[
                  {{{review}}},
                  {id:'later', when:{at:'2099-02-01T00:00:00Z'}, do:{kind:'run', cmd:'Later'}},
                  {id:'notify', when:{after:[{node:'review', on:'any'},{node:'later', on:'ok'}]},
                   do:{kind:'wake', session:'lead', prompt:'Template join'}}
                ]};
                """ }));
    }

    [Fact]
    public async Task CancelingArmedWakeWhileLeadBusyReleasesDeferredWake()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var leadId = snapshot.Track.LeadSessionId!;
        var holdLead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AgentRuntime.TurnScript = (_, input, ct) => RunScript(input, holdLead.Task, ct);

        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review", "Review"));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("notify", new("wake", Session: "lead", Prompt: "Review ready"),
                When: new(After: [new("review", "any")])));

        // Hold Lead busy so the explicit wake arms (Pending + TriggerRunKey set) without delivering yet.
        await ctx.Kernel.SendSessionMessageAsync(leadId, new("Hold lead", "hold-lead"), Human, default);
        await WaitUntilAsync(async () => (await ctx.Kernel.GetSessionItemsAsync(leadId, new(), Human, default)).ActiveTurn is not null);

        await ctx.Kernel.CompleteTaskDoneAsync(trackId, worker.Session.Id, outcome: FlowOutcome.Attention);
        await WaitUntilAsync(async () => (await ctx.Kernel.ListTasksAsync(trackId))
            .Any(t => t.TaskKey == "notify" && t.Status == TaskRunStatus.Pending && t.TriggerRunKey == "review#1"));
        var pending = await ReadTaskWakeAsync(ctx, worker.Task);
        Assert.Equal(TrackWakeStates.Pending, pending.State);

        await ctx.Kernel.CancelTaskAsync(trackId, leadId, "notify", "Superseded", default);
        Assert.Equal(pending, await ReadTaskWakeAsync(ctx, worker.Task));

        holdLead.TrySetResult();
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        var delivered = await ReadTaskWakeAsync(ctx, worker.Task);
        Assert.Equal(pending.Id, delivered.Id);
        Assert.Equal(TrackWakeStates.Done, delivered.State);
        Assert.DoesNotContain(ctx.AgentRuntime.OpenedSessions.SelectMany(s => s.RecordedInputs),
            input => input.Prompt == "Review ready");
    }

    [Fact]
    public async Task SilentOneShotWatchAttentionDoesNotEnqueueTaskSettlementWake()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        ctx.CommandRunner.Handler = (_, _) => Task.FromResult(new ShellCommandResult(2, "failed probe", "", false, 1));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("oneshot-watch", new("watch", Cmd: "probe", Interval: "5s", Timeout: "10s", Repeat: false)),
            new FlowNodeSpecDto("blocker", new("run", Cmd: "later"), When: new(At: "2099-01-01T00:00:00Z")),
            new FlowNodeSpecDto("watch-join", new("wake", Session: "lead", Prompt: "Watch joined"),
                When: new(After: [new("oneshot-watch", "attention"), new("blocker", "any")])));
        await WaitUntilAsync(async () => (await ctx.Kernel.ListTasksAsync(trackId))
            .Any(t => t.TaskKey == "oneshot-watch" && t.Status == TaskRunStatus.Done && t.Outcome == FlowOutcome.Attention));
        Assert.Empty(await ctx.Store.TrackWakes.ListAsync(trackId, default));
        await ctx.Kernel.CancelTaskAsync(trackId, snapshot.Track.LeadSessionId!, "watch-join", "Cancel", default);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(await ctx.Store.TrackWakes.ListAsync(trackId, default));
    }

    private static async IAsyncEnumerable<UnifiedAgentEvent> RunScript(
        TurnInput input, Task hold, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (input.Prompt == "Hold lead")
            await hold.WaitAsync(ct);
        yield return new UnifiedAgentEvent.TurnCompleted(null);
    }

    [Fact]
    public async Task UnboundedWatchSiblingDoesNotDeferWorkerDefaultWake()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        ctx.CommandRunner.Handler = (_, _) => Task.FromResult(new ShellCommandResult(75, "still waiting", "", false, 1));
        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review", "Review"));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("forever-watch", new("watch", Cmd: "probe", Interval: "5m", Timeout: "never", Repeat: false)),
            new FlowNodeSpecDto("notify", new("wake", Session: "lead", Prompt: "Both done"),
                When: new(After: [new("review", "any"), new("forever-watch", "any")])));

        await ctx.Kernel.CompleteTaskDoneAsync(trackId, worker.Session.Id, outcome: FlowOutcome.Attention);
        await WaitUntilAsync(async () => (await ReadTaskWakeAsync(ctx, worker.Task)).State == TrackWakeStates.Done);
    }

    [Theory]
    [InlineData("ok", "attention", false)]
    [InlineData("ok", "blocked", false)]
    [InlineData("attention", "blocked", true)]
    public async Task ContradictoryRecurringDependenciesDoNotDeferSettlement(string left, string right, bool transitive)
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review", "Review"));
        FlowAfterRefDto[] contradictory = [new("clock", left), new("clock", right)];
        List<FlowNodeSpecDto> nodes = [new("clock", new("run", Cmd: "tick"), When: new(Cron: "* * * * *", Tz: "UTC"))];
        if (transitive)
            nodes.Add(new("intermediate", new("run", Cmd: "join"), When: new(After: contradictory)));
        FlowAfterRefDto[] after = transitive ? [new("review", "any"), new("intermediate")] : [new("review", "any"), .. contradictory];
        nodes.Add(new("notify", new("wake", Session: "lead", Prompt: "Impossible join"), When: new(After: after)));
        await FlowFixture.AddAsync(ctx.Kernel, trackId, [.. nodes]);

        await ctx.Kernel.CompleteTaskDoneAsync(trackId, worker.Session.Id, outcome: FlowOutcome.Attention);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        var delivered = await ReadTaskWakeAsync(ctx, worker.Task);
        Assert.Equal(TrackWakeStates.Done, delivered.State);
        Assert.Equal(Assert.Single(ctx.Store.GetTurnsForSession(snapshot.Track.LeadSessionId!)).Id, delivered.TurnId);
    }

    [Fact]
    public async Task PausedRepeatingWatchBehindActiveCronDoesNotDeferSettlement()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        ctx.CommandRunner.Handler = (request, _) =>
            Task.FromResult(new ShellCommandResult(request.Command == "probe" ? 2 : 0, "", "", false, 1));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("clock", new("run", Cmd: "tick"), When: new(Cron: "* * * * *", Tz: "UTC")),
            new("watch", new("watch", Cmd: "probe", Interval: "5s", Timeout: "10s", Repeat: true),
                When: new(After: [new("clock")])));
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(await ctx.Kernel.FireDueTrackSchedulesAsync(ctx.Time.GetUtcNow().ToUnixTimeMilliseconds()));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        var paused = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId), task => task.TaskKey == "watch");
        Assert.Equal(FlowOutcome.Attention, paused.Outcome);
        Assert.Equal(TrackScheduleStates.Active, (await ctx.Store.GetAsync(trackId, "flow:clock", default))!.State);

        var worker = await FlowFixture.StartWorkerAsync(ctx.Kernel, trackId, FlowFixture.Worker("review", "Review"));
        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            new FlowNodeSpecDto("notify", new("wake", Session: "lead", Prompt: "Paused join"),
                When: new(After: [new("watch", "ok"), new("review", "any")])));
        await ctx.Kernel.CompleteTaskDoneAsync(trackId, worker.Session.Id);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TrackWakeStates.Done, (await ReadTaskWakeAsync(ctx, worker.Task)).State);
    }

    [Fact]
    public async Task GcToleranceRejectsChangedArchivedIdPreservesBookkeepingOnFailedWriteAndResetsOnRehydration()
    {
        await using var ctx = new KernelTestContext();
        var (_, snapshot) = await ctx.CreateDefaultTrackAsync();
        var trackId = snapshot.Track.Id;
        const string oneShot = """{"id":"one-shot","do":{"kind":"run","cmd":"once"}}""";
        var first = await ctx.Kernel.CommitTrackFlowAsync(trackId, false, 0, Human,
            content: $$"""{"rev":0,"nodes":[{{oneShot}}]}""");
        Assert.True(first.Ok);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, (await ctx.Kernel.GetTrackFlowAsync(trackId)).FlowRev);

        // Changed spec reusing the GC-archived ID is rejected by archived-task validation.
        var reused = await ctx.Kernel.CommitTrackFlowAsync(trackId, true, 1, Human,
            content: """{"rev":1,"nodes":[{"id":"one-shot","do":{"kind":"run","cmd":"changed"}}]}""");
        Assert.False(reused.Ok);
        Assert.Contains(reused.Diagnostics, d => d.Message.Contains("one-shot", StringComparison.Ordinal));

        // Failed persistence leaves GC tolerance intact so a retry succeeds.
        var failOnce = true;
        ctx.Store.OnBeforeCommitTrackFlow = (_, _) =>
        {
            if (failOnce)
            {
                failOnce = false;
                throw new IOException("transient store failure");
            }
        };
        const string nextDraft = """{"rev":1,"nodes":[{"id":"one-shot","do":{"kind":"run","cmd":"once"}},{"id":"future","when":{"at":"2099-01-01T00:00:00Z"},"do":{"kind":"run","cmd":"later"}}]}""";
        await Assert.ThrowsAsync<IOException>(() => ctx.Kernel.CommitTrackFlowAsync(trackId, false, 1, Human, content: nextDraft));
        var retried = await ctx.Kernel.CommitTrackFlowAsync(trackId, true, 1, Human, content: nextDraft);
        Assert.True(retried.Ok, string.Join(Environment.NewLine, retried.Diagnostics));

        // Actor eviction & rehydration resets semantic watermark to durable rev 2, so rev 1 now conflicts.
        await ctx.Kernel.Actors.RemoveAndDisposeAsync(trackId);
        var conflict = await Assert.ThrowsAsync<KernelException>(() =>
            ctx.Kernel.CommitTrackFlowAsync(trackId, true, 1, Human, content: nextDraft));
        Assert.Equal(KernelErrorCode.Conflict, conflict.Code);
    }

    private static async Task<TaskDto> WaitForWorkerRunningAsync(PergaKernelService kernel, string trackId, string taskKey)
    {
        TaskDto? running = null;
        await WaitUntilAsync(async () =>
        {
            running = (await kernel.ListTasksAsync(trackId))
                .FirstOrDefault(t => t.TaskKey == taskKey && t.Status == TaskRunStatus.Running && t.SessionId is not null);
            return running is not null;
        });
        return running!;
    }

    private static async Task<TrackWake> ReadTaskWakeAsync(KernelTestContext ctx, TaskDto task) =>
        Assert.Single(await ctx.Store.TrackWakes.ListAsync(task.TrackId, default),
            wake => wake.CoalesceKey == TrackWakeRequests.TaskSettled(task).CoalesceKey);

    private static void AssertSuperseded(TrackWake wake, string turnId)
    {
        Assert.Equal(TrackWakeStates.Dropped, wake.State);
        Assert.Equal(TrackWakeReasons.SupersededByFlowWake, wake.Reason);
        Assert.Equal(turnId, wake.TurnId);
        Assert.Equal(0, wake.Attempts);
    }
}
