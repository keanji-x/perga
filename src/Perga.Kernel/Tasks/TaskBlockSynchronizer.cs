using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;
using Perga.ReportDoc;

namespace Perga.Kernel.Tasks;

internal sealed record TaskSyncOutcome(
    IReadOnlyList<TaskDto> SyncedTasks,
    IReadOnlyList<TaskDto> ChangedOrAddedTasks,
    IReadOnlyList<string> SupersededOrRemovedActiveTaskIds,
    IReadOnlyList<ReportDiagnosticDto> Diagnostics);

internal static class TaskBlockSynchronizer
{
    public static void ValidatePreCommitBoundTaskFence(
        string? callerBoundTaskId,
        ReportDocument current,
        IReadOnlyList<BlockOp> ops)
    {
        if (string.IsNullOrEmpty(callerBoundTaskId))
        {
            return;
        }

        var existingById = current.Blocks.ToDictionary(b => b.Id, StringComparer.Ordinal);

        foreach (var op in ops)
        {
            switch (op)
            {
                case BlockOp.UpsertBlockOp upsert:
                    if (upsert.Kind == ReportBlockKind.Task)
                    {
                        throw KernelException.Forbidden(
                            $"Session bound to task '{callerBoundTaskId}' is forbidden from creating or editing Task blocks.");
                    }

                    if (upsert.Id is not null &&
                        existingById.TryGetValue(upsert.Id, out var targetUpsert) &&
                        targetUpsert.Kind == ReportBlockKind.Task)
                    {
                        throw KernelException.Forbidden(
                            $"Session bound to task '{callerBoundTaskId}' is forbidden from modifying Task block '{upsert.Id}'.");
                    }
                    break;

                case BlockOp.DeleteBlockOp del:
                    if (existingById.TryGetValue(del.Id, out var targetDel) &&
                        targetDel.Kind == ReportBlockKind.Task)
                    {
                        throw KernelException.Forbidden(
                            $"Session bound to task '{callerBoundTaskId}' is forbidden from deleting Task block '{del.Id}'.");
                    }
                    break;

                case BlockOp.MoveBlockOp move:
                    if (existingById.TryGetValue(move.Id, out var targetMove) &&
                        targetMove.Kind == ReportBlockKind.Task)
                    {
                        throw KernelException.Forbidden(
                            $"Session bound to task '{callerBoundTaskId}' is forbidden from moving Task block '{move.Id}'.");
                    }
                    break;
            }
        }
    }

    public static void ValidatePostCommitBoundTaskFence(
        string? callerBoundTaskId,
        ReportDocument previous,
        ReportCommitResult commitResult)
    {
        if (string.IsNullOrEmpty(callerBoundTaskId))
        {
            return;
        }

        foreach (var change in commitResult.Changes)
        {
            if (change.Kind == ReportBlockKind.Task)
            {
                throw KernelException.Forbidden(
                    $"Session bound to task '{callerBoundTaskId}' is forbidden from mutating Task block '{change.Id}'.");
            }
        }

        var prevTasks = previous.Blocks.Where(b => b.Kind == ReportBlockKind.Task).ToArray();
        var nextTasks = commitResult.NextDocument.Blocks.Where(b => b.Kind == ReportBlockKind.Task).ToArray();

        if (prevTasks.Length != nextTasks.Length)
        {
            throw KernelException.Forbidden(
                $"Session bound to task '{callerBoundTaskId}' is forbidden from adding or removing Task blocks.");
        }

        for (var i = 0; i < prevTasks.Length; i++)
        {
            var prev = prevTasks[i];
            var next = nextTasks[i];
            if (!string.Equals(prev.Id, next.Id, StringComparison.Ordinal) ||
                prev.Rev != next.Rev ||
                !string.Equals(prev.SectionH1, next.SectionH1, StringComparison.Ordinal) ||
                !string.Equals(prev.Markdown, next.Markdown, StringComparison.Ordinal) ||
                !JsonHelpers.JsonEquals(prev.Payload, next.Payload))
            {
                throw KernelException.Forbidden(
                    $"Session bound to task '{callerBoundTaskId}' is forbidden from altering Task block '{prev.Id}'.");
            }
        }
    }

    public static TaskSyncOutcome SyncTasksFromReport(
        string trackId,
        ReportDocument report,
        IReadOnlyList<TaskDto> existingTasks,
        bool requireGateForReadWrite,
        long nowMs)
    {
        var existingByKey = existingTasks.ToDictionary(t => t.TaskKey, StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var synced = new List<TaskDto>();
        var changed = new List<TaskDto>();
        var cancelActiveIds = new List<string>();
        var diagnostics = new List<ReportDiagnosticDto>();

        foreach (var block in report.Blocks)
        {
            if (block.Kind != ReportBlockKind.Task)
            {
                continue;
            }

            var parsed = ParseTaskBlock(trackId, block, nowMs, diagnostics);
            if (FlowConstants.IsFlowBlockId(block.Id) &&
                (FlowConstants.IsFlowTask(parsed) || !existingByKey.TryGetValue(parsed.TaskKey, out var existingBlock) ||
                 existingBlock.BlockId != block.Id))
                throw KernelException.InvalidArgument("New Report task block IDs cannot start with 'flow:'.");
            if (!seenKeys.Add(parsed.TaskKey))
            {
                throw KernelException.InvalidArgument($"Duplicate task_key '{parsed.TaskKey}' in report.");
            }

            if (requireGateForReadWrite &&
                parsed.Access == AccessMode.ReadWrite &&
                (parsed.Gate is null || parsed.Gate.Steps.Count == 0))
            {
                throw KernelException.PolicyDenied(
                    $"Template policy requires a non-empty gate for read_write task '{parsed.TaskKey}'.");
            }

            if (!existingByKey.TryGetValue(parsed.TaskKey, out var oldTask))
            {
                var created = parsed;

                synced.Add(created);
                changed.Add(created);
                continue;
            }

            if (FlowConstants.IsFlowTask(oldTask))
                throw KernelException.Conflict($"Task key '{parsed.TaskKey}' belongs to Track Flow.");

            var contractChanged =
                !string.Equals(oldTask.GoalOrCmd, parsed.GoalOrCmd, StringComparison.Ordinal) ||
                !string.Equals(oldTask.Acceptance, parsed.Acceptance, StringComparison.Ordinal) ||
                !string.Equals(oldTask.Role, parsed.Role, StringComparison.Ordinal) ||
                !GatesEqual(oldTask.Gate, parsed.Gate);

            if (contractChanged)
            {
                if (oldTask.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)
                {
                    cancelActiveIds.Add(oldTask.Id);
                }

                var bumped = oldTask with
                {
                    BlockId = block.Id,
                    Generation = oldTask.Generation + 1,
                    Kind = parsed.Kind,
                    Provider = parsed.Provider,
                    Role = parsed.Role,
                    Access = parsed.Access,
                    Ready = parsed.Ready,
                    DependsOn = parsed.DependsOn,
                    GoalOrCmd = parsed.GoalOrCmd,
                    Context = parsed.Context,
                    Acceptance = parsed.Acceptance,
                    Gate = parsed.Gate,
                    Status = parsed.Ready ? TaskRunStatus.Pending : TaskRunStatus.Draft,
                    StatusDetail = null,
                    FinishedAtMs = null,
                    SemanticVerdict = SemanticVerdict.None,
                    SemanticNote = null,
                    UpdatedAtMs = nowMs
                };

                synced.Add(bumped);
                changed.Add(bumped);
                continue;
            }

            var nextStatus = oldTask.Status;
            if (oldTask.Status == TaskRunStatus.Draft && parsed.Ready)
            {
                nextStatus = TaskRunStatus.Pending;
            }
            else if (oldTask.Status == TaskRunStatus.Pending && !parsed.Ready)
            {
                nextStatus = TaskRunStatus.Draft;
            }

            var metadataChanged =
                !string.Equals(oldTask.BlockId, block.Id, StringComparison.Ordinal) ||
                !string.Equals(oldTask.Kind, parsed.Kind, StringComparison.Ordinal) ||
                !string.Equals(oldTask.Provider, parsed.Provider, StringComparison.Ordinal) ||
                oldTask.Access != parsed.Access ||
                oldTask.Ready != parsed.Ready ||
                !SequenceEqualsOrdinal(oldTask.DependsOn, parsed.DependsOn) ||
                !JsonHelpers.JsonEquals(oldTask.Context, parsed.Context) ||
                nextStatus != oldTask.Status;

            if (metadataChanged)
            {
                var updated = oldTask with
                {
                    BlockId = block.Id,
                    Kind = parsed.Kind,
                    Provider = parsed.Provider,
                    Role = parsed.Role,
                    Access = parsed.Access,
                    Ready = parsed.Ready,
                    DependsOn = parsed.DependsOn,
                    Context = parsed.Context,
                    Status = nextStatus,
                    UpdatedAtMs = nowMs
                };
                synced.Add(updated);
                changed.Add(updated);
            }
            else
            {
                synced.Add(oldTask);
            }
        }

        // Handle previously existing tasks whose blocks were deleted from the report.
        // Flow-owned tasks are governed exclusively by flow.json and preserved intact.
        foreach (var oldTask in existingTasks)
        {
            if (seenKeys.Contains(oldTask.TaskKey))
            {
                continue;
            }

            if (FlowConstants.IsFlowTask(oldTask))
            {
                synced.Add(oldTask);
                continue;
            }

            if (oldTask.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)
            {
                cancelActiveIds.Add(oldTask.Id);
            }

            if (oldTask.Status is TaskRunStatus.Draft or TaskRunStatus.Pending or TaskRunStatus.Running or TaskRunStatus.Verifying)
            {
                var canceled = oldTask with
                {
                    Ready = false,
                    Status = TaskRunStatus.Canceled,
                    StatusDetail = "Task block removed from report.",
                    FinishedAtMs = oldTask.FinishedAtMs ?? nowMs,
                    UpdatedAtMs = nowMs
                };
                synced.Add(canceled);
                changed.Add(canceled);
            }
            else if (oldTask.Ready)
            {
                var unready = oldTask with { Ready = false, UpdatedAtMs = nowMs };
                synced.Add(unready);
                changed.Add(unready);
            }
            else
            {
                synced.Add(oldTask);
            }
        }

        return new TaskSyncOutcome(synced, changed, cancelActiveIds, diagnostics);
    }

    internal static TaskDto ParseTaskBlock(string trackId, ReportBlock block, long nowMs,
        List<ReportDiagnosticDto>? diagnostics = null)
    {
        var parsed = ReadTaskDeclaration(block, diagnostics ?? []);
        return new TaskDto(
            Id: $"tsk_{Guid.NewGuid():N}",
            TrackId: trackId,
            BlockId: block.Id,
            TaskKey: parsed.TaskKey,
            Generation: 1,
            Kind: parsed.Kind,
            Provider: parsed.Provider,
            Role: parsed.Role,
            Access: parsed.Access,
            Ready: parsed.Ready,
            DependsOn: parsed.DependsOn,
            GoalOrCmd: parsed.GoalOrCmd,
            Context: parsed.Context,
            Acceptance: parsed.Acceptance,
            Gate: parsed.Gate,
            Status: parsed.Ready ? TaskRunStatus.Pending : TaskRunStatus.Draft,
            StatusDetail: null,
            SemanticVerdict: SemanticVerdict.None,
            SemanticNote: null,
            CreatedAtMs: nowMs,
            UpdatedAtMs: nowMs);
    }

    private sealed record ParsedTaskDeclaration(
        string TaskKey,
        string Kind,
        string? Provider,
        AccessMode Access,
        bool Ready,
        IReadOnlyList<string> DependsOn,
        string GoalOrCmd,
        JsonElement? Context,
        string? Acceptance,
        GateSpecDto? Gate,
        string? Role = null);

    internal static JsonElement? ExtractTaskJson(ReportBlock block)
    {
        JsonElement? rootObj = null;
        if (block.Payload is { ValueKind: JsonValueKind.Object } payloadObj)
        {
            rootObj = payloadObj;
        }
        else if (!string.IsNullOrWhiteSpace(block.Markdown))
        {
            var trimmed = StripFenceIfPresent(block.Markdown.Trim());
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    rootObj = JsonHelpers.ParseClone(trimmed);
                }
                catch (JsonException ex)
                {
                    throw KernelException.InvalidArgument(
                        $"Task block '{block.Id}' contains invalid JSON: {ex.Message}");
                }
            }
        }

        return rootObj is { ValueKind: JsonValueKind.Object } ? rootObj : null;
    }

    private static ParsedTaskDeclaration ReadTaskDeclaration(ReportBlock block, List<ReportDiagnosticDto> diagnostics)
    {
        var rootObj = ExtractTaskJson(block);
        if (rootObj is null || rootObj.Value.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new ReportDiagnosticDto(
                "warning",
                "task.unstructured",
                $"Task block '{block.Id}' had no JSON payload; using block ID as task_key.",
                block.Id));

            return new ParsedTaskDeclaration(
                TaskKey: block.Id,
                Kind: "agent",
                Provider: null,
                Access: AccessMode.ReadWrite,
                Ready: false,
                DependsOn: [],
                GoalOrCmd: block.Markdown?.Trim() ?? string.Empty,
                Context: null,
                Acceptance: null,
                Gate: null);
        }

        var obj = rootObj.Value;
        var taskKey = ReadString(obj, ["task_key", "taskKey", "key", "id"]) ?? block.Id;
        var rawKind = ReadString(obj, ["kind", "type"]) ?? "agent";
        var rawProvider = ReadString(obj, ["provider"]);
        var (kind, provider) = NormalizeKindAndProvider(rawKind, rawProvider);
        var accessStr = ReadString(obj, ["access", "access_mode", "accessMode"]);
        var access = ParseAccessMode(accessStr);

        var ready = false;
        if (JsonHelpers.TryGetPropertyIgnoreCase(obj, ["ready"], out var readyElem))
        {
            ready = readyElem.ValueKind == JsonValueKind.True ||
                    (readyElem.ValueKind == JsonValueKind.String &&
                     bool.TryParse(readyElem.GetString(), out var rb) && rb);
        }

        var dependsOn = new List<string>();
        if (JsonHelpers.TryGetPropertyIgnoreCase(obj, ["depends_on", "dependsOn"], out var depsElem) &&
            depsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in depsElem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } depKey)
                {
                    dependsOn.Add(depKey);
                }
            }
        }

        var goalOrCmd = ReadString(obj, ["goal_or_cmd", "goalOrCmd", "goal", "cmd", "command", "prompt"]) ?? string.Empty;

        JsonElement? context = null;
        if (JsonHelpers.TryGetPropertyIgnoreCase(obj, ["context"], out var ctxElem) &&
            ctxElem.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            context = ctxElem.Clone();
        }

        if (ReadString(obj, ["model"]) is { } model)
        {
            var values = context is { ValueKind: JsonValueKind.Object } existingContext
                ? System.Text.Json.Nodes.JsonNode.Parse(existingContext.GetRawText())!.AsObject() : new System.Text.Json.Nodes.JsonObject();
            values["initialModel"] = model;
            context = JsonHelpers.ParseClone(values.ToJsonString());
        }
        var acceptance = ReadString(obj, ["acceptance", "acceptance_criteria", "acceptanceCriteria"]);
        var gate = ParseGateSpec(obj);

        return new ParsedTaskDeclaration(
            TaskKey: taskKey,
            Kind: kind,
            Provider: provider,
            Access: access,
            Ready: ready,
            DependsOn: dependsOn,
            GoalOrCmd: goalOrCmd,
            Context: context,
            Acceptance: acceptance,
            Gate: gate,
            Role: ReadString(obj, ["role"]));
    }

    internal static string? NormalizeProviderId(string? rawProvider)
    {
        if (string.IsNullOrWhiteSpace(rawProvider))
        {
            return null;
        }

        var trimmed = rawProvider.Trim();
        if (string.Equals(trimmed, "antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return "acp:antigravity";
        }

        if (string.Equals(trimmed, "opencode", StringComparison.OrdinalIgnoreCase))
        {
            return "acp:opencode";
        }

        if (string.Equals(trimmed, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return "codex";
        }

        if (string.Equals(trimmed, "claude", StringComparison.OrdinalIgnoreCase))
        {
            return "claude";
        }

        if (trimmed.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
        {
            return "acp:" + trimmed[4..].ToLowerInvariant();
        }

        return trimmed;
    }

    private static (string Kind, string? Provider) NormalizeKindAndProvider(string rawKind, string? rawProvider)
    {
        var trimmedKind = rawKind.Trim();
        var normalizedProvider = NormalizeProviderId(rawProvider);

        if (normalizedProvider is null &&
            (string.Equals(trimmedKind, "codex", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trimmedKind, "claude", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trimmedKind, "antigravity", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(trimmedKind, "opencode", StringComparison.OrdinalIgnoreCase) ||
             trimmedKind.StartsWith("acp:", StringComparison.OrdinalIgnoreCase)))
        {
            normalizedProvider = NormalizeProviderId(trimmedKind);
            trimmedKind = "agent";
        }

        return (trimmedKind, normalizedProvider);
    }

    private static string StripFenceIfPresent(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewLine = text.IndexOf('\n');
        if (firstNewLine < 0)
        {
            return text;
        }

        var body = text[(firstNewLine + 1)..];
        var closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            body = body[..closingFence];
        }

        return body.Trim();
    }

    private static AccessMode ParseAccessMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AccessMode.ReadWrite;
        }

        var normalized = raw.Trim().Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        if (string.Equals(normalized, "readonly", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "ro", StringComparison.OrdinalIgnoreCase))
        {
            return AccessMode.ReadOnly;
        }

        return AccessMode.ReadWrite;
    }

    private static GateSpecDto? ParseGateSpec(JsonElement obj)
    {
        if (!JsonHelpers.TryGetPropertyIgnoreCase(obj, ["gate"], out var gateElem) ||
            gateElem.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (gateElem.ValueKind == JsonValueKind.Array)
        {
            var steps = ParseGateStepsArray(gateElem);
            return new GateSpecDto(steps);
        }

        if (gateElem.ValueKind == JsonValueKind.Object)
        {
            var timeoutSecs = 1800;
            if (JsonHelpers.TryGetPropertyIgnoreCase(gateElem, ["timeout_secs", "timeoutSecs", "timeout"], out var timeoutElem) &&
                timeoutElem.ValueKind == JsonValueKind.Number &&
                timeoutElem.TryGetInt32(out var parsedTimeout) &&
                parsedTimeout > 0)
            {
                timeoutSecs = parsedTimeout;
            }

            var steps = new List<GateStepDto>();
            if (JsonHelpers.TryGetPropertyIgnoreCase(gateElem, ["steps"], out var stepsElem) &&
                stepsElem.ValueKind == JsonValueKind.Array)
            {
                steps.AddRange(ParseGateStepsArray(stepsElem));
            }
            else if (ReadString(gateElem, ["cmd", "command"]) is { Length: > 0 } singleCmd)
            {
                var stepName = ReadString(gateElem, ["name"]) ?? "gate";
                steps.Add(new GateStepDto(stepName, singleCmd));
            }

            return new GateSpecDto(steps, timeoutSecs);
        }

        if (gateElem.ValueKind == JsonValueKind.String && gateElem.GetString() is { Length: > 0 } cmdStr)
        {
            return new GateSpecDto([new GateStepDto("gate", cmdStr)]);
        }

        return null;
    }

    private static List<GateStepDto> ParseGateStepsArray(JsonElement arrayElem)
    {
        var steps = new List<GateStepDto>();
        var idx = 1;
        foreach (var item in arrayElem.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } rawCmd)
            {
                steps.Add(new GateStepDto($"step_{idx}", rawCmd));
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                var cmd = ReadString(item, ["cmd", "command"]);
                if (!string.IsNullOrWhiteSpace(cmd))
                {
                    var name = ReadString(item, ["name"]) ?? $"step_{idx}";
                    steps.Add(new GateStepDto(name, cmd));
                }
            }

            idx++;
        }

        return steps;
    }

    private static string? ReadString(JsonElement obj, ReadOnlySpan<string> keys)
    {
        if (JsonHelpers.TryGetPropertyIgnoreCase(obj, keys, out var elem) && elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString();
        }

        return null;
    }

    public static bool GatesEqual(GateSpecDto? left, GateSpecDto? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left.TimeoutSecs != right.TimeoutSecs || left.Steps.Count != right.Steps.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Steps.Count; i++)
        {
            if (!string.Equals(left.Steps[i].Name, right.Steps[i].Name, StringComparison.Ordinal) ||
                !string.Equals(left.Steps[i].Cmd, right.Steps[i].Cmd, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequenceEqualsOrdinal(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
