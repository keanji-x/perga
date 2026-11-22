using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perga.Kernel.Internal;

internal static class JsonHelpers
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) }
    };

    public static readonly JsonElement EmptyObject = ParseClone("{}");
    public static readonly JsonElement EmptyArray = ParseClone("[]");

    public static JsonElement ParseClone(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static JsonElement ToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, SerializerOptions).Clone();
    }

    public static T? Deserialize<T>(JsonElement element)
    {
        return element.Deserialize<T>(SerializerOptions);
    }

    public static JsonElement EnsureClonedObject(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return EmptyObject;
        }

        return element.Value.Clone();
    }

    public static bool JsonEquals(JsonElement? left, JsonElement? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return JsonElement.DeepEquals(left.Value, right.Value);
    }

    /// <summary>
    /// Extracts the last non-empty valid JSON line from command stdout and merges it into <paramref name="existingMeta"/>.
    /// </summary>
    public static JsonElement MergeLastValidJsonLine(JsonElement existingMeta, string? stdout)
    {
        var baseDict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (existingMeta.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in existingMeta.EnumerateObject())
            {
                baseDict[prop.Name] = prop.Value.Clone();
            }
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return baseDict.Count == 0 ? EmptyObject : ToElement(baseDict);
        }

        var lines = stdout.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject())
                    {
                        baseDict[prop.Name] = prop.Value.Clone();
                    }

                    return ToElement(baseDict);
                }

                if (root.ValueKind is JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                {
                    baseDict["result"] = root.Clone();
                    return ToElement(baseDict);
                }
            }
            catch (JsonException)
            {
                // Not valid JSON on this line; keep scanning earlier non-empty lines.
            }
        }

        return baseDict.Count == 0 ? EmptyObject : ToElement(baseDict);
    }

    public static bool TryGetPropertyIgnoreCase(JsonElement obj, ReadOnlySpan<string> candidateNames, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in candidateNames)
            {
                if (obj.TryGetProperty(name, out value))
                {
                    return true;
                }
            }

            foreach (var prop in obj.EnumerateObject())
            {
                foreach (var name in candidateNames)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
        }

        value = default;
        return false;
    }
}
