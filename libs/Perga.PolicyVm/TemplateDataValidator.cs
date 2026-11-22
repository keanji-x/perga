using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using static Perga.PolicyVm.ResourceBudget;

namespace Perga.PolicyVm;


/// <summary>Trusted host validation, independent of the template JavaScript realm.</summary>
internal static partial class TemplateDataValidator
{
    private const int MaxValidationErrors = 64;

    internal static readonly HashSet<string> ForbiddenKeys = new(StringComparer.Ordinal)
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

    public static List<DataValidationError> ValidateRoot(
        TemplateSchemaNode? rootSchema,
        JsonElement data,
        IReadOnlyList<string>? knownTaskKeys)
    {
        var errors = new List<DataValidationError>();
        var budget = ResourceBudget.Create();
        if (rootSchema is null)
        {
            CountNodesBounded(data, depth: 0, ref budget);
            return errors;
        }

        var taskSet = knownTaskKeys is not null
            ? new HashSet<string>(knownTaskKeys, StringComparer.Ordinal)
            : null;
        ValidateNode(rootSchema, data, exists: true, string.Empty, taskSet, errors, depth: 0, ref budget);
        return errors;
    }

    public static void ValidateDefault(TemplateSchemaNode schema, JsonElement value, string path, ref ResourceBudget budget)
    {
        var errors = new List<DataValidationError>();
        ScanPatchSafety(value, path, errors, depth: 0, ref budget);
        if (errors.Count == 0)
            ValidateNode(schema, value, exists: true, path, knownTaskSet: null, errors, depth: 0, ref budget, allowMissingDefaults: true);

        if (errors.Count > 0)
        {
            var error = errors[0];
            throw new InvalidOperationException($"Invalid template schema default at {error.Path}: {error.Message}.");
        }
    }

    public static JsonElement MaterializeDefaults(
        TemplateSchemaNode? schema, JsonElement data, string nowIso, string source, JsonElement? previous = null)
    {
        if (schema is null || !schema.ContainsDefaults || data.ValueKind != JsonValueKind.Object)
            return data;

        var budget = ResourceBudget.Create();
        var root = JsonNode.Parse(data.GetRawText());
        if (!FillDefaults(schema, root, depth: 0, isNew: false, nowIso, source, ref budget))
            return data;

        var serialized = root!.ToJsonString();
        if (serialized.Length > MaxTotalStringChars)
            throw new InvalidOperationException($"Merged template data exceeds maximum serialized size ({MaxTotalStringChars} chars).");

        using var doc = JsonDocument.Parse(serialized);
        if (previous is { } prior)
        {
            var compareBudget = ResourceBudget.Create();
            var priorValue = CanonicalJsonWithoutMeta(prior, ref compareBudget);
            compareBudget = ResourceBudget.Create();
            if (priorValue == CanonicalJsonWithoutMeta(doc.RootElement, ref compareBudget))
                return prior;
        }

        return doc.RootElement.Clone();
    }

    private static bool FillDefaults(
        TemplateSchemaNode schema, JsonNode? value, int depth, bool isNew, string nowIso, string source, ref ResourceBudget budget)
    {
        budget.ConsumeNode("default materialization");
        if (depth > MaxDepth)
            throw new InvalidOperationException("Template data nesting exceeds maximum depth.");

        var changed = isNew;
        if (value is JsonObject obj && schema.Shape is { } shape)
        {
            foreach (var (name, field) in shape)
            {
                if (obj.TryGetPropertyValue(name, out var child))
                {
                    changed |= FillDefaults(field, child, depth + 1, isNew, nowIso, source, ref budget);
                }
                else if (field.HasDefault)
                {
                    var copy = ToSafeJsonNode(field.DefaultValue!.Value, ref budget);
                    obj.Add(name, copy);
                    FillDefaults(field, copy, depth + 1, isNew: true, nowIso, source, ref budget);
                    changed = true;
                }
            }

            if (changed)
                obj["_meta"] = BuildMetaObject(nowIso, source);
        }
        else if (value is JsonArray array && schema.Item is { } item)
        {
            foreach (var child in array)
                changed |= FillDefaults(item, child, depth + 1, isNew, nowIso, source, ref budget);
        }

        return changed;
    }

    private static void CountNodesBounded(JsonElement val, int depth, ref ResourceBudget budget)
    {
        budget.ConsumeNode("validation");
        if (depth > MaxDepth)
            throw new InvalidOperationException("Template data nesting exceeds maximum depth.");

        switch (val.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in val.EnumerateObject())
                {
                    budget.ConsumeNode("validation");
                    budget.ConsumeChars(prop.Name.Length, "validation");
                    CountNodesBounded(prop.Value, depth + 1, ref budget);
                }
                break;
            case JsonValueKind.Array:
                if (val.GetArrayLength() > MaxArrayLength)
                    throw new InvalidOperationException($"Template data array length ({val.GetArrayLength()}) exceeds maximum allowed ({MaxArrayLength}).");
                foreach (var item in val.EnumerateArray())
                    CountNodesBounded(item, depth + 1, ref budget);
                break;
            case JsonValueKind.String:
                budget.ConsumeChars(val.GetString()!.Length, "validation");
                break;
        }
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

        if (val.ValueKind == JsonValueKind.String)
            budget.ConsumeChars(val.GetString()!.Length, "safety scan");

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
        ref ResourceBudget budget,
        bool allowMissingDefaults = false)
    {
        budget.ConsumeNode("validation");
        if (depth > MaxDepth)
        {
            errors.Add(new DataValidationError(path, "data nesting exceeds maximum depth"));
            return;
        }

        if (!exists || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (schema.IsOptional || !exists && allowMissingDefaults && schema.HasDefault)
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
                    ValidateNode(schema.Item!, item, exists: true, $"{path}[{i}]", knownTaskSet, errors, depth + 1, ref budget, allowMissingDefaults);
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
                        ValidateNode(kv.Value, fieldVal, exists: true, childPath, knownTaskSet, errors, depth + 1, ref budget, allowMissingDefaults);
                    }
                    else
                    {
                        ValidateNode(kv.Value, default, exists: false, childPath, knownTaskSet, errors, depth + 1, ref budget, allowMissingDefaults);
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

}
