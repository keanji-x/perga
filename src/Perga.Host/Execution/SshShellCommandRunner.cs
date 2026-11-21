using System.Diagnostics;
using System.Text;
using Perga.Kernel.Ports;

namespace Perga.Host.Execution;

public sealed class SshShellCommandRunner : IShellCommandRunner
{
    private readonly SshTargetConfig _config;

    public SshShellCommandRunner(SshTargetConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<ShellCommandResult> RunAsync(ShellCommandRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();
        var tailChars = request.OutputTailChars ?? int.MaxValue;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailChars);

        var runToken = Guid.NewGuid().ToString("N")[..12];
        var remotePidFile = $"{_config.WorkspacesRoot.TrimEnd('/')}/.perga-runtime/pids/cmd-{runToken}.pid";

        IReadOnlyDictionary<string, string?>? extraEnv = null;
        if (request.Environment is { Count: > 0 })
        {
            var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var kv in request.Environment)
            {
                dict[kv.Key] = kv.Value;
            }

            extraEnv = dict;
        }

        var workingDirScript = !string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? $"if [ -d {SshCommandBuilder.QuotePosix(request.WorkingDirectory)} ]; then cd {SshCommandBuilder.QuotePosix(request.WorkingDirectory)}; else cd /tmp; fi\n"
            : "cd /tmp\n";

        var body = workingDirScript + SshCommandBuilder.BuildTrackedCommand(
            _config, remotePidFile, $"sh -c {SshCommandBuilder.QuotePosix(request.Command)}");

        var remoteScript = SshCommandBuilder.BuildRemoteScript(
            workingDirectory: null,
            extraEnv: extraEnv,
            targetDefaultEnv: _config.ExtraEnv,
            body: body);

        var sshArgs = SshCommandBuilder.BuildSshExecArguments(_config, remoteScript);
        var psi = SshCommandBuilder.CreateSshStartInfo(_config, sshArgs, redirectStdin: false);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start remote shell command on target '{_config.TargetId}'.");
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
            await SshCommandBuilder.TryTerminateTrackedProcessAsync(_config, remotePidFile).ConfigureAwait(false);
            SshCommandBuilder.TryKill(process);
            await Task.WhenAll(captureStdout, captureStderr).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            timedOut = true;
        }

        var exitCode = timedOut ? -1 : process.ExitCode;
        return new ShellCommandResult(
            exitCode,
            stdout.ToString(),
            stderr.ToString(),
            timedOut,
            sw.ElapsedMilliseconds,
            captureStdout.Result,
            captureStderr.Result);
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
}
