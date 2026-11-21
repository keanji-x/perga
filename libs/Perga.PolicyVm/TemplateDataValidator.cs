using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Perga.PolicyVm;

internal sealed record TemplateSchemaNode(
    string Kind,
    bool IsOptional,
    IReadOnlyList<string>? EnumValues = null,
    string? RefTarget = null,
    TemplateSchemaNode? Item = null,
    IReadOnlyDictionary<string, TemplateSchemaNode>? Shape = null);

/// <summary>
/// Trusted host-side (C#) Fail-Closed validator, safety scanner, and `_meta` stamper.
/// Runs completely outside the untrusted Jint realm so template prototype/intrinsic mutation
/// can never weaken validation, and enforces independent host node, array, string, and time budgets.
/// </summary>
internal static class TemplateDataValidator
{
    private const int MaxDepth = 64;
    private const int MaxNodes = 10_000;
    private const int MaxArrayLength = 10_000;
    private const int MaxTotalStringChars = 1_048_576;
    private const int MaxValidationErrors = 64;
    private const int HostBudgetMs = 200;

    private static readonly HashSet<string> ForbiddenKeys = new(StringComparer.Ordinal)
    {
        "__proto__",
        "constructor",
        "prototype"
    };

    private static readonly Regex IdRegex = new(
        @"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IsoDateRegex = new(
        @"^(\d{4})-(\d{2})-(\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private struct ResourceBudget
    {
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

    /// <summary>
    /// Extracts a schema AST directly from Jint own properties without invoking JS prototypes or toJSON.
    /// </summary>
    public static TemplateSchemaNode ExtractSchemaNode(JsValue value)
    {
        var budget = ResourceBudget.Create();
        return ExtractSchemaNodeCore(value, depth: 0, ref budget);
    }

    /// <summary>
    /// Extracts a plain JSON value directly from Jint own enumerable properties without invoking JS prototypes or toJSON.
    /// </summary>
    public static JsonElement ExtractDataElement(JsValue value)
    {
        var budget = ResourceBudget.Create();
        var node = ExtractJsonNode(value, depth: 0, ref budget);
        if (node is null)
        {
            using var nullDoc = JsonDocument.Parse("null");
            return nullDoc.RootElement.Clone();
        }

        var raw = node.ToJsonString();
        if (raw.Length > MaxTotalStringChars)
        {
            throw new InvalidOperationException($"Template data exceeds maximum serialized size ({MaxTotalStringChars} chars).");
        }

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    public static (bool Ok, JsonElement Data, IReadOnlyList<DataValidationError> Errors) MergeAndStamp(
        JsonElement prevData,
        JsonElement patch,
        string nowIso,
        string source)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            return (false, prevData, [new DataValidationError("$", "Patch payload must be a JSON object.")]);
        }

        var budget = ResourceBudget.Create();
        var safetyErrors = new List<DataValidationError>();
        ScanPatchSafety(patch, string.Empty, safetyErrors, depth: 0, ref budget);
        if (safetyErrors.Count > 0)
        {
            return (false, prevData, safetyErrors);
        }

        var nextObj = CopySafeObject(prevData.ValueKind == JsonValueKind.Object ? prevData : default, ref budget);

        foreach (var prop in patch.EnumerateObject())
        {
            budget.ConsumeNode("merge");
            if (ForbiddenKeys.Contains(prop.Name))
            {
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                JsonElement? prevArr = prevData.ValueKind == JsonValueKind.Object &&
                                       prevData.TryGetProperty(prop.Name, out var existing) &&
                                       existing.ValueKind == JsonValueKind.Array
                    ? existing
                    : null;
                nextObj[prop.Name] = StampItemMeta(prevArr, prop.Value, nowIso, source, ref budget);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                JsonObject mergedNested;
                if (prevData.ValueKind == JsonValueKind.Object &&
                    prevData.TryGetProperty(prop.Name, out var existing) &&
                    existing.ValueKind == JsonValueKind.Object)
                {
                    mergedNested = CopySafeObject(existing, ref budget);
                }
                else
                {
                    mergedNested = new JsonObject();
                }

                foreach (var childProp in prop.Value.EnumerateObject())
                {
                    budget.ConsumeNode("merge");
                    if (ForbiddenKeys.Contains(childProp.Name))
                    {
                        continue;
                    }

                    mergedNested[childProp.Name] = ToSafeJsonNode(childProp.Value, ref budget);
                }

                mergedNested["_meta"] = BuildMetaObject(nowIso, source);
                nextObj[prop.Name] = mergedNested;
            }
            else
            {
                nextObj[prop.Name] = ToSafeJsonNode(prop.Value, ref budget);
            }
        }

        nextObj["_meta"] = BuildMetaObject(nowIso, source);

        var serialized = nextObj.ToJsonString();
        if (serialized.Length > MaxTotalStringChars)
        {
            throw new InvalidOperationException($"Merged template data exceeds maximum serialized size ({MaxTotalStringChars} chars).");
        }

        using var doc = JsonDocument.Parse(serialized);
        return (true, doc.RootElement.Clone(), []);
    }

    public static List<DataValidationError> ValidateRoot(
        TemplateSchemaNode? rootSchema,
        JsonElement data,
        IReadOnlyList<string>? knownTaskKeys)
    {
        var errors = new List<DataValidationError>();
        if (rootSchema is null)
        {
            return errors;
        }

        var budget = ResourceBudget.Create();
        var taskSet = knownTaskKeys is not null
            ? new HashSet<string>(knownTaskKeys, StringComparer.Ordinal)
            : null;
        ValidateNode(rootSchema, data, exists: true, string.Empty, taskSet, errors, depth: 0, ref budget);
        return errors;
    }

    private static TemplateSchemaNode ExtractSchemaNodeCore(JsValue value, int depth, ref ResourceBudget budget)
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

            return kind switch
            {
                "string" or "markdown" or "id" or "isoDate" or "number" or "boolean" =>
                    new TemplateSchemaNode(kind, isOptional),
                "enum" => ExtractEnumNode(obj, isOptional, ref budget),
                "ref" => ExtractRefNode(obj, isOptional),
                "array" => new TemplateSchemaNode(
                    "array",
                    isOptional,
                    Item: ExtractSchemaNodeCore(GetOwnDataValue(obj, "item"), depth + 1, ref budget)),
                "object" => ExtractObjectNode(GetOwnDataValue(obj, "shape"), isOptional, depth + 1, ref budget),
                _ => throw new InvalidOperationException($"Unsupported template schema kind '{kind}'.")
            };
        }

        // Root shorthand: `{ field: t.string(), ... }` -> wrap as object schema
        if (depth == 0)
        {
            return ExtractObjectNode(obj, isOptional: false, depth + 1, ref budget);
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

    private static TemplateSchemaNode ExtractObjectNode(JsValue shapeVal, bool isOptional, int depth, ref ResourceBudget budget)
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

            shape[key] = ExtractSchemaNodeCore(prop.Value.Value, depth, ref budget);
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

    private static JsonNode? ExtractJsonNode(JsValue value, int depth, ref ResourceBudget budget)
    {
        budget.ConsumeNode("data");
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("Template data nesting exceeds maximum depth.");
        }

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
                jsonArr.Add(ExtractJsonNode(elem, depth + 1, ref budget));
            }

            return jsonArr;
        }

        if (value is ObjectInstance obj)
        {
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
                if (childVal.IsUndefined())
                {
                    continue;
                }

                var key = prop.Key.AsString();
                budget.ConsumeChars(key.Length, "data");
                jsonObj[key] = ExtractJsonNode(childVal, depth + 1, ref budget);
            }

            return jsonObj;
        }

        throw new InvalidOperationException($"Unsupported JavaScript value type '{value.Type}' in template data.");
    }

    private static void ScanPatchSafety(JsonElement val, string path, List<DataValidationError> errors, int depth, ref ResourceBudget budget)
    {
        budget.ConsumeNode("safety scan");
        if (depth > MaxDepth)
        {
            errors.Add(new DataValidationError(string.IsNullOrEmpty(path) ? "$" : path, "data nesting exceeds maximum depth"));
            return;
        }

        if (val.ValueKind == JsonValueKind.Array)
        {
            if (val.GetArrayLength() > MaxArrayLength)
            {
                throw new InvalidOperationException($"Template data array length ({val.GetArrayLength()}) exceeds maximum allowed ({MaxArrayLength}).");
            }

            var i = 0;
            foreach (var elem in val.EnumerateArray())
            {
                ScanPatchSafety(elem, $"{path}[{i}]", errors, depth + 1, ref budget);
                i++;
            }

            return;
        }

        if (val.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var prop in val.EnumerateObject())
        {
            budget.ConsumeNode("safety scan");
            budget.ConsumeChars(prop.Name.Length, "safety scan");

            var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
            if (ForbiddenKeys.Contains(prop.Name))
            {
                AddBoundedError(errors, childPath, $"forbidden property name '{prop.Name}'");
                if (errors.Count >= MaxValidationErrors) return;
                continue;
            }

            if (string.Equals(prop.Name, "_meta", StringComparison.Ordinal))
            {
                AddBoundedError(errors, childPath, "system field '_meta' is managed by the kernel and cannot be modified");
                if (errors.Count >= MaxValidationErrors) return;
                continue;
            }

            ScanPatchSafety(prop.Value, childPath, errors, depth + 1, ref budget);
            if (errors.Count >= MaxValidationErrors) return;
        }
    }

    private static void AddBoundedError(List<DataValidationError> errors, string path, string message)
    {
        if (errors.Count < MaxValidationErrors)
        {
            errors.Add(new DataValidationError(path, message));
        }
    }

    private static void ValidateNode(
        TemplateSchemaNode schema,
        JsonElement value,
        bool exists,
        string path,
        HashSet<string>? knownTaskSet,
        List<DataValidationError> errors,
        int depth,
        ref ResourceBudget budget)
    {
        budget.ConsumeNode("validation");
        if (depth > MaxDepth)
        {
            errors.Add(new DataValidationError(path, "data nesting exceeds maximum depth"));
            return;
        }

        if (!exists || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (schema.IsOptional)
            {
                return;
            }

            errors.Add(new DataValidationError(path, "required value is missing"));
            return;
        }

        switch (schema.Kind)
        {
            case "string":
            case "markdown":
                if (value.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new DataValidationError(path, "expected string"));
                }

                return;

            case "id":
                if (value.ValueKind != JsonValueKind.String || !IdRegex.IsMatch(value.GetString() ?? string.Empty))
                {
                    errors.Add(new DataValidationError(path, "expected id matching /^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$/"));
                }

                return;

            case "isoDate":
                if (value.ValueKind != JsonValueKind.String || !IsValidCalendarDate(value.GetString()))
                {
                    errors.Add(new DataValidationError(path, "expected valid ISO date YYYY-MM-DD"));
                }

                return;

            case "number":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    errors.Add(new DataValidationError(path, "expected finite number"));
                }

                return;

            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add(new DataValidationError(path, "expected boolean"));
                }

                return;

            case "enum":
            {
                var enumValues = schema.EnumValues ?? [];
                if (value.ValueKind != JsonValueKind.String || !enumValues.Contains(value.GetString()!, StringComparer.Ordinal))
                {
                    errors.Add(new DataValidationError(
                        path,
                        $"expected one of [{string.Join(", ", enumValues)}], got {value.GetRawText()}"));
                }

                return;
            }

            case "ref":
            {
                var target = schema.RefTarget ?? string.Empty;
                if (value.ValueKind != JsonValueKind.String)
                {
                    errors.Add(new DataValidationError(path, $"expected non-empty ref(\"{target}\")"));
                    return;
                }

                var str = value.GetString() ?? string.Empty;
                var trimmed = str.Trim();
                if (trimmed.Length == 0)
                {
                    errors.Add(new DataValidationError(path, $"expected non-empty ref(\"{target}\")"));
                    return;
                }

                if (string.Equals(target, "file", StringComparison.Ordinal))
                {
                    if (!string.Equals(trimmed, str, StringComparison.Ordinal))
                    {
                        errors.Add(new DataValidationError(path, "file ref cannot have leading or trailing whitespace"));
                        return;
                    }

                    var normalized = trimmed.Replace('\\', '/');
                    if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                        normalized.Contains(':', StringComparison.Ordinal) ||
                        normalized.Contains('?', StringComparison.Ordinal) ||
                        normalized.Contains('#', StringComparison.Ordinal) ||
                        normalized.Contains('\0', StringComparison.Ordinal) ||
                        normalized.Split('/').Contains("..", StringComparer.Ordinal))
                    {
                        errors.Add(new DataValidationError(path, "file ref must stay relative inside track workspace"));
                    }
                }
                else if (string.Equals(target, "task", StringComparison.Ordinal) &&
                         knownTaskSet is not null &&
                         !knownTaskSet.Contains(str))
                {
                    errors.Add(new DataValidationError(path, $"task ref \"{str}\" does not match any task in this track"));
                }

                return;
            }

            case "array":
            {
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new DataValidationError(path, "expected array"));
                    return;
                }

                if (value.GetArrayLength() > MaxArrayLength)
                {
                    throw new InvalidOperationException($"Template data array length ({value.GetArrayLength()}) exceeds maximum allowed ({MaxArrayLength}).");
                }

                var i = 0;
                foreach (var item in value.EnumerateArray())
                {
                    ValidateNode(schema.Item!, item, exists: true, $"{path}[{i}]", knownTaskSet, errors, depth + 1, ref budget);
                    i++;
                }

                return;
            }

            case "object":
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new DataValidationError(path, "expected object"));
                    return;
                }

                var shape = schema.Shape ?? new Dictionary<string, TemplateSchemaNode>(StringComparer.Ordinal);
                foreach (var kv in shape)
                {
                    var childPath = string.IsNullOrEmpty(path) ? kv.Key : $"{path}.{kv.Key}";
                    if (value.TryGetProperty(kv.Key, out var fieldVal))
                    {
                        ValidateNode(kv.Value, fieldVal, exists: true, childPath, knownTaskSet, errors, depth + 1, ref budget);
                    }
                    else
                    {
                        ValidateNode(kv.Value, default, exists: false, childPath, knownTaskSet, errors, depth + 1, ref budget);
                    }
                }

                foreach (var prop in value.EnumerateObject())
                {
                    budget.ConsumeNode("validation");
                    budget.ConsumeChars(prop.Name.Length, "validation");
                    if (string.Equals(prop.Name, "_meta", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ForbiddenKeys.Contains(prop.Name) || !shape.ContainsKey(prop.Name))
                    {
                        var childPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                        AddBoundedError(errors, childPath, "unknown property (not declared in template schema)");
                        if (errors.Count >= MaxValidationErrors) return;
                    }
                }

                return;
            }
        }
    }

    private static bool IsValidCalendarDate(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var match = IsoDateRegex.Match(raw);
        if (!match.Success)
        {
            return false;
        }

        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (year < 1 || month < 1 || month > 12 || day < 1)
        {
            return false;
        }

        return day <= DateTime.DaysInMonth(year, month);
    }

    private static JsonArray StampItemMeta(JsonElement? prevArr, JsonElement nextArr, string nowIso, string source, ref ResourceBudget budget)
    {
        if (nextArr.GetArrayLength() > MaxArrayLength)
        {
            throw new InvalidOperationException($"Template data array length ({nextArr.GetArrayLength()}) exceeds maximum allowed ({MaxArrayLength}).");
        }

        var prevByKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (prevArr is { ValueKind: JsonValueKind.Array } prev)
        {
            foreach (var item in prev.EnumerateArray())
            {
                budget.ConsumeNode("merge");
                if (item.ValueKind == JsonValueKind.Object && TryGetItemKey(item, out var key))
                {
                    prevByKey[key] = item;
                }
            }
        }

        var result = new JsonArray();
        foreach (var item in nextArr.EnumerateArray())
        {
            budget.ConsumeNode("merge");
            if (item.ValueKind != JsonValueKind.Object)
            {
                result.Add(ToSafeJsonNode(item, ref budget));
                continue;
            }

            var copy = CopySafeObject(item, ref budget);
            JsonElement prevItem = default;
            var hasPrev = TryGetItemKey(item, out var key) && prevByKey.TryGetValue(key, out prevItem);
            var changed = !hasPrev ||
                          !string.Equals(
                              CanonicalJsonWithoutMeta(prevItem, ref budget),
                              CanonicalJsonWithoutMeta(item, ref budget),
                              StringComparison.Ordinal);

            if (!changed && prevItem.TryGetProperty("_meta", out var existingMeta) && existingMeta.ValueKind == JsonValueKind.Object)
            {
                copy["_meta"] = ToSafeJsonNode(existingMeta, ref budget);
            }
            else
            {
                copy["_meta"] = BuildMetaObject(nowIso, source);
            }

            result.Add(copy);
        }

        return result;
    }

    private static bool TryGetItemKey(JsonElement item, out string key)
    {
        if (item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            key = idEl.GetString()!;
            return true;
        }

        if (item.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
        {
            key = codeEl.GetString()!;
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static string CanonicalJsonWithoutMeta(JsonElement val, ref ResourceBudget budget)
    {
        var node = StripMetaCanonical(val, ref budget);
        return node?.ToJsonString() ?? "null";
    }

    private static JsonNode? StripMetaCanonical(JsonElement val, ref ResourceBudget budget)
    {
        budget.ConsumeNode("merge");
        return val.ValueKind switch
        {
            JsonValueKind.Object => BuildSortedObjectWithoutMeta(val, ref budget),
            JsonValueKind.Array => BuildArrayWithoutMeta(val, ref budget),
            JsonValueKind.String => JsonValue.Create(val.GetString()),
            JsonValueKind.Number => JsonNode.Parse(val.GetRawText()),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            _ => null
        };
    }

    private static JsonObject BuildSortedObjectWithoutMeta(JsonElement obj, ref ResourceBudget budget)
    {
        var result = new JsonObject();
        foreach (var prop in obj.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
        {
            budget.ConsumeNode("merge");
            budget.ConsumeChars(prop.Name.Length, "merge");
            if (string.Equals(prop.Name, "_meta", StringComparison.Ordinal) || ForbiddenKeys.Contains(prop.Name))
            {
                continue;
            }

            result[prop.Name] = StripMetaCanonical(prop.Value, ref budget);
        }

        return result;
    }

    private static JsonArray BuildArrayWithoutMeta(JsonElement arr, ref ResourceBudget budget)
    {
        var result = new JsonArray();
        foreach (var item in arr.EnumerateArray())
        {
            result.Add(StripMetaCanonical(item, ref budget));
        }

        return result;
    }

    private static JsonObject CopySafeObject(JsonElement obj, ref ResourceBudget budget)
    {
        var result = new JsonObject();
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            budget.ConsumeNode("merge");
            if (ForbiddenKeys.Contains(prop.Name))
            {
                continue;
            }

            result[prop.Name] = ToSafeJsonNode(prop.Value, ref budget);
        }

        return result;
    }

    private static JsonNode? ToSafeJsonNode(JsonElement val, ref ResourceBudget budget)
    {
        budget.ConsumeNode("merge");
        switch (val.ValueKind)
        {
            case JsonValueKind.Object:
                return CopySafeObject(val, ref budget);
            case JsonValueKind.Array:
                return CopySafeArray(val, ref budget);
            case JsonValueKind.String:
            {
                var str = val.GetString() ?? string.Empty;
                budget.ConsumeChars(str.Length, "merge");
                return JsonValue.Create(str);
            }
            case JsonValueKind.Number:
                return JsonNode.Parse(val.GetRawText());
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }

    private static JsonArray CopySafeArray(JsonElement arr, ref ResourceBudget budget)
    {
        if (arr.GetArrayLength() > MaxArrayLength)
        {
            throw new InvalidOperationException($"Template data array length ({arr.GetArrayLength()}) exceeds maximum allowed ({MaxArrayLength}).");
        }

        var result = new JsonArray();
        foreach (var item in arr.EnumerateArray())
        {
            result.Add(ToSafeJsonNode(item, ref budget));
        }

        return result;
    }

    private static JsonObject BuildMetaObject(string nowIso, string source) =>
        new()
        {
            ["updatedAt"] = nowIso,
            ["source"] = source
        };
}
