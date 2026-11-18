using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Perga.AgentKit.Internal;

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

    private static readonly string[] DefaultCodexModels = ["gpt-5.4", "gpt-5.3-codex", "o4-mini"];
    private static readonly string[] DefaultClaudeModels = ["claude-sonnet-4-6", "claude-opus-4-6"];
    private static readonly string[] DefaultAntigravityModels =
    [
        "gemini-4-carbon-medium",
        "gemini-4-carbon-high",
        "gemini-3.8-flash-high",
        "claude-sonnet-5-5-medium"
    ];
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly AgentRuntimeOptions _options;
    private readonly OsSandboxWrapper _sandboxWrapper;
    private readonly ConcurrentDictionary<IAgentSession, byte> _activeSessions = new();
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private IReadOnlyList<ProviderStatus>? _cachedProbeStatuses;
    private DateTimeOffset _cachedProbeExpiresAt = DateTimeOffset.MinValue;
    private int _disposed;

    public AgentRuntime()
        : this(new AgentRuntimeOptions())
    {
    }

    public AgentRuntime(AgentRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _sandboxWrapper = new OsSandboxWrapper(options.Sandbox);
    }

    public OsSandboxWrapper SandboxWrapper => _sandboxWrapper;

    public static string NormalizeProviderName(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return string.Empty;
        }

        var trimmed = provider.Trim();
        if (string.Equals(trimmed, "antigravity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "agy", StringComparison.OrdinalIgnoreCase))
        {
            return "acp:antigravity";
        }

        if (string.Equals(trimmed, "opencode", StringComparison.OrdinalIgnoreCase))
        {
            return "acp:opencode";
        }

        return trimmed;
    }

    public Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct) =>
        ProbeProvidersAsync(forceRefresh: false, ct);

    public async Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(
        bool forceRefresh,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var ttl = _options.ProbeCacheTtl;
        if (!forceRefresh && ttl > TimeSpan.Zero)
        {
            var cached = Volatile.Read(ref _cachedProbeStatuses);
            if (cached is not null && _options.TimeProvider.GetUtcNow() < _cachedProbeExpiresAt)
            {
                return cached;
            }
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

            var providerNames = new List<string>(CanonicalProviders.Length + _options.ProviderOverrides.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var canonical in CanonicalProviders)
            {
                if (seen.Add(canonical))
                {
                    providerNames.Add(canonical);
                }
            }

            foreach (var customKey in _options.ProviderOverrides.Keys)
            {
                if (string.IsNullOrWhiteSpace(customKey))
                {
                    continue;
                }

                var normalizedCustom = NormalizeProviderName(customKey);
                if (seen.Add(normalizedCustom))
                {
                    providerNames.Add(normalizedCustom);
                }
            }

            var probeTasks = providerNames
                .Select(provider => ProbeSingleProviderAsync(provider, ct))
                .ToArray();

            var freshResults = await Task.WhenAll(probeTasks).ConfigureAwait(false);
            if (ttl > TimeSpan.Zero)
            {
                _cachedProbeExpiresAt = _options.TimeProvider.GetUtcNow().Add(ttl);
                Volatile.Write(ref _cachedProbeStatuses, freshResults);
            }

            return freshResults;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    public async Task<IAgentSession> OpenSessionAsync(HarnessLaunchSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(spec.Provider))
        {
            throw new ArgumentException("HarnessLaunchSpec.Provider must not be empty.", nameof(spec));
        }

        if (string.IsNullOrWhiteSpace(spec.Cwd))
        {
            throw new ArgumentException("HarnessLaunchSpec.Cwd must not be empty.", nameof(spec));
        }

        var fullCwd = Path.GetFullPath(spec.Cwd);
        if (!Directory.Exists(fullCwd))
        {
            throw new DirectoryNotFoundException($"Workspace Cwd directory does not exist: '{fullCwd}'.");
        }

        var provider = NormalizeProviderName(spec.Provider);
        var normalizedSpec = spec with { Provider = provider, Cwd = fullCwd };

        IAgentSession session;
        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            var cmdOverride = ResolveCommandSpec(provider, defaultExecutable: "codex");
            var args = new List<string>();
            if (cmdOverride.PrependArgs is not null)
            {
                args.AddRange(cmdOverride.PrependArgs);
            }

            args.Add("app-server");
            var env = BuildProcessEnvironment(cmdOverride.Env);
            var resolvedExe = FindExecutableOnPath(cmdOverride.FileName, env) ?? cmdOverride.FileName;
            var stateDirectory = PrepareCodexSandboxState(normalizedSpec, env);
            var sandboxApiKey = stateDirectory is null ? null : _options.CodexSandboxApiKey;
            if (stateDirectory is not null)
            {
                args.Add("-c");
                args.Add("cli_auth_credentials_store=\"file\"");
            }
            var rawInvocation = new CommandInvocation(resolvedExe, args, fullCwd, env);
            var wrapped = _sandboxWrapper.Wrap(rawInvocation, normalizedSpec.Sandbox, normalizedSpec.OverlayMounts,
                writableStateDirectory: stateDirectory);
            session = await CodexAgentSession.OpenAsync(
                wrapped,
                normalizedSpec,
                _options.HandshakeTimeout,
                UnregisterSession,
                ct,
                sandboxApiKey).ConfigureAwait(false);
        }
        else if (string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase))
        {
            var cmdOverride = ResolveCommandSpec(provider, defaultExecutable: "claude");
            var env = BuildProcessEnvironment(cmdOverride.Env);
            var resolvedExe = FindExecutableOnPath(cmdOverride.FileName, env) ?? cmdOverride.FileName;
            var effectiveCmd = cmdOverride with { FileName = resolvedExe };
            session = await ClaudeAgentSession.OpenAsync(
                effectiveCmd,
                _sandboxWrapper,
                normalizedSpec,
                env,
                UnregisterSession,
                ct).ConfigureAwait(false);
        }
        else if (provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
        {
            var (cmdOverride, appendAcpSubcommand) = ResolveAcpCommandSpec(provider);
            var args = new List<string>();
            if (cmdOverride.PrependArgs is not null)
            {
                args.AddRange(cmdOverride.PrependArgs);
            }

            if (appendAcpSubcommand)
            {
                args.Add("acp");
            }

            var env = BuildProcessEnvironment(cmdOverride.Env);
            var resolvedExe = FindExecutableOnPath(cmdOverride.FileName, env) ?? cmdOverride.FileName;
            var rawInvocation = new CommandInvocation(resolvedExe, args, fullCwd, env);
            var wrapped = _sandboxWrapper.Wrap(rawInvocation, normalizedSpec.Sandbox, normalizedSpec.OverlayMounts);
            session = await AcpAgentSession.OpenAsync(
                wrapped,
                normalizedSpec,
                _options.HandshakeTimeout,
                UnregisterSession,
                ct).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException(
                $"Unsupported AgentKit provider '{provider}'. Supported surfaces: 'codex', 'claude', 'acp:antigravity', 'acp:opencode', 'acp:<cmd>'.");
        }

        _activeSessions.TryAdd(session, 0);
        if (Volatile.Read(ref _disposed) != 0)
        {
            _activeSessions.TryRemove(session, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(AgentRuntime));
        }

        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sessions = _activeSessions.Keys.ToArray();
        _activeSessions.Clear();

        foreach (var session in sessions)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup of active sessions.
            }
        }
    }

    private void UnregisterSession(IAgentSession session) =>
        _activeSessions.TryRemove(session, out _);

    private ProviderCommandOverride ResolveCommandSpec(string provider, string defaultExecutable)
    {
        if (_options.ProviderOverrides.TryGetValue(provider, out var configured))
        {
            return configured;
        }

        return new ProviderCommandOverride(defaultExecutable);
    }

    private (ProviderCommandOverride Command, bool AppendAcpSubcommand) ResolveAcpCommandSpec(string provider)
    {
        var acpTarget = provider["acp:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(acpTarget))
        {
            throw new ArgumentException($"ACP provider '{provider}' must specify a target after 'acp:'.", nameof(provider));
        }

        var isKnownSubcommandTarget =
            string.Equals(acpTarget, "antigravity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(acpTarget, "opencode", StringComparison.OrdinalIgnoreCase);

        if (_options.ProviderOverrides.TryGetValue(provider, out var configured) ||
            _options.ProviderOverrides.TryGetValue(acpTarget, out configured))
        {
            return (configured, isKnownSubcommandTarget);
        }

        if (string.Equals(acpTarget, "antigravity", StringComparison.OrdinalIgnoreCase))
        {
            var probeEnv = BuildProcessEnvironment(null);
            var exe = FindExecutableOnPath("agy", probeEnv) is not null
                ? "agy"
                : FindExecutableOnPath("antigravity", probeEnv) is not null
                    ? "antigravity"
                    : "agy";
            return (new ProviderCommandOverride(exe), AppendAcpSubcommand: true);
        }

        if (string.Equals(acpTarget, "opencode", StringComparison.OrdinalIgnoreCase))
        {
            return (new ProviderCommandOverride("opencode"), AppendAcpSubcommand: true);
        }

        return (new ProviderCommandOverride(acpTarget), AppendAcpSubcommand: false);
    }

    private async Task<ProviderStatus> ProbeSingleProviderAsync(string provider, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        ProviderCommandOverride cmdSpec;
        var appendAcpSubcommand = false;
        try
        {
            if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
            {
                cmdSpec = ResolveCommandSpec(provider, "codex");
            }
            else if (string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase))
            {
                cmdSpec = ResolveCommandSpec(provider, "claude");
            }
            else if (provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            {
                (cmdSpec, appendAcpSubcommand) = ResolveAcpCommandSpec(provider);
            }
            else
            {
                cmdSpec = ResolveCommandSpec(provider, provider);
            }
        }
        catch (Exception ex)
        {
            return new ProviderStatus(provider, Available: false, Version: null, Reason: ex.Message, Models: []);
        }

        var env = BuildProcessEnvironment(cmdSpec.Env);
        var resolvedExe = FindExecutableOnPath(cmdSpec.FileName, env);
        if (resolvedExe is null)
        {
            return new ProviderStatus(
                provider,
                Available: false,
                Version: null,
                Reason: $"Executable '{cmdSpec.FileName}' was not found on PATH.",
                Models: []);
        }

        var psi = new ProcessStartInfo
        {
            FileName = resolvedExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };

        if (cmdSpec.PrependArgs is not null)
        {
            foreach (var arg in cmdSpec.PrependArgs)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        psi.ArgumentList.Add("--version");

        foreach (var kvp in env)
        {
            psi.Environment[kvp.Key] = kvp.Value;
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new ProviderStatus(
                    provider,
                    Available: false,
                    Version: null,
                    Reason: $"Failed to start '{resolvedExe} --version'.",
                    Models: []);
            }

            process.StandardInput.Close();

            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(_options.ProbeTimeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(probeCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(probeCts.Token);

            try
            {
                await process.WaitForExitAsync(probeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                return new ProviderStatus(
                    provider,
                    Available: false,
                    Version: null,
                    Reason: $"Timed out after {_options.ProbeTimeout.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms running --version.",
                    Models: []);
            }

            var stdout = await SafeAwaitOutputAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await SafeAwaitOutputAsync(stderrTask).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var errDetail = ExtractFirstNonEmptyLine(stderr)
                                ?? ExtractFirstNonEmptyLine(stdout)
                                ?? $"Process exited with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}.";
                return new ProviderStatus(
                    provider,
                    Available: false,
                    Version: null,
                    Reason: errDetail,
                    Models: []);
            }

            var version = ExtractVersionString(stdout) ?? ExtractVersionString(stderr) ?? "unknown";
            var models = cmdSpec.Models ?? await ProbeModelsForProviderAsync(
                provider,
                resolvedExe,
                cmdSpec.PrependArgs,
                appendAcpSubcommand,
                env,
                ct).ConfigureAwait(false);

            return new ProviderStatus(
                provider,
                Available: true,
                Version: version,
                Reason: null,
                Models: models);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            throw;
        }
        catch (Exception ex)
        {
            TryKillProcessTree(process);
            return new ProviderStatus(
                provider,
                Available: false,
                Version: null,
                Reason: ex.Message,
                Models: []);
        }
    }

    private async Task<IReadOnlyList<string>> ProbeModelsForProviderAsync(
        string provider,
        string resolvedExe,
        IReadOnlyList<string>? prependArgs,
        bool appendAcpSubcommand,
        IReadOnlyDictionary<string, string> env,
        CancellationToken ct)
    {
        var fallback = GetDefaultModelsForProvider(provider);
        var budgetMs = Math.Min(
            _options.ModelProbeTimeout.TotalMilliseconds,
            _options.ProbeTimeout.TotalMilliseconds);
        if (budgetMs <= 0)
        {
            return fallback;
        }

        var budget = TimeSpan.FromMilliseconds(budgetMs);

        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            var probed = await TryProbeCodexModelsAsync(resolvedExe, prependArgs, env, budget, ct)
                .ConfigureAwait(false);
            return probed is { Count: > 0 } ? probed : fallback;
        }

        if (string.Equals(provider, "acp:antigravity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(provider, "acp:opencode", StringComparison.OrdinalIgnoreCase))
        {
            var probed = await TryProbeAcpModelsAsync(resolvedExe, prependArgs, appendAcpSubcommand, env, budget, ct)
                .ConfigureAwait(false);
            return probed is { Count: > 0 } ? probed : fallback;
        }

        return fallback;
    }

    private static async Task<IReadOnlyList<string>?> TryProbeCodexModelsAsync(
        string resolvedExe,
        IReadOnlyList<string>? prependArgs,
        IReadOnlyDictionary<string, string> env,
        TimeSpan budget,
        CancellationToken ct)
    {
        var args = new List<string>();
        if (prependArgs is not null)
        {
            args.AddRange(prependArgs);
        }

        args.Add("app-server");
        var invocation = new CommandInvocation(resolvedExe, args, Path.GetTempPath(), env);

        StdioSubprocess? process = null;
        JsonRpcStdioConnection? rpc = null;
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(budget);

            process = StdioSubprocess.Start(invocation);
            rpc = new JsonRpcStdioConnection(
                process,
                onNotification: static (_, _) => { },
                onServerRequest: static (_, _, _) => { },
                onDisconnected: static _ => { });

            var initParams = new Dictionary<string, object?>
            {
                ["clientInfo"] = new Dictionary<string, object?>
                {
                    ["name"] = "perga-agentkit",
                    ["version"] = "1.0.0"
                },
                ["capabilities"] = new Dictionary<string, object?>
                {
                    ["experimentalApi"] = true
                }
            };

            await rpc.SendRequestAsync("initialize", initParams, probeCts.Token).ConfigureAwait(false);
            await rpc.SendNotificationAsync("initialized", new Dictionary<string, object?>(), probeCts.Token).ConfigureAwait(false);
            var listResult = await rpc.SendRequestAsync("model/list", new Dictionary<string, object?>(), probeCts.Token)
                .ConfigureAwait(false);
            return ModelListParsers.ParseCodexModels(listResult);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (rpc is not null)
            {
                await rpc.DisposeAsync().ConfigureAwait(false);
            }
            else if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<IReadOnlyList<string>?> TryProbeAcpModelsAsync(
        string resolvedExe,
        IReadOnlyList<string>? prependArgs,
        bool appendAcpSubcommand,
        IReadOnlyDictionary<string, string> env,
        TimeSpan budget,
        CancellationToken ct)
    {
        var args = new List<string>();
        if (prependArgs is not null)
        {
            args.AddRange(prependArgs);
        }

        if (appendAcpSubcommand)
        {
            args.Add("acp");
        }

        var probeCwd = Path.GetFullPath(Path.GetTempPath());
        var invocation = new CommandInvocation(resolvedExe, args, probeCwd, env);

        StdioSubprocess? process = null;
        JsonRpcStdioConnection? rpc = null;
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(budget);

            process = StdioSubprocess.Start(invocation);
            rpc = new JsonRpcStdioConnection(
                process,
                onNotification: static (_, _) => { },
                onServerRequest: static (_, _, _) => { },
                onDisconnected: static _ => { });

            var initParams = new Dictionary<string, object?>
            {
                ["protocolVersion"] = 1,
                ["clientCapabilities"] = new Dictionary<string, object?>
                {
                    ["fs"] = new Dictionary<string, object?>
                    {
                        ["readTextFile"] = false,
                        ["writeTextFile"] = false
                    },
                    ["terminal"] = false
                },
                ["clientInfo"] = new Dictionary<string, object?>
                {
                    ["name"] = "perga-agentkit",
                    ["version"] = "1.0.0"
                }
            };

            var initResult = await rpc.SendRequestAsync("initialize", initParams, probeCts.Token).ConfigureAwait(false);
            var sessionParams = new Dictionary<string, object?>
            {
                ["cwd"] = probeCwd,
                ["mcpServers"] = Array.Empty<object>()
            };
            var sessionResult = await rpc.SendRequestAsync("session/new", sessionParams, probeCts.Token).ConfigureAwait(false);

            var sessionModels = ModelListParsers.ParseAcpModels(sessionResult);
            if (sessionModels.Count > 0)
            {
                return sessionModels;
            }

            return ModelListParsers.ParseAcpModels(initResult);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (rpc is not null)
            {
                await rpc.DisposeAsync().ConfigureAwait(false);
            }
            else if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<string> GetDefaultModelsForProvider(string provider)
    {
        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultCodexModels;
        }

        if (string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultClaudeModels;
        }

        if (string.Equals(provider, "acp:antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultAntigravityModels;
        }

        return [];
    }

    private string? PrepareCodexSandboxState(HarnessLaunchSpec spec, Dictionary<string, string> env)
    {
        if (!OperatingSystem.IsLinux() || spec.Sandbox == OsSandboxMode.None ||
            _options.SandboxStateRootDirectory is not { } root || spec.StateKey is not { } key)
            return null;

        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var directory = Path.Combine(Path.GetFullPath(root), "codex", digest);
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(directory, ownerOnly);
        if (new DirectoryInfo(directory).LinkTarget is not null)
            throw new IOException("The Codex state directory must not be a symbolic link.");

        var configuredHome = env.GetValueOrDefault("CODEX_HOME");
        var hostHome = _options.Sandbox.HomeDirectoryOverride ?? env.GetValueOrDefault("HOME");
        var source = !string.IsNullOrWhiteSpace(configuredHome) ? configuredHome
            : !string.IsNullOrWhiteSpace(hostHome) ? Path.Combine(hostHome, ".codex") : null;
        // Authentication is configured separately so sessions never share rotating login credentials.
        if (source is not null)
            CopyCodexStateSeed(source, directory, "config.toml");

        env["CODEX_HOME"] = directory;
        return directory;
    }

    private static void CopyCodexStateSeed(string source, string directory, string name)
    {
        var input = Path.Combine(source, name);
        var destination = Path.Combine(directory, name);
        if (!File.Exists(input) || File.Exists(destination) || new FileInfo(destination).LinkTarget is not null)
            return;

        try
        {
            File.Copy(input, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination))
        {
            // Another launch seeded this state first.
        }
    }

    private Dictionary<string, string> BuildProcessEnvironment(IReadOnlyDictionary<string, string>? overrides)
    {
        var env = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var current = Environment.GetEnvironmentVariables();
        foreach (System.Collections.DictionaryEntry entry in current)
        {
            if (entry.Key is string key && entry.Value is string value &&
                !key.Equals("Perga__CodexSandboxApiKey", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("Perga:CodexSandboxApiKey", StringComparison.OrdinalIgnoreCase))
            {
                env[key] = value;
            }
        }

        var pathExplicitlyOverridden = false;
        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (string.Equals(key, "PATH", StringComparison.OrdinalIgnoreCase))
                {
                    pathExplicitlyOverridden = true;
                }

                env[key] = value;
            }
        }

        var homeDir = _options.Sandbox.HomeDirectoryOverride;
        if (string.IsNullOrWhiteSpace(homeDir))
        {
            env.TryGetValue("HOME", out homeDir);
        }

        if (string.IsNullOrWhiteSpace(homeDir))
        {
            homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        env.TryGetValue("PATH", out var rawPath);
        var prioritizedPath = PrioritizeUserLocalBinInPath(
            rawPath,
            homeDir,
            allowAppendWhenMissing: !pathExplicitlyOverridden);
        if (!string.IsNullOrWhiteSpace(prioritizedPath))
        {
            env["PATH"] = prioritizedPath;
        }

        return env;
    }

    internal static string PrioritizeUserLocalBinInPath(
        string? pathVar,
        string? homeDir,
        bool allowAppendWhenMissing = true)
    {
        var segments = string.IsNullOrWhiteSpace(pathVar)
            ? new List<string>()
            : pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();

        if (string.IsNullOrWhiteSpace(homeDir))
        {
            return string.Join(Path.PathSeparator, segments);
        }

        string userLocalBin;
        try
        {
            userLocalBin = Path.GetFullPath(Path.Combine(homeDir, ".local", "bin"));
        }
        catch
        {
            return string.Join(Path.PathSeparator, segments);
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var firstLegacyIdx = -1;
        var existingUserIdx = -1;

        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (firstLegacyIdx < 0 && IsLegacyNeigeLocalBinSegment(seg))
            {
                firstLegacyIdx = i;
            }

            if (existingUserIdx < 0)
            {
                try
                {
                    var normSeg = Path.GetFullPath(seg).TrimEnd(Path.DirectorySeparatorChar);
                    var normUser = userLocalBin.TrimEnd(Path.DirectorySeparatorChar);
                    if (pathComparer.Equals(normSeg, normUser))
                    {
                        existingUserIdx = i;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entry.
                }
            }
        }

        if (firstLegacyIdx >= 0)
        {
            if (existingUserIdx > firstLegacyIdx)
            {
                var entry = segments[existingUserIdx];
                segments.RemoveAt(existingUserIdx);
                segments.Insert(firstLegacyIdx, entry);
            }
            else if (existingUserIdx < 0 && Directory.Exists(userLocalBin))
            {
                segments.Insert(firstLegacyIdx, userLocalBin);
            }
        }
        else if (existingUserIdx < 0 && allowAppendWhenMissing && Directory.Exists(userLocalBin))
        {
            segments.Add(userLocalBin);
        }

        return string.Join(Path.PathSeparator, segments);
    }

    private static bool IsLegacyNeigeLocalBinSegment(string segment)
    {
        var normalized = segment.Replace('\\', '/');
        return normalized.Contains("neige-calm", StringComparison.OrdinalIgnoreCase) &&
               normalized.Contains(".local-bin", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? FindExecutableOnPath(string executable, IReadOnlyDictionary<string, string>? env)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        if (executable.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            executable.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            var full = Path.GetFullPath(executable);
            return File.Exists(full) ? full : null;
        }

        string? pathVar = null;
        string? homeDir = null;
        if (env is not null)
        {
            env.TryGetValue("PATH", out pathVar);
            env.TryGetValue("HOME", out homeDir);
        }

        pathVar ??= Environment.GetEnvironmentVariable("PATH");
        homeDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var prioritized = PrioritizeUserLocalBinInPath(pathVar, homeDir, allowAppendWhenMissing: env is null);
        if (string.IsNullOrWhiteSpace(prioritized))
        {
            return null;
        }

        var segments = prioritized.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in segments)
        {
            try
            {
                var candidate = Path.Combine(dir, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                if (OperatingSystem.IsWindows() && !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var exeCandidate = candidate + ".exe";
                    if (File.Exists(exeCandidate))
                    {
                        return exeCandidate;
                    }
                }
            }
            catch
            {
                // Ignore malformed PATH segment.
            }
        }

        return null;
    }

    private static async Task<string> SafeAwaitOutputAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? ExtractVersionString(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : lines[^1];
    }

    private static string? ExtractFirstNonEmptyLine(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : lines[0];
    }

    private static void TryKillProcessTree(Process process)
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
            // Ignore kill failures on already-exited processes.
        }
    }
}
