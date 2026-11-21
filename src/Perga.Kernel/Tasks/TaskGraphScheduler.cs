using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;

namespace Perga.Kernel.Tasks;

public static class TaskGraphScheduler
{
    /// <summary>
    /// Computes the set of tasks eligible to start immediately per DESIGN.md Section 2.7.4
    /// and Track Flow v1 outcome-aware dependency semantics.
    /// </summary>
    public static IReadOnlyList<TaskDto> ComputeSchedulable(
        IReadOnlyList<TaskDto> tasks,
        bool allowParallelWriteLeases = false,
        FlowSpecDto? flowSpec = null)
    {
        if (tasks.Count == 0)
        {
            return [];
        }

        var byKey = new Dictionary<string, TaskDto>(tasks.Count, StringComparer.Ordinal);
        var byId = new Dictionary<string, TaskDto>(tasks.Count, StringComparer.Ordinal);
        var flowNodesById = flowSpec?.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var hasInFlightReadWrite = false;
        var hasInFlightReadOnly = false;

        foreach (var task in tasks)
        {
            if (task.ArchivedAtMs is not null)
            {
                continue;
            }

            byKey[task.TaskKey] = task;
            byId[task.Id] = task;

            if (task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)
            {
                if (IsWorkspaceLockExempt(task))
                {
                    continue;
                }

                if (task.Access == AccessMode.ReadWrite)
                {
                    hasInFlightReadWrite = true;
                }
                else
                {
                    hasInFlightReadOnly = true;
                }
            }
        }

        var eligible = new List<(TaskDto Task, int Priority, int OriginalIndex)>();
        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            if (task.ArchivedAtMs is not null || !task.Ready || task.Status != TaskRunStatus.Pending)
            {
                continue;
            }

            if (!AreDependenciesSatisfied(task, byKey, byId, flowNodesById))
            {
                continue;
            }

            eligible.Add((task, ExtractPriority(task.Context), i));
        }

        if (eligible.Count == 0)
        {
            return [];
        }

        var ordered = eligible
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Task.CreatedAtMs)
            .ThenBy(x => x.OriginalIndex)
            .Select(x => x.Task)
            .ToArray();

        if (allowParallelWriteLeases)
        {
            return ordered;
        }

        var exempt = ordered.Where(IsWorkspaceLockExempt).ToArray();
        var workspaceBound = ordered.Where(t => !IsWorkspaceLockExempt(t)).ToArray();

        if (hasInFlightReadWrite)
        {
            return exempt;
        }

        if (workspaceBound.Length == 0)
        {
            return exempt;
        }

        IReadOnlyList<TaskDto> selectedWorkspace;
        if (hasInFlightReadOnly)
        {
            selectedWorkspace = workspaceBound.Where(t => t.Access == AccessMode.ReadOnly).ToArray();
        }
        else if (workspaceBound[0].Access == AccessMode.ReadWrite)
        {
            selectedWorkspace = [workspaceBound[0]];
        }
        else
        {
            selectedWorkspace = workspaceBound.Where(t => t.Access == AccessMode.ReadOnly).ToArray();
        }

        return exempt.Length == 0 ? selectedWorkspace : [.. exempt, .. selectedWorkspace];
    }

    private static bool IsWorkspaceLockExempt(TaskDto task) =>
        task.Kind is ("flow_watch" or "flow_wake") && FlowConstants.IsFlowTask(task);

    private static bool AreDependenciesSatisfied(
        TaskDto task,
        Dictionary<string, TaskDto> byKey,
        Dictionary<string, TaskDto> byId,
        Dictionary<string, FlowNodeSpecDto>? flowNodesById)
    {
        if (FlowConstants.IsFlowTask(task))
            return flowNodesById?.TryGetValue(task.TaskKey, out var node) == true &&
                !node.Canceled && (node.When?.After is null || task.TriggerRunKey is not null);

        foreach (var dep in task.DependsOn)
        {
            if (string.IsNullOrWhiteSpace(dep))
            {
                continue;
            }

            if (!byKey.TryGetValue(dep, out var depTask) && !byId.TryGetValue(dep, out depTask))
            {
                return false;
            }

            if (depTask.Status != TaskRunStatus.Done ||
                depTask.SemanticVerdict == SemanticVerdict.Rejected ||
                depTask.Outcome is FlowOutcome.Attention or FlowOutcome.Blocked)
            {
                return false;
            }
        }

        return true;
    }

    private static int ExtractPriority(JsonElement? context)
    {
        if (context is { ValueKind: JsonValueKind.Object } obj &&
            JsonHelpers.TryGetPropertyIgnoreCase(obj, ["priority"], out var prioElem) &&
            prioElem.ValueKind == JsonValueKind.Number &&
            prioElem.TryGetInt32(out var prio))
        {
            return prio;
        }

        return 0;
    }
}
