using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Connections;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.Cli;
using Perga.Kernel.Ports;

namespace Perga.Host.IntegrationTests;

// The launcher and installer target Linux and macOS only.
[UnsupportedOSPlatform("windows")]
public sealed class HostCliTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("perga-host-cli-");
    private readonly Dictionary<string, string> _variables = new(StringComparer.Ordinal);
    private readonly List<(string File, string[] Args, Dictionary<string, string> Env)> _processes = [];
    private string _baseDirectory;
    private string _input = string.Empty;
    private bool _interactive;
    private OarNodeRuntimeStatus _node = new(true, true, new Version(24, 4, 1), "/opt/node/bin/node", null);
    private Func<string, IReadOnlyList<string>, int> _processResult = (_, _) => 0;

    public HostCliTests()
    {
        // The temp root can sit behind a symlink (macOS /var); compare against physical paths.
        Root = HostCliCommand.PhysicalPath(_root.FullName);
        Install = Path.Combine(Root, "app");
        _baseDirectory = Install;
        Bin = Path.Combine(Root, "bin");
        Data = Path.Combine(Root, "data");
        Home = Path.Combine(Root, "home");
        Directory.CreateDirectory(Home);
    }

    private string Root { get; }
    private string Install { get; }
    private string Bin { get; }
    private string Data { get; }
    private string Home { get; }
    private string Launcher => Path.Combine(Bin, "perga");

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData("help")]
    [InlineData("-h")]
    [InlineData("--help")]
    public async Task HelpListsEveryLauncherCommand(string command)
    {
        var result = await RunAsync(command);

        Assert.Equal(0, result.Code);
        foreach (var usage in new[] { "perga [host args...]", "perga start", "perga auth", "perga version", "perga doctor", "perga upgrade [version]", "perga uninstall [--yes]" })
            Assert.Contains(usage, result.Output);
    }

    [Fact]
    public void DispatchCoversOnlyInstallationCommands()
    {
        foreach (var command in new[] { "version", "-v", "--version", "doctor", "upgrade", "uninstall", "help", "-h", "--help" })
            Assert.True(HostCliCommand.Handles([command]), command);
        foreach (var args in new[] { Array.Empty<string>(), ["auth"], ["start"], ["--urls", "http://127.0.0.1:0"] })
            Assert.False(HostCliCommand.Handles(args));
    }

    [Fact]
    public async Task ProgramRoutesInstallationCommandsAndStripsALeadingStart()
    {
        CreatePackage(version: "v9.9.9\n");
        var version = await RunHostProcessAsync("version");
        Assert.True(version.Code == 0, version.Error);
        Assert.StartsWith("perga v9.9.9\n", Normalize(version.Output));

        var openApi = Path.Combine(Root, "openapi.json");
        var start = await RunHostProcessAsync("start", "--export-openapi", openApi);
        Assert.True(start.Code == 0, start.Error);
        Assert.True(new FileInfo(openApi).Length > 0);
    }

    [Fact]
    public async Task ProgramReportsOptionsBindingFailureAsConfigurationError()
    {
        var result = await RunHostProcessAsync("--PERGA_AUTH_SESSION_DAYS=invalid");

        Assert.Equal(1, result.Code);
        Assert.Contains("error: Invalid Perga configuration:", result.Error);
        Assert.Contains("invalid", result.Error);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("-v")]
    [InlineData("--version")]
    public async Task VersionPrintsTheFirstLineOfVersionAndThePaths(string command)
    {
        CreatePackage(version: "  v0.1.0  \nsecond line\n");
        UseLauncherEnvironment();

        var result = await RunAsync(command);

        Assert.Equal(0, result.Code);
        Assert.Equal($"perga v0.1.0\ninstall: {Install}\ndata:    {Data}\n", Normalize(result.Output));
    }

    [Fact]
    public async Task VersionReportsAnExplicitDatabaseAndFallsBackToUnknown()
    {
        Directory.CreateDirectory(Install);
        UseLauncherEnvironment();
        _variables["PERGA_DB_PATH"] = Path.Combine(Root, "db", "perga.db");

        var result = await RunAsync("version");

        Assert.Equal(0, result.Code);
        Assert.StartsWith("perga unknown\n", Normalize(result.Output));
        Assert.Contains($"db:      {Path.Combine(Root, "db", "perga.db")}", result.Output);
    }

    [Fact]
    public async Task VersionAndDoctorRejectExtraArgumentsAndPrintHelp()
    {
        foreach (var command in new[] { "version", "doctor" })
        {
            var help = await RunAsync(command, "--help");
            Assert.Equal(0, help.Code);
            Assert.Contains($"Usage: perga {command}", help.Output);

            var extra = await RunAsync(command, "extra");
            Assert.Equal(2, extra.Code);
            Assert.Contains("unexpected argument 'extra'", extra.Error);
        }
    }

    [Fact]
    public async Task DoctorPassesWithPackageFilesAndNode24AndListsProviders()
    {
        CreatePackage();
        UseLauncherEnvironment();
        WriteLauncher();
        _variables["PERGA_OAR_NODE_BIN"] = "/custom/node";
        var localBin = Path.Combine(Home, ".local", "bin");
        var pathBin = Path.Combine(Root, "path-bin");
        WriteExecutable(Path.Combine(localBin, "claude"));
        WriteExecutable(Path.Combine(pathBin, "codex"));
        File.WriteAllText(Path.Combine(pathBin, "kimi"), "not executable");
        _variables["PATH"] = pathBin;
        string? probedWith = null;
        _node = _node with { ExecutablePath = "/custom/node" };
        var probe = _node;

        var result = await RunAsync(env => env with
        {
            ProbeNode = (configured, _) =>
            {
                probedWith = configured;
                return probe;
            }
        }, "doctor");

        Assert.Equal(0, result.Code);
        Assert.Equal("/custom/node", probedWith);
        Assert.Contains("[ok] version", result.Output);
        Assert.Contains($"[ok] launcher   {Launcher}", result.Output);
        Assert.Contains("[ok] package    all required files present", result.Output);
        Assert.Contains("[ok] node       v24.4.1 (/custom/node)", result.Output);
        Assert.Contains($"codex ({Path.Combine(pathBin, "codex")})", result.Output);
        Assert.Contains($"claude ({Path.Combine(localBin, "claude")})", result.Output);
        Assert.DoesNotContain("kimi", result.Output.Split('\n').Single(l => l.Contains("providers", StringComparison.Ordinal)));
        Assert.Contains("[warn] database", result.Output);
        Assert.Contains("All required checks passed.", result.Output);
    }

    [Fact]
    public async Task DoctorFailsOnMissingPackageFilesOrOldNode()
    {
        CreatePackage();
        UseLauncherEnvironment();
        File.Delete(Path.Combine(Install, "web", "index.html"));
        File.SetUnixFileMode(Path.Combine(Install, "perga-term-bridge"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var missing = await RunAsync("doctor");
        Assert.Equal(1, missing.Code);
        Assert.Contains("[fail] package    missing or not executable: perga-term-bridge, web/index.html", missing.Output);
        Assert.Contains("[warn] providers", missing.Output);

        CreatePackage();
        _node = new OarNodeRuntimeStatus(true, false, new Version(20, 1, 0), "/usr/bin/node", "Node.js >= 24.0.0 is required, but '/usr/bin/node' is v20.1.0.");
        var oldNode = await RunAsync("doctor");
        Assert.Equal(1, oldNode.Code);
        Assert.Contains("[fail] node       Node.js >= 24.0.0 is required", oldNode.Output);
        Assert.Contains("Required checks failed.", oldNode.Output);

        File.WriteAllBytes(Path.Combine(Data, "perga.db"), new byte[7]);
        _node = _node with { MeetsMinimum = true, Version = new Version(24, 0, 0), Error = null };
        var healthy = await RunAsync("doctor");
        Assert.Equal(0, healthy.Code);
        Assert.Contains($"[ok] database   {Path.Combine(Data, "perga.db")} (7 bytes)", healthy.Output);
    }

    [Fact]
    public async Task UpgradeDownloadsTheInstallerAndRunsItWithTheLauncherPaths()
    {
        UseLauncherEnvironment();
        _variables["PERGA_DB_PATH"] = Path.Combine(Root, "db", "perga.db");
        _variables["PATH"] = "/usr/bin:/bin";
        string? script = null;
        _processResult = (file, args) =>
        {
            if (file == "curl")
            {
                script = args[^2];
                File.WriteAllText(script, "#!/usr/bin/env bash\n");
                return 0;
            }

            Assert.True(File.Exists(args[0]));
            return 7;
        };

        var result = await RunAsync("upgrade");

        Assert.Equal(7, result.Code);
        Assert.Equal(2, _processes.Count);
        var (curl, curlArgs, curlEnv) = _processes[0];
        Assert.Equal("curl", curl);
        Assert.Equal(
            ["--fail", "--silent", "--show-error", "--location", "--retry", "3", "--max-time", "60", "--output", script!, HostCliCommand.DefaultInstallerUrl],
            curlArgs);
        var (bash, bashArgs, bashEnv) = _processes[1];
        Assert.Equal("bash", bash);
        Assert.Equal([script!], bashArgs);
        Assert.Equal("latest", bashEnv["PERGA_VERSION"]);
        Assert.Equal(HostCliCommand.DefaultRepo, bashEnv["PERGA_REPO"]);
        foreach (var name in new[] { "PERGA_INSTALL_DIR", "PERGA_BIN_DIR", "PERGA_DATA_DIR", "PERGA_DB_PATH", "PATH" })
            Assert.Equal(_variables[name], bashEnv[name]);
        Assert.Equal(curlEnv, bashEnv);
        Assert.False(Directory.Exists(Path.GetDirectoryName(script)));
    }

    [Fact]
    public async Task UpgradeSelectsTheVersionAndInstallerUrl()
    {
        _variables["PERGA_VERSION"] = "v0.1.0";
        var fromEnv = await RunAsync("upgrade");
        Assert.Equal(1, fromEnv.Code);
        Assert.Contains("could not download the installer", fromEnv.Error);
        Assert.Equal("v0.1.0", _processes.Single().Env["PERGA_VERSION"]);

        _processes.Clear();
        _variables["PERGA_REPO"] = "someone/fork";
        _processResult = (file, args) =>
        {
            if (file == "curl") File.WriteAllText(args[^2], "exit 0\n");
            return 0;
        };
        var pinned = await RunAsync("upgrade", "v0.2.0-rc.1");
        Assert.Equal(0, pinned.Code);
        Assert.Equal("https://raw.githubusercontent.com/someone/fork/main/install.sh", _processes[0].Args[^1]);
        Assert.Equal("v0.2.0-rc.1", _processes[1].Env["PERGA_VERSION"]);
        Assert.Equal("someone/fork", _processes[1].Env["PERGA_REPO"]);

        _processes.Clear();
        _variables["PERGA_INSTALLER_URL"] = "file:///srv/install.sh";
        Assert.Equal(0, (await RunAsync("upgrade", "latest")).Code);
        Assert.Equal("file:///srv/install.sh", _processes[0].Args[^1]);
    }

    [Theory]
    [InlineData(2, "unknown option '--force'", "--force")]
    [InlineData(2, "expected at most one version", "v0.1.0", "v0.2.0")]
    [InlineData(1, "invalid version 'v1.2'", "v1.2")]
    [InlineData(1, "invalid version '1.2.3'", "1.2.3")]
    [InlineData(1, "invalid version 'v01.2.3'", "v01.2.3")]
    [InlineData(1, "invalid version 'v1.2.3-01'", "v1.2.3-01")]
    [InlineData(1, "invalid version 'v1.2.3+build'", "v1.2.3+build")]
    public async Task UpgradeRejectsInvalidArgumentsBeforeDownloading(int code, string message, params string[] args)
    {
        var result = await RunAsync(["upgrade", .. args]);

        Assert.Equal(code, result.Code);
        Assert.Contains(message, result.Error);
        Assert.Empty(_processes);
    }

    [Theory]
    [InlineData("PERGA_REPO", "not-a-repo", "invalid PERGA_REPO")]
    [InlineData("PERGA_REPO", "../evil", "invalid PERGA_REPO")]
    [InlineData("PERGA_INSTALLER_URL", "http://example.com/install.sh", "refusing installer URL")]
    public async Task UpgradeRejectsUnsafeSources(string name, string value, string message)
    {
        _variables[name] = value;

        var result = await RunAsync("upgrade");

        Assert.Equal(1, result.Code);
        Assert.Contains(message, result.Error);
        Assert.Empty(_processes);
        Assert.Contains("Usage: perga upgrade", (await RunAsync("upgrade", "--help")).Output);
    }

    [Theory]
    [InlineData("--yes")]
    [InlineData("-y")]
    public async Task UninstallRemovesThePackageAndLauncherAndKeepsData(string confirm)
    {
        CreatePackage();
        UseLauncherEnvironment();
        WriteLauncher();
        var db = Path.Combine(Root, "db", "custom.db");
        _variables["PERGA_DB_PATH"] = db;
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        File.WriteAllText(db, "db");
        File.WriteAllText(Path.Combine(Data, "perga.db"), "data");
        File.WriteAllText(Path.Combine(Bin, "other-tool"), "keep");

        var result = await RunAsync("uninstall", confirm);

        Assert.Equal(0, result.Code);
        Assert.False(Directory.Exists(Install));
        Assert.False(File.Exists(Launcher));
        Assert.Equal("db", File.ReadAllText(db));
        Assert.Equal("data", File.ReadAllText(Path.Combine(Data, "perga.db")));
        Assert.True(File.Exists(Path.Combine(Bin, "other-tool")));
        Assert.Contains($"Your data remains in {Data}", result.Output);
    }

    [Fact]
    public async Task UninstallAsksInteractivelyAndRequiresYesOtherwise()
    {
        CreatePackage();
        UseLauncherEnvironment();
        WriteLauncher();

        var refused = await RunAsync("uninstall");
        Assert.Equal(1, refused.Code);
        Assert.Contains("re-run with --yes", refused.Error);
        AssertInstalled();

        _interactive = true;
        _input = "n\n";
        var declined = await RunAsync("uninstall");
        Assert.Equal(1, declined.Code);
        Assert.Contains("Continue? [y/N]", declined.Output);
        Assert.Contains("Uninstall cancelled.", declined.Output);
        AssertInstalled();

        _input = "YES\n";
        var accepted = await RunAsync("uninstall");
        Assert.Equal(0, accepted.Code);
        Assert.False(Directory.Exists(Install));
        Assert.False(File.Exists(Launcher));
    }

    [Fact]
    public async Task UninstallRejectsUnknownArgumentsAndPrintsHelp()
    {
        CreatePackage();
        UseLauncherEnvironment();

        var unknown = await RunAsync("uninstall", "--force");
        Assert.Equal(2, unknown.Code);
        Assert.Contains("unknown argument '--force'", unknown.Error);
        Assert.Contains("Usage: perga uninstall [--yes]", (await RunAsync("uninstall", "-h")).Output);
        AssertInstalled();
    }

    [Fact]
    public async Task UninstallRequiresARegularInstallMarker()
    {
        CreatePackage();
        UseLauncherEnvironment();
        var marker = Path.Combine(Install, HostCliCommand.InstallMarker);
        File.Delete(marker);

        await AssertUninstallRefusedAsync($"has no regular {HostCliCommand.InstallMarker}");

        var elsewhere = Path.Combine(Root, "marker");
        File.WriteAllText(elsewhere, "perga-install-v1\n");
        File.CreateSymbolicLink(marker, elsewhere);
        await AssertUninstallRefusedAsync($"has no regular {HostCliCommand.InstallMarker}");
    }

    [Fact]
    public async Task UninstallRejectsASymlinkedInstallDirectoryAndTheRoot()
    {
        CreatePackage();
        var link = Path.Combine(Root, "app-link");
        Directory.CreateSymbolicLink(link, Install);
        UseLauncherEnvironment(install: link);

        await AssertUninstallRefusedAsync("is a symlink");

        UseLauncherEnvironment(install: "/");
        await AssertUninstallRefusedAsync("filesystem root");
        AssertInstalled();
    }

    [Fact]
    public async Task UninstallResolvesSymlinkedAncestorsToThePhysicalInstall()
    {
        var real = Path.Combine(Root, "real");
        var alias = Path.Combine(Root, "alias");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(alias, real);
        var physical = Path.Combine(real, "app");
        CreatePackage(physical);
        UseLauncherEnvironment(install: Path.Combine(alias, "app"));
        WriteLauncher(physical);

        var result = await RunAsync("uninstall", "--yes");

        Assert.Equal(0, result.Code);
        Assert.Contains($"install:  {physical}", result.Output);
        Assert.False(Directory.Exists(physical));
        Assert.False(File.Exists(Launcher));
    }

    [Fact]
    public async Task UninstallRefusesDataOrDatabaseInsideTheInstall()
    {
        CreatePackage();
        UseLauncherEnvironment();
        _variables["PERGA_DATA_DIR"] = Path.Combine(Install, "data");
        await AssertUninstallRefusedAsync("PERGA_DATA_DIR");

        _variables["PERGA_DATA_DIR"] = Install;
        await AssertUninstallRefusedAsync("inside the install directory");

        _variables["PERGA_DATA_DIR"] = Data;
        _variables["PERGA_DB_PATH"] = Path.Combine(Install, "perga.db");
        await AssertUninstallRefusedAsync("database path");

        // A path whose ancestor is a symlink into the install is caught by physical resolution.
        var link = Path.Combine(Root, "into-install");
        Directory.CreateSymbolicLink(link, Install);
        _variables["PERGA_DB_PATH"] = Path.Combine(link, "nested", "perga.db");
        await AssertUninstallRefusedAsync("inside the install directory");
    }

    [Fact]
    public async Task UninstallRefusesSymlinkedDataOrDatabase()
    {
        CreatePackage();
        UseLauncherEnvironment();
        var elsewhere = Path.Combine(Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var dataLink = Path.Combine(Root, "data-link");
        Directory.CreateSymbolicLink(dataLink, elsewhere);
        _variables["PERGA_DATA_DIR"] = dataLink;
        await AssertUninstallRefusedAsync("PERGA_DATA_DIR");

        _variables["PERGA_DATA_DIR"] = Data;
        var dbLink = Path.Combine(Root, "db-link");
        File.CreateSymbolicLink(dbLink, Path.Combine(elsewhere, "perga.db"));
        _variables["PERGA_DB_PATH"] = dbLink;
        await AssertUninstallRefusedAsync("database path");
    }

    [Theory]
    [InlineData("PERGA_INSTALL_DIR")]
    [InlineData("PERGA_BIN_DIR")]
    public async Task UninstallRefusesOverridesNamingAnotherInstance(string name)
    {
        CreatePackage();
        var other = Path.Combine(Root, "other");
        CreatePackage(other);
        UseLauncherEnvironment();
        WriteLauncher();
        _variables[name] = other;

        await AssertUninstallRefusedAsync("does not match this launcher");
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task UninstallRefusesALauncherOwnedByAnotherInstall()
    {
        CreatePackage();
        UseLauncherEnvironment();
        WriteLauncher(Path.Combine(Root, "other"));
        await AssertUninstallRefusedAsync("does not belong to");

        File.WriteAllText(Launcher, $"#!/usr/bin/env bash\n# DEFAULT_INSTALL_DIR='{Install}'\n");
        await AssertUninstallRefusedAsync("does not belong to");

        File.Delete(Launcher);
        var target = Path.Combine(Root, "launcher-target");
        File.WriteAllText(target, $"DEFAULT_INSTALL_DIR='{Install}'\n");
        File.CreateSymbolicLink(Launcher, target);
        await AssertUninstallRefusedAsync("not a regular file");

        File.Delete(Launcher);
        Directory.CreateDirectory(Launcher);
        await AssertUninstallRefusedAsync("not a regular file");
    }

    [Fact]
    public async Task UninstallMatchesShellQuotedInstallPathsAndToleratesAMissingLauncher()
    {
        var quoted = Path.Combine(Root, "it's app");
        CreatePackage(quoted);
        UseLauncherEnvironment(install: quoted);
        WriteLauncher(quoted);
        Assert.Contains(@"DEFAULT_INSTALL_DIR='" + Root + @"/it'\''s app'", File.ReadAllText(Launcher));

        Assert.Equal(0, (await RunAsync("uninstall", "--yes")).Code);
        Assert.False(Directory.Exists(quoted));

        CreatePackage();
        UseLauncherEnvironment();
        var noLauncher = await RunAsync("uninstall", "--yes");
        Assert.Equal(0, noLauncher.Code);
        Assert.DoesNotContain("launcher:", noLauncher.Output);
        Assert.False(Directory.Exists(Install));
    }

    [Theory]
    [InlineData("PERGA_INSTALL_DIR")]
    [InlineData("PERGA_DATA_DIR")]
    [InlineData("PERGA_DB_PATH")]
    [InlineData("PERGA_MANAGED_BIN_DIR")]
    public async Task UninstallRejectsControlCharacters(string name)
    {
        CreatePackage();
        UseLauncherEnvironment();
        _variables.Remove("PERGA_MANAGED_INSTALL_DIR");
        _variables[name] = _variables.GetValueOrDefault(name, Data) + "\nsecond";

        await AssertUninstallRefusedAsync("control characters");
    }

    [Fact]
    public async Task UninstallDirectInvocationRefusesOverridePointingToAnotherMarkedInstall()
    {
        CreatePackage(Install);
        var other = Path.Combine(Root, "other");
        CreatePackage(other);
        Directory.CreateDirectory(Data);
        _baseDirectory = Install;
        _variables["PERGA_DATA_DIR"] = Data;
        _variables["PERGA_INSTALL_DIR"] = other;

        await AssertUninstallRefusedAsync("does not match this running installation");
        Assert.True(Directory.Exists(Install));
        Assert.True(Directory.Exists(other));

        _variables["PERGA_MANAGED_INSTALL_DIR"] = other;
        await AssertUninstallRefusedAsync("does not match this running installation");
        Assert.True(Directory.Exists(Install));
        Assert.True(Directory.Exists(other));

        _variables.Remove("PERGA_MANAGED_INSTALL_DIR");
        _variables.Remove("PERGA_INSTALL_DIR");
        var direct = await RunAsync("uninstall", "--yes");
        Assert.Equal(0, direct.Code);
        Assert.False(Directory.Exists(Install));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task UninstallRefusesAspNetCoreStorageVariablesInsideTheInstall()
    {
        CreatePackage();
        UseLauncherEnvironment();

        _variables["Perga__DbPath"] = Path.Combine(Install, "custom.db");
        await AssertUninstallRefusedAsync("inside the install directory");
        _variables.Remove("Perga__DbPath");

        _variables["perga__dbpath"] = Path.Combine(Install, "custom.db");
        await AssertUninstallRefusedAsync("inside the install directory");
        _variables.Remove("perga__dbpath");

        _variables["Perga__DataDir"] = Path.Combine(Install, "asp-data");
        await AssertUninstallRefusedAsync("inside the install directory");
        _variables.Remove("Perga__DataDir");

        _variables["PERGA__DATADIRECTORY"] = Path.Combine(Install, "legacy-data");
        await AssertUninstallRefusedAsync("inside the install directory");
        _variables.Remove("PERGA__DATADIRECTORY");

        var aspData = Path.Combine(Root, "asp-data");
        Directory.CreateDirectory(aspData);
        File.CreateSymbolicLink(Path.Combine(aspData, "perga.db"), Path.Combine(Install, "perga.db"));
        _variables["Perga__DataDir"] = aspData;
        await AssertUninstallRefusedAsync("is a symlink");
        _variables.Remove("Perga__DataDir");

        _variables["Perga__DbPath"] = "relative/perga.db";
        await AssertUninstallRefusedAsync("must be an absolute path");
        _variables.Remove("Perga__DbPath");

        _variables["Perga__DataDir"] = "./relative-data";
        await AssertUninstallRefusedAsync("must be an absolute path");
        _variables.Remove("Perga__DataDir");

        _variables["PERGA__DATADIRECTORY"] = "legacy-relative";
        await AssertUninstallRefusedAsync("must be an absolute path");
        _variables.Remove("PERGA__DATADIRECTORY");

        _variables["Perga__DbPath"] = Path.Combine(Root, "bad\npath.db");
        await AssertUninstallRefusedAsync("control characters");
    }

    [Fact]
    public async Task UninstallUsesCaseInsensitiveExclusionButExactOwnershipWhenConfigured()
    {
        CreatePackage();
        UseLauncherEnvironment();
        WriteLauncher();
        var upperInstall = Path.Combine(Root, "APP");
        var upperBin = Path.Combine(Root, "BIN");

        _variables["PERGA_DATA_DIR"] = Path.Combine(upperInstall, "data");
        var dataOverlap = await RunAsync(env => env with { CaseInsensitivePaths = true }, "uninstall", "--yes");
        Assert.Equal(1, dataOverlap.Code);
        Assert.Contains("inside the install directory", dataOverlap.Error);
        AssertInstalled();

        _variables["PERGA_DATA_DIR"] = Data;
        _variables["PERGA_DB_PATH"] = Path.Combine(upperInstall, "perga.db");
        var dbOverlap = await RunAsync(env => env with { CaseInsensitivePaths = true }, "uninstall", "--yes");
        Assert.Equal(1, dbOverlap.Code);
        Assert.Contains("inside the install directory", dbOverlap.Error);
        AssertInstalled();
        _variables.Remove("PERGA_DB_PATH");

        _variables["Perga__DataDir"] = Path.Combine(upperInstall, "asp-data");
        var aspOverlap = await RunAsync(env => env with { CaseInsensitivePaths = true }, "uninstall", "--yes");
        Assert.Equal(1, aspOverlap.Code);
        Assert.Contains("inside the install directory", aspOverlap.Error);
        AssertInstalled();
        _variables.Remove("Perga__DataDir");

        // Authorization / ownership checks remain exact Ordinal even when CaseInsensitivePaths is true.
        WriteLauncher(upperInstall);
        var mismatchedLauncher = await RunAsync(env => env with { CaseInsensitivePaths = true }, "uninstall", "--yes");
        Assert.Equal(1, mismatchedLauncher.Code);
        Assert.Contains("does not belong to", mismatchedLauncher.Error);
        AssertInstalled();
        Assert.True(File.Exists(Launcher));

        WriteLauncher(Install);
        _variables["PERGA_INSTALL_DIR"] = upperInstall;
        var mismatchedInstall = await RunAsync(env => env with { CaseInsensitivePaths = true }, "uninstall", "--yes");
        Assert.Equal(1, mismatchedInstall.Code);
        Assert.Contains("does not match this launcher", mismatchedInstall.Error);
        AssertInstalled();

        _variables["PERGA_INSTALL_DIR"] = Install;
        _variables["PERGA_BIN_DIR"] = upperBin;
        var mismatchedBin = await RunAsync(env => env with { CaseInsensitivePaths = true }, "uninstall", "--yes");
        Assert.Equal(1, mismatchedBin.Code);
        Assert.Contains("does not match this launcher", mismatchedBin.Error);
        AssertInstalled();

        _variables["PERGA_BIN_DIR"] = Bin;
        var ok = await RunAsync(env => env with { CaseInsensitivePaths = true }, "uninstall", "--yes");
        Assert.Equal(0, ok.Code);
        Assert.False(Directory.Exists(Install));
        Assert.False(File.Exists(Launcher));
    }

    [Fact]
    public async Task UpgradeReturnsCleanErrorWhenExecutableIsMissing()
    {
        var defaultRunner = new HostCliEnvironment
        {
            Variables = _variables,
            BaseDirectory = Install,
            HomeDirectory = Home,
            Input = TextReader.Null,
            Output = TextWriter.Null,
            Error = TextWriter.Null
        }.RunProcessAsync;

        var missing = await RunAsync(env => env with
        {
            RunProcessAsync = (_, args, vars, ct) =>
                defaultRunner("perga-nonexistent-binary-for-test", args, vars, ct)
        }, "upgrade");
        Assert.Equal(1, missing.Code);
        Assert.Contains("error: Could not start 'perga-nonexistent-binary-for-test'", missing.Error);

        var win32 = await RunAsync(env => env with
        {
            RunProcessAsync = (_, _, _, _) => throw new Win32Exception("missing binary")
        }, "upgrade");
        Assert.Equal(1, win32.Code);
        Assert.Contains("error: missing binary", win32.Error);
    }

    [Fact]
    public async Task DoctorDeepReportsAvailableAndUnavailableProvidersWithActionableFixHints()
    {
        CreatePackage();
        UseLauncherEnvironment();
        var called = false;
        string? probedInstall = null;
        string? probedNode = null;

        var result = await RunAsync(env => env with
        {
            ProbeProvidersDeepAsync = (installDir, nodePath, _, _) =>
            {
                called = true;
                probedInstall = installDir;
                probedNode = nodePath;
                return Task.FromResult<IReadOnlyList<ProviderStatus>>(
                [
                    new ProviderStatus("claude", true, "2.1.0", null, ["claude-sonnet-4", "claude-opus-4"]),
                    new ProviderStatus("codex", false, null, "Not logged in", []),
                    new ProviderStatus("acp:antigravity", false, null, "agy binary not found", []),
                    new ProviderStatus("acp:opencode", false, null, "opencode binary not found", [])
                ]);
            }
        }, "doctor", "--deep");

        Assert.Equal(0, result.Code);
        Assert.True(called);
        Assert.Equal(Install, probedInstall);
        Assert.Equal(_node.ExecutablePath, probedNode);
        Assert.Contains("[ok]   provider:claude  2.1.0 (2 models)", result.Output);
        Assert.Contains("[warn] provider:codex  unavailable — Not logged in", result.Output);
        Assert.Contains("fix: npm install -g @openai/codex && codex login", result.Output);
        Assert.Contains("[warn] provider:acp:antigravity  unavailable — agy binary not found", result.Output);
        Assert.Contains("Install standalone agy >= 1.5.0: curl -fsSL https://antigravity.google/cli/install.sh | bash (not just the IDE app)", result.Output);
        Assert.Contains("[warn] provider:acp:opencode  unavailable — opencode binary not found", result.Output);
        Assert.Contains("fix: Install OpenCode and sign in with 'opencode'", result.Output);
        Assert.DoesNotContain("No provider is ready yet.", result.Output);
    }

    [Theory]
    [InlineData(false, null, "not found", "unavailable")]
    [InlineData(true, "2.1.0", "unauthenticated", "2.1.0")]
    [InlineData(true, null, "model list failed", "unknown version")]
    public async Task DoctorDeepPrintsNoProviderReadyTipWhenProvidersHaveErrors(
        bool available, string? version, string reason, string expectedVersion)
    {
        CreatePackage();
        UseLauncherEnvironment();

        var result = await RunAsync(env => env with
        {
            ProbeProvidersDeepAsync = (_, _, _, _) => Task.FromResult<IReadOnlyList<ProviderStatus>>(
            [
                new ProviderStatus("claude", available, version, reason, [])
            ])
        }, "doctor", "--deep");

        Assert.Equal(0, result.Code);
        Assert.Contains($"[warn] provider:claude  {expectedVersion} — {reason}", result.Output);
        Assert.Contains("fix: Install Claude Code and run 'claude auth login'", result.Output);
        Assert.DoesNotContain("[ok]   provider:claude", result.Output);
        Assert.Contains("No provider is ready yet. Install and sign in to at least one CLI above, then re-run 'perga doctor --deep'.", result.Output);
    }

    [Theory]
    [InlineData(null, null, "Production", false)]
    [InlineData("Staging", "Development", "Development", false)]
    [InlineData("Staging", null, "Staging", false)]
    [InlineData(null, "Development", "Development", false)]
    [InlineData(null, null, "Production", true)]
    public async Task DoctorDeepUsesHostProviderOverridesAndTheFilteredCallerEnvironment(
        string? aspnetEnvironment, string? dotnetEnvironment, string expectedEnvironment, bool invalidAuthSetting)
    {
        CreatePackage();
        UseLauncherEnvironment();
        Write(Path.Combine(Install, "appsettings.json"), """
            { "Perga": { "ProviderExecutables": { "codex": "json-codex", "agy": "json-agy" } } }
            """);
        foreach (var environmentName in new[] { "Production", "Staging", "Development" })
        {
            Write(Path.Combine(Install, $"appsettings.{environmentName}.json"), $$"""
                { "Perga": { "ProviderExecutables": { "codex": "profile-codex", "claude": "{{environmentName}}-claude" } } }
                """);
        }
        if (aspnetEnvironment is not null)
            _variables["ASPNETCORE_ENVIRONMENT"] = aspnetEnvironment;
        if (dotnetEnvironment is not null)
            _variables["DOTNET_ENVIRONMENT"] = dotnetEnvironment;
        if (invalidAuthSetting)
            _variables["PERGA_AUTH_SESSION_DAYS"] = "abc";
        _variables["Perga__ProviderExecutables__codex"] = "env-codex";
        _variables["PERGA_DB_PATH"] = Path.Combine(Data, "perga.db");
        _variables["PROBE_TEST"] = "caller";
        _variables["PROBE_EXPECTED_PROFILE"] = expectedEnvironment;
        _variables["HOME"] = Home;
        _variables["PATH"] = "/usr/bin:/bin";
        Directory.CreateDirectory(Path.Combine(Home, ".local", "bin"));

        var fakeNode = Path.Combine(Root, "node");
        WriteExecutable(fakeNode, """
            #!/bin/sh
            if [ "$1" = --version ]; then echo v24.4.1; exit 0; fi
            [ "$PROBE_TEST" = caller ] && [ -z "${PERGA_DB_PATH+x}" ] || exit 1
            case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) exit 1 ;; esac
            IFS= read -r request
            case "$request" in
              *'"runtime":"codex"'*'"executable":"env-codex"'*|*'"runtime":"antigravity"'*'"executable":"json-agy"'*) available=true ;;
              *'"runtime":"claude"'*'"executable":"'"$PROBE_EXPECTED_PROFILE"'-claude"'*) available=true ;;
              *) available=false ;;
            esac
            printf '{"jsonrpc":"2.0","id":1,"result":{"available":%s,"version":"test","models":[]}}\n' "$available"
            """);
        _node = _node with { ExecutablePath = fakeNode };

        var result = await RunAsync("doctor", "--deep");

        Assert.Equal(0, result.Code);
        Assert.Contains("[ok]   provider:codex  test (0 models)", result.Output);
        Assert.Contains("[ok]   provider:acp:antigravity  test (0 models)", result.Output);
        Assert.Contains("[ok]   provider:claude  test (0 models)", result.Output);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    public async Task DoctorDeepReportsMalformedSettingsAsCliError(string settingsFile)
    {
        CreatePackage();
        UseLauncherEnvironment();
        Write(Path.Combine(Install, settingsFile), "{");

        var result = await RunAsync("doctor", "--deep");

        Assert.Equal(1, result.Code);
        Assert.StartsWith("error: ", result.Error);
        Assert.Contains(settingsFile, result.Error);
    }

    [Fact]
    public async Task DoctorDeepSkipsProbeWhenNodeFailsOrBridgeIsMissingAndRejectsUnknownFlags()
    {
        CreatePackage();
        UseLauncherEnvironment();
        var called = false;
        _node = new OarNodeRuntimeStatus(true, false, new Version(20, 0, 0), "/usr/bin/node", "Node 20 is too old.");

        var oldNode = await RunAsync(env => env with
        {
            ProbeProvidersDeepAsync = (_, _, _, _) => { called = true; return Task.FromResult<IReadOnlyList<ProviderStatus>>([]); }
        }, "doctor", "--deep");

        Assert.Equal(1, oldNode.Code);
        Assert.False(called);
        Assert.Contains("[warn] deep-probe skipped (fix package/node checks above first)", oldNode.Output);

        _node = new OarNodeRuntimeStatus(true, true, new Version(24, 4, 1), "/opt/node/bin/node", null);
        File.Delete(Path.Combine(Install, "oar-bridge", "perga-oar-bridge.mjs"));
        var missingBridge = await RunAsync(env => env with
        {
            ProbeProvidersDeepAsync = (_, _, _, _) => { called = true; return Task.FromResult<IReadOnlyList<ProviderStatus>>([]); }
        }, "doctor", "--deep");

        Assert.Equal(1, missingBridge.Code);
        Assert.False(called);
        Assert.Contains("[warn] deep-probe skipped (fix package/node checks above first)", missingBridge.Output);

        var unknownFlag = await RunAsync("doctor", "--bogus");
        Assert.Equal(2, unknownFlag.Code);
        Assert.Contains("unexpected argument '--bogus'", unknownFlag.Error);
        Assert.Contains("Usage: perga doctor [--deep]", unknownFlag.Error);
    }

    [Fact]
    public async Task StartupOnboardingSkipsNonInteractiveTestingEphemeralAndExistingOwner()
    {
        var authStore = new FakeAuthStore(owner: null);
        var called = false;
        Task<int> Runner(string[] _, TextReader __, TextWriter ___, TextWriter ____, CancellationToken _____)
        {
            called = true;
            return Task.FromResult(0);
        }

        Assert.Equal(0, await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, "/tmp/perga.db", isEphemeral: false, isTestingEnvironment: false, isInteractive: false,
            TextReader.Null, TextWriter.Null, TextWriter.Null, Runner));
        Assert.Equal(0, await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, "/tmp/perga.db", isEphemeral: false, isTestingEnvironment: true, isInteractive: true,
            TextReader.Null, TextWriter.Null, TextWriter.Null, Runner));
        Assert.Equal(0, await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, "/tmp/perga.db", isEphemeral: true, isTestingEnvironment: false, isInteractive: true,
            TextReader.Null, TextWriter.Null, TextWriter.Null, Runner));
        Assert.Equal(0, authStore.GetOwnerCalls);

        var withOwner = new FakeAuthStore(new AuthUserRecord("usr_1", "owner", "Owner", "owner", "hash", 1, 1, null));
        Assert.Equal(0, await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            withOwner, "/tmp/perga.db", isEphemeral: false, isTestingEnvironment: false, isInteractive: true,
            TextReader.Null, TextWriter.Null, TextWriter.Null, Runner));
        Assert.Equal(1, withOwner.GetOwnerCalls);
        Assert.False(called);
    }

    [Fact]
    public async Task StartupOnboardingPromptsInteractivelyAndDelegatesToAuthCli()
    {
        var dbPath = Path.Combine(Data, "owner's $active.db");
        var authStore = new FakeAuthStore(owner: null);

        // Declining prints hint and continues startup with exit 0.
        var declineOut = new StringWriter();
        var declineCalled = false;
        var declineCode = await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, dbPath, isEphemeral: false, isTestingEnvironment: false, isInteractive: true,
            new StringReader("n\n"), declineOut, TextWriter.Null,
            (_, _, _, _, _) => { declineCalled = true; return Task.FromResult(0); });
        Assert.Equal(0, declineCode);
        Assert.False(declineCalled);
        Assert.Contains($"No owner account is configured yet for {dbPath}.", declineOut.ToString());
        Assert.Contains($"perga auth init-owner --username owner --allow-migrate --db-path '{Data}/owner'\\''s $active.db'", declineOut.ToString());

        // Accepting with Enter + empty username defaults to 'owner' and delegates to AuthCliCommand.
        string[]? capturedArgs = null;
        var acceptOut = new StringWriter();
        var acceptCode = await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, dbPath, isEphemeral: false, isTestingEnvironment: false, isInteractive: true,
            new StringReader("\n\n"), acceptOut, TextWriter.Null,
            (args, _, _, _, _) => { capturedArgs = args; return Task.FromResult(0); });
        Assert.Equal(0, acceptCode);
        Assert.NotNull(capturedArgs);
        Assert.Equal(["auth", "init-owner", "--username", "owner", "--allow-migrate", "--db-path", dbPath], capturedArgs);

        // Custom username + non-zero exit code propagates cleanly.
        var failCode = await HostStartupOnboarding.EnsureInteractiveOwnerIfNeededAsync(
            authStore, dbPath, isEphemeral: false, isTestingEnvironment: false, isInteractive: true,
            new StringReader("yes\nalice\n"), new StringWriter(), TextWriter.Null,
            (args, _, _, _, _) => { capturedArgs = args; return Task.FromResult(1); });
        Assert.Equal(1, failCode);
        Assert.NotNull(capturedArgs);
        Assert.Equal(["auth", "init-owner", "--username", "alice", "--allow-migrate", "--db-path", dbPath], capturedArgs);
    }

    [Fact]
    public void FormatStartupErrorHandlesPortInUseAndPermissionDenied()
    {
        var addressInUse = HostStartupOnboarding.FormatStartupError(
            new IOException("Failed to bind", new AddressInUseException("port busy")));
        Assert.NotNull(addressInUse);
        Assert.Contains("error: Could not start Perga because the address/port is already in use.", addressInUse);
        Assert.Contains("perga --urls http://127.0.0.1:4042", addressInUse);

        var socketInUse = HostStartupOnboarding.FormatStartupError(
            new IOException("Failed to bind", new SocketException((int)SocketError.AddressAlreadyInUse)));
        Assert.Equal(addressInUse, socketInUse);

        var permission = HostStartupOnboarding.FormatStartupError(new UnauthorizedAccessException("Access denied"));
        Assert.NotNull(permission);
        Assert.Contains("insufficient permissions", permission);
        Assert.Contains("perga --urls http://127.0.0.1:4041", permission);

        var socketAccess = HostStartupOnboarding.FormatStartupError(
            new SocketException((int)SocketError.AccessDenied));
        Assert.NotNull(socketAccess);
        Assert.Contains("insufficient permissions", socketAccess);

        Assert.Null(HostStartupOnboarding.FormatStartupError(new InvalidOperationException("foreign_key_check failed")));
        Assert.Null(HostStartupOnboarding.FormatStartupError(new ArgumentOutOfRangeException("unrelated")));
    }

    [Fact]
    public void FormatConfigurationErrorHandlesBindingAndValidationFailures()
    {
        foreach (var exception in new Exception[]
                 {
                     new FormatException("Invalid integer value"),
                     new InvalidOperationException("Invalid auth policy"),
                     new ArgumentException("Invalid auth enum"),
                     new OverflowException("Integer value out of range")
                 })
        {
            var message = HostStartupOnboarding.FormatConfigurationError(exception);
            Assert.StartsWith("error: Invalid Perga configuration:", message);
            Assert.Contains(exception.Message, message);
        }
    }

    private sealed class FakeAuthStore(AuthUserRecord? owner) : IAuthStore
    {
        public int GetOwnerCalls { get; private set; }

        public Task<AuthUserRecord?> GetOwnerAsync(CancellationToken ct)
        {
            GetOwnerCalls++;
            return Task.FromResult(owner);
        }

        public Task<SessionIdentityDto?> TryCreateSessionIfVersionMatchesAsync(AuthUserRecord ownerRecord, NewAuthSession session, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> UpgradePasswordHashIfVersionMatchesAsync(AuthUserRecord ownerRecord, string passwordHash, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SessionIdentityDto?> GetValidOwnerSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> IsOwnerSessionValidAsync(string sessionId, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task TouchSessionLastSeenIfStaleAsync(string sessionId, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RevokeSessionByTokenHashAsync(string tokenHash, long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<SystemAuthStatusDto> GetAuthStatusAsync(long nowMs, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private async Task AssertUninstallRefusedAsync(string message)
    {
        var result = await RunAsync("uninstall", "--yes");
        Assert.Equal(1, result.Code);
        Assert.Contains(message, result.Error);
        Assert.DoesNotContain("Removed Perga", result.Output);
        if (Directory.Exists(Install))
            AssertInstalled();
    }

    private void AssertInstalled() => Assert.True(File.Exists(Path.Combine(Install, "Perga.Host")));

    private void UseLauncherEnvironment(string? install = null)
    {
        install ??= Install;
        _baseDirectory = install;
        _variables["PERGA_INSTALL_DIR"] = install;
        _variables["PERGA_MANAGED_INSTALL_DIR"] = install;
        _variables["PERGA_BIN_DIR"] = Bin;
        _variables["PERGA_MANAGED_BIN_DIR"] = Bin;
        _variables["PERGA_DATA_DIR"] = Data;
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Bin);
    }

    private void CreatePackage(string? install = null, string version = "v0.1.0\n")
    {
        install ??= Install;
        WriteExecutable(Path.Combine(install, "Perga.Host"));
        WriteExecutable(Path.Combine(install, "perga-term-bridge"));
        Write(Path.Combine(install, "web", "index.html"), "<html></html>");
        Write(Path.Combine(install, "oar-bridge", "perga-oar-bridge.mjs"), "export {};");
        Write(Path.Combine(install, "VERSION"), version);
        Write(Path.Combine(install, HostCliCommand.InstallMarker), "perga-install-v1\n");
    }

    private void WriteLauncher(string? install = null)
    {
        var quoted = "'" + (install ?? Install).Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        Write(Launcher, $"#!/usr/bin/env bash\nset -euo pipefail\nDEFAULT_INSTALL_DIR={quoted}\nexec \"$DEFAULT_INSTALL_DIR/Perga.Host\" \"$@\"\n");
        File.SetUnixFileMode(Launcher, (UnixFileMode)0b111_101_101);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void WriteExecutable(string path, string content = "#!/bin/sh\n")
    {
        Write(path, content);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private Task<CliResult> RunAsync(params string[] args) => RunAsync(env => env, args);

    private async Task<CliResult> RunAsync(Func<HostCliEnvironment, HostCliEnvironment> customize, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var environment = customize(new HostCliEnvironment
        {
            Variables = new Dictionary<string, string>(_variables, StringComparer.Ordinal),
            BaseDirectory = _baseDirectory,
            HomeDirectory = Home,
            Input = new StringReader(_input),
            Output = output,
            Error = error,
            IsInteractive = _interactive,
            ProbeNode = (_, _) => _node,
            RunProcessAsync = (file, arguments, env, _) =>
            {
                _processes.Add((file, arguments.ToArray(), new Dictionary<string, string>(env, StringComparer.Ordinal)));
                return Task.FromResult(_processResult(file, arguments));
            }
        });
        var code = await HostCliCommand.RunAsync(args, environment);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    private async Task<CliResult> RunHostProcessAsync(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Perga.Host"))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Root
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["PERGA_INSTALL_DIR"] = Install;
        start.Environment["PERGA_DATA_DIR"] = Data;
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new CliResult(process.ExitCode, await output, await error);
    }

    private sealed record CliResult(int Code, string Output, string Error);
}
