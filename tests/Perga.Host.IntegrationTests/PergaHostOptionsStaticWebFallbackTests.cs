using Microsoft.Extensions.Configuration;
using Perga.Host.Configuration;

namespace Perga.Host.IntegrationTests;

public sealed class PergaHostOptionsStaticWebFallbackTests
{
    [Fact]
    public void BindFrom_FallsBackToBundledWebDirectoryNextToBaseDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"perga-web-fallback-{Guid.NewGuid():N}");
        var packageDir = Path.Combine(tempRoot, "pkg");
        var bundledWeb = Path.Combine(packageDir, "web");
        var unrelatedCwd = Path.Combine(tempRoot, "cwd");
        Directory.CreateDirectory(bundledWeb);
        Directory.CreateDirectory(unrelatedCwd);
        File.WriteAllText(Path.Combine(bundledWeb, "index.html"), "<!doctype html><title>Perga</title>");

        var prevEnv = Environment.GetEnvironmentVariable("PERGA_STATIC_WEB_DIR");
        Environment.SetEnvironmentVariable("PERGA_STATIC_WEB_DIR", null);
        try
        {
            var config = new ConfigurationBuilder().Build();
            var opts = PergaHostOptions.BindFrom(config, unrelatedCwd, packageDir);
            Assert.Equal(Path.GetFullPath(bundledWeb), opts.StaticWebRootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERGA_STATIC_WEB_DIR", prevEnv);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BindFrom_DoesNotServeUnbuiltSourceWebFromContentRootWhenFeDistExists()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"perga-fe-source-{Guid.NewGuid():N}");
        var feDir = Path.Combine(tempRoot, "fe");
        var sourceWebDir = Path.Combine(feDir, "web");
        var distWebDir = Path.Combine(sourceWebDir, "dist");
        var devBinDir = Path.Combine(tempRoot, "src", "Perga.Host", "bin", "Debug", "net11.0");
        Directory.CreateDirectory(distWebDir);
        Directory.CreateDirectory(devBinDir);
        File.WriteAllText(Path.Combine(sourceWebDir, "index.html"), "<script type=\"module\" src=\"/src/main.tsx\"></script>");
        File.WriteAllText(Path.Combine(distWebDir, "index.html"), "<script type=\"module\" src=\"/assets/index.js\"></script>");

        var prevEnv = Environment.GetEnvironmentVariable("PERGA_STATIC_WEB_DIR");
        Environment.SetEnvironmentVariable("PERGA_STATIC_WEB_DIR", null);
        try
        {
            var config = new ConfigurationBuilder().Build();
            var opts = PergaHostOptions.BindFrom(config, feDir, devBinDir);
            Assert.Equal(Path.GetFullPath(distWebDir), opts.StaticWebRootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERGA_STATIC_WEB_DIR", prevEnv);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void BindFrom_IgnoresBundledWebDirectoryWithoutIndexHtml_AndHonorsExplicitOverride()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"perga-web-override-{Guid.NewGuid():N}");
        var packageDir = Path.Combine(tempRoot, "pkg");
        var emptyWeb = Path.Combine(packageDir, "web");
        var customWeb = Path.Combine(tempRoot, "custom-web");
        Directory.CreateDirectory(emptyWeb);
        Directory.CreateDirectory(customWeb);

        var prevEnv = Environment.GetEnvironmentVariable("PERGA_STATIC_WEB_DIR");
        Environment.SetEnvironmentVariable("PERGA_STATIC_WEB_DIR", null);
        try
        {
            var emptyConfig = new ConfigurationBuilder().Build();
            var withoutIndex = PergaHostOptions.BindFrom(emptyConfig, packageDir, packageDir);
            Assert.NotEqual(Path.GetFullPath(emptyWeb), withoutIndex.StaticWebRootDirectory);

            File.WriteAllText(Path.Combine(emptyWeb, "index.html"), "<!doctype html>");
            var overrideConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Perga:StaticWebRootDirectory"] = customWeb,
                })
                .Build();
            var overridden = PergaHostOptions.BindFrom(overrideConfig, packageDir, packageDir);
            Assert.Equal(customWeb, overridden.StaticWebRootDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERGA_STATIC_WEB_DIR", prevEnv);
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
