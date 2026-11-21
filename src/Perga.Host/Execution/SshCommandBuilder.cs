using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Perga.Host.Execution;

public static partial class SshCommandBuilder
{
    private static readonly string[] AllowedSshClientEnvKeys =
    [
        "HOME",
        "USER",
        "LOGNAME",
        "PATH",
        "SSH_AUTH_SOCK",
        "LANG",
        "LC_ALL",
        "TERM"
    ];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidPosixEnvNameRegex();

    public static string QuotePosix(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    public static bool IsValidPosixEnvName(string name) =>
        !string.IsNullOrEmpty(name) && ValidPosixEnvNameRegex().IsMatch(name);

    public static string BuildRemotePathBootstrapScript() =>
        """
        for _pg_p in "$HOME/.local/bin" "$HOME/.cargo/bin" "$HOME/.dotnet" "$HOME/.opencode/bin" /usr/local/bin; do
          case ":$PATH:" in
            *":$_pg_p:"*) ;;
            *) [ -d "$_pg_p" ] && PATH="$_pg_p:$PATH" ;;
          esac
        done
        if [ -d "$HOME/.nvm/versions/node" ]; then
          _pg_nvm_bin=$(ls -d "$HOME/.nvm/versions/node"/v*/bin 2>/dev/null | sort -V | tail -n 1)
          if [ -n "$_pg_nvm_bin" ]; then
            case ":$PATH:" in
              *":$_pg_nvm_bin:"*) ;;
              *) PATH="$_pg_nvm_bin:$PATH" ;;
            esac
          fi
        fi
        export PATH
        """;

    public static List<string> BuildSshExecArguments(
        SshTargetConfig config,
        string remoteScript,
        bool enableControlMaster = true)
    {
        var args = BuildSshCommonOptions(config, enableControlMaster);
        args.Add("-T");
        args.Add("--");
        args.Add(config.Destination);
        args.Add("sh -c " + QuotePosix(remoteScript));
        return args;
    }

    public static List<string> BuildSshCommonOptions(
        SshTargetConfig config,
        bool enableControlMaster = true)
    {
        var args = new List<string>(24)
        {
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=10",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3",
            "-o", $"StrictHostKeyChecking={config.StrictHostKeyChecking}"
        };

        if (!string.IsNullOrWhiteSpace(config.KnownHostsFile))
        {
            args.Add("-o");
            args.Add($"UserKnownHostsFile={config.KnownHostsFile}");
        }

        if (!string.IsNullOrWhiteSpace(config.IdentityFile))
        {
            args.Add("-i");
            args.Add(config.IdentityFile);
            args.Add("-o");
            args.Add("IdentitiesOnly=yes");
        }

        if (config.Port != 22)
        {
            args.Add("-p");
            args.Add(config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (enableControlMaster)
        {
            config.EnsureControlDirectoryExists();
            args.Add("-o");
            args.Add("ControlMaster=auto");
            args.Add("-o");
            args.Add("ControlPersist=120");
            args.Add("-o");
            args.Add($"ControlPath={config.ControlPath}");
        }

        return args;
    }

    public static string BuildRemoteScript(
        string? workingDirectory,
        IReadOnlyDictionary<string, string?>? extraEnv,
        IReadOnlyDictionary<string, string>? targetDefaultEnv,
        string body)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildRemotePathBootstrapScript());

        if (targetDefaultEnv is not null)
        {
            foreach (var (key, value) in targetDefaultEnv)
            {
                if (IsValidPosixEnvName(key))
                {
                    sb.Append("export ").Append(key).Append('=').Append(QuotePosix(value)).Append('\n');
                }
            }
        }

        if (extraEnv is not null)
        {
            foreach (var (key, value) in extraEnv)
            {
                if (!IsValidPosixEnvName(key))
                {
                    continue;
                }

                if (value is null)
                {
                    sb.Append("unset ").Append(key).Append('\n');
                }
                else
                {
                    sb.Append("export ").Append(key).Append('=').Append(QuotePosix(value)).Append('\n');
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            sb.Append("cd ").Append(QuotePosix(workingDirectory)).Append(" || exit 1\n");
        }

        sb.Append(body);
        return sb.ToString();
    }

    public static ProcessStartInfo CreateSshStartInfo(
        SshTargetConfig config,
        IReadOnlyList<string> arguments,
        bool redirectStdin = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.SshExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStdin,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (redirectStdin)
        {
            psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        // Build ssh client subprocess environment from an explicit allowlist.
        psi.Environment.Clear();
        foreach (var key in AllowedSshClientEnvKeys)
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(val))
            {
                psi.Environment[key] = val;
            }
        }

        return psi;
    }

    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunTextAsync(
        SshTargetConfig config,
        string remoteScript,
        string? stdin = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var args = BuildSshExecArguments(config, remoteScript);
        var psi = CreateSshStartInfo(config, args, redirectStdin: true);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start ssh process for target '{config.TargetId}'.");
        }

        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }

            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return (process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public static async Task<(int ExitCode, byte[] Stdout, string Stderr)> RunBytesAsync(
        SshTargetConfig config,
        string remoteScript,
        byte[]? stdinBytes = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var args = BuildSshExecArguments(config, remoteScript);
        var psi = CreateSshStartInfo(config, args, redirectStdin: true);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start ssh process for target '{config.TargetId}'.");
        }

        try
        {
            if (stdinBytes is { Length: > 0 })
            {
                await process.StandardInput.BaseStream.WriteAsync(stdinBytes, ct).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
            }

            process.StandardInput.Close();

            using var stdoutMs = new MemoryStream();
            var copyStdoutTask = process.StandardOutput.BaseStream.CopyToAsync(stdoutMs, ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(copyStdoutTask, process.WaitForExitAsync(ct)).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            return (process.ExitCode, stdoutMs.ToArray(), stderr);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    public static async Task TryCloseControlMasterAsync(SshTargetConfig config)
    {
        if (!File.Exists(config.ControlPath))
        {
            return;
        }

        try
        {
            var args = BuildSshCommonOptions(config, enableControlMaster: false);
            args.Add("-S");
            args.Add(config.ControlPath);
            args.Add("-O");
            args.Add("exit");
            args.Add("--");
            args.Add(config.Destination);

            var psi = CreateSshStartInfo(config, args, redirectStdin: false);
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    TryKill(proc);
                }
            }
        }
        catch
        {
            // Best-effort control master teardown.
        }
    }

    public static void TryKill(Process process)
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
            // Ignore race if process exited concurrently.
        }
    }
}
