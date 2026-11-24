using System.Text.Json;
using System.Text.Json.Nodes;
using Perga.Contracts;

namespace Perga.Kernel.Flow;

/// <summary>
/// Canonical JSON serializer for `flow.json` matching `docs/schemas/flow.v1.schema.json`.
/// </summary>
public static class TrackFlowSerializer
{
    private static readonly JsonSerializerOptions CanonicalWriteOptions = new()
    {
        WriteIndented = true
    };

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
        if (!string.IsNullOrWhiteSpace(action.ReasoningEffort)) obj["reasoningEffort"] = action.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(action.PermissionMode)) obj["permissionMode"] = action.PermissionMode;
        if (!string.IsNullOrWhiteSpace(action.Gate)) obj["gate"] = action.Gate;
        if (!string.IsNullOrWhiteSpace(action.Prompt)) obj["prompt"] = action.Prompt;
        if (!string.IsNullOrWhiteSpace(action.Acceptance)) obj["acceptance"] = action.Acceptance;
        if (!string.IsNullOrWhiteSpace(action.Cmd)) obj["cmd"] = action.Cmd;
        if (action.PrChecks.HasValue) obj["prChecks"] = action.PrChecks.Value;
        if (!string.IsNullOrWhiteSpace(action.Repo)) obj["repo"] = action.Repo;
        if (!string.IsNullOrWhiteSpace(action.HeadSha)) obj["headSha"] = action.HeadSha;
        if (!string.IsNullOrWhiteSpace(action.Interval)) obj["interval"] = action.Interval;
        if (!string.IsNullOrWhiteSpace(action.Timeout)) obj["timeout"] = action.Timeout;
        if (action.Repeat == true) obj["repeat"] = true;
        if (!string.IsNullOrWhiteSpace(action.Title)) obj["title"] = action.Title;
        if (!string.IsNullOrWhiteSpace(action.Area)) obj["area"] = action.Area;
        if (!string.IsNullOrWhiteSpace(action.Template)) obj["template"] = action.Template;
        if (action.TemplateInputs is { } inputs) obj["templateInputs"] = JsonNode.Parse(inputs.GetRawText());
        if (!string.IsNullOrWhiteSpace(action.Cwd)) obj["cwd"] = action.Cwd;
        if (!string.IsNullOrWhiteSpace(action.WorkspaceDriver)) obj["workspaceDriver"] = action.WorkspaceDriver;
        if (!string.IsNullOrWhiteSpace(action.ExecutionTargetId)) obj["executionTargetId"] = action.ExecutionTargetId;
        return obj;
    }
}
