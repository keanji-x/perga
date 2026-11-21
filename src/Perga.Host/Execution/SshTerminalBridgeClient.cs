using System.Diagnostics;
using System.Net.WebSockets;
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

        if (_innerClient is not null)
        {
            await _innerClient.DisposeAsync().ConfigureAwait(false);
            _innerClient = null;
        }

        if (_forwarderSsh is not null)
        {
            SshCommandBuilder.TryKill(_forwarderSsh);
            _forwarderSsh.Dispose();
            _forwarderSsh = null;
        }

        if (_remoteDaemonSsh is not null)
        {
            SshCommandBuilder.TryKill(_remoteDaemonSsh);
            _remoteDaemonSsh.Dispose();
            _remoteDaemonSsh = null;
        }

        if (!string.IsNullOrEmpty(_remotePidFile) || !string.IsNullOrEmpty(_remoteSocketDir))
        {
            try
            {
                var qPid = SshCommandBuilder.QuotePosix(_remotePidFile ?? string.Empty);
                var qDir = SshCommandBuilder.QuotePosix(_remoteSocketDir ?? string.Empty);
                var cleanup =
                    $"if [ -n {qPid} ] && [ -f {qPid} ]; then _p=$(cat {qPid} 2>/dev/null); [ -n \"$_p\" ] && kill -TERM \"$_p\" 2>/dev/null || true; fi; " +
                    $"if [ -n {qDir} ] && [ -d {qDir} ]; then rm -rf -- {qDir}; fi";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await SshCommandBuilder.RunTextAsync(_config, cleanup, stdin: null, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort remote cleanup.
            }
        }

        if (!string.IsNullOrEmpty(_localUdsPath))
        {
            try { File.Delete(_localUdsPath); } catch { }
        }

        _initLock.Dispose();
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

            var runToken = Guid.NewGuid().ToString("N")[..10];
            var remoteDir = $"/tmp/pg-tb-{runToken}";
            var remoteUds = $"{remoteDir}/tb.sock";
            var remotePid = $"{remoteDir}/tb.pid";
            var localUds = Path.Combine(_config.ControlDir, $"tb-{runToken}.sock");
            if (File.Exists(localUds))
            {
                try { File.Delete(localUds); } catch { }
            }

            var bridgeBin = _config.TerminalBridgeExecutablePath ?? "perga-term-bridge";
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
            if (!daemonProc.Start())
            {
                daemonProc.Dispose();
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
                            SshCommandBuilder.TryKill(daemonProc);
                            daemonProc.Dispose();
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
                    SshCommandBuilder.TryKill(daemonProc);
                    daemonProc.Dispose();
                    throw new TerminalBridgeException(
                        $"Timed out waiting for remote perga-term-bridge on target '{_config.TargetId}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(announcedUds))
            {
                SshCommandBuilder.TryKill(daemonProc);
                daemonProc.Dispose();
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
            var fwdProc = new Process { StartInfo = fwdPsi };
            if (!fwdProc.Start())
            {
                SshCommandBuilder.TryKill(daemonProc);
                daemonProc.Dispose();
                fwdProc.Dispose();
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
                    SshCommandBuilder.TryKill(daemonProc);
                    daemonProc.Dispose();
                    fwdProc.Dispose();
                    throw new TerminalBridgeException(
                        $"SSH terminal socket forwarder failed on target '{_config.TargetId}': {err.Trim()}");
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    SshCommandBuilder.TryKill(fwdProc);
                    SshCommandBuilder.TryKill(daemonProc);
                    fwdProc.Dispose();
                    daemonProc.Dispose();
                    throw new TerminalBridgeException(
                        $"Timed out waiting for SSH forwarded terminal socket on target '{_config.TargetId}'.");
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            _remoteDaemonSsh = daemonProc;
            _forwarderSsh = fwdProc;
            _localUdsPath = localUds;
            _remoteSocketDir = remoteDir;
            _remotePidFile = remotePid;
            _innerClient = TerminalBridgeClient.ForUnixSocket(localUds);
            return _innerClient;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task CleanupBrokenSessionAsync()
    {
        if (_innerClient is not null)
        {
            await _innerClient.DisposeAsync().ConfigureAwait(false);
            _innerClient = null;
        }

        if (_forwarderSsh is not null)
        {
            SshCommandBuilder.TryKill(_forwarderSsh);
            _forwarderSsh.Dispose();
            _forwarderSsh = null;
        }

        if (_remoteDaemonSsh is not null)
        {
            SshCommandBuilder.TryKill(_remoteDaemonSsh);
            _remoteDaemonSsh.Dispose();
            _remoteDaemonSsh = null;
        }

        if (!string.IsNullOrEmpty(_localUdsPath))
        {
            try { File.Delete(_localUdsPath); } catch { }
            _localUdsPath = null;
        }
    }
}
