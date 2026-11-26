using System.Collections;
using System.Diagnostics;

namespace Perga.Processes;

/// <summary>
/// Isolates child subprocesses from Perga Host configuration and launcher handles
/// while keeping the caller's normal OS, network, VCS, and toolchain environment intact.
/// </summary>
public static class ChildProcessEnvironment
{
    private static readonly StringComparer NameComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly string[] ReservedPrefixes =
    [
        "PERGA_",
        "PERGA:",
        "ASPNETCORE_",
        "DOTNET_PERGA_",
        "DOTNET_PERGA:"
    ];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DOTNET_ENVIRONMENT",
        "DOTNET_APPLICATIONNAME",
        "DOTNET_CONTENTROOT"
    };

    public static bool IsHostReserved(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (ReservedNames.Contains(name))
        {
            return true;
        }

        foreach (var prefix in ReservedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static Dictionary<string, string> Capture()
    {
        var result = new Dictionary<string, string>(NameComparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value && !IsHostReserved(name))
            {
                result[name] = value;
            }
        }

        return result;
    }

    public static Dictionary<string, string> Capture(IReadOnlyDictionary<string, string> source)
    {
        var result = new Dictionary<string, string>(NameComparer);
        foreach (var (name, value) in source)
        {
            if (!IsHostReserved(name))
                result[name] = value;
        }
        return result;
    }

    public static void Apply(ProcessStartInfo process, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(environment);

        process.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            process.Environment[name] = value;
        }
    }
}
