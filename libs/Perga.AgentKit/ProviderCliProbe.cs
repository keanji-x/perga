namespace Perga.AgentKit;

/// <summary>A provider CLI executable discovered on the local host.</summary>
public sealed record DetectedProviderCli(string Name, string ExecutablePath);

/// <summary>Discovers supported provider CLIs using the same PATH and local user bin directories as <see cref="AgentRuntime"/>.</summary>
public static class ProviderCliProbe
{
    public static IReadOnlyList<string> KnownCommands { get; } =
    [
        "claude",
        "codex",
        "agy",
        "antigravity",
        "opencode",
        "kimi",
        "qwen",
        "devin",
        "factory",
        "grok",
        "codebuddy",
        "qoder",
        "dsh"
    ];

    public static IReadOnlyList<DetectedProviderCli> Discover(
        IReadOnlyDictionary<string, string>? environment,
        string? homeDirectory = null)
    {
        var pathValue = environment is null
            ? Environment.GetEnvironmentVariable("PATH")
            : GetEnvironmentValue(environment, "PATH");
        var home = homeDirectory
            ?? (environment is not null
                ? GetEnvironmentValue(environment, "HOME")
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seenDirectories = new HashSet<string>(comparer);
        var directories = new List<string>();
        foreach (var dir in (pathValue ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (seenDirectories.Add(dir))
                directories.Add(dir);
        }

        foreach (var extra in AgentRuntime.GetExtraLocalPathDirectories(home))
        {
            if (seenDirectories.Add(extra))
                directories.Add(extra);
        }

        var detected = new List<DetectedProviderCli>();
        foreach (var command in KnownCommands)
        {
            foreach (var directory in directories)
            {
                if (TryResolveExecutable(directory, command) is { } executablePath)
                {
                    detected.Add(new DetectedProviderCli(command, executablePath));
                    break;
                }
            }
        }

        return detected;
    }

    private static string? GetEnvironmentValue(IReadOnlyDictionary<string, string> environment, string name)
    {
        if (environment.TryGetValue(name, out var value))
            return value;
        if (!OperatingSystem.IsWindows())
            return null;
        foreach (var (key, candidate) in environment)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    private static string? TryResolveExecutable(string directory, string command)
    {
        var candidate = Path.Combine(directory, command);
        if (IsExecutable(candidate))
            return candidate;
        if (OperatingSystem.IsWindows() &&
            !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            IsExecutable(candidate + ".exe"))
        {
            return candidate + ".exe";
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (!File.Exists(path))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        try
        {
            return (File.GetUnixFileMode(path) &
                    (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
