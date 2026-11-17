using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Perga.TermClient;

/// <summary>
/// Options for launching a managed <c>perga-term-bridge</c> child daemon process.
/// </summary>
public sealed record TerminalBridgeDaemonOptions(
    string? ExecutablePath = null,
    string? ListenUnixSocketPath = null,
    string? ListenTcpAddress = "127.0.0.1:0",
    bool EnableLinuxUnshare = false,
    bool BuildFromSourceIfMissing = true,
    TimeSpan? StartupTimeout = null);

/// <summary>
/// Spawns, awaits readiness of, and supervises a local <c>perga-term-bridge</c> daemon process
/// so <c>Perga.Host</c> and integration tests can launch/connect over UDS or TCP effortlessly.
/// </summary>
public sealed class TerminalBridgeDaemon : IAsyncDisposable
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    private readonly Process _process;
    private readonly bool _ownsUnixSocketFile;
    private int _disposed;

    private TerminalBridgeDaemon(
        Process process,
        Uri? tcpEndpoint,
        string? unixSocketPath,
        bool ownsUnixSocketFile)
    {
        _process = process;
        TcpEndpoint = tcpEndpoint;
        UnixSocketPath = unixSocketPath;
        _ownsUnixSocketFile = ownsUnixSocketFile;
    }

    public int ProcessId => _process.Id;
    public Uri? TcpEndpoint { get; }
    public string? UnixSocketPath { get; }

    public Uri PreferredEndpointUri =>
        UnixSocketPath is not null
            ? TerminalBridgeClient.BuildUnixUri(UnixSocketPath)
            : TcpEndpoint ?? throw new InvalidOperationException("Daemon has neither UDS nor TCP endpoint.");

    public TerminalBridgeClient CreateClient() => new(PreferredEndpointUri);

    public TerminalBridgeClient CreateTcpClient() =>
        TcpEndpoint is not null
            ? TerminalBridgeClient.ForTcp(TcpEndpoint)
            : throw new InvalidOperationException("Daemon was started without --listen-tcp.");

    public TerminalBridgeClient CreateUnixSocketClient() =>
        UnixSocketPath is not null
            ? TerminalBridgeClient.ForUnixSocket(UnixSocketPath)
            : throw new InvalidOperationException("Daemon was started without --listen-uds.");

    /// <summary>
    /// Starts a managed <c>perga-term-bridge</c> process and returns an <see cref="ITerminalBridgeClient"/>
    /// that automatically disposes the daemon when the client is disposed.
    /// </summary>
    public static async Task<TerminalBridgeClient> StartManagedClientAsync(
        TerminalBridgeDaemonOptions? options = null,
        CancellationToken ct = default)
    {
        var daemon = await StartAsync(options, ct).ConfigureAwait(false);
        try
        {
            return new TerminalBridgeClient(daemon.PreferredEndpointUri, ownedResource: daemon);
        }
        catch
        {
            await daemon.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<TerminalBridgeDaemon> StartAsync(
        TerminalBridgeDaemonOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TerminalBridgeDaemonOptions();
        if (string.IsNullOrWhiteSpace(options.ListenUnixSocketPath)
            && string.IsNullOrWhiteSpace(options.ListenTcpAddress))
        {
            throw new ArgumentException(
                "At least one of ListenUnixSocketPath or ListenTcpAddress must be specified.",
                nameof(options));
        }

        var exePath = await ResolveOrBuildExecutableAsync(
                options.ExecutablePath,
                options.BuildFromSourceIfMissing,
                ct)
            .ConfigureAwait(false);

        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var expectUds = false;
        var expectTcp = false;
        string? configuredUds = null;

        if (!string.IsNullOrWhiteSpace(options.ListenUnixSocketPath))
        {
            configuredUds = Path.GetFullPath(options.ListenUnixSocketPath);
            var parent = Path.GetDirectoryName(configuredUds);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            psi.ArgumentList.Add("--listen-uds");
            psi.ArgumentList.Add(configuredUds);
            expectUds = true;
        }

        if (!string.IsNullOrWhiteSpace(options.ListenTcpAddress))
        {
            psi.ArgumentList.Add("--listen-tcp");
            psi.ArgumentList.Add(options.ListenTcpAddress);
            expectTcp = true;
        }

        if (options.EnableLinuxUnshare)
        {
            psi.ArgumentList.Add("--linux-unshare");
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderrBuffer = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderrBuffer)
                {
                    if (stderrBuffer.Count < 64)
                    {
                        stderrBuffer.Add(e.Data);
                    }
                }
            }
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new TerminalBridgeException($"Failed to start perga-term-bridge executable '{exePath}'.");
        }

        process.BeginErrorReadLine();

        var timeout = options.StartupTimeout ?? TimeSpan.FromSeconds(15);
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startupCts.CancelAfter(timeout);

        Uri? boundTcp = null;
        string? boundUds = null;

        try
        {
            while ((expectUds && boundUds is null) || (expectTcp && boundTcp is null))
            {
                var line = await process.StandardOutput.ReadLineAsync(startupCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    string stderr;
                    lock (stderrBuffer)
                    {
                        stderr = string.Join(Environment.NewLine, stderrBuffer);
                    }

                    throw new TerminalBridgeException(
                        $"perga-term-bridge exited prematurely before binding endpoints. Stderr: {stderr}");
                }

                var trimmed = line.Trim();
                if (trimmed.StartsWith("PERGA_TERM_BRIDGE_UDS=", StringComparison.Ordinal))
                {
                    boundUds = trimmed["PERGA_TERM_BRIDGE_UDS=".Length..];
                }
                else if (trimmed.StartsWith("PERGA_TERM_BRIDGE_TCP=", StringComparison.Ordinal))
                {
                    boundTcp = new Uri(trimmed["PERGA_TERM_BRIDGE_TCP=".Length..]);
                }
            }

            // Drain remaining stdout asynchronously so the child never blocks on pipe writes.
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is not null)
                    {
                    }
                }
                catch
                {
                    // Ignore pipe close on daemon shutdown.
                }
            }, CancellationToken.None);

            return new TerminalBridgeDaemon(process, boundTcp, boundUds, ownsUnixSocketFile: expectUds);
        }
        catch
        {
            TryKillProcess(process);
            process.Dispose();
            throw;
        }
    }

    public static async Task<string> ResolveOrBuildExecutableAsync(
        string? explicitPath = null,
        bool buildIfMissing = true,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"Configured perga-term-bridge binary not found at '{full}'.", full);
            }

            return full;
        }

        var envOverride = Environment.GetEnvironmentVariable("PERGA_TERM_BRIDGE_BIN");
        if (!string.IsNullOrWhiteSpace(envOverride) && File.Exists(envOverride))
        {
            return Path.GetFullPath(envOverride);
        }

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "perga-term-bridge.exe"
            : "perga-term-bridge";

        var adjacent = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(adjacent))
        {
            return adjacent;
        }

        var crateDir = FindBridgeCrateDirectory();
        if (crateDir is not null)
        {
            var releaseBin = Path.Combine(crateDir, "target", "release", exeName);
            if (File.Exists(releaseBin))
            {
                return releaseBin;
            }

            var debugBin = Path.Combine(crateDir, "target", "debug", exeName);
            if (File.Exists(debugBin))
            {
                return debugBin;
            }

            if (buildIfMissing)
            {
                await BuildLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (File.Exists(debugBin))
                    {
                        return debugBin;
                    }

                    await BuildBridgeCrateAsync(crateDir, ct).ConfigureAwait(false);
                    if (File.Exists(debugBin))
                    {
                        return debugBin;
                    }
                }
                finally
                {
                    BuildLock.Release();
                }
            }
        }

        throw new TerminalBridgeException(
            "Unable to locate perga-term-bridge executable. Set PERGA_TERM_BRIDGE_BIN or build tools/perga-term-bridge.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                TryKillProcess(_process);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch
        {
            // Ignore shutdown races if the child already terminated.
        }
        finally
        {
            _process.Dispose();
            if (_ownsUnixSocketFile && UnixSocketPath is not null && File.Exists(UnixSocketPath))
            {
                try
                {
                    File.Delete(UnixSocketPath);
                }
                catch
                {
                }
            }
        }
    }

    private static string? FindBridgeCrateDirectory()
    {
        foreach (var startDir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(startDir))
            {
                continue;
            }

            var current = new DirectoryInfo(startDir);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "tools", "perga-term-bridge");
                if (File.Exists(Path.Combine(candidate, "Cargo.toml")))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private static async Task BuildBridgeCrateAsync(string crateDir, CancellationToken ct)
    {
        var cargoExe = ResolveCargoExecutable();
        var psi = new ProcessStartInfo(cargoExe)
        {
            WorkingDirectory = crateDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("--bin");
        psi.ArgumentList.Add("perga-term-bridge");
        psi.ArgumentList.Add("--offline");
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add("build.rustc-wrapper=\"\"");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var cargoBin = Path.Combine(home, ".cargo", "bin");
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            psi.Environment["PATH"] = $"{cargoBin}{Path.PathSeparator}{existingPath}";
        }

        using var proc = Process.Start(psi)
            ?? throw new TerminalBridgeException("Failed to start cargo build for perga-term-bridge.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            throw new TerminalBridgeException(
                $"cargo build for perga-term-bridge failed with exit code {proc.ExitCode}: {stderr}");
        }
    }

    private static string ResolveCargoExecutable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cargo.exe" : "cargo";
            var userCargo = Path.Combine(home, ".cargo", "bin", exeName);
            if (File.Exists(userCargo))
            {
                return userCargo;
            }
        }

        return "cargo";
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
