using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Perga.Contracts;
using Perga.Kernel.Events;

namespace Perga.Host.Serialization;

public static class PergaJsonDefaults
{
    public static readonly JsonSerializerOptions ApiOptions = CreateApiOptions();
    public static readonly JsonSerializerOptions McpOptions = CreateMcpOptions();

    public static void ConfigureSerializerOptions(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;

        for (var i = options.Converters.Count - 1; i >= 0; i--)
        {
            if (options.Converters[i] is JsonStringEnumConverter or FlexibleSnakeCaseEnumConverterFactory)
            {
                options.Converters.RemoveAt(i);
            }
        }

        options.Converters.Add(new FlexibleSnakeCaseEnumConverterFactory());
    }

    private static JsonSerializerOptions CreateApiOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ConfigureSerializerOptions(options);
        return options;
    }

    private static JsonSerializerOptions CreateMcpOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new FlexibleSnakeCaseEnumConverterFactory());
        return options;
    }

    /// <summary>
    /// Ensures nested <c>EventEnvelopeDto.Payload</c> enum values (such as <c>TaskDto.access</c>)
    /// are emitted strictly in <c>snake_case</c> over <c>WS /api/v1/events</c> to match REST 1:1.
    /// </summary>
    public static EventEnvelopeDto NormalizeEventEnvelope(EventEnvelopeDto envelope)
    {
        if (!string.Equals(envelope.Kind, KernelEventKinds.TaskUpdated, StringComparison.Ordinal) ||
            envelope.Payload.ValueKind != JsonValueKind.Object)
        {
            return envelope;
        }

        var node = JsonNode.Parse(envelope.Payload.GetRawText());
        if (node is not JsonObject rootObj)
        {
            return envelope;
        }

        var mutated = false;
        if (rootObj["task"] is JsonObject singleTask)
        {
            mutated |= NormalizeTaskNodeEnums(singleTask);
        }

        if (rootObj["tasks"] is JsonArray tasksArray)
        {
            foreach (var item in tasksArray)
            {
                if (item is JsonObject taskObj)
                {
                    mutated |= NormalizeTaskNodeEnums(taskObj);
                }
            }
        }

        if (!mutated)
        {
            return envelope;
        }

        using var doc = JsonDocument.Parse(rootObj.ToJsonString());
        return envelope with { Payload = doc.RootElement.Clone() };
    }

    private static bool NormalizeTaskNodeEnums(JsonObject taskObj)
    {
        if (taskObj["access"] is JsonValue accessVal && accessVal.TryGetValue<string>(out var rawAccess))
        {
            var normalized = rawAccess switch
            {
                "readOnly" or "ReadOnly" or "READ_ONLY" => "read_only",
                "readWrite" or "ReadWrite" or "READ_WRITE" => "read_write",
                _ => rawAccess
            };

            if (!string.Equals(rawAccess, normalized, StringComparison.Ordinal))
            {
                taskObj["access"] = normalized;
                return true;
            }
        }

        return false;
    }
}

public sealed class FlexibleSnakeCaseEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(FlexibleSnakeCaseEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class FlexibleSnakeCaseEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private readonly Dictionary<TEnum, string> _toWire;
        private readonly Dictionary<string, TEnum> _fromWireExactOrInsensitive;
        private readonly Dictionary<string, TEnum> _fromNormalized;

        public FlexibleSnakeCaseEnumConverter()
        {
            _toWire = new Dictionary<TEnum, string>();
            _fromWireExactOrInsensitive = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);
            _fromNormalized = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);

            foreach (var val in Enum.GetValues<TEnum>())
            {
                var rawName = val.ToString();
                var snake = JsonNamingPolicy.SnakeCaseLower.ConvertName(rawName);
                _toWire[val] = snake;
                _fromWireExactOrInsensitive[snake] = val;
                _fromWireExactOrInsensitive[rawName] = val;
                _fromNormalized[StripSeparators(rawName)] = val;
            }
        }

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                {
                    var trimmed = str.Trim();
                    if (_fromWireExactOrInsensitive.TryGetValue(trimmed, out var direct))
                    {
                        return direct;
                    }

                    if (_fromNormalized.TryGetValue(StripSeparators(trimmed), out var normalized))
                    {
                        return normalized;
                    }
                }

                throw new JsonException($"Invalid {typeof(TEnum).Name} value '{str}'.");
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var intVal))
            {
                var cast = (TEnum)Enum.ToObject(typeof(TEnum), intVal);
                if (Enum.IsDefined(cast))
                {
                    return cast;
                }
            }

            throw new JsonException($"Unexpected token '{reader.TokenType}' when parsing {typeof(TEnum).Name}.");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (_toWire.TryGetValue(value, out var wire))
            {
                writer.WriteStringValue(wire);
                return;
            }

            writer.WriteStringValue(JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()));
        }

        private static string StripSeparators(string input) =>
            input.Replace("_", string.Empty, StringComparison.Ordinal)
                 .Replace("-", string.Empty, StringComparison.Ordinal);
    }
}
