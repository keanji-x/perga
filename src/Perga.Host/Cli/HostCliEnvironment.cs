using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using Perga.AgentKit;
using Perga.Host.Configuration;

namespace Perga.Host.Cli;

/// <summary>The process state that <see cref="HostCliCommand"/> reads, so tests can supply a controlled one.</summary>
public sealed record HostCliEnvironment
{
    public required IReadOnlyDictionary<string, string> Variables { get; init; }

    /// <summary>Directory containing the running Perga.Host, used when PERGA_INSTALL_DIR is unset.</summary>
    public required string BaseDirectory { get; init; }

    public required string HomeDirectory { get; init; }
    public required TextReader Input { get; init; }
    public required TextWriter Output { get; init; }
    public required TextWriter Error { get; init; }

    /// <summary>Whether a person can answer a confirmation prompt on <see cref="Input"/>.</summary>
    public bool IsInteractive { get; init; }

    /// <summary>Whether filesystem path comparisons should be case-insensitive (macOS and Windows).</summary>
    public bool CaseInsensitivePaths { get; init; } = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    /// <summary>Runs a child process with inherited standard streams and returns its exit code.</summary>
    public Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, CancellationToken, Task<int>> RunProcessAsync { get; init; } =
        RunInheritedAsync;

    public Func<string?, IReadOnlyDictionary<string, string>, OarNodeRuntimeStatus> ProbeNode { get; init; } = OarNodeRuntimeProbe.Probe;

    public Func<IReadOnlyDictionary<string, string>, string?, IReadOnlyList<DetectedProviderCli>> DiscoverProviders { get; init; } =
        ProviderCliProbe.Discover;

    public Func<string, string, IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyList<ProviderStatus>>> ProbeProvidersDeepAsync { get; init; } =
        DefaultProbeProvidersDeepAsync;

    public string? Get(string name) => Variables.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    public static HostCliEnvironment FromProcess() => new()
    {
        Variables = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? string.Empty, StringComparer.Ordinal),
        BaseDirectory = AppContext.BaseDirectory,
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Input = Console.In,
        Output = Console.Out,
        Error = Console.Error,
        IsInteractive = !Console.IsInputRedirected,
        ProbeNode = (configured, environment) => OarNodeRuntimeProbe.Probe(configured, environment, includeSystemLocations: true)
    };

    private static async Task<int> RunInheritedAsync(
        string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (key, value) in environment)
            start.Environment[key] = value;
        try
        {
            using var process = Process.Start(start) ?? throw new IOException($"Could not start '{fileName}'.");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Win32Exception ex)
        {
            throw new IOException($"Could not start '{fileName}': {ex.Message}", ex);
        }
    }

    private static async Task<IReadOnlyList<ProviderStatus>> DefaultProbeProvidersDeepAsync(
        string installDir, string nodeExecutablePath, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        await using var runtime = new AgentRuntime(BuildDeepProbeRuntimeOptions(installDir, nodeExecutablePath, environment));
        return await runtime.ProbeProvidersAsync(forceRefresh: true, ct).ConfigureAwait(false);
    }

    private static AgentRuntimeOptions BuildDeepProbeRuntimeOptions(
        string installDir, string nodeExecutablePath, IReadOnlyDictionary<string, string> environment)
    {
        var environmentName = new[]
        {
            environment.GetValueOrDefault("DOTNET_ENVIRONMENT"),
            environment.GetValueOrDefault("ASPNETCORE_ENVIRONMENT")
        }.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? "Production";
        using var config = new ConfigurationManager();
        config.AddJsonFile(Path.Combine(installDir, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(installDir, $"appsettings.{environmentName}.json"), optional: true)
            .AddInMemoryCollection(environment.Select(entry =>
                KeyValuePair.Create(entry.Key.Replace("__", ":", StringComparison.Ordinal), (string?)entry.Value)));
        var providerExecutables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        config.GetSection($"{PergaHostOptions.SectionName}:{nameof(PergaHostOptions.ProviderExecutables)}").Bind(providerExecutables);
        return new AgentRuntimeOptions
        {
            BridgeScriptPath = Path.Combine(installDir, "oar-bridge", "perga-oar-bridge.mjs"),
            NodeExecutablePath = nodeExecutablePath,
            ProviderOverrides = PergaHostOptions.ToProviderOverrides(providerExecutables),
            BaseEnvironment = environment,
            ProbeTimeout = TimeSpan.FromSeconds(15)
        };
    }
}
