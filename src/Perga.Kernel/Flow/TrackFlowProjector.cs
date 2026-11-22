using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Flow;

/// <summary>
/// Read-only projection builder deriving `FlowProjectionDto` from `FlowSpecDto`, tasks, sessions, schedules, and live watches.
/// </summary>
internal static class TrackFlowProjector
{
    public static FlowProjectionDto BuildProjection(
        FlowSpecDto spec,
        string? filePath,
        IReadOnlyList<TaskDto> tasks,
        IReadOnlyDictionary<string, AgentSessionDto> sessions,
        IReadOnlyList<TrackScheduleDto> schedules,
        IReadOnlyDictionary<string, ActiveWatchState> activeWatches,
        Func<string?, string?> interpolate)
    {
        var tasksByKey = TrackFlowReconciler.BuildTasksByKey(tasks);
        var schedulesByNode = schedules
            .Where(s => !string.IsNullOrWhiteSpace(s.NodeKey))
            .GroupBy(s => s.NodeKey!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.UpdatedAtMs).First(), StringComparer.Ordinal);

        var projectedNodes = new List<FlowNodeProjectionDto>(spec.Nodes.Count);
        foreach (var node in spec.Nodes)
        {
            tasksByKey.TryGetValue(node.Id, out var task);
            if (task?.ArchivedAtMs is not null)
            {
                task = null;
            }

            schedulesByNode.TryGetValue(node.Id, out var sched);
            var boundSession = task?.SessionId is { } sid && sessions.TryGetValue(sid, out var s) ? s : null;
            var watchState = task is not null && activeWatches.TryGetValue(task.Id, out var aw) ? aw : null;

            var whenKind = node.When switch
            {
                { Cron: not null } => "cron",
                { At: not null } => "at",
                { After.Count: > 0 } => "after",
                _ => "immediate"
            };

            var afterDeps = node.When?.After is { Count: > 0 } after
                ? after.Select(a => new FlowDependencyProjectionDto(a.Node, a.On)).ToArray()
                : Array.Empty<FlowDependencyProjectionDto>();

            var status = ComputeProjectedStatus(node, task);
            var waitingOn = ComputeWaitingOn(node, status, tasksByKey);
            var gates = ExtractGateProjections(task);
            var outcomeStr = FlowConstants.FormatOutcome(task?.Outcome ?? FlowOutcome.None);
            var effectiveModel = boundSession?.Model ?? node.Do.Model;
            var effectiveProvider = boundSession?.Provider ?? node.Do.Provider;

            var effectiveAccess = task is not null && node.Do.Kind == "run"
                ? (task.Access == AccessMode.ReadOnly ? "read_only" : "read_write")
                : node.Do.Access;

            var watchProgress = watchState?.Progress ?? ExtractPersistedWatchProgress(task);

            projectedNodes.Add(new FlowNodeProjectionDto(
                Id: node.Id,
                Title: interpolate(node.Title) ?? node.Id,
                Source: node.Source,
                WhenKind: whenKind,
                After: afterDeps,
                Cron: node.When?.Cron,
                Tz: node.When?.Tz ?? sched?.Timezone,
                At: node.When?.At,
                DoKind: node.Do.Kind,
                Role: node.Do.Role,
                Access: effectiveAccess,
                TargetSession: node.Do.Session,
                Provider: effectiveProvider,
                Model: effectiveModel,
                Prompt: interpolate(node.Do.Prompt),
                Cmd: interpolate(node.Do.Cmd),
                Gate: interpolate(node.Do.Gate),
                Canceled: node.Canceled,
                Status: status,
                Outcome: outcomeStr,
                OutcomeSummary: task?.OutcomeSummary ?? task?.StatusDetail,
                RunCount: task?.RunCount ?? 0,
                SessionId: task?.SessionId,
                StartedAtMs: task?.StartedAtMs,
                FinishedAtMs: task?.FinishedAtMs,
                NextFireAtMs: sched?.State == TrackScheduleStates.Active ? sched.NextFireAtMs : null,
                LastFiredAtMs: sched?.LastFiredAtMs,
                FiredCount: sched?.FiredCount ?? 0,
                WaitingOn: waitingOn,
                Gates: gates,
                WatchProgress: watchProgress,
                Repeat: FlowConstants.IsRepeatingWatch(node),
                ReasoningEffort: boundSession?.ReasoningEffort ?? node.Do.ReasoningEffort));
        }

        var archived = tasks
            .Where(t => t.ArchivedAtMs is not null)
            .OrderByDescending(t => t.ArchivedAtMs!.Value)
            .ThenByDescending(t => t.UpdatedAtMs)
            .Select(t =>
            {
                var session = t.SessionId is { } sid && sessions.TryGetValue(sid, out var s) ? s : null;
                var doKind = t.Kind switch
                {
                    "flow_wake" => "wake",
                    "flow_watch" => "watch",
                    _ => "run"
                };
                var title = ExtractTitleFromContext(t.Context) ?? t.TaskKey;
                return new FlowArchivedNodeProjectionDto(
                    Id: t.TaskKey,
                    Title: title,
                    DoKind: doKind,
                    Status: t.Status.ToString().ToLowerInvariant(),
                    Outcome: FlowConstants.FormatOutcome(t.Outcome),
                    OutcomeSummary: t.OutcomeSummary ?? t.StatusDetail,
                    RunCount: t.RunCount,
                    SessionId: t.SessionId,
                    Model: session?.Model ?? ExtractModelFromContext(t.Context),
                    FinishedAtMs: t.FinishedAtMs,
                    ArchivedAtMs: t.ArchivedAtMs!.Value);
            })
            .ToArray();

        return new FlowProjectionDto(spec.Rev, filePath, projectedNodes, archived);
    }

    internal static FlowWatchProgressDto? ExtractPersistedWatchProgress(TaskDto? task)
    {
        if (task?.Meta is not { ValueKind: JsonValueKind.Object } meta ||
            !meta.TryGetProperty("watchProgress", out var wpEl) ||
            wpEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return JsonHelpers.Deserialize<FlowWatchProgressDto>(wpEl);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ComputeProjectedStatus(FlowNodeSpecDto node, TaskDto? task)
    {
        if (node.Canceled || task?.Status == TaskRunStatus.Canceled)
        {
            return "canceled";
        }

        if (task is null)
        {
            return "pending";
        }

        if (string.Equals(node.Do.Kind, "watch", StringComparison.Ordinal) &&
            task.Status == TaskRunStatus.Running)
        {
            return "waiting";
        }

        return task.Status switch
        {
            TaskRunStatus.Draft => "pending",
            TaskRunStatus.Pending => "pending",
            TaskRunStatus.Running => "running",
            TaskRunStatus.Verifying => "verifying",
            TaskRunStatus.Done => "done",
            TaskRunStatus.Failed => "failed",
            TaskRunStatus.Canceled => "canceled",
            _ => "pending"
        };
    }

    private static IReadOnlyList<string> ComputeWaitingOn(
        FlowNodeSpecDto node,
        string status,
        Dictionary<string, TaskDto> tasksByKey)
    {
        if (status != "pending" || node.When?.After is not { Count: > 0 } after)
        {
            return [];
        }

        var waiting = new List<string>();
        foreach (var dep in after)
        {
            if (!tasksByKey.TryGetValue(dep.Node, out var depTask) ||
                !FlowConstants.OutcomeMatchesCondition(depTask, dep.On))
            {
                waiting.Add(dep.Node);
            }
        }

        return waiting;
    }

    private static IReadOnlyList<FlowGateCheckProjectionDto> ExtractGateProjections(TaskDto? task)
    {
        if (task?.GateVerdict is not { ValueKind: JsonValueKind.Object } gv ||
            !gv.TryGetProperty("steps", out var stepsEl) ||
            stepsEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<FlowGateCheckProjectionDto>();
        foreach (var step in stepsEl.EnumerateArray())
        {
            var name = step.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() : "gate";
            var exitCode = step.TryGetProperty("exitCode", out var ecEl) && ecEl.TryGetInt32(out var ec) ? ec : 0;
            var passed = step.TryGetProperty("passed", out var pEl) && pEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? pEl.GetBoolean()
                : exitCode == 0;
            var durationMs = step.TryGetProperty("durationMs", out var dEl) && dEl.TryGetInt64(out var dm) ? dm : 0;
            var cmd = step.TryGetProperty("cmd", out var cEl) && cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : null;
            var tail = step.TryGetProperty("outputTail", out var oEl) && oEl.ValueKind == JsonValueKind.String ? oEl.GetString() : null;
            var stdout = step.TryGetProperty("stdout", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
            list.Add(new FlowGateCheckProjectionDto(
                Label: name ?? "gate",
                Status: passed ? "pass" : "fail",
                Detail: durationMs > 0 ? $"{durationMs} ms" : null,
                Command: cmd,
                Output: string.IsNullOrWhiteSpace(tail) ? stdout : tail));
        }

        return list;
    }

    private static string? ExtractTitleFromContext(JsonElement? context) =>
        context is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty("title", out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string? ExtractModelFromContext(JsonElement? context) =>
        context is { ValueKind: JsonValueKind.Object } obj &&
        obj.TryGetProperty("initialModel", out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
