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
        if (!string.IsNullOrWhiteSpace(configuredExecutable))
        {
            var resolved = AgentRuntime.FindExecutableOnPath(configuredExecutable.Trim(), environment)
                           ?? Path.GetFullPath(configuredExecutable.Trim());
            if (!File.Exists(resolved))
                throw new FileNotFoundException($"Configured Node 24 executable was not found: '{configuredExecutable}'.", resolved);
            if (ProbeNodeVersion(resolved, environment) is { Major: < MinimumNodeMajorVersion } v)
                throw new InvalidOperationException($"perga-oar-bridge requires Node.js >= {MinimumNodeMajorVersion}.0.0, but '{resolved}' is v{v}.");
            return resolved;
        }

        if (AgentRuntime.FindExecutableOnPath("node", environment) is { } pathNode &&
            ProbeNodeVersion(pathNode, environment) is { Major: >= MinimumNodeMajorVersion })
            return pathNode;

        if (FindNvmNode24(environment) is { } nvmNode)
            return nvmNode;

        throw new InvalidOperationException(
            $"perga-oar-bridge requires Node.js >= {MinimumNodeMajorVersion}.0.0. Configure Perga:OarNodeExecutablePath or PERGA_OAR_NODE_BIN.");
    }

    public static Version? ParseNodeVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var s = output.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var parsed) ? parsed : null;
    }

    private static string? FindNvmNode24(IReadOnlyDictionary<string, string>? environment)
    {
        var home = environment?.GetValueOrDefault("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) return null;
        var nvmRoot = Path.Combine(home, ".nvm", "versions", "node");
        if (!Directory.Exists(nvmRoot)) return null;

        var binName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        return Directory.EnumerateDirectories(nvmRoot)
            .Select(dir => (Version: ParseNodeVersion(Path.GetFileName(dir)), Exe: Path.Combine(dir, "bin", binName)))
            .Where(x => x.Version is { Major: >= MinimumNodeMajorVersion } && File.Exists(x.Exe))
            .OrderByDescending(x => x.Version)
            .Select(x => x.Exe)
            .FirstOrDefault();
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
