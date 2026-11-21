using System.Text.Json;
using Perga.AgentKit;

namespace Perga.AgentKit.Tests;

public sealed class SubprocessTransportTests
{
    [Fact]
    public async Task NonLocalTransport_DoesNotLeakHostEnvironment_AndDelegatesResolution()
    {
        var transport = new RecordingRemoteTransport();
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            SubprocessTransport = transport,
            BridgeScriptPath = "/host/private/perga-oar-bridge.mjs",
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new(
                    "/home/jasper/.local/bin/codex",
                    new Dictionary<string, string> { ["CUSTOM_TARGET_VAR"] = "from-spec" })
            }
        });

        await using var session = runtime.CreateSession(new HarnessLaunchSpec(
            Provider: "codex",
            Model: null,
            Cwd: "/home/jasper/repo",
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: new Dictionary<string, McpServerConfig>()));

        await session.InitializeAsync(CancellationToken.None);

        Assert.Single(transport.StartedInvocations);
        var inv = transport.StartedInvocations[0];
        Assert.Equal("/remote/bin/node24", inv.FileName);
        Assert.Equal(["/remote/staged/perga-oar-bridge.mjs"], inv.Arguments);
        Assert.Equal("/home/jasper/repo", inv.WorkingDirectory);

        // Must NOT inherit Host HOME/USER/PATH/SSH_AUTH_SOCK for non-local targets.
        Assert.False(inv.Environment.ContainsKey("HOME"));
        Assert.False(inv.Environment.ContainsKey("USER"));
        Assert.False(inv.Environment.ContainsKey("SSH_AUTH_SOCK"));
        Assert.Equal("from-spec", inv.Environment["CUSTOM_TARGET_VAR"]);
    }

    private sealed class RecordingRemoteTransport : ISubprocessTransport
    {
        public List<CommandInvocation> StartedInvocations { get; } = [];
        public bool IsLocal => false;

        public ISubprocessHandle StartInteractive(CommandInvocation invocation)
        {
            StartedInvocations.Add(invocation);
            return new FakeOarRpcHandle();
        }

        public string ResolveNodeExecutable(string? configuredExecutable, IReadOnlyDictionary<string, string>? environment) =>
            "/remote/bin/node24";

        public string ResolveBridgeScriptPath(string? configuredBridgeScriptPath) =>
            "/remote/staged/perga-oar-bridge.mjs";

        public bool DirectoryExists(string path) => path == "/home/jasper/repo";
    }

    private sealed class FakeOarRpcHandle : ISubprocessHandle
    {
        private readonly System.Threading.Channels.Channel<string> _stdout =
            System.Threading.Channels.Channel.CreateUnbounded<string>();

        public int? ProcessId => 4242;
        public bool HasExited { get; private set; }
        public bool ShutdownConfirmed => HasExited;
        public int? TryGetExitCode() => HasExited ? 0 : null;

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idElem) && root.TryGetProperty("method", out var methodElem))
            {
                var method = methodElem.GetString();
                var idRaw = idElem.GetRawText();
                if (method == "session.open")
                {
                    _stdout.Writer.TryWrite(
                        "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{\"sessionId\":\"remote-ses-1\",\"capabilities\":{\"supportsSteer\":true,\"supportsResume\":true}}}");
                }
                else
                {
                    _stdout.Writer.TryWrite("{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":{}}");
                }
            }

            return Task.CompletedTask;
        }

        public async ValueTask<string?> ReadStdoutLineAsync(CancellationToken ct)
        {
            try
            {
                return await _stdout.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (System.Threading.Channels.ChannelClosedException)
            {
                return null;
            }
        }

        public string GetStderrSummary() => string.Empty;
        public bool SendInterruptSignal() => true;

        public ValueTask DisposeAsync()
        {
            HasExited = true;
            _stdout.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
