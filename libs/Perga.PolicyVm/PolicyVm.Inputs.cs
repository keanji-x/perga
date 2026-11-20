using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perga.PolicyVm;

public sealed partial class PolicyVm
{
    public void ValidateInputs(CompiledPolicy policy, JsonElement inputs)
    {
        if (inputs.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Template inputs must be an object.");
        foreach (var field in policy.Manifest.InputsSchema.EnumerateObject())
        {
            var schema = field.Value;
            if (!inputs.TryGetProperty(field.Name, out var value) && !schema.TryGetProperty("default", out value))
            {
                if (schema.TryGetProperty("optional", out var optional) && optional.ValueKind == JsonValueKind.True) continue;
                throw new InvalidOperationException($"Missing template input '{field.Name}'.");
            }
            var type = schema.GetProperty("type").GetString();
            var valid = type switch
            {
                "string" => value.ValueKind == JsonValueKind.String,
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "enum" => value.ValueKind == JsonValueKind.String && schema.TryGetProperty("values", out var values)
                    && values.ValueKind == JsonValueKind.Array && values.EnumerateArray().Any(v =>
                        v.ValueKind == JsonValueKind.String && v.GetString() == value.GetString()),
                _ => false
            };
            if (!valid) throw new InvalidOperationException($"Invalid template input '{field.Name}': expected {type}.");
        }
    }

    public string InterpolateInputs(CompiledPolicy policy, string text, JsonElement inputs) => Regex.Replace(text,
        @"\$\{inputs\.([A-Za-z0-9_]+)\}", match =>
        {
            var key = match.Groups[1].Value;
            if (inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(key, out var supplied)) return supplied.ValueKind == JsonValueKind.String ? supplied.GetString()! : supplied.GetRawText();
            if (policy.Manifest.InputsSchema.TryGetProperty(key, out var schema) && schema.TryGetProperty("default", out var fallback)) return fallback.ValueKind == JsonValueKind.String ? fallback.GetString()! : fallback.GetRawText();
            throw new InvalidOperationException($"Missing template input '{key}' for interpolation.");
        });
}
