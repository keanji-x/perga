using System.Text.Json;
using System.Text.Json.Nodes;
using Perga.Contracts;

namespace Perga.Kernel.Flow;

/// <summary>
/// Strict parser and canonical serializer for `flow.json` against `docs/schemas/flow.v1.schema.json`.
/// Emits Rust-style actionable diagnostics with exact JSON paths and concrete suggestions.
/// </summary>
public static class TrackFlowParser
{
    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "$schema", "rev", "nodes"
    };

    private static readonly HashSet<string> NodeProperties = new(StringComparer.Ordinal)
    {
        "id", "title", "source", "when", "do", "canceled"
    };

    private static readonly HashSet<string> WhenProperties = new(StringComparer.Ordinal)
    {
        "after", "on", "cron", "tz", "maxOccurrences", "at"
    };

    private static readonly HashSet<string> DoProperties = new(StringComparer.Ordinal)
    {
        "kind", "session", "role", "access", "provider", "model", "gate",
        "prompt", "acceptance", "cmd", "prChecks", "repo", "headSha", "interval", "timeout"
    };

    private static readonly HashSet<string> AllowedSources = new(StringComparer.Ordinal)
    {
        "agent", "user", "template"
    };

    private static readonly HashSet<string> AllowedOutcomeConditions = new(StringComparer.Ordinal)
    {
        "ok", "attention", "blocked", "any"
    };

    private static readonly HashSet<string> AllowedDoKinds = new(StringComparer.Ordinal)
    {
        "run", "wake", "watch"
    };

    private static readonly JsonSerializerOptions CanonicalWriteOptions = new()
    {
        WriteIndented = true
    };

    public static (FlowSpecDto? Spec, IReadOnlyList<FlowValidationErrorDto> Errors) Parse(string rawJson)
    {
        var errors = new List<FlowValidationErrorDto>();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            errors.Add(new FlowValidationErrorDto(
                "$",
                "flow.json is empty.",
                """Initialize flow.json with {"rev": 0, "nodes": []}."""));
            return (null, errors);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException ex)
        {
            errors.Add(new FlowValidationErrorDto(
                "$",
                $"Invalid JSON syntax in flow.json: {ex.Message}",
                "Fix the JSON syntax error (no trailing commas or comments) and retry flow_commit(dryRun: true)."));
            return (null, errors);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new FlowValidationErrorDto(
                    "$",
                    "flow.json root must be a JSON object.",
                    """Use {"rev": 0, "nodes": [...]}."""));
                return (null, errors);
            }

            foreach (var prop in root.EnumerateObject())
            {
                if (!RootProperties.Contains(prop.Name))
                {
                    errors.Add(new FlowValidationErrorDto(
                        $"$.{prop.Name}",
                        $"Unknown top-level field '{prop.Name}'.",
                        "Allowed top-level fields: $schema, rev, nodes."));
                }
            }

            string? schema = null;
            if (root.TryGetProperty("$schema", out var schemaEl))
            {
                if (schemaEl.ValueKind == JsonValueKind.String)
                {
                    schema = schemaEl.GetString();
                }
                else if (schemaEl.ValueKind != JsonValueKind.Null)
                {
                    errors.Add(new FlowValidationErrorDto("$.$schema", "'$schema' must be a string."));
                }
            }

            long rev = 0;
            if (!root.TryGetProperty("rev", out var revEl))
            {
                errors.Add(new FlowValidationErrorDto(
                    "$.rev",
                    "Missing required integer field 'rev'.",
                    "Include the current track flowRev (e.g. \"rev\": 0) without modifying it; flow_commit advances it."));
            }
            else if (revEl.ValueKind != JsonValueKind.Number || !revEl.TryGetInt64(out rev) || rev < 0)
            {
                errors.Add(new FlowValidationErrorDto(
                    "$.rev",
                    "'rev' must be a non-negative integer.",
                    "Set \"rev\" to the current track flowRev integer."));
            }

            var nodes = new List<FlowNodeSpecDto>();
            if (!root.TryGetProperty("nodes", out var nodesEl))
            {
                errors.Add(new FlowValidationErrorDto(
                    "$.nodes",
                    "Missing required array field 'nodes'.",
                    "Add \"nodes\": [] at the root of flow.json."));
            }
            else if (nodesEl.ValueKind != JsonValueKind.Array)
            {
                errors.Add(new FlowValidationErrorDto(
                    "$.nodes",
                    "'nodes' must be an array of flow nodes.",
                    "Replace 'nodes' with a JSON array: \"nodes\": [ ... ]."));
            }
            else
            {
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                var idx = 0;
                foreach (var nodeEl in nodesEl.EnumerateArray())
                {
                    var node = ParseNode(nodeEl, idx, seenIds, errors);
                    if (node is not null)
                    {
                        nodes.Add(node);
                    }

                    idx++;
                }
            }

            if (errors.Count > 0)
            {
                return (null, errors);
            }

            return (new FlowSpecDto(rev, nodes, schema), errors);
        }
    }

    public static FlowSpecDto ParsePersisted(string? flowSpecJson, long flowRev)
    {
        var (spec, errors) = Parse(flowSpecJson ?? FlowConstants.DefaultFlowSpecJson);
        if (spec is null || errors.Count > 0 || spec.Rev != flowRev)
            throw new InvalidOperationException("The stored Track Flow specification and revision are inconsistent.");
        return spec;
    }

    public static string SerializeCanonical(FlowSpecDto spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var root = new JsonObject
        {
            ["$schema"] = string.IsNullOrWhiteSpace(spec.Schema) ? FlowConstants.SchemaPointer : spec.Schema,
            ["rev"] = spec.Rev,
        };

        var nodesArr = new JsonArray();
        foreach (var node in spec.Nodes)
        {
            var nodeObj = new JsonObject
            {
                ["id"] = node.Id
            };
            if (!string.IsNullOrWhiteSpace(node.Title))
            {
                nodeObj["title"] = node.Title;
            }

            if (!string.IsNullOrWhiteSpace(node.Source) &&
                !string.Equals(node.Source, "agent", StringComparison.Ordinal))
            {
                nodeObj["source"] = node.Source;
            }

            if (node.When is { } when)
            {
                var whenObj = SerializeWhen(when);
                if (whenObj.Count > 0)
                {
                    nodeObj["when"] = whenObj;
                }
            }

            nodeObj["do"] = SerializeDo(node.Do);

            if (node.Canceled)
            {
                nodeObj["canceled"] = true;
            }

            nodesArr.Add(nodeObj);
        }

        root["nodes"] = nodesArr;
        return root.ToJsonString(CanonicalWriteOptions) + "\n";
    }

    private static JsonObject SerializeWhen(FlowWhenSpecDto when)
    {
        var obj = new JsonObject();
        if (when.After is { Count: > 0 } after)
        {
            var defaultOn = string.IsNullOrWhiteSpace(when.On) ? "ok" : when.On!;
            var afterArr = new JsonArray();
            foreach (var dep in after)
            {
                if (string.Equals(dep.On, defaultOn, StringComparison.Ordinal))
                {
                    afterArr.Add(dep.Node);
                }
                else
                {
                    afterArr.Add(new JsonObject
                    {
                        ["node"] = dep.Node,
                        ["on"] = dep.On
                    });
                }
            }

            obj["after"] = afterArr;
            if (!string.Equals(defaultOn, "ok", StringComparison.Ordinal))
            {
                obj["on"] = defaultOn;
            }
        }
        else if (!string.IsNullOrWhiteSpace(when.Cron))
        {
            obj["cron"] = when.Cron;
            if (!string.IsNullOrWhiteSpace(when.Tz))
            {
                obj["tz"] = when.Tz;
            }

            if (when.MaxOccurrences.HasValue)
            {
                obj["maxOccurrences"] = when.MaxOccurrences.Value;
            }
        }
        else if (!string.IsNullOrWhiteSpace(when.At))
        {
            obj["at"] = when.At;
        }

        return obj;
    }

    private static JsonObject SerializeDo(FlowDoSpecDto action)
    {
        var obj = new JsonObject
        {
            ["kind"] = action.Kind
        };
        if (!string.IsNullOrWhiteSpace(action.Session)) obj["session"] = action.Session;
        if (!string.IsNullOrWhiteSpace(action.Role)) obj["role"] = action.Role;
        if (!string.IsNullOrWhiteSpace(action.Access)) obj["access"] = action.Access;
        if (!string.IsNullOrWhiteSpace(action.Provider)) obj["provider"] = action.Provider;
        if (!string.IsNullOrWhiteSpace(action.Model)) obj["model"] = action.Model;
        if (!string.IsNullOrWhiteSpace(action.Gate)) obj["gate"] = action.Gate;
        if (!string.IsNullOrWhiteSpace(action.Prompt)) obj["prompt"] = action.Prompt;
        if (!string.IsNullOrWhiteSpace(action.Acceptance)) obj["acceptance"] = action.Acceptance;
        if (!string.IsNullOrWhiteSpace(action.Cmd)) obj["cmd"] = action.Cmd;
        if (action.PrChecks.HasValue) obj["prChecks"] = action.PrChecks.Value;
        if (!string.IsNullOrWhiteSpace(action.Repo)) obj["repo"] = action.Repo;
        if (!string.IsNullOrWhiteSpace(action.HeadSha)) obj["headSha"] = action.HeadSha;
        if (!string.IsNullOrWhiteSpace(action.Interval)) obj["interval"] = action.Interval;
        if (!string.IsNullOrWhiteSpace(action.Timeout)) obj["timeout"] = action.Timeout;
        return obj;
    }

    private static FlowNodeSpecDto? ParseNode(
        JsonElement nodeEl,
        int index,
        HashSet<string> seenIds,
        List<FlowValidationErrorDto> errors)
    {
        var path = $"$.nodes[{index}]";
        if (nodeEl.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new FlowValidationErrorDto(path, "Each node must be a JSON object."));
            return null;
        }

        foreach (var prop in nodeEl.EnumerateObject())
        {
            if (!NodeProperties.Contains(prop.Name))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.{prop.Name}",
                    $"Unknown field '{prop.Name}' on flow node.",
                    SuggestNodeFieldFix(prop.Name)));
            }
        }

        var id = ReadOptionalString(nodeEl, "id", $"{path}.id", errors);
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.id",
                "Node is missing required 'id' field.",
                "Add a lowercase slug id like \"id\": \"fix-r2\"."));
        }
        else if (!FlowConstants.NodeIdRegex().IsMatch(id))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.id",
                $"Node id '{id}' does not match pattern '^[a-z0-9][a-z0-9._:-]{{0,63}}$'.",
                "Use lowercase ASCII letters, digits, '.', '_', ':', or '-', starting with a letter or digit."));
        }
        else if (!seenIds.Add(id))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.id",
                $"Duplicate node id '{id}'.",
                "Give each node in flow.json a unique id."));
        }

        var title = ReadOptionalString(nodeEl, "title", $"{path}.title", errors);
        var source = ReadOptionalString(nodeEl, "source", $"{path}.source", errors) ?? "agent";
        if (!AllowedSources.Contains(source))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.source",
                $"Invalid source '{source}'.",
                "Use one of: \"agent\", \"user\", \"template\"."));
        }

        var canceled = false;
        if (nodeEl.TryGetProperty("canceled", out var canceledEl))
        {
            if (canceledEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                canceled = canceledEl.GetBoolean();
            }
            else
            {
                errors.Add(new FlowValidationErrorDto($"{path}.canceled", "'canceled' must be a boolean."));
            }
        }

        FlowWhenSpecDto? when = null;
        if (nodeEl.TryGetProperty("when", out var whenEl) && whenEl.ValueKind != JsonValueKind.Null)
        {
            when = ParseWhen(whenEl, $"{path}.when", errors);
        }

        FlowDoSpecDto? doSpec = null;
        if (!nodeEl.TryGetProperty("do", out var doEl) || doEl.ValueKind == JsonValueKind.Null)
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.do",
                "Node is missing required 'do' object.",
                """Add "do": { "kind": "run" | "wake" | "watch", ... }."""));
        }
        else
        {
            doSpec = ParseDo(doEl, $"{path}.do", errors);
        }

        if (string.IsNullOrWhiteSpace(id) || doSpec is null)
        {
            return null;
        }

        return new FlowNodeSpecDto(id, doSpec, title, source, when, canceled);
    }

    private static FlowWhenSpecDto? ParseWhen(JsonElement whenEl, string path, List<FlowValidationErrorDto> errors)
    {
        if (whenEl.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new FlowValidationErrorDto(path, "'when' must be a JSON object."));
            return null;
        }

        foreach (var prop in whenEl.EnumerateObject())
        {
            if (!WhenProperties.Contains(prop.Name))
            {
                var suggestion = prop.Name.Equals("dependsOn", StringComparison.OrdinalIgnoreCase) ||
                                 prop.Name.Equals("depends_on", StringComparison.OrdinalIgnoreCase)
                    ? """Use "when": { "after": [...] }."""
                    : "Allowed 'when' fields: after, on, cron, tz, maxOccurrences, at.";
                errors.Add(new FlowValidationErrorDto($"{path}.{prop.Name}", $"Unknown field '{prop.Name}' in 'when'.", suggestion));
            }
        }

        var hasAfter = whenEl.TryGetProperty("after", out var afterEl) && afterEl.ValueKind != JsonValueKind.Null;
        var hasCron = whenEl.TryGetProperty("cron", out var cronEl) && cronEl.ValueKind != JsonValueKind.Null;
        var hasAt = whenEl.TryGetProperty("at", out var atEl) && atEl.ValueKind != JsonValueKind.Null;

        var modeCount = (hasAfter ? 1 : 0) + (hasCron ? 1 : 0) + (hasAt ? 1 : 0);
        if (modeCount > 1)
        {
            errors.Add(new FlowValidationErrorDto(
                path,
                "'when' must specify at most one trigger mode: 'after', 'cron', or 'at'.",
                "Keep only one of 'after', 'cron', or 'at' on this node."));
        }
        else if (modeCount == 0 && whenEl.EnumerateObject().Any())
        {
            errors.Add(new FlowValidationErrorDto(
                path,
                "'when' object must declare one of 'after', 'cron', or 'at' (or omit 'when' to run immediately).",
                "Omit 'when' to run once immediately on flow_commit, or specify 'after', 'cron', or 'at'."));
        }

        var defaultOn = ReadOptionalString(whenEl, "on", $"{path}.on", errors);
        if (defaultOn is not null)
        {
            if (!AllowedOutcomeConditions.Contains(defaultOn))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.on",
                    $"Invalid outcome condition '{defaultOn}'.",
                    "Use one of: \"ok\", \"attention\", \"blocked\", \"any\"."));
            }

            if (!hasAfter)
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.on",
                    "'when.on' can only be used together with 'when.after'.",
                    "Remove 'on' or add 'after': [\"upstream-node\"]."));
            }
        }

        List<FlowAfterRefDto>? afterList = null;
        if (hasAfter)
        {
            if (afterEl.ValueKind != JsonValueKind.Array || afterEl.GetArrayLength() == 0)
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.after",
                    "'when.after' must be a non-empty array of node ids or { node, on } objects.",
                    """Example: "after": ["build"] or "after": [{"node": "wait-ci", "on": "attention"}]."""));
            }
            else
            {
                afterList = [];
                var effectiveDefaultOn = defaultOn ?? "ok";
                var aIdx = 0;
                foreach (var entry in afterEl.EnumerateArray())
                {
                    var entryPath = $"{path}.after[{aIdx}]";
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        var depId = entry.GetString() ?? string.Empty;
                        if (!FlowConstants.NodeIdRegex().IsMatch(depId))
                        {
                            errors.Add(new FlowValidationErrorDto(entryPath, $"Invalid upstream node id '{depId}'."));
                        }
                        else
                        {
                            afterList.Add(new FlowAfterRefDto(depId, effectiveDefaultOn));
                        }
                    }
                    else if (entry.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in entry.EnumerateObject())
                        {
                            if (p.Name is not ("node" or "on"))
                            {
                                errors.Add(new FlowValidationErrorDto(
                                    $"{entryPath}.{p.Name}",
                                    $"Unknown field '{p.Name}' in after entry.",
                                    """Allowed fields: "node", "on"."""));
                            }
                        }

                        var depId = ReadOptionalString(entry, "node", $"{entryPath}.node", errors);
                        var depOn = ReadOptionalString(entry, "on", $"{entryPath}.on", errors) ?? effectiveDefaultOn;
                        if (string.IsNullOrWhiteSpace(depId) || !FlowConstants.NodeIdRegex().IsMatch(depId))
                        {
                            errors.Add(new FlowValidationErrorDto($"{entryPath}.node", "after entry requires a valid 'node' id."));
                        }
                        else if (!AllowedOutcomeConditions.Contains(depOn))
                        {
                            errors.Add(new FlowValidationErrorDto(
                                $"{entryPath}.on",
                                $"Invalid outcome condition '{depOn}'.",
                                "Use one of: \"ok\", \"attention\", \"blocked\", \"any\"."));
                        }
                        else
                        {
                            afterList.Add(new FlowAfterRefDto(depId, depOn));
                        }
                    }
                    else
                    {
                        errors.Add(new FlowValidationErrorDto(
                            entryPath,
                            "Each 'when.after' entry must be a string node id or an object { node, on? }."));
                    }

                    aIdx++;
                }
            }
        }

        string? cron = null;
        if (hasCron)
        {
            if (cronEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(cronEl.GetString()) || cronEl.GetString()!.Trim().Length < 5)
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.cron",
                    "'when.cron' must be a non-empty 5-field cron expression.",
                    """Example: "cron": "0 9 * * *" """));
            }
            else
            {
                cron = cronEl.GetString()!.Trim();
            }
        }

        var tz = ReadOptionalString(whenEl, "tz", $"{path}.tz", errors);
        if (tz is not null && !hasCron)
        {
            errors.Add(new FlowValidationErrorDto($"{path}.tz", "'when.tz' is only valid with 'when.cron'."));
        }

        int? maxOccurrences = null;
        if (whenEl.TryGetProperty("maxOccurrences", out var maxEl) && maxEl.ValueKind != JsonValueKind.Null)
        {
            if (maxEl.ValueKind != JsonValueKind.Number || !maxEl.TryGetInt32(out var maxVal) || maxVal < 1)
            {
                errors.Add(new FlowValidationErrorDto($"{path}.maxOccurrences", "'when.maxOccurrences' must be an integer >= 1."));
            }
            else if (!hasCron)
            {
                errors.Add(new FlowValidationErrorDto($"{path}.maxOccurrences", "'when.maxOccurrences' is only valid with 'when.cron'."));
            }
            else
            {
                maxOccurrences = maxVal;
            }
        }

        string? at = null;
        if (hasAt)
        {
            if (atEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(atEl.GetString()))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.at",
                    "'when.at' must be a non-empty ISO-8601 timestamp string.",
                    """Example: "at": "2026-12-01T09:00:00Z"."""));
            }
            else
            {
                at = atEl.GetString()!.Trim();
            }
        }

        if (modeCount == 0)
        {
            return null;
        }

        return new FlowWhenSpecDto(afterList, defaultOn, cron, tz, maxOccurrences, at);
    }

    private static FlowDoSpecDto? ParseDo(JsonElement doEl, string path, List<FlowValidationErrorDto> errors)
    {
        if (doEl.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new FlowValidationErrorDto(path, "'do' must be a JSON object."));
            return null;
        }

        foreach (var prop in doEl.EnumerateObject())
        {
            if (!DoProperties.Contains(prop.Name))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.{prop.Name}",
                    $"Unknown field '{prop.Name}' in 'do'.",
                    "Allowed 'do' fields: kind, session, role, access, provider, model, gate, prompt, acceptance, cmd, prChecks, repo, headSha, interval, timeout."));
            }
        }

        var kind = ReadOptionalString(doEl, "kind", $"{path}.kind", errors);
        if (string.IsNullOrWhiteSpace(kind) || !AllowedDoKinds.Contains(kind))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.kind",
                $"Invalid or missing 'do.kind' ('{kind ?? ""}').",
                """Set "do": { "kind": "run" | "wake" | "watch", ... }."""));
            return null;
        }

        var session = ReadOptionalString(doEl, "session", $"{path}.session", errors);
        var role = ReadOptionalString(doEl, "role", $"{path}.role", errors);
        var access = ReadOptionalString(doEl, "access", $"{path}.access", errors);
        var provider = ReadOptionalString(doEl, "provider", $"{path}.provider", errors);
        var model = ReadOptionalString(doEl, "model", $"{path}.model", errors);
        var gate = ReadOptionalString(doEl, "gate", $"{path}.gate", errors);
        var prompt = ReadOptionalString(doEl, "prompt", $"{path}.prompt", errors);
        var acceptance = ReadOptionalString(doEl, "acceptance", $"{path}.acceptance", errors);
        var cmd = ReadOptionalString(doEl, "cmd", $"{path}.cmd", errors);
        var repo = ReadOptionalString(doEl, "repo", $"{path}.repo", errors);
        var headSha = ReadOptionalString(doEl, "headSha", $"{path}.headSha", errors);
        var interval = ReadOptionalString(doEl, "interval", $"{path}.interval", errors);
        var timeout = ReadOptionalString(doEl, "timeout", $"{path}.timeout", errors);

        int? prChecks = null;
        if (doEl.TryGetProperty("prChecks", out var prEl) && prEl.ValueKind != JsonValueKind.Null)
        {
            if (prEl.ValueKind != JsonValueKind.Number || !prEl.TryGetInt32(out var prNum) || prNum < 1)
            {
                errors.Add(new FlowValidationErrorDto($"{path}.prChecks", "'do.prChecks' must be a positive integer PR number."));
            }
            else
            {
                prChecks = prNum;
            }
        }

        if (access is not null and not ("read_only" or "read_write"))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.access",
                $"Invalid access mode '{access}'.",
                """Use "read_only" or "read_write"."""));
        }

        switch (kind)
        {
            case "run":
                ValidateRunDo(path, session, role, provider, model, prompt, cmd, prChecks, repo, headSha, interval, timeout, errors);
                break;
            case "wake":
                ValidateWakeDo(path, session, role, access, provider, model, gate, prompt, acceptance, cmd, prChecks, repo, headSha, interval, timeout, errors);
                break;
            case "watch":
                ValidateWatchDo(path, session, role, access, provider, model, gate, prompt, acceptance, cmd, prChecks, interval, timeout, errors);
                break;
        }

        return new FlowDoSpecDto(kind, session, role, access, provider, model, gate, prompt, acceptance, cmd, prChecks, repo, headSha, interval, timeout);
    }

    private static void ValidateRunDo(
        string path, string? session, string? role, string? provider, string? model, string? prompt, string? cmd,
        int? prChecks, string? repo, string? headSha, string? interval, string? timeout,
        List<FlowValidationErrorDto> errors)
    {
        if (prChecks.HasValue || repo is not null || headSha is not null || interval is not null || timeout is not null)
        {
            errors.Add(new FlowValidationErrorDto(
                path,
                "'prChecks', 'repo', 'headSha', 'interval', and 'timeout' are only valid for kind='watch'.",
                """Use "kind": "watch" for external probes or remove watch-only fields."""));
        }

        var hasCmd = !string.IsNullOrWhiteSpace(cmd);
        var hasPrompt = !string.IsNullOrWhiteSpace(prompt);
        if (hasCmd && hasPrompt)
        {
            errors.Add(new FlowValidationErrorDto(
                path,
                "kind='run' cannot specify both 'cmd' and 'prompt'.",
                "Use 'cmd' for a 0-token shell command OR 'prompt' for an agent session run."));
        }
        else if (!hasCmd && !hasPrompt)
        {
            errors.Add(new FlowValidationErrorDto(
                path,
                "kind='run' requires either 'prompt' (for a session worker) or 'cmd' (for a 0-token command).",
                """Add "prompt": "..." or "cmd": "..." inside "do"."""));
        }

        if (hasCmd && (session is not null || role is not null || provider is not null || model is not null))
        {
            errors.Add(new FlowValidationErrorDto(
                path,
                "0-token 'cmd' runs cannot set 'session', 'role', 'provider', or 'model'.",
                "Remove session/model fields from 'cmd' node or switch to a 'prompt' session run."));
        }

        if (session is not null && !string.Equals(session, "new", StringComparison.Ordinal) &&
            !session.StartsWith("ses_", StringComparison.Ordinal))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.session",
                $"Invalid run session target '{session}'.",
                """Omit 'session', use "new", or pass an existing track session id ('ses_...')."""));
        }
    }

    private static void ValidateWakeDo(
        string path, string? session, string? role, string? access, string? provider, string? model,
        string? gate, string? prompt, string? acceptance, string? cmd, int? prChecks,
        string? repo, string? headSha, string? interval, string? timeout,
        List<FlowValidationErrorDto> errors)
    {
        if (string.IsNullOrWhiteSpace(session) ||
            (!string.Equals(session, "lead", StringComparison.Ordinal) && !session.StartsWith("ses_", StringComparison.Ordinal)))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.session",
                "kind='wake' requires 'session' set to \"lead\" or an existing session id ('ses_...').",
                """Example: "session": "lead"."""));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            errors.Add(new FlowValidationErrorDto(
                $"{path}.prompt",
                "kind='wake' requires a non-empty 'prompt'.",
                """Add "prompt": "..." describing what the awakened session should do."""));
        }

        if (role is not null || access is not null || provider is not null || model is not null ||
            gate is not null || acceptance is not null || cmd is not null ||
            prChecks.HasValue || repo is not null || headSha is not null || interval is not null || timeout is not null)
        {
            errors.Add(new FlowValidationErrorDto(
                path,
                "kind='wake' only accepts 'kind', 'session', and 'prompt'.",
                "Use kind='run' if you need role/access/model/gate/cmd."));
        }
    }

    private static void ValidateWatchDo(
        string path, string? session, string? role, string? access, string? provider, string? model,
        string? gate, string? prompt, string? acceptance, string? cmd, int? prChecks,
        string? interval, string? timeout,
        List<FlowValidationErrorDto> errors)
    {
        if (session is not null || role is not null || access is not null || provider is not null ||
            model is not null || gate is not null || prompt is not null || acceptance is not null)
        {
            errors.Add(new FlowValidationErrorDto(
                path,
                "kind='watch' is a 0-token probe and cannot set session/prompt/role/access/model/gate fields.",
                "Use 'prChecks' or 'cmd' with optional 'interval' and 'timeout'."));
        }

        var hasPr = prChecks.HasValue;
        var hasCmd = !string.IsNullOrWhiteSpace(cmd);
        if (hasPr == hasCmd)
        {
            errors.Add(new FlowValidationErrorDto(
                path,
                "kind='watch' requires exactly one probe: 'prChecks' (PR number) OR 'cmd' (shell command).",
                """Example: "do": { "kind": "watch", "prChecks": 115, "interval": "60s", "timeout": "30m" }."""));
        }

        var intervalSpan = TimeSpan.FromSeconds(60);
        if (interval is not null)
        {
            if (!FlowConstants.TryParseDuration(interval, out intervalSpan))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.interval",
                    $"Invalid watch interval '{interval}'.",
                    "Use a duration matching '^[1-9][0-9]*[smh]$' (e.g. '30s', '60s', '2m')."));
            }
            else if (intervalSpan < TimeSpan.FromSeconds(FlowConstants.MinWatchIntervalSeconds))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.interval",
                    $"Watch interval '{interval}' is shorter than minimum {FlowConstants.MinWatchIntervalSeconds}s.",
                    "Set 'interval' to at least '5s' (e.g. '30s' or '60s')."));
            }
        }

        var timeoutSpan = TimeSpan.FromMinutes(60);
        if (timeout is not null)
        {
            if (!FlowConstants.TryParseDuration(timeout, out timeoutSpan))
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.timeout",
                    $"Invalid watch timeout '{timeout}'.",
                    "Use a duration matching '^[1-9][0-9]*[smh]$' (e.g. '10m', '30m', '2h')."));
            }
            else if (timeoutSpan < intervalSpan)
            {
                errors.Add(new FlowValidationErrorDto(
                    $"{path}.timeout",
                    $"Watch timeout '{timeout}' must be greater than or equal to interval '{interval ?? FlowConstants.DefaultWatchInterval}'.",
                    "Increase 'timeout' or decrease 'interval'."));
            }
        }
    }

    private static string? ReadOptionalString(JsonElement obj, string name, string path, List<FlowValidationErrorDto> errors)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.String)
        {
            errors.Add(new FlowValidationErrorDto(path, $"'{name}' must be a string."));
            return null;
        }

        return el.GetString();
    }

    private static string SuggestNodeFieldFix(string unknownField) => unknownField.ToLowerInvariant() switch
    {
        "dependson" or "depends_on" or "after" or "dependencies" =>
            """Use "when": { "after": [...] }.""",
        "cron" or "at" or "schedule" or "tz" or "timezone" =>
            """Use "when": { "cron": "..." } or "when": { "at": "..." }.""",
        "kind" or "cmd" or "command" or "prompt" or "role" or "access" or "model" or "provider" or "gate" or "session" or "prchecks" =>
            """Move execution fields inside "do": { "kind": "run" | "wake" | "watch", ... }.""",
        _ => "Allowed node fields: id, title, source, when, do, canceled."
    };
}
