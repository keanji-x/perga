using System.Text.Json;
using System.Text.RegularExpressions;
using Perga.Contracts;

namespace Perga.Kernel.Flow;

public static partial class FlowConstants
{
    public const string FlowBlockIdPrefix = "flow:";
    public const string FlowScheduleIdPrefix = "flow:";
    public const string SchemaPointer = "../../schemas/flow.v1.schema.json";

    [GeneratedRegex(@"\$\{inputs\.([A-Za-z0-9_.-]+)\}", RegexOptions.CultureInvariant)]
    public static partial Regex InputInterpolationRegex();

    public static bool IsFlowTask(TaskDto task) =>
        task.BlockId == MakeBlockId(task.TaskKey) &&
        task.Context is { ValueKind: JsonValueKind.Object } context &&
        context.TryGetProperty("flowNode", out var marker) && marker.ValueKind == JsonValueKind.True;

    public static string MakeBlockId(string nodeId) => FlowBlockIdPrefix + nodeId;
    public static string MakeScheduleId(string nodeId) => FlowScheduleIdPrefix + nodeId;

    public static string FormatOutcome(FlowOutcome outcome) => outcome switch
    {
        FlowOutcome.Ok => "ok",
        FlowOutcome.Attention => "attention",
        FlowOutcome.Blocked => "blocked",
        _ => "none"
    };

    public static bool TryParseOutcome(string? raw, out FlowOutcome outcome)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "ok":
                outcome = FlowOutcome.Ok;
                return true;
            case "attention":
                outcome = FlowOutcome.Attention;
                return true;
            case "blocked":
                outcome = FlowOutcome.Blocked;
                return true;
            default:
                outcome = FlowOutcome.None;
                return false;
        }
    }

    public static bool OutcomeMatchesCondition(TaskDto depTask, string? condition)
    {
        if (depTask.RunCount <= 0)
        {
            return false;
        }

        if (depTask.Status is not (TaskRunStatus.Done or TaskRunStatus.Failed))
        {
            return false;
        }

        var effectiveOutcome = depTask.Outcome != FlowOutcome.None
            ? depTask.Outcome
            : depTask.Status == TaskRunStatus.Failed
                ? FlowOutcome.Attention
                : FlowOutcome.Ok;

        var normalized = string.IsNullOrWhiteSpace(condition) ? "ok" : condition.Trim().ToLowerInvariant();
        return normalized switch
        {
            "any" => effectiveOutcome is FlowOutcome.Ok or FlowOutcome.Attention or FlowOutcome.Blocked,
            "ok" => effectiveOutcome == FlowOutcome.Ok,
            "attention" => effectiveOutcome == FlowOutcome.Attention,
            "blocked" => effectiveOutcome == FlowOutcome.Blocked,
            _ => false
        };
    }

    public static string? InterpolateInputs(string? text, JsonElement inputs)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("${inputs.", StringComparison.Ordinal))
        {
            return text;
        }

        if (inputs.ValueKind != JsonValueKind.Object)
        {
            return text;
        }

        return InputInterpolationRegex().Replace(text, match =>
        {
            var path = match.Groups[1].Value;
            if (!TryResolveInputPath(inputs, path, out var val))
            {
                return match.Value;
            }

            return val.ValueKind switch
            {
                JsonValueKind.String => val.GetString() ?? string.Empty,
                JsonValueKind.Number => val.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => val.GetRawText()
            };
        });
    }

    private static bool TryResolveInputPath(JsonElement root, string path, out JsonElement value)
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                value = default;
                return false;
            }
        }

        value = current;
        return true;
    }

    public static string ComputeWaveTriggerKey(IReadOnlyList<FlowAfterRefDto> after, IReadOnlyDictionary<string, TaskDto> tasksByKey)
    {
        var parts = new List<string>(after.Count);
        foreach (var dep in after.OrderBy(a => a.Node, StringComparer.Ordinal))
        {
            var runCount = tasksByKey.TryGetValue(dep.Node, out var depTask) ? depTask.RunCount : 0;
            parts.Add($"{dep.Node}#{runCount}");
        }

        return string.Join(",", parts);
    }

    public static string? ExtractTailLine(string? stdout, string? stderr, int maxChars = 400)
    {
        var combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout ?? string.Empty
            : string.IsNullOrWhiteSpace(stdout)
                ? stderr!
                : $"{stdout}\n{stderr}";
        var trimmed = combined.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[^maxChars..].TrimStart();
    }
}

internal sealed class ActiveWatchState(
    string taskId,
    string nodeId,
    int runCount,
    CancellationTokenSource cts,
    bool isWorkspaceExempt,
    FlowWatchProgressDto initialProgress)
{
    public string TaskId { get; } = taskId;
    public string NodeId { get; } = nodeId;
    public int RunCount { get; } = runCount;
    public CancellationTokenSource Cts { get; } = cts;
    public bool IsWorkspaceExempt { get; } = isWorkspaceExempt;
    public FlowWatchProgressDto Progress { get; set; } = initialProgress;
    public Task? Execution { get; set; }
}
