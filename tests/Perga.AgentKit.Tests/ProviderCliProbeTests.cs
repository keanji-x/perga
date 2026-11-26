using System.Runtime.Versioning;

namespace Perga.AgentKit.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class ProviderCliProbeTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("perga-provider-probe-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void DiscoversExecutablesAcrossPathAndLocalUserBinDirectories()
    {
        var pathBin = Path.Combine(_root.FullName, "path-bin");
        var home = Path.Combine(_root.FullName, "home");
        var localBin = Path.Combine(home, ".local", "bin");
        var opencodeBin = Path.Combine(home, ".opencode", "bin");

        var pathCodex = WriteExecutable(pathBin, "codex");
        var localClaude = WriteExecutable(localBin, "claude");
        var opencodeExe = WriteExecutable(opencodeBin, "opencode");
        // PATH takes precedence when the same executable also exists in ~/.local/bin.
        WriteExecutable(localBin, "codex");

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = pathBin,
            ["HOME"] = home
        };

        var discovered = ProviderCliProbe.Discover(env);

        Assert.Equal(
        [
            new DetectedProviderCli("claude", localClaude),
            new DetectedProviderCli("codex", pathCodex),
            new DetectedProviderCli("opencode", opencodeExe)
        ], discovered);
    }

    [Fact]
    public void IgnoresNonExecutableFilesAndDirectories()
    {
        var pathBin = Path.Combine(_root.FullName, "path-bin");
        var home = Path.Combine(_root.FullName, "home");
        var localBin = Path.Combine(home, ".local", "bin");

        Directory.CreateDirectory(pathBin);
        Directory.CreateDirectory(Path.Combine(pathBin, "claude"));
        var nonExec = Path.Combine(pathBin, "kimi");
        File.WriteAllText(nonExec, "#!/bin/sh\n");
        File.SetUnixFileMode(nonExec, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var validAgy = WriteExecutable(localBin, "agy");

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = pathBin
        };

        var discovered = ProviderCliProbe.Discover(env, home);

        var detected = Assert.Single(discovered);
        Assert.Equal(new DetectedProviderCli("agy", validAgy), detected);
    }

    [Fact]
    public void DoesNotLeakAmbientEnvironmentWhenControlledEnvironmentIsEmpty()
    {
        var emptyEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        Assert.Empty(ProviderCliProbe.Discover(emptyEnv));
    }

    [Fact]
    public void IgnoresMixedCasePathAndHomeVariablesOnUnix()
    {
        var pathBin = Path.Combine(_root.FullName, "mixed-path");
        var home = Path.Combine(_root.FullName, "mixed-home");
        WriteExecutable(pathBin, "kimi");
        WriteExecutable(Path.Combine(home, ".local", "bin"), "claude");

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Path"] = pathBin,
            ["Home"] = home
        };

        Assert.Empty(ProviderCliProbe.Discover(env));
    }

    [Fact]
    public void DiscoversAntigravityCliInGeminiCliBinDirectory()
    {
        var home = Path.Combine(_root.FullName, "agy-home");
        var agyExe = WriteExecutable(Path.Combine(home, ".gemini", "antigravity-cli", "bin"), "agy");

        var discovered = ProviderCliProbe.Discover(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = home });

        Assert.Equal([new DetectedProviderCli("agy", agyExe)], discovered);
    }

    [Fact]
    public void KnownCommandsAndExtraLocalPathDirectoriesMatchRuntimeConventions()
    {
        foreach (var command in new[]
                 {
                     "claude", "codex", "agy", "antigravity", "opencode", "kimi",
                     "qwen", "devin", "factory", "grok", "codebuddy", "qoder", "dsh"
                 })
        {
            Assert.Contains(command, ProviderCliProbe.KnownCommands);
        }

        Assert.Empty(AgentRuntime.GetExtraLocalPathDirectories(null));
        Assert.Empty(AgentRuntime.GetExtraLocalPathDirectories("   "));
        Assert.Equal(
            [
                Path.Combine("/tmp/home", ".local", "bin"),
                Path.Combine("/tmp/home", ".gemini", "antigravity-cli", "bin"),
                Path.Combine("/tmp/home", ".opencode", "bin")
            ],
            AgentRuntime.GetExtraLocalPathDirectories("/tmp/home"));
    }

    private static string WriteExecutable(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return path;
    }
}
