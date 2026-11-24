using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Perga.Contracts;

namespace Perga.Host.Http;

/// <summary>JSON Schema for the same serializer metadata used by HTTP responses.</summary>
internal sealed class OpenApiSchemaRegistry(JsonSerializerOptions options)
{
    public JsonObject Schemas { get; } = new();
    private readonly Dictionary<string, Type> _typesByName = new(StringComparer.Ordinal);
    private readonly NullabilityInfoContext _nullability = new();

    public JsonNode Schema(Type type, NullabilityInfo? nullable = null, bool request = false)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying is not null) return OrNull(Schema(underlying, request: request));
        var result = NonNullable(type, nullable, request);
        return nullable?.ReadState == NullabilityState.Nullable && !type.IsValueType ? OrNull(result) : result;
    }

    private JsonNode NonNullable(Type type, NullabilityInfo? nullable, bool request)
    {
        if (type == typeof(Perga.Kernel.OptionalString)) return OrNull(new JsonObject { ["type"] = "string" });
        if (type == typeof(object) || type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type)) return new JsonObject();
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type.IsEnum) return new JsonObject { ["type"] = "string",
            ["enum"] = new JsonArray(Enum.GetValues(type).Cast<object>().Select(value => JsonSerializer.SerializeToNode(value, type, options)).ToArray()) };
        if (type.IsPrimitive || type == typeof(decimal)) return new JsonObject { ["type"] = type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" : "integer" };
        var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType &&
            (t.GetGenericTypeDefinition() == typeof(IDictionary<,>) || t.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dictionary is not null) return new JsonObject { ["type"] = "object",
            ["additionalProperties"] = Schema(dictionary.GetGenericArguments()[1], nullable?.GenericTypeArguments.ElementAtOrDefault(1), request) };
        var enumerable = type.GetInterfaces().Append(type).FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null) return new JsonObject { ["type"] = "array",
            ["items"] = Schema(enumerable.GetGenericArguments()[0], type.IsArray ? nullable?.ElementType : nullable?.GenericTypeArguments.FirstOrDefault(), request) };
        var name = type.Name;
        if (_typesByName.TryGetValue(name, out var existingType) && existingType != type)
            throw new InvalidOperationException($"Ambiguous contract schema name: {name}");
        _typesByName[name] = type;
        if (!Schemas.ContainsKey(name))
        {
            Schemas[name] = new JsonObject(); // Reserve before recursion.
            var info = options.GetTypeInfo(type);
            if (info.Kind != JsonTypeInfoKind.Object) throw new InvalidOperationException($"Unsupported contract type: {type}");
            var properties = new JsonObject();
            var required = new JsonArray();
            var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            foreach (var property in info.Properties.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                if (property.Get is null && property.Set is null) continue;
                var member = property.AttributeProvider as PropertyInfo;
                var nullInfo = member is null ? null : _nullability.Create(member);
                properties[property.Name] = type == typeof(SessionItemDto) && member?.Name == nameof(SessionItemDto.Payload)
                    ? new JsonObject { ["type"] = "object", ["additionalProperties"] = true,
                        ["properties"] = new JsonObject
                        {
                            ["attachments"] = Schema(typeof(MessageAttachmentDto[])),
                            ["requestKey"] = new JsonObject { ["type"] = "string" }
                        } }
                    : Schema(property.PropertyType, nullInfo, request);
                var parameter = constructor?.GetParameters().FirstOrDefault(p => string.Equals(p.Name, member?.Name, StringComparison.OrdinalIgnoreCase));
                var ignore = member?.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition;
                if (property.IsRequired || (request ? parameter?.HasDefaultValue == false : ignore is not (JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault))) required.Add(property.Name);
            }
            var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
            if (type == typeof(MessageAttachmentDto))
                ConstrainImageAttachment(schema, properties);
            Schemas[name] = schema;
        }
        return new JsonObject { ["$ref"] = "#/components/schemas/" + name };
    }

    private static void ConstrainImageAttachment(JsonObject schema, JsonObject properties)
    {
        properties["contentType"]!["enum"] = new JsonArray(
            ImageAttachmentContract.MediaTypes.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        properties["size"]!["minimum"] = 1;
        properties["size"]!["maximum"] = ImageAttachmentContract.MaxAttachmentBytes;
        schema["oneOf"] = new JsonArray(ImageAttachmentContract.MediaTypesByExtension
            .OrderBy(format => format.Key, StringComparer.Ordinal)
            .Select(format =>
            {
                var fileName = @"[0-9a-f]{64}\." + format.Key + "$";
                return (JsonNode)new JsonObject
                {
                    ["properties"] = new JsonObject
                    {
                        ["id"] = new JsonObject { ["pattern"] = "^" + fileName },
                        ["contentType"] = new JsonObject { ["const"] = format.Value },
                        ["url"] = new JsonObject { ["pattern"] = "^/api/v1/tracks/[A-Za-z0-9_-]+/media/" + fileName }
                    }
                };
            }).ToArray());
    }

    private static JsonNode OrNull(JsonNode schema) => new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
}
