using System.Diagnostics;
using System.Text;
using Perga.Processes;

namespace Perga.AgentKit.Internal;

internal static class OarNodeResolver
{
    internal const int MinimumNodeMajorVersion = 24;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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
        string? configuredExecutable, IReadOnlyDictionary<string, string>? environment)
    {
        var status = Probe(configuredExecutable, environment);
        if (status.MeetsMinimum) return status.ExecutablePath!;
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            if (!status.Found)
                throw new FileNotFoundException($"Configured Node 24 executable was not found: '{configuredExecutable}'.", status.ExecutablePath);
            // A configured executable whose version cannot be read is trusted, as the caller chose it explicitly.
            if (status.Version is null) return status.ExecutablePath!;
            throw new InvalidOperationException(
                $"perga-oar-bridge requires Node.js >= {MinimumNodeMajorVersion}.0.0, but '{status.ExecutablePath}' is v{status.Version}.");
        }

        throw new InvalidOperationException(
            $"perga-oar-bridge requires Node.js >= {MinimumNodeMajorVersion}.0.0. Configure Perga:OarNodeExecutablePath or PERGA_OAR_NODE_BIN.");
    }

    /// <summary>Checks the configured executable, else <c>node</c> on PATH, else the newest qualifying nvm install.</summary>
    public static OarNodeRuntimeStatus Probe(string? configuredExecutable, IReadOnlyDictionary<string, string>? environment)
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

        if (FindNvmNode24(environment) is { } nvm)
            return new(true, true, nvm.Version, nvm.Exe, null);

        return pathNode is not null
            ? Classify(pathNode, pathVersion)
            : new(false, false, null, null,
                $"Node.js >= {MinimumNodeMajorVersion} was not found on PATH or in ~/.nvm/versions/node. Install it or set PERGA_OAR_NODE_BIN.");
    }

    private static OarNodeRuntimeStatus Classify(string executable, Version? version) => version switch
    {
        null => new(true, false, null, executable, $"Could not read the Node.js version from '{executable}'."),
        { Major: >= MinimumNodeMajorVersion } => new(true, true, version, executable, null),
        _ => new(true, false, version, executable,
            $"Node.js >= {MinimumNodeMajorVersion}.0.0 is required, but '{executable}' is v{version}.")
    };

    public static Version? ParseNodeVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var s = output.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var parsed) ? parsed : null;
    }

    private static (Version Version, string Exe)? FindNvmNode24(IReadOnlyDictionary<string, string>? environment)
    {
        var home = environment?.GetValueOrDefault("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
