using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.AgentKit;
using Perga.Host.Configuration;
using Perga.Host.Lifecycle;
using Perga.Host.Mcp;
using Perga.Host.Terminals;
using Perga.Host.Workspaces;
using Perga.Host.SystemUpgrade;
using Perga.Kernel.Templates;
using Perga.Kernel;
using Perga.Kernel.Ports;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.Storage.Sqlite;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Host;

public sealed record ResolvedHostStoragePaths(
    string DataDir,
    string DbPath,
    string WorkspacesRootDir,
    bool IsEphemeral);

internal sealed class EphemeralStorageCleanupHook : IDisposable
{
    private readonly ResolvedHostStoragePaths _paths;
    private readonly PergaHostOptions _options;
    private int _disposed;

    public EphemeralStorageCleanupHook(ResolvedHostStoragePaths paths, PergaHostOptions options)
    {
        _paths = paths;
        _options = options;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_paths.IsEphemeral && _options.CleanupEphemeralDataOnShutdown && Directory.Exists(_paths.DataDir))
        {
            try
            {
                Directory.Delete(_paths.DataDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of ephemeral per-instance temporary storage.
            }
        }
    }
}

public static class PergaHostServiceCollectionExtensions
{
    private static readonly object PolicyVmWarmupLock = new();
    private static bool _policyVmWarmedUp;

    public static IServiceCollection AddPergaHostServices(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRootPath)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => PergaHostOptions.BindFrom(configuration, contentRootPath));

        services.TryAddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<PergaHostOptions>();
            var (dataDir, dbPath, wsDir, isEphemeral) = opts.ResolveStoragePaths();
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(wsDir);
            return new ResolvedHostStoragePaths(dataDir, dbPath, wsDir, isEphemeral);
        });

        // Registered BEFORE SqliteTrackStore & PergaKernelService so DI LIFO disposal deletes temp dir LAST.
        services.AddSingleton<EphemeralStorageCleanupHook>();

        services.TryAddSingleton(sp =>
        {
            _ = sp.GetRequiredService<EphemeralStorageCleanupHook>();
            var paths = sp.GetRequiredService<ResolvedHostStoragePaths>();
            var timeProvider = sp.GetRequiredService<TimeProvider>();
            return new SqliteTrackStore(paths.DbPath, paths.DataDir, timeProvider);
        });

        services.TryAddSingleton<ITrackStore>(sp => sp.GetRequiredService<SqliteTrackStore>());
        services.TryAddSingleton<ITrackScheduleStore>(sp => sp.GetRequiredService<SqliteTrackStore>());
        services.TryAddSingleton<ISystemUpgradeStore>(sp => sp.GetRequiredService<SqliteTrackStore>());
        services.TryAddSingleton<ITodayReadStore>(sp => sp.GetRequiredService<SqliteTrackStore>());
        services.TryAddSingleton<IScheduleCronEvaluator, Perga.Host.Lifecycle.CronosScheduleCronEvaluator>();
        services.TryAddSingleton<TodayReportService>();
        services.TryAddSingleton<ITrackReadStore>(sp => sp.GetRequiredService<ITrackStore>());

        services.TryAddSingleton<IReportDocEngine, ReportDocEngine>();
        services.TryAddSingleton<IPolicyVm, PolicyVm.PolicyVm>();
        services.TryAddSingleton<IAgentRuntime>(sp =>
        {
            var opts = sp.GetRequiredService<PergaHostOptions>();
            return new AgentRuntime(new AgentRuntimeOptions
            {
                NodeExecutablePath = opts.OarNodeExecutablePath,
                BridgeScriptPath = opts.OarBridgeScriptPath,
                ProviderOverrides = opts.ProviderExecutables.ToDictionary(
                    entry => AgentRuntime.NormalizeProviderName(entry.Key),
                    entry => new ProviderCommandOverride(entry.Value),
                    StringComparer.OrdinalIgnoreCase)
            });
        });
        services.TryAddSingleton<VcsService>();
        services.TryAddSingleton<IVcsService>(sp => sp.GetRequiredService<VcsService>());
        services.TryAddSingleton<IGitHubForgeService, GitHubForgeService>();
        services.TryAddSingleton<IOverlayMounts>(sp =>
        {
            var paths = sp.GetRequiredService<ResolvedHostStoragePaths>();
            return new OverlayMountClient(sp.GetRequiredService<PergaHostOptions>(), paths.WorkspacesRootDir, paths.IsEphemeral);
        });
        services.TryAddSingleton<IWorkspaceDriverRegistry>(sp =>
        {
            var paths = sp.GetRequiredService<ResolvedHostStoragePaths>();
            var options = sp.GetRequiredService<PergaHostOptions>();
            return new WorkspaceDriverRegistry(
            [
                new DirectWorkspaceDriver(),
                new VcsWorkspaceDriver(sp.GetRequiredService<IVcsService>()),
                new OverlayWorkspaceDriver(sp.GetRequiredService<VcsService>(), sp.GetRequiredService<IOverlayMounts>(),
                    new OverlayWorkspaceOptions(paths.WorkspacesRootDir,
                        options.OverlayWorkspaceWarmCommand, options.OverlayWorkspaceCacheVersion)),
            ]);
        });

        services.TryAddSingleton<ITerminalBridgeClient>(sp =>
            new ManagedTerminalBridgeClient(sp.GetRequiredService<PergaHostOptions>()));

        services.TryAddSingleton(sp =>
            new HostMcpSessionBridge(
                sp.GetRequiredService<PergaHostOptions>(),
                sp.GetService<IServer>()));
        services.TryAddSingleton<IMcpSessionBridge>(sp => sp.GetRequiredService<HostMcpSessionBridge>());

        services.TryAddSingleton<Perga.Host.Http.SystemManagementAuthorization>();
        services.TryAddSingleton<ReleaseSnapshotManager>();
        services.TryAddSingleton<ITemplateCatalog>(sp => sp.GetRequiredService<ReleaseSnapshotManager>());
        services.TryAddSingleton<IUpgradeProcessRunner, UpgradeProcessRunner>();
        services.TryAddSingleton<ISystemUpgradeController, SystemUpgradeController>();
        services.TryAddSingleton<ISystemUpgradeReceiptSource, UpgradeReceiptSource>();

        services.TryAddSingleton<IPergaKernel>(sp =>
        {
            var opts = sp.GetRequiredService<PergaHostOptions>();
            var paths = sp.GetRequiredService<ResolvedHostStoragePaths>();
            var bridge = sp.GetRequiredService<IMcpSessionBridge>();
            var baseUri = bridge is HostMcpSessionBridge hostBridge
                ? hostBridge.ResolveBaseUri()
                : (opts.McpBaseUri ?? "http://127.0.0.1:5000");

            var kernelOpts = new KernelOptions
            {
                Version = opts.Version,
                TemplatesDirectory = opts.TemplatesDirectory,
                WorkspacesRootDirectory = paths.WorkspacesRootDir,
                DefaultProvider = opts.DefaultProvider,
                DefaultModel = opts.DefaultModel,
                DefaultWorkspaceDriver = opts.DefaultWorkspaceDriver,
                McpBaseUri = baseUri,
                DispatchWakesInBackground = opts.DispatchWakesInBackground,
                SessionStartupTimeout = opts.SessionStartupTimeout,
                SessionCompactionTimeout = opts.SessionCompactionTimeout,
                ProviderCancelDispatchGrace = opts.ProviderCancelDispatchGrace,
                ProviderTerminalStopGrace = opts.ProviderTerminalStopGrace
            };

            var policyVm = sp.GetRequiredService<IPolicyVm>();
            var templateCatalog = sp.GetService<Perga.Kernel.Templates.ITemplateCatalog>()
                                  ?? CreateResilientTemplateCatalog(policyVm, opts.TemplatesDirectory);

            return new PergaKernelService(
                store: sp.GetRequiredService<ITrackStore>(),
                reportEngine: sp.GetRequiredService<IReportDocEngine>(),
                policyVm: policyVm,
                agentRuntime: sp.GetRequiredService<IAgentRuntime>(),
                workspaceDrivers: sp.GetRequiredService<IWorkspaceDriverRegistry>(),
                vcsService: sp.GetRequiredService<IVcsService>(),
                terminalClient: sp.GetRequiredService<ITerminalBridgeClient>(),
                options: kernelOpts,
                templateCatalog: templateCatalog,
                mcpSessionBridge: bridge,
                commandRunner: sp.GetService<IShellCommandRunner>(),
                timeProvider: sp.GetRequiredService<TimeProvider>(),
                systemUpgradeController: sp.GetRequiredService<ISystemUpgradeController>(),
                upgradeReceiptSource: sp.GetRequiredService<ISystemUpgradeReceiptSource>(),
                scheduleCronEvaluator: sp.GetRequiredService<IScheduleCronEvaluator>());
        });

        services.TryAddSingleton<KernelMcpBackend>();
        services.TryAddSingleton<VcsMcpBackend>();
        services.TryAddSingleton<McpPluginRouter>();
        services.TryAddSingleton<McpTransportSessionManager>();

        // Hosted lifecycle services (boot reconciler runs first)
        services.AddSingleton<PergaBootHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<PergaBootHostedService>());

        services.AddHostedService<UpgradeReceiptHostedService>();

        services.AddSingleton<CronosScheduleHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<CronosScheduleHostedService>());

        services.AddSingleton<SqliteMaintenanceHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SqliteMaintenanceHostedService>());

        return services;
    }

    internal static Perga.Kernel.Templates.ITemplateCatalog CreateResilientTemplateCatalog(
        IPolicyVm policyVm,
        string? templatesDirectory)
    {
        EnsurePolicyVmJitWarmedUp(policyVm);
        var catalog = new Perga.Kernel.Templates.TemplateCatalog(policyVm, initialTemplatesDirectory: null);
        if (string.IsNullOrWhiteSpace(templatesDirectory) || !Directory.Exists(templatesDirectory))
        {
            return catalog;
        }

        var files = Directory.GetFiles(templatesDirectory, "*.perga.js", SearchOption.TopDirectoryOnly)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToArray();

        foreach (var file in files)
        {
            var source = File.ReadAllText(file, System.Text.Encoding.UTF8);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    lock (PolicyVmWarmupLock)
                    {
                        catalog.RegisterTemplate(source);
                    }

                    break;
                }
                catch (InvalidOperationException) when (attempt < 2)
                {
                    // Retry once if OS thread preemption tripped Jint's 50ms wall-clock budget during startup.
                }
            }
        }

        return catalog;
    }

    private static void EnsurePolicyVmJitWarmedUp(IPolicyVm policyVm)
    {
        if (Volatile.Read(ref _policyVmWarmedUp))
        {
            return;
        }

        lock (PolicyVmWarmupLock)
        {
            if (_policyVmWarmedUp)
            {
                return;
            }

            const string warmupModule =
                """
                export default defineTemplate({
                  id: "_perga_jit_warmup",
                  title: "Warmup",
                  before() { return Allow(); },
                  schedules: [{ id: "w", cron: "* * * * *", prompt: "ok" }]
                });
                """;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    _ = policyVm.Compile(warmupModule);
                    break;
                }
                catch (InvalidOperationException) when (attempt < 2)
                {
                    // Cold CLR JIT across parallel test hosts can exceed Jint's 50ms budget on first pass; retry once JITted.
                }
            }

            Volatile.Write(ref _policyVmWarmedUp, true);
        }
    }
}
