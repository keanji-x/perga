using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

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
    private static readonly string[] DefaultAntigravityModels = ["gemini-3.1-pro", "gemini-3-flash"];
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly AgentRuntimeOptions _options;
    private readonly OsSandboxWrapper _sandboxWrapper;
    private readonly ConcurrentDictionary<IAgentSession, byte> _activeSessions = new();
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

        return await Task.WhenAll(probeTasks).ConfigureAwait(false);
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
            var rawInvocation = new CommandInvocation(cmdOverride.FileName, args, fullCwd, env);
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
            session = await ClaudeAgentSession.OpenAsync(
                cmdOverride,
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
            var rawInvocation = new CommandInvocation(cmdOverride.FileName, args, fullCwd, env);
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
            var models = cmdSpec.Models ?? GetDefaultModelsForProvider(provider);
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
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return null;
        }

        var segments = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
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
