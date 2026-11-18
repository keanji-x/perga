using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perga.AgentKit.Internal;

internal static partial class ModelListParsers
{
    public static (IReadOnlyList<string> Models, string? DefaultModel) ParseCodexModelsWithDefault(JsonElement result)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? defaultModel = null;

        JsonElement arrayElem = default;
        var hasArray = false;

        if (result.ValueKind == JsonValueKind.Array)
        {
            arrayElem = result;
            hasArray = true;
        }
        else if (result.ValueKind == JsonValueKind.Object)
        {
            foreach (var propName in new[] { "data", "models", "items" })
            {
                if (result.TryGetProperty(propName, out var candidate) &&
                    candidate.ValueKind == JsonValueKind.Array)
                {
                    arrayElem = candidate;
                    hasArray = true;
                    break;
                }
            }
        }

        if (!hasArray)
        {
            return (list, null);
        }

        foreach (var item in arrayElem.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var raw = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(raw) && seen.Add(raw))
                {
                    list.Add(raw);
                }

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var modelId = TryGetString(item, "id")
                          ?? TryGetString(item, "model")
                          ?? TryGetString(item, "modelId")
                          ?? TryGetString(item, "slug")
                          ?? TryGetString(item, "value")
                          ?? TryGetString(item, "name");
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            modelId = modelId.Trim();
            if (seen.Add(modelId))
            {
                list.Add(modelId);
            }

            if (defaultModel is null &&
                (TryGetBool(item, "isDefault") == true || TryGetBool(item, "default") == true))
            {
                defaultModel = modelId;
            }
        }

        return (list, defaultModel);
    }

    public static IReadOnlyList<string> ParseCodexModels(JsonElement result) =>
        ParseCodexModelsWithDefault(result).Models;

    public static string? SelectCodexFallbackModel(
        JsonElement? modelListResult,
        string? rejectedModel,
        IReadOnlyList<string> staticFallbacks)
    {
        bool IsAcceptable(string? candidate) =>
            !string.IsNullOrWhiteSpace(candidate) &&
            (string.IsNullOrWhiteSpace(rejectedModel) ||
             !string.Equals(candidate.Trim(), rejectedModel.Trim(), StringComparison.OrdinalIgnoreCase));

        if (modelListResult is { } elem)
        {
            var (models, defaultModel) = ParseCodexModelsWithDefault(elem);
            if (IsAcceptable(defaultModel))
            {
                return defaultModel!.Trim();
            }

            foreach (var m in models)
            {
                if (IsAcceptable(m))
                {
                    return m;
                }
            }
        }

        foreach (var fallback in staticFallbacks)
        {
            if (IsAcceptable(fallback))
            {
                return fallback;
            }
        }

        return null;
    }

    public static string? ExtractRejectedModelName(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            return null;
        }

        var match = QuotedModelBeforeKeywordRegex().Match(errorMessage);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        var afterMatch = QuotedModelAfterKeywordRegex().Match(errorMessage);
        if (afterMatch.Success)
        {
            return afterMatch.Groups[1].Value;
        }

        return null;
    }

    public static IReadOnlyList<string> ParseAcpModels(JsonElement result)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (result.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        // 1. models.availableModels[].modelId (and models.currentModelId)
        if (result.TryGetProperty("models", out var modelsProp))
        {
            if (modelsProp.ValueKind == JsonValueKind.Object)
            {
                if (modelsProp.TryGetProperty("availableModels", out var availArr) &&
                    availArr.ValueKind == JsonValueKind.Array)
                {
                    AppendModelItems(availArr, list, seen);
                }

                var currentModelId = TryGetString(modelsProp, "currentModelId");
                if (!string.IsNullOrWhiteSpace(currentModelId) && seen.Add(currentModelId.Trim()))
                {
                    list.Insert(0, currentModelId.Trim());
                }
            }
            else if (modelsProp.ValueKind == JsonValueKind.Array)
            {
                AppendModelItems(modelsProp, list, seen);
            }
        }

        if (result.TryGetProperty("availableModels", out var topAvailArr) &&
            topAvailArr.ValueKind == JsonValueKind.Array)
        {
            AppendModelItems(topAvailArr, list, seen);
        }

        // 2. configOptions[id="model" or category="model"].options[].value (flat or grouped)
        if (result.TryGetProperty("configOptions", out var configOpts) &&
            configOpts.ValueKind == JsonValueKind.Array)
        {
            foreach (var optObj in configOpts.EnumerateArray())
            {
                if (optObj.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var optId = TryGetString(optObj, "id");
                var category = TryGetString(optObj, "category");
                if (!string.Equals(optId, "model", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(category, "model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (optObj.TryGetProperty("options", out var optionsArr) &&
                    optionsArr.ValueKind == JsonValueKind.Array)
                {
                    AppendConfigSelectOptions(optionsArr, list, seen);
                }

                var currentVal = TryGetString(optObj, "currentValue");
                if (!string.IsNullOrWhiteSpace(currentVal) && seen.Add(currentVal.Trim()))
                {
                    list.Insert(0, currentVal.Trim());
                }
            }
        }

        return list;
    }

    private static void AppendModelItems(JsonElement arrayElem, List<string> list, HashSet<string> seen)
    {
        foreach (var item in arrayElem.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(s) && seen.Add(s))
                {
                    list.Add(s);
                }

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = TryGetString(item, "modelId")
                     ?? TryGetString(item, "id")
                     ?? TryGetString(item, "value")
                     ?? TryGetString(item, "model");
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id.Trim()))
            {
                list.Add(id.Trim());
            }
        }
    }

    private static void AppendConfigSelectOptions(JsonElement optionsArr, List<string> list, HashSet<string> seen)
    {
        foreach (var entry in optionsArr.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var val = TryGetString(entry, "value") ?? TryGetString(entry, "modelId") ?? TryGetString(entry, "id");
            if (!string.IsNullOrWhiteSpace(val) && seen.Add(val.Trim()))
            {
                list.Add(val.Trim());
            }

            if (entry.TryGetProperty("options", out var nestedOpts) &&
                nestedOpts.ValueKind == JsonValueKind.Array)
            {
                AppendConfigSelectOptions(nestedOpts, list, seen);
            }
        }
    }

    private static string? TryGetString(JsonElement obj, string propName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propName, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool? TryGetBool(JsonElement obj, string propName) =>
        obj.ValueKind == JsonValueKind.Object &&
        obj.TryGetProperty(propName, out var prop) &&
        prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : null;

    [GeneratedRegex(@"['""`]([A-Za-z0-9._:-]+)['""`]\s+model\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedModelBeforeKeywordRegex();

    [GeneratedRegex(@"\bmodel\s+['""`]([A-Za-z0-9._:-]+)['""`]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuotedModelAfterKeywordRegex();
}
