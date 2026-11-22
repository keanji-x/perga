using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Perga.Host;
using Perga.Host.Auth;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.Mcp;
using Perga.Host.Serialization;
using Perga.Host.SystemUpgrade;

if (args is ["auth", ..])
    return await AuthCliCommand.RunAsync(args, Console.In, Console.Out, Console.Error);

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => PergaJsonDefaults.ConfigureSerializerOptions(o.SerializerOptions));
builder.Services.AddPergaHostServices(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddPergaAuthentication();
// The framework startup filter uses DI options. Perga applies explicit options after capturing the socket.
builder.Services.PostConfigure<ForwardedHeadersOptions>(framework => framework.ForwardedHeaders = ForwardedHeaders.None);
var app = builder.Build();

var options = app.Services.GetRequiredService<PergaHostOptions>();
options.ValidateAuth();
app.UseMiddleware<CaptureRawTransportMiddleware>();
if (options.TrustForwardedHeaders)
{
    var forwarding = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1
    };
#pragma warning disable ASPDEPR005 // Clear the legacy and current network lists.
    forwarding.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
    forwarding.KnownIPNetworks.Clear();
    forwarding.KnownProxies.Clear();
    foreach (var proxy in options.TrustedProxyAddresses.Length == 0
        ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
        : options.TrustedProxyAddresses.Select(IPAddress.Parse))
        forwarding.KnownProxies.Add(proxy);
    app.UseForwardedHeaders(forwarding);
}

app.Use(HttpErrorHandling.InvokeExceptionShieldMiddlewareAsync);
app.UseMiddleware<TransportSecurityMiddleware>();
app.UseMiddleware<RequestOriginGuard>();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
// Fail closed on unauthenticated synthetic 405 endpoints under /api and /mcp.
app.Use(async (context, next) =>
{
    var isApi = context.Request.Path.StartsWithSegments("/api");
    var isMcp = !isApi && context.Request.Path.StartsWithSegments("/mcp");
    if ((isApi || isMcp) && context.GetEndpoint() is { } endpoint &&
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null &&
        endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
    {
        var scheme = isMcp ? PergaAuthSchemes.McpSession : PergaAuthSchemes.WebSession;
        var auth = await context.AuthenticateAsync(scheme);
        if (!auth.Succeeded)
        {
            context.Response.Headers.CacheControl = "no-store";
            await HttpErrorHandling.Unauthorized().ExecuteAsync(context);
            return;
        }
    }

    await next(context);
});
app.Use(RequestOriginGuard.EnforceWriteSourceAsync);

_ = app.Services.GetRequiredService<IAuthPasswordHasher>();
_ = app.Services.GetRequiredService<SystemManagementAuthorization>();
var releases = app.Services.GetRequiredService<ReleaseSnapshotManager>();
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = releases, RequestPath = string.Empty });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = releases, RequestPath = string.Empty, ContentTypeProvider = contentTypeProvider,
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", service = "perga" })).AllowAnonymous();
app.MapPergaAuth();
app.MapPergaApiV1();
app.MapPergaSystem();
app.MapTodayEndpoints();
app.MapPergaMcpEndpoints();

app.MapFallback("/api/{**path}", (HttpContext ctx) => HttpErrorHandling.NotFound($"Route '{ctx.Request.Path}' was not found."))
    .RequireAuthorization(PergaAuthPolicies.UserSession);
app.MapFallback("/mcp/{**path}", (HttpContext ctx) => HttpErrorHandling.NotFound($"Route '{ctx.Request.Path}' was not found."))
    .RequireAuthorization(PergaAuthPolicies.McpSessionOnly);

app.MapFallback(async (HttpContext ctx) =>
{
    if (releases.GetFileInfo("index.html") is { Exists: true, PhysicalPath: not null } index &&
        (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
    {
        ctx.Response.Headers.CacheControl = "no-cache";
        await Results.File(index.PhysicalPath, "text/html; charset=utf-8").ExecuteAsync(ctx).ConfigureAwait(false);
        return;
    }

    await HttpErrorHandling.NotFound($"Route '{ctx.Request.Path}' was not found.").ExecuteAsync(ctx).ConfigureAwait(false);
});

app.Run();
return 0;

public partial class Program;
