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
        var report = await ctx.Kernel.ReadReportAsync(track.Id, ct: ct);
        await ctx.Kernel.CommitReportAsync(track.Id, track.LeadSessionId!, new ReportCommitRequest(report.Anchor,
            [new BlockOp.UpsertBlockOp(null, ReportBlockKind.Task, null, TestJson.FromObject(new
            {
                task_key = "guarded", ready = true, access = "read_write", goal_or_cmd = "Implement", gate = "verify"
            }))]), ct);
        await ctx.Kernel.WaitForTrackIdleAsync(track.Id, ct);
        var worker = Assert.Single((await ctx.Kernel.ListSessionsAsync(track.Id, new(IncludeArchived: true), new InteropPrincipal.Human(), ct)).Select(summary => summary.Session).ToArray(), session => session.BoundAttemptId is not null);
        var finish = ctx.Kernel.CompleteAttemptDoneAsync(track.Id, worker.Id, ct: ct);
        try
        {
            await entered.Task.WaitAsync(ct);
            var blocked = await Assert.ThrowsAsync<KernelException>(() =>
                ctx.Kernel.CancelTaskAsync(track.Id, track.LeadSessionId, "guarded", ct: ct));
            Assert.Equal(KernelErrorCode.Conflict, blocked.Code);
            Assert.Empty(ctx.DirectDriver.SettleLeaseCalls);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(TaskRunStatus.Done, (await finish).Status);
        Assert.Equal(AttemptLeaseDisposition.Promote, Assert.Single(ctx.DirectDriver.SettleLeaseCalls).Disposition);
    }

    [Fact]
    public async Task TaskDone_RunsAfterWorkerDoneCmdJsonMerge_RunsGate_PromotesLeaseOnPass_UnblocksDependent_AndWakesLead()
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

        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [
                new BlockOp.UpsertBlockOp("b_t1", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    task_key = "step-1-write",
                    access = "read_write",
                    ready = true,
                    goal_or_cmd = "Implement core module",
                    gate = new
                    {
                        timeoutSecs = 60,
                        steps = new[] { new { name = "unit-tests", cmd = "dotnet test --no-restore" } }
                    }
                })),
                new BlockOp.UpsertBlockOp("b_t2", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    task_key = "step-2-audit",
                    access = "read_only",
                    ready = true,
                    depends_on = new[] { "step-1-write" },
                    goal_or_cmd = "Audit core module"
                }))
            ]));

        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        // Verify step-1-write is Running with WorkspaceWriteBwrap sandbox in lease.EffectiveCwd, and step-2-audit is still Pending
        var tasksBefore = await ctx.Kernel.ListTasksAsync(trackId);
        var step1 = tasksBefore.Single(t => t.TaskKey == "step-1-write");
        var step2 = tasksBefore.Single(t => t.TaskKey == "step-2-audit");
        Assert.Equal(TaskRunStatus.Running, step1.Status);
        Assert.Equal(TaskRunStatus.Pending, step2.Status);

        var attemptsBefore = await ctx.Kernel.ListTaskAttemptsAsync(trackId, "step-1-write");
        var attempt1 = Assert.Single(attemptsBefore);
        Assert.NotNull(attempt1.WorkspaceLease);
        var expectedLeaseCwd = $"/repo/main/leases/{attempt1.AttemptId}";

        var subagent1 = Assert.Single((await ctx.Kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray(), s => s.BoundAttemptId == attempt1.AttemptId);
        var subagentLaunch = ctx.AgentRuntime.OpenedSessions.Single(s => s.LaunchSpec.Cwd == expectedLeaseCwd);
        Assert.Equal(expectedLeaseCwd, subagentLaunch.LaunchSpec.Cwd);

        var leadTurnsBefore = ctx.Store.GetTurnsForSession(leadId).Count;

        // Subagent calls task_done!
        var doneTask = await ctx.Kernel.CompleteAttemptDoneAsync(
            trackId,
            subagent1.Id,
            TestJson.FromObject(new { summary = "Implemented module cleanly" }));
        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        Assert.Equal(TaskRunStatus.Done, doneTask.Status);

        // Verify afterWorkerDoneCmd & Gate both executed inside lease.EffectiveCwd
        Assert.Contains(ctx.CommandRunner.ExecutedCommands,
            c => c.Command == "collect-vcs-snapshot.sh" && c.WorkingDirectory == expectedLeaseCwd);
        Assert.Contains(ctx.CommandRunner.ExecutedCommands,
            c => c.Command == "dotnet test --no-restore" && c.WorkingDirectory == expectedLeaseCwd);

        // Verify attempt.Meta merged JSON & PolicyVm.OnTaskSettled received merged AttemptMeta
        var settledAttempt = Assert.Single(await ctx.Kernel.ListTaskAttemptsAsync(trackId, "step-1-write"));
        Assert.Equal("done", settledAttempt.Status);
        Assert.Equal("kkmpptxz", settledAttempt.Meta.GetProperty("changeId").GetString());
        Assert.Equal(3, settledAttempt.Meta.GetProperty("changedFiles").GetInt32());
        Assert.NotNull(settledAttempt.GateVerdict);
        Assert.True(settledAttempt.GateVerdict!.Value.GetProperty("passed").GetBoolean());
        var firstGateStep = settledAttempt.GateVerdict.Value.GetProperty("steps")[0];
        Assert.True(firstGateStep.GetProperty("passed").GetBoolean());
        Assert.Contains("All tests passed!", firstGateStep.GetProperty("stdout").GetString());
        Assert.Contains("All tests passed!", firstGateStep.GetProperty("outputTail").GetString());

        Assert.True(ctx.PolicyVm.RecordedSettledEvents.TryDequeue(out var settledCtx));
        Assert.Equal("done", settledCtx.GetProperty("attemptStatus").GetString());
        Assert.Equal("kkmpptxz", settledCtx.GetProperty("meta").GetProperty("changeId").GetString());

        // Verify lease settled with Promote!
        Assert.Contains(ctx.DirectDriver.SettleLeaseCalls,
            s => s.Lease.AttemptId == attempt1.AttemptId && s.Disposition == AttemptLeaseDisposition.Promote);

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
    public async Task TaskDone_WhenGateFails_DiscardsLease_MarksTaskFailed_AndWakesLead(bool runnerThrows)
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

        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [
                new BlockOp.UpsertBlockOp("b_t1", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    task_key = "broken-patch",
                    access = "read_write",
                    ready = true,
                    goal_or_cmd = "Patch parser",
                    gate = new { steps = new[] { new { name = "cargo-test", cmd = "cargo test" } } }
                }))
            ]));

        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        var subagent = Assert.Single((await ctx.Kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray(), s => s.BoundAttemptId is not null);
        var leadTurnsBefore = ctx.Store.GetTurnsForSession(leadId).Count;

        var settled = await ctx.Kernel.CompleteAttemptDoneAsync(trackId, subagent.Id, TestJson.FromObject(new { ok = true }));
        Assert.Equal(TaskRunStatus.Failed, settled.Status);
        Assert.Contains("cargo-test", settled.StatusDetail ?? "");

        // Verify lease was Discarded (NOT Promoted)!
        Assert.Contains(ctx.DirectDriver.SettleLeaseCalls,
            s => s.Lease.AttemptId == subagent.BoundAttemptId && s.Disposition == AttemptLeaseDisposition.Discard);
        Assert.DoesNotContain(ctx.DirectDriver.SettleLeaseCalls,
            s => s.Disposition == AttemptLeaseDisposition.Promote);

        // Verify LeadSessionId woken
        Assert.True(ctx.Store.GetTurnsForSession(leadId).Count > leadTurnsBefore);
    }

    [Fact]
    public async Task TaskFail_And_CommandKindTask_And_ParallelWriteLeases_WorkEndToEnd()
    {
        await using var ctx = new KernelTestContext();
        var (area, _) = await ctx.CreateDefaultTrackAsync();

        // Create track using cow-overlay driver (SupportsParallelIsolatedWriteLeases == true)
        var cowSnap = await ctx.Kernel.CreateTrackAsync(new CreateTrackRequest(
            AreaId: area.Id,
            Title: "CoW Parallel Track",
            Cwd: "/repo/cow",
            WorkspaceDriver: "cow-overlay"));
        var trackId = cowSnap.Track.Id;
        var leadId = cowSnap.Track.LeadSessionId!;

        var r0 = await ctx.Kernel.ReadReportAsync(trackId, leadId);
        await ctx.Kernel.CommitReportAsync(trackId, leadId, new ReportCommitRequest(
            r0.Anchor,
            [
                new BlockOp.UpsertBlockOp("b_rw1", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    task_key = "parallel-rw-1",
                    access = "read_write",
                    ready = true,
                    goal_or_cmd = "Work 1"
                })),
                new BlockOp.UpsertBlockOp("b_rw2", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    task_key = "parallel-rw-2",
                    access = "read_write",
                    ready = true,
                    goal_or_cmd = "Work 2"
                })),
                new BlockOp.UpsertBlockOp("b_cmd", ReportBlockKind.Task, null, TestJson.FromObject(new
                {
                    task_key = "cmd-task",
                    kind = "command",
                    access = "read_only",
                    ready = true,
                    goal_or_cmd = "echo ok"
                }))
            ]));

        await ctx.Kernel.WaitForTrackIdleAsync(trackId);

        // Both ReadWrite tasks are concurrently Running with isolated CoW leases, while command task auto-completed to Done!
        var tasks = await ctx.Kernel.ListTasksAsync(trackId);
        Assert.Equal(TaskRunStatus.Running, tasks.Single(t => t.TaskKey == "parallel-rw-1").Status);
        Assert.Equal(TaskRunStatus.Running, tasks.Single(t => t.TaskKey == "parallel-rw-2").Status);
        Assert.Equal(TaskRunStatus.Done, tasks.Single(t => t.TaskKey == "cmd-task").Status);

        // Fail parallel-rw-1 via task_fail -> discards lease & wakes Lead!
        var sessions = (await ctx.Kernel.ListSessionsAsync(trackId, new(IncludeArchived: true), new InteropPrincipal.Human(), default)).Select(summary => summary.Session).ToArray();
        var rw1Id = tasks.Single(t => t.TaskKey == "parallel-rw-1").Id;
        var sub1 = sessions.Single(s => s.BoundTaskId == rw1Id);

        var failed = await ctx.Kernel.CompleteAttemptFailAsync(trackId, sub1.Id, "Blocked on upstream spec");
        Assert.Equal(TaskRunStatus.Failed, failed.Status);
        Assert.Contains(ctx.CowOverlayDriver.SettleLeaseCalls,
            s => s.Lease.AttemptId == sub1.BoundAttemptId && s.Disposition == AttemptLeaseDisposition.Discard);
    }
}
