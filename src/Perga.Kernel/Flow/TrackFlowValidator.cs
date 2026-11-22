using System.Globalization;
using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Ports;
using Perga.PolicyVm;

namespace Perga.Kernel.Flow;

/// <summary>
/// Semantic graph, schedule, session reuse, anti-deletion, and template routing validator for Track Flow v1.
/// </summary>
internal static class TrackFlowValidator
{
    internal sealed record WorkerCatalog(IReadOnlyList<ProviderStatus> Providers, string DefaultProvider, string? DefaultModel);

    public static IReadOnlyList<FlowValidationErrorDto> Validate(
        FlowSpecDto nextSpec,
        FlowSpecDto previousSpec,
        IReadOnlyList<TaskDto> existingTasks,
        IReadOnlySet<string> activeTaskIds,
        IReadOnlyDictionary<string, AgentSessionDto> sessions,
        string? leadSessionId,
        CompiledPolicy? policy,
        IPolicyVm policyVm,
        JsonElement templateInputs,
        IScheduleCronEvaluator? cronEvaluator,
        TimeProvider timeProvider,
        IReadOnlySet<string>? replacedTemplateNodeIds = null,
        WorkerCatalog? workerCatalog = null)
    {
        var errors = new List<FlowValidationErrorDto>();
        var nextById = nextSpec.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var prevById = previousSpec.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var tasksByKey = TrackFlowReconciler.BuildTasksByKey(existingTasks);

        for (var i = 0; i < nextSpec.Nodes.Count; i++)
        {
            var node = nextSpec.Nodes[i];
            FlowSyntax.ValidateActionOptions(node.Do, $"$.nodes[{i}].do", errors);
            // Template binding archives the previous task before inserting the replacement.
            if (replacedTemplateNodeIds?.Contains(node.Id) == true)
                continue;
            if (tasksByKey.TryGetValue(node.Id, out var task) &&
                (!FlowConstants.IsFlowTask(task) || task.ArchivedAtMs is not null))
            {
                errors.Add(new($"$.nodes[{i}].id", $"Node ID '{node.Id}' already belongs to another task or archived run.",
                    "Choose a new node ID."));
            }

            if (prevById.TryGetValue(node.Id, out var previous) && previous.Canceled && !node.Canceled)
                errors.Add(new($"$.nodes[{i}].canceled", $"Node '{node.Id}' was already canceled.", "Use a new node ID for a new run."));
        }

        // 1. Validate missing `after` targets
        for (var i = 0; i < nextSpec.Nodes.Count; i++)
        {
            var node = nextSpec.Nodes[i];
            if (node.When?.After is not { Count: > 0 } afterList)
            {
                continue;
            }

            for (var j = 0; j < afterList.Count; j++)
            {
                var dep = afterList[j];
                if (!nextById.ContainsKey(dep.Node))
                {
                    errors.Add(new FlowValidationErrorDto(
                        $"$.nodes[{i}].when.after[{j}]",
                        $"Upstream node '{dep.Node}' referenced by '{node.Id}' does not exist in flow.json.",
                        $"Add node '{dep.Node}' to 'nodes' or remove it from '{node.Id}.when.after'."));
                }
            }
        }

        // 2. Cycle detection across `when.after`
        DetectCycles(nextSpec, errors);

        // 3. Validate cron/tz and ISO `at`
        for (var i = 0; i < nextSpec.Nodes.Count; i++)
        {
            var node = nextSpec.Nodes[i];
            if (node.When?.Cron is { } cron)
            {
                var tz = string.IsNullOrWhiteSpace(node.When.Tz)
                    ? TodayCalendar.IanaId(TodayCalendar.Zone(timeProvider))
                    : node.When.Tz.Trim();
                ValidateCronAndTz($"$.nodes[{i}].when", cron, tz, cronEvaluator, errors);
            }

            if (node.When?.At is { } at)
            {
                if (!TryParseIsoAtMs(at, out _))
                {
                    errors.Add(new FlowValidationErrorDto(
                        $"$.nodes[{i}].when.at",
                        $"Invalid ISO-8601 timestamp '{at}'.",
                        "Use an ISO-8601 timestamp with timezone, e.g. '2026-12-01T09:00:00Z'."));
                }
            }
        }

        // 4. Anti-accidental-deletion guard
        foreach (var prevNode in previousSpec.Nodes)
        {
            if (nextById.ContainsKey(prevNode.Id))
            {
                continue;
            }

            if (FlowConstants.IsRecurringSource(prevNode))
            {
                var kind = prevNode.When?.Cron is not null ? "cron" : "repeating watch";
                errors.Add(new FlowValidationErrorDto(
                    "$.nodes",
                    $"Cannot delete active {kind} node '{prevNode.Id}' from flow.json.",
                    $"Set \"canceled\": true explicitly instead of removing active node '{prevNode.Id}'."));
                continue;
            }

            if (tasksByKey.TryGetValue(prevNode.Id, out var prevTask))
            {
                var isActive = activeTaskIds.Contains(prevTask.Id) ||
                               prevTask.Status is TaskRunStatus.Pending or TaskRunStatus.Running or TaskRunStatus.Verifying;
                if (isActive && !prevNode.Canceled)
                {
                    errors.Add(new FlowValidationErrorDto(
                        "$.nodes",
                        $"Cannot delete active node '{prevNode.Id}' (status: {prevTask.Status.ToString().ToLowerInvariant()}).",
                        $"Set \"canceled\": true explicitly instead of removing active node '{prevNode.Id}'."));
                }
            }
            else if (!prevNode.Canceled)
            {
                errors.Add(new FlowValidationErrorDto(
                    "$.nodes",
                    $"Cannot delete active node '{prevNode.Id}' from flow.json.",
                    $"Set \"canceled\": true explicitly instead of removing active node '{prevNode.Id}'."));
            }
        }

        // 5. Reject mutating `when` or `do` of an actively running node
        for (var i = 0; i < nextSpec.Nodes.Count; i++)
        {
            var nextNode = nextSpec.Nodes[i];
            if (nextNode.Canceled || replacedTemplateNodeIds?.Contains(nextNode.Id) == true ||
                !prevById.TryGetValue(nextNode.Id, out var prevNode))
            {
                continue;
            }

            if (FlowConstants.IsRepeatingWatch(prevNode) && !FlowConstants.IsRepeatingWatch(nextNode))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"$.nodes[{i}].do.repeat",
                    $"Cannot remove 'repeat' from active repeating watch node '{nextNode.Id}'.",
                    $"Set \"canceled\": true explicitly to stop repeating watch '{nextNode.Id}'."));
                continue;
            }

            if (tasksByKey.TryGetValue(nextNode.Id, out var task) &&
                (activeTaskIds.Contains(task.Id) || task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying))
            {
                if (!WhenEquals(prevNode.When, nextNode.When) || !DoEquals(prevNode.Do, nextNode.Do))
                {
                    errors.Add(new FlowValidationErrorDto(
                        $"$.nodes[{i}]",
                        $"Cannot mutate 'when' or 'do' of running node '{nextNode.Id}'.",
                        "Set \"canceled\": true to stop it, or add a new follow-up node instead."));
                }
            }
        }

        // 6. Validate `do.session` ownership, occupancy, and dispatch requirements
        for (var i = 0; i < nextSpec.Nodes.Count; i++)
        {
            var node = nextSpec.Nodes[i];
            if (node.Canceled)
            {
                continue;
            }

            ValidateSessionBinding(node, i, nextById, existingTasks, activeTaskIds, sessions, leadSessionId, errors);
            var reused = node.Do.Session is { } id && sessions.TryGetValue(id, out var session) ? session : null;
            ValidateTaskDispatch(node, i, reused, policy, policyVm, templateInputs, workerCatalog, errors);
        }

        return errors;
    }

    public static bool TryParseIsoAtMs(string raw, out long unixMs)
    {
        unixMs = 0;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        // Require explicit date-time separator and timezone designator (Z or +/-HH:MM) for unambiguous ISO-8601.
        if (!trimmed.Contains('T', StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tIdx = trimmed.IndexOf('T', StringComparison.OrdinalIgnoreCase);
        var timePart = trimmed[(tIdx + 1)..];
        var hasZone = timePart.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
                      timePart.Contains('+') ||
                      timePart.Contains('-');
        if (!hasZone)
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return false;
        }

        unixMs = dto.ToUnixTimeMilliseconds();
        return true;
    }

    private static void ValidateCronAndTz(
        string path,
        string cron,
        string tz,
        IScheduleCronEvaluator? cronEvaluator,
        List<FlowValidationErrorDto> errors)
    {
        if (cronEvaluator is not null)
        {
            if (!cronEvaluator.TryValidate(cron, tz, out _, out var err))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.cron",
                    err ?? $"Invalid cron expression '{cron}' or timezone '{tz}'.",
                    "Use a standard 5-field cron expression (e.g. '0 9 * * *') and a valid IANA timezone (e.g. 'UTC' or 'Asia/Shanghai')."));
            }

            return;
        }

        // Fallback structural validation when no IScheduleCronEvaluator is wired (e.g., isolated unit tests).
        var parts = cron.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (5 or 6))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.cron",
                $"Invalid cron expression '{cron}'. Expected 5-field cron expression.",
                "Example: '0 9 * * *'."));
        }

        if (!TodayCalendar.TryResolveZone(tz, out _))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.tz",
                $"Unknown timezone '{tz}'.",
                "Use a valid IANA timezone such as 'UTC' or 'Asia/Shanghai'."));
        }
    }

    private static void DetectCycles(FlowSpecDto spec, List<FlowValidationErrorDto> errors)
    {
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < spec.Nodes.Count; i++)
        {
            indexById[spec.Nodes[i].Id] = i;
        }

        // 0 = unvisited, 1 = visiting, 2 = visited
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();

        foreach (var node in spec.Nodes)
        {
            if (state.GetValueOrDefault(node.Id) != 0)
            {
                continue;
            }

            if (Dfs(node.Id, spec, indexById, state, stack, out var cyclePath))
            {
                var startIdx = indexById.GetValueOrDefault(cyclePath[0]);
                errors.Add(new FlowValidationErrorDto(
                    $"$.nodes[{startIdx}].when.after",
                    $"Cycle detected in flow graph: {string.Join(" -> ", cyclePath)}.",
                    "Break the cycle by removing one dependency or creating a separate follow-up node reusing the same session."));
                return;
            }
        }
    }

    private static bool Dfs(
        string nodeId,
        FlowSpecDto spec,
        Dictionary<string, int> indexById,
        Dictionary<string, int> state,
        List<string> stack,
        out List<string> cyclePath)
    {
        state[nodeId] = 1;
        stack.Add(nodeId);

        if (indexById.TryGetValue(nodeId, out var idx))
        {
            var node = spec.Nodes[idx];
            if (node.When?.After is { Count: > 0 } after)
            {
                foreach (var dep in after)
                {
                    if (!indexById.ContainsKey(dep.Node))
                    {
                        continue;
                    }

                    var depState = state.GetValueOrDefault(dep.Node);
                    if (depState == 1)
                    {
                        var cycleStart = stack.IndexOf(dep.Node);
                        cyclePath = stack.Skip(cycleStart).Append(dep.Node).ToList();
                        return true;
                    }

                    if (depState == 0 && Dfs(dep.Node, spec, indexById, state, stack, out cyclePath))
                    {
                        return true;
                    }
                }
            }
        }

        stack.RemoveAt(stack.Count - 1);
        state[nodeId] = 2;
        cyclePath = [];
        return false;
    }

    private static void ValidateSessionBinding(
        FlowNodeSpecDto node,
        int index,
        Dictionary<string, FlowNodeSpecDto> nextById,
        IReadOnlyList<TaskDto> existingTasks,
        IReadOnlySet<string> activeTaskIds,
        IReadOnlyDictionary<string, AgentSessionDto> sessions,
        string? leadSessionId,
        List<FlowValidationErrorDto> errors)
    {
        var target = node.Do.Session;
        if (string.IsNullOrWhiteSpace(target) ||
            string.Equals(target, "new", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(node.Do.Kind, "wake", StringComparison.Ordinal) &&
            string.Equals(target, "lead", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(leadSessionId) || !sessions.ContainsKey(leadSessionId))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"$.nodes[{index}].do.session",
                    "Track has no active Lead session to wake.",
                    "Specify an existing session id or ensure the Track has a Lead session."));
            }

            return;
        }

        if (!sessions.TryGetValue(target, out var session))
        {
            errors.Add(new FlowValidationErrorDto(
                $"$.nodes[{index}].do.session",
                $"Session '{target}' does not exist in this Track.",
                "Use session_list to find a valid session id in this Track, or omit 'session' to create a new one."));
            return;
        }

        if (session.ArchivedAtMs is not null)
        {
            errors.Add(new FlowValidationErrorDto(
                $"$.nodes[{index}].do.session",
                $"Session '{target}' is archived and cannot be targeted by flow node '{node.Id}'.",
                "Reopen the session first or omit 'session' to spawn a fresh worker."));
            return;
        }

        if (string.Equals(node.Do.Kind, "run", StringComparison.Ordinal))
        {
            if (session.IsLead || string.Equals(session.Id, leadSessionId, StringComparison.Ordinal))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"$.nodes[{index}].do.session",
                    $"Cannot bind kind='run' node '{node.Id}' to the Lead session '{target}'.",
                    """Use "kind": "wake", "session": "lead" to wake Lead, or target a worker/subagent session."""));
                return;
            }

            // Check if another active task (that this node does NOT wait on via `when.after`) holds this session.
            var occupying = existingTasks.FirstOrDefault(t =>
                !string.Equals(t.TaskKey, node.Id, StringComparison.Ordinal) &&
                string.Equals(t.SessionId, target, StringComparison.Ordinal) &&
                !(t.Kind == "flow_wake" && FlowConstants.IsFlowTask(t)) &&
                (activeTaskIds.Contains(t.Id) || t.Status is TaskRunStatus.Pending or TaskRunStatus.Running or TaskRunStatus.Verifying));

            if (occupying is not null && !TransitivelyDependsOn(node.Id, occupying.TaskKey, nextById))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"$.nodes[{index}].do.session",
                    $"Session '{target}' is currently occupied by active task '{occupying.TaskKey}' (status: {occupying.Status.ToString().ToLowerInvariant()}).",
                    $"Add \"when\": {{ \"after\": [\"{occupying.TaskKey}\"] }} to serialize reuse, or omit 'session' to spawn a new worker."));
            }
        }
    }

    private static bool TransitivelyDependsOn(
        string nodeId,
        string candidateAncestor,
        Dictionary<string, FlowNodeSpecDto> nodesById)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(nodeId);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!nodesById.TryGetValue(cur, out var curNode) || curNode.When?.After is not { Count: > 0 } after)
            {
                continue;
            }

            foreach (var dep in after)
            {
                if (string.Equals(dep.Node, candidateAncestor, StringComparison.Ordinal))
                {
                    return true;
                }

                if (visited.Add(dep.Node))
                {
                    queue.Enqueue(dep.Node);
                }
            }
        }

        return false;
    }

    private static void ValidateTaskDispatch(
        FlowNodeSpecDto node,
        int index,
        AgentSessionDto? reusedSession,
        CompiledPolicy? policy,
        IPolicyVm policyVm,
        JsonElement templateInputs,
        WorkerCatalog? workerCatalog,
        List<FlowValidationErrorDto> errors)
    {
        if (node.Do.Kind != "run")
            return;

        var provider = SessionLaunchValidator.NormalizeTaskProviderId(node.Do.Provider);
        var model = node.Do.Model;
        if (reusedSession is not null)
        {
            if ((provider is not null && provider != reusedSession.Provider) ||
                (model is not null && model != reusedSession.Model))
                errors.Add(new($"$.nodes[{index}].do", "Requested provider/model differ from the target session.",
                    "Use the current session provider/model or create a new session."));
            if (node.Do.ReasoningEffort is { } effort && effort != reusedSession.ReasoningEffort)
                errors.Add(new($"$.nodes[{index}].do.reasoningEffort", "Requested reasoningEffort differs from the target session.",
                    "Use the current session reasoning effort or create a new session."));
        }

        var route = policy is null
            ? new PolicyTaskRoute(reusedSession?.Provider ?? provider, reusedSession is null ? model : reusedSession.Model)
            : FlowTaskRouting.Resolve(node, policy, policyVm, templateInputs, reusedSession);
        if (reusedSession is not null && route.Denial is null && route.Model != reusedSession.Model)
            errors.Add(new($"$.nodes[{index}].do", $"Template routing requires model '{route.Model}' on the target session.",
                "Use a session configured for the routed model."));
        if (route.Denial is { } denial)
        {
            errors.Add(new FlowValidationErrorDto(
                $"$.nodes[{index}].do",
                $"Template routing denied node '{node.Id}': {denial}",
                "Adjust provider/model/access on this node to match template tasks.routing enforcement."));
            return;
        }

        var effectiveAccess = route.Access ?? node.Do.Access ?? "read_write";
        if (policy?.Manifest.RequireGateForReadWrite == true &&
            string.Equals(effectiveAccess, "read_write", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(node.Do.Gate))
        {
            errors.Add(new FlowValidationErrorDto(
                $"$.nodes[{index}].do.gate",
                $"Template policy requires a non-empty 'gate' for read_write node '{node.Id}'.",
                """Add a verification command inside "do": { "gate": "dotnet test ..." }."""));
        }

        if (workerCatalog is not null && node.Do.ReasoningEffort is { } requestedEffort)
        {
            var defaultProvider = SessionLaunchValidator.NormalizeProvider(workerCatalog.DefaultProvider);
            var selected = SessionLaunchValidator.NormalizeProvider(reusedSession?.Provider ?? route.Provider ?? defaultProvider);
            try
            {
                var status = SessionLaunchValidator.ValidateProvider(workerCatalog.Providers, selected, explicitlySelected: true, requireAvailable: true);
                var routedModel = SessionLaunchValidator.NormalizeRequestedModel(route.Model);
                var effectiveModel = reusedSession is not null ? reusedSession.Model :
                    routedModel ?? (selected == defaultProvider ? workerCatalog.DefaultModel : null);
                SessionLaunchValidator.ValidateModelAndEffort(selected, status, reusedSession is null ? routedModel : model,
                    effectiveModel, requestedEffort, requireKnownEffort: true);
            }
            catch (KernelException ex)
            {
                errors.Add(new($"$.nodes[{index}].do.reasoningEffort", ex.Message, ex.NextStep));
            }
        }
    }

    internal static bool WhenEquals(FlowWhenSpecDto? a, FlowWhenSpecDto? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (!string.Equals(a.On, b.On, StringComparison.Ordinal) ||
            !string.Equals(a.Cron, b.Cron, StringComparison.Ordinal) ||
            !string.Equals(a.Tz, b.Tz, StringComparison.Ordinal) ||
            a.MaxOccurrences != b.MaxOccurrences ||
            !string.Equals(a.At, b.At, StringComparison.Ordinal))
        {
            return false;
        }

        var aAfter = a.After ?? [];
        var bAfter = b.After ?? [];
        if (aAfter.Count != bAfter.Count) return false;
        for (var i = 0; i < aAfter.Count; i++)
        {
            if (aAfter[i] != bAfter[i]) return false;
        }

        return true;
    }

    internal static bool DoEquals(FlowDoSpecDto a, FlowDoSpecDto b) => a == b;
}
