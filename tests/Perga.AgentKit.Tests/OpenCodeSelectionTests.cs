using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class OpenCodeSelectionTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task AppliesOnlyTheChangedSettingAndRequiresTheOpenCodeReadback()
    {
        var log = Path.Combine(_fixture.RootDir, "opencode-acp.jsonl");
        await using var runtime = CreateRuntime(log);
        await using var session = await OpenAsync(runtime);
        var selected = SessionConfiguration.FromNullable("vendor/second", "max");
        var applied = Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(selected));
        Assert.Equal("vendor/second", applied.Configuration.NativeModel);
        Assert.Equal("max", applied.Configuration.NativeEffort);

        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(selected));
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(
            SessionConfiguration.FromNullable("vendor/second", "high")));
        var reset = Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(
            SessionConfiguration.FromNullable("vendor/second")));
        Assert.Equal("default", reset.Configuration.NativeEffort);
        var switched = Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(
            SessionConfiguration.FromNullable("vendor/first")));
        Assert.Equal("vendor/first", switched.Configuration.NativeModel);
        Assert.Null(switched.Configuration.NativeEffort);
        Assert.Equal(
            [("pick-model", "vendor/second"), ("pick-effort", "max"), ("pick-effort", "high"),
                ("pick-effort", "default"), ("pick-model", "vendor/first")],
            ReadSelections(log));
    }

    [Fact]
    public async Task FollowingTheFreshNativeSelectionDoesNotResetAProviderPreference()
    {
        var log = Path.Combine(_fixture.RootDir, "opencode-acp-follow.jsonl");
        await using var runtime = CreateRuntime(log, startWithEffort: true);
        await using var session = await OpenAsync(runtime);
        Assert.Equal("high", session.ConfigurationState?.NativeEffort);
        var followDefault = SessionConfiguration.FromNullable(null);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var applied = Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(followDefault));
            Assert.Equal("high", applied.Configuration.NativeEffort);
        }
        Assert.Empty(ReadSelections(log));
    }

    [Fact]
    public async Task EffortResetMustReadBackTheKnownEffortOption()
    {
        var log = Path.Combine(_fixture.RootDir, "opencode-acp-reset.jsonl");
        await using var runtime = CreateRuntime(log, "missing-reset-effort", startWithEffort: true);
        await using var session = await OpenAsync(runtime);
        Assert.IsType<SessionConfigurationOutcome.Applied>(
            await session.ConfigureAsync(SessionConfiguration.FromNullable("vendor/second", "high")));
        var reset = SessionConfiguration.FromNullable("vendor/second");
        Assert.IsType<SessionConfigurationOutcome.Rejected>(await session.ConfigureAsync(reset));
        Assert.IsType<SessionConfigurationOutcome.Rejected>(await session.ConfigureAsync(reset));
        Assert.Null(session.ConfigurationState);
        Assert.Equal([("pick-effort", "default"), ("pick-effort", "default")], ReadSelections(log));
    }

    [Fact]
    public async Task ReusingAnOpenCodeWorkerDoesNotIssueAnOrdinarySelectionWrite()
    {
        var log = Path.Combine(_fixture.RootDir, "opencode-worker.jsonl");
        await using var runtime = CreateRuntime(log);
        var spec = new HarnessLaunchSpec("acp:opencode", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ConfigureWorker: true);
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.IsType<SessionConfigurationOutcome.Applied>(await session.ConfigureAsync(SessionConfiguration.FromLaunch(spec)));
        Assert.Empty(ReadSelections(log));
    }

    [Theory]
    [InlineData("wrong-model")]
    [InlineData("wrong-effort")]
    [InlineData("malformed-effort")]
    public async Task ContradictoryOrIncompleteReadbackCannotConfirmASelection(string reply)
    {
        var log = Path.Combine(_fixture.RootDir, "opencode-acp-rejected.jsonl");
        await using var runtime = CreateRuntime(log, reply);
        await using var session = await OpenAsync(runtime);
        var rejected = Assert.IsType<SessionConfigurationOutcome.Rejected>(
            await session.ConfigureAsync(SessionConfiguration.FromNullable("vendor/second", "max")));
        Assert.Equal(ConfigurationFailureCode.InvalidAcknowledgement, rejected.Code);
        Assert.Null(session.ConfigurationState);
        Assert.Null(session.ContextUsageRevision);
        await Assert.ThrowsAsync<SessionConfigurationException>(async () =>
        {
            await foreach (var _ in session.StreamTurnAsync(new("blocked", "blocked"), CancellationToken.None)) { }
        });
        Assert.DoesNotContain(File.ReadAllLines(log), line => line.Contains("session/prompt", StringComparison.Ordinal));
        (string?, string?)[] expected = reply == "wrong-effort"
            ? [("pick-model", "vendor/second"), ("pick-effort", "max")]
            : [("pick-model", "vendor/second")];
        Assert.Equal(expected, ReadSelections(log));
    }

    private Task<IAgentSession> OpenAsync(AgentRuntime runtime) => runtime.OpenSessionAsync(new HarnessLaunchSpec(
        "acp:opencode", null, _fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>()), CancellationToken.None);

    private static (string? Id, string? Value)[] ReadSelections(string log) => File.ReadAllLines(log)
        .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
        .Where(call => call.GetProperty("method").GetString() == "session/set_config_option")
        .Select(call => call.GetProperty("params"))
        .Select(parameters => (parameters.GetProperty("configId").GetString(), parameters.GetProperty("value").GetString()))
        .ToArray();

    private AgentRuntime CreateRuntime(string log, string reply = "valid", bool startWithEffort = false)
    {
        var executable = _fixture.WriteExecutable("mock-opencode-v2.py", """
            #!/usr/bin/env python3
            import json, os, sys

            initial = os.environ["MOCK_OPENCODE_WITH_EFFORT"] == "1"
            current = "vendor/second" if initial else "vendor/first"
            effort = "high" if initial else "default"
            reply = os.environ["MOCK_OPENCODE_REPLY"]

            def options():
                result = [{"id": "pick-model", "category": "model", "type": "select", "currentValue": current,
                           "options": [{"value": model} for model in ("vendor/first", "vendor/second")]}]
                if current == "vendor/second":
                    result.append({"id": "pick-effort", "category": "thought_level", "type": "select", "currentValue": effort,
                                   "options": [{"value": value} for value in ("default", "high", "max")]})
                return result

            for line in sys.stdin:
                call = json.loads(line)
                with open(os.environ["MOCK_OPENCODE_LOG"], "a") as log:
                    log.write(line)
                method = call["method"]
                params = call.get("params") or {}
                if method == "initialize":
                    result = {"protocolVersion": 1, "agentCapabilities": {},
                              "agentInfo": {"name": "OpenCode", "version": "2.1.4"}}
                elif method == "session/new":
                    result = {"sessionId": "sample", "configOptions": options()}
                elif method == "session/set_config_option":
                    if params["configId"] == "pick-effort":
                        effort = params["value"]
                    else:
                        model = params["value"]
                        effort = effort if model == current else "default"
                        current = model
                    if reply == "wrong-model":
                        current = "vendor/first"
                    if reply == "wrong-effort":
                        effort = "high"
                    if reply == "malformed-effort":
                        effort = "not-advertised"
                    result = {"configOptions": options()}
                    if reply == "missing-reset-effort" and params["configId"] == "pick-effort" and params["value"] == "default":
                        result["configOptions"] = result["configOptions"][:1]
                else:
                    print(json.dumps({"jsonrpc": "2.0", "id": call["id"],
                                      "error": {"code": -32601, "message": "Unknown method"}}), flush=True)
                    continue
                print(json.dumps({"jsonrpc": "2.0", "id": call["id"], "result": result}), flush=True)
            """);
        return new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:opencode"] = new(executable, Env: new Dictionary<string, string>
                {
                    ["MOCK_OPENCODE_LOG"] = log,
                    ["MOCK_OPENCODE_REPLY"] = reply,
                    ["MOCK_OPENCODE_WITH_EFFORT"] = startWithEffort ? "1" : "0"
                })
            }
        });
    }
}
