using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Reflection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Perga.Host.Serialization;

namespace Perga.Host.Http;

public sealed record NullableResponse;
public sealed record OptionalWebSession;
public sealed record ResponseHeader(int StatusCode, string Name, string SchemaType = "string");
// Unlike IAcceptsMetadata, this never participates in content-type route matching.
public sealed record JsonRequestContract(Type RequestType, bool IsOptional);
public sealed record NoRequestBody;
public sealed record RequestHeader(string Name, bool Required);
public sealed record StrictRequest;
public sealed record SuccessStatus(int Code, bool ReplayedAsOk = false);

/// <summary>Exports registered HTTP contracts without starting the application.</summary>
public static class OpenApiDocumentExporter
{
    public static string Export(WebApplication app)
    {
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not null
                && endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription != true
                && (endpoint.RoutePattern.RawText == "/healthz" || endpoint.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true))
            .OrderBy(endpoint => endpoint.RoutePattern.RawText, StringComparer.Ordinal);
        var services = app.Services.GetRequiredService<IServiceProviderIsService>();
        var nullability = new NullabilityInfoContext();
        var options = new JsonSerializerOptions(PergaJsonDefaults.ApiOptions)
        { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var registry = new OpenApiSchemaRegistry(options);
        JsonNode Schema(Type type) => registry.Schema(type);
        var paths = new JsonObject();
        foreach (var endpoint in endpoints)
        foreach (var method in endpoint.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods)
        {
            var route = endpoint.RoutePattern.RawText!;
            var pathItem = paths[route] as JsonObject;
            if (pathItem is null) paths[route] = pathItem = new JsonObject();
            var parameters = new JsonArray();
            JsonObject? body = null;
            var accepts = endpoint.Metadata.GetMetadata<JsonRequestContract>();
            if (accepts?.RequestType is { } requestType)
                body = new JsonObject { ["required"] = !accepts.IsOptional,
                    ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = registry.Schema(requestType, request: true) } } };
            foreach (var parameter in endpoint.Metadata.GetRequiredMetadata<MethodInfo>().GetParameters())
            {
                var type = parameter.ParameterType;
                if (type == typeof(HttpContext) || type == typeof(CancellationToken) || services.IsService(type)) continue;
                var isPath = endpoint.RoutePattern.Parameters.Any(p => p.Name == parameter.Name);
                var scalar = type == typeof(string) || type.IsPrimitive || Nullable.GetUnderlyingType(type)?.IsPrimitive == true;
                if (!isPath && !scalar)
                {
                    if (body is null) body = new JsonObject { ["required"] = true,
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = registry.Schema(type, request: true) } } };
                    continue;
                }
                parameters.Add(new JsonObject { ["name"] = parameter.Name, ["in"] = isPath ? "path" : "query",
                    ["required"] = isPath || (!parameter.HasDefaultValue && nullability.Create(parameter).ReadState != NullabilityState.Nullable),
                    ["schema"] = registry.Schema(type, nullability.Create(parameter)) });
            }
            foreach (var header in endpoint.Metadata.OfType<RequestHeader>())
                parameters.Add(new JsonObject { ["name"] = header.Name, ["in"] = "header", ["required"] = header.Required,
                    ["schema"] = new JsonObject { ["type"] = "string" } });
            if (method is "POST" or "PATCH" or "PUT" && body is null && !endpoint.Metadata.OfType<NoRequestBody>().Any())
                throw new InvalidOperationException($"Missing HTTP request contract: {method} {route}");
            if (accepts?.RequestType is { } strictType && endpoint.Metadata.OfType<StrictRequest>().Any())
                registry.Schemas[strictType.Name]!["additionalProperties"] = false;
            var responses = new JsonObject();
            var resultType = endpoint.Metadata.GetRequiredMetadata<MethodInfo>().ReturnType;
            if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(Task<>)) resultType = resultType.GetGenericArguments()[0];
            if (resultType == typeof(Microsoft.AspNetCore.Http.HttpResults.NoContent))
                responses["204"] = new JsonObject { ["description"] = "HTTP 204" };
            if (resultType == typeof(Microsoft.AspNetCore.Http.HttpResults.Accepted))
                responses["202"] = new JsonObject { ["description"] = "HTTP 202" };
            if (resultType == typeof(Microsoft.AspNetCore.Http.HttpResults.FileStreamHttpResult))
                responses["200"] = new JsonObject { ["description"] = "Binary file (range requests supported)",
                    ["content"] = new JsonObject { ["application/octet-stream"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string", ["format"] = "binary" } } } };
            if (resultType.IsGenericType && (resultType.GetGenericTypeDefinition() == typeof(Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<>)
                || resultType.GetGenericTypeDefinition() == typeof(Microsoft.AspNetCore.Http.HttpResults.Ok<>)))
            {
                var valueType = resultType.GetGenericArguments()[0];
                responses["200"] = new JsonObject { ["description"] = "HTTP 200",
                    ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Schema(valueType) } } };
            }

            foreach (var response in endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().GroupBy(r => r.StatusCode).Select(group => group.Last()).OrderBy(r => r.StatusCode))
            {
                var item = new JsonObject { ["description"] = $"HTTP {response.StatusCode}" };
                if (response.Type is not null && response.Type != typeof(void))
                {
                    JsonNode schema = Schema(response.Type);
                    if (endpoint.Metadata.OfType<NullableResponse>().Any())
                        schema = new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) };
                    item["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = schema } };
                }
                responses[response.StatusCode.ToString()] = item;
            }
            if (endpoint.Metadata.GetMetadata<SuccessStatus>() is { } success && responses["200"] is { } successResponse)
            {
                responses[success.Code.ToString()] = successResponse.DeepClone();
                if (!success.ReplayedAsOk) responses.Remove("200");
            }
            if (responses.Count > 1 && responses.ContainsKey("202")) responses.Remove("200");
            if (responses.Count == 0) throw new InvalidOperationException($"Missing HTTP response contract: {method} {route}");
            responses["default"] = new JsonObject { ["description"] = "Error mapped by Host exception handling or request guards",
                ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Schema(typeof(ApiErrorResponse)) } } };
            foreach (var header in endpoint.Metadata.OfType<ResponseHeader>())
            {
                var response = responses[header.StatusCode.ToString()]!.AsObject();
                if (response["headers"] is not JsonObject) response["headers"] = new JsonObject();
                response["headers"]![header.Name] = new JsonObject { ["schema"] = new JsonObject { ["type"] = header.SchemaType } };
            }
            var operation = new JsonObject { ["parameters"] = parameters, ["responses"] = responses };
            var security = OpenApiAuthorization.Security(app.Services, endpoint);
            if (!JsonNode.DeepEquals(security, OpenApiAuthorization.CookieSecurity())) operation["security"] = security;
            if (body is not null) operation["requestBody"] = body;
            pathItem[method.ToLowerInvariant()] = operation;
        }
        var document = new JsonObject { ["openapi"] = "3.1.0",
            ["info"] = new JsonObject { ["title"] = "Perga Host", ["version"] = "v1" },
            ["security"] = OpenApiAuthorization.CookieSecurity(),
            ["paths"] = paths, ["components"] = new JsonObject { ["schemas"] = registry.Schemas, ["securitySchemes"] = OpenApiAuthorization.Schemes() } };
        return Sort(document).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static JsonNode Sort(JsonNode node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => KeyValuePair.Create(pair.Key, pair.Value is null ? null : Sort(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(value => value is null ? null : Sort(value)).ToArray()),
        _ => node.DeepClone()
    };
}
