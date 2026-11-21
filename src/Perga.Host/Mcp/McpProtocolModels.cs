using System.Text.Json;
using System.Text.Json.Serialization;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.ReportDoc;

namespace Perga.Host.Mcp;

public sealed record McpToolWireDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    [property: JsonPropertyName("annotations"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpToolAnnotations? Annotations = null);

public sealed record McpContentBlock(
    string Type,
    string Text);

public sealed record McpCallToolResult(
    IReadOnlyList<McpContentBlock> Content,
    [property: JsonPropertyName("isError")] bool IsError = false);

public interface IMcpToolBackend
{
    string ServerName { get; }
    Task<IReadOnlyList<McpToolWireDescriptor>> ListToolsAsync(McpSessionPrincipal caller, CancellationToken ct);
    Task<McpCallToolResult> CallToolAsync(McpSessionPrincipal caller, string toolName, JsonElement arguments, CancellationToken ct);
}

public static class McpToolResultFormatter
{
    public static readonly JsonElement EmptyObject = ParseClone("{}");

    public static JsonElement ParseClone(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static McpCallToolResult Success<T>(T payload)
    {
        string text;
        if (payload is string str)
        {
            text = str;
        }
        else if (payload is JsonElement elem)
        {
            text = elem.ValueKind == JsonValueKind.String
                ? elem.GetString() ?? string.Empty
                : elem.GetRawText();
        }
        else
        {
            text = JsonSerializer.Serialize(payload, PergaJsonDefaults.ApiOptions);
        }

        return new McpCallToolResult([new McpContentBlock("text", text)], IsError: false);
    }

    public static McpCallToolResult Error(string message) =>
        new([new McpContentBlock("text", message)], IsError: true);

    public static McpCallToolResult FromException(Exception ex)
    {
        return ex switch
        {
            ReportConflictException conflict => Error(
                JsonSerializer.Serialize(
                    new
                    {
                        code = conflict.Code,
                        error = conflict.Message,
                        nextStep = conflict.NextStep
                    },
                    PergaJsonDefaults.ApiOptions)),

            KernelException { Code: KernelErrorCode.SystemUpgradeLeadRequired } denied => Error(
                JsonSerializer.Serialize(new { code = "system_upgrade_lead_required", error = denied.Message }, PergaJsonDefaults.ApiOptions)),

            KernelException { Code: KernelErrorCode.WorkerTurnChanged } stale => Error(
                JsonSerializer.Serialize(new { code = "worker_turn_changed", error = stale.Message }, PergaJsonDefaults.ApiOptions)),

            KernelException kex => string.IsNullOrWhiteSpace(kex.NextStep)
                ? Error(kex.Message)
                : Error($"{kex.Message} (nextStep: {kex.NextStep})"),

            _ => Error(ex.Message)
        };
    }

    public static bool TryGetPropertyIgnoreCase(JsonElement obj, ReadOnlySpan<string> names, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (obj.TryGetProperty(name, out value))
                {
                    return true;
                }
            }

            foreach (var prop in obj.EnumerateObject())
            {
                foreach (var name in names)
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

    public static string RequireString(JsonElement obj, ReadOnlySpan<string> names)
    {
        var val = OptionalString(obj, names);
        if (string.IsNullOrWhiteSpace(val))
        {
            throw KernelException.InvalidArgument($"Missing required argument '{names[0]}'.");
        }

        return val;
    }

    public static string? OptionalString(JsonElement obj, ReadOnlySpan<string> names)
    {
        if (TryGetPropertyIgnoreCase(obj, names, out var elem) && elem.ValueKind == JsonValueKind.String)
        {
            return elem.GetString();
        }

        return null;
    }

    public static int RequireInt(JsonElement obj, ReadOnlySpan<string> names)
    {
        if (TryGetPropertyIgnoreCase(obj, names, out var elem))
        {
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out var num))
            {
                return num;
            }

            if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        throw KernelException.InvalidArgument($"Missing or invalid integer argument '{names[0]}'.");
    }
}
