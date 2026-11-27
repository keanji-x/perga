using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using UsageException = Perga.Host.Cli.HostCliCommand.UsageException;

namespace Perga.Host.Cli;

internal static class HostDaemonCommand
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private const long MaxLogBytes = 10 * 1024 * 1024;

    public static bool Handles(string command) => command is "start" or "serve" or "status" or "stop" or "restart" or "logs" or "open";

    public static HostDaemonOptions? ForegroundOptions(string[] args)
    {
        if (args is not ["start" or "serve", .. var rest] || HostDaemonOptions.IsHelp(rest))
            return null;
        var options = HostDaemonOptions.Parse(rest);
        return args[0] == "serve" || options.Foreground ? options : null;
    }

    public static async Task<int> RunWithConsoleCancellationAsync(string[] args, HostCliEnvironment env)
    {
        using var stopping = new CancellationTokenSource();
        void Cancel(object? _, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stopping.Cancel();
        }

        Console.CancelKeyPress += Cancel;
        try
        {
            return await HostCliCommand.RunAsync(args, env, stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return args[0] == "logs" ? 0 : 130;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }
    }

    public static async Task<int> RunAsync(string command, string[] args, HostCliEnvironment env, CancellationToken ct)
    {
        var usage = command switch
        {
            "start" => "Usage: perga start [--foreground|-f] [--no-open] [--verbose] [host args...]",
            "serve" => "Usage: perga serve [--no-open] [--verbose] [host args...]",
            "restart" => "Usage: perga restart [--no-open] [--verbose] [host args...]",
            "logs" => "Usage: perga logs [-f|--follow] [-n|--lines <N>]",
            _ => $"Usage: perga {command}"
        };
        if (HostDaemonOptions.IsHelp(args))
        {
            env.Output.WriteLine(usage);
            return 0;
        }

        var paths = HostCliCommand.InstallationPaths.From(env);
        if (command == "logs")
        {
            var (count, follow) = ParseLogOptions(args, usage);
            return await HostDaemonLogs.RunAsync(HostDaemonState.LogPathIn(paths.DataDir), env.Output, count, follow, ct).ConfigureAwait(false);
        }
        if (command is "status" or "stop" or "open")
        {
            if (args.Length != 0)
                throw new UsageException($"unexpected argument '{args[0]}'.", usage);
            if (!Directory.Exists(paths.DataDir))
                return ReportStopped(command, env, stale: false);
            using var held = await AcquireLockAsync(paths.DataDir, ct).ConfigureAwait(false);
            return await HandleExistingAsync(command, env, paths.DataDir, ct).ConfigureAwait(false);
        }

        var options = HostDaemonOptions.Parse(args);
        using var startupLock = await AcquireLockAsync(paths.DataDir, ct).ConfigureAwait(false);
        var previous = TryGetRunningInstance(paths.DataDir, out _);
        if (command == "restart" && previous is not null)
        {
            options = options.WithPreviousArguments(previous.HostArgs);
            await StopInstanceAsync(paths.DataDir, previous, ct).ConfigureAwait(false);
            previous = null;
        }
        return await StartBackgroundAsync(env, paths, options, previous, ct).ConfigureAwait(false);
    }

    public static async Task<FileStream> AcquireLockAsync(string dataDir, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDir);
        var lockPath = Path.Combine(dataDir, "host.lifecycle.lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Never unlink the lock file: another command could still have its inode open.
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (IsLockContention(ex))
            {
                if (elapsed.Elapsed >= StartTimeout + StopTimeout)
                    throw new IOException("Another Perga lifecycle command is still running.", ex);
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsLockContention(IOException ex) => OperatingSystem.IsWindows()
        ? (ex.HResult & 0xffff) is 32 or 33
        : ex.HResult is 11 or 35; // EAGAIN / EWOULDBLOCK on Linux and macOS.

    public static HostDaemonState? TryGetRunningInstance(string dataDir, out bool stale)
    {
        stale = false;
        var statePath = HostDaemonState.PathIn(dataDir);
        if (HostDaemonState.Read(statePath) is not { } state)
            return null;
        using var process = state.GetOwnedProcess();
        if (process is not null)
            return state;

        state.RemoveIfOwned(statePath);
        stale = true;
        return null;
    }

    private static async Task<int> HandleExistingAsync(string command, HostCliEnvironment env, string dataDir, CancellationToken ct)
    {
        var state = TryGetRunningInstance(dataDir, out var stale);
        if (state is null)
            return ReportStopped(command, env, stale);

        if (command == "stop")
        {
            if (!await StopInstanceAsync(dataDir, state, ct).ConfigureAwait(false))
                return ReportStopped(command, env, stale: false);
            env.Output.WriteLine($"Stopped Perga (PID {state.Pid}).");
        }
        else if (command == "open")
        {
            if (HostDaemonBrowser.TryOpenBrowser(env, state.Url, state.DataDir, noOpen: false, explicitRequest: true))
                env.Output.WriteLine(state.Url);
            else
                env.Output.WriteLine($"Open {state.Url} in your browser.");
        }
        else
        {
            env.Output.WriteLine($"Perga {state.Version} is running");
            env.Output.WriteLine($"  PID:     {state.Pid}");
            env.Output.WriteLine($"  Local:   {state.Url}");
            env.Output.WriteLine($"  Data:    {state.DataDir}");
            env.Output.WriteLine($"  Logs:    {state.LogPath}");
        }
        return 0;
    }

    private static int ReportStopped(string command, HostCliEnvironment env, bool stale)
    {
        if (command == "stop")
        {
            env.Output.WriteLine("Perga is not running.");
            return 0;
        }

        if (command == "open")
            env.Error.WriteLine("error: Perga is not running. Start it with 'perga'.");
        else
            env.Output.WriteLine(stale ? "Perga is stopped (removed stale daemon state)." : "Perga is stopped.");
        return 1;
    }

    public static async Task<bool> StopInstanceAsync(string dataDir, HostDaemonState state, CancellationToken ct)
    {
        using var process = state.GetOwnedProcess();
        var statePath = HostDaemonState.PathIn(dataDir);
        if (process is null)
        {
            state.RemoveIfOwned(statePath);
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            try { process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        else if (!HostDaemonState.HasExited(process) &&
                 kill(process.Id, 15) != 0 && Marshal.GetLastPInvokeError() is var error && error != 3) // ESRCH
        {
            throw new IOException($"Could not stop Perga (PID {process.Id}, errno {error}).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StopTimeout);
        try
        {
            while (!HostDaemonState.HasExited(process))
                await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"Perga (PID {process.Id}) did not stop within {StopTimeout.TotalSeconds:g} seconds.");
        }

        state.RemoveIfOwned(statePath);
        return true;
    }

    private static async Task<int> StartBackgroundAsync(
        HostCliEnvironment env, HostCliCommand.InstallationPaths paths, HostDaemonOptions options, HostDaemonState? previous, CancellationToken ct)
    {
        if (previous is not null)
        {
            PrintReady(env, previous, alreadyRunning: true);
            var requestedUrls = HostDaemonOptions.RequestedUrls(options.HostArgs);
            var currentUrls = HostDaemonOptions.RequestedUrls(previous.HostArgs) ?? previous.Url;
            if (requestedUrls is not null &&
                !string.Equals(requestedUrls.TrimEnd('/'), currentUrls.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                env.Output.WriteLine("A different URL was requested. Use 'perga restart --urls <url>' to change it.");
            HostDaemonBrowser.TryOpenBrowser(env, previous.Url, previous.DataDir, options.NoOpen);
            return 0;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new IOException("Background mode is available on macOS and Linux. Use 'perga --foreground' to run in this terminal.");

        var logPath = HostDaemonState.LogPathIn(paths.DataDir);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        if (new FileInfo(logPath) is { Exists: true, Length: > MaxLogBytes })
            File.Move(logPath, logPath + ".1", overwrite: true);
        long offset;
        using (var log = new FileStream(logPath, HostDaemonState.PrivateFileOptions(FileMode.Append)))
            offset = log.Length;

        var statePath = HostDaemonState.PathIn(paths.DataDir);
        var version = HostCliCommand.ReadVersion(paths.InstallDir) ?? "unknown";
        using var child = Process.Start(HostDaemonRuntime.ChildStartInfo(env, paths, statePath, logPath, version, options.HostArgs))
            ?? throw new IOException("Could not start the Perga Host.");
        var ready = false;
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(1)
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(StartTimeout);
            try
            {
                while (true)
                {
                    if (child.HasExited)
                    {
                        await HostDaemonLogs.WriteTailAsync(logPath, env.Error, 40, offset, ct).ConfigureAwait(false);
                        await env.Error.WriteLineAsync($"error: Perga failed to start (exit code {child.ExitCode}). See {logPath}.").ConfigureAwait(false);
                        return 1;
                    }

                    if (HostDaemonState.Read(statePath) is { } current && current.Pid == child.Id &&
                        await IsReadyAsync(client, current.Url, timeout.Token).ConfigureAwait(false))
                    {
                        ready = true;
                        PrintReady(env, current, alreadyRunning: false);
                        HostDaemonBrowser.TryOpenBrowser(env, current.Url, current.DataDir, options.NoOpen);
                        return 0;
                    }

                    await Task.Delay(PollInterval, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await HostDaemonLogs.WriteTailAsync(logPath, env.Error, 40, offset, ct).ConfigureAwait(false);
                await env.Error.WriteLineAsync($"error: Perga did not become ready within {StartTimeout.TotalSeconds:g} seconds. See {logPath}.").ConfigureAwait(false);
                return 1;
            }
        }
        finally
        {
            if (!ready)
                await CleanUpFailedStartAsync(child, statePath).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsReadyAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(new Uri(new Uri(url), "/healthz"),
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    private static async Task CleanUpFailedStartAsync(Process child, string statePath)
    {
        if (!child.HasExited)
        {
            // This is the process we spawned and still own, even if it never published state.
            kill(child.Id, 15);
            using var timeout = new CancellationTokenSource(StopTimeout);
            try
            {
                await child.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { child.Kill(); }
                catch (InvalidOperationException) when (child.HasExited) { }
                await child.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        if (HostDaemonState.Read(statePath) is { } state && state.Pid == child.Id)
            state.RemoveIfOwned(statePath);
    }

    private static void PrintReady(HostCliEnvironment env, HostDaemonState state, bool alreadyRunning)
    {
        env.Output.WriteLine($"Perga {state.Version} is {(alreadyRunning ? "already running" : "running")}");
        env.Output.WriteLine($"  Local:  {state.Url}");
        env.Output.WriteLine($"  Logs:   {state.LogPath}");
        env.Output.WriteLine($"  PID:    {state.Pid}  (perga status | perga logs -f | perga stop)");
    }

    private static (int Count, bool Follow) ParseLogOptions(string[] args, string usage)
    {
        var count = 50;
        var follow = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-f" or "--follow":
                    follow = true;
                    break;
                case "-n" or "--lines":
                    if (++index >= args.Length || !int.TryParse(args[index], out count) || count < 0)
                        throw new UsageException("expected a non-negative line count.", usage);
                    break;
                default:
                    throw new UsageException($"unexpected argument '{args[index]}'.", usage);
            }
        }
        return (count, follow);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
