using System.Diagnostics;
using System.Security.Cryptography;
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

    public static string BuildTrackedCommand(SshTargetConfig config, string pidFile, string command)
    {
        var runtime = config.WorkspacesRoot.TrimEnd('/') + "/.perga-runtime";
        var pids = runtime + "/pids";
        var child = "echo $$ > " + QuotePosix(pidFile) + "\nexec " + command;
        var cleanup = "_status=$?; _p=$(cat " + QuotePosix(pidFile) + " 2>/dev/null); " +
            "if [ -n \"$_p\" ] && ! kill -0 -\"$_p\" 2>/dev/null && ! kill -0 \"$_p\" 2>/dev/null; then rm -f -- " +
            QuotePosix(pidFile) + "; fi; exit $_status";
        return "umask 077\nmkdir -p -- " + QuotePosix(pids) + " && chmod 700 -- " +
            QuotePosix(runtime) + " " + QuotePosix(pids) + " || exit 1\n" +
            "if command -v setsid >/dev/null 2>&1; then setsid sh -c " + QuotePosix(child) +
            "; else sh -c " + QuotePosix(child) + "; fi\n" + cleanup;
    }

    public static async Task<bool> TryTerminateTrackedProcessAsync(
        SshTargetConfig config, string pidFile, CancellationToken ct = default)
    {
        var q = QuotePosix(pidFile);
        var script = $$"""
            [ -f {{q}} ] || exit 0
            _p=$(cat {{q}}) || exit 1
            case "$_p" in ''|*[!0-9]*) exit 1 ;; esac
            _alive() { kill -0 -"$_p" 2>/dev/null || kill -0 "$_p" 2>/dev/null; }
            if _alive; then
              kill -TERM -"$_p" 2>/dev/null || kill -TERM "$_p" 2>/dev/null || true
              sleep 0.2
              if _alive; then
                kill -KILL -"$_p" 2>/dev/null || kill -KILL "$_p" 2>/dev/null || true
              fi
              for _i in 1 2 3 4 5; do
                _alive || break
                sleep 0.1
              done
            fi
            _alive && exit 1
            rm -f -- {{q}}
            """;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var (code, _, _) = await RunTextAsync(config, script, ct: timeout.Token).ConfigureAwait(false);
            return code == 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task<string> StageFileAsync(
        SshTargetConfig config, string localPath, string remoteFilePrefix, string extension,
        bool executable, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var runtimeDir = $"{config.WorkspacesRoot.TrimEnd('/')}/.perga-runtime";
        var stagedPath = $"{runtimeDir}/{remoteFilePrefix}-{hash}{extension}";
        var qStaged = QuotePosix(stagedPath);
        var (exists, _, _) = await RunTextAsync(config,
            $"[ -f {qStaged} ] && [ \"$(wc -c < {qStaged})\" -eq {bytes.Length} ]" + (executable ? $" && [ -x {qStaged} ]" : ""), ct: ct).ConfigureAwait(false);
        if (exists == 0) return stagedPath;

        var qTemp = QuotePosix($"{stagedPath}.tmp.{Guid.NewGuid():N}");
        var script = $"umask 077; trap {QuotePosix($"rm -f -- {qTemp}")} EXIT; " +
            $"mkdir -p -- {QuotePosix(runtimeDir)} && cat > {qTemp} && [ \"$(wc -c < {qTemp})\" -eq {bytes.Length} ]" +
            (executable ? $" && chmod 700 -- {qTemp}" : "") + $" && mv -f -- {qTemp} {qStaged}";
        var (code, _, stderr) = await RunBytesAsync(config, script, bytes, ct).ConfigureAwait(false);
        if (code != 0)
            throw new IOException($"Failed to stage file onto target '{config.TargetId}' at '{stagedPath}': {stderr.Trim()}");
        return stagedPath;
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
        else
        {
            args.Add("-o");
            args.Add("ControlMaster=no");
            args.Add("-o");
            args.Add("ControlPath=none");
            args.Add("-o");
            args.Add("ControlPersist=no");
        }

        return args;
    }

    public static string BuildRemoteScript(
        string? workingDirectory,
        IEnumerable<KeyValuePair<string, string?>>? extraEnv,
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
