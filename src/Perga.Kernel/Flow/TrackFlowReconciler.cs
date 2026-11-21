using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;

namespace Perga.Kernel.Flow;

/// <summary>
/// Pure diff, GC eligibility, reconciliation, and projection logic for Track Flow v1.
/// </summary>
internal static class TrackFlowReconciler
{
    public static FlowPlanDiffDto ComputePlanDiff(
        FlowSpecDto previousSpec,
        FlowSpecDto nextSpec,
        IReadOnlySet<string> prunedArchiveIds)
    {
        var prevById = previousSpec.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var nextById = nextSpec.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        var added = new List<string>();
        var updated = new List<string>();
        var canceled = new List<string>();
        var archived = new List<string>(prunedArchiveIds.OrderBy(static x => x, StringComparer.Ordinal));
        var unchanged = new List<string>();

        foreach (var prev in previousSpec.Nodes)
        {
            if (!nextById.ContainsKey(prev.Id) && !prunedArchiveIds.Contains(prev.Id))
            {
                archived.Add(prev.Id);
            }
        }

        foreach (var next in nextSpec.Nodes)
        {
            if (prunedArchiveIds.Contains(next.Id))
            {
                continue;
            }

            if (!prevById.TryGetValue(next.Id, out var prev))
            {
                if (next.Canceled)
                {
                    canceled.Add(next.Id);
                }
                else
                {
                    added.Add(next.Id);
                }

                continue;
            }

            if (!prev.Canceled && next.Canceled)
            {
                canceled.Add(next.Id);
            }
            else if (NodeSpecEquals(prev, next))
            {
                unchanged.Add(next.Id);
            }
            else
            {
                updated.Add(next.Id);
            }
        }

        return new FlowPlanDiffDto(
            added,
            updated,
            canceled,
            archived.Distinct(StringComparer.Ordinal).ToArray(),
            unchanged);
    }

    /// <summary>
    /// Computes node IDs eligible for background GC archiving:
    /// 1) One-shot nodes (`when.cron == null`) in terminal status (`Done`/`Failed`/`Canceled`) with NO non-terminal
    ///    downstream dependents in `spec` and NOT transitively dependent on any active (non-canceled) `cron` node.
    /// 2) `canceled: true` cron nodes with no active run and no non-terminal downstream dependents.
    /// </summary>
    public static HashSet<string> ComputeGcEligibleNodeIds(
        FlowSpecDto spec,
        IReadOnlyList<TaskDto> tasks,
        IReadOnlySet<string> activeTaskIds)
    {
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        if (spec.Nodes.Count == 0)
        {
            return eligible;
        }

        var nodesById = spec.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var tasksByKey = BuildTasksByKey(tasks);

        // Build reverse adjacency: node -> direct downstream dependents
        var downstream = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in spec.Nodes)
        {
            if (node.When?.After is not { Count: > 0 } after)
            {
                continue;
            }

            foreach (var dep in after)
            {
                if (!downstream.TryGetValue(dep.Node, out var list))
                {
                    list = [];
                    downstream[dep.Node] = list;
                }

                list.Add(node.Id);
            }
        }

        // Compute nodes fed by any live (non-canceled) cron node — those stay resident across waves!
        var cronFed = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var node in spec.Nodes)
        {
            if (node.When?.Cron is not null && !node.Canceled)
            {
                if (cronFed.Add(node.Id))
                {
                    queue.Enqueue(node.Id);
                }
            }
        }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!downstream.TryGetValue(cur, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (cronFed.Add(child))
                {
                    queue.Enqueue(child);
                }
            }
        }

        bool IsTerminal(FlowNodeSpecDto node)
        {
            if (tasksByKey.TryGetValue(node.Id, out var task))
            {
                if (activeTaskIds.Contains(task.Id))
                {
                    return false;
                }

                return task.Status is TaskRunStatus.Done or TaskRunStatus.Failed or TaskRunStatus.Canceled;
            }

            return node.Canceled;
        }

        // Every surviving node must retain its prerequisites, including one-shot
        // prerequisites of a join that is still fed by a recurring upstream.
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in spec.Nodes)
        {
            if (cronFed.Contains(node.Id) || !IsTerminal(node))
            {
                retained.Add(node.Id);
                queue.Enqueue(node.Id);
            }
        }

        while (queue.TryDequeue(out var id))
        {
            if (nodesById[id].When?.After is not { } dependencies)
                continue;
            foreach (var dependency in dependencies)
            {
                if (nodesById.ContainsKey(dependency.Node) && retained.Add(dependency.Node))
                    queue.Enqueue(dependency.Node);
            }
        }

        eligible.UnionWith(nodesById.Keys.Where(id => !retained.Contains(id)));
        return eligible;
    }

    public static TaskDto BuildOrUpdateTaskForNode(
        string trackId,
        FlowNodeSpecDto node,
        TaskDto? existing,
        long nowMs)
    {
        var (kind, goalOrCmd, access, provider, role, gate, acceptance, context) = MapNodeDoToTaskFields(node);
        var dependsOn = node.When?.After is { Count: > 0 } after
            ? after.Select(a => a.Node).Distinct(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        var isTimed = node.When?.Cron is not null || node.When?.At is not null;
        var hasAfterTrigger = node.When?.After is { Count: > 0 };
        var isImmediate = !isTimed && !hasAfterTrigger;

        if (existing is null)
        {
            var initialStatus = node.Canceled ? TaskRunStatus.Canceled : TaskRunStatus.Pending;
            var initialReady = !node.Canceled && isImmediate;
            return new TaskDto(
                Id: $"tsk_{Guid.NewGuid():N}",
                TrackId: trackId,
                BlockId: FlowConstants.MakeBlockId(node.Id),
                TaskKey: node.Id,
                Generation: 1,
                Kind: kind,
                Provider: provider,
                Access: access,
                Ready: initialReady,
                DependsOn: dependsOn,
                GoalOrCmd: goalOrCmd,
                Context: context,
                Acceptance: acceptance,
                Gate: gate,
                Status: initialStatus,
                StatusDetail: node.Canceled ? "Canceled in flow." : null,
                SemanticVerdict: SemanticVerdict.None,
                SemanticNote: null,
                CreatedAtMs: nowMs,
                UpdatedAtMs: nowMs,
                Role: role,
                SessionId: null,
                RunCount: 0,
                FinishedAtMs: node.Canceled ? nowMs : null,
                Outcome: FlowOutcome.None,
                OutcomeSummary: null,
                TriggerRunKey: null,
                ArchivedAtMs: null);
        }

        var nextStatus = existing.Status;
        var nextReady = existing.Ready;
        long? finishedAt = existing.FinishedAtMs;
        string? statusDetail = existing.StatusDetail;

        if (node.Canceled)
        {
            nextStatus = TaskRunStatus.Canceled;
            nextReady = false;
            finishedAt ??= nowMs;
            statusDetail = string.IsNullOrWhiteSpace(statusDetail) ? "Canceled in flow." : statusDetail;
        }
        else if (existing.ArchivedAtMs is not null)
        {
            // Node id reused after previous archival -> start fresh generation.
            nextStatus = TaskRunStatus.Pending;
            nextReady = isImmediate;
            finishedAt = null;
            statusDetail = null;
        }
        else if (existing.Status == TaskRunStatus.Pending)
        {
            nextReady = isImmediate || existing.Ready;
        }

        var sessionId = existing.SessionId;

        return existing with
        {
            BlockId = FlowConstants.MakeBlockId(node.Id),
            Kind = kind,
            Provider = provider,
            Role = role,
            Access = access,
            Ready = nextReady,
            DependsOn = dependsOn,
            GoalOrCmd = goalOrCmd,
            Context = context,
            Acceptance = acceptance,
            Gate = gate,
            SessionId = sessionId,
            Status = nextStatus,
            StatusDetail = statusDetail,
            FinishedAtMs = finishedAt,
            ArchivedAtMs = null,
            UpdatedAtMs = nowMs
        };
    }

    public static TrackScheduleDto? BuildScheduleForNode(
        string trackId,
        FlowNodeSpecDto node,
        long nowMs,
        IScheduleCronEvaluator? cronEvaluator,
        TimeProvider timeProvider)
    {
        if (node.Canceled || (node.When?.Cron is null && node.When?.At is null))
        {
            return null;
        }

        var scheduleId = FlowConstants.MakeScheduleId(node.Id);
        var title = string.IsNullOrWhiteSpace(node.Title) ? node.Id : node.Title!;
        var prompt = node.Do.Prompt ?? node.Do.Cmd ?? $"Run flow node '{node.Id}'";
        var sourceKind = node.Source is TrackScheduleSources.Agent or TrackScheduleSources.User or TrackScheduleSources.Template
            ? node.Source
            : TrackScheduleSources.Agent;

        if (node.When.Cron is { } cron)
        {
            var rawTz = string.IsNullOrWhiteSpace(node.When.Tz)
                ? TodayCalendar.IanaId(TodayCalendar.Zone(timeProvider))
                : node.When.Tz.Trim();
            var normalizedTz = TodayCalendar.IanaId(TodayCalendar.ResolveZone(rawTz));
            long? nextFire = cronEvaluator?.GetNextOccurrenceMs(cron, normalizedTz, nowMs);
            return new TrackScheduleDto(
                Id: scheduleId,
                TrackId: trackId,
                SourceKind: sourceKind,
                ScheduleKind: TrackScheduleKinds.Cron,
                Title: title,
                Prompt: prompt,
                CronExpr: cron,
                Timezone: normalizedTz,
                RunAtMs: null,
                NextFireAtMs: nextFire,
                LastFiredAtMs: null,
                MaxOccurrences: node.When.MaxOccurrences,
                FiredCount: 0,
                State: TrackScheduleStates.Active,
                CreatedAtMs: nowMs,
                UpdatedAtMs: nowMs,
                NodeKey: node.Id);
        }

        if (node.When.At is { } at && TrackFlowValidator.TryParseIsoAtMs(at, out var runAtMs))
        {
            var defaultTz = TodayCalendar.IanaId(TodayCalendar.Zone(timeProvider));
            return new TrackScheduleDto(
                Id: scheduleId,
                TrackId: trackId,
                SourceKind: sourceKind,
                ScheduleKind: TrackScheduleKinds.Once,
                Title: title,
                Prompt: prompt,
                CronExpr: null,
                Timezone: defaultTz,
                RunAtMs: runAtMs,
                NextFireAtMs: runAtMs,
                LastFiredAtMs: null,
                MaxOccurrences: 1,
                FiredCount: 0,
                State: TrackScheduleStates.Active,
                CreatedAtMs: nowMs,
                UpdatedAtMs: nowMs,
                NodeKey: node.Id);
        }

        return null;
    }

    public static FlowProjectionDto BuildProjection(
        FlowSpecDto spec,
        string? filePath,
        IReadOnlyList<TaskDto> tasks,
        IReadOnlyDictionary<string, AgentSessionDto> sessions,
        IReadOnlyList<TrackScheduleDto> schedules,
        IReadOnlyDictionary<string, ActiveWatchState> activeWatches,
        Func<string?, string?> interpolate)
    {
        var tasksByKey = BuildTasksByKey(tasks);
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
                Access: node.Do.Access,
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
                WatchProgress: watchProgress));
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

    private static (string Kind, string GoalOrCmd, AccessMode Access, string? Provider, string? Role, GateSpecDto? Gate, string? Acceptance, JsonElement Context)
        MapNodeDoToTaskFields(FlowNodeSpecDto node)
    {
        var d = node.Do;
        var access = string.Equals(d.Access, "read_only", StringComparison.OrdinalIgnoreCase)
            ? AccessMode.ReadOnly
            : AccessMode.ReadWrite;

        switch (d.Kind)
        {
            case "wake":
                return (
                    "flow_wake",
                    d.Prompt ?? string.Empty,
                    AccessMode.ReadOnly,
                    null,
                    null,
                    null,
                    null,
                    JsonHelpers.ToElement(new
                    {
                        flowNode = true,
                        title = node.Title ?? node.Id,
                        targetSession = d.Session
                    }));

            case "watch":
                return (
                    "flow_watch",
                    d.Cmd ?? $"prChecks:{d.PrChecks}",
                    AccessMode.ReadOnly,
                    null,
                    null,
                    null,
                    null,
                    JsonHelpers.ToElement(new
                    {
                        flowNode = true,
                        title = node.Title ?? node.Id,
                        prChecks = d.PrChecks,
                        repo = d.Repo,
                        headSha = d.HeadSha,
                        cmd = d.Cmd,
                        interval = d.Interval ?? FlowConstants.DefaultWatchInterval,
                        timeout = d.Timeout ?? FlowConstants.DefaultWatchTimeout
                    }));

            default:
                var gateSpec = string.IsNullOrWhiteSpace(d.Gate)
                    ? null
                    : new GateSpecDto([new GateStepDto("gate", d.Gate!)]);
                if (!string.IsNullOrWhiteSpace(d.Cmd))
                {
                    return (
                        "command",
                        d.Cmd!,
                        access,
                        null,
                        d.Role,
                        gateSpec,
                        d.Acceptance,
                        JsonHelpers.ToElement(new
                        {
                            flowNode = true,
                            title = node.Title ?? node.Id
                        }));
                }

                var reuseSession = !string.IsNullOrWhiteSpace(d.Session) &&
                                   d.Session.StartsWith("ses_", StringComparison.Ordinal);
                return (
                    "agent",
                    d.Prompt ?? string.Empty,
                    access,
                    TaskBlockSynchronizer.NormalizeProviderId(d.Provider),
                    d.Role,
                    gateSpec,
                    d.Acceptance,
                    JsonHelpers.ToElement(new
                    {
                        flowNode = true,
                        title = node.Title ?? node.Id,
                        initialModel = d.Model,
                        targetSession = d.Session,
                        reuseSession
                    }));
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

    private static Dictionary<string, TaskDto> BuildTasksByKey(IReadOnlyList<TaskDto> tasks)
    {
        var map = new Dictionary<string, TaskDto>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (!map.TryGetValue(task.TaskKey, out var existing) ||
                (existing.ArchivedAtMs is not null && task.ArchivedAtMs is null))
            {
                map[task.TaskKey] = task;
            }
        }

        return map;
    }

    private static bool NodeSpecEquals(FlowNodeSpecDto a, FlowNodeSpecDto b) =>
        string.Equals(a.Id, b.Id, StringComparison.Ordinal) &&
        string.Equals(a.Title, b.Title, StringComparison.Ordinal) &&
        string.Equals(a.Source, b.Source, StringComparison.Ordinal) &&
        a.Canceled == b.Canceled &&
        TrackFlowValidator.WhenEquals(a.When, b.When) &&
        TrackFlowValidator.DoEquals(a.Do, b.Do);
}
