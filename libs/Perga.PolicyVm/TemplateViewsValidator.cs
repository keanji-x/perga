using System.Text.Json;

namespace Perga.PolicyVm;

internal static class TemplateViewsValidator
{
    private static readonly Field[] Check =
    [
        Text("label", required: true, min: 1), Enum("status", "pass running fail pending", required: true),
        Text("detail"), Text("command"), Text("output")
    ];

    private static readonly Field[] Expectation =
    [
        Text("label", required: true, min: 1), Text("expected", required: true, min: 1),
        Text("actual"), Enum("status", "pending beat inline miss")
    ];

    private static readonly Field[] Trigger =
    [
        Text("trigger", required: true, min: 1), Text("action", required: true, min: 1),
        Text("assign", required: true, min: 1), Enum("assignKind", "model command")
    ];

    private static readonly Field[] Item =
    [
        Text("id", required: true, min: 1, max: 120), Text("title", required: true, min: 1),
        Enum("status", "done running pending blocked", required: true),
        Text("statusLabel"), Text("triggerAt"), Text("assign"), Enum("assignKind", "model command"),
        Text("role", max: 64), Text("summary"), Text("docPath", min: 1), Text("docLabel", min: 1),
        Text("sessionId", min: 1, max: 160), Text("taskKey", min: 1, max: 160),
        Text("model", min: 1, max: 160), Text("workerLabel", min: 1), Text("constraint", min: 1),
        Text("blockedReason", min: 1), Strings("bullets", 12), Strings("dependsOn", 16),
        Objects("checks", Check, 16), Objects("expectations", Expectation, 12),
    ];

    private static readonly Field[] ListItem =
    [
        .. Item, Text("group"), Text("date"), Text("category"), Text("verdict"),
        Text("ruleBadge", min: 1), Objects("triggers", Trigger, 16)
    ];

    private static readonly Field[] Metric =
    [
        Text("code", min: 1), Text("label", required: true, min: 1), Text("tag"), Text("current"),
        Enum("status", "met on_track at_risk pending"), Text("updatedAt"), Text("evidence"), Text("unit"),
        Number("target"), Enum("direction", "lte gte"), Text("targetLabel"),
        Objects("points", [Text("date", required: true, min: 1), Number("value", required: true), Text("note")],
            max: 90, min: 2)
    ];

    private static readonly Field[] Range =
    [
        Text("unit"),
        Object("current", [Number("value", required: true), Text("display", min: 1), Text("label"), Text("note")]),
        Objects("points", [Number("value", required: true), Text("display", min: 1),
            Text("note", required: true, min: 1), Boolean("emphasis")], max: 8, min: 2, required: true)
    ];

    private static readonly Field[] Checkpoints =
    [
        Text("title"), Text("tier"), Ignored("layout"), Ignored("triggers"),
        Object("range", Range), Objects("metrics", Metric, max: 12, min: 1),
        Objects("rules", [Text("label", required: true, min: 1), Text("text", required: true, min: 1)], max: 16),
        Objects("items", ListItem, max: 100, required: true)
    ];

    private const string FlowStatuses = "pending waiting running verifying done failed canceled";
    private const string FlowOutcomes = "none ok attention blocked";
    private static readonly Field[] FlowNode =
    [
        Text("id", required: true, max: int.MaxValue), Text("title", required: true, max: int.MaxValue),
        Enum("status", FlowStatuses), Enum("outcome", FlowOutcomes), Enum("doKind", "run wake watch"),
        Count("runCount"), .. UnboundedText("sessionId model outcomeSummary"), Number("finishedAtMs")
    ];

    private static readonly Field[] FlowActiveNode =
    [
        .. FlowNode, Enum("source", "agent user template"), Enum("whenKind", "immediate after cron at"),
        Enum("access", "read_only read_write"), Boolean("canceled"),
        .. UnboundedText("cron tz at role targetSession provider prompt cmd gate"),
        Number("startedAtMs"), Number("nextFireAtMs"), Number("lastFiredAtMs"), Count("firedCount"),
        Strings("waitingOn", int.MaxValue, min: 0),
        Objects("after", [Text("node", required: true, max: int.MaxValue), Enum("on", "ok attention blocked any")],
            int.MaxValue, strict: false),
        Objects("gates", [Text("label", required: true, max: int.MaxValue), Enum("status", "pass fail running", required: true),
            .. UnboundedText("detail command output")], int.MaxValue, strict: false),
        Object("watchProgress", [Enum("probeKind", "prChecks cmd", required: true),
            Text("label", required: true, max: int.MaxValue), Count("passedChecks"), Count("totalChecks"),
            Number("lastCheckedAtMs"), Number("startedAtMs"), Number("timeoutAtMs"), .. UnboundedText("detail")], strict: false)
    ];

    public static bool IsBuiltin(string use) => use.StartsWith("builtin:", StringComparison.Ordinal);

    public static void ValidateDeclaration(string use, string? view)
    {
        if (use is not ("builtin:flow" or "builtin:stepper" or "builtin:list" or "builtin:table" or "builtin:detail"))
            throw new InvalidOperationException($"Unknown builtin widget '{use}' referenced in report template.");
        if (string.IsNullOrWhiteSpace(view))
            throw new InvalidOperationException($"Widget '{use}' requires a non-empty view.");
    }

    public static void ValidateViewOutput(string use, string view, JsonElement output)
    {
        if (!IsBuiltin(use))
            return;
        var path = $"Template view '{view}' ({use})";
        switch (use)
        {
            case "builtin:stepper":
            case "builtin:list":
                if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty("items", out var items) &&
                    items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 0)
                    return;
                ValidateObject(output, path, Checkpoints, strict: true);
                break;
            case "builtin:detail":
                ValidateObject(output, path, [Objects("items", Item, int.MaxValue, required: true, strict: false)], strict: false);
                break;
            case "builtin:table":
                ValidateObject(output, path, [Objects("metrics", Metric, max: 12, required: true)], strict: false);
                break;
            case "builtin:flow":
                ValidateObject(output, path,
                    [Count("rev"), Text("filePath", max: int.MaxValue),
                     Objects("nodes", FlowActiveNode, int.MaxValue, required: true, strict: false),
                     Objects("archived", [.. FlowNode, Number("archivedAtMs", required: true)], int.MaxValue, strict: false)],
                    strict: false);
                break;
        }
    }

    private sealed record Field(string Name, bool Required, Action<JsonElement, string> Validate);

    private static void ValidateObject(JsonElement value, string path, Field[] fields, bool strict)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw Invalid(path, "must be an object");
        foreach (var field in fields)
        {
            var childPath = $"{path}.{field.Name}";
            if (value.TryGetProperty(field.Name, out var child) && child.ValueKind != JsonValueKind.Null)
                field.Validate(child, childPath);
            else if (field.Required)
                throw Invalid(childPath, "is required");
        }
        if (strict)
        {
            foreach (var property in value.EnumerateObject())
                if (!fields.Any(field => field.Name == property.Name))
                    throw Invalid($"{path}.{property.Name}", "is not supported");
        }
    }

    private static Field Text(string name, bool required = false, int min = 0, int max = 2048) =>
        new(name, required, (value, path) => ValidateText(value, path, min, max));

    private static Field[] UnboundedText(string names) => names.Split(' ').Select(name => Text(name, max: int.MaxValue)).ToArray();

    private static void ValidateText(JsonElement value, string path, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.String)
            throw Invalid(path, "must be a string");
        var text = value.GetString()!;
        var length = max < 2048 ? text.Length : text.EnumerateRunes().Count();
        if (length < min || length > max)
            throw Invalid(path, $"must contain {min} to {max} characters");
    }

    private static Field Enum(string name, string allowed, bool required = false)
    {
        var choices = allowed.Split(' ');
        return new(name, required, (value, path) =>
        {
            if (value.ValueKind != JsonValueKind.String || !choices.Contains(value.GetString(), StringComparer.Ordinal))
                throw Invalid(path, $"must be one of {string.Join(", ", choices)}");
        });
    }

    private static Field Number(string name, bool required = false) => new(name, required, (value, path) =>
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Invalid(path, "must be a finite number");
    });

    private static Field Count(string name) => new(name, false, (value, path) =>
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) || number < 0)
            throw Invalid(path, "must be a nonnegative integer");
    });

    private static Field Ignored(string name) => new(name, false, (_, _) => { });

    private static Field Boolean(string name) => new(name, false, (value, path) =>
    {
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid(path, "must be a boolean");
    });

    private static Field Object(string name, Field[] fields, bool strict = true) =>
        new(name, false, (value, path) => ValidateObject(value, path, fields, strict));

    private static Field Objects(string name, Field[] fields, int max, int min = 0, bool required = false, bool strict = true) =>
        new(name, required, (value, path) => ValidateArray(value, path, min, max, (item, child) => ValidateObject(item, child, fields, strict)));

    private static Field Strings(string name, int max, int min = 1) =>
        new(name, false, (value, path) => ValidateArray(value, path, 0, max, (item, child) => ValidateText(item, child, min, 2048)));

    private static void ValidateArray(JsonElement value, string path, int min, int max, Action<JsonElement, string> validate)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw Invalid(path, "must be an array");
        var count = value.GetArrayLength();
        if (count < min || count > max)
            throw Invalid(path, $"must contain {min} to {max} entries");
        var index = 0;
        foreach (var element in value.EnumerateArray())
            validate(element, $"{path}[{index++}]");
    }

    private static InvalidOperationException Invalid(string path, string message) => new($"{path} {message}.");
}
