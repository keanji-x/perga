using System.Diagnostics;
using System.Text;
using Perga.Processes;

namespace Perga.AgentKit.Internal;

internal static class OarNodeResolver
{
    internal const int MinimumNodeMajorVersion = 24;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] MacOsHomebrewCandidates =
    [
        "/opt/homebrew/opt/node@24/bin/node",
        "/opt/homebrew/bin/node",
        "/usr/local/opt/node@24/bin/node"
    ];

    public static string? TryMapProviderToOarRuntime(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        var trimmed = provider.Trim();
        if (trimmed.StartsWith("oar:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["oar:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        return trimmed.ToLowerInvariant() switch
        {
            "codex" => "codex",
            "claude" => "claude",
            "acp:antigravity" or "antigravity" or "agy" => "antigravity",
            "acp:opencode" or "opencode" => "opencode",
            var s when s.StartsWith("acp:", StringComparison.Ordinal) => null,
            var other => other
        };
    }

    public static string ResolveNode24Executable(
        string? configuredExecutable, IReadOnlyDictionary<string, string>? environment, bool includeSystemLocations = true)
    {
        var status = Probe(configuredExecutable, environment, includeSystemLocations);
        if (status.MeetsMinimum) return status.ExecutablePath!;
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            if (!status.Found)
                throw new FileNotFoundException($"Configured Node.js {MinimumNodeMajorVersion}+ executable was not found: '{configuredExecutable}'.", status.ExecutablePath);
            // A configured executable whose version cannot be read is trusted, as the caller chose it explicitly.
            if (status.Version is null) return status.ExecutablePath!;
            throw new InvalidOperationException(
                $"Perga requires Node.js >= {MinimumNodeMajorVersion}.0.0 to run agent CLIs, but '{status.ExecutablePath}' is v{status.Version}. " +
                $"Install Node.js {MinimumNodeMajorVersion}+ (e.g. 'nvm install {MinimumNodeMajorVersion}' or 'brew install node@{MinimumNodeMajorVersion}') or set PERGA_OAR_NODE_BIN.");
        }

        if (status.Found && status.Version is not null)
        {
            throw new InvalidOperationException(
                $"Perga requires Node.js >= {MinimumNodeMajorVersion}.0.0 to run agent CLIs, but '{status.ExecutablePath}' is v{status.Version}. " +
                $"Install Node.js {MinimumNodeMajorVersion}+ (e.g. 'nvm install {MinimumNodeMajorVersion}' or 'brew install node@{MinimumNodeMajorVersion}') or set PERGA_OAR_NODE_BIN (or Perga:OarNodeExecutablePath).");
        }

        throw new InvalidOperationException(
            $"Perga requires Node.js >= {MinimumNodeMajorVersion}.0.0 to run agent CLIs. " +
            $"Install Node.js {MinimumNodeMajorVersion}+ (e.g. 'nvm install {MinimumNodeMajorVersion}' or 'brew install node@{MinimumNodeMajorVersion}') or set PERGA_OAR_NODE_BIN (or Perga:OarNodeExecutablePath).");
    }

    /// <summary>Checks the configured executable, else <c>node</c> on PATH, else nvm/Volta/fnm/Homebrew Node 24+ installs.</summary>
    internal static OarNodeRuntimeStatus Probe(
        string? configuredExecutable,
        IReadOnlyDictionary<string, string>? environment,
        bool includeSystemLocations,
        IEnumerable<string>? extraCandidatePaths = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            var resolved = AgentRuntime.FindExecutableOnPath(configuredExecutable.Trim(), environment)
                           ?? Path.GetFullPath(configuredExecutable.Trim());
            if (!File.Exists(resolved))
                return new(false, false, null, resolved, $"Configured Node.js executable was not found: '{configuredExecutable}'.");
            return Classify(resolved, ProbeNodeVersion(resolved, environment));
        }

        var pathNode = AgentRuntime.FindExecutableOnPath("node", environment);
        var pathVersion = pathNode is null ? null : ProbeNodeVersion(pathNode, environment);
        if (pathVersion is { Major: >= MinimumNodeMajorVersion })
            return new(true, true, pathVersion, pathNode, null);

        var home = environment is null
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : environment.GetValueOrDefault("HOME");
        if (FindNvmNode24(home, environment) is { } nvm)
            return new(true, true, nvm.Version, nvm.Exe, null);

        if (FindWellKnownNode24(home, environment, includeSystemLocations, extraCandidatePaths) is { } wellKnown)
            return new(true, true, wellKnown.Version, wellKnown.Exe, null);

        return pathNode is not null
            ? Classify(pathNode, pathVersion)
            : new(false, false, null, null,
                $"Perga requires Node.js >= {MinimumNodeMajorVersion}.0.0 to run agent CLIs. " +
                $"Install Node.js {MinimumNodeMajorVersion}+ (e.g. 'nvm install {MinimumNodeMajorVersion}' or 'brew install node@{MinimumNodeMajorVersion}') or set PERGA_OAR_NODE_BIN (or Perga:OarNodeExecutablePath).");
    }

    private static OarNodeRuntimeStatus Classify(string executable, Version? version) => version switch
    {
        null => new(true, false, null, executable,
            $"Could not read the Node.js version from '{executable}'. Install Node.js {MinimumNodeMajorVersion}+ (e.g. 'nvm install {MinimumNodeMajorVersion}' or 'brew install node@{MinimumNodeMajorVersion}') or set PERGA_OAR_NODE_BIN."),
        { Major: >= MinimumNodeMajorVersion } => new(true, true, version, executable, null),
        _ => new(true, false, version, executable,
            $"Perga requires Node.js >= {MinimumNodeMajorVersion}.0.0 to run agent CLIs, but '{executable}' is v{version}. " +
            $"Install Node.js {MinimumNodeMajorVersion}+ (e.g. 'nvm install {MinimumNodeMajorVersion}' or 'brew install node@{MinimumNodeMajorVersion}') or set PERGA_OAR_NODE_BIN.")
    };

    public static Version? ParseNodeVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var s = output.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var parsed) ? parsed : null;
    }

    private static (Version Version, string Exe)? FindNvmNode24(string? home, IReadOnlyDictionary<string, string>? environment)
    {
        if (string.IsNullOrWhiteSpace(home)) return null;
        var nvmRoot = Path.Combine(home, ".nvm", "versions", "node");
        if (!Directory.Exists(nvmRoot)) return null;

        var binName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var candidate in Directory.EnumerateDirectories(nvmRoot)
                     .Select(dir => (DirVersion: ParseNodeVersion(Path.GetFileName(dir)), Exe: Path.Combine(dir, "bin", binName)))
                     .Where(x => x.DirVersion is { Major: >= MinimumNodeMajorVersion } && File.Exists(x.Exe))
                     .OrderByDescending(x => x.DirVersion))
        {
            if (ProbeNodeVersion(candidate.Exe, environment) is { Major: >= MinimumNodeMajorVersion } probed)
                return (probed, candidate.Exe);
        }

        return null;
    }

    private static (Version Version, string Exe)? FindWellKnownNode24(
        string? home,
        IReadOnlyDictionary<string, string>? environment,
        bool includeSystemLocations,
        IEnumerable<string>? extraCandidatePaths)
    {
        var binName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, ".volta", "bin", binName));
            candidates.Add(Path.Combine(home, ".local", "share", "fnm", "aliases", "default", "bin", binName));
            candidates.Add(Path.Combine(home, ".fnm", "aliases", "default", "bin", binName));
        }

        if (includeSystemLocations)
            candidates.AddRange(extraCandidatePaths ?? (OperatingSystem.IsMacOS() ? MacOsHomebrewCandidates : []));

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate))
                continue;
            if (ProbeNodeVersion(candidate, environment) is { Major: >= MinimumNodeMajorVersion } probed)
                return (probed, candidate);
        }

        return null;
    }

    private static Version? ProbeNodeVersion(string executable, IReadOnlyDictionary<string, string>? environment)
    {
        try
        {
            var psi = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Utf8NoBom, StandardErrorEncoding = Utf8NoBom
            };
            psi.ArgumentList.Add("--version");
            ChildProcessEnvironment.Apply(psi, environment ?? ChildProcessEnvironment.Capture());
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.StandardInput.Close();
            if (!p.WaitForExit(3000)) { try { p.Kill(entireProcessTree: true); } catch { } return null; }
            return p.ExitCode == 0 ? ParseNodeVersion(p.StandardOutput.ReadToEnd()) : null;
        }
        catch { return null; }
    }
}
