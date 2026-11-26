using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using Perga.AgentKit;

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
        IsInteractive = !Console.IsInputRedirected
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
}
