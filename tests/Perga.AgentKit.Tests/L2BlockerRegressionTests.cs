using System.Runtime.InteropServices;
using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class L2BlockerRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedSuffixCatalogCannotConfirmUnknownDefaultEffort(bool reset)
    {
        using var fixture = new MockCliFixture();
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:custom"] = new(fixture.MockAcpPath, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_MODELS"] = JsonSerializer.Serialize(new { models = new {
                        currentModelId = "vendor/model-high", availableModels = new[] {
                            new { modelId = "vendor/model-medium" }, new { modelId = "vendor/model-high" } } } })
                })
            }
        });
        var spec = new HarnessLaunchSpec("acp:custom", "vendor/model", fixture.WorkspaceDir,
            "stored-session", null, new Dictionary<string, McpServerConfig>());
        if (!reset)
        {
            var error = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(spec, default));
            Assert.IsType<SessionConfigurationOutcome.Unsupported>(error.Outcome);
        }
        else
        {
            await using var session = await runtime.OpenSessionAsync(spec with { ReasoningEffort = "high" }, default);
            Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(SessionConfiguration.FromLaunch(spec)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaudeRejectsContradictoryResumedIdentity(bool late)
    {
        using var fixture = new MockCliFixture();
        var script = fixture.WriteExecutable("identity.py", """
#!/usr/bin/env python3
import json, sys
if '--help' in sys.argv:
    print('--effort <level>')
    sys.exit(0)
for line in sys.stdin:
    frame = json.loads(line)
    if frame['type'] == 'control_request':
        print(json.dumps({'type':'control_response', 'session_id': 'original' if LATE else 'other', 'response':{'subtype':'success','request_id':frame['request_id'],'response':{}}}), flush=True)
    elif frame['type'] == 'user':
        print(json.dumps({'type':'result','session_id':'other','subtype':'success','is_error':False}), flush=True)
""".Replace("LATE", late ? "True" : "False"));
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions { ProviderOverrides =
            new Dictionary<string, ProviderCommandOverride> { ["claude"] = new(script) } });
        var spec = new HarnessLaunchSpec("claude", null, fixture.WorkspaceDir, "original", null, new Dictionary<string, McpServerConfig>());
        if (!late)
            await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(spec, default));
        else
        {
            await using var session = await runtime.OpenSessionAsync(spec, default);
            var events = new List<UnifiedAgentEvent>();
            await foreach (var evt in session.StreamTurnAsync(new("test", "test"), default)) events.Add(evt);
            Assert.Equal("original", session.NativeThreadId);
            Assert.Contains(events, evt => evt is UnifiedAgentEvent.TurnError);
        }
    }

    [Fact]
    public async Task ClaudeHelpCacheIsScopedToCwdSandboxAndOverlays()
    {
        using var fixture = new MockCliFixture();
        var log = Path.Combine(fixture.RootDir, "wrapped.jsonl");
        var wrapper = fixture.WriteExecutable("capture-wrapper.py", $$"""
#!/usr/bin/env python3
import json, os, sys
with open({{JsonSerializer.Serialize(log)}}, 'a') as f:
    f.write(json.dumps(sys.argv[1:]) + '\n')
sep = sys.argv.index('--')
os.chdir(sys.argv[sys.argv.index('--chdir') + 1])
os.execvp(sys.argv[sep + 1], sys.argv[sep + 1:])
""");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Sandbox = new() { PlatformOverride = OSPlatform.Linux, BwrapVersionOverride = new Version(0, 9, 0), BwrapExecutablePath = wrapper },
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride> { ["claude"] = new(fixture.MockClaudePath) }
        });
        var cwd2 = Path.Combine(fixture.RootDir, "second-workspace");
        Directory.CreateDirectory(cwd2);
        var lower = Path.Combine(fixture.RootDir, "lower");
        Directory.CreateDirectory(lower);
        var spec = new HarnessLaunchSpec("claude", null, fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>());
        await using (var host = await runtime.OpenSessionAsync(spec, default)) { }
        spec = spec with { Sandbox = OsSandboxMode.ReadOnlyBwrap };
        await using (var first = await runtime.OpenSessionAsync(spec, default)) { }
        await using (var cached = await runtime.OpenSessionAsync(spec, default)) { }
        await using (var cwd = await runtime.OpenSessionAsync(spec with { Cwd = cwd2 }, default)) { }
        await using (var overlay = await runtime.OpenSessionAsync(spec with { OverlayMounts = [new([lower], null, null, fixture.WorkspaceDir)] }, default)) { }
        await using (var writable = await runtime.OpenSessionAsync(spec with { Sandbox = OsSandboxMode.WorkspaceWriteBwrap }, default)) { }
        var helpInvocations = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<string[]>(line)!)
            .Where(args => args.Contains("--help")).ToArray();
        Assert.Equal(4, helpInvocations.Length);
        Assert.Contains(helpInvocations, args => args[Array.IndexOf(args, "--chdir") + 1] == cwd2);
        Assert.Contains(helpInvocations, args => args.Contains(lower));
        Assert.All(helpInvocations, args => Assert.Contains(fixture.MockClaudePath, args));
    }

    [Fact]
    public async Task ClaudeHelpCannotExecuteWhenRequestedIsolationCannotStart()
    {
        using var fixture = new MockCliFixture();
        var marker = Path.Combine(fixture.RootDir, "help-ran");
        var script = fixture.WriteExecutable("help.py", $"""
#!/usr/bin/env python3
import sys
if '--help' in sys.argv:
    open({JsonSerializer.Serialize(marker)}, 'w').write('outside isolation')
    print('--effort <level>')
""");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            Sandbox = new() { PlatformOverride = OSPlatform.Linux, BwrapVersionOverride = new Version(0, 9, 0),
                BwrapExecutablePath = Path.Combine(fixture.RootDir, "missing-bwrap") },
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride> { ["claude"] = new(script) }
        });
        var spec = new HarnessLaunchSpec("claude", null, fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), Sandbox: OsSandboxMode.ReadOnlyBwrap);
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.OpenSessionAsync(spec, default));
        Assert.False(File.Exists(marker));
    }
}
