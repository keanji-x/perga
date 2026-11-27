using System.Diagnostics;
using System.Runtime.Versioning;
using Perga.Host.Cli;

namespace Perga.Host.IntegrationTests;

// install.sh and tools/release.sh run under bash on Linux and macOS.
[UnsupportedOSPlatform("windows")]
public sealed class InstallAndReleaseScriptsTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Installer = Path.Combine(RepoRoot, "install.sh");
    private static readonly string ReleaseScript = Path.Combine(RepoRoot, "tools", "release.sh");
    private static readonly string Package = OperatingSystem.IsMacOS() ? "perga-darwin-arm64" : "perga-linux-x64";

    // Echoes what the launcher handed over; `upgrade` re-runs the installer the way HostCliCommand does.
    private const string StubHost = """
        #!/usr/bin/env bash
        if [[ "${1:-}" == upgrade ]]; then exec bash "$PERGA_TEST_INSTALLER"; fi
        printf 'self=%s\n' "$0"
        printf 'pwd=%s\n' "$PWD"
        printf 'args=%s\n' "$*"
        for name in PERGA_INSTALL_DIR PERGA_MANAGED_INSTALL_DIR PERGA_MANAGED_BIN_DIR PERGA_BIN_DIR PERGA_DATA_DIR PERGA_DB_PATH PERGA_REPO PERGA_STATIC_WEB_DIR; do
          printf '%s=%s\n' "$name" "${!name-<unset>}"
        done
        """;

    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("perga-install-");

    public InstallAndReleaseScriptsTests()
    {
        Root = HostCliCommand.PhysicalPath(_temp.FullName);
        Home = Path.Combine(Root, "home");
        Directory.CreateDirectory(Home);
    }

    private string Root { get; }
    private string Home { get; }
    private string InstallDir => Path.Combine(Home, ".local", "share", "perga-app");
    private string Launcher => Path.Combine(Home, ".local", "bin", "perga");
    private string DefaultDataDir => Path.Combine(Home, ".local", "share", "perga");

    public void Dispose() => _temp.Delete(recursive: true);

    [Fact]
    public async Task ScriptsHaveValidBashSyntax()
    {
        foreach (var script in new[] { "install.sh", "tools/publish-host.sh", "tools/smoke-package.sh", "tools/release.sh" })
        {
            var result = await RunAsync("bash", ["-n", Path.Combine(RepoRoot, script)], new());
            Assert.True(result.Code == 0, $"{script}: {result.Error}");
        }

        Assert.True((File.GetUnixFileMode(ReleaseScript) & UnixFileMode.UserExecute) != 0, "tools/release.sh must be executable.");
    }

    [Fact]
    public async Task InstallerAndLauncherStayThin()
    {
        var installer = await File.ReadAllLinesAsync(Installer);
        Assert.InRange(installer.Length, 1, 280);
        var start = Array.IndexOf(installer, "  cat <<'LAUNCHER_EOF'");
        var end = Array.IndexOf(installer, "LAUNCHER_EOF");
        Assert.True(start > 0 && end > start, "install.sh writes the launcher body from a single-quoted heredoc.");
        Assert.DoesNotContain(installer[start..end], line => line.Contains("\\$", StringComparison.Ordinal));

        var package = CreatePackage("v0.1.0");
        Assert.Equal(0, (await InstallAsync(package)).Code);
        Assert.InRange((await File.ReadAllLinesAsync(Launcher)).Length, 1, 45);
    }

    [Fact]
    public async Task InstallerTellsNonBashShUsersHowToRunIt()
    {
        var shell = await RunAsync("sh", ["-c", "printf '%s' \"${BASH_VERSION:-}\""], BaseEnvironment(), Root);
        Assert.True(shell.Code == 0, shell.Error);
        if (shell.Output.Length > 0) return;

        var result = await RunAsync("sh", [Installer], BaseEnvironment(), Root);

        Assert.Equal(1, result.Code);
        Assert.Contains("Perga's installer requires bash. Run: curl -fsSL https://keanji-x.github.io/perga/install.sh | bash", result.Error);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task InstallerRejectsMuslBeforeDownloading(int lddExitCode)
    {
        if (!OperatingSystem.IsLinux()) return;

        var bin = Path.Combine(Root, "stubs");
        WriteExecutable(Path.Combine(bin, "getconf"), "#!/bin/sh\nexit 1\n");
        WriteExecutable(Path.Combine(bin, "ldd"), $"#!/bin/sh\necho 'musl libc (x86_64)' >&2\nexit {lddExitCode}\n");
        WriteExecutable(Path.Combine(bin, "curl"), "#!/bin/sh\necho 'unexpected download' >&2\nexit 1\n");
        var env = BaseEnvironment();
        env["PATH"] = bin + Path.PathSeparator + env["PATH"];

        var result = await RunAsync("bash", [Installer], env, Root);

        Assert.Equal(1, result.Code);
        Assert.Contains("Linux musl (for example Alpine Linux) is not supported. Perga's Linux x64 release requires glibc.", result.Error);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task InstallerAcceptsGlibcWhenMuslLddIsOnPath()
    {
        if (!OperatingSystem.IsLinux()) return;

        var env = BaseEnvironment();
        var libc = await RunAsync("bash", ["-c", "getconf GNU_LIBC_VERSION"], env, Root);
        if (libc.Code != 0) return;

        var bin = Path.Combine(Root, "stubs");
        WriteExecutable(Path.Combine(bin, "ldd"), "#!/bin/sh\necho 'musl libc (x86_64)' >&2\n");
        env["PATH"] = bin + Path.PathSeparator + env["PATH"];

        var install = await InstallAsync(CreatePackage("v0.1.0"), env);

        Assert.True(install.Code == 0, install.Error);
        Assert.Contains("Installed Perga v0.1.0", install.Output);
    }

    [Fact]
    public async Task InstallerPrintsThreeOnboardingStepsWithCopyableCommands()
    {
        var package = CreatePackage("v0.1.0");
        var bin = Path.Combine(Root, "my bin");
        var launcher = $"'{Path.Combine(bin, "perga")}'";
        var env = BaseEnvironment();
        env["PERGA_BIN_DIR"] = bin;

        var install = await InstallAsync(package, env);

        Assert.True(install.Code == 0, install.Error);
        var steps = install.Output.Split("Next steps (first run):\n", 2);
        Assert.Equal(2, steps.Length);
        Assert.Equal($"""
              1. Check Node.js 24+ and your provider CLIs: {launcher} doctor
              2. Start Perga in the background: {launcher}
              3. If your browser does not open, visit the address shown by Perga.
            """ + "\n", steps[1]);
        Assert.Equal("doctor", Values(await RunAsync("sh", ["-c", $"{launcher} doctor"], env, Root))["args"]);
    }

    [Fact]
    public async Task InstallerReportsReinstallingTheSameVersion()
    {
        var package = CreatePackage("v0.1.0");
        var first = await InstallAsync(package);
        Assert.True(first.Code == 0, first.Error);

        var reinstall = await InstallAsync(package);

        Assert.True(reinstall.Code == 0, reinstall.Error);
        Assert.Contains("Reinstalled Perga v0.1.0", reinstall.Output);
        Assert.DoesNotContain("Upgraded Perga", reinstall.Output);
    }

    [Fact]
    public async Task FreshInstallWritesALauncherThatRoutesEveryCommand()
    {
        var package = CreatePackage("v0.1.0");

        var install = await InstallAsync(package);

        Assert.True(install.Code == 0, install.Error);
        Assert.Contains("Installed Perga v0.1.0", install.Output);
        Assert.Equal("perga-install-v1\n", await File.ReadAllTextAsync(Path.Combine(InstallDir, HostCliCommand.InstallMarker)));
        Assert.True(Directory.Exists(DefaultDataDir));
        var launcher = await File.ReadAllTextAsync(Launcher);
        Assert.Contains($"DEFAULT_INSTALL_DIR='{InstallDir}'", launcher);
        Assert.Contains("DEFAULT_DB_PATH=''", launcher);
        Assert.Contains("DEFAULT_REPO='keanji-x/perga'", launcher);
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(Launcher));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Launcher)!, ".perga-launcher.*"));

        var version = Values(await RunLauncherAsync(["version"]));
        Assert.Equal("version", version["args"]);
        Assert.Equal(Path.Combine(InstallDir, "Perga.Host"), version["self"]);
        Assert.Equal(InstallDir, version["PERGA_INSTALL_DIR"]);
        Assert.Equal(InstallDir, version["PERGA_MANAGED_INSTALL_DIR"]);
        Assert.Equal(Path.GetDirectoryName(Launcher), version["PERGA_MANAGED_BIN_DIR"]);
        Assert.Equal(DefaultDataDir, version["PERGA_DATA_DIR"]);
        Assert.Equal("<unset>", version["PERGA_DB_PATH"]);
        Assert.Equal(Path.Combine(InstallDir, "web"), version["PERGA_STATIC_WEB_DIR"]);

        var host = Values(await RunLauncherAsync([]));
        Assert.Equal($"start --contentRoot {InstallDir}", host["args"]);
        Assert.Equal(InstallDir, host["pwd"]);
        var start = Values(await RunLauncherAsync(["start", "--urls=http://127.0.0.1:9", "--flag"]));
        Assert.Equal($"start --contentRoot {InstallDir} --urls=http://127.0.0.1:9 --flag", start["args"]);
        var serve = Values(await RunLauncherAsync(["serve", "--require-auth"]));
        Assert.Equal($"serve --contentRoot {InstallDir} --require-auth", serve["args"]);
        foreach (var command in new[] { "status", "stop", "restart", "logs", "open" })
        {
            var routed = Values(await RunLauncherAsync([command]));
            Assert.Equal(command, routed["args"]);
            Assert.Equal(Root, routed["pwd"]);
        }

        // A relative data override resolves from the caller; uninstall always runs the pinned instance.
        var other = Path.Combine(Root, "other");
        var uninstall = Values(await RunLauncherAsync(["uninstall", "--yes"],
            new() { ["PERGA_INSTALL_DIR"] = other, ["PERGA_DATA_DIR"] = "rel-data" }));
        Assert.Equal(Path.Combine(InstallDir, "Perga.Host"), uninstall["self"]);
        Assert.Equal(other, uninstall["PERGA_INSTALL_DIR"]);
        Assert.Equal(Path.Combine(Root, "rel-data"), uninstall["PERGA_DATA_DIR"]);
    }

    [Theory]
    [InlineData("/bin/zsh", """echo 'export PATH="<bin>:$PATH"' >> ~/.zshrc && source ~/.zshrc""")]
    [InlineData("/bin/bash", """echo 'export PATH="<bin>:$PATH"' >> ~/.bashrc && source ~/.bashrc""")]
    [InlineData("/usr/local/bin/fish", "fish_add_path '<bin>'")]
    [InlineData("/bin/sh", "export PATH=\"<bin>:$PATH\"")]
    public async Task InstallerPrintsShellAwarePathHintWhenBinDirIsNotOnPath(string shell, string expectedHint)
    {
        var package = CreatePackage("v0.1.0");
        var customBin = Path.Combine(Root, "bin-" + Path.GetFileName(shell));
        var install = await InstallAsync(package, new()
        {
            ["PERGA_BIN_DIR"] = customBin,
            ["SHELL"] = shell
        });

        Assert.True(install.Code == 0, install.Error);
        Assert.Contains($"Add {customBin} to your PATH:", install.Output);
        Assert.Contains($"  {expectedHint.Replace("<bin>", customBin, StringComparison.Ordinal)}\n", install.Output);
    }

    [Fact]
    public async Task InstallerPathHintsPreserveShellCharacters()
    {
        var package = CreatePackage("v0.1.0");
        var bin = Path.Combine(Root, "it's \"$TESTVAR\" \\bin");
        foreach (var (shell, rc) in new[] { ("bash", ".bashrc"), ("zsh", ".zshrc"), ("sh", "") })
        {
            var env = BaseEnvironment();
            env["SHELL"] = shell;
            env["PERGA_BIN_DIR"] = bin;
            env["TESTVAR"] = "expanded";
            env["PATH"] = "/usr/bin:/bin";

            var install = await InstallAsync(package, env);
            Assert.True(install.Code == 0, install.Error);
            var hint = Assert.Single(install.Output.Split('\n'), line => line.StartsWith("  ") && line.Contains("export PATH="));
            if (rc.Length > 0)
                Assert.StartsWith("  printf '%s\\n' ", hint);
            var syntax = await RunAsync("bash", ["-n", "-c", hint], env);
            Assert.True(syntax.Code == 0, syntax.Error);

            var apply = await RunAsync(shell == "sh" ? "sh" : "bash", ["-e", "-c", hint + "\nprintf '%s' \"$PATH\""], env);
            Assert.True(apply.Code == 0, apply.Error);
            Assert.Equal($"{bin}:/usr/bin:/bin", apply.Output);
            if (rc.Length > 0)
            {
                var reload = await RunAsync("bash", ["-e", "-c", ". \"$1\"; printf '%s' \"$PATH\"", "test", Path.Combine(Home, rc)], env);
                Assert.True(reload.Code == 0, reload.Error);
                Assert.Equal(apply.Output, reload.Output);
            }
        }

        var fish = await InstallAsync(package, new() { ["SHELL"] = "fish", ["PERGA_BIN_DIR"] = bin });
        Assert.True(fish.Code == 0, fish.Error);
        var fishBin = $"""{Root}/it\'s "$TESTVAR" \\bin""";
        Assert.Contains($"  fish_add_path '{fishBin}'\n", fish.Output);
        Assert.Contains($"  1. Check Node.js 24+ and your provider CLIs: '{fishBin}/perga' doctor\n", fish.Output);
    }

    [Fact]
    public async Task ChecksumMismatchInstallsNothing()
    {
        var package = CreatePackage("v0.1.0");
        await File.WriteAllTextAsync(Path.Combine(package, $"{Package}.tar.gz.sha256"), $"{new string('0', 64)}  {Package}.tar.gz\n");

        var result = await InstallAsync(package);

        Assert.Equal(1, result.Code);
        Assert.Contains("SHA-256 mismatch", result.Error);
        Assert.False(Directory.Exists(InstallDir));
        Assert.False(File.Exists(Launcher));

        await File.WriteAllTextAsync(Path.Combine(package, $"{Package}.tar.gz.sha256"), $"{new string('a', 64)}  other.tar.gz\n");
        Assert.Contains("did not contain a unique entry", (await InstallAsync(package)).Error);
    }

    [Theory]
    [InlineData("control", "must not contain control characters")]
    [InlineData("install-symlink", "must not be a symlink")]
    [InlineData("broken-ancestor", "has a broken symlink")]
    [InlineData("dot-dot-tail", "must not contain '.' or '..'")]
    [InlineData("data-inside", "PERGA_DATA_DIR")]
    [InlineData("db-inside", "PERGA_DB_PATH")]
    [InlineData("default-db-symlink", "database path")]
    [InlineData("non-perga", "is not a Perga installation")]
    [InlineData("launcher-dir", "is a directory")]
    [InlineData("root", "refusing to use '/'")]
    [InlineData("version", "PERGA_VERSION must be")]
    [InlineData("repo", "PERGA_REPO must look like")]
    [InlineData("empty-install-dir", "PERGA_INSTALL_DIR cannot be empty")]
    [InlineData("empty-bin-dir", "PERGA_BIN_DIR cannot be empty")]
    [InlineData("empty-data-dir", "PERGA_DATA_DIR cannot be empty")]
    public async Task InstallerFailsClosedOnUnsafeInputs(string scenario, string message)
    {
        var package = CreatePackage("v0.1.0");
        var custom = Path.Combine(Root, "custom-app");
        var env = new Dictionary<string, string>();
        switch (scenario)
        {
            case "control":
                env["PERGA_INSTALL_DIR"] = custom + "\nevil";
                break;
            case "install-symlink":
                Directory.CreateDirectory(Path.Combine(Root, "target"));
                Directory.CreateSymbolicLink(custom, Path.Combine(Root, "target"));
                env["PERGA_INSTALL_DIR"] = custom;
                break;
            case "broken-ancestor":
                File.CreateSymbolicLink(Path.Combine(Root, "dangling"), Path.Combine(Root, "missing"));
                env["PERGA_INSTALL_DIR"] = Path.Combine(Root, "dangling", "app");
                break;
            case "dot-dot-tail":
                env["PERGA_INSTALL_DIR"] = Path.Combine(Root, "missing", "..", "app");
                break;
            case "data-inside":
                env["PERGA_INSTALL_DIR"] = custom;
                env["PERGA_DATA_DIR"] = Path.Combine(custom, "data");
                break;
            case "db-inside":
                env["PERGA_INSTALL_DIR"] = custom;
                env["PERGA_DB_PATH"] = Path.Combine(custom, "perga.db");
                break;
            case "default-db-symlink":
                Directory.CreateDirectory(DefaultDataDir);
                File.CreateSymbolicLink(Path.Combine(DefaultDataDir, "perga.db"), Path.Combine(InstallDir, "perga.db"));
                break;
            case "non-perga":
                Directory.CreateDirectory(custom);
                await File.WriteAllTextAsync(Path.Combine(custom, "notes.txt"), "keep me");
                env["PERGA_INSTALL_DIR"] = custom;
                break;
            case "launcher-dir":
                Directory.CreateDirectory(Launcher);
                break;
            case "root":
                env["PERGA_INSTALL_DIR"] = "/";
                break;
            case "version":
                env["PERGA_VERSION"] = "v1.2";
                break;
            case "repo":
                env["PERGA_REPO"] = "owner/repo/extra";
                break;
            case "empty-install-dir":
                env["PERGA_INSTALL_DIR"] = "";
                break;
            case "empty-bin-dir":
                env["PERGA_BIN_DIR"] = "";
                break;
            case "empty-data-dir":
                env["PERGA_DATA_DIR"] = "";
                break;
        }

        var result = await InstallAsync(package, env);

        Assert.Equal(1, result.Code);
        Assert.Contains(message, result.Error);
        Assert.False(File.Exists(Launcher));
        Assert.False(Directory.Exists(InstallDir));
        if (scenario == "non-perga")
            Assert.Equal(["notes.txt"], Directory.GetFileSystemEntries(custom).Select(Path.GetFileName));
    }

    [Fact]
    public async Task InstallerRequiresFullLegacyLayoutWithoutMarkerAndProtectsBinInsideInstall()
    {
        var package = CreatePackage("v0.1.0");

        // A directory named Perga.Host (executable/searchable) + web/index.html is not a legacy install.
        var dirHost = Path.Combine(Root, "legacy-dir-host");
        Directory.CreateDirectory(Path.Combine(dirHost, "Perga.Host"));
        Directory.CreateDirectory(Path.Combine(dirHost, "web"));
        await File.WriteAllTextAsync(Path.Combine(dirHost, "web", "index.html"), "<html></html>");
        var dirResult = await InstallAsync(package, new() { ["PERGA_INSTALL_DIR"] = dirHost });
        Assert.Equal(1, dirResult.Code);
        Assert.Contains("is not a Perga installation", dirResult.Error);
        Assert.True(Directory.Exists(Path.Combine(dirHost, "Perga.Host")));

        // Regular Perga.Host + web/index.html without oar-bridge/perga-oar-bridge.mjs is also rejected.
        var missingBridge = Path.Combine(Root, "legacy-missing-bridge");
        WriteExecutable(Path.Combine(missingBridge, "Perga.Host"));
        Directory.CreateDirectory(Path.Combine(missingBridge, "web"));
        await File.WriteAllTextAsync(Path.Combine(missingBridge, "web", "index.html"), "<html></html>");
        var bridgeResult = await InstallAsync(package, new() { ["PERGA_INSTALL_DIR"] = missingBridge });
        Assert.Equal(1, bridgeResult.Code);
        Assert.Contains("is not a Perga installation", bridgeResult.Error);
        Assert.True(File.Exists(Path.Combine(missingBridge, "Perga.Host")));

        // A complete legacy layout without .perga-install-marker upgrades cleanly.
        Directory.CreateDirectory(Path.Combine(missingBridge, "oar-bridge"));
        await File.WriteAllTextAsync(Path.Combine(missingBridge, "oar-bridge", "perga-oar-bridge.mjs"), "export {};\n");
        var legacyUpgrade = await InstallAsync(package, new() { ["PERGA_INSTALL_DIR"] = missingBridge });
        Assert.True(legacyUpgrade.Code == 0, legacyUpgrade.Error);
        Assert.Equal("v0.1.0\n", await File.ReadAllTextAsync(Path.Combine(missingBridge, "VERSION")));

        // PERGA_BIN_DIR inside PERGA_INSTALL_DIR is rejected before touching the existing installation.
        var next = CreatePackage("v0.2.0");
        var binInside = await InstallAsync(next, new()
        {
            ["PERGA_INSTALL_DIR"] = missingBridge,
            ["PERGA_BIN_DIR"] = Path.Combine(missingBridge, "bin")
        });
        Assert.Equal(1, binInside.Code);
        Assert.Contains("PERGA_BIN_DIR", binInside.Error);
        Assert.Equal("v0.1.0\n", await File.ReadAllTextAsync(Path.Combine(missingBridge, "VERSION")));
        Assert.True(File.Exists(Path.Combine(missingBridge, HostCliCommand.InstallMarker)));
    }

    [Fact]
    public async Task UpgradeThroughTheLauncherPreservesDataAndDatabaseAndSwapsAtomically()
    {
        var data = Path.Combine(Root, "my data");
        var db = Path.Combine(Root, "db", "it's.db");
        var first = await InstallAsync(CreatePackage("v0.1.0"), new() { ["PERGA_DATA_DIR"] = data, ["PERGA_DB_PATH"] = db });
        Assert.True(first.Code == 0, first.Error);
        await File.WriteAllTextAsync(Path.Combine(data, "keep.txt"), "user data");
        var inode = Inode(Launcher);

        // The running launcher execs the Host, which re-runs the installer with the launcher's paths.
        var next = CreatePackage("v0.2.0");
        var upgrade = await RunLauncherAsync(["upgrade"], new() { ["PERGA_TEST_INSTALLER"] = Installer, ["PERGA_BASE_URL"] = $"file://{next}" });

        Assert.True(upgrade.Code == 0, upgrade.Error);
        Assert.Contains("Upgraded Perga: v0.1.0 -> v0.2.0", upgrade.Output);
        Assert.Equal("v0.2.0\n", await File.ReadAllTextAsync(Path.Combine(InstallDir, "VERSION")));
        Assert.Equal("user data", await File.ReadAllTextAsync(Path.Combine(data, "keep.txt")));
        Assert.NotEqual(inode, Inode(Launcher));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(InstallDir)!, ".perga-*"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Launcher)!, ".perga-launcher.*"));

        // A direct re-run of the installer without overrides keeps the pinned paths too.
        var rerun = await InstallAsync(CreatePackage("v0.3.0"));
        Assert.True(rerun.Code == 0, rerun.Error);
        Assert.Contains("Upgraded Perga: v0.2.0 -> v0.3.0", rerun.Output);
        var values = Values(await RunLauncherAsync(["version"]));
        Assert.Equal(data, values["PERGA_DATA_DIR"]);
        Assert.Equal(db, values["PERGA_DB_PATH"]);
    }

    [Fact]
    public async Task HostUpgradeCommandDownloadsAndRunsTheInstaller()
    {
        var first = await InstallAsync(CreatePackage("v0.1.0"));
        Assert.True(first.Code == 0, first.Error);
        var next = CreatePackage("v0.2.0");
        var output = new StringWriter();
        var error = new StringWriter();
        var variables = BaseEnvironment();
        variables["PERGA_INSTALL_DIR"] = InstallDir;
        variables["PERGA_BIN_DIR"] = Path.GetDirectoryName(Launcher)!;
        variables["PERGA_DATA_DIR"] = DefaultDataDir;
        variables["PERGA_INSTALLER_URL"] = $"file://{Installer}";
        variables["PERGA_BASE_URL"] = $"file://{next}";

        var code = await HostCliCommand.RunAsync(["upgrade", "v0.2.0"], new HostCliEnvironment
        {
            Variables = variables, BaseDirectory = InstallDir, HomeDirectory = Home,
            Input = TextReader.Null, Output = output, Error = error
        });

        Assert.True(code == 0, error.ToString());
        Assert.Equal("v0.2.0\n", await File.ReadAllTextAsync(Path.Combine(InstallDir, "VERSION")));
        Assert.Contains($"DEFAULT_DATA_DIR='{DefaultDataDir}'", await File.ReadAllTextAsync(Launcher));
    }

    [Fact]
    public async Task InstallerUsesCaseInsensitiveOverlapProtectionWhenConfigured()
    {
        var package = CreatePackage("v0.1.0");
        var install = Path.Combine(Root, "App");
        var lower = Path.Combine(Root, "app");

        var dataOverlap = await InstallAsync(package, new()
        {
            ["PERGA_CASE_INSENSITIVE_PATHS"] = "1",
            ["PERGA_INSTALL_DIR"] = install,
            ["PERGA_DATA_DIR"] = Path.Combine(lower, "data")
        });
        Assert.Equal(1, dataOverlap.Code);
        Assert.Contains("PERGA_DATA_DIR", dataOverlap.Error);
        Assert.False(Directory.Exists(install));
        Assert.False(Directory.Exists(lower));
        Assert.False(File.Exists(Launcher));

        var binOverlap = await InstallAsync(package, new()
        {
            ["PERGA_CASE_INSENSITIVE_PATHS"] = "1",
            ["PERGA_INSTALL_DIR"] = install,
            ["PERGA_BIN_DIR"] = Path.Combine(lower, "bin")
        });
        Assert.Equal(1, binOverlap.Code);
        Assert.Contains("PERGA_BIN_DIR", binOverlap.Error);
        Assert.False(Directory.Exists(install));
        Assert.False(Directory.Exists(lower));
        Assert.False(File.Exists(Launcher));
    }

    [Fact]
    public async Task InstallerAndUpgradeProtectAspNetCoreStorageVariables()
    {
        var first = await InstallAsync(CreatePackage("v0.1.0"));
        Assert.True(first.Code == 0, first.Error);
        var next = CreatePackage("v0.2.0");

        var cases = new (string Name, string Value, string Message)[]
        {
            ("Perga__DbPath", Path.Combine(InstallDir, "custom.db"), "must not be inside PERGA_INSTALL_DIR"),
            ("perga__dbpath", "relative.db", "must be an absolute path"),
            ("Perga__DataDir", Path.Combine(InstallDir, "asp-data"), "must not be inside PERGA_INSTALL_DIR"),
            ("Perga__DataDir", "./relative-data", "must be an absolute path"),
            ("PERGA__DATADIRECTORY", Path.Combine(InstallDir, "legacy-data"), "must not be inside PERGA_INSTALL_DIR")
        };

        foreach (var (name, value, message) in cases)
        {
            var rerun = await InstallAsync(next, new() { [name] = value });
            Assert.Equal(1, rerun.Code);
            Assert.Contains(message, rerun.Error);
            Assert.Equal("v0.1.0\n", await File.ReadAllTextAsync(Path.Combine(InstallDir, "VERSION")));

            var launcherUpgrade = await RunLauncherAsync(["upgrade"], new()
            {
                ["PERGA_TEST_INSTALLER"] = Installer,
                ["PERGA_BASE_URL"] = $"file://{next}",
                [name] = value
            });
            Assert.Equal(1, launcherUpgrade.Code);
            Assert.Contains(message, launcherUpgrade.Error);
            Assert.Equal("v0.1.0\n", await File.ReadAllTextAsync(Path.Combine(InstallDir, "VERSION")));
        }

        // Symlinked perga.db inside an external Perga__DataDir is also rejected before swap.
        var aspData = Path.Combine(Root, "asp-data");
        Directory.CreateDirectory(aspData);
        File.CreateSymbolicLink(Path.Combine(aspData, "perga.db"), Path.Combine(InstallDir, "perga.db"));
        var dbLink = await InstallAsync(next, new() { ["Perga__DataDir"] = aspData });
        Assert.Equal(1, dbLink.Code);
        Assert.Contains("must not be a symlink", dbLink.Error);
        Assert.Equal("v0.1.0\n", await File.ReadAllTextAsync(Path.Combine(InstallDir, "VERSION")));

        // HostCliCommand.UpgradeAsync forwards Perga__* variables to install.sh and fails closed too.
        foreach (var (name, value) in new[]
                 {
                     ("Perga__DbPath", Path.Combine(InstallDir, "custom.db")),
                     ("Perga__DbPath", "relative.db"),
                     ("Perga__DataDir", Path.Combine(InstallDir, "asp-data")),
                     ("Perga__DataDir", "./relative-data")
                 })
        {
            var variables = BaseEnvironment();
            variables["PERGA_INSTALL_DIR"] = InstallDir;
            variables["PERGA_BIN_DIR"] = Path.GetDirectoryName(Launcher)!;
            variables["PERGA_DATA_DIR"] = DefaultDataDir;
            variables["PERGA_INSTALLER_URL"] = $"file://{Installer}";
            variables["PERGA_BASE_URL"] = $"file://{next}";
            variables[name] = value;

            var code = await HostCliCommand.RunAsync(["upgrade", "v0.2.0"], new HostCliEnvironment
            {
                Variables = variables, BaseDirectory = InstallDir, HomeDirectory = Home,
                Input = TextReader.Null, Output = TextWriter.Null, Error = TextWriter.Null
            });
            Assert.Equal(1, code);
            Assert.Equal("v0.1.0\n", await File.ReadAllTextAsync(Path.Combine(InstallDir, "VERSION")));
        }
    }

    [Theory]
    [InlineData("v1.2", "not a strict SemVer tag")]
    [InlineData("1.2.3", "not a strict SemVer tag")]
    [InlineData("v01.2.3", "not a strict SemVer tag")]
    [InlineData("v1.2.3+build", "not a strict SemVer tag")]
    [InlineData("v1.2.3-rc.lock", "not a valid Git tag name")]
    public async Task ReleaseRejectsInvalidTags(string tag, string message)
    {
        var work = CreateReleaseRepo();

        var result = await ReleaseAsync(work, [tag, "--skip-ci-check"]);

        Assert.Equal(1, result.Code);
        Assert.Contains(message, result.Error);
    }

    [Fact]
    public async Task ReleaseRequiresACleanTreeAtOriginMain()
    {
        var work = CreateReleaseRepo();
        await File.WriteAllTextAsync(Path.Combine(work, "untracked.txt"), "ignored");
        await File.WriteAllTextAsync(Path.Combine(work, "README.md"), "changed");
        Assert.Contains("uncommitted changes", (await ReleaseAsync(work, ["v0.1.0", "--skip-ci-check"])).Error);

        await GitAsync(work, "commit", "-qam", "local only");
        Assert.Contains("is not origin/main", (await ReleaseAsync(work, ["v0.1.0", "--skip-ci-check"])).Error);
    }

    [Fact]
    public async Task ReleaseRejectsExistingTagsAndUnreachableRemotes()
    {
        var work = CreateReleaseRepo();
        await GitAsync(work, "tag", "v0.1.0");
        Assert.Contains("already exists locally", (await ReleaseAsync(work, ["v0.1.0", "--skip-ci-check"])).Error);

        var other = Path.Combine(Root, "other-clone");
        await GitAsync(Root, "clone", "-q", Path.Combine(Root, "origin.git"), other);
        await GitAsync(other, "tag", "v0.2.0");
        await GitAsync(other, "push", "-q", "origin", "refs/tags/v0.2.0");
        Assert.Contains("v0.2.0 already exists", (await ReleaseAsync(work, ["v0.2.0", "--skip-ci-check"])).Error);

        // Only ls-remote fails, so the check cannot pass on an unknown remote state.
        var fakeBin = Path.Combine(Root, "fake-git");
        var git = (await RunAsync("bash", ["-c", "command -v git"], BaseEnvironment())).Output.Trim();
        WriteExecutable(Path.Combine(fakeBin, "git"), $"#!/usr/bin/env bash\n[[ \"$1\" == ls-remote ]] && exit 128\nexec '{git}' \"$@\"\n");
        var failed = await ReleaseAsync(work, ["v0.3.0", "--skip-ci-check"], fakeBin);
        Assert.Equal(1, failed.Code);
        Assert.Contains("could not list tags on origin", failed.Error);
    }

    [Fact]
    public async Task ReleaseDryRunChecksCiAndPushOnlyTagsWhenAsked()
    {
        var work = CreateReleaseRepo();
        var head = (await GitAsync(work, "rev-parse", "HEAD")).Trim();
        var fakeBin = Path.Combine(Root, "fake-gh");
        var log = Path.Combine(Root, "gh.log");
        WriteExecutable(Path.Combine(fakeBin, "gh"), $"#!/usr/bin/env bash\nprintf '%s\\n' \"$*\" >> '{log}'\ncat '{Root}/gh-result'\n");

        await File.WriteAllTextAsync(Path.Combine(Root, "gh-result"), "completed:failure\n");
        var failing = await ReleaseAsync(work, ["v0.1.0"], fakeBin);
        Assert.Equal(1, failing.Code);
        Assert.Contains("'completed:failure', not completed:success", failing.Error);
        Assert.Contains($"run list --workflow ci.yml --branch main --event push --commit {head}", await File.ReadAllTextAsync(log));

        await File.WriteAllTextAsync(Path.Combine(Root, "gh-result"), "completed:success\n");
        var dryRun = await ReleaseAsync(work, ["v0.1.0"], fakeBin);
        Assert.True(dryRun.Code == 0, dryRun.Error);
        Assert.Contains($"Dry run passed: v0.1.0 would tag {head}", dryRun.Output);
        Assert.Empty((await GitAsync(work, "tag", "--list")).Trim());

        var push = await ReleaseAsync(work, ["v0.1.0-rc.1", "--push", "--skip-ci-check"]);
        Assert.True(push.Code == 0, push.Error);
        var origin = Path.Combine(Root, "origin.git");
        Assert.Equal("tag", (await GitAsync(origin, "cat-file", "-t", "refs/tags/v0.1.0-rc.1")).Trim());
        Assert.Equal(head, (await GitAsync(origin, "rev-parse", "v0.1.0-rc.1^{commit}")).Trim());

        Assert.Equal(1, (await ReleaseAsync(work, ["v0.1.0", "--bogus"])).Code);
        Assert.Contains("Usage: bash tools/release.sh", (await ReleaseAsync(work, ["--help"])).Output);
    }

    private string CreatePackage(string version)
    {
        var directory = Path.Combine(Root, "pkg-" + version);
        var perga = Path.Combine(directory, "perga");
        WriteExecutable(Path.Combine(perga, "Perga.Host"), StubHost + "\n");
        Directory.CreateDirectory(Path.Combine(perga, "web"));
        File.WriteAllText(Path.Combine(perga, "web", "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(perga, "VERSION"), version + "\n");
        var archive = RunAsync("bash", ["-c", $"tar -czf {Package}.tar.gz perga && (sha256sum {Package}.tar.gz 2>/dev/null || shasum -a 256 {Package}.tar.gz) > {Package}.tar.gz.sha256"],
            BaseEnvironment(), directory).GetAwaiter().GetResult();
        Assert.True(archive.Code == 0, archive.Error);
        return directory;
    }

    private Task<ProcessResult> InstallAsync(string package, Dictionary<string, string>? env = null)
    {
        var variables = BaseEnvironment();
        variables["PERGA_BASE_URL"] = $"file://{package}";
        foreach (var (key, value) in env ?? [])
            variables[key] = value;
        return RunAsync("bash", [Installer], variables, Root);
    }

    private Task<ProcessResult> RunLauncherAsync(string[] args, Dictionary<string, string>? env = null)
    {
        var variables = BaseEnvironment();
        foreach (var (key, value) in env ?? [])
            variables[key] = value;
        return RunAsync(Launcher, args, variables, Root);
    }

    private string CreateReleaseRepo()
    {
        var origin = Path.Combine(Root, "origin.git");
        var work = Path.Combine(Root, "work");
        GitAsync(Root, "init", "-q", "--bare", "-b", "main", origin).GetAwaiter().GetResult();
        GitAsync(Root, "init", "-q", "-b", "main", work).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(work, "README.md"), "release test\n");
        foreach (var args in new[]
                 {
                     new[] { "add", "README.md" }, ["commit", "-qm", "initial"], ["remote", "add", "origin", origin], ["push", "-q", "origin", "main"]
                 })
            GitAsync(work, args).GetAwaiter().GetResult();
        return work;
    }

    private Task<ProcessResult> ReleaseAsync(string work, string[] args, string? prependPath = null)
    {
        var variables = BaseEnvironment();
        if (prependPath is not null)
            variables["PATH"] = prependPath + Path.PathSeparator + variables["PATH"];
        return RunAsync("bash", [ReleaseScript, .. args], variables, work);
    }

    private async Task<string> GitAsync(string directory, params string[] args)
    {
        var result = await RunAsync("git", args, BaseEnvironment(), directory);
        Assert.True(result.Code == 0, $"git {string.Join(' ', args)}: {result.Error}");
        return result.Output;
    }

    private Dictionary<string, string> BaseEnvironment() => new()
    {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
        ["HOME"] = Home,
        ["LC_ALL"] = "C",
        ["TMPDIR"] = Root,
        ["GIT_CONFIG_NOSYSTEM"] = "1",
        ["GIT_AUTHOR_NAME"] = "Perga Test",
        ["GIT_AUTHOR_EMAIL"] = "test@example.invalid",
        ["GIT_COMMITTER_NAME"] = "Perga Test",
        ["GIT_COMMITTER_EMAIL"] = "test@example.invalid"
    };

    private static Dictionary<string, string> Values(ProcessResult result)
    {
        Assert.True(result.Code == 0, result.Error);
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1]);
    }

    private static string Inode(string path) =>
        RunAsync("bash", ["-c", "ls -i \"$1\" | awk '{print $1}'", "inode", path], new() { ["PATH"] = "/usr/bin:/bin" })
            .GetAwaiter().GetResult().Output.Trim();

    private static void WriteExecutable(string path, string content = "#!/bin/sh\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName, IEnumerable<string> args, Dictionary<string, string> env, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? RepoRoot
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var (key, value) in env)
            start.Environment[key] = value;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Perga.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Perga.sln was not found above the test output.");
    }

    private sealed record ProcessResult(int Code, string Output, string Error);
}
