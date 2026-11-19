using System.Text.Json;

namespace Perga.AgentKit.Internal;

internal sealed record AcpSelectValue(string Id, string Name);

internal sealed record AcpSessionOption(string Id, string Current, IReadOnlyList<AcpSelectValue> Values);

internal sealed record AcpSessionOptions(AcpSessionOption? Model, AcpSessionOption? Reasoning)
{
    public static AcpSessionOptions? Read(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("configOptions", out var options) ||
            options.ValueKind != JsonValueKind.Array)
            return null;

        AcpSessionOption? model = null;
        AcpSessionOption? reasoning = null;
        foreach (var entry in options.EnumerateArray())
        {
            var category = Text(entry, "category");
            if (category is not ("model" or "thought_level") || Text(entry, "type") != "select" ||
                Text(entry, "id") is not { Length: > 0 } id || Text(entry, "currentValue") is not { Length: > 0 } current ||
                !entry.TryGetProperty("options", out var values) || values.ValueKind != JsonValueKind.Array)
                continue;

            var choices = values.EnumerateArray().SelectMany(ValuesOf)
                .Select(value => (Id: Text(value, "value"), Name: Text(value, "name")))
                .Where(value => !string.IsNullOrWhiteSpace(value.Id)).DistinctBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => new AcpSelectValue(value.Id!, value.Name ?? value.Id!)).ToArray();
            if (!choices.Any(choice => choice.Id == current))
                continue;
            var option = new AcpSessionOption(id, current, choices);
            if (category == "model") model ??= option;
            else reasoning ??= option;
        }
        return new AcpSessionOptions(model, reasoning);
    }

    private static IEnumerable<JsonElement> ValuesOf(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("options", out var group) && group.ValueKind == JsonValueKind.Array)
            return group.EnumerateArray();
        return [value];
    }

    private static string? Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    public WorkerProviderConfiguration? ToProviderConfiguration(AcpSessionOptions? defaults)
    {
        if (Model is null && Reasoning is null) return null;
        var defaultModel = defaults?.Model?.Current;
        var defaultEffort = defaults?.Reasoning?.Current;
        return new WorkerProviderConfiguration(Model?.Values.Select(value => new WorkerModelOption(value.Id, value.Name, [])).ToArray() ?? [],
            Model?.Values.Any(value => value.Id == defaultModel) == true ? defaultModel : null,
            Reasoning?.Values.Any(value => value.Id == defaultEffort) == true ? defaultEffort : null,
            Reasoning?.Values.Select(value => value.Id).ToArray());
    }
}
