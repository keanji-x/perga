using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.Kernel.Tests.Fakes;
using Perga.ReportDoc;
using Perga.VcsKit;

namespace Perga.Kernel.Tests;

public sealed class AttemptExecutionAndGateTests
{
    [Fact]
    public async Task CancelDuringGateRetainsLeaseUntilVerificationFinishes()
    {
        await using var ctx = new KernelTestContext();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.CommandRunner.Handler = async (request, _) =>
        {
            if (request.Command == "verify")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return new ShellCommandResult(0, "", "", TimedOut: false, DurationMs: 1);
        };

        var (_, snap) = await ctx.CreateDefaultTrackAsync();
        var track = snap.Track;
        await FlowFixture.AddAsync(ctx.Kernel, track.Id,
            FlowFixture.Worker("guarded", "Implement", "read_write", gate: "verify"));
        await ctx.Kernel.WaitForTrackIdleAsync(track.Id, ct);
        var guarded = Assert.Single(await ctx.Kernel.ListTasksAsync(track.Id, ct), t => t.TaskKey == "guarded");
        var workerId = guarded.SessionId!;
        var finish = ctx.Kernel.CompleteTaskDoneAsync(track.Id, workerId, ct: ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            var blocked = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.CancelTaskAsync(track.Id, track.LeadSessionId, "guarded", ct: ct));
            Assert.Equal(KernelErrorCode.Conflict, blocked.Code);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(TaskRunStatus.Done, (await finish).Status);
    }

    [Fact]
    public async Task TaskDone_RunsAfterWorkerDoneCmdJsonMerge_RunsGate_UnblocksDependent_AndWakesLead()
    {
        await using var ctx = new KernelTestContext();
        ctx.Kernel.Templates.RegisterTemplate(
            """
            export default defineTemplate({
              id: "ci-tpl",
              title: "CI Template",
              tasks: { afterWorkerDoneCmd: "collect-vcs-snapshot.sh" }
            });
            """);

        ctx.CommandRunner.Handler = (req, _) =>
        {
            if (req.Command == "collect-vcs-snapshot.sh")
            {
                // Deliberately include noisy non-JSON lines + trailing whitespace to verify last valid JSON line extraction!
                var noisyStdout = """
                    [hook] Inspecting working copy...
                    not-json-warning: progress 50%
                    {"changeId":"kkmpptxz","commitId":"a1b2c3d4","changedFiles":3}
                    
                    """;
                return Task.FromResult(new ShellCommandResult(0, noisyStdout, "", TimedOut: false, DurationMs: 8));
            }

            if (req.Command == "dotnet test --no-restore")
            {
                return Task.FromResult(new ShellCommandResult(0, "All tests passed!", "", TimedOut: false, DurationMs: 25));
            }

            return Task.FromResult(new ShellCommandResult(0, "", "", TimedOut: false, DurationMs: 1));
        };

        var (_, snap) = await ctx.CreateDefaultTrackAsync(templateId: "ci-tpl", cwd: "/repo/main");
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            FlowFixture.Worker("step-1-write", "Implement core module", "read_write", gate: "dotnet test --no-restore"),
            FlowFixture.Worker("step-2-audit", "Audit core module", after: "step-1-write"));

        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        // Verify step-1-write is Running in track workspace, and step-2-audit is still Pending
        var tasksBefore = await ctx.Kernel.ListTasksAsync(trackId);
        var step1 = tasksBefore.Single(t => t.TaskKey == "step-1-write");
        var step2 = tasksBefore.Single(t => t.TaskKey == "step-2-audit");
        Assert.Equal(TaskRunStatus.Running, step1.Status);
        Assert.Equal(1, step1.RunCount);
        Assert.NotNull(step1.SessionId);
        Assert.Equal(TaskRunStatus.Pending, step2.Status);

        var expectedCwd = "/repo/main";
        var subagent1 = Assert.Single((await ctx.Kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray(), s => s.Id == step1.SessionId);
        var subagentLaunch = ctx.AgentRuntime.OpenedSessions.Single(s => s.LaunchSpec.StateKey == subagent1.Id);
        Assert.Equal(expectedCwd, subagentLaunch.LaunchSpec.Cwd);

        var leadTurnsBefore = ctx.Store.GetTurnsForSession(leadId).Count;

        // Subagent calls perga_task_done!
        var doneTask = await ctx.Kernel.CompleteTaskDoneAsync(
            trackId,
            subagent1.Id,
            TestJson.FromObject(new { summary = "Implemented module cleanly" }));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        Assert.Equal(TaskRunStatus.Done, doneTask.Status);

        // Verify afterWorkerDoneCmd & Gate both executed inside Track workspace
        Assert.Contains(ctx.CommandRunner.ExecutedCommands,
            c => c.Command == "collect-vcs-snapshot.sh" && c.WorkingDirectory == expectedCwd);
        Assert.Contains(ctx.CommandRunner.ExecutedCommands,
            c => c.Command == "dotnet test --no-restore" && c.WorkingDirectory == expectedCwd);

        // Verify task.Meta merged JSON & PolicyVm.OnTaskSettled received merged Meta
        var settledTask = (await ctx.Kernel.ListTasksAsync(trackId)).Single(t => t.TaskKey == "step-1-write");
        Assert.Equal(TaskRunStatus.Done, settledTask.Status);
        Assert.Equal("kkmpptxz", settledTask.Meta!.Value.GetProperty("changeId").GetString());
        Assert.Equal(3, settledTask.Meta!.Value.GetProperty("changedFiles").GetInt32());
        Assert.NotNull(settledTask.GateVerdict);
        Assert.True(settledTask.GateVerdict!.Value.GetProperty("passed").GetBoolean());
        var firstGateStep = settledTask.GateVerdict.Value.GetProperty("steps")[0];
        Assert.True(firstGateStep.GetProperty("passed").GetBoolean());
        Assert.Contains("All tests passed!", firstGateStep.GetProperty("stdout").GetString());
        Assert.Contains("All tests passed!", firstGateStep.GetProperty("outputTail").GetString());

        Assert.True(ctx.PolicyVm.RecordedSettledEvents.TryDequeue(out var settledCtx));
        Assert.Equal("done", settledCtx.GetProperty("status").GetString());
        Assert.Equal("kkmpptxz", settledCtx.GetProperty("meta").GetProperty("changeId").GetString());

        // Verify LeadSessionId was woken!
        Assert.True(ctx.Store.GetTurnsForSession(leadId).Count > leadTurnsBefore);

        // Verify dependent step-2-audit automatically started!
        var tasksAfter = await ctx.Kernel.ListTasksAsync(trackId);
        var step2After = tasksAfter.Single(t => t.TaskKey == "step-2-audit");
        Assert.Equal(TaskRunStatus.Running, step2After.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskDone_WhenGateFails_MarksTaskFailed_AndWakesLead(bool runnerThrows)
    {
        await using var ctx = new KernelTestContext();
        ctx.CommandRunner.Handler = (req, _) =>
        {
            if (req.Command == "cargo test")
            {
                if (runnerThrows)
                    throw new IOException("Unable to start cargo-test.");
                return Task.FromResult(new ShellCommandResult(101, "", "1 test failed", TimedOut: false, DurationMs: 15));
            }

            return Task.FromResult(new ShellCommandResult(0, "", "", TimedOut: false, DurationMs: 1));
        };

        var (_, snap) = await ctx.CreateDefaultTrackAsync(cwd: "/repo/rust");
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            FlowFixture.Worker("broken-patch", "Patch parser", "read_write", gate: "cargo test"));

        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var brokenTask = Assert.Single(await ctx.Kernel.ListTasksAsync(trackId));
        var leadTurnsBefore = ctx.Store.GetTurnsForSession(leadId).Count;

        var settled = await ctx.Kernel.CompleteTaskDoneAsync(trackId, brokenTask.SessionId!, TestJson.FromObject(new { ok = true }));
        Assert.Equal(TaskRunStatus.Failed, settled.Status);
        Assert.Contains(runnerThrows ? "Unable to start cargo-test." : "gate", settled.StatusDetail ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(settled.FinishedAtMs);

        // Verify LeadSessionId woken
        Assert.True(ctx.Store.GetTurnsForSession(leadId).Count > leadTurnsBefore);
    }

    [Fact]
    public async Task TaskFail_And_CommandKindTask_And_SingleWriterSerialization_WorkEndToEnd()
    {
        await using var ctx = new KernelTestContext();
        var (_, snap) = await ctx.CreateDefaultTrackAsync(cwd: "/repo/serial");
        var trackId = snap.Track.Id;
        var leadId = snap.Track.LeadSessionId!;

        await FlowFixture.AddAsync(ctx.Kernel, trackId,
            FlowFixture.Worker("serial-rw-1", "Work 1", "read_write"),
            FlowFixture.Worker("serial-rw-2", "Work 2", "read_write"),
            new("cmd-task", new("run", Cmd: "echo ok", Access: "read_only")));

        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        // Single-writer policy runs serial-rw-1 first while holding serial-rw-2 and cmd-task until serial-rw-1 settles.
        var tasks = await ctx.Kernel.ListTasksAsync(trackId);
        var rw1 = tasks.Single(t => t.TaskKey == "serial-rw-1");
        Assert.Equal(TaskRunStatus.Running, rw1.Status);
        Assert.Equal(TaskRunStatus.Pending, tasks.Single(t => t.TaskKey == "serial-rw-2").Status);

        // Fail serial-rw-1 via perga_task_fail -> unblocks serial-rw-2 & wakes Lead!
        var failed = await ctx.Kernel.CompleteTaskFailAsync(trackId, rw1.SessionId!, "Blocked on upstream spec");
        Assert.Equal(TaskRunStatus.Failed, failed.Status);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var tasksAfter = await ctx.Kernel.ListTasksAsync(trackId);
        var rw2After = tasksAfter.Single(t => t.TaskKey == "serial-rw-2");
        Assert.Equal(TaskRunStatus.Running, rw2After.Status);
        await ctx.Kernel.CompleteTaskDoneAsync(trackId, rw2After.SessionId!);
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var finalTasks = await ctx.Kernel.ListTasksAsync(trackId);
        Assert.Equal(TaskRunStatus.Done, finalTasks.Single(t => t.TaskKey == "cmd-task").Status);
    }
}
