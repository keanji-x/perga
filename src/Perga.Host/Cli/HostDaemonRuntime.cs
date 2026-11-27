using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Console;
using Perga.Host.Configuration;

namespace Perga.Host.Cli;

internal sealed class HostDaemonRuntime(string statePath, string logPath, string version, string installDir)
{
    private const string ChildFlag = "PERGA_DAEMON_CHILD";
    private const string StatePath = "PERGA_DAEMON_STATE_PATH";
    private const string LogPath = "PERGA_DAEMON_LOG_PATH";
    private const string Version = "PERGA_DAEMON_VERSION";
    private const string RequestDiagnostics = "Microsoft.AspNetCore.Hosting.Diagnostics";

    private HostDaemonState? _state;

    public static HostDaemonRuntime? FromProcess()
    {
        if (Environment.GetEnvironmentVariable(ChildFlag) != "1")
            return null;

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                if (setsid() < 0)
                    throw new IOException($"Could not detach Perga from the terminal (errno {Marshal.GetLastPInvokeError()}).");
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new IOException("Could not detach Perga from the terminal.", ex);
            }
        }

        var runtime = new HostDaemonRuntime(RequiredVariable(StatePath), RequiredVariable(LogPath), RequiredVariable(Version),
            RequiredVariable("PERGA_INSTALL_DIR"));
        foreach (var variable in new[] { ChildFlag, StatePath, LogPath, Version })
            Environment.SetEnvironmentVariable(variable, null);
        return runtime;
    }

    private static string RequiredVariable(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new IOException($"Missing daemon runtime variable {name}.");

    public static ProcessStartInfo ChildStartInfo(
        HostCliEnvironment env, HostCliCommand.InstallationPaths paths, string statePath, string logPath, string version, string[] hostArgs)
    {
        var executable = Path.Combine(paths.InstallDir, "Perga.Host");
        var arguments = hostArgs;
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" &&
            paths.InstallDir == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))
        {
            executable = Environment.ProcessPath!;
            arguments = [typeof(HostDaemonRuntime).Assembly.Location, .. hostArgs];
        }

        // The shell execs the host in place, keeping the PID and redirecting all
        // standard streams without creating pipes owned by the launcher.
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            WorkingDirectory = paths.InstallDir
        };
        foreach (var arg in new[] { "-c", "exec \"$@\" </dev/null >>\"$0\" 2>&1", logPath, executable })
            start.ArgumentList.Add(arg);
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var (key, value) in env.Variables)
            start.Environment[key] = value;
        start.Environment["PERGA_INSTALL_DIR"] = paths.InstallDir;
        start.Environment["PERGA_DATA_DIR"] = paths.DataDir;
        if (paths.ExplicitDbPath)
            start.Environment["PERGA_DB_PATH"] = paths.DbPath;
        start.Environment[ChildFlag] = "1";
        start.Environment[StatePath] = statePath;
        start.Environment[LogPath] = logPath;
        start.Environment[Version] = version;
        return start;
    }

    public void Publish(WebApplication app, ResolvedHostStoragePaths storage, string[] hostArgs)
    {
        using var process = Process.GetCurrentProcess();
        var url = ListeningUrl(app) ?? throw new IOException("Perga started without an HTTP or HTTPS listener.");
        _state = new HostDaemonState(process.Id, process.StartTime.ToUniversalTime().Ticks, process.ProcessName,
            url, version, installDir, storage.DataDir, storage.DbPath, logPath, hostArgs);
        _state.Write(statePath);
    }

    public void RemoveState() => _state?.RemoveIfOwned(statePath);

    public static void Configure(WebApplicationBuilder builder, HostDaemonOptions? foreground)
    {
        var config = builder.Configuration;
        if (string.IsNullOrWhiteSpace(config[WebHostDefaults.ServerUrlsKey]) &&
            string.IsNullOrWhiteSpace(config["HTTP_PORTS"]) && string.IsNullOrWhiteSpace(config["HTTPS_PORTS"]) &&
            !config.GetSection("Kestrel:Endpoints").Exists())
            config[WebHostDefaults.ServerUrlsKey] = config["PERGA_URLS"] ?? "http://127.0.0.1:4041";

        var logging = builder.Logging;
        if (foreground is { Verbose: false })
        {
            logging.AddFilter<ConsoleLoggerProvider>("Microsoft", LogLevel.Warning);
            logging.AddFilter<ConsoleLoggerProvider>("System", LogLevel.Warning);
        }

        // The framework's request start/finish messages contain the query string.
        logging.AddFilter<ConsoleLoggerProvider>(RequestDiagnostics, LogLevel.Warning);
    }

    public static string? ListeningUrl(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var urls = addresses?.Select(LocalUrl).OfType<string>().ToArray() ?? [];
        return urls.FirstOrDefault(url => new Uri(url).IsLoopback) ?? urls.FirstOrDefault();
    }

    private static string? LocalUrl(string address)
    {
        var separator = address.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
            return null;
        var authority = address[(separator + 3)..];
        if (authority.StartsWith("*:", StringComparison.Ordinal) || authority.StartsWith("+:", StringComparison.Ordinal))
            address = address[..(separator + 3)] + "127.0.0.1" + authority[1..];

        if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            return null;

        if (url.Host is "0.0.0.0" or "[::]")
            url = new UriBuilder(url) { Host = "127.0.0.1" }.Uri;
        return url.AbsoluteUri;
    }

    public static void PrintForegroundBanner(WebApplication app, HostDaemonOptions options)
    {
        if (ListeningUrl(app) is not { } url)
            return;

        var env = HostCliEnvironment.FromProcess();
        var paths = HostCliCommand.InstallationPaths.From(env);
        var version = HostCliCommand.ReadVersion(paths.InstallDir) ?? "unknown";
        var storage = app.Services.GetRequiredService<ResolvedHostStoragePaths>();
        env.Output.WriteLine($"Perga {version} is running");
        env.Output.WriteLine($"  Local:  {url}");
        env.Output.WriteLine("  Logs:   terminal");
        env.Output.WriteLine($"  PID:    {Environment.ProcessId}  (Press Ctrl+C to stop.)");
        HostDaemonBrowser.TryOpenBrowser(env, url, storage.DataDir, options.NoOpen);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();
}
