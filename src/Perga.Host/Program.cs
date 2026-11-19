using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Perga.Host;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.Mcp;
using Perga.Host.Serialization;
using Perga.Host.SystemUpgrade;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => PergaJsonDefaults.ConfigureSerializerOptions(o.SerializerOptions));
builder.Services.AddPergaHostServices(builder.Configuration, builder.Environment.ContentRootPath);

var app = builder.Build();

app.Use(HttpErrorHandling.InvokeExceptionShieldMiddlewareAsync);
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

_ = app.Services.GetRequiredService<SystemManagementAuthorization>();
var releases = app.Services.GetRequiredService<ReleaseSnapshotManager>();
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";
foreach (var requestPath in new[] { string.Empty, "/next" })
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = releases, RequestPath = requestPath });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = releases, RequestPath = requestPath, ContentTypeProvider = contentTypeProvider,
        OnPrepareResponse = ctx =>
        {
            if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    });
}

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "perga" }));
app.MapPergaApiV1();
app.MapPergaSystem();
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
        releases.GetFileInfo("index.html") is { Exists: true, PhysicalPath: not null } index &&
        (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
    {
        ctx.Response.Headers.CacheControl = "no-cache";
        await Results.File(index.PhysicalPath, "text/html; charset=utf-8").ExecuteAsync(ctx).ConfigureAwait(false);
        return;
    }

    await HttpErrorHandling.NotFound($"Route '{path}' was not found.").ExecuteAsync(ctx).ConfigureAwait(false);
});

app.Run();

public partial class Program;
