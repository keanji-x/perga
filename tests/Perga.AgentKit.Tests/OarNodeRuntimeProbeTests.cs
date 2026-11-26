using System.Runtime.Versioning;
using Perga.AgentKit.Internal;

namespace Perga.AgentKit.Tests;

[UnsupportedOSPlatform("windows")]
[Collection("OAR process environment")]
public sealed class OarNodeRuntimeProbeTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("perga-node-probe-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void ConfiguredExecutableIsClassifiedByItsVersion()
    {
        var missing = OarNodeRuntimeProbe.Probe(Path.Combine(_root.FullName, "missing-node"), Environment());
        Assert.False(missing.Found);
        Assert.Contains("was not found", missing.Error);

        var old = OarNodeRuntimeProbe.Probe(FakeNode("old", "v20.11.1"), Environment());
        Assert.True(old.Found);
        Assert.False(old.MeetsMinimum);
        Assert.Equal(new Version(20, 11, 1), old.Version);
        Assert.Contains("is v20.11.1", old.Error);

        var current = OarNodeRuntimeProbe.Probe(FakeNode("current", "v24.4.1"), Environment());
        Assert.True(current.MeetsMinimum);
        Assert.Equal(new Version(24, 4, 1), current.Version);
        Assert.Null(current.Error);
    }

    [Fact]
    public void OldPathNodeFallsBackToTheNewestNvmNode24()
    {
        var pathDir = Path.GetDirectoryName(FakeNode("path", "v18.0.0"))!;
        var home = Path.Combine(_root.FullName, "home");
        foreach (var version in new[] { "v24.1.0", "v25.0.0", "v22.0.0" })
            FakeNode(Path.Combine(home, ".nvm", "versions", "node", version, "bin"), version);

        var nvm = OarNodeRuntimeProbe.Probe(null, Environment(pathDir, home));
        Assert.True(nvm.MeetsMinimum);
        Assert.Equal(new Version(25, 0, 0), nvm.Version);
        Assert.Equal(Path.Combine(home, ".nvm", "versions", "node", "v25.0.0", "bin", "node"), nvm.ExecutablePath);

        var pathOnly = OarNodeRuntimeProbe.Probe(null, Environment(pathDir, Path.Combine(_root.FullName, "empty-home")));
        Assert.True(pathOnly.Found);
        Assert.False(pathOnly.MeetsMinimum);
        Assert.Equal(new Version(18, 0, 0), pathOnly.Version);

        var none = OarNodeRuntimeProbe.Probe(null, Environment(Path.Combine(_root.FullName, "no-bin")));
        Assert.False(none.Found);
        Assert.Contains("PERGA_OAR_NODE_BIN", none.Error);
    }

    [Fact]
    public void EmptyEnvironmentDoesNotProbeAmbientNodeLocations()
    {
        var ambientHome = Path.Combine(_root.FullName, "ambient-home");
        FakeNode(Path.Combine(ambientHome, ".nvm", "versions", "node", "v24.1.0", "bin"), "v24.1.0");
        var ambientPath = Path.GetDirectoryName(FakeNode("ambient-path", "v24.2.0"))!;
        var originalHome = System.Environment.GetEnvironmentVariable("HOME");
        var originalPath = System.Environment.GetEnvironmentVariable("PATH");
        try
        {
            System.Environment.SetEnvironmentVariable("HOME", ambientHome);
            System.Environment.SetEnvironmentVariable("PATH", ambientPath);
            Assert.Equal(ambientHome, System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
            Assert.False(OarNodeRuntimeProbe.Probe(null, new Dictionary<string, string>()).Found);
            Assert.False(OarNodeRuntimeProbe.Probe(null, new Dictionary<string, string> { ["PATH"] = "" }).Found);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("HOME", originalHome);
            System.Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void NvmFallbackProbesCandidateBinariesInsteadOfTrustingDirectoryNames()
    {
        var pathDir = Path.GetDirectoryName(FakeNode("path-old", "v18.0.0"))!;
        var home = Path.Combine(_root.FullName, "home-probe");

        // Non-executable newest candidate must be skipped.
        var nonExec = FakeNode(Path.Combine(home, ".nvm", "versions", "node", "v26.0.0", "bin"), "v26.0.0");
        File.SetUnixFileMode(nonExec, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Candidate whose binary actually reports an older version must be skipped.
        FakeNode(Path.Combine(home, ".nvm", "versions", "node", "v25.0.0", "bin"), "v20.0.0");

        // Candidate whose binary reports a valid Node 24+ version is selected with its probed version.
        var valid = FakeNode(Path.Combine(home, ".nvm", "versions", "node", "v24.1.0", "bin"), "v24.9.2");

        var result = OarNodeRuntimeProbe.Probe(null, Environment(pathDir, home));
        Assert.True(result.MeetsMinimum);
        Assert.Equal(new Version(24, 9, 2), result.Version);
        Assert.Equal(valid, result.ExecutablePath);
    }

    [Fact]
    public void WellKnownVoltaFnmAndHomebrewCandidatesAreProbedWhenPathAndNvmLackNode24()
    {
        var oldPathDir = Path.GetDirectoryName(FakeNode("wellknown-old-path", "v20.0.0"))!;

        // Volta fallback when PATH has Node 20.
        var voltaHome = Path.Combine(_root.FullName, "home-volta");
        var voltaNode = FakeNode(Path.Combine(voltaHome, ".volta", "bin"), "v24.3.0");
        var volta = OarNodeRuntimeProbe.Probe(null, Environment(oldPathDir, voltaHome));
        Assert.True(volta.MeetsMinimum);
        Assert.Equal(new Version(24, 3, 0), volta.Version);
        Assert.Equal(voltaNode, volta.ExecutablePath);

        // Old Volta is skipped in favor of fnm default alias.
        var fnmHome = Path.Combine(_root.FullName, "home-fnm");
        FakeNode(Path.Combine(fnmHome, ".volta", "bin"), "v22.0.0");
        var fnmNode = FakeNode(Path.Combine(fnmHome, ".local", "share", "fnm", "aliases", "default", "bin"), "v24.5.1");
        var fnm = OarNodeRuntimeProbe.Probe(null, Environment(oldPathDir, fnmHome));
        Assert.True(fnm.MeetsMinimum);
        Assert.Equal(new Version(24, 5, 1), fnm.Version);
        Assert.Equal(fnmNode, fnm.ExecutablePath);

        // Legacy ~/.fnm fallback when ~/.local/share/fnm is absent.
        var legacyFnmHome = Path.Combine(_root.FullName, "home-legacy-fnm");
        var legacyFnmNode = FakeNode(Path.Combine(legacyFnmHome, ".fnm", "aliases", "default", "bin"), "v24.6.0");
        var legacyFnm = OarNodeRuntimeProbe.Probe(null, Environment(oldPathDir, legacyFnmHome));
        Assert.True(legacyFnm.MeetsMinimum);
        Assert.Equal(new Version(24, 6, 0), legacyFnm.Version);
        Assert.Equal(legacyFnmNode, legacyFnm.ExecutablePath);

        // Simulate the Homebrew search without depending on a system install.
        var brewOld = FakeNode(Path.Combine("brew-old", "bin"), "v22.14.0");
        var brew24 = FakeNode(Path.Combine("brew-24", "bin"), "v24.2.0");
        var env = Environment(oldPathDir);
        var brewCandidates = new[] { brewOld, brew24 };
        Assert.False(OarNodeResolver.Probe(null, env, includeSystemLocations: false, brewCandidates).MeetsMinimum);
        var brew = OarNodeResolver.Probe(null, env, includeSystemLocations: true, brewCandidates);
        Assert.True(brew.MeetsMinimum);
        Assert.Equal(new Version(24, 2, 0), brew.Version);
        Assert.Equal(brew24, brew.ExecutablePath);
    }

    [Fact]
    public void NodeErrorsUsePlainEnglishGuidanceWithoutInternalBridgeJargon()
    {
        var oldNode = FakeNode("plain-old", "v20.11.1");
        var oldStatus = OarNodeRuntimeProbe.Probe(oldNode, Environment());
        Assert.False(oldStatus.MeetsMinimum);
        Assert.DoesNotContain("perga-oar-bridge", oldStatus.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Perga requires Node.js >= 24.0.0 to run agent CLIs", oldStatus.Error);
        Assert.Contains("nvm install 24", oldStatus.Error);
        Assert.Contains("brew install node@24", oldStatus.Error);
        Assert.Contains("PERGA_OAR_NODE_BIN", oldStatus.Error);

        var oldEx = Assert.Throws<InvalidOperationException>(() =>
            OarNodeResolver.ResolveNode24Executable(oldNode, Environment()));
        Assert.DoesNotContain("perga-oar-bridge", oldEx.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Perga requires Node.js >= 24.0.0 to run agent CLIs", oldEx.Message);
        Assert.Contains("brew install node@24", oldEx.Message);

        var noneStatus = OarNodeRuntimeProbe.Probe(null, Environment(Path.Combine(_root.FullName, "no-bin")));
        Assert.False(noneStatus.Found);
        Assert.DoesNotContain("perga-oar-bridge", noneStatus.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Perga requires Node.js >= 24.0.0 to run agent CLIs", noneStatus.Error);
        Assert.Contains("nvm install 24", noneStatus.Error);
        Assert.Contains("brew install node@24", noneStatus.Error);
        Assert.Contains("PERGA_OAR_NODE_BIN", noneStatus.Error);
    }

    [Fact]
    public async Task AgentRuntimeUsesControlledNodeLocationsForLocalSessionsAndProbes()
    {
        var ambientNode = FakeNode("ambient-runtime", "v24.4.1");
        var controlledNode = FakeNode("controlled-runtime", "v20.11.1");
        var bridge = Path.Combine(_root.FullName, "bridge.mjs");
        File.WriteAllText(bridge, "");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            BaseEnvironment = Environment(Path.GetDirectoryName(controlledNode)),
            BridgeScriptPath = bridge,
            SubprocessTransport = LocalSubprocessTransport.Instance
        });
        var spec = new HarnessLaunchSpec("codex", null, _root.FullName, null, null, new Dictionary<string, McpServerConfig>());
        var nodeError = $"'{controlledNode}' is v20.11.1";

        var originalPath = System.Environment.GetEnvironmentVariable("PATH");
        try
        {
            System.Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(ambientNode));
            Assert.Equal(ambientNode, OarNodeRuntimeProbe.Probe(null, null).ExecutablePath);
            var failure = Assert.Throws<InvalidOperationException>(() => runtime.CreateSession(spec));
            Assert.Contains(nodeError, failure.Message);

            var statuses = await runtime.ProbeProvidersAsync(CancellationToken.None);
            Assert.NotEmpty(statuses);
            Assert.All(statuses, status =>
            {
                Assert.False(status.Available);
                Assert.Contains(nodeError, status.Reason);
            });
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    private string FakeNode(string directory, string version)
    {
        var dir = Path.Combine(_root.FullName, directory);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "node");
        File.WriteAllText(path, $"#!/bin/sh\necho {version}\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return path;
    }

    private Dictionary<string, string> Environment(string? path = null, string? home = null) => new()
    {
        ["PATH"] = path ?? "/usr/bin:/bin",
        ["HOME"] = home ?? Path.Combine(_root.FullName, "empty-home")
    };
}
