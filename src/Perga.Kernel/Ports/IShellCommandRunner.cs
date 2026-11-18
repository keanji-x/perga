using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Perga.Kernel.Ports;

public sealed record ShellCommandRequest(
    string Command,
    string WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record ShellCommandResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    long DurationMs);

public interface IShellCommandRunner
{
    Task<ShellCommandResult> RunAsync(ShellCommandRequest request, CancellationToken ct = default);
}

public sealed class ProcessShellCommandRunner : IShellCommandRunner
{
    public async Task<ShellCommandResult> RunAsync(ShellCommandRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var psi = CreateStartInfo(request);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start shell command: {request.Command}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcessTree(process);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }

        sw.Stop();
        string outStr;
        string errStr;
        lock (stdout)
        {
            outStr = stdout.ToString();
        }

        lock (stderr)
        {
            errStr = stderr.ToString();
        }

        var exitCode = timedOut ? -1 : process.ExitCode;
        return new ShellCommandResult(exitCode, outStr, errStr, timedOut, sw.ElapsedMilliseconds);
    }

    private static ProcessStartInfo CreateStartInfo(ShellCommandRequest request)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            WorkingDirectory = Directory.Exists(request.WorkingDirectory)
                ? request.WorkingDirectory
                : Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isWindows)
        {
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(request.Command);
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(request.Command);
        }

        if (request.Environment is not null)
        {
            foreach (var kvp in request.Environment)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
        }

        return psi;
    }

    private static void TryKillProcessTree(Process process)
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
            // Best-effort cleanup on timeout or cancellation.
        }
    }
}
