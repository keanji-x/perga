using System.Runtime.Versioning;

namespace Perga.AgentKit.Tests;

[UnsupportedOSPlatform("windows")]
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

        var none = OarNodeRuntimeProbe.Probe(null, Environment(Path.Combine(_root.FullName, "no-bin"), Path.Combine(_root.FullName, "empty-home")));
        Assert.False(none.Found);
        Assert.Contains("PERGA_OAR_NODE_BIN", none.Error);
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
