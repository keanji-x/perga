using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Perga.AgentKit;

namespace Perga.Host.Execution;

public sealed class SshSubprocessTransport : ISubprocessTransport
{
    private readonly SshTargetConfig _config;
    private readonly object _cacheLock = new();
    private string? _resolvedNodePath;
    private string? _resolvedBridgeScriptPath;

    public SshSubprocessTransport(SshTargetConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public bool IsLocal => false;

    public ISubprocessHandle StartInteractive(CommandInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        var runToken = Guid.NewGuid().ToString("N")[..12];
        var remotePidFile = $"{_config.WorkspacesRoot.TrimEnd('/')}/.perga-runtime/pids/agent-{runToken}.pid";

        var cmd = new StringBuilder();
        cmd.Append(SshCommandBuilder.QuotePosix(invocation.FileName));
        foreach (var arg in invocation.Arguments)
        {
            cmd.Append(' ').Append(SshCommandBuilder.QuotePosix(arg));
        }
        cmd.Append('\n');

        var remoteScript = SshCommandBuilder.BuildRemoteScript(
            workingDirectory: invocation.WorkingDirectory,
            extraEnv: invocation.Environment?.Select(static kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)),
            targetDefaultEnv: _config.ExtraEnv,
            body: SshCommandBuilder.BuildTrackedCommand(_config, remotePidFile, cmd.ToString()));

        var sshArgs = SshCommandBuilder.BuildSshExecArguments(_config, remoteScript);
        var psi = SshCommandBuilder.CreateSshStartInfo(_config, sshArgs, redirectStdin: true);

        var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Failed to start ssh subprocess '{invocation.FileName}' on target '{_config.TargetId}'.");
            }
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Failed to start ssh subprocess '{invocation.FileName}' on target '{_config.TargetId}': {ex.Message}",
                ex);
        }

        return new SshSubprocessHandle(_config, process, remotePidFile);
    }

    public string ResolveNodeExecutable(string? configuredExecutable, IReadOnlyDictionary<string, string>? environment)
    {
        var explicitPath = !string.IsNullOrWhiteSpace(_config.OarNodeExecutablePath)
            ? _config.OarNodeExecutablePath
            : configuredExecutable;

        lock (_cacheLock)
        {
            if (_resolvedNodePath is not null && string.IsNullOrWhiteSpace(explicitPath))
            {
                return _resolvedNodePath;
            }
        }

        var candidatesScript = new StringBuilder();
        candidatesScript.AppendLine(SshCommandBuilder.BuildRemotePathBootstrapScript());
        candidatesScript.AppendLine("""
            _pg_check_node() {
              _bin="$1"
              [ -n "$_bin" ] || return 1
              [ -x "$_bin" ] || return 1
              _ver=$("$_bin" -v 2>/dev/null || true)
              _major=$(printf '%s' "$_ver" | sed -n 's/^v\([0-9][0-9]*\).*/\1/p')
              if [ -n "$_major" ] && [ "$_major" -ge 24 ] 2>/dev/null; then
                printf '%s\n' "$_bin"
                return 0
              fi
              return 1
            }
            """);

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidatesScript.Append("_pg_check_node ")
                .Append(SshCommandBuilder.QuotePosix(explicitPath))
                .AppendLine(" && exit 0");
        }

        candidatesScript.AppendLine("""
            _path_node=$(command -v node 2>/dev/null || true)
            _pg_check_node "$_path_node" && exit 0
            if [ -d "$HOME/.nvm/versions/node" ]; then
              for _d in $(ls -d "$HOME/.nvm/versions/node"/v* 2>/dev/null | sort -Vr); do
                _pg_check_node "$_d/bin/node" && exit 0
              done
            fi
            for _cand in /usr/local/bin/node /usr/bin/node; do
              _pg_check_node "$_cand" && exit 0
            done
            exit 1
            """);

        var (exitCode, stdout, stderr) = SshCommandBuilder
            .RunTextAsync(_config, candidatesScript.ToString(), stdin: null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var resolved = exitCode == 0
            ? stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                $"Target '{_config.TargetId}' requires Node.js >= 24 for perga-oar-bridge. " +
                $"Install Node 24+ for '{_config.User}' or configure ExecutionTargets:{_config.TargetId}:OarNodeExecutablePath. " +
                (string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"Detail: {stderr.Trim()}"));
        }

        lock (_cacheLock)
        {
            _resolvedNodePath = resolved;
        }

        return resolved;
    }

    public string ResolveBridgeScriptPath(string? configuredBridgeScriptPath)
    {
        if (!string.IsNullOrWhiteSpace(_config.OarBridgeScriptPath))
        {
            var q = SshCommandBuilder.QuotePosix(_config.OarBridgeScriptPath);
            var (code, _, _) = SshCommandBuilder
                .RunTextAsync(_config, $"[ -r {q} ]", stdin: null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (code == 0)
            {
                return _config.OarBridgeScriptPath;
            }

            throw new FileNotFoundException(
                $"Configured OAR bridge script '{_config.OarBridgeScriptPath}' was not readable on target '{_config.TargetId}'.",
                _config.OarBridgeScriptPath);
        }

        lock (_cacheLock)
        {
            if (_resolvedBridgeScriptPath is not null)
            {
                return _resolvedBridgeScriptPath;
            }
        }

        var localScriptPath = !string.IsNullOrWhiteSpace(configuredBridgeScriptPath)
            ? configuredBridgeScriptPath
            : _config.HostOarBridgeScriptPath;

        if (string.IsNullOrWhiteSpace(localScriptPath) || !File.Exists(localScriptPath))
        {
            throw new InvalidOperationException(
                $"OAR bridge script path is not configured or missing on Host for target '{_config.TargetId}'.");
        }

        var fullLocalPath = Path.GetFullPath(localScriptPath);

        // On same-machine setups where the bridge script lives in a shared readable location,
        // avoid unnecessary staging if the target user can already read it directly.
        if (_config.IsLoopbackHost)
        {
            var qLocal = SshCommandBuilder.QuotePosix(fullLocalPath);
            var (canRead, _, _) = SshCommandBuilder
                .RunTextAsync(_config, $"[ -r {qLocal} ]", stdin: null, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (canRead == 0)
            {
                lock (_cacheLock)
                {
                    _resolvedBridgeScriptPath = fullLocalPath;
                }

                return fullLocalPath;
            }
        }

        var stagedPath = SshCommandBuilder.StageFileAsync(_config, fullLocalPath,
            "perga-oar-bridge", ".mjs", executable: false, CancellationToken.None).GetAwaiter().GetResult();

        lock (_cacheLock)
        {
            _resolvedBridgeScriptPath = stagedPath;
        }

        return stagedPath;
    }

    public bool DirectoryExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var (code, _, _) = SshCommandBuilder
            .RunTextAsync(_config, $"[ -d {SshCommandBuilder.QuotePosix(path)} ]", stdin: null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return code == 0;
    }

    private sealed class SshSubprocessHandle : ISubprocessHandle
    {
        private const int MaxStderrLines = 64;

        private readonly SshTargetConfig _config;
        private readonly Process _process;
        private readonly string _remotePidFile;
        private readonly SemaphoreSlim _stdinLock = new(1, 1);
        private readonly ConcurrentQueue<string> _stderrTail = new();
        private readonly CancellationTokenSource _disposeCts = new();
        private readonly Task _stderrPumpTask;
        private int _disposed;
        private int _stdinClosed;

        public SshSubprocessHandle(SshTargetConfig config, Process process, string remotePidFile)
        {
            _config = config;
            _process = process;
            _remotePidFile = remotePidFile;
            _stderrPumpTask = Task.Run(PumpStderrAsync);
        }

        public int? ProcessId
        {
            get
            {
                try { return _process.Id; }
                catch { return null; }
            }
        }

        public bool HasExited
        {
            get
            {
                try { return _process.HasExited; }
                catch { return true; }
            }
        }

        public bool ShutdownConfirmed { get; private set; }

        public int? TryGetExitCode()
        {
            try { return _process.HasExited ? _process.ExitCode : null; }
            catch { return null; }
        }

        public async Task WriteLineAsync(string line, CancellationToken ct, Func<CancellationToken, Task>? beforeWrite = null)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _stdinLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (beforeWrite is not null) await beforeWrite(ct).ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                ct.ThrowIfCancellationRequested();
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _stdinLock.Release();
            }
        }

        public async ValueTask<string?> ReadStdoutLineAsync(CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0 && HasExited)
            {
                return null;
            }

            try
            {
                return await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        public string GetStderrSummary() => string.Join('\n', _stderrTail);

        public bool SendInterruptSignal()
        {
            if (HasExited)
            {
                return false;
            }

            try
            {
                var q = SshCommandBuilder.QuotePosix(_remotePidFile);
                var script = $"if [ -f {q} ]; then _p=$(cat {q} 2>/dev/null); [ -n \"$_p\" ] && kill -INT \"$_p\" 2>/dev/null; fi";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var (code, _, _) = SshCommandBuilder
                    .RunTextAsync(_config, script, stdin: null, cts.Token)
                    .GetAwaiter()
                    .GetResult();
                return code == 0;
            }
            catch
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            CloseStdinOnce();

            if (!HasExited)
            {
                try
                {
                    using var graceCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
                    await _process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Fall through to remote signal + local ssh kill.
                }
            }

            ShutdownConfirmed = await SshCommandBuilder.TryTerminateTrackedProcessAsync(
                _config, _remotePidFile).ConfigureAwait(false);
            SshCommandBuilder.TryKill(_process);
            try
            {
                using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }

            await _disposeCts.CancelAsync().ConfigureAwait(false);
            try { await _stderrPumpTask.ConfigureAwait(false); } catch { }
            _disposeCts.Dispose();
            _stdinLock.Dispose();
            _process.Dispose();
        }

        private void CloseStdinOnce()
        {
            if (Interlocked.Exchange(ref _stdinClosed, 1) != 0)
            {
                return;
            }

            try { _process.StandardInput.Close(); } catch { }
        }

        private async Task PumpStderrAsync()
        {
            try
            {
                while (!_disposeCts.IsCancellationRequested)
                {
                    var line = await _process.StandardError.ReadLineAsync(_disposeCts.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > MaxStderrLines)
                    {
                        _stderrTail.TryDequeue(out _);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch
            {
            }
        }
    }
}
