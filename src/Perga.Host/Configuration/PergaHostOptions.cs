using Microsoft.Extensions.Configuration;
using Perga.Kernel;

namespace Perga.Host.Configuration;

public sealed class PergaHostOptions
{
    public const string SectionName = "Perga";

    public string? InstallationRoot { get; set; }
    public string InstallationName { get; set; } = "perga";
    public string? BridgeBuildId { get; set; }
    public string? SystemManagementTokenSha256 { get; set; }
    public Dictionary<string, SystemUpgrade.UpgradeSourceOptions> UpgradeSources { get; set; } = new(StringComparer.Ordinal);
    public string Version { get; set; } = "1.0.0";
    public string? DataDir { get; set; }
    public string? DbPath { get; set; }
    public string? TemplatesDirectory { get; set; }
    public string? WorkspacesRootDirectory { get; set; }
    public string? OverlayWorkspaceWarmCommand { get; set; }
    public string? OverlayWorkspaceCacheVersion { get; set; }
    public string? StaticWebRootDirectory { get; set; }
    public string? McpPluginsConfigPath { get; set; }
    public string? McpBaseUri { get; set; }

    public string DefaultProvider { get; set; } = "codex";
    public string? DefaultModel { get; set; }
    public Dictionary<string, string> ProviderExecutables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? OarNodeExecutablePath { get; set; }
    public string? OarBridgeScriptPath { get; set; }
    public string? WorkerApprovalAuthorizationSha256 { get; set; }
    public string DefaultWorkspaceDriver { get; set; } = "direct";
    public bool DispatchWakesInBackground { get; set; } = true;

    public string? TerminalBridgeEndpoint { get; set; }
    public string? TerminalBridgeExecutablePath { get; set; }
    public bool AutoManageTerminalBridge { get; set; } = true;
    public bool EagerStartTerminalBridge { get; set; } = false;
    public bool EnableTerminalLinuxUnshare { get; set; } = false;

    public TimeSpan SchedulePollInterval { get; set; } = TimeSpan.FromSeconds(15);
    public bool EnableScheduleService { get; set; } = true;

    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromHours(6);
    public bool EnableMaintenanceService { get; set; } = true;

    public TimeSpan SessionStartupTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SessionCompactionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan ProviderCancelDispatchGrace { get; set; } = KernelOptions.DefaultProviderCancelDispatchGrace;
    public TimeSpan ProviderTerminalStopGrace { get; set; } = KernelOptions.DefaultProviderTerminalStopGrace;

    public bool CleanupEphemeralDataOnShutdown { get; set; } = true;

    public static PergaHostOptions BindFrom(IConfiguration configuration, string contentRootPath)
    {
        var opts = new PergaHostOptions();
        var section = configuration.GetSection(SectionName);
        section.Bind(opts);

        if (string.IsNullOrWhiteSpace(opts.DataDir))
        {
            opts.DataDir = section["DataDirectory"]
                           ?? Environment.GetEnvironmentVariable("PERGA_DATA_DIR");
        }

        if (string.IsNullOrWhiteSpace(opts.DbPath))
        {
            opts.DbPath = Environment.GetEnvironmentVariable("PERGA_DB_PATH");
        }

        if (string.IsNullOrWhiteSpace(opts.TemplatesDirectory))
        {
            opts.TemplatesDirectory = Environment.GetEnvironmentVariable("PERGA_TEMPLATES_DIR")
                                      ?? DiscoverDirectoryUpwards(contentRootPath, "templates")
                                      ?? DiscoverDirectoryUpwards(AppContext.BaseDirectory, "templates");
        }

        if (string.IsNullOrWhiteSpace(opts.StaticWebRootDirectory))
        {
            opts.StaticWebRootDirectory = section["FrontendDistDirectory"]
                                          ?? Environment.GetEnvironmentVariable("PERGA_STATIC_WEB_DIR")
                                          ?? DiscoverDirectoryUpwards(contentRootPath, Path.Combine("fe", "web", "dist"))
                                          ?? DiscoverDirectoryUpwards(AppContext.BaseDirectory, Path.Combine("fe", "web", "dist"));
        }

        if (string.IsNullOrWhiteSpace(opts.McpPluginsConfigPath))
        {
            opts.McpPluginsConfigPath = Environment.GetEnvironmentVariable("PERGA_MCP_PLUGINS_PATH")
                                        ?? DiscoverFileUpwards(contentRootPath, "mcp-plugins.json");
        }

        if (string.IsNullOrWhiteSpace(opts.TerminalBridgeEndpoint))
        {
            opts.TerminalBridgeEndpoint = Environment.GetEnvironmentVariable("PERGA_TERM_BRIDGE_URL");
        }

        if (string.IsNullOrWhiteSpace(opts.TerminalBridgeExecutablePath))
        {
            opts.TerminalBridgeExecutablePath = section["TerminalBridgeBinaryPath"]
                                                ?? Environment.GetEnvironmentVariable("PERGA_TERM_BRIDGE_BIN");
        }

        if (string.IsNullOrWhiteSpace(opts.McpBaseUri))
        {
            opts.McpBaseUri = Environment.GetEnvironmentVariable("PERGA_MCP_BASE_URI");
        }

        if (string.IsNullOrWhiteSpace(opts.OarNodeExecutablePath))
        {
            opts.OarNodeExecutablePath = Environment.GetEnvironmentVariable("PERGA_OAR_NODE_BIN");
        }

        if (string.IsNullOrWhiteSpace(opts.OarBridgeScriptPath))
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "oar-bridge", "perga-oar-bridge.mjs");
            opts.OarBridgeScriptPath = Environment.GetEnvironmentVariable("PERGA_OAR_BRIDGE_SCRIPT")
                ?? (File.Exists(bundled) ? bundled : null)
                ?? DiscoverFileUpwards(contentRootPath, Path.Combine("tools", "perga-oar-bridge", "dist", "perga-oar-bridge.mjs"))
                ?? DiscoverFileUpwards(AppContext.BaseDirectory, Path.Combine("tools", "perga-oar-bridge", "dist", "perga-oar-bridge.mjs"));
        }

        opts.InstallationRoot ??= Environment.GetEnvironmentVariable("PERGA_INSTALL_ROOT");
        if (opts.InstallationRoot is null && opts.StaticWebRootDirectory is { } web &&
            Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(web))) == "current")
            opts.InstallationRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(web))!);
        opts.InstallationName = section["InstallationName"] ?? Environment.GetEnvironmentVariable("PERGA_INSTANCE_NAME") ?? "perga";
        opts.BridgeBuildId ??= Environment.GetEnvironmentVariable("PERGA_TERM_BRIDGE_BUILD_ID");
        return opts;
    }

    public (string ResolvedDataDir, string ResolvedDbPath, string ResolvedWorkspacesDir, bool IsEphemeral) ResolveStoragePaths()
    {
        var isEphemeral = string.IsNullOrWhiteSpace(DataDir) && string.IsNullOrWhiteSpace(DbPath);
        string resolvedDataDir;
        string resolvedDbPath;

        if (!string.IsNullOrWhiteSpace(DbPath))
        {
            resolvedDbPath = Path.GetFullPath(DbPath);
            resolvedDataDir = !string.IsNullOrWhiteSpace(DataDir)
                ? Path.GetFullPath(DataDir)
                : Path.GetDirectoryName(resolvedDbPath) ?? Path.Combine(Path.GetTempPath(), "perga-host");
        }
        else if (!string.IsNullOrWhiteSpace(DataDir))
        {
            resolvedDataDir = Path.GetFullPath(DataDir);
            resolvedDbPath = Path.Combine(resolvedDataDir, "perga.db");
        }
        else
        {
            resolvedDataDir = Path.Combine(Path.GetTempPath(), "perga-host", $"inst_{Guid.NewGuid():N}");
            resolvedDbPath = Path.Combine(resolvedDataDir, "perga.db");
        }

        var resolvedWorkspaces = !string.IsNullOrWhiteSpace(WorkspacesRootDirectory)
            ? Path.GetFullPath(WorkspacesRootDirectory)
            : Path.Combine(resolvedDataDir, "workspaces");

        return (resolvedDataDir, resolvedDbPath, resolvedWorkspaces, isEphemeral);
    }

    private static string? DiscoverDirectoryUpwards(string? startDirectory, string relativeSubpath)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativeSubpath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? DiscoverFileUpwards(string? startDirectory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }
}
