using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Internal;

namespace Perga.Kernel.Tasks;

public static class TaskGraphScheduler
{
    /// <summary>
    /// Computes the set of tasks eligible to start immediately per DESIGN.md Section 2.7.4.
    /// </summary>
    public static IReadOnlyList<TaskDto> ComputeSchedulable(
        IReadOnlyList<TaskDto> tasks,
        bool allowParallelWriteLeases = false)
    {
        if (tasks.Count == 0)
        {
            return [];
        }

        var byKey = new Dictionary<string, TaskDto>(tasks.Count, StringComparer.Ordinal);
        var byId = new Dictionary<string, TaskDto>(tasks.Count, StringComparer.Ordinal);
        var hasInFlightReadWrite = false;
        var hasInFlightReadOnly = false;

        foreach (var task in tasks)
        {
            byKey[task.TaskKey] = task;
            byId[task.Id] = task;

            if (task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)
            {
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

        if (!allowParallelWriteLeases && hasInFlightReadWrite)
        {
            return [];
        }

        var eligible = new List<(TaskDto Task, int Priority, int OriginalIndex)>();
        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            if (!task.Ready || task.Status != TaskRunStatus.Pending)
            {
                continue;
            }

            if (!AreDependenciesSatisfied(task, byKey, byId))
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

        if (hasInFlightReadOnly)
        {
            return ordered.Where(t => t.Access == AccessMode.ReadOnly).ToArray();
        }

        // Idle (!hasInFlightReadWrite && !hasInFlightReadOnly):
        // Dispatch either the ReadOnly batch or the top single ReadWrite candidate based on top candidate order.
        if (ordered[0].Access == AccessMode.ReadWrite)
        {
            return [ordered[0]];
        }

        return ordered.Where(t => t.Access == AccessMode.ReadOnly).ToArray();
    }

    private static bool AreDependenciesSatisfied(
        TaskDto task,
        Dictionary<string, TaskDto> byKey,
        Dictionary<string, TaskDto> byId)
    {
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

            if (depTask.Status != TaskRunStatus.Done || depTask.SemanticVerdict == SemanticVerdict.Rejected)
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
