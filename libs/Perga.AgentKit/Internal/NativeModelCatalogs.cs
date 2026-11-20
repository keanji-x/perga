using System.Text.Json;

namespace Perga.AgentKit.Internal;

internal static class NativeModelCatalogs
{
    internal delegate Task<IReadOnlyList<ProviderModelInfo>> Read(
        string directory, Func<IReadOnlyList<string>, TimeSpan, Task<JsonElement>> command,
        TimeSpan probeTimeout, TimeSpan handshakeTimeout);

    internal static Read? ForProvider(string provider) => provider.ToLowerInvariant() switch
    {
        "acp:opencode" => ReadOpenCodeAsync,
        "acp:antigravity" => ReadAntigravityAsync,
        _ => null
    };

    private static async Task<IReadOnlyList<ProviderModelInfo>> ReadOpenCodeAsync(
        string directory, Func<IReadOnlyList<string>, TimeSpan, Task<JsonElement>> command,
        TimeSpan probeTimeout, TimeSpan handshakeTimeout)
    {
        var location = $"location[directory]={directory}";
        var response = await Task.WhenAll(
            command(["api", "model.list", "--standalone", "--param", location], probeTimeout),
            command(["api", "model.default", "--standalone", "--param", location], probeTimeout)).ConfigureAwait(false);
        return ReadOpenCode(response[0], response[1]);
    }

    private static async Task<IReadOnlyList<ProviderModelInfo>> ReadAntigravityAsync(
        string directory, Func<IReadOnlyList<string>, TimeSpan, Task<JsonElement>> command,
        TimeSpan probeTimeout, TimeSpan handshakeTimeout)
    {
        var list = await command(["--output-format", "json", "models"], handshakeTimeout).ConfigureAwait(false);
        // Only issue the local query once the binary proves it supports the JSON catalog.
        if (!HasAntigravityModels(list))
            return [];
        var selection = await command(["--settings", """{"disableSlashCommands":false}""", "--disable-slash-commands=false",
            "--output-format", "json", "-p", "/model"], handshakeTimeout).ConfigureAwait(false);
        return ReadAntigravity(list, selection);
    }

    private static IReadOnlyList<ProviderModelInfo> ReadOpenCode(JsonElement list, JsonElement defaults)
    {
        var models = Property(list, "data");
        if (models.ValueKind != JsonValueKind.Array)
            return [];

        var defaultModel = Property(defaults, "data");
        var preferredId = OpenCodeModelId(defaultModel);
        var preferredVariant = String(defaultModel, "variant");
        var catalog = new List<ProviderModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            var id = OpenCodeModelId(model);
            if (id is null || Property(model, "enabled").ValueKind == JsonValueKind.False || !seen.Add(id))
                continue;

            var isDefault = id == preferredId;
            var efforts = ReadOpenCodeVariants(model);
            var defaultEffort = isDefault && efforts.Any(option => option.ReasoningEffort == preferredVariant)
                ? preferredVariant : null;
            catalog.Add(new(id, String(model, "name") ?? id, String(model, "description") ?? string.Empty,
                isDefault, defaultEffort, efforts));
        }
        return catalog;
    }

    // Antigravity lists each model and effort pair as its exact selectable ID.
    // /model gives the effective choice for a fresh invocation, even when it is a saved preference.
    private static bool HasAntigravityModels(JsonElement list) =>
        Property(CommandData(list, "models"), "models") is { ValueKind: JsonValueKind.Array } models &&
        models.GetArrayLength() > 0;

    private static IReadOnlyList<ProviderModelInfo> ReadAntigravity(JsonElement list, JsonElement selection)
    {
        var models = Property(CommandData(list, "models"), "models");
        if (models.ValueKind != JsonValueKind.Array)
            return [];

        var effectiveModel = CommandData(selection, "model");
        var preferredId = String(effectiveModel, "id");
        var catalog = new List<ProviderModelInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            var id = String(model, "id");
            if (id is null || !seen.Add(id))
                continue;

            var isDefault = id == preferredId;
            catalog.Add(new(id, String(model, "label") ?? id, string.Empty, isDefault,
                isDefault ? String(effectiveModel, "effort") : null, []));
        }
        return catalog;
    }

    private static ProviderModelEffort[] ReadOpenCodeVariants(JsonElement model)
    {
        var variants = Property(model, "variants");
        if (variants.ValueKind != JsonValueKind.Array)
            return [];
        return variants.EnumerateArray()
            .Select(variant => String(variant, "id"))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Select(variant => new ProviderModelEffort(variant, string.Empty))
            .ToArray();
    }

    private static string? OpenCodeModelId(JsonElement model) =>
        String(model, "providerID") is { } provider && String(model, "id") is { } id
            ? $"{provider}/{id}" : null;

    private static JsonElement CommandData(JsonElement result, string name)
    {
        var command = Property(result, "command");
        return String(result, "status") == "SUCCESS" && String(command, "name") == name
            ? Property(command, "data") : default;
    }

    private static JsonElement Property(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : default;

    private static string? String(JsonElement obj, string name) =>
        Property(obj, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text) ? text : null;
}
