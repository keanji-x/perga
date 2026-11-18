using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Perga.Contracts;
using Perga.ReportDoc;
using ZstdSharp;

namespace Perga.Storage.Sqlite;

/// <summary>
/// Read-time JSON <c>"v": 1</c> schema upcaster, enum mapper, and Zstd <see cref="ReportBlock"/> serializer.
/// </summary>
public sealed class SqliteJsonUpcaster
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions FlexibleJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true)
        }
    };

    private readonly ConcurrentDictionary<(string Domain, int FromVersion), Func<JsonObject, JsonObject>> _steps = new();

    public static SqliteJsonUpcaster Default { get; } = new();

    public void RegisterMigration(string domain, int fromVersion, Func<JsonObject, JsonObject> step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentOutOfRangeException.ThrowIfNegative(fromVersion);
        ArgumentNullException.ThrowIfNull(step);

        _steps[(domain, fromVersion)] = step;
    }

    public JsonElement UpcastObjectOrElement(string? rawJson, string domain, string fallbackJson = "{}")
    {
        var text = string.IsNullOrWhiteSpace(rawJson) ? fallbackJson : rawJson;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object || _steps.IsEmpty)
        {
            return root.Clone();
        }

        var version = 0;
        if (root.TryGetProperty("v", out var vProp) && vProp.ValueKind == JsonValueKind.Number && vProp.TryGetInt32(out var parsedV))
        {
            version = parsedV;
        }

        if (version >= CurrentSchemaVersion)
        {
            return root.Clone();
        }

        JsonObject? currentObj = null;
        var mutated = false;
        while (version < CurrentSchemaVersion)
        {
            if (_steps.TryGetValue((domain, version), out var step))
            {
                currentObj ??= JsonNode.Parse(text) as JsonObject ?? new JsonObject();
                currentObj = step(currentObj);
                currentObj["v"] = version + 1;
                mutated = true;
            }

            version++;
        }

        if (!mutated || currentObj is null)
        {
            return root.Clone();
        }

        using var outDoc = JsonDocument.Parse(currentObj.ToJsonString());
        return outDoc.RootElement.Clone();
    }

    public static JsonElement ParseElementOrDefault(string? rawJson, string fallbackJson = "{}")
    {
        var text = string.IsNullOrWhiteSpace(rawJson) ? fallbackJson : rawJson;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    public static JsonElement? ParseNullableElement(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(rawJson);
        return doc.RootElement.Clone();
    }

    public static string SerializeElement(JsonElement element, string fallbackJson = "{}") =>
        element.ValueKind == JsonValueKind.Undefined ? fallbackJson : element.GetRawText();

    public static string? SerializeNullableElement(JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Undefined } val ? val.GetRawText() : null;

    public static string SerializeStringList(IReadOnlyList<string>? items) =>
        JsonSerializer.Serialize(items ?? Array.Empty<string>(), FlexibleJsonOptions);

    public static IReadOnlyList<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        return JsonSerializer.Deserialize<List<string>>(json, FlexibleJsonOptions)
            ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    public static string SerializeReportBlocks(IReadOnlyList<ReportBlock>? blocks) =>
        JsonSerializer.Serialize(blocks ?? Array.Empty<ReportBlock>(), FlexibleJsonOptions);

    public static IReadOnlyList<ReportBlock> DeserializeReportBlocks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<ReportBlock>();
        }

        return JsonSerializer.Deserialize<List<ReportBlock>>(json, FlexibleJsonOptions)
            ?? (IReadOnlyList<ReportBlock>)Array.Empty<ReportBlock>();
    }

    public static byte[] CompressReportBlocksZstd(IReadOnlyList<ReportBlock> blocks)
    {
        var utf8Json = JsonSerializer.SerializeToUtf8Bytes(blocks ?? Array.Empty<ReportBlock>(), FlexibleJsonOptions);
        using var compressor = new Compressor();
        return compressor.Wrap(utf8Json).ToArray();
    }

    public static IReadOnlyList<ReportBlock> DecompressReportBlocksZstd(byte[]? compressed)
    {
        if (compressed is null || compressed.Length == 0)
        {
            return Array.Empty<ReportBlock>();
        }

        using var input = new MemoryStream(compressed, writable: false);
        using var zstd = new DecompressionStream(input);
        using var output = new MemoryStream();
        zstd.CopyTo(output);

        if (output.Length == 0)
        {
            return Array.Empty<ReportBlock>();
        }

        var span = new ReadOnlySpan<byte>(output.GetBuffer(), 0, (int)output.Length);
        return JsonSerializer.Deserialize<List<ReportBlock>>(span, FlexibleJsonOptions)
            ?? (IReadOnlyList<ReportBlock>)Array.Empty<ReportBlock>();
    }

    public static string? SerializeGateSpec(GateSpecDto? gate) =>
        gate is null ? null : JsonSerializer.Serialize(gate, FlexibleJsonOptions);

    public static GateSpecDto? DeserializeGateSpec(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<GateSpecDto>(json, FlexibleJsonOptions);

    public static string SerializeAskQuestions(IReadOnlyList<AskQuestionDto> questions) =>
        JsonSerializer.Serialize(questions ?? Array.Empty<AskQuestionDto>(), FlexibleJsonOptions);

    public static IReadOnlyList<AskQuestionDto> DeserializeAskQuestions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<AskQuestionDto>();
        }

        return JsonSerializer.Deserialize<List<AskQuestionDto>>(json, FlexibleJsonOptions)
            ?? (IReadOnlyList<AskQuestionDto>)Array.Empty<AskQuestionDto>();
    }

    public static string FormatAccessMode(AccessMode mode) => mode switch
    {
        AccessMode.ReadOnly => "read_only",
        AccessMode.ReadWrite => "read_write",
        _ => "read_write"
    };

    public static AccessMode ParseAccessMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "read_only" or "readonly" => AccessMode.ReadOnly,
        "read_write" or "readwrite" => AccessMode.ReadWrite,
        _ => AccessMode.ReadWrite
    };

    public static string FormatTaskStatus(TaskRunStatus status) => status switch
    {
        TaskRunStatus.Draft => "draft",
        TaskRunStatus.Pending => "pending",
        TaskRunStatus.Running => "running",
        TaskRunStatus.Verifying => "verifying",
        TaskRunStatus.Done => "done",
        TaskRunStatus.Failed => "failed",
        TaskRunStatus.Canceled => "canceled",
        _ => "pending"
    };

    public static TaskRunStatus ParseTaskStatus(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "draft" => TaskRunStatus.Draft,
        "pending" => TaskRunStatus.Pending,
        "running" => TaskRunStatus.Running,
        "verifying" => TaskRunStatus.Verifying,
        "done" => TaskRunStatus.Done,
        "failed" => TaskRunStatus.Failed,
        "canceled" or "cancelled" => TaskRunStatus.Canceled,
        _ => TaskRunStatus.Pending
    };

    public static string FormatSemanticVerdict(SemanticVerdict verdict) => verdict switch
    {
        SemanticVerdict.None => "none",
        SemanticVerdict.Accepted => "accepted",
        SemanticVerdict.Rejected => "rejected",
        _ => "none"
    };

    public static SemanticVerdict ParseSemanticVerdict(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "accepted" => SemanticVerdict.Accepted,
        "rejected" => SemanticVerdict.Rejected,
        _ => SemanticVerdict.None
    };

    public static string FormatAskStatus(AskStatus status) => status switch
    {
        AskStatus.Open => "open",
        AskStatus.Answered => "answered",
        AskStatus.Withdrawn => "withdrawn",
        _ => "open"
    };

    public static AskStatus ParseAskStatus(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "answered" => AskStatus.Answered,
        "withdrawn" => AskStatus.Withdrawn,
        _ => AskStatus.Open
    };

    public static string FormatTerminalStatus(TerminalStatus status) => status switch
    {
        TerminalStatus.Running => "running",
        TerminalStatus.Exited => "exited",
        _ => "running"
    };

    public static TerminalStatus ParseTerminalStatus(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "exited" => TerminalStatus.Exited,
        _ => TerminalStatus.Running
    };

    public static string TruncateUtf8Safe(string text, int maxUtf8Bytes)
    {
        if (string.IsNullOrEmpty(text) || maxUtf8Bytes <= 0)
        {
            return string.Empty;
        }

        if (Encoding.UTF8.GetByteCount(text) <= maxUtf8Bytes)
        {
            return text;
        }

        var charEnd = Math.Min(text.Length, maxUtf8Bytes);
        while (charEnd > 0)
        {
            if (char.IsHighSurrogate(text[charEnd - 1]))
            {
                charEnd--;
            }

            if (Encoding.UTF8.GetByteCount(text.AsSpan(0, charEnd)) <= maxUtf8Bytes)
            {
                return text[..charEnd];
            }

            charEnd = Math.Max(0, charEnd - Math.Max(1, (Encoding.UTF8.GetByteCount(text.AsSpan(0, charEnd)) - maxUtf8Bytes + 3) / 4));
        }

        return string.Empty;
    }
}
