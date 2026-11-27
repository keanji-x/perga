using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Perga.Host;
using Perga.Host.Auth;
using Perga.Host.Cli;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.Mcp;
using Perga.Host.Serialization;
using Perga.Host.SystemUpgrade;
using Perga.Kernel.Ports;
using Perga.Storage.Sqlite;

var daemon = HostDaemonRuntime.FromProcess();
var foreground = daemon is null ? HostDaemonCommand.ForegroundOptions(args) : null;
if (daemon is null && foreground is null)
{
    if (args is ["auth", ..])
        return await AuthCliCommand.RunAsync(args, Console.In, Console.Out, Console.Error);
    if (HostCliCommand.Handles(args))
        return HostDaemonCommand.Handles(args[0])
            ? await HostDaemonCommand.RunWithConsoleCancellationAsync(args, HostCliEnvironment.FromProcess())
            : await HostCliCommand.RunAsync(args, HostCliEnvironment.FromProcess());
}
args = foreground?.HostArgs ?? args;

string? exportPath = null;
if (args.Contains("--export-openapi", StringComparer.Ordinal))
{
    if (args is not ["--export-openapi", var destination] || string.IsNullOrWhiteSpace(destination))
        throw new ArgumentException("Usage: Perga.Host --export-openapi <path>");
    exportPath = Path.GetFullPath(destination);
}
var managed = daemon is not null || foreground is not null;
var builder = PergaHttpEndpoints.CreateBuilder(exportPath is not null ? [] :
    (managed ? HostDaemonOptions.ConfigurationArguments(args) : args).Select(NormalizeHostArgument).ToArray());
if (managed)
    HostDaemonRuntime.Configure(builder, foreground);
// The framework's information-level request logs include query strings.
builder.Services.PostConfigure<LoggerFilterOptions>(filters =>
{
    for (var i = 0; i < filters.Rules.Count; i++)
    {
        var rule = filters.Rules[i];
        filters.Rules[i] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
            (provider, category, level) => AllowHostingDiagnostics(category, level) &&
                (rule.Filter?.Invoke(provider, category, level) ?? true));
    }
    filters.Rules.Insert(0, new LoggerFilterRule(null, null, filters.MinLevel,
        (_, category, level) => AllowHostingDiagnostics(category, level)));
});

// The framework startup filter uses DI options. Perga applies explicit options after capturing the socket.
builder.Services.PostConfigure<ForwardedHeadersOptions>(framework => framework.ForwardedHeaders = ForwardedHeaders.None);
var app = builder.Build();
if (exportPath is not null)
{
    app.MapPublishedHttpEndpoints();
    await File.WriteAllTextAsync(exportPath, OpenApiDocumentExporter.Export(app));
    await app.DisposeAsync();
    return 0;
}

var isTestHost = app.Environment.IsEnvironment("Testing") ||
                 string.Equals(app.Services.GetService<IServer>()?.GetType().FullName,
                     "Microsoft.AspNetCore.TestHost.TestServer", StringComparison.Ordinal);

PergaHostOptions options;
try
{
    options = app.Services.GetRequiredService<PergaHostOptions>();
    options.ValidateAuth();
}
catch (Exception ex) when (!isTestHost && HostStartupOnboarding.FormatConfigurationError(ex) is { } message)
{
    await Console.Error.WriteLineAsync(message);
    await app.DisposeAsync();
    return 1;
}
if (options.AuthAllowInsecureHttp)
{
    app.Logger.LogWarning(
        "Perga:AuthAllowInsecureHttp is enabled. Cleartext HTTP requests are permitted on /api; use only on trusted local or private networks.");
}
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

try
{
    var releases = app.Services.GetRequiredService<ReleaseSnapshotManager>();
    MapWebAndEndpoints(app, releases);

    _ = app.Services.GetRequiredService<IAuthPasswordHasher>();
    if (!isTestHost && !options.RequireAuth && options.AuthIngressMode == AuthIngressMode.Direct &&
        LocalAuthStartup.HasNonLoopbackBindUrl(app.Configuration))
    {
        await app.Services.GetRequiredService<SqliteTrackStore>().InitializeAsync(default);
        if (await app.Services.GetRequiredService<IAuthStore>().GetOwnerAsync(default) is null)
        {
            app.Services.GetRequiredService<LocalSessionManager>().ClearStaleTokenFile();
            await Console.Error.WriteLineAsync(
                "Perga refuses to bind a non-loopback address without authentication. Start with --require-auth or PERGA_AUTH_REQUIRE=true, or run perga auth init-owner.");
            await app.DisposeAsync();
            return 1;
        }
    }

    _ = app.Services.GetRequiredService<SystemManagementAuthorization>();
    var storagePaths = app.Services.GetRequiredService<ResolvedHostStoragePaths>();
    if (daemon is not null)
    {
        try
        {
            await app.StartAsync();
            // StartAsync completes after ApplicationStarted and the server publishes its bound addresses.
            daemon.Publish(app, storagePaths, args);
            await app.WaitForShutdownAsync();
        }
        finally
        {
            try { await app.DisposeAsync(); }
            finally { daemon.RemoveState(); }
        }
    }
    else
    {
        using var ready = foreground is not null
            ? app.Lifetime.ApplicationStarted.Register(() => HostDaemonRuntime.PrintForegroundBanner(app, foreground))
            : default;
        app.Run();
    }
}
catch (Exception ex) when (!isTestHost && HostStartupOnboarding.FormatStartupError(ex) is { } message)
{
    await Console.Error.WriteLineAsync(message);
    await app.DisposeAsync();
    return 1;
}
return 0;

static string NormalizeHostArgument(string argument) => argument switch
{
    "--require-auth" => "--Perga:RequireAuth=true",
    _ when argument.StartsWith("--require-auth=", StringComparison.Ordinal) =>
        "--Perga:RequireAuth=" + argument["--require-auth=".Length..],
    _ => argument
};

static bool AllowHostingDiagnostics(string? category, LogLevel level) =>
    level >= LogLevel.Warning ||
    !string.Equals(category, "Microsoft.AspNetCore.Hosting.Diagnostics", StringComparison.OrdinalIgnoreCase);

static void MapWebAndEndpoints(WebApplication app, ReleaseSnapshotManager releases)
{
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

    app.MapPublishedHttpEndpoints();
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
}

public partial class Program;
