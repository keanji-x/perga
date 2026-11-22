using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using Perga.Host.Terminals;
using Perga.TermClient;

namespace Perga.Host.Execution;

/// <summary>
/// Manages a per-target <c>perga-term-bridge</c> daemon running on an SSH execution target
/// over a private <c>0700</c> Unix Domain Socket, forwarded to a private <c>0700</c> UDS on the Host
/// via OpenSSH <c>-L local.sock:remote.sock</c>. Zero TCP ports are exposed on either side.
/// </summary>
public sealed class SshTerminalBridgeClient : ITerminalBridgeClient, IHostTerminalWebSocketConnector
{
    private const string UdsAnnouncementPrefix = "PERGA_TERM_BRIDGE_UDS=";

    private readonly SshTargetConfig _config;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private Process? _remoteDaemonSsh;
    private Process? _forwarderSsh;
    private TerminalBridgeClient? _innerClient;
    private string? _localUdsPath;
    private string? _remoteSocketDir;
    private string? _remotePidFile;
    private string? _resolvedRemoteBridgeBin;
    private int _disposed;

    public SshTerminalBridgeClient(SshTargetConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<TermEnsureResult> EnsureAsync(TermEnsureRequest req, CancellationToken ct)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        return await client.EnsureAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<TermObserveResult> ObserveAsync(TermObserveRequest req, CancellationToken ct)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        return await client.ObserveAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<TermActionResult> SendActionAsync(TermActionRequest req, CancellationToken ct)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        return await client.SendActionAsync(req, ct).ConfigureAwait(false);
    }

    public async Task ResizeAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        await client.ResizeAsync(terminalId, cols, rows, ct).ConfigureAwait(false);
    }

    public async Task KillAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        await client.KillAsync(terminalId, graceMs, ct).ConfigureAwait(false);
    }

    public Uri GetWebSocketRelayUri(string terminalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalId);
        return _innerClient?.GetWebSocketRelayUri(terminalId)
               ?? new Uri($"ws://localhost/ws/{Uri.EscapeDataString(terminalId)}");
    }

    public async Task<ClientWebSocket> ConnectWebSocketAsync(string terminalId, CancellationToken ct = default)
    {
        var client = await GetOrInitializeClientAsync(ct).ConfigureAwait(false);
        return await client.ConnectWebSocketAsync(terminalId, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupBrokenSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<TerminalBridgeClient> GetOrInitializeClientAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_innerClient is not null &&
            _remoteDaemonSsh is { HasExited: false } &&
            _forwarderSsh is { HasExited: false })
        {
            return _innerClient;
        }

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_innerClient is not null &&
                _remoteDaemonSsh is { HasExited: false } &&
                _forwarderSsh is { HasExited: false })
            {
                return _innerClient;
            }

            await CleanupBrokenSessionAsync().ConfigureAwait(false);
            _config.EnsureControlDirectoryExists();

            var bridgeBin = await ResolveRemoteBridgeExecutableAsync(ct).ConfigureAwait(false);
            var runToken = Guid.NewGuid().ToString("N")[..10];
            var remoteDir = $"/tmp/pg-tb-{runToken}";
            var remoteUds = $"{remoteDir}/tb.sock";
            var remotePid = $"{remoteDir}/tb.pid";
            var localDir = Path.Combine(_config.ControlDir, $"tb-{runToken}");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(localDir);
            else Directory.CreateDirectory(localDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var localUds = Path.Combine(localDir, "tb.sock");
            var launchBody =
                $"umask 077\n" +
                $"mkdir -p -- {SshCommandBuilder.QuotePosix(remoteDir)}\n" +
                $"chmod 700 -- {SshCommandBuilder.QuotePosix(remoteDir)}\n" +
                $"echo $$ > {SshCommandBuilder.QuotePosix(remotePid)}\n" +
                $"exec {SshCommandBuilder.QuotePosix(bridgeBin)} --listen-uds {SshCommandBuilder.QuotePosix(remoteUds)}\n";

            var remoteScript = SshCommandBuilder.BuildRemoteScript(
                workingDirectory: null,
                extraEnv: null,
                targetDefaultEnv: _config.ExtraEnv,
                body: launchBody);

            var daemonArgs = SshCommandBuilder.BuildSshExecArguments(_config, remoteScript, enableControlMaster: false);
            var daemonPsi = SshCommandBuilder.CreateSshStartInfo(_config, daemonArgs, redirectStdin: false);
            var daemonProc = new Process { StartInfo = daemonPsi };
            Process? fwdProc = null;
            TerminalBridgeClient? client = null;
            var initialized = false;
            try
            {
                if (!daemonProc.Start())
                {
                    throw new TerminalBridgeException(
                        $"Failed to start remote perga-term-bridge via SSH on target '{_config.TargetId}'.");
                }

                string? announcedUds = null;
                using (var startupCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    startupCts.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        while (!startupCts.IsCancellationRequested)
                        {
                            var line = await daemonProc.StandardOutput.ReadLineAsync(startupCts.Token).ConfigureAwait(false);
                            if (line is null)
                            {
                                var stderr = await daemonProc.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
                                throw new TerminalBridgeException(
                                    $"Remote perga-term-bridge on target '{_config.TargetId}' exited before announcing UDS. {stderr.Trim()}");
                            }

                            if (line.StartsWith(UdsAnnouncementPrefix, StringComparison.Ordinal))
                            {
                                announcedUds = line[UdsAnnouncementPrefix.Length..].Trim();
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TerminalBridgeException(
                            $"Timed out waiting for remote perga-term-bridge on target '{_config.TargetId}'.");
                    }
                }

                if (string.IsNullOrWhiteSpace(announcedUds))
                {
                    throw new TerminalBridgeException(
                        $"Remote perga-term-bridge on target '{_config.TargetId}' did not report a UDS path.");
                }

                var fwdArgs = SshCommandBuilder.BuildSshCommonOptions(_config, enableControlMaster: false);
                fwdArgs.Add("-N");
                fwdArgs.Add("-o");
                fwdArgs.Add("ExitOnForwardFailure=yes");
                fwdArgs.Add("-L");
                fwdArgs.Add($"{localUds}:{announcedUds}");
                fwdArgs.Add("--");
                fwdArgs.Add(_config.Destination);

                var fwdPsi = SshCommandBuilder.CreateSshStartInfo(_config, fwdArgs, redirectStdin: false);
                fwdProc = new Process { StartInfo = fwdPsi };
                if (!fwdProc.Start())
                {
                    throw new TerminalBridgeException(
                        $"Failed to start SSH UDS forwarder for target '{_config.TargetId}'.");
                }

                // Wait for local forwarded Unix socket to materialize.
                var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
                while (!File.Exists(localUds))
                {
                    if (fwdProc.HasExited)
                    {
                        var err = await fwdProc.StandardError.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
                        throw new TerminalBridgeException(
                            $"SSH terminal socket forwarder failed on target '{_config.TargetId}': {err.Trim()}");
                    }

                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        throw new TerminalBridgeException(
                            $"Timed out waiting for SSH forwarded terminal socket on target '{_config.TargetId}'.");
                    }

                    await Task.Delay(50, ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                client = TerminalBridgeClient.ForUnixSocket(localUds);
                _remoteDaemonSsh = daemonProc;
                _forwarderSsh = fwdProc;
                _localUdsPath = localUds;
                _remoteSocketDir = remoteDir;
                _remotePidFile = remotePid;
                _innerClient = client;
                initialized = true;
                return client;
            }
            finally
            {
                if (!initialized)
                {
                    await CleanupSessionAsync(client, fwdProc, daemonProc, localUds, remoteDir, remotePid).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<string> ResolveRemoteBridgeExecutableAsync(CancellationToken ct)
    {
        if (_resolvedRemoteBridgeBin is not null) return _resolvedRemoteBridgeBin;
        if (_config.TerminalBridgeExecutablePath is { } configured) return _resolvedRemoteBridgeBin = configured;
        var (code, path, _) = await SshCommandBuilder.RunTextAsync(_config,
            SshCommandBuilder.BuildRemotePathBootstrapScript() + "\ncommand -v perga-term-bridge 2>/dev/null", ct: ct).ConfigureAwait(false);
        if (code == 0 && !string.IsNullOrWhiteSpace(path)) return _resolvedRemoteBridgeBin = path.Trim();

        var (unameCode, uname, unameErr) = await SshCommandBuilder.RunTextAsync(_config, "uname -sm", ct: ct).ConfigureAwait(false);
        if (unameCode != 0)
            throw new TerminalBridgeException($"Failed to query OS/architecture for target '{_config.TargetId}': {unameErr.Trim()}");
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", _ => null
        };
        var hostOsArch = $"{(OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription)} {arch ?? RuntimeInformation.ProcessArchitecture.ToString()}";
        var message = $"Remote target '{_config.TargetId}' ({uname.Trim()}) does not have 'perga-term-bridge' installed and cannot auto-stage from host ({hostOsArch}). " +
            "Install 'perga-term-bridge' on the remote PATH or configure TerminalBridgeExecutablePath.";
        if (!OperatingSystem.IsLinux() || arch is null || uname.Trim() != $"Linux {arch}")
            throw new TerminalBridgeException($"OS/architecture mismatch. {message}");
        string localBin;
        try
        {
            localBin = await TerminalBridgeDaemon.ResolveOrBuildExecutableAsync(
                _config.HostTerminalBridgeExecutablePath, buildIfMissing: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TerminalBridgeException or FileNotFoundException)
        {
            throw new TerminalBridgeException($"{message} {ex.Message}");
        }
        return _resolvedRemoteBridgeBin = await SshCommandBuilder.StageFileAsync(_config,
            localBin, "perga-term-bridge", string.Empty, executable: true, ct).ConfigureAwait(false);
    }

    private async Task CleanupBrokenSessionAsync()
    {
        await CleanupSessionAsync(_innerClient, _forwarderSsh, _remoteDaemonSsh,
            _localUdsPath, _remoteSocketDir, _remotePidFile).ConfigureAwait(false);
        _innerClient = null;
        _forwarderSsh = null;
        _remoteDaemonSsh = null;
        _localUdsPath = null;
        _remoteSocketDir = null;
        _remotePidFile = null;
    }

    private async Task CleanupSessionAsync(TerminalBridgeClient? client, Process? forwarder, Process? daemon,
        string? localUds, string? remoteDir, string? remotePid)
    {
        try
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (forwarder is not null)
            {
                SshCommandBuilder.TryKill(forwarder);
                forwarder.Dispose();
            }
            if (daemon is not null)
            {
                SshCommandBuilder.TryKill(daemon);
                daemon.Dispose();
            }
            if (remotePid is not null && remoteDir is not null)
            {
                var confirmed = await SshCommandBuilder.TryTerminateTrackedProcessAsync(_config, remotePid).ConfigureAwait(false);
                if (confirmed)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await SshCommandBuilder.RunTextAsync(_config,
                            "rm -rf -- " + SshCommandBuilder.QuotePosix(remoteDir), ct: cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or System.ComponentModel.Win32Exception) { }
                }
            }
            if (localUds is not null)
            {
                try { Directory.Delete(Path.GetDirectoryName(localUds)!, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
