using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Perga.Host.Cli;
using Perga.Host.Configuration;
using Perga.Host.Http;
using Perga.Host.WebSockets;
using Perga.Kernel.Ports;

namespace Perga.Host.Auth;

internal sealed class LocalAuthStartup(
    PergaHostOptions options, IConfiguration configuration, IAuthStore store, LocalSessionManager sessions,
    IWebHostEnvironment environment, IHostApplicationLifetime lifetime, IServer server,
    ResolvedHostStoragePaths paths, AuthWebSocketRegistry sockets, ILogger<LocalAuthStartup> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken ct)
    {
        if (options.RequireAuth || options.AuthIngressMode != AuthIngressMode.Direct ||
            HasNonLoopbackBindUrl(configuration) || await store.GetOwnerAsync(ct) is not null)
        {
            sessions.ClearStaleTokenFile();
            return;
        }

        var interactive = !environment.IsEnvironment("Testing") &&
            server.GetType().FullName != "Microsoft.AspNetCore.TestHost.TestServer" &&
            HostStartupOnboarding.IsForegroundInteractiveTerminal();
        string? token = sessions.Start();
        lifetime.ApplicationStopping.Register(sessions.Dispose);
        if (interactive)
        {
            lifetime.ApplicationStarted.Register(() =>
            {
                var bound = server.Features.Get<IServerAddressesFeature>()?.Addresses;
                var address = options.AuthCookieSecurePolicy == AuthCookieSecurePolicy.Always
                    ? bound?.FirstOrDefault(value => value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    : bound?.FirstOrDefault();
                if (address is not null)
                    Console.Out.WriteLine($"Open Perga: {address.TrimEnd('/')}/auth/local?token={token}");
                token = null;
            });
        }
        else
        {
            token = null;
        }

        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The CLI writes in a separate process. Only query SQLite when file metadata changes.
        var db = new FileInfo(paths.DbPath);
        var wal = new FileInfo(paths.DbPath + "-wal");
        ((DateTime, long) Db, (DateTime, long) Wal)? previous = null;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (sessions.IsActive && await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!sessions.IsActive)
                    return;

                var stamp = (ReadStamp(db), ReadStamp(wal));
                if (stamp == previous)
                    continue;

                previous = stamp;
                if (await sessions.CheckOwnerAsync(store, sockets, stoppingToken))
                    return;
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not observe local owner changes. Session revalidation will check SQLite.");
        }
    }

    private static (DateTime LastWriteTimeUtc, long Length) ReadStamp(FileInfo file)
    {
        file.Refresh();
        return file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
    }

    public static bool HasNonLoopbackBindUrl(IConfiguration configuration)
    {
        var urls = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        var endpoints = configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Select(endpoint => endpoint["Url"]).OfType<string>();
        if (!string.IsNullOrWhiteSpace(urls))
            endpoints = endpoints.Concat(urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (!string.IsNullOrWhiteSpace(configuration["http_ports"]) ||
                 !string.IsNullOrWhiteSpace(configuration["https_ports"]))
            return true;

        return endpoints.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !RawTransport.IsLoopbackHost(uri.Host));
    }
}
