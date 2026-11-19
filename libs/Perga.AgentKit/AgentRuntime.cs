using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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

    public async Task<IReadOnlyList<PermissionModeOption>> ProbePermissionModesAsync(
        string provider, string? cwd, OsSandboxMode sandbox, IReadOnlyList<OverlayMountSpec>? overlayMounts, CancellationToken ct)
    {
        if (!provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            return ProviderPermissionModes.GetModesForProvider(provider);

        // Permission catalogues may depend on project configuration. Never cache a
        // workspace catalogue as provider-global metadata or borrow one from temp.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(GetModelProbeTimeout());
        await using var session = await OpenSessionAsync(new HarnessLaunchSpec(
            provider, null, cwd ?? Path.GetTempPath(), null, null,
            new Dictionary<string, McpServerConfig>(), Sandbox: sandbox, OverlayMounts: overlayMounts), probeCts.Token).ConfigureAwait(false);
        return ((AcpAgentSession)session).AvailablePermissionModes;
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

            if (string.Equals(provider.Trim(), "acp:antigravity", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(normalizedSpec.PermissionMode?.Trim(), "bypassPermissions", StringComparison.Ordinal))
            {
                args.Add("--dangerously-skip-permissions");
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
        var permissionModes = ProviderPermissionModes.GetModesForProvider(provider);

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
            return new ProviderStatus(provider, Available: false, Version: null, Reason: ex.Message, Models: [], PermissionModes: permissionModes);
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
                Models: [],
                PermissionModes: permissionModes);
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
                    Models: [],
                    PermissionModes: permissionModes);
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
                    Models: [],
                    PermissionModes: permissionModes);
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
                    Models: [],
                    PermissionModes: permissionModes);
            }

            var version = ExtractVersionString(stdout) ?? ExtractVersionString(stderr) ?? "unknown";
            IReadOnlyList<string> models;
            IReadOnlyList<ProviderModelInfo> catalog;
            if (provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            {
                (models, catalog, permissionModes) = await ProbeAcpModelsAsync(provider, ct).ConfigureAwait(false);
                if (cmdSpec.Models is not null)
                {
                    models = cmdSpec.Models;
                    catalog = BuildSimpleModelCatalog(models);
                }
            }
            else if (cmdSpec.Models is not null)
            {
                models = cmdSpec.Models;
                catalog = BuildSimpleModelCatalog(models);
            }
            else if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase))
            {
                (models, catalog) = await ProbeCodexModelsAsync(ct).ConfigureAwait(false);
            }
            else
            {
                models = GetDefaultModelsForProvider(provider);
                catalog = BuildSimpleModelCatalog(models);
            }

            return new ProviderStatus(
                provider,
                Available: true,
                Version: version,
                Reason: null,
                Models: models,
                ModelCatalog: catalog,
                PermissionModes: permissionModes);
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
                Models: [],
                PermissionModes: permissionModes);
        }
    }

    private TimeSpan GetModelProbeTimeout() =>
        TimeSpan.FromMilliseconds(Math.Min(_options.ModelProbeTimeout.TotalMilliseconds,
            Math.Min(_options.ProbeTimeout.TotalMilliseconds, _options.HandshakeTimeout.TotalMilliseconds)));

    private async Task<(IReadOnlyList<string> Models, IReadOnlyList<ProviderModelInfo> Catalog, IReadOnlyList<PermissionModeOption> Modes)> ProbeAcpModelsAsync(
        string provider,
        CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(GetModelProbeTimeout());
        IReadOnlyList<PermissionModeOption> modes = [];
        try
        {
            await using var session = await OpenSessionAsync(new HarnessLaunchSpec(
                provider, null, Path.GetTempPath(), null, null,
                new Dictionary<string, McpServerConfig>()), probeCts.Token).ConfigureAwait(false);
            var acp = (AcpAgentSession)session;
            modes = acp.AvailablePermissionModes;
            var catalog = acp.AvailableModelCatalog;
            var models = catalog.Count > 0 ? catalog.Select(m => m.Model).ToArray() : acp.AvailableModels;
            if (models.Count > 0)
            {
                return (models, catalog, modes);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fall through to provider-specific fallback or empty list.
        }

        if (string.Equals(provider, "acp:opencode", StringComparison.OrdinalIgnoreCase))
        {
            var cliModels = await ProbeOpenCodeCliModelsAsync(ct).ConfigureAwait(false);
            if (cliModels.Count > 0)
            {
                return (cliModels, BuildSimpleModelCatalog(cliModels), modes);
            }
        }

        return ([], [], modes);
    }

    private async Task<IReadOnlyList<string>> ProbeOpenCodeCliModelsAsync(CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(GetModelProbeTimeout());
        try
        {
            var (cmdOverride, _) = ResolveAcpCommandSpec("acp:opencode");
            var env = BuildProcessEnvironment(cmdOverride.Env);
            var exePath = FindExecutableOnPath(cmdOverride.FileName, env);
            if (exePath is null)
            {
                return [];
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("models");
            foreach (var (k, v) in env)
            {
                psi.Environment[k] = v;
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return [];
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(probeCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(probeCts.Token);
            string stdout;
            try
            {
                await process.WaitForExitAsync(probeCts.Token).ConfigureAwait(false);
                stdout = await SafeAwaitOutputAsync(stdoutTask).ConfigureAwait(false);
                await SafeAwaitOutputAsync(stderrTask).ConfigureAwait(false);
                probeCts.Token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    TryKillProcessTree(process);
                    return [];
                }
            }
            catch
            {
                TryKillProcessTree(process);
                throw;
            }
            finally
            {
                await SafeAwaitOutputAsync(stdoutTask).ConfigureAwait(false);
                await SafeAwaitOutputAsync(stderrTask).ConfigureAwait(false);
            }

            return stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static line => line.Contains('/', StringComparison.Ordinal) && !line.Contains(' ', StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<(IReadOnlyList<string> Models, IReadOnlyList<ProviderModelInfo> Catalog)> ProbeCodexModelsAsync(
        CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(GetModelProbeTimeout());
        try
        {
            var cmdOverride = ResolveCommandSpec("codex", defaultExecutable: "codex");
            var args = new List<string>();
            if (cmdOverride.PrependArgs is not null)
            {
                args.AddRange(cmdOverride.PrependArgs);
            }

            args.Add("app-server");
            var env = BuildProcessEnvironment(cmdOverride.Env);
            var invocation = new CommandInvocation(cmdOverride.FileName, args, Path.GetTempPath(), env);
            var process = StdioSubprocess.Start(invocation);
            await using var rpc = new JsonRpcStdioConnection(
                process,
                onNotification: static (_, _) => { },
                onServerRequest: static (_, _, _) => Task.FromResult<object?>(new { }),
                onDisconnected: static _ => { });

            await rpc.SendRequestAsync("initialize", new
            {
                clientInfo = new { name = "perga-probe", version = "1.0.0" }
            }, probeCts.Token).ConfigureAwait(false);

            var response = await rpc.SendRequestAsync("model/list", new { }, probeCts.Token).ConfigureAwait(false);
            if (response.ValueKind == JsonValueKind.Object &&
                response.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                var catalog = new List<ProviderModelInfo>();
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (item.TryGetProperty("hidden", out var hiddenProp) &&
                        hiddenProp.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    var model = TryGetJsonString(item, "model") ?? TryGetJsonString(item, "id");
                    if (string.IsNullOrWhiteSpace(model))
                    {
                        continue;
                    }

                    var displayName = TryGetJsonString(item, "displayName") ?? model;
                    var description = TryGetJsonString(item, "description") ?? string.Empty;
                    var isDefault = item.TryGetProperty("isDefault", out var defProp) && defProp.ValueKind == JsonValueKind.True;
                    var defaultEffort = TryGetJsonString(item, "defaultReasoningEffort");
                    var efforts = new List<ProviderModelEffort>();
                    if (item.TryGetProperty("supportedReasoningEfforts", out var effArr) &&
                        effArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var eff in effArr.EnumerateArray())
                        {
                            var effName = TryGetJsonString(eff, "reasoningEffort");
                            if (!string.IsNullOrWhiteSpace(effName))
                            {
                                efforts.Add(new ProviderModelEffort(effName, TryGetJsonString(eff, "description") ?? string.Empty));
                            }
                        }
                    }

                    catalog.Add(new ProviderModelInfo(
                        Model: model,
                        DisplayName: displayName,
                        Description: description,
                        IsDefault: isDefault,
                        DefaultReasoningEffort: defaultEffort,
                        SupportedReasoningEfforts: efforts));
                }

                if (catalog.Count > 0)
                {
                    return (catalog.Select(m => m.Model).ToArray(), catalog);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fall back to static defaults when codex app-server probe fails (e.g. mock CLI).
        }

        return (DefaultCodexModels, BuildSimpleModelCatalog(DefaultCodexModels));
    }

    private static IReadOnlyList<ProviderModelInfo> BuildSimpleModelCatalog(IReadOnlyList<string> models) =>
        models.Select((m, i) => new ProviderModelInfo(m, m, string.Empty, i == 0, null, [])).ToArray();

    private static string? TryGetJsonString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

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
        // Retain OpenCode's installer location for the default environment while
        // respecting a caller's explicit PATH boundary.
        if (!pathExplicitlyOverridden && !string.IsNullOrWhiteSpace(homeDir))
        {
            var openCodeBin = Path.Combine(homeDir, ".opencode", "bin");
            if (Directory.Exists(openCodeBin))
            {
                var segments = prioritizedPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                if (!segments.Contains(openCodeBin, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                    prioritizedPath = string.Join(Path.PathSeparator, [.. segments, openCodeBin]);
            }
        }
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
