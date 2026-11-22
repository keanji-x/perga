using System.Diagnostics;
using System.Text.RegularExpressions;
using Perga.Kernel;

namespace Perga.Host.Execution;

internal sealed partial class SshMcpTunnel(SshTargetConfig config) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private CancellationTokenSource? _drainCts;
    private Task _drain = Task.CompletedTask;
    private string? _key;
    private string? _uri;
    private int _disposed;

    [GeneratedRegex(@"Allocated port (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex AllocatedPortRegex();

    public async Task<string> EnsureTunnelUriAsync(string hostMcpBaseUri, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(hostMcpBaseUri.Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal)
                .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal), UriKind.Absolute, out var host) ||
            host.Scheme is not ("http" or "https") || host.Port is < 1 or > 65535)
            throw KernelException.PreconditionFailed($"Invalid Host MCP URI '{hostMcpBaseUri}'.");
        if (host.Scheme == "https")
            throw KernelException.PreconditionFailed("Automatic MCP SSH tunneling requires an HTTP Host MCP endpoint. " +
                "Configure a reachable McpBaseUriOverride for HTTPS to preserve TLS hostname verification.");
        var forwardHost = host.Host.Trim('[', ']') is "0.0.0.0" or "::" ? "127.0.0.1" : host.Host;
        var key = $"{host.Scheme}|{forwardHost}|{host.Port}|{host.AbsolutePath.TrimEnd('/')}";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_process is { HasExited: false } && _key == key) return _uri!;
            await CleanupAsync().ConfigureAwait(false);
            var args = SshCommandBuilder.BuildSshCommonOptions(config, enableControlMaster: false);
            args.AddRange(["-N", "-o", "LogLevel=INFO", "-o", "ExitOnForwardFailure=yes", "-R",
                $"127.0.0.1:0:{forwardHost}:{host.Port}", "--", config.Destination]);
            var process = new Process { StartInfo = SshCommandBuilder.CreateSshStartInfo(config, args, redirectStdin: false) };
            var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var recent = new Queue<string>();
            var detail = "";
            var initialized = false;
            _process = process;
            _drainCts = new CancellationTokenSource();
            try
            {
                process.Start();
                var token = _drainCts.Token;
                async Task ReadStderrAsync()
                {
                    while (await process.StandardError.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                    {
                        recent.Enqueue(line);
                        if (recent.Count > 10) recent.Dequeue();
                        detail = string.Join("\n", recent);
                        var match = AllocatedPortRegex().Match(line);
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var port) && port is > 0 and <= 65535)
                            ready.TrySetResult(port);
                    }
                    ready.TrySetException(new IOException(string.Join("\n", recent)));
                }
                _drain = Task.WhenAll(ReadStderrAsync(), process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var allocated = await ready.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                _key = key;
                _uri = $"{host.Scheme}://127.0.0.1:{allocated}{host.AbsolutePath.TrimEnd('/')}";
                initialized = true;
                return _uri;
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                throw KernelException.PreconditionFailed($"MCP SSH tunnel for target '{config.TargetId}' failed: {ex.Message}. {detail} " +
                    "Check AllowTcpForwarding or configure McpBaseUriOverride.");
            }
            finally
            {
                if (!initialized) await CleanupAsync().ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task CleanupAsync()
    {
        _drainCts?.Cancel();
        if (_process is not null)
        {
            SshCommandBuilder.TryKill(_process);
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            try { await _drain.ConfigureAwait(false); } catch (Exception) { }
            _process.Dispose();
            _process = null;
        }
        _drainCts?.Dispose();
        _drainCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CleanupAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        _gate.Dispose();
    }
}
