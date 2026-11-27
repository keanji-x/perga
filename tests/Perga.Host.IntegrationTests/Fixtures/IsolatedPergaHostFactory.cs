using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.Host.Configuration;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests.Fixtures;

internal sealed class IsolatedPergaHostFactory(
    string dataDir,
    Action<PergaHostOptions>? configureOptions = null,
    Action<IServiceCollection>? configureServices = null,
    Action<IWebHostBuilder>? configureHost = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The endpoint key still exists with a null value, which Kestrel rejects.
            ["Kestrel:Endpoints:Http:Url"] = "http://127.0.0.1:0",
            ["urls"] = null,
            ["ASPNETCORE_URLS"] = null
        }));
        configureHost?.Invoke(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<PergaHostOptions>();
            services.AddSingleton(sp =>
            {
                var env = sp.GetRequiredService<IWebHostEnvironment>();
                var config = sp.GetRequiredService<IConfiguration>();
                var options = PergaHostOptions.BindFrom(config, env.ContentRootPath);
                options.DataDir = dataDir;
                options.DbPath = Path.Combine(dataDir, "perga.db");
                options.WorkspacesRootDirectory = Path.Combine(dataDir, "workspaces");
                options.InstallationRoot = null;
                options.BridgeBuildId = null;
                options.SystemManagementTokenSha256 = null;
                options.TerminalBridgeEndpoint = null;
                options.AutoManageTerminalBridge = true;
                options.EagerStartTerminalBridge = false;
                options.McpBaseUri = "http://localhost";
                options.DispatchWakesInBackground = false;
                options.EnableScheduleService = false;
                options.EnableMaintenanceService = false;
                options.CleanupEphemeralDataOnShutdown = false;
                configureOptions?.Invoke(options);
                return options;
            });
            services.PostConfigure<PasswordHasherOptions>(options => options.IterationCount = 1_000);
            services.RemoveAll<IOverlayMounts>();
            services.AddSingleton<IOverlayMounts, UnavailableOverlayMounts>();
            configureServices?.Invoke(services);
        });
    }

    private sealed class UnavailableOverlayMounts : IOverlayMounts
    {
        public Task<bool> ProbeAsync(CancellationToken ct) => Task.FromResult(false);
        public Task MountAsync(string trackId, string snapshotId, CancellationToken ct) => Unavailable();
        public Task UnmountAsync(string trackId, string snapshotId, CancellationToken ct) => Unavailable();

        private static Task Unavailable() =>
            Task.FromException(new WorkspaceUnavailableException("The test Host has no OverlayFS launcher."));
    }
}
