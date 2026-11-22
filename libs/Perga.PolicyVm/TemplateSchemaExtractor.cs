using Jint;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint.Native;
using Jint.Native.Object;
using Jint.Native.Function;
using static Perga.PolicyVm.ResourceBudget;
using static Perga.PolicyVm.TemplateDataValidator;

namespace Perga.PolicyVm;

internal sealed record TemplateSchemaNode(
    string Kind,
    bool IsOptional,
    IReadOnlyList<string>? EnumValues = null,
    string? RefTarget = null,
    TemplateSchemaNode? Item = null,
    IReadOnlyDictionary<string, TemplateSchemaNode>? Shape = null,
    bool HasDefault = false,
    JsonElement? DefaultValue = null)
{
    public bool ContainsDefaults => HasDefault || Item?.ContainsDefaults == true ||
        Shape?.Values.Any(child => child.ContainsDefaults) == true;
}

internal struct ResourceBudget
    {
    internal const int MaxDepth = 64;
    internal const int MaxNodes = 10_000;
    internal const int MaxArrayLength = 10_000;
    internal const int MaxTotalStringChars = 1_048_576;
    internal const int HostBudgetMs = 200;

        public int NodesRemaining;
        public int CharsRemaining;
        public long DeadlineTicks;

        public static ResourceBudget Create(int maxMs = HostBudgetMs) => new()
        {
            NodesRemaining = MaxNodes,
            CharsRemaining = MaxTotalStringChars,
            DeadlineTicks = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (maxMs / 1000.0))
        };

        public void ConsumeNode(string phase)
        {
            if (--NodesRemaining < 0)
            {
                throw new InvalidOperationException($"Template {phase} exceeds maximum node budget ({MaxNodes}).");
            }

            if (Stopwatch.GetTimestamp() > DeadlineTicks)
            {
                throw new InvalidOperationException($"Template {phase} exceeded host time budget ({HostBudgetMs}ms).");
            }
        }

        public void ConsumeChars(int count, string phase)
        {
            CharsRemaining -= count;
            if (CharsRemaining < 0)
            {
                throw new InvalidOperationException($"Template {phase} exceeds maximum string size budget ({MaxTotalStringChars} chars).");
            }
        }
    }


internal static class TemplateSchemaExtractor
{
    /// <summary>
    /// Extracts a schema AST directly from Jint own properties without invoking JS prototypes or toJSON.
    /// </summary>
    public static TemplateSchemaNode ExtractSchemaNode(JsValue value)
    {
        var budget = ResourceBudget.Create();
        return ExtractSchemaNodeCore(value, "$", depth: 0, ref budget);
    }

    /// <summary>
    /// Extracts a plain JSON value directly from Jint own enumerable properties without invoking JS prototypes or toJSON.
    /// </summary>
    public static JsonElement ExtractDataElement(JsValue value)
    {
        var budget = ResourceBudget.Create();
        return SerializeDataNode(ExtractJsonNode(value, depth: 0, ref budget));
    }

    private static JsonElement SerializeDataNode(JsonNode? node)
    {
        var raw = node?.ToJsonString() ?? "null";
        if (raw.Length > MaxTotalStringChars)
        {
            throw new InvalidOperationException($"Template data exceeds maximum serialized size ({MaxTotalStringChars} chars).");
        }

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static TemplateSchemaNode ExtractSchemaNodeCore(JsValue value, string path, int depth, ref ResourceBudget budget)
    {
        budget.ConsumeNode("schema");
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("Template schema nesting exceeds maximum depth.");
        }

        if (value is not ObjectInstance obj || value.IsArray())
        {
            throw new InvalidOperationException("Template schema node must be a plain object created by `t.*`.");
        }

        var marker = GetOwnDataValue(obj, "__pergaSchema");
        if (marker.IsBoolean() && marker.AsBoolean())
        {
            var kindVal = GetOwnDataValue(obj, "kind");
            if (!kindVal.IsString())
            {
                throw new InvalidOperationException("Template schema node is missing a valid string 'kind'.");
            }

            var kind = kindVal.AsString();
            var optVal = GetOwnDataValue(obj, "isOptional");
            var isOptional = optVal.IsBoolean() && optVal.AsBoolean();

            var node = kind switch
            {
                "string" or "markdown" or "id" or "isoDate" or "number" or "boolean" =>
                    new TemplateSchemaNode(kind, isOptional),
                "enum" => ExtractEnumNode(obj, isOptional, ref budget),
                "ref" => ExtractRefNode(obj, isOptional),
                "array" => new TemplateSchemaNode(
                    "array",
                    isOptional,
                    Item: ExtractSchemaNodeCore(GetOwnDataValue(obj, "item"), $"{path}[]", depth + 1, ref budget)),
                "object" => ExtractObjectNode(GetOwnDataValue(obj, "shape"), isOptional, path, depth + 1, ref budget),
                _ => throw new InvalidOperationException($"Unsupported template schema kind '{kind}'.")
            };

            var hasDefault = GetOwnDataValue(obj, "hasDefault");
            if (!hasDefault.IsBoolean() || !hasDefault.AsBoolean())
                return node;

            JsonElement defaultValue;
            try
            {
                var raw = GetOwnDataValue(obj, "defaultValue");
                defaultValue = SerializeDataNode(ExtractJsonNode(raw, depth, ref budget, strictJson: true));
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Invalid template schema default at {path}: {ex.Message}", ex);
            }

            ValidateDefault(node, defaultValue, path, ref budget);
            return node with { HasDefault = true, DefaultValue = defaultValue };
        }

        // Root shorthand: `{ field: t.string(), ... }` -> wrap as object schema
        if (depth == 0)
        {
            return ExtractObjectNode(obj, isOptional: false, path, depth + 1, ref budget);
        }

        throw new InvalidOperationException("Nested template schema field must be a schema node created by `t.*`.");
    }

    private static TemplateSchemaNode ExtractEnumNode(ObjectInstance obj, bool isOptional, ref ResourceBudget budget)
    {
        var valuesVal = GetOwnDataValue(obj, "values");
        if (!valuesVal.IsArray())
        {
            throw new InvalidOperationException("t.enum(values) schema node must contain a non-empty string array.");
        }

        var arr = valuesVal.AsArray();
        if (arr.Length == 0 || arr.Length > MaxArrayLength)
        {
            throw new InvalidOperationException($"t.enum(values) must contain between 1 and {MaxArrayLength} strings.");
        }

        var list = new List<string>((int)arr.Length);
        for (uint i = 0; i < arr.Length; i++)
        {
            budget.ConsumeNode("schema");
            var item = GetOwnDataValue(arr, i.ToString(CultureInfo.InvariantCulture));
            if (!item.IsString())
            {
                throw new InvalidOperationException("t.enum(values) entries must be strings.");
            }

            var text = item.AsString();
            budget.ConsumeChars(text.Length, "schema");
            list.Add(text);
        }

        return new TemplateSchemaNode("enum", isOptional, EnumValues: list);
    }

    private static TemplateSchemaNode ExtractRefNode(ObjectInstance obj, bool isOptional)
    {
        var targetVal = GetOwnDataValue(obj, "target");
        if (!targetVal.IsString())
        {
            throw new InvalidOperationException("t.ref(target) schema node requires a string target.");
        }

        var target = targetVal.AsString();
        if (target is not ("task" or "file"))
        {
            throw new InvalidOperationException("t.ref(target) only supports \"task\" or \"file\".");
        }

        return new TemplateSchemaNode("ref", isOptional, RefTarget: target);
    }

    private static TemplateSchemaNode ExtractObjectNode(JsValue shapeVal, bool isOptional, string path, int depth, ref ResourceBudget budget)
    {
        if (shapeVal is not ObjectInstance shapeObj || shapeVal.IsArray())
        {
            throw new InvalidOperationException("t.object(shape) requires a plain object mapping field names to schemas.");
        }

        var shape = new Dictionary<string, TemplateSchemaNode>(StringComparer.Ordinal);
        foreach (var prop in shapeObj.GetOwnProperties())
        {
            budget.ConsumeNode("schema");
            if (!prop.Value.Enumerable || !prop.Value.IsDataDescriptor())
            {
                continue;
            }

            var key = prop.Key.AsString();
            budget.ConsumeChars(key.Length, "schema");
            if (ForbiddenKeys.Contains(key))
            {
                throw new InvalidOperationException($"Template schema declares forbidden property name '{key}'.");
            }

            var field = ExtractSchemaNodeCore(prop.Value.Value, $"{path}.{key}", depth, ref budget);
            if (key == "_meta" && field.ContainsDefaults)
                throw new InvalidOperationException($"Invalid template schema default at {path}.{key}: '_meta' is managed by the kernel.");

            shape[key] = field;
        }

        return new TemplateSchemaNode("object", isOptional, Shape: shape);
    }

    private static JsValue GetOwnDataValue(ObjectInstance obj, string key)
    {
        var desc = obj.GetOwnProperty(key);
        if (desc == Jint.Runtime.Descriptors.PropertyDescriptor.Undefined || !desc.IsDataDescriptor())
        {
            return JsValue.Undefined;
        }

        return desc.Value;
    }

    private static JsonNode? ExtractJsonNode(JsValue value, int depth, ref ResourceBudget budget, bool strictJson = false)
    {
        budget.ConsumeNode("data");
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("Template data nesting exceeds maximum depth.");
        }

        if (strictJson && (value.IsUndefined() || value is Function || value.IsNumber() && !double.IsFinite(value.AsNumber())))
            throw new InvalidOperationException("Default must be a JSON value.");

        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        if (value.IsBoolean())
        {
            return JsonValue.Create(value.AsBoolean());
        }

        if (value.IsNumber())
        {
            var number = value.AsNumber();
            if (!double.IsFinite(number))
            {
                return null;
            }

            return JsonValue.Create(number);
        }

        if (value.IsString())
        {
            var text = value.AsString();
            budget.ConsumeChars(text.Length, "data");
            return JsonValue.Create(text);
        }

        if (value.IsArray())
        {
            var arr = value.AsArray();
            if (arr.Length > MaxArrayLength)
            {
                throw new InvalidOperationException($"Template data array length ({arr.Length}) exceeds maximum allowed ({MaxArrayLength}).");
            }

            var jsonArr = new JsonArray();
            for (uint i = 0; i < arr.Length; i++)
            {
                var elem = GetOwnDataValue(arr, i.ToString(CultureInfo.InvariantCulture));
                jsonArr.Add(ExtractJsonNode(elem, depth + 1, ref budget, strictJson));
            }

            return jsonArr;
        }

        if (value is ObjectInstance obj)
        {
            if (strictJson && obj.GetType() != typeof(JsObject))
            {
                throw new InvalidOperationException("Default must be a plain JSON object.");
            }

            var jsonObj = new JsonObject();
            foreach (var prop in obj.GetOwnProperties())
            {
                if (!prop.Value.Enumerable)
                {
                    continue;
                }

                if (!prop.Value.IsDataDescriptor())
                {
                    throw new InvalidOperationException($"Template data property '{prop.Key.AsString()}' must be a plain data value.");
                }

                var childVal = prop.Value.Value;
                if (!strictJson && childVal.IsUndefined())
                {
                    continue;
                }

                var key = prop.Key.AsString();
                budget.ConsumeChars(key.Length, "data");
                jsonObj[key] = ExtractJsonNode(childVal, depth + 1, ref budget, strictJson);
            }

            return jsonObj;
        }

        throw new InvalidOperationException($"Unsupported JavaScript value type '{value.Type}' in template data.");
    }

}
