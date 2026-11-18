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

    private static readonly string[] DefaultCodexModels = ["gpt-5.4", "gpt-5.3-codex"];
    private static readonly string[] DefaultClaudeModels = ["claude-sonnet-4-6", "claude-opus-4-6"];
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan ProbeCacheTtl = TimeSpan.FromSeconds(60);

    private readonly AgentRuntimeOptions _options;
    private readonly OsSandboxWrapper _sandboxWrapper;
    private readonly ConcurrentDictionary<IAgentSession, byte> _activeSessions = new();
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private IReadOnlyList<ProviderStatus>? _cachedProbeStatuses;
    private long _cachedProbeAtMs;
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

    public async Task<IReadOnlyList<ProviderStatus>> ProbeProvidersAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        var nowMs = Environment.TickCount64;
        var cached = Volatile.Read(ref _cachedProbeStatuses);
        if (cached is not null && nowMs - Interlocked.Read(ref _cachedProbeAtMs) < (long)ProbeCacheTtl.TotalMilliseconds)
        {
            return cached;
        }

        await _probeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            nowMs = Environment.TickCount64;
            cached = Volatile.Read(ref _cachedProbeStatuses);
            if (cached is not null && nowMs - Interlocked.Read(ref _cachedProbeAtMs) < (long)ProbeCacheTtl.TotalMilliseconds)
            {
                return cached;
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
                if (!string.IsNullOrWhiteSpace(customKey) && seen.Add(customKey))
                {
                    providerNames.Add(customKey);
                }
            }

            var probeTasks = providerNames
                .Select(provider => ProbeSingleProviderAsync(provider, ct))
                .ToArray();

            var results = await Task.WhenAll(probeTasks).ConfigureAwait(false);
            Volatile.Write(ref _cachedProbeStatuses, results);
            Interlocked.Exchange(ref _cachedProbeAtMs, Environment.TickCount64);
            return results;
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

        var normalizedSpec = spec with { Cwd = fullCwd };
        var provider = normalizedSpec.Provider.Trim();

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
            var exePath = FindExecutableOnPath(cmdOverride.FileName, env) ?? cmdOverride.FileName;
            var rawInvocation = new CommandInvocation(exePath, args, fullCwd, env);
            var wrapped = _sandboxWrapper.Wrap(rawInvocation, normalizedSpec.Sandbox, normalizedSpec.OverlayMounts);
            session = await CodexAgentSession.OpenAsync(
                wrapped,
                normalizedSpec,
                _options.HandshakeTimeout,
                UnregisterSession,
                ct).ConfigureAwait(false);
        }
        else if (string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase))
        {
            var cmdOverride = ResolveCommandSpec(provider, defaultExecutable: "claude");
            var env = BuildProcessEnvironment(cmdOverride.Env);
            var exePath = FindExecutableOnPath(cmdOverride.FileName, env) ?? cmdOverride.FileName;
            session = await ClaudeAgentSession.OpenAsync(
                cmdOverride with { FileName = exePath },
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
            var exePath = FindExecutableOnPath(cmdOverride.FileName, env) ?? cmdOverride.FileName;
            var rawInvocation = new CommandInvocation(exePath, args, fullCwd, env);
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

        if (_options.ProviderOverrides.TryGetValue(provider, out var configured))
        {
            var isKnownSubcommandTarget =
                string.Equals(acpTarget, "antigravity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(acpTarget, "opencode", StringComparison.OrdinalIgnoreCase);
            return (configured, isKnownSubcommandTarget);
        }

        if (string.Equals(acpTarget, "antigravity", StringComparison.OrdinalIgnoreCase))
        {
            var exe = FindExecutableOnPath("antigravity", null) is not null
                ? "antigravity"
                : FindExecutableOnPath("agy", null) is not null
                    ? "agy"
                    : "antigravity";
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
                (cmdSpec, _) = ResolveAcpCommandSpec(provider);
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
            IReadOnlyList<string> models;
            IReadOnlyList<ProviderModelInfo> catalog;
            if (cmdSpec.Models is not null)
            {
                models = cmdSpec.Models;
                catalog = BuildSimpleModelCatalog(models);
            }
            else if (provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            {
                (models, catalog) = await ProbeAcpModelsAsync(provider, ct).ConfigureAwait(false);
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
                ModelCatalog: catalog);
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

    private TimeSpan GetModelProbeTimeout() =>
        _options.ProbeTimeout < TimeSpan.FromSeconds(2) ? _options.ProbeTimeout : _options.HandshakeTimeout;

    private async Task<(IReadOnlyList<string> Models, IReadOnlyList<ProviderModelInfo> Catalog)> ProbeAcpModelsAsync(
        string provider,
        CancellationToken ct)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(GetModelProbeTimeout());
        try
        {
            await using var session = await OpenSessionAsync(new HarnessLaunchSpec(
                provider, null, Path.GetTempPath(), null, null,
                new Dictionary<string, McpServerConfig>()), probeCts.Token).ConfigureAwait(false);
            var acp = (AcpAgentSession)session;
            var catalog = acp.AvailableModelCatalog;
            var models = catalog.Count > 0 ? catalog.Select(m => m.Model).ToArray() : acp.AvailableModels;
            if (models.Count > 0)
            {
                return (models, catalog);
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
                return (cliModels, BuildSimpleModelCatalog(cliModels));
            }
        }

        return ([], []);
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

    private static Dictionary<string, string> BuildProcessEnvironment(IReadOnlyDictionary<string, string>? overrides)
    {
        var env = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var current = Environment.GetEnvironmentVariables();
        foreach (System.Collections.DictionaryEntry entry in current)
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                env[key] = value;
            }
        }

        return env;
    }

    private static string? FindExecutableOnPath(string executable, IReadOnlyDictionary<string, string>? env)
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
        if (env is not null)
        {
            env.TryGetValue("PATH", out pathVar);
        }

        pathVar ??= Environment.GetEnvironmentVariable("PATH");
        var segments = new List<string>(
            (pathVar ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDir))
        {
            segments.Add(Path.Combine(homeDir, ".local", "bin"));
            segments.Add(Path.Combine(homeDir, ".opencode", "bin"));
        }

        if (segments.Count == 0)
        {
            return null;
        }
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
