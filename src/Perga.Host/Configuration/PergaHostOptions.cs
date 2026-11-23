using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;
using Perga.Host.Http;
using Perga.Kernel;

namespace Perga.Host.Configuration;

public sealed class ExecutionTargetOptions
{
    public string Kind { get; set; } = "ssh";
    public string? Label { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 22;
    public string? User { get; set; }
    public string? IdentityFile { get; set; }
    public string? KnownHostsFile { get; set; }
    public string StrictHostKeyChecking { get; set; } = "accept-new";
    public string? WorkspacesRoot { get; set; }
    public string? TerminalBridgeExecutablePath { get; set; }
    public string? OarNodeExecutablePath { get; set; }
    public string? OarBridgeScriptPath { get; set; }
    public string? McpBaseUriOverride { get; set; }
    public string? SshExecutablePath { get; set; }
    public Dictionary<string, string> ExtraEnv { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ProviderExecutables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum AuthIngressMode { Direct, Proxied }
public enum AuthCookieSecurePolicy { Auto, Always }

public sealed class PergaHostOptions
{
    public const string SectionName = "Perga";

    public string[] AllowedOrigins { get; set; } = [];
    public string[] AllowedHosts { get; set; } = [];

    public AuthIngressMode AuthIngressMode { get; set; } = AuthIngressMode.Direct;
    public AuthCookieSecurePolicy AuthCookieSecurePolicy { get; set; } = AuthCookieSecurePolicy.Auto;
    public int AuthSessionLifetimeDays { get; set; } = 30;
    public string? AuthPublicOrigin { get; set; }
    public bool TrustForwardedHeaders { get; set; }
    public string[] TrustedProxyAddresses { get; set; } = [];
    public TimeSpan AuthWsRevalidationInterval { get; set; } = TimeSpan.FromSeconds(15);

    public void ValidateAuth()
    {
        if (!Enum.IsDefined(AuthIngressMode) || !Enum.IsDefined(AuthCookieSecurePolicy))
            throw new InvalidOperationException("Invalid auth ingress or cookie security policy.");
        if (AuthSessionLifetimeDays is < 1 or > 365)
            throw new InvalidOperationException("AuthSessionLifetimeDays must be between 1 and 365.");
        if (AuthWsRevalidationInterval < TimeSpan.FromSeconds(1) || AuthWsRevalidationInterval > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("AuthWsRevalidationInterval must be between one second and five minutes.");
        if (AuthPublicOrigin is not null &&
            (!RequestOriginGuard.TryOrigin(AuthPublicOrigin, out var origin) || !origin.StartsWith("https://", StringComparison.Ordinal)))
            throw new InvalidOperationException("AuthPublicOrigin must be an HTTPS origin.");
        if (AuthIngressMode == AuthIngressMode.Proxied && AuthPublicOrigin is null && AllowedHosts.Length == 0)
            throw new InvalidOperationException("Proxied ingress requires AuthPublicOrigin or AllowedHosts.");
        if (TrustForwardedHeaders && AuthIngressMode != AuthIngressMode.Proxied)
            throw new InvalidOperationException("TrustForwardedHeaders requires proxied ingress.");
        if (TrustedProxyAddresses.Any(address => !IPAddress.TryParse(address, out _)))
            throw new InvalidOperationException("TrustedProxyAddresses entries must be IP literals.");
    }

    public string? InstallationRoot { get; set; }
    public string InstallationName { get; set; } = "perga";
    public string? BridgeBuildId { get; set; }
    public string? SystemManagementTokenSha256 { get; set; }
    public Dictionary<string, SystemUpgrade.UpgradeSourceOptions> UpgradeSources { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ExecutionTargetOptions> ExecutionTargets { get; set; } = new(StringComparer.Ordinal);
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
    public string DefaultWorkspaceDriver { get; set; } = "direct";
    public bool DispatchWakesInBackground { get; set; } = true;
    public bool EnableTemplateSelectorSubagent { get; set; } = true;

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
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SessionCompactionTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan ProviderCancelDispatchGrace { get; set; } = KernelOptions.DefaultProviderCancelDispatchGrace;
    public TimeSpan ProviderTerminalStopGrace { get; set; } = KernelOptions.DefaultProviderTerminalStopGrace;

    public bool CleanupEphemeralDataOnShutdown { get; set; } = true;

    public static PergaHostOptions BindFrom(IConfiguration configuration, string contentRootPath)
    {
        var opts = new PergaHostOptions();
        var section = configuration.GetSection(SectionName);
        section.Bind(opts);
        if (section[nameof(AuthIngressMode)] is null && configuration["PERGA_AUTH_INGRESS_MODE"] is { } ingress)
            opts.AuthIngressMode = Enum.Parse<AuthIngressMode>(ingress, ignoreCase: true);
        if (section[nameof(AuthCookieSecurePolicy)] is null && configuration["PERGA_AUTH_COOKIE_SECURE"] is { } secure)
            opts.AuthCookieSecurePolicy = Enum.Parse<AuthCookieSecurePolicy>(secure, ignoreCase: true);
        if (section[nameof(AuthSessionLifetimeDays)] is null && configuration["PERGA_AUTH_SESSION_DAYS"] is { } days)
            opts.AuthSessionLifetimeDays = int.Parse(days, CultureInfo.InvariantCulture);
        if (section[nameof(AuthPublicOrigin)] is null)
            opts.AuthPublicOrigin = configuration["PERGA_AUTH_PUBLIC_ORIGIN"];
        if (section[nameof(TrustForwardedHeaders)] is null && configuration["PERGA_TRUST_FORWARDED_HEADERS"] is { } forwarded)
            opts.TrustForwardedHeaders = bool.Parse(forwarded);

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
