using Perga.Contracts;
using Perga.Kernel.Tasks;
using Perga.Kernel.Tests.Fakes;

namespace Perga.Kernel.Tests;

public sealed class TaskGraphSchedulerTests
{
    private static TaskDto MakeTask(
        string key,
        AccessMode access,
        TaskRunStatus status = TaskRunStatus.Pending,
        bool ready = true,
        IReadOnlyList<string>? dependsOn = null,
        long createdAtMs = 1000,
        int? priority = null)
    {
        return new TaskDto(
            Id: $"id_{key}",
            TrackId: "trk_1",
            BlockId: $"b_{key}",
            TaskKey: key,
            Generation: 1,
            Kind: "agent",
            Provider: null,
            Access: access,
            Ready: ready,
            DependsOn: dependsOn ?? [],
            GoalOrCmd: $"Do {key}",
            Context: priority.HasValue ? TestJson.FromObject(new { priority = priority.Value }) : null,
            Acceptance: null,
            Gate: null,
            Status: status,
            StatusDetail: null,
            SemanticVerdict: SemanticVerdict.None,
            SemanticNote: null,
            LatestAttemptId: null,
            CreatedAtMs: createdAtMs,
            UpdatedAtMs: createdAtMs);
    }

    [Fact]
    public void ComputeSchedulable_FiltersUnreadyDraftAndUnsatisfiedDependencies()
    {
        var tasks = new[]
        {
            MakeTask("draft", AccessMode.ReadOnly, status: TaskRunStatus.Draft, ready: false),
            MakeTask("unready", AccessMode.ReadOnly, status: TaskRunStatus.Pending, ready: false),
            MakeTask("dep_pending", AccessMode.ReadOnly, status: TaskRunStatus.Pending, ready: true),
            MakeTask("blocked_child", AccessMode.ReadOnly, status: TaskRunStatus.Pending, ready: true, dependsOn: ["dep_pending"]),
            MakeTask("dep_done", AccessMode.ReadWrite, status: TaskRunStatus.Done, ready: true),
            MakeTask("ready_child", AccessMode.ReadOnly, status: TaskRunStatus.Pending, ready: true, dependsOn: ["dep_done"])
        };

        var result = TaskGraphScheduler.ComputeSchedulable(tasks, allowParallelWriteLeases: false);
        Assert.Equal(["dep_pending", "ready_child"], result.Select(t => t.TaskKey).ToArray());
    }

    [Fact]
    public void ComputeSchedulable_WhenNoParallelWriteLeases_RunningOrVerifyingReadWriteBlocksAll()
    {
        var withRunningRw = new[]
        {
            MakeTask("rw_running", AccessMode.ReadWrite, status: TaskRunStatus.Running),
            MakeTask("ro_waiting", AccessMode.ReadOnly, status: TaskRunStatus.Pending),
            MakeTask("rw_waiting", AccessMode.ReadWrite, status: TaskRunStatus.Pending)
        };
        Assert.Empty(TaskGraphScheduler.ComputeSchedulable(withRunningRw, allowParallelWriteLeases: false));

        var withVerifyingRw = new[]
        {
            MakeTask("rw_verifying", AccessMode.ReadWrite, status: TaskRunStatus.Verifying),
            MakeTask("ro_waiting", AccessMode.ReadOnly, status: TaskRunStatus.Pending)
        };
        Assert.Empty(TaskGraphScheduler.ComputeSchedulable(withVerifyingRw, allowParallelWriteLeases: false));
    }

    [Fact]
    public void ComputeSchedulable_WhenNoParallelWriteLeases_RunningReadOnlyAllowsOnlyReadOnly()
    {
        var tasks = new[]
        {
            MakeTask("ro_running", AccessMode.ReadOnly, status: TaskRunStatus.Running, createdAtMs: 100),
            MakeTask("rw_high_prio", AccessMode.ReadWrite, status: TaskRunStatus.Pending, createdAtMs: 200, priority: 100),
            MakeTask("ro_next_1", AccessMode.ReadOnly, status: TaskRunStatus.Pending, createdAtMs: 300),
            MakeTask("ro_next_2", AccessMode.ReadOnly, status: TaskRunStatus.Pending, createdAtMs: 400)
        };

        var result = TaskGraphScheduler.ComputeSchedulable(tasks, allowParallelWriteLeases: false);
        Assert.Equal(["ro_next_1", "ro_next_2"], result.Select(t => t.TaskKey).ToArray());
    }

    [Fact]
    public void ComputeSchedulable_WhenIdleAndNoParallelWriteLeases_DispatchesReadOnlyBatchOrTopSingleReadWrite()
    {
        // Case A: Top candidate is ReadOnly -> dispatches full ReadOnly batch, holds ReadWrite
        var roFirst = new[]
        {
            MakeTask("ro_1", AccessMode.ReadOnly, createdAtMs: 100),
            MakeTask("rw_1", AccessMode.ReadWrite, createdAtMs: 200),
            MakeTask("ro_2", AccessMode.ReadOnly, createdAtMs: 300)
        };
        var batchRes = TaskGraphScheduler.ComputeSchedulable(roFirst, allowParallelWriteLeases: false);
        Assert.Equal(["ro_1", "ro_2"], batchRes.Select(t => t.TaskKey).ToArray());

        // Case B: Top candidate is ReadWrite -> dispatches ONLY that single ReadWrite task
        var rwFirst = new[]
        {
            MakeTask("rw_1", AccessMode.ReadWrite, createdAtMs: 100),
            MakeTask("rw_2", AccessMode.ReadWrite, createdAtMs: 200),
            MakeTask("ro_1", AccessMode.ReadOnly, createdAtMs: 300)
        };
        var singleRwRes = TaskGraphScheduler.ComputeSchedulable(rwFirst, allowParallelWriteLeases: false);
        Assert.Equal(["rw_1"], singleRwRes.Select(t => t.TaskKey).ToArray());
    }

    [Fact]
    public void ComputeSchedulable_WhenParallelWriteLeasesEnabled_DispatchesReadOnlyAndMultipleReadWriteConcurrently()
    {
        var tasks = new[]
        {
            MakeTask("rw_running", AccessMode.ReadWrite, status: TaskRunStatus.Running, createdAtMs: 50),
            MakeTask("rw_1", AccessMode.ReadWrite, status: TaskRunStatus.Pending, createdAtMs: 100),
            MakeTask("ro_1", AccessMode.ReadOnly, status: TaskRunStatus.Pending, createdAtMs: 200),
            MakeTask("rw_2", AccessMode.ReadWrite, status: TaskRunStatus.Pending, createdAtMs: 300)
        };

        var result = TaskGraphScheduler.ComputeSchedulable(tasks, allowParallelWriteLeases: true);
        Assert.Equal(["rw_1", "ro_1", "rw_2"], result.Select(t => t.TaskKey).ToArray());
    }
}
