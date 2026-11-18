using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Perga.Host;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.Mcp;
using Perga.Host.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => PergaJsonDefaults.ConfigureSerializerOptions(o.SerializerOptions));
builder.Services.AddPergaHostServices(builder.Configuration, builder.Environment.ContentRootPath);

var app = builder.Build();

app.Use(HttpErrorHandling.InvokeExceptionShieldMiddlewareAsync);
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

var hostOptions = app.Services.GetRequiredService<PergaHostOptions>();
string? staticIndexHtmlPath = null;

if (!string.IsNullOrWhiteSpace(hostOptions.StaticWebRootDirectory) &&
    Directory.Exists(hostOptions.StaticWebRootDirectory))
{
    var fullStaticDir = Path.GetFullPath(hostOptions.StaticWebRootDirectory);
    var fileProvider = new PhysicalFileProvider(fullStaticDir);
    var contentTypeProvider = new FileExtensionContentTypeProvider();
    contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";

    foreach (var requestPath in new[] { string.Empty, "/next" })
    {
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = fileProvider,
            RequestPath = requestPath
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = requestPath,
            ContentTypeProvider = contentTypeProvider
        });
    }

    var candidateIndex = Path.Combine(fullStaticDir, "index.html");
    if (File.Exists(candidateIndex))
    {
        staticIndexHtmlPath = candidateIndex;
    }
}

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "perga" }));
app.MapPergaApiV1();
app.MapPergaMcpEndpoints();

app.MapFallback(async (HttpContext ctx) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;
    var isApiOrMcp =
        path.Equals("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/mcp", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/mcp/", StringComparison.OrdinalIgnoreCase);

    if (!isApiOrMcp &&
        staticIndexHtmlPath is not null &&
        File.Exists(staticIndexHtmlPath) &&
        (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
    {
        await Results.File(staticIndexHtmlPath, "text/html; charset=utf-8").ExecuteAsync(ctx).ConfigureAwait(false);
        return;
    }

    await HttpErrorHandling.NotFound($"Route '{path}' was not found.").ExecuteAsync(ctx).ConfigureAwait(false);
});

app.Run();

public partial class Program;
