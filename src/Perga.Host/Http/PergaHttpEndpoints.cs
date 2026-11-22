using Perga.Host.Auth;
using Perga.Host.Serialization;

namespace Perga.Host.Http;

public sealed record HealthStatus(string Status, string Service);

/// <summary>The published HTTP surface shared by serving and offline export.</summary>
public static class PergaHttpEndpoints
{
    public static WebApplicationBuilder CreateBuilder(string[] args, string? contentRoot = null)
    {
        var builder = contentRoot is null ? WebApplication.CreateBuilder(args)
            : WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });
        builder.Services.ConfigureHttpJsonOptions(o => PergaJsonDefaults.ConfigureSerializerOptions(o.SerializerOptions));
        builder.Services.AddPergaHostServices(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.AddPergaAuthentication();
        return builder;
    }

    public static void MapPublishedHttpEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => TypedResults.Ok(new HealthStatus("ok", "perga"))).AllowAnonymous();
        app.MapPergaAuth();
        app.MapPergaApiV1();
        app.MapPergaSystem();
        app.MapTodayEndpoints();
    }
}
