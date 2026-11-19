using System.Text.Json;

namespace Perga.AgentKit;

/// <summary>The session's advertised permission choices and their ACP transport.</summary>
internal sealed record AcpPermissionModes(IReadOnlyList<PermissionModeOption> Options, string? ConfigId)
{
    internal static AcpPermissionModes Read(JsonElement response)
    {
        if (response.TryGetProperty("configOptions", out var configs) && configs.ValueKind == JsonValueKind.Array)
        {
            foreach (var config in configs.EnumerateArray())
            {
                var id = Text(config, "id");
                if ((Text(config, "category") != "mode" && id != "mode") ||
                    Text(config, "type") != "select" || string.IsNullOrWhiteSpace(id) ||
                    !config.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
                    continue;
                return new(ParseOptions(options, Text(config, "currentValue"), configBacked: true), id);
            }
        }

        if (response.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Object &&
            modes.TryGetProperty("availableModes", out var available) && available.ValueKind == JsonValueKind.Array)
            return new(ParseOptions(available, Text(modes, "currentModeId"), configBacked: false), null);
        return new([], null);
    }

    private static IReadOnlyList<PermissionModeOption> ParseOptions(JsonElement options, string? current, bool configBacked)
    {
        var result = new List<PermissionModeOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonElement values)
        {
            foreach (var option in values.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object) continue;
                if (option.TryGetProperty("options", out var children) && children.ValueKind == JsonValueKind.Array)
                    Visit(children);
                var id = Text(option, configBacked ? "value" : "id");
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    result.Add(new(id, Text(option, "name") ?? id, Text(option, "description") ?? "", "neutral", id == current));
            }
        }
        Visit(options);
        return result;
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}
