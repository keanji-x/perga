using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Perga.Host.Cli;

namespace Perga.Host.IntegrationTests;

public sealed class HostDaemonTests : IDisposable
{
    private const string DynamicUrl = "http://127.0.0.1:0";
    private static readonly string Token = new('a', 64);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("perga daemon test ");
    private string Install => Path.Combine(_root.FullName, "app");
    private string Data => Path.Combine(_root.FullName, "data");
    private string Bin => Path.Combine(_root.FullName, "bin");
    private string StatePath => Path.Combine(Data, "host.state.json");
    private string LogPath => Path.Combine(Data, "logs", "host.log");
    private string TokenPath => Path.Combine(Data, "runtime", "local-launch-token");
    private string BrowserOutput => Path.Combine(_root.FullName, "opened-url");
    private bool _prepared;
    private static bool IsUnix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    [Fact]
    public void StartupSwitchesAreRemovedAndRemainingArgumentsStayInOrder()
    {
        var options = HostDaemonCommand.ForegroundOptions(
            ["start", "--urls", DynamicUrl, "--require-auth", "-f", "--no-open", "--verbose", "--Perga:AuthAllowInsecureHttp=true"]);

        Assert.NotNull(options);
        Assert.True(options.Foreground);
        Assert.True(options.NoOpen);
        Assert.True(options.Verbose);
        Assert.Equal(["--urls", DynamicUrl, "--require-auth", "--Perga:AuthAllowInsecureHttp=true"], options.HostArgs);
        Assert.Equal(["--urls", DynamicUrl, "--Perga:RequireAuth=true", "--Perga:AuthAllowInsecureHttp=true"],
            HostDaemonOptions.ConfigurationArguments(options.HostArgs));
        Assert.NotNull(HostDaemonCommand.ForegroundOptions(["start", "--foreground"]));
        Assert.NotNull(HostDaemonCommand.ForegroundOptions(["serve"]));
        Assert.Null(HostDaemonCommand.ForegroundOptions(["start", "--help", "--foreground"]));
        Assert.Null(HostDaemonCommand.ForegroundOptions(["--urls", DynamicUrl]));
    }

    [Fact]
    public void TokenResolutionUsesOnlyLoopbackUrls()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        File.WriteAllText(TokenPath, Token);

        Assert.Equal("http://192.0.2.10:4041/", HostDaemonBrowser.ResolveBrowserLaunchUrl("http://192.0.2.10:4041", Data));
        Assert.Equal("http://[::1]:4041/auth/local?token=" + Token,
            HostDaemonBrowser.ResolveBrowserLaunchUrl("http://[::1]:4041", Data));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("serve")]
    [InlineData("restart")]
    [InlineData("status")]
    [InlineData("stop")]
    [InlineData("logs")]
    [InlineData("open")]
    public async Task LifecycleHelpHasNoSideEffects(string command)
    {
        var result = await InvokeAsync(command, "--help");
        Assert.Equal(0, result.Code);
        Assert.Contains($"Usage: perga {command}", result.Output);
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public void TokenResolutionRequiresLowercaseHexInARegularNonSymlinkFile()
    {
        const string url = "http://127.0.0.1:4041/";
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        Assert.Equal(url, HostDaemonBrowser.ResolveBrowserLaunchUrl(url, Data));

        foreach (var invalid in new[] { "bad token", new string('A', 64), new string('g', 64), new string('0', 65) })
        {
            File.WriteAllText(TokenPath, invalid);
            Assert.Equal(url, HostDaemonBrowser.ResolveBrowserLaunchUrl(url, Data));
        }

        File.WriteAllText(TokenPath, $"  {Token}\n");
        Assert.Equal(url + "auth/local?token=" + Token, HostDaemonBrowser.ResolveBrowserLaunchUrl(url, Data));

        if (!IsUnix)
            return;
        var real = TokenPath + ".real";
        File.Move(TokenPath, real);
        File.CreateSymbolicLink(TokenPath, real);
        Assert.Equal(url, HostDaemonBrowser.ResolveBrowserLaunchUrl(url, Data));
    }

    [Theory]
    [InlineData("PERGA_NO_OPEN", "1")]
    [InlineData("PERGA_NO_OPEN", "true")]
    [InlineData("SSH_CONNECTION", "ssh")]
    [InlineData("SSH_CLIENT", "ssh")]
    [InlineData("SSH_TTY", "ssh")]
    [InlineData("CI", "")]
    public void BrowserIsSuppressedForHeadlessAndOptOutEnvironments(string name, string value)
    {
        if (!IsUnix)
            return;
        var variables = new Dictionary<string, string> { ["DISPLAY"] = ":0", [name] = value };
        var env = CliEnvironment(variables, TextWriter.Null, TextWriter.Null);
        Assert.False(HostDaemonBrowser.CanOpen(env, noOpen: false));
        Assert.False(HostDaemonBrowser.CanOpen(env, noOpen: false, explicitRequest: true));
    }

    [Fact]
    public void BrowserAutoOpenRequiresDesktopAndTerminalUnlessExplicitlyRequested()
    {
        if (!IsUnix)
            return;
        var env = CliEnvironment(new Dictionary<string, string> { ["DISPLAY"] = ":0" }, TextWriter.Null, TextWriter.Null);
        Assert.True(HostDaemonBrowser.CanOpen(env, noOpen: false));
        Assert.False(HostDaemonBrowser.CanOpen(env, noOpen: true));
        Assert.False(HostDaemonBrowser.CanOpen(env with { IsOutputRedirected = true }, noOpen: false));
        Assert.True(HostDaemonBrowser.CanOpen(env with { IsOutputRedirected = true }, noOpen: false, explicitRequest: true));
        if (OperatingSystem.IsLinux())
        {
            Assert.False(HostDaemonBrowser.CanOpen(env with { Variables = new Dictionary<string, string>() }, noOpen: false));
            Assert.True(HostDaemonBrowser.CanOpen(env with { Variables = new Dictionary<string, string> { ["WAYLAND_DISPLAY"] = "wayland-0" } }, noOpen: false));
        }
    }

    [Fact]
    public async Task DeadPidIsReportedAsStaleAndCleanedUp()
    {
        var state = State(int.MaxValue, DateTime.UtcNow.Ticks, "Perga.Host");
        Directory.CreateDirectory(Data);
        state.Write(StatePath);

        var status = await InvokeAsync("status");
        Assert.Equal(1, status.Code);
        Assert.Contains("stale", status.Output);
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public async Task UninstallPreviewLeavesStaleStateAndDoesNotAcquireTheLifecycleLock()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Path.Combine(Install, HostCliCommand.InstallMarker), "perga-install-v1\n");
        var state = State(int.MaxValue, DateTime.UtcNow.Ticks, "Perga.Host");
        state.Write(StatePathAfterCreatingData());

        var preview = await InvokeAsync("uninstall");

        Assert.Equal(1, preview.Code);
        Assert.Contains("re-run with --yes", preview.Error);
        Assert.DoesNotContain("currently running", preview.Output);
        Assert.Equal(state.Pid, HostDaemonState.Read(StatePath)?.Pid);
        Assert.False(File.Exists(Path.Combine(Data, "host.lifecycle.lock")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopDoesNotSignalAnUnrelatedProcess(bool wrongStartTime)
    {
        if (!IsUnix)
            return;
        using var other = Process.Start("/bin/sleep", "60")!;
        try
        {
            var time = other.StartTime.ToUniversalTime();
            State(other.Id, wrongStartTime ? time.AddSeconds(-3).Ticks : time.Ticks,
                wrongStartTime ? other.ProcessName : "unrelated-process").Write(StatePathAfterCreatingData());

            var stopped = await InvokeAsync("stop");
            Assert.Equal(0, stopped.Code);
            Assert.Contains("Perga is not running.", stopped.Output);
            Assert.False(other.HasExited);
            Assert.False(File.Exists(StatePath));
        }
        finally
        {
            if (!other.HasExited)
                other.Kill();
            await other.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task LogsPrintMissingNoticeAndTail()
    {
        var missing = await InvokeAsync("logs", "--follow");
        Assert.Equal(0, missing.Code);
        Assert.Contains("No Perga log yet", missing.Output);

        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        await File.WriteAllLinesAsync(LogPath, Enumerable.Range(1, 55).Select(i => $"line {i}"));
        var result = await InvokeAsync("logs", "--lines", "2");
        Assert.Equal(0, result.Code);
        Assert.Equal(new[] { "line 54", "line 55" }, Lines(result.Output));
        Assert.Equal(50, Lines((await InvokeAsync("logs")).Output).Length);
        Assert.Equal(2, (await InvokeAsync("logs", "-n", "-1")).Code);
    }

    [Fact]
    public async Task FollowReadsAppendsRotationAndTruncationUntilCanceled()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        await File.WriteAllTextAsync(LogPath, "before tail\n");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = new LineOutput();
        var following = HostCliCommand.RunAsync(["logs", "-f", "-n", "1"],
            CliEnvironment(Variables(), output, TextWriter.Null), timeout.Token);
        try
        {
            Assert.Equal("before tail", await output.ReadAsync(timeout.Token));
            await File.AppendAllTextAsync(LogPath, "appended\n", timeout.Token);
            Assert.Equal("appended", await output.ReadAsync(timeout.Token));
            File.Move(LogPath, LogPath + ".1");
            await File.WriteAllTextAsync(LogPath, "rotated with a much longer first line\n", timeout.Token);
            Assert.Equal("rotated with a much longer first line", await output.ReadAsync(timeout.Token));
            await File.AppendAllTextAsync(LogPath, "after rotation\n", timeout.Token);
            Assert.Equal("after rotation", await output.ReadAsync(timeout.Token));
            await File.WriteAllTextAsync(LogPath, "short\n", timeout.Token);
            Assert.Equal("short", await output.ReadAsync(timeout.Token));
        }
        finally
        {
            await timeout.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => following);
        }
    }

    [Fact]
    public async Task BackgroundLifecycleWaitsForHealthAndReusesThenRestartsWithSavedArguments()
    {
        if (!IsUnix)
            return;
        PrepareInstall();
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        await File.WriteAllTextAsync(TokenPath, Token);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var args = new[] { "start", "--urls", DynamicUrl, "--require-auth", "--no-open", "--verbose", "--Perga:AuthAllowInsecureHttp=true" };
        var starts = await Task.WhenAll(RunProcessAsync(args), RunProcessAsync(args));
        Assert.All(starts, result => Assert.True(result.Code == 0, result.Error));
        var started = Assert.Single(starts, result => result.Output.StartsWith("Perga v0.1.2 is running", StringComparison.Ordinal));
        var alreadyRunning = Assert.Single(starts, result => result.Output.StartsWith("Perga v0.1.2 is already running", StringComparison.Ordinal));
        Assert.Equal(4, Lines(started.Output).Length);
        Assert.Equal(Lines(started.Output)[1..], Lines(alreadyRunning.Output)[1..]);
        Assert.DoesNotContain(Token, started.Output + alreadyRunning.Output);

        var state = Assert.IsType<HostDaemonState>(HostDaemonState.Read(StatePath));
        Assert.Equal(["--urls", DynamicUrl, "--require-auth", "--Perga:AuthAllowInsecureHttp=true"], state.HostArgs);
        Assert.Contains(state.Url, started.Output);
        Assert.False(File.Exists(TokenPath));
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(new Uri(state.Url), "/healthz"))).StatusCode);
        using var tokenRequest = await client.GetAsync(state.Url + "auth/local?token=" + Token);

        var status = await RunProcessAsync(["status"]);
        Assert.Equal(0, status.Code);
        Assert.Contains($"Perga {state.Version} is running", status.Output);
        Assert.Contains(state.Pid.ToString(), status.Output);
        Assert.Contains(Data, status.Output);
        Assert.DoesNotContain(Token, status.Output);
        var logs = await RunProcessAsync(["logs", "-n", "50"]);
        Assert.Equal(0, logs.Code);
        Assert.Contains("Now listening on:", logs.Output);
        Assert.DoesNotContain(Token, logs.Output);
        Assert.DoesNotContain(Token, await File.ReadAllTextAsync(LogPath));

        await CheckExplicitBrowserAsync(state.Url, state.Url);
        var bare = await RunProcessAsync(["start"]);
        Assert.Equal(0, bare.Code);
        Assert.Equal(Lines(alreadyRunning.Output), Lines(bare.Output));
        Assert.DoesNotContain("A different URL was requested", bare.Output);
        var different = await RunProcessAsync(["start", "--urls=http://127.0.0.1:4041", "--no-open"]);
        Assert.Equal(0, different.Code);
        Assert.Contains("perga restart --urls <url>", different.Output);

        var restarted = await RunProcessAsync(["restart", "--urls=http://0.0.0.0:0", "--no-open"]);
        Assert.True(restarted.Code == 0, restarted.Error);
        var newState = Assert.IsType<HostDaemonState>(HostDaemonState.Read(StatePath));
        Assert.NotEqual(state.Pid, newState.Pid);
        Assert.Equal(["--require-auth", "--Perga:AuthAllowInsecureHttp=true", "--urls=http://0.0.0.0:0"], newState.HostArgs);
        Assert.Equal("127.0.0.1", new Uri(newState.Url).Host);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri(new Uri(newState.Url), "/healthz"))).StatusCode);

        var stopped = await RunProcessAsync(["stop"]);
        Assert.True(stopped.Code == 0, stopped.Error);
        Assert.Contains($"Stopped Perga (PID {newState.Pid}).", stopped.Output);
        Assert.False(File.Exists(StatePath));
        Assert.Equal(1, (await RunProcessAsync(["status"])).Code);
        Assert.Equal(0, (await RunProcessAsync(["stop"])).Code);
        var open = await RunProcessAsync(["open"]);
        Assert.Equal(1, open.Code);
        Assert.Contains("Start it with 'perga'.", open.Error);
    }

    [Fact]
    public async Task RestartPreservesInvocationLocalUrlAndDatabasePath()
    {
        if (!IsUnix)
            return;
        PrepareInstall();
        var database = Path.Combine(Data, "custom.db");
        var start = await RunProcessAsync(["start"], new()
        {
            ["ASPNETCORE_URLS"] = DynamicUrl,
            ["PERGA_DB_PATH"] = database
        });
        Assert.True(start.Code == 0, start.Error);
        var old = Assert.IsType<HostDaemonState>(HostDaemonState.Read(StatePath));
        Assert.Empty(old.HostArgs);
        Assert.Equal(database, old.DbPath);
        Assert.True(File.Exists(TokenPath));
        var localToken = (await File.ReadAllTextAsync(TokenPath)).Trim();
        Assert.Equal(64, localToken.Length);
        Assert.DoesNotContain(localToken, start.Output);
        using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) })
        using (var exchange = await client.GetAsync(old.Url + "auth/local?token=" + localToken))
            Assert.Equal(HttpStatusCode.Found, exchange.StatusCode);
        Assert.DoesNotContain(localToken, await File.ReadAllTextAsync(LogPath));
        await CheckExplicitBrowserAsync(old.Url, old.Url + "auth/local?token=" + localToken);

        var restart = await RunProcessAsync(["restart"]);
        Assert.True(restart.Code == 0, restart.Error);
        var current = Assert.IsType<HostDaemonState>(HostDaemonState.Read(StatePath));
        Assert.NotEqual(old.Pid, current.Pid);
        Assert.Equal(old.Url, current.Url);
        Assert.Equal(database, current.DbPath);
        Assert.False(File.Exists(Path.Combine(Data, "perga.db")));
        var restartedToken = (await File.ReadAllTextAsync(TokenPath)).Trim();
        Assert.Equal(64, restartedToken.Length);
        Assert.NotEqual(localToken, restartedToken);
    }

    [Fact]
    public async Task BackgroundEarlyExitPrintsOnlyNewLogTail()
    {
        if (!IsUnix)
            return;
        PrepareInstall();
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        await File.WriteAllTextAsync(LogPath, "old log marker\n");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

        var start = await RunProcessAsync(["start", "--urls", url]);
        Assert.Equal(1, start.Code);
        Assert.Empty(start.Output);
        Assert.Contains("already in use", start.Error);
        Assert.Contains("Perga failed to start (exit code 1)", start.Error);
        Assert.Contains(LogPath, start.Error);
        Assert.DoesNotContain("old log marker", start.Error);
        Assert.False(File.Exists(StatePath));
    }

    [Theory]
    [InlineData("direct", false)]
    [InlineData("serve", false)]
    [InlineData("serve", true)]
    [InlineData("start", false)]
    public async Task DirectHostKeepsForegroundAndDefaultLogsWhileExplicitForegroundHasReadyBanner(string mode, bool verbose)
    {
        if (!IsUnix)
            return;
        PrepareInstall();
        string[] hostArgs = ["--urls", DynamicUrl];
        string[] args = mode switch
        {
            "direct" => hostArgs,
            "start" => ["start", "--foreground", "--no-open", .. hostArgs],
            _ when verbose => ["serve", "--verbose", "--no-open", .. hostArgs],
            _ => ["serve", "--no-open", .. hostArgs]
        };

        using var process = StartProcess(args);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var lines = new List<string>();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                lines.Add(line);
                if (line.Contains(mode == "direct" ? "Now listening on:" : "Press Ctrl+C to stop.", StringComparison.Ordinal))
                    break;
            }
            Assert.False(process.HasExited);
            Assert.False(File.Exists(StatePath));
            if (mode == "direct")
            {
                Assert.Contains(lines, line => line.Contains("Now listening on:", StringComparison.Ordinal));
                Assert.DoesNotContain(lines, line => line.Contains("Perga v0.1.2 is running", StringComparison.Ordinal));
            }
            else
            {
                Assert.Contains("Perga v0.1.2 is running", lines);
                Assert.Equal(verbose, lines.Any(line => line.Contains("Now listening on:", StringComparison.Ordinal)));
                Assert.Contains(lines, line => line.Contains("Press Ctrl+C to stop.", StringComparison.Ordinal));
            }
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task UpgradeRestartsWithNewVersionAndUninstallStopsTheDaemonBeforeRemovingPackage()
    {
        if (!IsUnix)
            return;
        PrepareInstall();
        var database = Path.Combine(Data, "upgrade.db");
        var start = await RunProcessAsync(["start", "--require-auth"], new()
        {
            ["ASPNETCORE_URLS"] = DynamicUrl,
            ["PERGA_DB_PATH"] = database
        });
        Assert.True(start.Code == 0, start.Error);
        var old = Assert.IsType<HostDaemonState>(HostDaemonState.Read(StatePath));

        var installer = Path.Combine(_root.FullName, "installer");
        await File.WriteAllTextAsync(installer, """
            set -eu
            printf 'v0.1.3\n' > "$PERGA_INSTALL_DIR/VERSION"
            """);
        var upgrade = await RunProcessAsync(["upgrade"], new() { ["PERGA_INSTALLER_URL"] = new Uri(installer).AbsoluteUri });
        Assert.True(upgrade.Code == 0, upgrade.Error);
        var updated = Assert.IsType<HostDaemonState>(HostDaemonState.Read(StatePath));
        Assert.Equal("v0.1.3", updated.Version);
        Assert.NotEqual(old.Pid, updated.Pid);
        Assert.Equal(old.Url, updated.Url);
        Assert.Equal(database, updated.DbPath);
        Assert.Equal(["--urls", old.Url.TrimEnd('/'), "--require-auth"], updated.HostArgs);

        using var input = new StringReader("n\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var interactive = CliEnvironment(Variables(), output, error) with { IsInteractive = true, Input = input };
        Assert.Equal(1, await HostCliCommand.RunAsync(["uninstall"], interactive));
        Assert.Contains("Stop Perga and uninstall? [y/N] ", output.ToString());
        Assert.DoesNotContain("Stopping Perga", output.ToString());
        Assert.Equal(updated.Pid, HostDaemonState.Read(StatePath)?.Pid);

        var uninstall = await RunProcessAsync(["uninstall", "--yes"]);
        Assert.True(uninstall.Code == 0, uninstall.Error);
        Assert.StartsWith(string.Join(Environment.NewLine,
        [
            $"Perga {updated.Version} is currently running (PID {updated.Pid}, {updated.Url}).",
            "Uninstalling will stop this background instance first.",
            "",
            "This removes:"
        ]), uninstall.Output);
        Assert.Contains($"Stopping Perga (PID {updated.Pid})...{Environment.NewLine}Stopped Perga (PID {updated.Pid}).{Environment.NewLine}Removed Perga", uninstall.Output);
        Assert.False(Directory.Exists(Install));
        Assert.False(File.Exists(StatePath));
        Assert.True(File.Exists(updated.DbPath));
    }

    [Theory]
    [InlineData("upgrade", "DataDir")]
    [InlineData("upgrade", "DbPath")]
    [InlineData("uninstall", "DataDir")]
    [InlineData("uninstall", "DbPath")]
    public async Task PackageChangesRefuseToRemoveTheRunningDaemonsStorage(string command, string storageSetting)
    {
        if (!IsUnix)
            return;
        PrepareInstall();
        var path = Path.Combine(Install, storageSetting == "DataDir" ? "storage" : "perga.db");
        var start = await RunProcessAsync(["start", "--urls", DynamicUrl, $"--Perga:{storageSetting}={path}"]);
        Assert.True(start.Code == 0, start.Error);
        var daemon = Assert.IsType<HostDaemonState>(HostDaemonState.Read(StatePath));

        var installer = Path.Combine(_root.FullName, "installer");
        await File.WriteAllTextAsync(installer, """printf 'unexpected\n' > "$PERGA_INSTALL_DIR/VERSION" """);
        var result = command == "upgrade"
            ? await RunProcessAsync(["upgrade"], new() { ["PERGA_INSTALLER_URL"] = new Uri(installer).AbsoluteUri })
            : await RunProcessAsync(["uninstall"]);

        Assert.Equal(1, result.Code);
        Assert.Contains("is inside the install directory", result.Error);
        Assert.DoesNotContain("This removes:", result.Output);
        Assert.Equal("v0.1.2\n", await File.ReadAllTextAsync(Path.Combine(Install, "VERSION")));
        Assert.True(File.Exists(daemon.DbPath));
        Assert.Equal(daemon.Pid, HostDaemonState.Read(StatePath)?.Pid);
    }

    [Fact]
    public async Task InteractiveUninstallRefusesToStopADaemonThatStartedAfterThePreview()
    {
        if (!IsUnix)
            return;
        PrepareInstall();
        var launcher = Path.Combine(Bin, "perga");
        await File.WriteAllTextAsync(launcher, $"DEFAULT_INSTALL_DIR='{HostCliCommand.PhysicalPath(Install)}'\n");
        var confirmation = new Pipe();
        using var input = new StreamReader(confirmation.Reader.AsStream());
        await using var answer = new StreamWriter(confirmation.Writer.AsStream()) { AutoFlush = true };
        using var output = new StringWriter();
        using var error = new StringWriter();
        var interactive = CliEnvironment(Variables(), output, error) with { IsInteractive = true, Input = input };

        var uninstall = HostCliCommand.RunAsync(["uninstall"], interactive);
        Assert.Contains("Continue? [y/N] ", output.ToString());

        var start = await RunProcessAsync(["start", "--urls", DynamicUrl, "--no-open"]);
        Assert.True(start.Code == 0, start.Error);
        var state = Assert.IsType<HostDaemonState>(HostDaemonState.Read(StatePath));
        await answer.WriteLineAsync("y");

        Assert.Equal(1, await uninstall);
        Assert.Contains($"error: Perga (PID {state.Pid}) started after confirmation.", error.ToString());
        Assert.Contains("hint:  Run 'perga stop' or re-run 'perga uninstall'.", error.ToString());
        Assert.DoesNotContain("Stopping Perga", output.ToString());
        Assert.True(File.Exists(Path.Combine(Install, "Perga.Host")));
        Assert.True(File.Exists(launcher));
        Assert.Equal(state.Pid, HostDaemonState.Read(StatePath)?.Pid);
        using var daemon = state.GetOwnedProcess();
        Assert.NotNull(daemon);
    }

    [Fact]
    public async Task UninstallReportsStopTimeoutAndKeepsTheInstallation()
    {
        if (!IsUnix)
            return;
        PrepareInstall();
        var launcher = Path.Combine(Bin, "perga");
        await File.WriteAllTextAsync(launcher, $"DEFAULT_INSTALL_DIR='{HostCliCommand.PhysicalPath(Install)}'\n");
        var start = new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "trap '' TERM; printf 'ready\\n'; read -r line" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        using var daemon = Process.Start(start)!;
        try
        {
            Assert.Equal("ready", await daemon.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            State(daemon.Id, daemon.StartTime.ToUniversalTime().Ticks, daemon.ProcessName).Write(StatePathAfterCreatingData());

            var result = await RunProcessAsync(["uninstall", "--yes"]);

            Assert.Equal(1, result.Code);
            Assert.Contains($"Stopping Perga (PID {daemon.Id})...", result.Output);
            Assert.DoesNotContain("Stopped Perga", result.Output);
            Assert.Contains($"error: Perga (PID {daemon.Id}) did not stop within 10 seconds.", result.Error);
            Assert.Contains($"hint:  Stop it first with 'perga stop' (or 'kill {daemon.Id}'), then re-run 'perga uninstall'.", result.Error);
            Assert.True(File.Exists(Path.Combine(Install, "Perga.Host")));
            Assert.True(File.Exists(launcher));
            Assert.Equal(daemon.Id, HostDaemonState.Read(StatePath)?.Pid);
        }
        finally
        {
            daemon.StandardInput.Close();
            await daemon.WaitForExitAsync();
        }
    }

    private async Task CheckExplicitBrowserAsync(string url, string expectedOpenedUrl)
    {
        var launcher = Path.Combine(Bin, OperatingSystem.IsMacOS() ? "open" : "xdg-open");
        await File.WriteAllTextAsync(launcher, """
            #!/bin/sh
            test -z "${PERGA_BROWSER_TEST_SECRET:-}" || exit 1
            printf '%s\n' "$1" > "$HOME/opened-url"
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var desktop = await RunProcessAsync(["open"], new() { ["DISPLAY"] = ":0", ["PERGA_BROWSER_TEST_SECRET"] = "sensitive" });
        Assert.Equal(0, desktop.Code);
        Assert.Equal(url, desktop.Output.Trim());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(BrowserOutput))
            await Task.Delay(25, timeout.Token);
        Assert.Equal(expectedOpenedUrl, (await File.ReadAllTextAsync(BrowserOutput, timeout.Token)).Trim());

        File.Delete(BrowserOutput);
        var ssh = await RunProcessAsync(["open"], new() { ["DISPLAY"] = ":0", ["SSH_CONNECTION"] = "1" });
        Assert.Equal(0, ssh.Code);
        Assert.Equal($"Open {url} in your browser.", ssh.Output.Trim());
        Assert.False(File.Exists(BrowserOutput));
    }

    private string StatePathAfterCreatingData()
    {
        Directory.CreateDirectory(Data);
        return StatePath;
    }

    private HostDaemonState State(int pid, long time, string name) =>
        new(pid, time, name, DynamicUrl, "v0.1.2", Install, Data, Path.Combine(Data, "perga.db"), LogPath, []);

    private void PrepareInstall()
    {
        Directory.CreateDirectory(Install);
        Directory.CreateDirectory(Bin);
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            var file = Path.GetFileName(path);
            if (file is "Perga.Host" or "Perga.Host.dll")
                File.Copy(path, Path.Combine(Install, file));
            else if (file.EndsWith(".dll", StringComparison.Ordinal) ||
                     file is "Perga.Host.deps.json" or "Perga.Host.runtimeconfig.json")
                File.CreateSymbolicLink(Path.Combine(Install, file), path);
        }
        var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (Directory.Exists(runtimes))
            Directory.CreateSymbolicLink(Path.Combine(Install, "runtimes"), runtimes);
        File.WriteAllText(Path.Combine(Install, "VERSION"), "v0.1.2\n");
        File.WriteAllText(Path.Combine(Install, HostCliCommand.InstallMarker), "perga-install-v1\n");
        _prepared = true;
    }

    private Dictionary<string, string> Variables() => new()
    {
        ["PERGA_INSTALL_DIR"] = Install,
        ["PERGA_DATA_DIR"] = Data,
        ["PERGA_BIN_DIR"] = Bin,
        ["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..")),
        ["HOME"] = _root.FullName,
        ["PATH"] = Bin + Path.PathSeparator + "/usr/bin:/bin"
    };

    private HostCliEnvironment CliEnvironment(IReadOnlyDictionary<string, string> variables, TextWriter output, TextWriter error) => new()
    {
        BaseDirectory = Install,
        HomeDirectory = _root.FullName,
        Variables = variables,
        Input = TextReader.Null,
        Output = output,
        Error = error
    };

    private async Task<Result> InvokeAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await HostCliCommand.RunAsync(args, CliEnvironment(Variables(), output, error));
        return new Result(code, output.ToString(), error.ToString());
    }

    private Process StartProcess(string[] args, Dictionary<string, string>? variables = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Install, "Perga.Host"))
        {
            WorkingDirectory = Install,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        start.Environment.Clear();
        foreach (var (key, value) in Variables())
            start.Environment[key] = value;
        if (variables is not null)
        {
            foreach (var (key, value) in variables)
                start.Environment[key] = value;
        }
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private async Task<Result> RunProcessAsync(string[] args, Dictionary<string, string>? variables = null)
    {
        using var process = StartProcess(args, variables);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new Result(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
        }
    }

    private static string[] Lines(string output) => output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    public void Dispose()
    {
        if (_prepared && File.Exists(StatePath) && File.Exists(Path.Combine(Install, "Perga.Host")))
        {
            using var process = StartProcess(["stop"]);
            if (!process.WaitForExit(11_000))
                process.Kill();
        }
        _root.Delete(recursive: true);
    }

    private sealed record Result(int Code, string Output, string Error);

    private sealed class LineOutput : TextWriter
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override Task WriteLineAsync(string? value)
        {
            _lines.Writer.TryWrite(value ?? "");
            return Task.CompletedTask;
        }
        public ValueTask<string> ReadAsync(CancellationToken ct) => _lines.Reader.ReadAsync(ct);
    }
}
