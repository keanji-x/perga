using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Host.Configuration;
using Perga.Host.Execution;
using Perga.Host.IntegrationTests.Fixtures;
using Perga.Host.Serialization;
using Perga.Kernel;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.TermClient;
using Perga.VcsKit;

namespace Perga.Host.IntegrationTests;

public sealed class ExecutionTargetIntegrationTests
{
    [Fact]
    public async Task RemoteMcpTunnel_ReusesRestartsAndDisposesSsh()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new RemoteSshFixture();
        await fixture.WriteSshAsync($$"""
            echo "$*" >> '{{fixture.Root}}/args'
            echo $$ >> '{{fixture.Root}}/pids'
            count=$(wc -l < '{{fixture.Root}}/pids')
            case " $* " in
              *" -o LogLevel=INFO "*) echo "Allocated port $((43209 + count)) for remote forward to 127.0.0.1:4240" >&2 ;;
              *) echo "LogLevel=INFO required" >&2; exit 1 ;;
            esac
            exec sleep 120
            """);
        await using var env = new SshExecutionEnvironment(fixture.Config);
        Assert.Equal("http://127.0.0.1:43210/mcp", await env.ResolveMcpBaseUriAsync("http://0.0.0.0:4240/mcp/"));
        Assert.Equal("http://127.0.0.1:43210/mcp", await env.ResolveMcpBaseUriAsync("http://0.0.0.0:4240/mcp/"));
        var pids = await File.ReadAllLinesAsync(Path.Combine(fixture.Root, "pids"));
        Assert.Single(pids);
        using var first = System.Diagnostics.Process.GetProcessById(int.Parse(pids[0]));
        first.Kill();
        await first.WaitForExitAsync();
        Assert.Equal("http://127.0.0.1:43211/mcp", await env.ResolveMcpBaseUriAsync("http://0.0.0.0:4240/mcp/"));
        pids = await File.ReadAllLinesAsync(Path.Combine(fixture.Root, "pids"));
        Assert.Equal(2, pids.Length);
        using var second = System.Diagnostics.Process.GetProcessById(int.Parse(pids[1]));
        var args = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "args"));
        Assert.Contains("-N -o LogLevel=INFO -o ExitOnForwardFailure=yes -R 127.0.0.1:0:127.0.0.1:4240 --", args);
        Assert.DoesNotContain("ControlMaster=auto", args);
        Assert.Contains("-o ControlMaster=no", args);
        Assert.Contains("-o ControlPath=none", args);
        Assert.Contains("-o ControlPersist=no", args);
        await env.DisposeAsync();
        Assert.True(second.HasExited);
    }

    [Fact]
    public async Task RemoteMcpTunnel_FailureIncludesStderr_AndOverrideSkipsSsh()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new RemoteSshFixture();
        await fixture.WriteSshAsync($$"""
            echo $$ > '{{fixture.Root}}/pid'
            echo 'remote forwarding denied' >&2
            exit 255
            """);
        await using (var env = new SshExecutionEnvironment(fixture.Config))
        {
            var ex = await Assert.ThrowsAsync<KernelException>(() => env.ResolveMcpBaseUriAsync("http://127.0.0.1:4240"));
            Assert.Equal(KernelErrorCode.PreconditionFailed, ex.Code);
            Assert.Contains("remote forwarding denied", ex.Message);
            Assert.Contains("AllowTcpForwarding", ex.Message);
            var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "pid")));
            Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(pid));
        }
        File.Delete(Path.Combine(fixture.Root, "pid"));
        fixture.Options.McpBaseUriOverride = "http://explicit.example:4240/";
        await using var overridden = new SshExecutionEnvironment(new SshTargetConfig("remote", fixture.Options));
        Assert.Equal("http://explicit.example:4240", await overridden.ResolveMcpBaseUriAsync("http://127.0.0.1:4240"));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "pid")));
    }

    [Fact]
    public async Task RemoteMcpTunnel_RejectsHttpsWithoutStartingSsh()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new RemoteSshFixture();
        var marker = Path.Combine(fixture.Root, "started");
        await fixture.WriteSshAsync($"touch {SshCommandBuilder.QuotePosix(marker)}");
        await using var env = new SshExecutionEnvironment(fixture.Config);
        var ex = await Assert.ThrowsAsync<KernelException>(() => env.ResolveMcpBaseUriAsync("https://perga.example:4240"));
        Assert.Equal(KernelErrorCode.PreconditionFailed, ex.Code);
        Assert.Contains("McpBaseUriOverride", ex.Message);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task StageFile_RejectsTruncatedUpload_AndRepairsTruncatedCache()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new RemoteSshFixture();
        var localPath = Path.Combine(fixture.Root, "payload");
        var bytes = Encoding.UTF8.GetBytes("complete payload");
        await File.WriteAllBytesAsync(localPath, bytes);
        await fixture.WriteSshAsync("""
            for arg do last=$arg; done
            case "$last" in
              *'cat >'*) head -c 3 | sh -c "$last" ;;
              *) exec sh -c "$last" ;;
            esac
            """);
        await Assert.ThrowsAsync<IOException>(() => SshCommandBuilder.StageFileAsync(
            fixture.Config, localPath, "payload", ".bin", executable: false));
        var runtime = Path.Combine(fixture.Root, ".perga-runtime");
        Assert.Empty(Directory.GetFiles(runtime));

        await fixture.WriteSshAsync("for arg do last=$arg; done\nexec sh -c \"$last\"");
        var staged = await SshCommandBuilder.StageFileAsync(fixture.Config, localPath, "payload", ".bin", executable: false);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(staged));
        await File.WriteAllBytesAsync(staged, bytes[..3]);
        Assert.Equal(staged, await SshCommandBuilder.StageFileAsync(fixture.Config, localPath, "payload", ".bin", executable: false));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(staged));
        Assert.Empty(Directory.GetFiles(runtime, "*.tmp.*"));
    }

    [Fact]
    public async Task RemoteMcpTunnel_CancellationKillsStartupProcess()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new RemoteSshFixture();
        await fixture.WriteSshAsync($$"""
            echo $$ > '{{fixture.Root}}/pid'
            exec sleep 120
            """);
        await using var env = new SshExecutionEnvironment(fixture.Config);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pending = env.ResolveMcpBaseUriAsync("http://127.0.0.1:4240", ct.Token);
        var pidFile = Path.Combine(fixture.Root, "pid");
        int pid;
        while (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out pid))
            await Task.Delay(10, ct.Token);
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(process.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteTerminalBridge_StagesMatchingArchitecture_RejectsMismatch(bool mismatch)
    {
        if (!OperatingSystem.IsLinux()) return;
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
            _ => null
        };
        if (arch is null) return;
        using var fixture = new RemoteSshFixture();
        var localBin = Path.Combine(fixture.Root, "host-bridge");
        await File.WriteAllTextAsync(localBin, "#!/bin/sh\necho 'staged bridge invoked' >&2\nexit 1\n");
        await fixture.WriteSshAsync($$"""
            for arg do last=$arg; done
            case "$last" in
              *'command -v perga-term-bridge'*) exit 1 ;;
              *'uname -sm'*) echo '{{(mismatch ? "Darwin arm64" : "Linux " + arch)}}'; exit 0 ;;
            esac
            exec sh -c "$last"
            """);
        var config = new SshTargetConfig("remote", fixture.Options, hostTerminalBridgeExecutablePath: localBin);
        await using var client = new SshTerminalBridgeClient(config);
        try
        {
            var ex = await Assert.ThrowsAsync<TerminalBridgeException>(() => client.EnsureAsync(
                new TermEnsureRequest("term", "sh", [], fixture.Root, new Dictionary<string, string>()), CancellationToken.None));
            var runtime = Path.Combine(fixture.Root, ".perga-runtime");
            if (mismatch)
            {
                Assert.Contains("architecture mismatch", ex.Message);
                Assert.Contains("TerminalBridgeExecutablePath", ex.Message);
                Assert.False(Directory.Exists(runtime));
            }
            else
            {
                Assert.Contains("staged bridge invoked", ex.Message);
                var staged = Assert.Single(Directory.GetFiles(runtime, "perga-term-bridge-*"));
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(localBin)))[..12].ToLowerInvariant();
                Assert.Equal("perga-term-bridge-" + hash, Path.GetFileName(staged));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(staged));
                Assert.Equal(await File.ReadAllBytesAsync(localBin), await File.ReadAllBytesAsync(staged));
            }
        }
        finally
        {
            if (Directory.Exists(config.ControlDir)) Directory.Delete(config.ControlDir, true);
        }
    }

    [Fact]
    public async Task RemoteTerminalBridge_UnameFailurePreservesStderr()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new RemoteSshFixture();
        await fixture.WriteSshAsync("""
            for arg do last=$arg; done
            case "$last" in
              *'command -v perga-term-bridge'*) exit 1 ;;
              *'uname -sm'*) echo 'connection lost during uname' >&2; exit 255 ;;
            esac
            """);
        await using var client = new SshTerminalBridgeClient(fixture.Config);
        var ex = await Assert.ThrowsAsync<TerminalBridgeException>(() => client.EnsureAsync(
            new TermEnsureRequest("term", "sh", [], fixture.Root, new Dictionary<string, string>()), CancellationToken.None));
        Assert.Contains("connection lost during uname", ex.Message);
        Assert.DoesNotContain("architecture mismatch", ex.Message);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" /host/bridge ", "/host/bridge")]
    public void SshTargetConfig_NormalizesHostTerminalBridgePath(string? input, string? expected)
    {
        var config = new SshTargetConfig("remote", new ExecutionTargetOptions { User = "test" },
            hostTerminalBridgeExecutablePath: input);
        Assert.Equal(expected, config.HostTerminalBridgeExecutablePath);
    }

    [Fact]
    public async Task SshConnectivityProbe_ReportsMissingPython()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new RemoteSshFixture();
        await fixture.WriteSshAsync($$"""
            echo "$*" > '{{fixture.Root}}/args'
            echo PERGA_MISSING_PYTHON3 >&2
            exit 42
            """);
        var result = await new ProcessSshTargetConnectivityProbe().ProbeAsync(fixture.Config);
        Assert.False(result.Success);
        Assert.Contains("missing 'python3'", result.ErrorMessage);
        Assert.Contains("command -v python3", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "args")));
    }

    private sealed class RemoteSshFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"perga-remote-{Guid.NewGuid():N}");
        public ExecutionTargetOptions Options { get; }
        public SshTargetConfig Config { get; }

        public RemoteSshFixture()
        {
            Directory.CreateDirectory(Root);
            Options = new ExecutionTargetOptions
            {
                Host = "remote.example", User = "test", WorkspacesRoot = Root, SshExecutablePath = Path.Combine(Root, "ssh")
            };
            Config = new SshTargetConfig("remote", Options);
        }

        public async Task WriteSshAsync(string body)
        {
            await File.WriteAllTextAsync(Options.SshExecutablePath!, "#!/bin/sh\n" + body + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Options.SshExecutablePath!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            Directory.Delete(Root, true);
            if (Directory.Exists(Config.ControlDir)) Directory.Delete(Config.ControlDir, true);
        }
    }

    [Theory]
    [InlineData("user@host", "host", 22)]
    [InlineData("user@host:1773", "host", 1773)]
    [InlineData("ssh://user@host:2222", "host", 2222)]
    [InlineData("user@[::1]:2222", "::1", 2222)]
    [InlineData("user@host:0", null, 0)]
    [InlineData("user@host:65536", null, 0)]
    [InlineData("user@host/foo", null, 0)]
    [InlineData("user:pass@host", null, 0)]
    public async Task DynamicSshTarget_ParsesEndpoint(string target, string? host, int port)
    {
        var root = Path.Combine(Path.GetTempPath(), $"perga-endpoint-{Guid.NewGuid():N}");
        try
        {
            var probe = new StubSshProbe();
            await using var factory = new IsolatedPergaHostFactory(root, configureServices: services =>
            {
                services.RemoveAll<ISshTargetConnectivityProbe>();
                services.AddSingleton<ISshTargetConnectivityProbe>(probe);
            });
            using var client = await BrowserAuthentication.CreateAuthenticatedClientAsync(factory);
            var response = await client.PostAsJsonAsync("/api/v1/targets", new { target });
            Assert.Equal(host is null ? HttpStatusCode.BadRequest : HttpStatusCode.Created, response.StatusCode);
            if (host is null) return;
            var config = probe.LastConfig!;
            Assert.Equal(("user", host, port), (config.User, config.Host, config.Port));
            Assert.Equal(config.Label, new SshTargetConfig("configured", new ExecutionTargetOptions { User = "user", Host = host, Port = port }).Label);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    public async Task RawFile_EmptyHeader_ReturnsPreconditionFailed(int exitCode)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"perga-ssh-header-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var ssh = Path.Combine(root, "ssh");
            await File.WriteAllTextAsync(ssh, $"#!/bin/sh\necho 'python3 unavailable' >&2\nexit {exitCode}\n");
            File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var files = new SshExecutionFileSystem(new SshTargetConfig("test", new ExecutionTargetOptions
            {
                User = "test", WorkspacesRoot = root, SshExecutablePath = ssh
            }));
            var error = await Assert.ThrowsAsync<KernelException>(() => files.OpenWorkspaceRawFileAsync(root, "file"));
            Assert.Equal(KernelErrorCode.PreconditionFailed, error.Code);
            Assert.Contains("python3 unavailable", error.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ResolveBridgeScriptPath_ConcurrentInitialStaging_DoesNotCollide()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"perga-ssh-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var ssh = Path.Combine(root, "ssh");
        await File.WriteAllTextAsync(ssh, "#!/bin/sh\nfor arg do last=$arg; done\nexec sh -c \"$last\"\n");
        File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var localBridge = Path.Combine(root, "perga-oar-bridge.mjs");
        var bridgeContent = "console.log('bridge');\n" + new string('x', 32 * 1024);
        await File.WriteAllTextAsync(localBridge, bridgeContent);
        var config = new SshTargetConfig("remote", new ExecutionTargetOptions
        {
            Host = "remote.example", User = "test", WorkspacesRoot = root, SshExecutablePath = ssh
        });
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                Task.Run(() => new SshSubprocessTransport(config).ResolveBridgeScriptPath(localBridge))));
            Assert.Single(results.Distinct());
            Assert.Equal(bridgeContent, await File.ReadAllTextAsync(results[0]));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, ".perga-runtime"), "*.tmp.*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(config.ControlDir)) Directory.Delete(config.ControlDir, recursive: true);
        }
    }

    [Fact]
    public async Task SshAdapters_StreamFiles_AndCleanUpCommandGroups()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), $"perga-ssh-adapters-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var ssh = Path.Combine(root, "ssh");
        var callsFile = Path.Combine(root, "ssh-calls");
        await File.WriteAllTextAsync(ssh,
            $"#!/bin/sh\nprintf 'call\\n' >> {SshCommandBuilder.QuotePosix(callsFile)}\nfor arg do last=$arg; done\nexec sh -c \"$last\"\n");
        File.SetUnixFileMode(ssh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var config = new SshTargetConfig("test", new ExecutionTargetOptions
        {
            User = "test", WorkspacesRoot = root, SshExecutablePath = ssh
        });
        try
        {
            var files = new SshExecutionFileSystem(config);
            var bytes = new byte[2 * 1024 * 1024];
            Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(Path.Combine(root, "data.bin"), bytes);
            var (path, content) = await files.OpenWorkspaceRawFileAsync(root, "data.bin");
            await using (content)
            {
                Assert.Equal(Path.Combine(root, "data.bin"), path);
                Assert.False(content.CanSeek);
                using var received = new MemoryStream();
                await content.CopyToAsync(received);
                Assert.Equal(bytes, received.ToArray());
            }
            var (_, httpContent) = await files.OpenWorkspaceRawFileAsync(root, "data.bin");
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = "GET";
            context.Request.Headers.Range = "bytes=0-4";
            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;
            await Results.File(httpContent, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Equal(bytes, responseBody.ToArray());
            Assert.False(context.Response.Headers.ContainsKey("Content-Range"));
            // Disposing before consuming stdout must also stop the transfer.
            var (_, unread) = await files.OpenWorkspaceRawFileAsync(root, "data.bin");
            await unread.DisposeAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "hello");
            Assert.Equal("hello", (await files.BrowseOrReadWorkspaceFileAsync(root, "hello.txt", 1024)).Content);
            var escaped = await Assert.ThrowsAsync<KernelException>(() => files.OpenWorkspaceRawFileAsync(root, "../outside"));
            Assert.Equal(KernelErrorCode.Forbidden, escaped.Code);
            File.CreateSymbolicLink(Path.Combine(root, "escape"), "/etc/passwd");
            await Assert.ThrowsAsync<KernelException>(() => files.OpenWorkspaceRawFileAsync(root, "escape"));
            await Assert.ThrowsAsync<KernelException>(() => files.OpenWorkspaceRawFileAsync(root, "missing"));
            var dirErr = await Assert.ThrowsAsync<KernelException>(() => files.OpenWorkspaceRawFileAsync(root, "."));
            Assert.Equal(KernelErrorCode.NotFound, dirErr.Code);

            Directory.CreateDirectory(Path.Combine(root, ".git"));
            await File.WriteAllTextAsync(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main");
            var nested = Path.Combine(root, "a", "b");
            Directory.CreateDirectory(nested);
            var ops = new SshWorkspaceFileSystemOps(config);
            Assert.Null(ops.ReadContainedFileBytes(root, Path.Combine(root, "escape"), 1024));
            var fifo = Path.Combine(root, "pipe");
            Assert.Equal(0, (await SshCommandBuilder.RunTextAsync(config,
                $"mkfifo {SshCommandBuilder.QuotePosix(fifo)}")).ExitCode);
            using (var fifoCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
            {
                await Assert.ThrowsAsync<KernelException>(() => files.BrowseOrReadWorkspaceFileAsync(root, "pipe", 1024, fifoCts.Token));
                var rawError = await Assert.ThrowsAsync<KernelException>(() => files.OpenWorkspaceRawFileAsync(root, "pipe", fifoCts.Token));
                Assert.Equal(KernelErrorCode.InvalidArgument, rawError.Code);
            }
            Directory.CreateDirectory(Path.Combine(nested, ".git")); // An empty marker is not a repository.
            var callsBeforeSearch = File.ReadAllLines(callsFile).Length;
            Assert.Equal((root, false, true), ops.FindNearestVcsRoot(nested));
            Assert.Equal(callsBeforeSearch + 1, File.ReadAllLines(callsFile).Length);
            Assert.Null(ops.FindNearestVcsRoot(nested, searchAncestors: false));

            var runner = new SshShellCommandRunner(config);
            Assert.Equal(0, (await runner.RunAsync(new ShellCommandRequest("true", root, TimeSpan.FromSeconds(5)))).ExitCode);
            var pids = Path.Combine(root, ".perga-runtime", "pids");
            Assert.Empty(Directory.GetFiles(pids));
            var transport = new SshSubprocessTransport(config);
            await using (var handle = transport.StartInteractive(new CommandInvocation(
                "sh", ["-c", "read line; printf '%s\\n' \"$line\""], root, new Dictionary<string, string>())))
            {
                await handle.WriteLineAsync("hello", CancellationToken.None);
                Assert.Equal("hello", await handle.ReadStdoutLineAsync(CancellationToken.None));
            }
            Assert.Empty(Directory.GetFiles(pids));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(pids));
            var marker = Path.Combine(root, "orphan");
            var started = Path.Combine(root, "started");
            var result = await runner.RunAsync(new ShellCommandRequest(
                $"touch {SshCommandBuilder.QuotePosix(started)}; (sleep 2; touch {SshCommandBuilder.QuotePosix(marker)}) & wait",
                root, TimeSpan.FromSeconds(1)));
            Assert.True(result.TimedOut);
            Assert.True(File.Exists(started));
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(File.Exists(marker));
            // A zombie group may remain visible until the host init reaps it. Keep its
            // pidfile rather than claiming shutdown while kill -0 still succeeds.
            foreach (var pidFile in Directory.GetFiles(pids))
            {
                Assert.False(await SshCommandBuilder.TryTerminateTrackedProcessAsync(config, pidFile));
                Assert.True(File.Exists(pidFile));
            }
            var stalePid = Path.Combine(pids, "stale.pid");
            await File.WriteAllTextAsync(stalePid, "2147483647");
            Assert.True(await SshCommandBuilder.TryTerminateTrackedProcessAsync(config, stalePid));
            Assert.False(File.Exists(stalePid));
            await File.WriteAllTextAsync(ssh, "#!/bin/sh\nexit 255\n");
            var disconnected = transport.StartInteractive(new CommandInvocation("sh", ["-c", "true"], root, new Dictionary<string, string>()));
            await disconnected.DisposeAsync();
            Assert.False(disconnected.ShutdownConfirmed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(config.ControlDir, recursive: true);
        }
    }

    [Fact]
    public void SshCommandBuilder_AndTargetConfig_EnforceQuotingAllowlistAndValidation()
    {
        Assert.Equal("''", SshCommandBuilder.QuotePosix(""));
        Assert.Equal("'hello world'", SshCommandBuilder.QuotePosix("hello world"));
        Assert.Equal("'a'\\''b$(rm -rf /)'", SshCommandBuilder.QuotePosix("a'b$(rm -rf /)"));

        Assert.Throws<ArgumentException>(() => new SshTargetConfig("local", new ExecutionTargetOptions { User = "jasper" }));
        Assert.Throws<ArgumentException>(() => new SshTargetConfig("jasper", new ExecutionTargetOptions { User = "" }));

        var cfg = new SshTargetConfig("jasper", new ExecutionTargetOptions
        {
            Host = "127.0.0.1",
            Port = 2222,
            User = "jasper",
            IdentityFile = "/home/kenji/.ssh/id_jasper",
            WorkspacesRoot = "/home/jasper/.perga/workspaces"
        });

        Assert.True(cfg.IsLoopbackHost);
        Assert.Equal("jasper@127.0.0.1", cfg.Destination);
        Assert.True(cfg.ControlPath.Length < 100, $"ControlPath too long for Unix socket sun_path: {cfg.ControlPath}");

        var args = SshCommandBuilder.BuildSshExecArguments(cfg, "id");
        Assert.Contains("BatchMode=yes", args);
        Assert.Contains("IdentitiesOnly=yes", args);
        Assert.Contains("-i", args);
        Assert.Contains("/home/kenji/.ssh/id_jasper", args);
        Assert.Contains("-p", args);
        Assert.Contains("2222", args);
        Assert.Contains("jasper@127.0.0.1", args);

        Environment.SetEnvironmentVariable("PERGA_TEST_SECRET_DO_NOT_LEAK", "top-secret-value");
        try
        {
            var psi = SshCommandBuilder.CreateSshStartInfo(cfg, args);
            Assert.False(psi.Environment.ContainsKey("PERGA_TEST_SECRET_DO_NOT_LEAK"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PERGA_TEST_SECRET_DO_NOT_LEAK", null);
        }
    }

    [Fact]
    public async Task AreaBoundExecutionTarget_RoutesFullPipeline_AndNeverFallsBackWhenUnconfigured()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-exec-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var jasperWorkspacesRoot = Path.Combine(tempDir, "jasper-workspaces");
        var jasperRepoDir = Path.Combine(tempDir, "jasper-repo");
        Directory.CreateDirectory(jasperWorkspacesRoot);
        Directory.CreateDirectory(jasperRepoDir);
        await File.WriteAllTextAsync(Path.Combine(jasperRepoDir, "hello.txt"), "hello from jasper");

        var localRuntime = new ScriptableAgentRuntime
        {
            ProviderStatuses =
            [
                new("codex", Available: true, Version: "local-1.0", Reason: null, Models: ["local-gpt"])
            ]
        };
        var jasperRuntime = new ScriptableAgentRuntime
        {
            ProviderStatuses =
            [
                new("claude", Available: true, Version: "jasper-2.0", Reason: null, Models: ["jasper-claude"])
            ]
        };
        var jasperVcs = new RecordingVcsService();
        var jasperTerminals = new RecordingTerminalBridgeClient();
        var jasperFs = new ScopedFakeFileSystem(jasperRepoDir);
        var localForge = new FakeGitHubForgeService();
        var jasperForge = new FakeGitHubForgeService();
        var jasperEnv = new FakeExecutionEnvironment(
            id: "jasper",
            label: "jasper (localhost)",
            workspacesRoot: jasperWorkspacesRoot,
            agentRuntime: jasperRuntime,
            vcs: jasperVcs,
            files: jasperFs,
            terminals: jasperTerminals) { Forge = jasperForge };

        string areaId;
        string trackId;

        try
        {
            await using (var factory = new IsolatedPergaHostFactory(
                tempDir,
                configureServices: services =>
                {
                    services.RemoveAll<IAgentRuntime>();
                    services.AddSingleton<IAgentRuntime>(localRuntime);
                    services.RemoveAll<IGitHubForgeService>();
                    services.AddSingleton<IGitHubForgeService>(localForge);
                    services.RemoveAll<IExecutionEnvironmentRegistry>();
                    services.AddSingleton<IExecutionEnvironmentRegistry>(sp =>
                    {
                        var paths = sp.GetRequiredService<ResolvedHostStoragePaths>();
                        var localEnv = new LocalExecutionEnvironment(
                            workspacesRoot: paths.WorkspacesRootDir,
                            agentRuntime: localRuntime,
                            shell: new ProcessShellCommandRunner(),
                            vcs: sp.GetRequiredService<IVcsService>(),
                            workspaceDrivers: sp.GetRequiredService<IWorkspaceDriverRegistry>(),
                            terminals: sp.GetRequiredService<ITerminalBridgeClient>(),
                            forge: sp.GetService<IGitHubForgeService>());
                        return new ExecutionEnvironmentRegistry([localEnv, jasperEnv]);
                    });
                }))
            {
                using var client = await BrowserAuthentication.CreateAuthenticatedClientAsync(factory);

                // 1. Bootstrap reports both local and jasper execution targets.
                var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/v1/bootstrap", PergaJsonDefaults.ApiOptions);
                var targets = bootstrap.GetProperty("executionTargets").EnumerateArray().ToArray();
                Assert.Contains(targets, t => t.GetProperty("id").GetString() == "local");
                Assert.Contains(targets, t => t.GetProperty("id").GetString() == "jasper" && t.GetProperty("kind").GetString() == "ssh");

                // 2. Reject unknown target IDs (strict whitelist).
                var badAreaResp = await client.PostAsJsonAsync("/api/v1/areas", new
                {
                    name = "Invalid Target Area",
                    executionTargetId = "unconfigured-host"
                });
                Assert.Equal(HttpStatusCode.BadRequest, badAreaResp.StatusCode);

                // 3. Create Area bound to jasper.
                var createAreaResp = await client.PostAsJsonAsync("/api/v1/areas", new
                {
                    name = "Jasper Area",
                    defaultCwd = jasperRepoDir,
                    executionTargetId = "jasper"
                });
                Assert.Equal(HttpStatusCode.Created, createAreaResp.StatusCode);
                var createdArea = await createAreaResp.Content.ReadFromJsonAsync<AreaDto>(PergaJsonDefaults.ApiOptions);
                Assert.NotNull(createdArea);
                Assert.Equal("jasper", createdArea!.ExecutionTargetId);
                areaId = createdArea.Id;

                // 4. Target-scoped directory listing and provider discovery route to jasper.
                var listDir = await client.GetFromJsonAsync<HostListDirResultDto>(
                    $"/api/v1/fs/listdir?targetId=jasper&path={Uri.EscapeDataString(jasperRepoDir)}",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(listDir);
                Assert.Equal(jasperRepoDir, listDir!.Path);
                Assert.Equal(1, jasperFs.ListDirectoryCalls);

                var jasperProviders = await client.GetFromJsonAsync<ProviderStatus[]>(
                    "/api/v1/targets/jasper/providers",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(jasperProviders);
                Assert.Single(jasperProviders!);
                Assert.Equal("claude", jasperProviders[0].Provider);
                Assert.Equal("jasper-2.0", jasperProviders[0].Version);

                // 5. Workspace options for the jasper Area probe jasper's environment.
                var wsOpts = await client.GetFromJsonAsync<TrackWorkspaceOptionsDto>(
                    $"/api/v1/areas/{areaId}/workspace-options?cwd={Uri.EscapeDataString(jasperRepoDir)}",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(wsOpts);
                Assert.Contains(wsOpts!.Options, o => o.Id == "direct");
                Assert.DoesNotContain(wsOpts.Options, o => o.Id == OverlayWorkspaceDriver.Id);

                // 6. Create Track in the jasper Area: inherits executionTargetId = jasper.
                var createTrackResp = await client.PostAsJsonAsync("/api/v1/tracks", new
                {
                    areaId,
                    title = "Jasper Feature",
                    cwd = jasperRepoDir,
                    workspaceOptionId = "direct",
                    provider = "claude"
                });
                Assert.Equal(HttpStatusCode.Created, createTrackResp.StatusCode);
                var snapshot = await createTrackResp.Content.ReadFromJsonAsync<TrackAggregateSnapshot>(PergaJsonDefaults.ApiOptions);
                Assert.NotNull(snapshot);
                Assert.Equal("jasper", snapshot!.Track.ExecutionTargetId);
                trackId = snapshot.Track.Id;

                // 7. Workspace files & raw files route to jasper's IExecutionFileSystem.
                var fileRes = await client.GetFromJsonAsync<WorkspaceFileResultDto>(
                    $"/api/v1/tracks/{trackId}/files?path=hello.txt",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(fileRes);
                Assert.Equal("hello from jasper", fileRes!.Content);
                Assert.True(jasperFs.BrowseCalls > 0);

                var rawResp = await client.GetAsync($"/api/v1/tracks/{trackId}/files/raw?path=hello.txt");
                Assert.Equal(HttpStatusCode.OK, rawResp.StatusCode);
                Assert.Equal("hello from jasper", await rawResp.Content.ReadAsStringAsync());

                // 8. VCS diff routes to jasper's IVcsService.
                var diffRes = await client.GetFromJsonAsync<DiffSummary>(
                    $"/api/v1/tracks/{trackId}/vcs-diff",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(diffRes);
                Assert.Equal("jasper-diff", diffRes!.DiffText);
                Assert.True(jasperVcs.DiffCalls > 0);

                // 9. Lead session turn executes on jasperRuntime, NOT localRuntime.
                var leadSessionId = snapshot.Track.LeadSessionId!;
                var localProbeCount = localRuntime.ProbeCount;
                var jasperProbeCount = jasperRuntime.ProbeCount;
                var cardBootstrap = await client.GetFromJsonAsync<BootstrapDto>(
                    $"/api/v1/bootstrap?card_id={leadSessionId}&refresh=true",
                    PergaJsonDefaults.ApiOptions);
                Assert.Equal("jasper-2.0", Assert.Single(cardBootstrap!.Providers).Version);
                Assert.Equal(localProbeCount, localRuntime.ProbeCount);
                Assert.Equal(jasperProbeCount + 1, jasperRuntime.ProbeCount);

                using var sendReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
                {
                    Content = JsonContent.Create(new { content = "hello jasper agent" })
                };
                sendReq.Headers.Add("Idempotency-Key", "msg-jasper-1");
                var sendResp = await client.SendAsync(sendReq);
                Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);

                var kernel = factory.Services.GetRequiredService<IPergaKernel>();
                await kernel.WaitForTrackIdleAsync(trackId);
                Assert.NotEmpty(jasperRuntime.ExecutedTurns);
                Assert.Empty(localRuntime.ExecutedTurns);

                // 10. Terminal open & WebSocket client resolution route to jasper's ITerminalBridgeClient.
                var termResp = await client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/terminals", new { title = "jasper-sh" });
                Assert.Equal(HttpStatusCode.Created, termResp.StatusCode);
                var termDto = await termResp.Content.ReadFromJsonAsync<TerminalDto>(PergaJsonDefaults.ApiOptions);
                Assert.NotNull(termDto);
                Assert.Equal(1, jasperTerminals.EnsureCalls);
                var resolvedTermClient = await kernel.ResolveTerminalClientAsync(termDto!.Id);
                Assert.Same(jasperTerminals, resolvedTermClient);

                var flowTurnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var finishFlowTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                jasperRuntime.BeforeTurn = async (_, ct) =>
                {
                    flowTurnStarted.TrySetResult();
                    await finishFlowTurn.Task.WaitAsync(ct);
                };
                string flowPath;
                try
                {
                    using var flowMessage = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/sessions/{leadSessionId}/messages")
                    {
                        Content = JsonContent.Create(new { content = "Author the flow on jasper" })
                    };
                    flowMessage.Headers.Add("Idempotency-Key", "msg-jasper-flow");
                    using var flowMessageResponse = await client.SendAsync(flowMessage);
                    flowMessageResponse.EnsureSuccessStatusCode();
                    await flowTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

                    var token = kernel.McpSessionBridge.GetTokenForSession(leadSessionId)!;
                    var principal = kernel.ObserveMcpExecution(kernel.McpSessionBridge.ResolveToken(token)!);
                    var flow = await kernel.InvokeKernelMcpToolAsync(principal, KernelMcpToolNames.FlowRead, JsonSerializer.SerializeToElement(new { }));
                    var rev = flow.GetProperty("flowRev").GetInt64();
                    flowPath = flow.GetProperty("flowPath").GetString()!;
                    Assert.Equal(0, rev);
                    Assert.Empty(flow.GetProperty("spec").GetProperty("nodes").EnumerateArray());
                    Assert.Empty(flow.GetProperty("projection").GetProperty("nodes").EnumerateArray());
                    var original = await File.ReadAllTextAsync(flowPath);
                    const string content = """
                        {"rev":0,"nodes":[
                          {"id":"ci","do":{"kind":"watch","prChecks":42}},
                          {"id":"periodic","when":{"cron":"0 * * * *","tz":"UTC"},"do":{"kind":"watch","prChecks":43}}
                        ]}
                        """;
                    var dryRun = await kernel.InvokeKernelMcpToolAsync(principal, KernelMcpToolNames.FlowCommit,
                        JsonSerializer.SerializeToElement(new { content, expectedFlowRev = rev, dryRun = true }));
                    Assert.True(dryRun.GetProperty("ok").GetBoolean());
                    Assert.Equal(rev, dryRun.GetProperty("flowRev").GetInt64());
                    Assert.Equal(original, await File.ReadAllTextAsync(flowPath));
                    Assert.Empty(jasperForge.CheckedPrs);

                    var args = JsonSerializer.SerializeToElement(new { content, expectedFlowRev = rev });
                    var committed = await kernel.InvokeKernelMcpToolAsync(principal, KernelMcpToolNames.FlowCommit, args);
                    Assert.True(committed.GetProperty("ok").GetBoolean());
                    var stale = await Assert.ThrowsAsync<KernelException>(() =>
                        kernel.InvokeKernelMcpToolAsync(principal, KernelMcpToolNames.FlowCommit, args));
                    Assert.Equal(KernelErrorCode.Conflict, stale.Code);
                    Assert.Contains("Flow revision conflict", stale.Message);
                }
                finally
                {
                    jasperRuntime.BeforeTurn = null;
                    finishFlowTurn.TrySetResult();
                }
                await kernel.WaitForTrackIdleAsync(trackId).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal((jasperRepoDir, 42), Assert.Single(jasperForge.CheckedPrs));
                Assert.Empty(localForge.CheckedPrs);

                var projected = await client.GetFromJsonAsync<FlowSnapshotDto>($"/api/v1/tracks/{trackId}/flow", PergaJsonDefaults.ApiOptions);
                Assert.NotNull(projected);
                Assert.Equal("periodic", Assert.Single(projected.Projection.Nodes).Id);
                Assert.Equal("ci", Assert.Single(projected.Projection.Archived).Id);
                using var canonical = JsonDocument.Parse(await File.ReadAllTextAsync(flowPath));
                Assert.True(JsonElement.DeepEquals(projected.Spec, canonical.RootElement));
                var persisted = (await kernel.GetTrackAggregateAsync(trackId)).Track;
                Assert.Equal(persisted.FlowFileSha256, Perga.Kernel.Flow.TrackFlowFileStore.ComputeSha256Hex(await File.ReadAllTextAsync(flowPath)));

                var updated = $$"""
                    {"rev":{{projected.FlowRev}},"nodes":[
                      {"id":"periodic","title":"Hourly CI","when":{"cron":"0 * * * *","tz":"UTC"},"do":{"kind":"watch","prChecks":43} }
                    ]}
                    """;
                using var httpCommit = await client.PostAsJsonAsync($"/api/v1/tracks/{trackId}/flow/commit",
                    new { expectedFlowRev = projected.FlowRev, content = updated });
                httpCommit.EnsureSuccessStatusCode();
                var httpResult = await httpCommit.Content.ReadFromJsonAsync<FlowCommitResultDto>(PergaJsonDefaults.ApiOptions);
                Assert.NotNull(httpResult);
                Assert.True(httpResult.Ok);
                Assert.Equal(projected.FlowRev + 1, httpResult.FlowRev);
                Assert.Equal("Hourly CI", Assert.Single(httpResult.Flow.Nodes).Title);
            }

            // 11. Restart WITHOUT jasper configured: MUST fail with precondition_failed (409), NEVER silently fall back to local!
            await using (var restartedFactory = new IsolatedPergaHostFactory(
                tempDir,
                configureServices: services =>
                {
                    services.RemoveAll<IAgentRuntime>();
                    services.AddSingleton<IAgentRuntime>(localRuntime);
                }))
            {
                using var restartedClient = await BrowserAuthentication.CreateAuthenticatedClientAsync(restartedFactory);

                var filesWhenMissing = await restartedClient.GetAsync($"/api/v1/tracks/{trackId}/files");
                Assert.Equal(HttpStatusCode.Conflict, filesWhenMissing.StatusCode);
                var filesErr = await filesWhenMissing.Content.ReadFromJsonAsync<JsonElement>(PergaJsonDefaults.ApiOptions);
                Assert.Equal("precondition_failed", filesErr.GetProperty("code").GetString());

                var wsOptsWhenMissing = await restartedClient.GetAsync($"/api/v1/areas/{areaId}/workspace-options");
                Assert.Equal(HttpStatusCode.Conflict, wsOptsWhenMissing.StatusCode);
                var wsErr = await wsOptsWhenMissing.Content.ReadFromJsonAsync<JsonElement>(PergaJsonDefaults.ApiOptions);
                Assert.Equal("precondition_failed", wsErr.GetProperty("code").GetString());
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DynamicSshTarget_CreateTestsConnectivity_PersistsAcrossRestart_AndBlocksDeleteWhileReferenced()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"perga-dyn-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var probe = new StubSshProbe();
            string createdTargetId;

            await using (var factory = new IsolatedPergaHostFactory(
                tempDir,
                configureServices: services =>
                {
                    services.RemoveAll<ISshTargetConnectivityProbe>();
                    services.AddSingleton<ISshTargetConnectivityProbe>(probe);
                }))
            {
                using var client = await BrowserAuthentication.CreateAuthenticatedClientAsync(factory);

                // 1. Failing SSH probe rejects creation and persists nothing.
                probe.NextResult = new SshProbeResult(false, null, "Permission denied (publickey).");
                var failedResp = await client.PostAsJsonAsync("/api/v1/targets", new
                {
                    target = "jasper@127.0.0.1",
                    label = "jasper"
                });
                Assert.Equal(HttpStatusCode.BadRequest, failedResp.StatusCode);
                var failedErr = await failedResp.Content.ReadFromJsonAsync<JsonElement>(PergaJsonDefaults.ApiOptions);
                Assert.Contains("Permission denied", failedErr.GetProperty("error").GetString());

                // 2. Passing SSH probe persists and registers the target immediately.
                probe.NextResult = new SshProbeResult(true, "/home/jasper", null);
                var createResp = await client.PostAsJsonAsync("/api/v1/targets", new
                {
                    target = "jasper@127.0.0.1:2222",
                    label = "Jasper Box"
                });
                Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
                var created = await createResp.Content.ReadFromJsonAsync<ExecutionTargetDto>(PergaJsonDefaults.ApiOptions);
                Assert.NotNull(created);
                Assert.Equal("Jasper Box", created!.Label);
                Assert.Equal("ssh", created.Kind);
                Assert.Equal("jasper@127.0.0.1:2222", created.Destination);
                Assert.True(created.Removable);
                createdTargetId = created.Id;

                var listed = await client.GetFromJsonAsync<List<ExecutionTargetDto>>("/api/v1/targets", PergaJsonDefaults.ApiOptions);
                Assert.NotNull(listed);
                Assert.Contains(listed!, t => t.Id == createdTargetId && t.Label == "Jasper Box");

                // 3. Create an Area bound to the dynamically added target.
                var areaResp = await client.PostAsJsonAsync("/api/v1/areas", new
                {
                    name = "Jasper Dynamic Area",
                    executionTargetId = createdTargetId
                });
                Assert.Equal(HttpStatusCode.Created, areaResp.StatusCode);

                // 4. Cannot delete while referenced by an Area.
                var blockedDelete = await client.DeleteAsync($"/api/v1/targets/{createdTargetId}");
                Assert.Equal(HttpStatusCode.Conflict, blockedDelete.StatusCode);
            }

            // 5. Restart fresh host against the same SQLite DB -> persisted target reloads automatically.
            await using (var restarted = new IsolatedPergaHostFactory(
                tempDir,
                configureServices: services =>
                {
                    services.RemoveAll<ISshTargetConnectivityProbe>();
                    services.AddSingleton<ISshTargetConnectivityProbe>(probe);
                }))
            {
                using var restartedClient = await BrowserAuthentication.CreateAuthenticatedClientAsync(restarted);
                var listedAfterRestart = await restartedClient.GetFromJsonAsync<List<ExecutionTargetDto>>(
                    "/api/v1/targets",
                    PergaJsonDefaults.ApiOptions);
                Assert.NotNull(listedAfterRestart);
                Assert.Contains(listedAfterRestart!, t => t.Id == createdTargetId && t.Label == "Jasper Box" && t.Removable);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class StubSshProbe : ISshTargetConnectivityProbe
    {
        public SshProbeResult NextResult { get; set; } = new(true, "/home/stub", null);

        public SshTargetConfig? LastConfig { get; private set; }
        public Task<SshProbeResult> ProbeAsync(SshTargetConfig config, CancellationToken ct = default)
        {
            LastConfig = config;
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeExecutionEnvironment : IExecutionEnvironment
    {
        public FakeExecutionEnvironment(
            string id,
            string label,
            string workspacesRoot,
            IAgentRuntime agentRuntime,
            IVcsService vcs,
            IExecutionFileSystem files,
            ITerminalBridgeClient terminals)
        {
            Descriptor = new ExecutionTargetDto(id, label, "ssh", IsDefault: false);
            WorkspacesRoot = workspacesRoot;
            AgentRuntime = agentRuntime;
            Shell = new ProcessShellCommandRunner();
            Vcs = vcs;
            Files = files;
            Terminals = terminals;
            WorkspaceDrivers = new WorkspaceDriverRegistry([new DirectWorkspaceDriver()]);
        }

        public ExecutionTargetDto Descriptor { get; }
        public string WorkspacesRoot { get; }
        public bool IsLocal => false;
        public IAgentRuntime AgentRuntime { get; }
        public IShellCommandRunner Shell { get; }
        public IVcsService Vcs { get; }
        public IGitHubForgeService? Forge { get; init; }
        public IWorkspaceDriverRegistry WorkspaceDrivers { get; }
        public IExecutionFileSystem Files { get; }
        public ITerminalBridgeClient Terminals { get; }

        public Task<string> ResolveMcpBaseUriAsync(string hostMcpBaseUri, CancellationToken ct = default) =>
            Task.FromResult(hostMcpBaseUri);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScopedFakeFileSystem(string rootDir) : IExecutionFileSystem
    {
        public int ListDirectoryCalls { get; private set; }
        public int BrowseCalls { get; private set; }

        public Task<HostListDirResultDto> ListDirectoryAsync(string? path, CancellationToken ct = default)
        {
            ListDirectoryCalls++;
            return LocalExecutionFileSystem.Instance.ListDirectoryAsync(string.IsNullOrWhiteSpace(path) ? rootDir : path, ct);
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.DirectoryExistsAsync(path, ct);

        public Task<string> ResolveCanonicalPathAsync(string path, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.ResolveCanonicalPathAsync(path, ct);

        public Task<WorkspaceFileResultDto> BrowseOrReadWorkspaceFileAsync(
            string workspaceRoot, string? relativePath, int maxBytes, CancellationToken ct = default)
        {
            BrowseCalls++;
            return LocalExecutionFileSystem.Instance.BrowseOrReadWorkspaceFileAsync(workspaceRoot, relativePath, maxBytes, ct);
        }

        public Task<(string Path, Stream Content)> OpenWorkspaceRawFileAsync(
            string workspaceRoot, string relativePath, CancellationToken ct = default) =>
            LocalExecutionFileSystem.Instance.OpenWorkspaceRawFileAsync(workspaceRoot, relativePath, ct);
    }

    private sealed class RecordingVcsService : IVcsService
    {
        public int DiffCalls { get; private set; }

        public Task<VcsBackendKind> DetectAsync(string path, CancellationToken ct) =>
            Task.FromResult(VcsBackendKind.PlainDirectory);

        public IReadOnlyList<VcsBackendKind> ListWorkspaceBackends(string path) =>
            [VcsBackendKind.PlainDirectory];

        public Task<IsolatedWorkspace> CreateWorkspaceAsync(string repoPath, string workspaceName, string targetDir, string? baseRev = null, CancellationToken ct = default, VcsBackendKind? backend = null) =>
            Task.FromResult(new IsolatedWorkspace(VcsBackendKind.PlainDirectory, repoPath, workspaceName, targetDir, baseRev));

        public Task RemoveWorkspaceAsync(IsolatedWorkspace workspace, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RevisionSnapshot> SnapshotWorkingCopyAsync(string workspacePath, string? commitMessageIfGit = null, CancellationToken ct = default) =>
            Task.FromResult(new RevisionSnapshot(VcsBackendKind.PlainDirectory, "main", "0000000", null, false, []));

        public Task<DiffSummary> DiffAsync(string workspacePath, string? fromRev = null, string? toRev = null, CancellationToken ct = default)
        {
            DiffCalls++;
            return Task.FromResult(new DiffSummary("jasper-diff", false, ["hello.txt"]));
        }

        public Task<DiffSummary> DiffDetailedAsync(string workspacePath, string? fromRev = null, string? toRev = null, string? filterPath = null, CancellationToken ct = default, bool searchAncestors = true)
        {
            DiffCalls++;
            return Task.FromResult(new DiffSummary("jasper-diff", false, ["hello.txt"]));
        }

        public Task RestoreOperationAsync(string workspacePath, string opId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<RevisionSnapshot> RebaseOntoUpstreamAsync(string workspacePath, string upstreamBranch = "main", CancellationToken ct = default) =>
            SnapshotWorkingCopyAsync(workspacePath, null, ct);

        public Task<PushResult> PushBookmarkAsync(string workspacePath, string bookmarkOrBranch, string? targetRev = null, CancellationToken ct = default) =>
            Task.FromResult(new PushResult(bookmarkOrBranch, "origin", "0000000"));
    }

    private sealed class RecordingTerminalBridgeClient : ITerminalBridgeClient
    {
        public int EnsureCalls { get; private set; }

        public Task<TermEnsureResult> EnsureAsync(TermEnsureRequest req, CancellationToken ct)
        {
            EnsureCalls++;
            return Task.FromResult(new TermEnsureResult(req.TerminalId, Pid: 9001, Created: true));
        }

        public Task<TermObserveResult> ObserveAsync(TermObserveRequest req, CancellationToken ct) =>
            Task.FromResult(new TermObserveResult(
                Revision: 1,
                Frame: new TerminalFrame(80, 24, new TerminalCursor(0, 0, true), false, 0, 0, 0, ["jasper$"]),
                Exited: false,
                ExitCode: null));

        public Task<TermActionResult> SendActionAsync(TermActionRequest req, CancellationToken ct) =>
            Task.FromResult(new TermActionResult(AckRevision: 2));

        public Task ResizeAsync(string terminalId, ushort cols, ushort rows, CancellationToken ct) => Task.CompletedTask;
        public Task KillAsync(string terminalId, int graceMs = 2000, CancellationToken ct = default) => Task.CompletedTask;
        public Uri GetWebSocketRelayUri(string terminalId) => new($"ws://jasper-uds/ws/{terminalId}");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
