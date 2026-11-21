using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Perga.Processes;

namespace Perga.Kernel.Ports;

public sealed record ShellCommandRequest(
    string Command,
    string WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string>? Environment = null,
    int? OutputTailChars = null);

public sealed record ShellCommandResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    long DurationMs,
    bool StdoutTruncated = false,
    bool StderrTruncated = false);

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

        var tailChars = request.OutputTailChars ?? int.MaxValue;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailChars);
        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start shell command: {request.Command}");
        }

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var captureStdout = CaptureAsync(process.StandardOutput, stdout, tailChars, linkedCts.Token);
        var captureStderr = CaptureAsync(process.StandardError, stderr, tailChars, linkedCts.Token);
        var exit = process.WaitForExitAsync(linkedCts.Token);
        var completion = Task.WhenAll(exit, captureStdout, captureStderr);
        var timedOut = false;
        try
        {
            await completion.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            linkedCts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await Task.WhenAll(captureStdout, captureStderr).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            timedOut = true;
        }

        var exitCode = timedOut ? -1 : process.ExitCode;
        return new ShellCommandResult(exitCode, stdout.ToString(), stderr.ToString(), timedOut, sw.ElapsedMilliseconds,
            captureStdout.Result, captureStderr.Result);
    }

    private static async Task<bool> CaptureAsync(StreamReader reader, StringBuilder output, int limit, CancellationToken ct)
    {
        var buffer = new char[4096];
        var truncated = false;
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (count == 0)
                {
                    return truncated;
                }

                output.Append(buffer, 0, count);
                if (output.Length > limit)
                {
                    output.Remove(0, output.Length - limit);
                    if (char.IsLowSurrogate(output[0]))
                    {
                        output.Remove(0, 1);
                    }

                    truncated = true;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Preserve the captured tail for timeout diagnostics.
        }

        return truncated;
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

        ChildProcessEnvironment.Apply(psi, ChildProcessEnvironment.Capture());
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
