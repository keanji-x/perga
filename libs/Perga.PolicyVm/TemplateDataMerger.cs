using System.Text.Json;
using System.Text.Json.Nodes;
using static Perga.PolicyVm.ResourceBudget;

namespace Perga.PolicyVm;

internal static partial class TemplateDataValidator
{
    public static (bool Ok, JsonElement Data, IReadOnlyList<DataValidationError> Errors) MergeAndStamp(
        JsonElement prevData,
        JsonElement patch,
        string nowIso,
        string source,
        bool stampUnchanged = false)
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
        var touchedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prop in patch.EnumerateObject())
        {
            budget.ConsumeNode("merge");
            budget.ConsumeChars(prop.Name.Length, "merge");
            if (ForbiddenKeys.Contains(prop.Name))
            {
                continue;
            }
            touchedKeys.Add(prop.Name);

            if (prop.Value.ValueKind == JsonValueKind.Object &&
                (prop.Value.TryGetProperty("$upsert", out _) || prop.Value.TryGetProperty("$delete", out _)))
            {
                JsonElement previous = default;
                if (prevData.ValueKind == JsonValueKind.Object) prevData.TryGetProperty(prop.Name, out previous);
                var (valid, records, error) = MergeKeyedArray(previous, prop.Value, ref budget);
                if (!valid) return (false, prevData, [new DataValidationError(prop.Name, error!)]);
                nextObj[prop.Name] = StampItemMeta(previous.ValueKind == JsonValueKind.Array ? previous : null,
                    records, nowIso, source, ref budget);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
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

        // Empty and idempotent writes preserve both metadata and the durable revision.
        if (!stampUnchanged)
        {
            var compareBudget = ResourceBudget.Create();
            if (!AnyTouchedKeyChanged(prevData, nextObj, touchedKeys, ref compareBudget))
                return (true, prevData.Clone(), []);
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

    private static bool AnyTouchedKeyChanged(
        JsonElement prevData, JsonObject nextObj, HashSet<string> touchedKeys, ref ResourceBudget budget)
    {
        foreach (var key in touchedKeys)
        {
            if (prevData.ValueKind != JsonValueKind.Object || !prevData.TryGetProperty(key, out var prevVal)) return true;
            var nextNode = nextObj[key];
            using var nextDoc = JsonDocument.Parse(nextNode?.ToJsonString() ?? "null");
            if (!string.Equals(
                    CanonicalJsonWithoutMeta(prevVal, ref budget),
                    CanonicalJsonWithoutMeta(nextDoc.RootElement, ref budget),
                    StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static (bool Valid, JsonElement Records, string? Error) MergeKeyedArray(
        JsonElement previous, JsonElement delta, ref ResourceBudget budget)
    {
        if (previous.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined))
            return (false, default, "Keyed array delta must target an array.");
        if (delta.EnumerateObject().Any(p => p.Name is not ("$upsert" or "$delete")))
            return (false, default, "Array delta accepts only $upsert and $delete.");
        var records = new List<JsonElement>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (previous.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in previous.EnumerateArray())
            {
                budget.ConsumeNode("keyed merge");
                if (record.ValueKind != JsonValueKind.Object || !TryGetItemKey(record, out var key) ||
                    string.IsNullOrWhiteSpace(key) || !indexes.TryAdd(key, records.Count))
                    return (false, default, "Existing array records must have unique non-empty id or code keys.");
                records.Add(record);
            }
        }
        var deletes = new HashSet<string>(StringComparer.Ordinal);
        if (delta.TryGetProperty("$delete", out var delete))
        {
            if (delete.ValueKind != JsonValueKind.Array)
                return (false, default, "$delete must be an array of string keys.");
            foreach (var key in delete.EnumerateArray())
            {
                budget.ConsumeNode("keyed merge");
                if (key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()))
                    return (false, default, "$delete requires non-empty string keys.");
                deletes.Add(key.GetString()!);
            }
        }
        var upserted = new HashSet<string>(StringComparer.Ordinal);
        if (delta.TryGetProperty("$upsert", out var upsert))
        {
            if (upsert.ValueKind != JsonValueKind.Array)
                return (false, default, "$upsert must be an array of records.");
            foreach (var record in upsert.EnumerateArray())
            {
                budget.ConsumeNode("keyed merge");
                if (record.ValueKind != JsonValueKind.Object || !TryGetItemKey(record, out var key) ||
                    string.IsNullOrWhiteSpace(key) || !upserted.Add(key))
                    return (false, default, "$upsert requires records with unique non-empty id or code keys.");
                // Deleting then re-inserting the same key starts a new record.
                if (indexes.TryGetValue(key, out var index) && !deletes.Contains(key))
                {
                    var merged = CopySafeObject(records[index], ref budget);
                    foreach (var field in record.EnumerateObject())
                        merged[field.Name] = ToSafeJsonNode(field.Value, ref budget);
                    records[index] = JsonSerializer.SerializeToElement(merged);
                }
                else
                {
                    records.Add(record);
                    deletes.Remove(key);
                    if (indexes.TryGetValue(key, out index)) records[index] = default;
                }
            }
        }
        var result = records.Where(r => r.ValueKind == JsonValueKind.Object &&
            TryGetItemKey(r, out var key) && !deletes.Contains(key)).ToArray();
        if (result.Length > MaxArrayLength)
            return (false, default, "Keyed array exceeds maximum length.");
        return (true, JsonSerializer.SerializeToElement(result), null);
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
        if (val.ValueKind == JsonValueKind.String) budget.ConsumeChars(val.GetString()!.Length, "merge");
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
            budget.ConsumeChars(prop.Name.Length, "merge");
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
