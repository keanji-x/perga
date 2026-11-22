using System.Collections.Concurrent;
using System.Text.Json;
using Perga.AgentKit.Internal;
using Perga.Processes;

namespace Perga.AgentKit;

public sealed class AgentRuntime : IAgentRuntime
{
    private static readonly string[] CanonicalProviders =
    [
        "codex",
        "claude",
        "acp:antigravity",
        "acp:opencode"
    ];

    private readonly AgentRuntimeOptions _options;
    private readonly ISubprocessTransport _transport;
    private readonly ConcurrentDictionary<IAgentSession, byte> _activeSessions = new();
    private readonly ConcurrentDictionary<string, bool> _resumeCapabilities = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private IReadOnlyList<ProviderStatus>? _cachedProbeStatuses;
    private DateTimeOffset _cachedProbeExpiresAt = DateTimeOffset.MinValue;
    private int _disposed;
    private readonly object _sessionSync = new();

    public AgentRuntime() : this(new AgentRuntimeOptions()) { }

    public AgentRuntime(AgentRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _transport = options.SubprocessTransport ?? LocalSubprocessTransport.Instance;
    }

    public bool CanResumeSession(string provider)
    {
        var name = NormalizeProviderName(provider);
        return _resumeCapabilities.TryGetValue(name, out var supported)
            ? supported
            : OarNodeResolver.TryMapProviderToOarRuntime(name) is not null;
    }
    public static string NormalizeProviderName(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return string.Empty;
        var trimmed = provider.Trim();
        if (trimmed.Equals("antigravity", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("agy", StringComparison.OrdinalIgnoreCase))
            return "acp:antigravity";
        if (trimmed.Equals("opencode", StringComparison.OrdinalIgnoreCase))
            return "acp:opencode";
        if (trimmed.StartsWith("oar:", StringComparison.OrdinalIgnoreCase))
            return "oar:" + trimmed["oar:".Length..].Trim().ToLowerInvariant();
        return trimmed;
    }

    public Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct) =>
        ProbeProvidersAsync(forceRefresh: false, ct);

    public async Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(bool forceRefresh, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var ttl = _options.ProbeCacheTtl;
        if (!forceRefresh && ttl > TimeSpan.Zero &&
            Volatile.Read(ref _cachedProbeStatuses) is { } cached &&
            _options.TimeProvider.GetUtcNow() < _cachedProbeExpiresAt)
        {
            return cached;
        }

        await _probeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!forceRefresh && ttl > TimeSpan.Zero && _cachedProbeStatuses is not null &&
                _options.TimeProvider.GetUtcNow() < _cachedProbeExpiresAt)
            {
                return _cachedProbeStatuses;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var providers = new List<string>(CanonicalProviders.Length + _options.ProviderOverrides.Count);
            foreach (var canonical in CanonicalProviders)
            {
                if (seen.Add(canonical)) providers.Add(canonical);
            }
            foreach (var custom in _options.ProviderOverrides.Keys)
            {
                var normalized = NormalizeProviderName(custom);
                if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                    providers.Add(normalized);
            }

            var results = await Task.WhenAll(providers.Select(p => ProbeSingleProviderAsync(p, ct))).ConfigureAwait(false);
            if (ttl > TimeSpan.Zero)
            {
                _cachedProbeExpiresAt = _options.TimeProvider.GetUtcNow().Add(ttl);
                Volatile.Write(ref _cachedProbeStatuses, results);
            }

            return results;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    public IAgentSession CreateSession(HarnessLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (string.IsNullOrWhiteSpace(spec.Provider))
            throw new ArgumentException("HarnessLaunchSpec.Provider must not be empty.", nameof(spec));
        if (string.IsNullOrWhiteSpace(spec.Cwd))
            throw new ArgumentException("HarnessLaunchSpec.Cwd must not be empty.", nameof(spec));

        var fullCwd = _transport.IsLocal ? Path.GetFullPath(spec.Cwd) : NormalizeRemotePath(spec.Cwd);
        if (!_transport.DirectoryExists(fullCwd))
            throw new DirectoryNotFoundException($"Workspace Cwd directory does not exist: '{fullCwd}'.");

        var provider = NormalizeProviderName(spec.Provider);
        var oarRuntimeId = OarNodeResolver.TryMapProviderToOarRuntime(provider)
            ?? throw new NotSupportedException($"Unsupported provider '{provider}'.");

        var bridgeScript = _transport.ResolveBridgeScriptPath(_options.BridgeScriptPath);
        var (exeOverride, extraEnv) = ResolveOverride(provider, oarRuntimeId);
        var env = BuildProcessEnvironment(extraEnv);
        var nodeExe = _transport.ResolveNodeExecutable(_options.NodeExecutablePath, env);

        var invocation = new CommandInvocation(nodeExe, [bridgeScript], fullCwd, env);
        lock (_sessionSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var session = OarAgentSession.Create(
                invocation, spec with { Provider = provider, Cwd = fullCwd }, oarRuntimeId,
                exeOverride, _options.HandshakeTimeout, UnregisterSession,
                initialized => _resumeCapabilities[provider] = initialized.SupportsSessionResume,
                _transport);
            _activeSessions.TryAdd(session, 0);
            return session;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IAgentSession[] sessions;
        lock (_sessionSync)
        {
            Interlocked.Exchange(ref _disposed, 1);
            sessions = _activeSessions.Keys.ToArray();
        }
        foreach (var session in sessions)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private void UnregisterSession(IAgentSession session) => _activeSessions.TryRemove(session, out _);

    private async Task<ProviderStatus> ProbeSingleProviderAsync(string provider, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var permissionModes = ProviderPermissionModes.GetModesForProvider(provider);
        var oarRuntimeId = OarNodeResolver.TryMapProviderToOarRuntime(provider);
        if (string.IsNullOrWhiteSpace(oarRuntimeId))
        {
            return new ProviderStatus(provider, Available: false, Version: null,
                Reason: $"Unsupported provider '{provider}'.", Models: [], PermissionModes: permissionModes);
        }

        try
        {
            var bridgeScript = _transport.ResolveBridgeScriptPath(_options.BridgeScriptPath);
            var (exeOverride, extraEnv) = ResolveOverride(provider, oarRuntimeId);
            var env = BuildProcessEnvironment(extraEnv);
            var nodeExe = _transport.ResolveNodeExecutable(_options.NodeExecutablePath, env);
            var cwd = _transport.TempDirectory;
            var timeout = _options.ProbeTimeout > TimeSpan.Zero ? _options.ProbeTimeout : TimeSpan.FromSeconds(30);

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(timeout);

            var process = _transport.StartInteractive(new CommandInvocation(nodeExe, [bridgeScript], cwd, env));
            await using var rpc = new JsonRpcStdioConnection(
                process, static (_, _) => { }, static (_, _, _) => { }, static _ => { });

            var response = await rpc.SendRequestAsync("probe", new Dictionary<string, object?>
            {
                ["runtime"] = oarRuntimeId,
                ["cwd"] = cwd,
                ["executable"] = exeOverride,
                ["timeoutMs"] = (int)timeout.TotalMilliseconds
            }, probeCts.Token).ConfigureAwait(false);

            var available = response.TryGetProperty("available", out var avail) && avail.ValueKind == JsonValueKind.True;
            var catalog = ParseModelCatalog(response);
            return new ProviderStatus(
                Provider: provider,
                Available: available,
                Version: TryGetJsonString(response, "version"),
                Reason: TryGetJsonString(response, "reason"),
                Models: catalog.Select(m => m.Model).ToArray(),
                ModelCatalog: catalog,
                PermissionModes: permissionModes,
                ConfigurationCapabilities: available ? SessionConfigurationCapabilities.LaunchOnly : SessionConfigurationCapabilities.Unknown);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProviderStatus(provider, Available: false, Version: null,
                Reason: ex.Message, Models: [], PermissionModes: permissionModes);
        }
    }

    private static string NormalizeRemotePath(string rawPath)
    {
        var trimmed = rawPath.Trim();
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private (string? ExecutableOverride, IReadOnlyDictionary<string, string>? ExtraEnv) ResolveOverride(string provider, string oarRuntimeId) =>
        _options.ProviderOverrides.TryGetValue(provider, out var configured) ||
        _options.ProviderOverrides.TryGetValue(oarRuntimeId, out configured)
            ? (configured.FileName, configured.Env)
            : (null, null);

    private Dictionary<string, string> BuildProcessEnvironment(IReadOnlyDictionary<string, string>? overrides)
    {
        if (!_transport.IsLocal)
        {
            // Never leak the Host user's HOME/PATH/credentials into another Linux user or remote SSH target.
            var remoteEnv = new Dictionary<string, string>(StringComparer.Ordinal);
            if (overrides is not null)
            {
                foreach (var (key, value) in overrides)
                {
                    remoteEnv[key] = value;
                }
            }

            return remoteEnv;
        }

        var env = ChildProcessEnvironment.Capture();
        var comparer = env.Comparer;

        var pathOverridden = false;
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase)) pathOverridden = true;
                env[key] = value;
            }
        }

        if (!pathOverridden)
        {
            var home = env.GetValueOrDefault("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                var segments = (env.GetValueOrDefault("PATH") ?? string.Empty)
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
                foreach (var extra in new[] { Path.Combine(home, ".local", "bin"), Path.Combine(home, ".opencode", "bin") })
                {
                    if (Directory.Exists(extra) && !segments.Contains(extra, comparer))
                        segments.Add(extra);
                }
                env["PATH"] = string.Join(Path.PathSeparator, segments);
            }
        }

        return env;
    }

    internal static string? FindExecutableOnPath(string executable, IReadOnlyDictionary<string, string>? env)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            var full = Path.GetFullPath(executable);
            return File.Exists(full) ? full : null;
        }

        var pathVar = env?.GetValueOrDefault("PATH") ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar)) return null;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate)) return candidate;
            if (OperatingSystem.IsWindows() && !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }
        return null;
    }

    private static IReadOnlyList<ProviderModelInfo> ParseModelCatalog(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array)
            return [];

        var catalog = new List<ProviderModelInfo>();
        foreach (var item in models.EnumerateArray())
        {
            var id = TryGetJsonString(item, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var efforts = new List<ProviderModelEffort>();
            if (item.TryGetProperty("effortLevels", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var eff in arr.EnumerateArray())
                {
                    if (eff.ValueKind == JsonValueKind.String && eff.GetString() is { Length: > 0 } level)
                        efforts.Add(new ProviderModelEffort(level, string.Empty));
                }
            }
            catalog.Add(new ProviderModelInfo(
                Model: id,
                DisplayName: TryGetJsonString(item, "displayName") ?? id,
                Description: TryGetJsonString(item, "description") ?? string.Empty,
                IsDefault: item.TryGetProperty("isDefault", out var def) && def.ValueKind == JsonValueKind.True,
                DefaultReasoningEffort: TryGetJsonString(item, "defaultEffort"),
                SupportedReasoningEfforts: efforts));
        }
        return catalog;
    }

    private static string? TryGetJsonString(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;
}
