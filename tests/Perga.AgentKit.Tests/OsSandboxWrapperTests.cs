using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class OsSandboxWrapperTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Wrap_NoneMode_ReturnsDirectCommandWithNormalizedCwd()
    {
        var wrapper = new OsSandboxWrapper();
        var env = new Dictionary<string, string> { ["HOME"] = "/home/tester" };
        var inner = new CommandInvocation("codex", ["app-server"], _fixture.WorkspaceDir, env);

        var wrapped = wrapper.Wrap(inner, OsSandboxMode.None);

        Assert.Equal("codex", wrapped.FileName);
        Assert.Equal(["app-server"], wrapped.Arguments);
        Assert.Equal(Path.GetFullPath(_fixture.WorkspaceDir), wrapped.WorkingDirectory);
    }

    [Fact]
    public void Wrap_LinuxWorkspaceWrite_ConstructsDeterministicBwrapArgs()
    {
        var wrapper = new OsSandboxWrapper(new OsSandboxOptions
        {
            PlatformOverride = OSPlatform.Linux,
            BwrapExecutablePath = "/usr/bin/bwrap",
            HomeDirectoryOverride = "/home/tester",
            BwrapVersionOverride = new Version(0, 8, 0)
        });

        var inner = new CommandInvocation(
            "codex",
            ["app-server"],
            _fixture.WorkspaceDir,
            new Dictionary<string, string>());

        var wrapped = wrapper.Wrap(
            inner,
            OsSandboxMode.WorkspaceWriteBwrap,
            auxiliaryReadOnlyFiles: ["/tmp/perga-aux-mcp.json"]);

        Assert.Equal("/usr/bin/bwrap", wrapped.FileName);
        var args = wrapped.Arguments;

        Assert.Equal("--die-with-parent", args[0]);
        AssertContainsPair(args, "--ro-bind", "/usr", "/usr");
        AssertContainsPair(args, "--ro-bind", "/bin", "/bin");
        AssertContainsPair(args, "--ro-bind", "/lib", "/lib");
        AssertContainsPair(args, "--ro-bind-try", "/lib64", "/lib64");
        AssertContainsPair(args, "--ro-bind", "/etc", "/etc");
        AssertContainsFlagValue(args, "--proc", "/proc");
        AssertContainsFlagValue(args, "--dev", "/dev");
        AssertContainsFlagValue(args, "--tmpfs", "/tmp");
        AssertContainsPair(args, "--ro-bind-try", "/home/tester/.dotnet", "/home/tester/.dotnet");
        AssertContainsPair(args, "--ro-bind-try", "/home/tester/.local", "/home/tester/.local");

        var tmpfsIdx = IndexOfSequence(args, "--tmpfs", "/tmp");
        var cwdBindIdx = IndexOfSequence(args, "--bind", _fixture.WorkspaceDir, _fixture.WorkspaceDir);
        var auxBindIdx = IndexOfSequence(args, "--ro-bind", "/tmp/perga-aux-mcp.json", "/tmp/perga-aux-mcp.json");
        Assert.True(tmpfsIdx >= 0);
        Assert.True(cwdBindIdx > tmpfsIdx, "Cwd bind must appear after --tmpfs /tmp so /tmp workspaces remain visible.");
        Assert.True(auxBindIdx > tmpfsIdx, "Auxiliary read-only file bind must appear after --tmpfs /tmp.");

        var sepIdx = IndexOfSingle(args, "--");
        Assert.True(sepIdx > cwdBindIdx);
        Assert.Equal(["codex", "app-server"], args.Skip(sepIdx + 1).ToArray());
    }

    [Fact]
    public void Wrap_LinuxReadOnly_MountsCwdReadOnly()
    {
        var wrapper = new OsSandboxWrapper(new OsSandboxOptions
        {
            PlatformOverride = OSPlatform.Linux,
            HomeDirectoryOverride = "/home/tester",
            BwrapVersionOverride = new Version(0, 8, 0)
        });

        var inner = new CommandInvocation("claude", ["--print"], _fixture.WorkspaceDir, new Dictionary<string, string>());
        var wrapped = wrapper.Wrap(inner, OsSandboxMode.ReadOnlyBwrap);

        Assert.True(IndexOfSequence(wrapped.Arguments, "--ro-bind", _fixture.WorkspaceDir, _fixture.WorkspaceDir) >= 0);
        Assert.Equal(-1, IndexOfSequence(wrapped.Arguments, "--bind", _fixture.WorkspaceDir, _fixture.WorkspaceDir));
    }

    [Fact]
    public void Wrap_OverlayMounts_UsesNativeOverlayOnBwrap09AndCleanFallbackOn08()
    {
        var overlays = new[]
        {
            new OverlayMountSpec(
                LowerDirs: ["/cache/lower1", "/cache/lower2"],
                UpperDir: "/cache/upper",
                WorkDir: "/cache/work",
                TargetPath: "/workspace/merged")
        };

        var inner = new CommandInvocation("codex", ["app-server"], _fixture.WorkspaceDir, new Dictionary<string, string>());

        var modernWrapper = new OsSandboxWrapper(new OsSandboxOptions
        {
            PlatformOverride = OSPlatform.Linux,
            BwrapVersionOverride = new Version(0, 9, 0)
        });

        var rwModern = modernWrapper.Wrap(inner, OsSandboxMode.WorkspaceWriteBwrap, overlays);
        Assert.True(IndexOfSequence(rwModern.Arguments, "--overlay-src", "/cache/lower1") >= 0);
        Assert.True(IndexOfSequence(rwModern.Arguments, "--overlay-src", "/cache/lower2") >= 0);
        Assert.True(IndexOfSequence(rwModern.Arguments, "--overlay", "/cache/upper", "/cache/work", "/workspace/merged") >= 0);

        var roModern = modernWrapper.Wrap(inner, OsSandboxMode.ReadOnlyBwrap, overlays);
        Assert.True(IndexOfSequence(roModern.Arguments, "--ro-overlay", "/workspace/merged") >= 0);

        var legacyWrapper = new OsSandboxWrapper(new OsSandboxOptions
        {
            PlatformOverride = OSPlatform.Linux,
            BwrapVersionOverride = new Version(0, 8, 0)
        });

        var rwLegacy = legacyWrapper.Wrap(inner, OsSandboxMode.WorkspaceWriteBwrap, overlays);
        Assert.True(IndexOfSequence(rwLegacy.Arguments, "--bind", "/cache/upper", "/workspace/merged") >= 0);

        var strictLegacyWrapper = new OsSandboxWrapper(new OsSandboxOptions
        {
            PlatformOverride = OSPlatform.Linux,
            BwrapVersionOverride = new Version(0, 8, 0),
            RequireNativeOverlaySupport = true
        });

        Assert.Throws<NotSupportedException>(() =>
            strictLegacyWrapper.Wrap(inner, OsSandboxMode.WorkspaceWriteBwrap, overlays));
    }

    [Fact]
    public void Wrap_InvalidOverlayMountSpec_ThrowsArgumentException()
    {
        var wrapper = new OsSandboxWrapper(new OsSandboxOptions
        {
            PlatformOverride = OSPlatform.Linux,
            BwrapVersionOverride = new Version(0, 9, 0)
        });

        var inner = new CommandInvocation("codex", ["app-server"], _fixture.WorkspaceDir, new Dictionary<string, string>());

        var missingLower = new[] { new OverlayMountSpec([], "/u", "/w", "/target") };
        Assert.Throws<ArgumentException>(() => wrapper.Wrap(inner, OsSandboxMode.WorkspaceWriteBwrap, missingLower));

        var missingWorkDir = new[] { new OverlayMountSpec(["/l"], "/u", null, "/target") };
        Assert.Throws<ArgumentException>(() => wrapper.Wrap(inner, OsSandboxMode.WorkspaceWriteBwrap, missingWorkDir));
    }

    [Fact]
    public void Wrap_MacOsSandboxExec_GeneratesSeatbeltProfiles()
    {
        var wrapper = new OsSandboxWrapper(new OsSandboxOptions
        {
            PlatformOverride = OSPlatform.OSX,
            MacSandboxExecPath = "/usr/bin/sandbox-exec"
        });

        var inner = new CommandInvocation("codex", ["app-server"], _fixture.WorkspaceDir, new Dictionary<string, string>());

        var rwWrapped = wrapper.Wrap(inner, OsSandboxMode.WorkspaceWriteBwrap);
        Assert.Equal("/usr/bin/sandbox-exec", rwWrapped.FileName);
        Assert.Equal("-p", rwWrapped.Arguments[0]);
        Assert.Contains("(deny file-write*)", rwWrapped.Arguments[1], StringComparison.Ordinal);
        Assert.Contains(_fixture.WorkspaceDir, rwWrapped.Arguments[1], StringComparison.Ordinal);
        Assert.Equal(["codex", "app-server"], rwWrapped.Arguments.Skip(2).ToArray());

        var roWrapped = wrapper.Wrap(inner, OsSandboxMode.ReadOnlyBwrap);
        Assert.DoesNotContain(_fixture.WorkspaceDir, roWrapped.Arguments[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseBwrapVersion_ParsesHostAndModernVersions()
    {
        Assert.Equal(new Version(0, 8, 0), OsSandboxWrapper.TryParseBwrapVersion("bubblewrap 0.8.0\n"));
        Assert.Equal(new Version(0, 10, 0), OsSandboxWrapper.TryParseBwrapVersion("bubblewrap 0.10.0"));
        Assert.False(OsSandboxWrapper.SupportsNativeOverlay(new Version(0, 8, 0)));
        Assert.True(OsSandboxWrapper.SupportsNativeOverlay(new Version(0, 9, 0)));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && File.Exists("/usr/bin/bwrap"))
        {
            var hostWrapper = new OsSandboxWrapper();
            var detected = hostWrapper.GetEffectiveBwrapVersion();
            Assert.NotNull(detected);
        }
    }

    [Fact]
    public async Task SandboxExecution_EndToEndThroughAgentRuntimeAndLiveHostBwrapWhenPermitted()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(
                    _fixture.MockCodexPath,
                    Env: new Dictionary<string, string>
                    {
                        ["PERGA_TEST_BWRAP_CAPTURE"] = _fixture.BwrapCapturedArgsPath
                    })
            },
            Sandbox = new OsSandboxOptions
            {
                PlatformOverride = OSPlatform.Linux,
                BwrapExecutablePath = _fixture.MockBwrapPath,
                BwrapVersionOverride = new Version(0, 8, 0)
            }
        });

        var spec = new HarnessLaunchSpec(
            Provider: "codex",
            Model: "gpt-5.4",
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: MockCliFixture.CreateSampleMcpServers(),
            Sandbox: OsSandboxMode.ReadOnlyBwrap);

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        var events = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("sandbox-hello", "corr_sbx"), CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Contains(events, e => e is UnifiedAgentEvent.TurnCompleted);
        Assert.True(File.Exists(_fixture.BwrapCapturedArgsPath));
        var capturedArgs = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(_fixture.BwrapCapturedArgsPath))!;
        Assert.True(IndexOfSequence(capturedArgs, "--ro-bind", _fixture.WorkspaceDir, _fixture.WorkspaceDir) >= 0);

        // Additionally run real /usr/bin/bwrap isolation check if unprivileged user namespaces are permitted in current host context.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && CanExecuteHostBwrap())
        {
            var liveWrapper = new OsSandboxWrapper();
            var probeFile = Path.Combine(_fixture.WorkspaceDir, "ro_probe.txt");

            var roCmd = liveWrapper.Wrap(
                new CommandInvocation("/bin/sh", ["-c", $"touch '{probeFile}'"], _fixture.WorkspaceDir, new Dictionary<string, string>()),
                OsSandboxMode.ReadOnlyBwrap);
            var roExit = await RunWrappedExitCodeAsync(roCmd);
            Assert.NotEqual(0, roExit);
            Assert.False(File.Exists(probeFile));

            var rwCmd = liveWrapper.Wrap(
                new CommandInvocation("/bin/sh", ["-c", $"echo ok > '{probeFile}'"], _fixture.WorkspaceDir, new Dictionary<string, string>()),
                OsSandboxMode.WorkspaceWriteBwrap);
            var rwExit = await RunWrappedExitCodeAsync(rwCmd);
            Assert.Equal(0, rwExit);
            Assert.True(File.Exists(probeFile));
        }
    }

    [Fact]
    public void IronLaws_StaticAuditOfAgentKitSources_ZeroCompactAndZeroCliPermissionDsl()
    {
        var repoRoot = FindRepoRoot();
        var libDir = Path.Combine(repoRoot, "libs", "Perga.AgentKit");
        var csFiles = Directory.GetFiles(libDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);

        var forbiddenSubstrings = new[]
        {
            "/compact",
            "shell_environment_policy",
            "Edit(//",
            "--allowedTools",
            "--disallowedTools",
            "--dangerously-skip-permissions"
        };

        foreach (var file in csFiles)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in forbiddenSubstrings)
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static bool CanExecuteHostBwrap()
    {
        if (!File.Exists("/usr/bin/bwrap"))
        {
            return false;
        }

        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/bwrap",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            foreach (var arg in new[] { "--ro-bind", "/", "/", "/bin/true" })
            {
                p.StartInfo.ArgumentList.Add(arg);
            }

            if (!p.Start())
            {
                return false;
            }

            p.WaitForExit(2000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunWrappedExitCodeAsync(CommandInvocation cmd)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cmd.FileName,
                WorkingDirectory = cmd.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var arg in cmd.Arguments)
        {
            p.StartInfo.ArgumentList.Add(arg);
        }

        p.Start();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Perga.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate Perga.sln above test base directory.");
    }

    private static void AssertContainsPair(IReadOnlyList<string> args, string flag, string src, string dest) =>
        Assert.True(
            IndexOfSequence(args, flag, src, dest) >= 0,
            $"Expected '{flag} {src} {dest}' in [{string.Join(' ', args)}]");

    private static void AssertContainsFlagValue(IReadOnlyList<string> args, string flag, string value) =>
        Assert.True(
            IndexOfSequence(args, flag, value) >= 0,
            $"Expected '{flag} {value}' in [{string.Join(' ', args)}]");

    private static int IndexOfSingle(IReadOnlyList<string> args, string token)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], token, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfSequence(IReadOnlyList<string> args, params string[] seq)
    {
        for (var i = 0; i <= args.Count - seq.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < seq.Length; j++)
            {
                if (!string.Equals(args[i + j], seq[j], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }
}
