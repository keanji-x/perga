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

        var safetyBudget = ResourceBudget.Create();
        var safetyErrors = new List<DataValidationError>();
        ScanPatchSafety(patch, string.Empty, safetyErrors, depth: 0, ref safetyBudget);
        if (safetyErrors.Count > 0)
        {
            return (false, prevData, safetyErrors);
        }

        var budget = ResourceBudget.Create();

        var nextObj = new JsonObject();
        if (prevData.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in prevData.EnumerateObject())
            {
                if (ForbiddenKeys.Contains(prop.Name)) continue;
                nextObj[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
            }
        }
        var touchedKeys = new HashSet<string>(StringComparer.Ordinal);
        var mutated = false;

        foreach (var prop in patch.EnumerateObject())
        {
            budget.ConsumeNode("merge");
            budget.ConsumeChars(prop.Name.Length, "merge");
            if (ForbiddenKeys.Contains(prop.Name))
            {
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.Object &&
                (prop.Value.TryGetProperty("$upsert", out _) || prop.Value.TryGetProperty("$delete", out _)))
            {
                JsonElement previous = default;
                if (prevData.ValueKind == JsonValueKind.Object) prevData.TryGetProperty(prop.Name, out previous);
                var (valid, records, changed, error) = MergeKeyedArray(previous, prop.Value, nowIso, source, ref budget);
                if (!valid) return (false, prevData, [new DataValidationError(prop.Name, error!)]);
                if (changed)
                {
                    nextObj[prop.Name] = records;
                    mutated = true;
                }
            }
            else if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                touchedKeys.Add(prop.Name);
                JsonElement? prevArr = prevData.ValueKind == JsonValueKind.Object &&
                                       prevData.TryGetProperty(prop.Name, out var existing) &&
                                       existing.ValueKind == JsonValueKind.Array
                    ? existing
                    : null;
                nextObj[prop.Name] = StampItemMeta(prevArr, prop.Value, nowIso, source, ref budget);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                touchedKeys.Add(prop.Name);
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
                touchedKeys.Add(prop.Name);
                nextObj[prop.Name] = ToSafeJsonNode(prop.Value, ref budget);
            }
        }

        // Empty and idempotent writes preserve both metadata and the durable revision.
        if (!stampUnchanged && !mutated)
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

    private static (bool Valid, JsonArray? Records, bool Changed, string? Error) MergeKeyedArray(
        JsonElement previous, JsonElement delta, string nowIso, string source, ref ResourceBudget budget)
    {
        if (previous.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined))
            return (false, null, false, "Keyed array delta must target an array.");
        if (delta.EnumerateObject().Any(p => p.Name is not ("$upsert" or "$delete")))
            return (false, null, false, "Array delta accepts only $upsert and $delete.");

        var entries = new List<(string Key, JsonElement? PrevRaw, JsonObject? Updated)>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        if (previous.ValueKind == JsonValueKind.Array)
        {
            foreach (var record in previous.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object || !TryGetItemKey(record, out var key) ||
                    string.IsNullOrWhiteSpace(key) || !indexes.TryAdd(key, entries.Count))
                    return (false, null, false, "Existing array records must have unique non-empty id or code keys.");
                entries.Add((key, record, null));
            }
        }
        var prevKeys = entries.Select(e => e.Key).ToList();

        var deletes = new HashSet<string>(StringComparer.Ordinal);
        if (delta.TryGetProperty("$delete", out var delete))
        {
            if (delete.ValueKind != JsonValueKind.Array)
                return (false, null, false, "$delete must be an array of string keys.");
            foreach (var key in delete.EnumerateArray())
            {
                budget.ConsumeNode("keyed merge");
                if (key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()))
                    return (false, null, false, "$delete requires non-empty string keys.");
                deletes.Add(key.GetString()!);
            }
        }

        var changed = false;
        var upserted = new HashSet<string>(StringComparer.Ordinal);
        if (delta.TryGetProperty("$upsert", out var upsert))
        {
            if (upsert.ValueKind != JsonValueKind.Array)
                return (false, null, false, "$upsert must be an array of records.");
            foreach (var record in upsert.EnumerateArray())
            {
                budget.ConsumeNode("keyed merge");
                if (record.ValueKind != JsonValueKind.Object || !TryGetItemKey(record, out var key) ||
                    string.IsNullOrWhiteSpace(key) || !upserted.Add(key))
                    return (false, null, false, "$upsert requires records with unique non-empty id or code keys.");

                // Deleting then re-inserting the same key starts a new record (preserving _meta if identical).
                if (indexes.TryGetValue(key, out var index) && !deletes.Contains(key))
                {
                    var prevRecord = entries[index].PrevRaw!.Value;
                    var merged = CopySafeObject(prevRecord, ref budget);
                    foreach (var field in record.EnumerateObject())
                        merged[field.Name] = ToSafeJsonNode(field.Value, ref budget);
                    using var mergedDoc = JsonDocument.Parse(merged.ToJsonString());
                    if (!string.Equals(
                            CanonicalJsonWithoutMeta(prevRecord, ref budget),
                            CanonicalJsonWithoutMeta(mergedDoc.RootElement, ref budget),
                            StringComparison.Ordinal))
                    {
                        merged["_meta"] = BuildMetaObject(nowIso, source);
                        entries[index] = (key, prevRecord, merged);
                        changed = true;
                    }
                }
                else
                {
                    deletes.Remove(key);
                    if (indexes.TryGetValue(key, out index))
                    {
                        var prevRecord = entries[index].PrevRaw!.Value;
                        entries[index] = (string.Empty, null, null);
                        if (string.Equals(
                                CanonicalJsonWithoutMeta(prevRecord, ref budget),
                                CanonicalJsonWithoutMeta(record, ref budget),
                                StringComparison.Ordinal))
                        {
                            entries.Add((key, prevRecord, null));
                            continue;
                        }
                    }

                    var fresh = CopySafeObject(record, ref budget);
                    fresh["_meta"] = BuildMetaObject(nowIso, source);
                    entries.Add((key, null, fresh));
                    changed = true;
                }
            }
        }

        var surviving = new List<(string Key, JsonElement? PrevRaw, JsonObject? Updated)>(entries.Count);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Key) || deletes.Contains(entry.Key)) continue;
            surviving.Add(entry);
        }

        var structureChanged = previous.ValueKind == JsonValueKind.Undefined || surviving.Count != prevKeys.Count;
        if (!structureChanged)
        {
            for (var i = 0; i < surviving.Count; i++)
            {
                if (!string.Equals(surviving[i].Key, prevKeys[i], StringComparison.Ordinal))
                {
                    structureChanged = true;
                    break;
                }
            }
        }

        if (!changed && !structureChanged)
            return (true, null, false, null);

        if (surviving.Count > MaxArrayLength)
            return (false, null, false, "Keyed array exceeds maximum length.");

        var result = new JsonArray();
        foreach (var (_, prevRaw, updated) in surviving)
        {
            result.Add(updated ?? JsonNode.Parse(prevRaw!.Value.GetRawText()));
        }
        return (true, result, true, null);
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
