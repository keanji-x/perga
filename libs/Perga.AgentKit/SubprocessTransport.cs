using Perga.AgentKit.Internal;

namespace Perga.AgentKit;

public interface ISubprocessHandle : IAsyncDisposable
{
    int? ProcessId { get; }
    bool HasExited { get; }
    bool ShutdownConfirmed { get; }
    int? TryGetExitCode();
    // Check beforeWrite after acquiring the transport writer and before emitting any bytes.
    Task WriteLineAsync(string line, CancellationToken ct, Func<CancellationToken, Task>? beforeWrite = null);
    ValueTask<string?> ReadStdoutLineAsync(CancellationToken ct);
    string GetStderrSummary();
    bool SendInterruptSignal();
}

public interface ISubprocessTransport
{
    bool IsLocal { get; }
    string TempDirectory => IsLocal ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) : "/tmp";

    ISubprocessHandle StartInteractive(CommandInvocation invocation);
    string ResolveNodeExecutable(string? configuredExecutable, IReadOnlyDictionary<string, string>? environment);
    string ResolveNodeExecutable(string? configuredExecutable, IReadOnlyDictionary<string, string>? environment, bool includeSystemLocations) =>
        ResolveNodeExecutable(configuredExecutable, environment);
    string ResolveBridgeScriptPath(string? configuredBridgeScriptPath);
    bool DirectoryExists(string path);
}

public sealed class LocalSubprocessTransport : ISubprocessTransport
{
    public static LocalSubprocessTransport Instance { get; } = new();

    public bool IsLocal => true;

    public ISubprocessHandle StartInteractive(CommandInvocation invocation) =>
        StdioSubprocess.Start(invocation);

    public string ResolveNodeExecutable(string? configuredExecutable, IReadOnlyDictionary<string, string>? environment) =>
        ResolveNodeExecutable(configuredExecutable, environment, includeSystemLocations: true);

    public string ResolveNodeExecutable(
        string? configuredExecutable, IReadOnlyDictionary<string, string>? environment, bool includeSystemLocations) =>
        OarNodeResolver.ResolveNode24Executable(configuredExecutable, environment, includeSystemLocations);

    public string ResolveBridgeScriptPath(string? configuredBridgeScriptPath)
    {
        if (string.IsNullOrWhiteSpace(configuredBridgeScriptPath))
            throw new InvalidOperationException("OAR bridge script path is not configured. Build tools/perga-oar-bridge or set PERGA_OAR_BRIDGE_SCRIPT.");
        var full = Path.GetFullPath(configuredBridgeScriptPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"OAR bridge script was not found at '{full}'.", full);
        return full;
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
