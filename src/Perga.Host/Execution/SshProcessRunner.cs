using System.ComponentModel;
using System.Text;
using Perga.VcsKit;

namespace Perga.Host.Execution;

public sealed class SshProcessRunner : IProcessRunner
{
    private readonly SshTargetConfig _config;

    public SshProcessRunner(SshTargetConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentNullException.ThrowIfNull(request.Arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);

        ct.ThrowIfCancellationRequested();

        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GH_PROMPT_DISABLED"] = "1",
            ["NO_COLOR"] = "1"
        };

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                env[key] = value;
            }
        }

        var cmdLine = new StringBuilder();
        cmdLine.Append("exec ").Append(SshCommandBuilder.QuotePosix(request.FileName));
        foreach (var arg in request.Arguments)
        {
            cmdLine.Append(' ').Append(SshCommandBuilder.QuotePosix(arg));
        }

        var remoteScript = SshCommandBuilder.BuildRemoteScript(
            workingDirectory: request.WorkingDirectory,
            extraEnv: env,
            targetDefaultEnv: _config.ExtraEnv,
            body: cmdLine.ToString());

        try
        {
            var (exitCode, stdout, stderr) = await SshCommandBuilder
                .RunTextAsync(_config, remoteScript, request.StandardInput, ct)
                .ConfigureAwait(false);
            return new ProcessRunResult(exitCode, stdout, stderr);
        }
        catch (Win32Exception ex)
        {
            var formatted = request.Arguments.Count == 0
                ? request.FileName
                : $"{request.FileName} {string.Join(' ', request.Arguments)}";
            throw new VcsCommandException(
                formatted,
                -1,
                string.Empty,
                ex.Message,
                $"SSH executable '{_config.SshExecutablePath}' could not be started: {ex.Message}",
                ex);
        }
    }
}
