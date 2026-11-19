using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class AcpProviderTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private AgentRuntime CreateRuntime(IReadOnlyDictionary<string, string>? acpEnv = null) =>
        new(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(Path.Combine(_fixture.RootDir, "unavailable-codex")),
                ["claude"] = new(Path.Combine(_fixture.RootDir, "unavailable-claude")),
                ["acp:antigravity"] = new(_fixture.MockAcpPath, Env: acpEnv),
                ["acp:opencode"] = new(_fixture.MockAcpPath, Env: acpEnv)
            }
        });

    [Fact]
    public async Task PermissionModeProbeUsesWorkspaceAndNullLaunchKeepsWorkspaceDefault()
    {
        var log = Path.Combine(_fixture.RootDir, "workspace-modes.jsonl");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_PROJECT_MODES"] = "1"
        });
        var payload = """{"modes":{"currentModeId":"project-plan","availableModes":[{"id":"project-plan","name":"Project plan"}]}}""";
        File.WriteAllText(Path.Combine(_fixture.WorkspaceDir, "permission-modes.json"), payload);
        var modes = await runtime.ProbePermissionModesAsync("acp:antigravity", _fixture.WorkspaceDir, CancellationToken.None);
        Assert.Equal("project-plan", Assert.Single(modes).Id);
        Assert.Empty(await runtime.ProbePermissionModesAsync("acp:antigravity", _fixture.RootDir, CancellationToken.None));
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>()), CancellationToken.None);
        Assert.Equal("project-plan", Assert.Single(((AcpAgentSession)session).AvailablePermissionModes).Id);
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var opens = calls.Where(call => call.GetProperty("method").GetString() == "session/new").ToArray();
        Assert.Equal([_fixture.WorkspaceDir, _fixture.RootDir, _fixture.WorkspaceDir],
            opens.Select(call => call.GetProperty("params").GetProperty("cwd").GetString()));
        Assert.DoesNotContain(calls, call => call.GetProperty("method").GetString() is "session/set_mode" or "session/set_config_option");
    }

    [Theory]
    [InlineData(0, null, false)]
    [InlineData(0, "restored", false)]
    [InlineData(0, "restored", true)]
    [InlineData(1, null, false)]
    [InlineData(1, "restored", false)]
    [InlineData(1, "restored", true)]
    [InlineData(2, null, false)]
    [InlineData(2, "restored", false)]
    [InlineData(2, "restored", true)]
    public async Task PermissionModesAreProbedAndAppliedOnNewLoadAndResume(int shape, string? resume, bool nativeResume)
    {
        var config = shape != 0;
        var log = Path.Combine(_fixture.RootDir, "mode-calls.jsonl");
        var payload = config
            ? """{"configOptions":[{"id":"unrelated","category":"model","type":"select","options":[{"value":"wrong"}]},{"id":"agent-mode","category":"mode","type":"select","currentValue":"safe","options":[{"value":"safe","name":"Safe","description":"Read first"},{"group":"Other","options":[{"value":"execute","name":"Execute"}]}]}]}"""
            : """{"modes":{"currentModeId":"safe","availableModes":[{"id":"safe","name":"Safe","description":"Read first"},{"id":"execute","name":"Execute"}]}}""";
        if (shape == 2) payload = payload.Replace("agent-mode", "mode").Replace("\"category\":\"mode\",", "");
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_MODELS"] = payload,
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_RESUME"] = nativeResume ? "1" : "0"
        });
        var statuses = await runtime.ProbeProvidersAsync(CancellationToken.None);
        foreach (var status in statuses.Where(status => status.Provider.StartsWith("acp:")))
        {
            Assert.Equal(["safe", "execute"], status.PermissionModes!.Select(mode => mode.Id));
            Assert.Equal("safe", Assert.Single(status.PermissionModes!, mode => mode.IsDefault).Id);
            Assert.Equal("Read first", status.PermissionModes![0].Description);
        }
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:antigravity", null, _fixture.WorkspaceDir, resume, null,
            new Dictionary<string, McpServerConfig>(), PermissionMode: "execute"), CancellationToken.None);
        Assert.Equal(["safe", "execute"], ((AcpAgentSession)session).AvailablePermissionModes.Select(mode => mode.Id));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var selected = Assert.Single(calls, call => call.GetProperty("method").GetString() ==
            (config ? "session/set_config_option" : "session/set_mode"));
        var parameters = selected.GetProperty("params");
        Assert.Equal(resume ?? "acp_sess_001", parameters.GetProperty("sessionId").GetString());
        Assert.Equal("execute", parameters.GetProperty(config ? "value" : "modeId").GetString());
        if (config) Assert.Equal(shape == 2 ? "mode" : "agent-mode", parameters.GetProperty("configId").GetString());
    }

    [Theory]
    [InlineData("vendor/model-high", null, "vendor/model-high")]
    [InlineData("vendor/model-high", "high", "vendor/model-high")]
    [InlineData("vendor/model-HIGH", "high", "vendor/model-HIGH")]
    [InlineData("vendor/model-low,vendor/model-high", null, "vendor/model-low")]
    [InlineData("vendor/model-low,vendor/model-high", "high", "vendor/model-high")]
    [InlineData("vendor/model,vendor/model-high", null, "vendor/model")]
    [InlineData("vendor/model,vendor/model-high", "high", "vendor/model-high")]
    [InlineData("vendor/model-medium,vendor/model-high", null, "vendor/model-medium")]
    public async Task AdvertisedGroupDefaultAndEffortUseExactIdsOnOpenAndSwitch(string ids, string? effort, string expected)
    {
        var log = Path.Combine(_fixture.RootDir, "strict-model.jsonl");
        var modelIds = ids.Split(',');
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_LOG"] = log,
            ["MOCK_ACP_STRICT_MODELS"] = "1",
            ["MOCK_ACP_MODELS"] = JsonSerializer.Serialize(new { models = new {
                currentModelId = modelIds[0], availableModels = modelIds.Select(id => new { modelId = id }) } })
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None), p => p.Provider == "acp:opencode");
        var model = Assert.Single(status.ModelCatalog!);
        Assert.Equal("vendor/model", model.Model);
        if (effort is null && !modelIds.Contains("vendor/model"))
            Assert.Equal(expected[(expected.LastIndexOf('-') + 1)..].ToLowerInvariant(), model.DefaultReasoningEffort);
        if (modelIds.Contains("vendor/model"))
            Assert.Null(model.DefaultReasoningEffort);
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", model.Model, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: effort), CancellationToken.None);
        Assert.True(await session.TrySetModelAsync(model.Model, effort));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.GetProperty("method").GetString() == "session/set_model").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal(expected, call.GetProperty("params").GetProperty("modelId").GetString()));
    }

    [Fact]
    public async Task ModelSelectionPropagatesErrorsOtherThanMethodNotFound()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:custom"] = new(_fixture.MockAcpPath, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_MODEL_ERROR"] = "-32602"
                })
            }
        });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:custom", "invalid-model", _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>()), CancellationToken.None));
        Assert.Contains("model switch failed", error.Message);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("loaded-session", 0)]
    [InlineData(null, -32601)]
    [InlineData("loaded-session", -32601)]
    public async Task ModelSelectionFollowsSessionBinding(string? resumeId, int error)
    {
        var log = Path.Combine(_fixture.RootDir, "rpc.jsonl");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:custom"] = new(_fixture.MockAcpPath, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_LOG"] = log,
                    ["MOCK_ACP_MODEL_ERROR"] = error.ToString()
                })
            }
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:custom", "selected-model", _fixture.WorkspaceDir, resumeId, null,
            new Dictionary<string, McpServerConfig>()), CancellationToken.None);
        Assert.Equal(error == 0, await session.TrySetModelAsync("another-model"));
        Assert.False(await session.TrySetModelAsync(null));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var expectedMethods = error == -32601
            ? new[] { "initialize", resumeId is null ? "session/new" : "session/load", "session/set_model", "session/set_config_option", "session/set_model", "session/set_config_option" }
            : new[] { "initialize", resumeId is null ? "session/new" : "session/load", "session/set_model", "session/set_model" };
        Assert.Equal(expectedMethods,
            calls.Select(call => call.GetProperty("method").GetString()));
        Assert.False(calls[1].GetProperty("params").TryGetProperty("model", out _));
        var selection = calls[2].GetProperty("params");
        Assert.Equal(session.NativeThreadId, selection.GetProperty("sessionId").GetString());
        Assert.Equal("selected-model", selection.GetProperty("modelId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerSettingsUseNegotiatedOptionsAndRequireAcknowledgmentBeforePrompt(bool rejectsEffort)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_WORKER_OPTIONS"] = "1",
            ["MOCK_ACP_REJECT_WORKER_EFFORT"] = rejectsEffort ? "1" : "0"
        });
        var spec = new HarnessLaunchSpec("acp:opencode", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers(), ConfigureWorker: true);
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.Equal("small", session.WorkerConfiguration!.DefaultModel);
        Assert.Equal("normal", session.WorkerConfiguration.DefaultReasoningEffort);
        Assert.Equal(["normal", "deep"], session.WorkerConfiguration.GetReasoningEfforts("large"));

        var events = await DrainTurnAsync(session, new TurnInput("process request", "acp-worker-config", "large", "deep"));
        if (rejectsEffort)
        {
            Assert.Contains(events, item => item is UnifiedAgentEvent.TurnError { BeforePrompt: true });
            Assert.DoesNotContain(events, item => item is UnifiedAgentEvent.TextDelta or UnifiedAgentEvent.TurnCompleted);
        }
        else
        {
            var output = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(item => item.Delta));
            Assert.Contains("model=large", output, StringComparison.Ordinal);
            Assert.Contains("effort=deep", output, StringComparison.Ordinal);
            Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
        }
    }

    [Fact]
    public async Task WorkerWithLegacyAcpModelKeepsItsExplicitLaunchModel()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec("acp:opencode", "gemini-4-carbon-high", _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers(), ConfigureWorker: true);
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.Null(session.WorkerConfiguration);

        var events = await DrainTurnAsync(session,
            new TurnInput("use launch model", "acp-worker-launch", spec.Model));
        var output = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(item => item.Delta));
        Assert.Contains("model=gemini-4-carbon-high|via=session/set_model", output, StringComparison.Ordinal);
        Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
    }

    [Fact]
    public async Task WorkerResumeUsesTurnSelectionWhenOriginalLaunchModelIsGone()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string> { ["MOCK_ACP_WORKER_OPTIONS"] = "1" });
        var spec = new HarnessLaunchSpec("acp:opencode", "small", _fixture.WorkspaceDir, "stored-session", null,
            MockCliFixture.CreateSampleMcpServers(), ConfigureWorker: true);
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.Equal("large", Assert.Single(session.WorkerConfiguration!.Models).Id);
        Assert.Null(session.WorkerConfiguration.DefaultModel);

        var events = await DrainTurnAsync(session, new TurnInput("continue Worker", "acp-worker-resume", "large", "deep"));
        var output = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(item => item.Delta));
        Assert.Contains("model=large", output, StringComparison.Ordinal);
        Assert.Contains("effort=deep", output, StringComparison.Ordinal);
        Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
    }

    [Theory]
    [InlineData("acp:antigravity", true)]
    [InlineData("acp:opencode", true)]
    [InlineData("antigravity", true)]
    [InlineData("opencode", true)]
    public async Task AcpBuiltInAndNormalizedSurfaces_SessionNewAndLoad_SetModel_SystemPromptAndUpdates(
        string provider,
        bool expectsAcpSubcommand)
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec(
            Provider: provider,
            Model: "gemini-4-carbon-medium",
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: "ACP system prompt",
            McpServers: MockCliFixture.CreateSampleMcpServers());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.False(session.SupportsNativeSteer);
        Assert.Equal("acp_sess_001", session.NativeThreadId);

        var events = await DrainTurnAsync(session, new TurnInput("hello-acp", "acp_corr_1"));
        Assert.Contains(events, e => e is UnifiedAgentEvent.ThreadBound { NativeThreadId: "acp_sess_001" });
        Assert.Contains(events, e => e is UnifiedAgentEvent.ThinkingDelta { Delta: "acp-thought-1" });

        var toolStarted = Assert.Single(events.OfType<UnifiedAgentEvent.ToolCallStarted>());
        Assert.Equal("vcs_status", toolStarted.Name);

        var toolCompleted = Assert.Single(events.OfType<UnifiedAgentEvent.ToolCallCompleted>());
        Assert.Equal(toolStarted.CallId, toolCompleted.CallId);
        Assert.Equal("clean-working-copy", toolCompleted.Output);
        Assert.False(toolCompleted.IsError);

        var text = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        var expectedPrefix =
            $"acp[session/new|acp_sess_001|sub={expectsAcpSubcommand}|model=gemini-4-carbon-medium|via=session/set_model|mcp=local-tools,perga]:" +
            "<system_instructions>\nACP system prompt\n</system_instructions>\n\nhello-acp";
        Assert.Contains(expectedPrefix, text, StringComparison.Ordinal);

        var done = Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.NotNull(done.Usage);
        Assert.Equal(200, done.Usage!.InputTokens);
        Assert.Equal(75, done.Usage.OutputTokens);
        Assert.Equal(40, done.Usage.CachedInputTokens);
        Assert.Equal(1048576, done.Usage.ContextWindowTokens);

        // Turn 2 on same session does NOT duplicate <system_instructions>
        var turn2Events = await DrainTurnAsync(session, new TurnInput("second-acp-turn", "acp_corr_1b"));
        var turn2Text = string.Concat(turn2Events.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.DoesNotContain("<system_instructions>", turn2Text, StringComparison.Ordinal);
        Assert.EndsWith(":second-acp-turn", turn2Text, StringComparison.Ordinal);

        // Resume via session/load does NOT prepend <system_instructions>
        var resumeSpec = spec with { ResumeNativeThreadId = "acp_loaded_88" };
        await using var loadedSession = await runtime.OpenSessionAsync(resumeSpec, CancellationToken.None);
        Assert.Equal("acp_loaded_88", loadedSession.NativeThreadId);
        var loadedEvents = await DrainTurnAsync(loadedSession, new TurnInput("resumed-prompt", "acp_corr_2"));
        var loadedText = string.Concat(loadedEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("acp[session/load|acp_loaded_88", loadedText, StringComparison.Ordinal);
        Assert.DoesNotContain("<system_instructions>", loadedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcpRestoreUsesNegotiatedResumeAndRejectsUnsupportedProviders(bool supportsResume)
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_DISABLE_LOAD"] = "1",
            ["MOCK_ACP_RESUME"] = supportsResume ? "1" : "0",
        });
        var spec = new HarnessLaunchSpec("acp:antigravity", null, _fixture.WorkspaceDir, "stored-session", null,
            MockCliFixture.CreateSampleMcpServers());
        if (!supportsResume)
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => runtime.OpenSessionAsync(spec, CancellationToken.None));
            return;
        }

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.Equal("stored-session", session.NativeThreadId);
        var events = await DrainTurnAsync(session, new TurnInput("next", "resume-turn"));
        Assert.Contains(events, item => item is UnifiedAgentEvent.TextDelta text &&
            text.Delta.Contains("session/resume|stored-session", StringComparison.Ordinal));
        Assert.True(session.SupportsScopedClose);
        Assert.True(await session.TryCloseSessionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcpMalformedRecoveryRepliesDoNotConfirmResumeOrClose()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_RESUME"] = "1",
            ["MOCK_ACP_INVALID_RESUME"] = "1",
            ["MOCK_ACP_INVALID_CLOSE"] = "1",
        });
        var spec = new HarnessLaunchSpec("acp:antigravity", null, _fixture.WorkspaceDir, "stored-session", null,
            MockCliFixture.CreateSampleMcpServers());
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.OpenSessionAsync(spec, CancellationToken.None));

        await using var session = await runtime.OpenSessionAsync(spec with { ResumeNativeThreadId = null }, CancellationToken.None);
        Assert.True(session.SupportsScopedClose);
        Assert.False(await session.TryCloseSessionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcpSetModel_FallsBackToSetConfigOptionWhenSetModelReturnsMethodNotFound()
    {
        await using var runtime = CreateRuntime(new Dictionary<string, string>
        {
            ["MOCK_ACP_NO_SET_MODEL"] = "1"
        });

        var spec = new HarnessLaunchSpec(
            Provider: "acp:antigravity",
            Model: "claude-sonnet-5-5-medium",
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: new Dictionary<string, McpServerConfig>());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        var events = await DrainTurnAsync(session, new TurnInput("check-fallback-model", "corr_cfg"));
        var text = string.Concat(events.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("model=claude-sonnet-5-5-medium|via=session/set_config_option", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcpWithoutHttpMcpCapability_BridgesHttpMcpToLocalEndpointViaStdioBypassingHttpProxy()
    {
        // Spin up a real local Streamable HTTP MCP server on 127.0.0.1:0
        await using var mcpServer = new LocalStreamableHttpMcpTestServer("Bearer pg_live_bridge_token");

        var acpDriverWithMcpExec = _fixture.WriteExecutable(
            "acp_stdio_mcp_consumer.py",
            """
            #!/usr/bin/env python3
            import json, os, subprocess, sys

            def send(obj):
                sys.stdout.write(json.dumps(obj) + "\n")
                sys.stdout.flush()

            mcp_servers = []
            for raw in sys.stdin:
                line = raw.strip()
                if not line:
                    continue
                msg = json.loads(line)
                method = msg.get("method")
                req_id = msg.get("id")
                params = msg.get("params") or {}

                if method == "initialize":
                    http_cap = os.environ.get("TEST_ACP_HTTP_CAP") == "1"
                    mcp_caps = {"http": True} if http_cap else {}
                    send({
                        "jsonrpc": "2.0",
                        "id": req_id,
                        "result": {
                            "protocolVersion": 1,
                            "agentCapabilities": {"loadSession": True, "mcpCapabilities": mcp_caps}
                        }
                    })
                elif method == "session/new":
                    mcp_servers = params.get("mcpServers") or []
                    send({"jsonrpc": "2.0", "id": req_id, "result": {"sessionId": "acp_bridge_sess"}})
                elif method in ("session/set_model", "session/set_config_option"):
                    send({"jsonrpc": "2.0", "id": req_id, "result": {}})
                elif method == "session/prompt":
                    srv = next(s for s in mcp_servers if s.get("name") == "perga")
                    if srv.get("type") == "http":
                        send({
                            "jsonrpc": "2.0",
                            "method": "session/update",
                            "params": {
                                "sessionId": "acp_bridge_sess",
                                "update": {"sessionUpdate": "agent_message_chunk", "content": f"direct-http:{srv.get('url')}"}
                            }
                        })
                        send({"jsonrpc": "2.0", "id": req_id, "result": {"stopReason": "end_turn"}})
                        continue

                    child_env = dict(os.environ)
                    for pair in srv.get("env") or []:
                        child_env[pair["name"]] = pair["value"]

                    proc = subprocess.Popen(
                        [srv["command"]] + list(srv.get("args") or []),
                        stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE,
                        env=child_env,
                        text=True
                    )
                    try:
                        # 1. MCP initialize (JSON response + Mcp-Session-Id)
                        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-03-26"}}) + "\n")
                        proc.stdin.flush()
                        init_resp = json.loads(proc.stdout.readline())

                        # 2. MCP notifications/initialized (202 Accepted empty body -> no stdout line)
                        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
                        proc.stdin.flush()

                        # 3. MCP tools/list (JSON response)
                        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}) + "\n")
                        proc.stdin.flush()
                        list_resp = json.loads(proc.stdout.readline())
                        tool_name = list_resp["result"]["tools"][0]["name"]

                        # 4. MCP tools/call (SSE text/event-stream data: response!)
                        proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": tool_name, "arguments": {"whole": True}}}) + "\n")
                        proc.stdin.flush()
                        call_resp = json.loads(proc.stdout.readline())
                        tool_text = call_resp["result"]["content"][0]["text"]
                    finally:
                        proc.stdin.close()
                        proc.wait(timeout=5)

                    summary = f"bridged-ok|srv={init_resp['result']['serverInfo']['name']}|tool={tool_name}|out={tool_text}|hasType={'type' in srv}"
                    send({
                        "jsonrpc": "2.0",
                        "method": "session/update",
                        "params": {
                            "sessionId": "acp_bridge_sess",
                            "update": {"sessionUpdate": "agent_message_chunk", "content": summary}
                        }
                    })
                    send({"jsonrpc": "2.0", "id": req_id, "result": {"stopReason": "end_turn"}})
            """);

        // Deliberately inject a dead http_proxy (simulating agy's http_proxy=http://127.0.0.1:2080)
        // to prove NO_PROXY=127.0.0.1,localhost + direct Node HTTP connects directly to local Perga MCP!
        await using var bridgedRuntime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:antigravity"] = new(
                    acpDriverWithMcpExec,
                    Env: new Dictionary<string, string>
                    {
                        ["http_proxy"] = "http://127.0.0.1:1",
                        ["HTTP_PROXY"] = "http://127.0.0.1:1"
                    }),
                ["acp:opencode"] = new(
                    acpDriverWithMcpExec,
                    Env: new Dictionary<string, string>
                    {
                        ["TEST_ACP_HTTP_CAP"] = "1"
                    })
            }
        });

        var mcpServers = new Dictionary<string, McpServerConfig>
        {
            ["perga"] = new McpServerConfig.HttpMcpServer(
                mcpServer.EndpointUrl,
                new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer pg_live_bridge_token"
                })
        };

        // Case 1: mcpCapabilities: {} -> automatic stdio-to-HTTP bridge
        await using var bridgedSession = await bridgedRuntime.OpenSessionAsync(
            new HarnessLaunchSpec("acp:antigravity", null, _fixture.WorkspaceDir, null, null, mcpServers),
            CancellationToken.None);

        var bridgedEvents = await DrainTurnAsync(bridgedSession, new TurnInput("run-mcp", "corr_mcp_1"));
        var bridgedText = string.Concat(bridgedEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Equal("bridged-ok|srv=perga-kernel-test|tool=perga_report_read|out=sse-report-body-ok|hasType=False", bridgedText);
        Assert.True(mcpServer.SawInitialize);
        Assert.True(mcpServer.SawInitializedNotification);
        Assert.True(mcpServer.SawSessionHeaderOnSubsequentCalls);

        // Case 2: mcpCapabilities: { http: true } -> direct HTTP entry preserved
        await using var directHttpSession = await bridgedRuntime.OpenSessionAsync(
            new HarnessLaunchSpec("acp:opencode", null, _fixture.WorkspaceDir, null, null, mcpServers),
            CancellationToken.None);

        var directEvents = await DrainTurnAsync(directHttpSession, new TurnInput("run-direct", "corr_mcp_2"));
        var directText = string.Concat(directEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Equal($"direct-http:{mcpServer.EndpointUrl}", directText);
    }

    [Fact]
    public async Task AcpGenericExecutableSurface_PermissionRequestCancelAndRpcError()
    {
        await using var runtime = new AgentRuntime();
        var spec = new HarnessLaunchSpec(
            Provider: $"acp:{_fixture.MockAcpPath}",
            Model: null,
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: new Dictionary<string, McpServerConfig>());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        // Permission allow selects allow_once optionId
        var allowEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_PERMISSION", "perm_1"), CancellationToken.None))
        {
            allowEvents.Add(evt);
            if (evt is UnifiedAgentEvent.ApprovalRequested req)
            {
                Assert.Equal("acp_perm_99", req.RequestId);
                Assert.Equal("write_file", req.ToolName);
                await session.RespondApprovalAsync(req.RequestId, approved: true, ct: CancellationToken.None);
            }
        }

        Assert.Contains(allowEvents.OfType<UnifiedAgentEvent.TextDelta>(), t => t.Delta == "acp-selected:opt_allow_once");

        // Permission reject selects reject_once optionId
        var rejectEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_PERMISSION", "perm_2"), CancellationToken.None))
        {
            rejectEvents.Add(evt);
            if (evt is UnifiedAgentEvent.ApprovalRequested req)
            {
                await session.RespondApprovalAsync(req.RequestId, approved: false, reason: "denied", ct: CancellationToken.None);
            }
        }

        Assert.Contains(rejectEvents.OfType<UnifiedAgentEvent.TextDelta>(), t => t.Delta == "acp-selected:opt_reject_once");

        // session/cancel stops active prompt cleanly
        var cancelEvents = new List<UnifiedAgentEvent>();
        var cancelSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("WAIT_FOR_CANCEL", "cancel_1"), CancellationToken.None))
        {
            cancelEvents.Add(evt);
            if (!cancelSent && evt is UnifiedAgentEvent.TextDelta)
            {
                cancelSent = true;
                await session.CancelTurnAsync(CancellationToken.None);
            }
        }

        Assert.Single(cancelEvents.OfType<UnifiedAgentEvent.TurnInterrupted>());

        // JSON-RPC error surfaces TurnError with IsRetryable = true on 429
        var rpcErrEvents = await DrainTurnAsync(session, new TurnInput("TRIGGER_RPC_ERROR", "err_1"));
        var rpcErr = Assert.Single(rpcErrEvents.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Equal("rpc_429", rpcErr.Code);
        Assert.True(rpcErr.IsRetryable);
    }

    private static async Task<List<UnifiedAgentEvent>> DrainTurnAsync(IAgentSession session, TurnInput input)
    {
        var list = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(input, CancellationToken.None))
        {
            list.Add(evt);
        }

        return list;
    }

    private sealed class LocalStreamableHttpMcpTestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _expectedAuth;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        public LocalStreamableHttpMcpTestServer(string expectedAuth)
        {
            _expectedAuth = expectedAuth;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            EndpointUrl = $"http://127.0.0.1:{port}/mcp/kernel";
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public string EndpointUrl { get; }
        public volatile bool SawInitialize;
        public volatile bool SawInitializedNotification;
        public volatile bool SawSessionHeaderOnSubsequentCalls;

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch
            {
                // Ignore accept shutdown.
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var headerBytes = new List<byte>();
                var buf = new byte[1];
                while (true)
                {
                    var n = await stream.ReadAsync(buf.AsMemory(0, 1), _cts.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    headerBytes.Add(buf[0]);
                    var c = headerBytes.Count;
                    if (c >= 4 &&
                        headerBytes[c - 4] == '\r' &&
                        headerBytes[c - 3] == '\n' &&
                        headerBytes[c - 2] == '\r' &&
                        headerBytes[c - 1] == '\n')
                    {
                        break;
                    }
                }

                var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
                var headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                for (var i = 1; i < headerLines.Length; i++)
                {
                    var colon = headerLines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        headers[headerLines[i][..colon].Trim()] = headerLines[i][(colon + 1)..].Trim();
                    }
                }

                var contentLen = headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var parsedLen)
                    ? parsedLen
                    : 0;
                var bodyBytes = new byte[contentLen];
                var readTotal = 0;
                while (readTotal < contentLen)
                {
                    var r = await stream.ReadAsync(bodyBytes.AsMemory(readTotal, contentLen - readTotal), _cts.Token);
                    if (r == 0)
                    {
                        break;
                    }

                    readTotal += r;
                }

                if (!headers.TryGetValue("Authorization", out var auth) ||
                    !string.Equals(auth, _expectedAuth, StringComparison.Ordinal))
                {
                    await WriteHttpResponseAsync(stream, "401 Unauthorized", "application/json", "{\"error\":\"unauthorized\"}", extraHeader: null);
                    return;
                }

                using var doc = JsonDocument.Parse(bodyBytes);
                var root = doc.RootElement;
                var method = root.TryGetProperty("method", out var mProp) ? mProp.GetString() : null;
                var hasId = root.TryGetProperty("id", out var idProp);

                if (!hasId)
                {
                    if (method == "notifications/initialized")
                    {
                        SawInitializedNotification = true;
                        if (headers.TryGetValue("Mcp-Session-Id", out var sid) && sid == "mcp_sess_bridge_1")
                        {
                            SawSessionHeaderOnSubsequentCalls = true;
                        }
                    }

                    await WriteHttpResponseAsync(stream, "202 Accepted", "text/plain", string.Empty, extraHeader: null);
                    return;
                }

                var idRaw = idProp.GetRawText();
                if (method == "initialize")
                {
                    SawInitialize = true;
                    var body = $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{{\"protocolVersion\":\"2025-03-26\",\"serverInfo\":{{\"name\":\"perga-kernel-test\",\"version\":\"1.0.0\"}}}}}}";
                    await WriteHttpResponseAsync(
                        stream,
                        "200 OK",
                        "application/json",
                        body,
                        extraHeader: "Mcp-Session-Id: mcp_sess_bridge_1");
                    return;
                }

                if (headers.TryGetValue("Mcp-Session-Id", out var callSid) && callSid == "mcp_sess_bridge_1")
                {
                    SawSessionHeaderOnSubsequentCalls = true;
                }

                if (method == "tools/list")
                {
                    var body = $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{{\"tools\":[{{\"name\":\"perga_report_read\"}}]}}}}";
                    await WriteHttpResponseAsync(stream, "200 OK", "application/json", body, extraHeader: null);
                    return;
                }

                if (method == "tools/call")
                {
                    var rpcJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{{\"content\":[{{\"type\":\"text\",\"text\":\"sse-report-body-ok\"}}]}}}}";
                    var sseBody = $": keepalive\n\nevent: message\ndata: {rpcJson}\n\n";
                    await WriteHttpResponseAsync(stream, "200 OK", "text/event-stream", sseBody, extraHeader: null);
                }
            }
        }

        private static async Task WriteHttpResponseAsync(
            NetworkStream stream,
            string status,
            string contentType,
            string body,
            string? extraHeader)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            sb.Append("Connection: close\r\n");
            if (!string.IsNullOrWhiteSpace(extraHeader))
            {
                sb.Append(extraHeader).Append("\r\n");
            }

            sb.Append("\r\n");
            var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headBytes);
            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes);
            }

            await stream.FlushAsync();
        }
    }
}
