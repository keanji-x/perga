using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;

namespace Perga.Kernel.Flow;

/// <summary>
/// Pure diff, GC eligibility, and reconciliation logic for Track Flow v1.
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

        // Provably unreachable one-shot outcome branches (e.g., an `on: attention` branch whose
        // non-cron upstream already settled `ok`, or a downstream of a canceled/unreachable one-shot node)
        // can never fire in the future and should not permanently pin finished one-shot chains.
        var unreachable = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var node in spec.Nodes)
            {
                if (cronFed.Contains(node.Id) || unreachable.Contains(node.Id) || IsTerminal(node))
                {
                    continue;
                }

                if (tasksByKey.TryGetValue(node.Id, out var task) &&
                    (activeTaskIds.Contains(task.Id) || task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying))
                {
                    continue;
                }

                if (node.When?.After is not { Count: > 0 } after)
                {
                    continue;
                }

                foreach (var dep in after)
                {
                    if (!nodesById.TryGetValue(dep.Node, out var depNode) || cronFed.Contains(dep.Node))
                    {
                        continue;
                    }

                    if (unreachable.Contains(dep.Node) ||
                        (IsTerminal(depNode) &&
                         (depNode.Canceled ||
                          !tasksByKey.TryGetValue(dep.Node, out var depTask) ||
                          !FlowConstants.OutcomeMatchesCondition(depTask, dep.On))))
                    {
                        unreachable.Add(node.Id);
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);

        // Every surviving node must retain its prerequisites, including one-shot
        // prerequisites of a join that is still fed by a recurring upstream.
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in spec.Nodes)
        {
            if (cronFed.Contains(node.Id) || (!IsTerminal(node) && !unreachable.Contains(node.Id)))
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

        // While a non-canceled upstream remains retained (e.g. a sibling branch is still pending/running),
        // keep its mismatched alternative branches alive too so a review verdict (`task_reject`/`task_accept`)
        // on that upstream can still activate them. Once the whole upstream chain finishes, all branches archive together.
        bool retainedChanged;
        do
        {
            retainedChanged = false;
            foreach (var id in unreachable)
            {
                if (retained.Contains(id) || nodesById[id].When?.After is not { Count: > 0 } deps)
                    continue;
                if (deps.Any(d => retained.Contains(d.Node) && nodesById.TryGetValue(d.Node, out var up) && !up.Canceled))
                {
                    retained.Add(id);
                    retainedChanged = true;
                }
            }
        } while (retainedChanged);

        eligible.UnionWith(nodesById.Keys.Where(id => !retained.Contains(id)));
        return eligible;
    }

    public static TaskDto ArchiveTaskForGc(TaskDto task, long nowMs) =>
        task.Status == TaskRunStatus.Pending
            ? task with
            {
                Ready = false,
                Status = TaskRunStatus.Canceled,
                StatusDetail = "Skipped: upstream outcome did not match.",
                FinishedAtMs = task.FinishedAtMs ?? nowMs,
                ArchivedAtMs = nowMs,
                UpdatedAtMs = nowMs
            }
            : task with { Ready = false, ArchivedAtMs = nowMs, UpdatedAtMs = nowMs };

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

    internal static Dictionary<string, TaskDto> BuildTasksByKey(IReadOnlyList<TaskDto> tasks)
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
