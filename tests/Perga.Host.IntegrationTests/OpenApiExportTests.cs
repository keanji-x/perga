using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Perga.Host;
using Perga.Host.Http;
using Perga.Kernel;
using Perga.Host.Serialization;
using System.Text.Json.Nodes;

namespace Perga.Host.IntegrationTests;

public sealed class OpenApiExportTests
{
    [Fact]
    public async Task Published_document_matches_real_routes_and_is_deterministic_without_starting_host()
    {
        var root = FindRoot();
        var builder = PergaHttpEndpoints.CreateBuilder([], root);
        await using var app = builder.Build();
        app.MapPublishedHttpEndpoints();
        var first = OpenApiDocumentExporter.Export(app);
        Assert.Equal(first, OpenApiDocumentExporter.Export(app));
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(root, "fe/core/api/generated/openapi.json")), first);
        using var document = JsonDocument.Parse(first);
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/today/daily", out _));
        Assert.False(paths.TryGetProperty("/api/today/daily", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var area = schemas.GetProperty("AreaDto").GetProperty("properties");
        Assert.True(area.TryGetProperty("createdAtMs", out _));
        Assert.False(area.TryGetProperty("created_at", out _));
        var daily = schemas.GetProperty("DailyTrackDto").GetProperty("properties");
        Assert.True(daily.TryGetProperty("timeZone", out _));
        Assert.True(daily.TryGetProperty("trackId", out _));
    }

    [Fact]
    public async Task Published_image_contract_constrains_attachment_fields_and_declares_multipart_requirements()
    {
        var builder = PergaHttpEndpoints.CreateBuilder([], FindRoot());
        await using var app = builder.Build();
        app.MapPublishedHttpEndpoints();
        using var document = JsonDocument.Parse(OpenApiDocumentExporter.Export(app));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var attachment = schemas.GetProperty("MessageAttachmentDto");
        Assert.False(attachment.TryGetProperty("oneOf", out _));
        var fields = attachment.GetProperty("properties");
        Assert.Equal(@"^[0-9a-f]{64}\.(png|jpg|gif|webp)$", fields.GetProperty("id").GetProperty("pattern").GetString());
        Assert.Equal(["image/png", "image/jpeg", "image/gif", "image/webp"],
            fields.GetProperty("contentType").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1, fields.GetProperty("size").GetProperty("minimum").GetInt64());
        Assert.Equal(10 * 1024 * 1024, fields.GetProperty("size").GetProperty("maximum").GetInt64());
        Assert.Equal(@"^/api/v1/tracks/[A-Za-z0-9_-]+/media/[0-9a-f]{64}\.(png|jpg|gif|webp)$",
            fields.GetProperty("url").GetProperty("pattern").GetString());

        var paths = document.RootElement.GetProperty("paths");
        var send = paths.GetProperty("/api/v1/sessions/{sessionId}/messages").GetProperty("post");
        var content = send.GetProperty("requestBody").GetProperty("content");
        var multipart = content.GetProperty("multipart/form-data");
        Assert.Equal("Idempotency-Key", Assert.Single(multipart.GetProperty("x-required-headers").EnumerateArray()).GetString());
        var payload = multipart.GetProperty("schema").GetProperty("properties").GetProperty("payload");
        var payloadRef = payload.GetProperty("contentSchema").GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/MultipartMessagePayload", payloadRef);
        var message = schemas.GetProperty("MultipartMessagePayload");
        var messageProperties = message.GetProperty("properties");
        Assert.False(message.GetProperty("additionalProperties").GetBoolean());
        Assert.False(messageProperties.TryGetProperty("messageId", out _));
        Assert.True(schemas.GetProperty("SendSessionMessageRequest").GetProperty("properties").TryGetProperty("messageId", out _));
        var key = Assert.Single(send.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.False(key.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task Missing_response_metadata_fails_closed()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        app.MapGet("/api/v1/untyped", () => Microsoft.AspNetCore.Http.Results.Ok(new { value = 1 }));
        Assert.Throws<InvalidOperationException>(() => OpenApiDocumentExporter.Export(app));
    }

    [Fact]
    public async Task Serialized_session_page_fixture_matches_current_Host_DTO()
    {
        var path = Path.Combine(FindRoot(), "fe/core/api/fixtures/unified-host-payloads.json");
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(path))!["page"]!;
        var dto = fixture.Deserialize<SessionItemsPageDto>(PergaJsonDefaults.ApiOptions)!;
        Assert.True(JsonNode.DeepEquals(fixture, JsonSerializer.SerializeToNode(dto, PergaJsonDefaults.ApiOptions)));
    }

    [Fact]
    public async Task Missing_mutation_body_contract_fails_closed()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        app.MapPost("/api/v1/untyped", () => Microsoft.AspNetCore.Http.TypedResults.Ok(new HealthStatus("ok", "test")));
        Assert.Throws<InvalidOperationException>(() => OpenApiDocumentExporter.Export(app));
    }

    [Fact]
    public async Task Colliding_schema_names_fail_closed()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        app.MapGet("/api/v1/first", () => Microsoft.AspNetCore.Http.TypedResults.Ok(new First.Duplicate("first")));
        app.MapGet("/api/v1/second", () => Microsoft.AspNetCore.Http.TypedResults.Ok(new Second.Duplicate("second")));
        Assert.Throws<InvalidOperationException>(() => OpenApiDocumentExporter.Export(app));
    }

    public static class First { public sealed record Duplicate(string Value); }
    public static class Second { public sealed record Duplicate(string Value); }

    [Fact]
    public async Task Published_routes_retain_upstream_auth_metadata_and_security()
    {
        var builder = PergaHttpEndpoints.CreateBuilder([], FindRoot());
        await using var app = builder.Build();
        app.MapPublishedHttpEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();
        var count = 0;
        foreach (var endpoint in endpoints)
        {
            var path = endpoint.RoutePattern.RawText!;
            var anonymous = path is "/healthz" or "/api/v1/auth/login" or "/api/v1/auth/logout"
                or "/api/v1/auth/setup" or "/api/v1/auth/local" or "/auth/local";
            Assert.Equal(anonymous, endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null);
            if (anonymous) continue;
            var policy = path switch
            {
                "/api/v1/system/info" => "UserOrManagement",
                "/api/v1/system/drain" or "/api/v1/system/reload" or "/api/v1/system/auth-status" => "SystemManagementOnly",
                _ => "UserSession"
            };
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == policy);
            count++;
        }
        Assert.True(count > 50);
        using var document = JsonDocument.Parse(OpenApiDocumentExporter.Export(app));
        var paths = document.RootElement.GetProperty("paths");
        Assert.False(paths.TryGetProperty("/auth/local", out _));
        Assert.Equal(0, paths.GetProperty("/api/v1/auth/login").GetProperty("post").GetProperty("security").GetArrayLength());
        Assert.Equal("SessionIdentityDto", paths.GetProperty("/api/v1/auth/session").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString()!.Split('/').Last());
        Assert.False(paths.GetProperty("/api/v1/auth/logout").GetProperty("post").TryGetProperty("requestBody", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_or_missing_authentication_schemes_fail_export(bool unknownScheme)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorizationBuilder().AddPolicy("test-policy", policy =>
        {
            policy.RequireAuthenticatedUser();
            if (unknownScheme) policy.AddAuthenticationSchemes("undeclared");
        });
        await using var app = builder.Build();
        app.MapGet("/api/v1/test-policy", () => Microsoft.AspNetCore.Http.TypedResults.Ok("value"))
            .RequireAuthorization("test-policy");
        Assert.Throws<InvalidOperationException>(() => OpenApiDocumentExporter.Export(app));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Perga.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
