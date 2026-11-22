using System.Diagnostics;
using System.Text;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Kernel;
using Perga.Kernel.Ports;

namespace Perga.Host.Execution;

public sealed record SshProbeResult(bool Success, string? RemoteHome, string? ErrorMessage);

public interface ISshTargetConnectivityProbe
{
    Task<SshProbeResult> ProbeAsync(SshTargetConfig config, CancellationToken ct = default);
}

public sealed class ProcessSshTargetConnectivityProbe : ISshTargetConnectivityProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public async Task<SshProbeResult> ProbeAsync(SshTargetConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var args = SshCommandBuilder.BuildSshExecArguments(
            config,
            "printf '%s' \"$HOME\"",
            enableControlMaster: false);

        using var process = new Process
        {
            StartInfo = SshCommandBuilder.CreateSshStartInfo(config, args, redirectStdin: false)
        };

        try
        {
            if (!process.Start())
            {
                return new SshProbeResult(false, null, "Failed to launch ssh process.");
            }
        }
        catch (Exception ex)
        {
            return new SshProbeResult(false, null, $"Failed to launch ssh: {ex.Message}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeout);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
            var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"ssh exited with code {process.ExitCode}."
                    : stderr;
                return new SshProbeResult(false, null, detail);
            }

            if (string.IsNullOrWhiteSpace(stdout) || !stdout.StartsWith('/'))
            {
                return new SshProbeResult(false, null, "Connected via SSH, but could not determine remote HOME directory.");
            }

            return new SshProbeResult(true, stdout, null);
        }
        catch (OperationCanceledException)
        {
            SshCommandBuilder.TryKill(process);
            ct.ThrowIfCancellationRequested();
            return new SshProbeResult(false, null, "SSH connection timed out. Check host reachability and passwordless key auth.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(config.ControlDir))
                {
                    Directory.Delete(config.ControlDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of unused control dir.
            }
        }
    }
}

public sealed class SshExecutionTargetManager : IExecutionTargetManager
{
    private readonly ITrackStore _store;
    private readonly IExecutionEnvironmentRegistry _registry;
    private readonly PergaHostOptions _options;
    private readonly ISshTargetConnectivityProbe _probe;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<string> _staticTargetIds;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public SshExecutionTargetManager(
        ITrackStore store,
        IExecutionEnvironmentRegistry registry,
        PergaHostOptions options,
        ISshTargetConnectivityProbe probe,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _staticTargetIds = new HashSet<string>(
            options.ExecutionTargets.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()),
            StringComparer.OrdinalIgnoreCase);
        _staticTargetIds.Add(ExecutionTargetIds.Local);
    }

    public async Task LoadPersistedTargetsAsync(CancellationToken ct = default)
    {
        var persisted = await _store.ListExecutionTargetsAsync(ct).ConfigureAwait(false);
        foreach (var item in persisted)
        {
            if (_staticTargetIds.Contains(item.Id) || _registry.TryGet(item.Id) is not null)
            {
                continue;
            }

            try
            {
                var config = BuildConfigFromStored(item);
                _registry.Register(new SshExecutionEnvironment(config));
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Skipping invalid persisted execution target '{0}': {1}", item.Id, ex.Message);
            }
        }
    }

    public async Task<ExecutionTargetDto> CreateSshTargetAsync(CreateExecutionTargetRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (user, host, port) = ParseEndpoint(request);
        var identityFile = ExpandLocalIdentityPath(request.IdentityFile);
        var explicitWorkspacesRoot = string.IsNullOrWhiteSpace(request.WorkspacesRoot)
            ? null
            : request.WorkspacesRoot.Trim();
        if (explicitWorkspacesRoot is not null && !explicitWorkspacesRoot.StartsWith('/'))
        {
            throw KernelException.InvalidArgument("WorkspacesRoot must be an absolute POSIX path.");
        }

        var isLoopback = SshTargetConfig.IsLoopback(host);
        var label = string.IsNullOrWhiteSpace(request.Label)
            ? SshTargetConfig.DefaultLabel(user, host, port)
            : request.Label.Trim();

        // Validate tokens and test SSH connectivity before persisting anything.
        SshTargetConfig probeConfig;
        try
        {
            probeConfig = new SshTargetConfig(
                targetId: "probe_" + Guid.NewGuid().ToString("N")[..8],
                options: new ExecutionTargetOptions
                {
                    Kind = "ssh",
                    Label = label,
                    Host = host,
                    Port = port,
                    User = user,
                    IdentityFile = identityFile,
                    WorkspacesRoot = explicitWorkspacesRoot ?? "/tmp"
                },
                hostOarBridgeScriptPath: _options.OarBridgeScriptPath,
                removable: true);
        }
        catch (ArgumentException ex)
        {
            throw KernelException.InvalidArgument(ex.Message);
        }

        var probeResult = await _probe.ProbeAsync(probeConfig, ct).ConfigureAwait(false);
        if (!probeResult.Success)
        {
            throw KernelException.InvalidArgument(
                $"SSH connection failed: {probeResult.ErrorMessage ?? "Could not reach target."}");
        }

        var remoteHome = probeResult.RemoteHome!.TrimEnd('/');
        var workspacesRoot = explicitWorkspacesRoot
            ?? (string.IsNullOrEmpty(remoteHome) ? "/.perga/workspaces" : $"{remoteHome}/.perga/workspaces");

        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = AllocateUniqueId(label, user, host, isLoopback);
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var stored = new StoredExecutionTarget(
                Id: id,
                Label: label,
                Kind: "ssh",
                Host: host,
                Port: port,
                User: user,
                IdentityFile: identityFile,
                WorkspacesRoot: workspacesRoot,
                CreatedAtMs: nowMs,
                UpdatedAtMs: nowMs);

            await _store.UpsertExecutionTargetAsync(stored, ct).ConfigureAwait(false);
            var finalConfig = BuildConfigFromStored(stored);
            var env = new SshExecutionEnvironment(finalConfig);
            _registry.Register(env);
            return env.Descriptor;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<bool> DeleteTargetAsync(string targetId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw KernelException.InvalidArgument("TargetId cannot be empty.");
        }

        var normalized = targetId.Trim();
        if (string.Equals(normalized, ExecutionTargetIds.Local, StringComparison.OrdinalIgnoreCase))
        {
            throw KernelException.InvalidArgument("The built-in 'local' execution target cannot be removed.");
        }

        if (_staticTargetIds.Contains(normalized))
        {
            throw KernelException.Conflict($"Execution target '{normalized}' is configured statically on this Host and cannot be removed via API.");
        }

        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var deleted = await _store.DeleteExecutionTargetAsync(normalized, ct).ConfigureAwait(false);
            var unregistered = await _registry.UnregisterAsync(normalized).ConfigureAwait(false);
            return deleted || unregistered;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    internal static (string User, string Host, int Port) ParseEndpoint(CreateExecutionTargetRequest request)
    {
        string? user = string.IsNullOrWhiteSpace(request.User) ? null : request.User.Trim();
        string? host = string.IsNullOrWhiteSpace(request.Host) ? null : request.Host.Trim();
        int port = request.Port ?? 22;

        if (!string.IsNullOrWhiteSpace(request.Target))
        {
            var raw = request.Target.Trim();
            if (raw.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) raw = raw["ssh://".Length..];

            if (raw.Contains('/') ||
                !Uri.TryCreate($"ssh://{raw}", UriKind.Absolute, out var uri) ||
                string.IsNullOrEmpty(uri.UserInfo) || uri.UserInfo.Contains(':') ||
                string.IsNullOrEmpty(uri.DnsSafeHost) ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.Port is 0 or > 65535)
            {
                throw KernelException.InvalidArgument("SSH target must be 'user@host' or 'user@host:port', without a password, path, query or fragment.");
            }

            user ??= uri.UserInfo;
            host ??= uri.DnsSafeHost;
            if (request.Port is null && uri.Port != -1) port = uri.Port;
        }

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(host))
        {
            throw KernelException.InvalidArgument("Specify SSH target as 'user@host' or 'user@host:port'.");
        }

        if (port is < 1 or > 65535)
        {
            throw KernelException.InvalidArgument("SSH port must be between 1 and 65535.");
        }

        try
        {
            SshTargetConfig.ValidateSshToken(user, "User");
            SshTargetConfig.ValidateSshToken(host, "Host");
        }
        catch (ArgumentException ex)
        {
            throw KernelException.InvalidArgument(ex.Message);
        }

        return (user, host, port);
    }

    private static string? ExpandLocalIdentityPath(string? identityFile)
    {
        if (string.IsNullOrWhiteSpace(identityFile))
        {
            return null;
        }

        var trimmed = identityFile.Trim();
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                trimmed = Path.Combine(home, trimmed[2..]);
            }
        }

        return trimmed;
    }

    private string AllocateUniqueId(string label, string user, string host, bool isLoopback)
    {
        var seed = isLoopback ? user : (string.IsNullOrWhiteSpace(label) ? $"{user}-{host}" : label);
        var slug = Slugify(seed);
        if (string.IsNullOrEmpty(slug) || string.Equals(slug, ExecutionTargetIds.Local, StringComparison.OrdinalIgnoreCase))
        {
            slug = "ssh-target";
        }

        if (_registry.TryGet(slug) is null && !_staticTargetIds.Contains(slug))
        {
            return slug;
        }

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = $"{slug}-{i}";
            if (_registry.TryGet(candidate) is null && !_staticTargetIds.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";
    }

    private static string Slugify(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastDash = false;
        foreach (var ch in value.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (sb.Length > 0 && !lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }

            if (sb.Length >= 32)
            {
                break;
            }
        }

        return sb.ToString().Trim('-');
    }

    private SshTargetConfig BuildConfigFromStored(StoredExecutionTarget item) =>
        new(
            targetId: item.Id,
            options: new ExecutionTargetOptions
            {
                Kind = item.Kind,
                Label = item.Label,
                Host = item.Host,
                Port = item.Port,
                User = item.User,
                IdentityFile = item.IdentityFile,
                WorkspacesRoot = item.WorkspacesRoot
            },
            hostOarBridgeScriptPath: _options.OarBridgeScriptPath,
            removable: true);
}
