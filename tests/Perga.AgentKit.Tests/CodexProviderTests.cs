using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class CodexProviderTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private AgentRuntime CreateRuntime() =>
        new(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(_fixture.MockCodexPath)
            }
        });

    [Theory]
    [InlineData(null)]
    [InlineData("saved-codex")]
    public async Task CompleteConfigurationIsAcceptedAtLaunchAndOrdinaryLiveChangesRequireReopen(string? resumeId)
    {
        var log = Path.Combine(_fixture.RootDir, "codex-launch-selection.jsonl");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            { ["codex"] = new(_fixture.MockCodexPath, Env: new Dictionary<string, string> { ["MOCK_CODEX_LOG"] = log }) }
        });
        var spec = new HarnessLaunchSpec("codex", null, _fixture.WorkspaceDir, resumeId, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "high");
        await using var session = await runtime.OpenSessionAsync(spec, default);
        Assert.Equal(SessionConfiguration.FromLaunch(spec), session.ConfigurationState?.Selection);
        Assert.Equal(ConfigurationEvidence.LaunchAccepted, session.ConfigurationState?.Evidence);
        Assert.Equal(ConfigurationSupport.Unsupported, session.ConfigurationCapabilities.Model.LiveChange);
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await session.ConfigureAsync(SessionConfiguration.FromNullable("next", "low")));
        var thread = Assert.Single(File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)),
            call => call.GetProperty("method").GetString() == (resumeId is null ? "thread/start" : "thread/resume"));
        Assert.Equal("high", thread.GetProperty("params").GetProperty("effort").GetString());
        if (resumeId is not null) Assert.Equal(resumeId, session.NativeThreadId);
    }

    [Fact]
    public async Task ExplicitLaunchEffortPreventsDefaultModelFallback()
    {
        var log = Path.Combine(_fixture.RootDir, "codex-no-fallback.jsonl");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            { ["codex"] = new(_fixture.MockCodexPath, Env: new Dictionary<string, string>
                { ["MOCK_CODEX_LOG"] = log, ["MOCK_CODEX_STALE_DEFAULT_MODEL"] = "stale" }) }
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec("codex", null, _fixture.WorkspaceDir, null,
            null, new Dictionary<string, McpServerConfig>(), ReasoningEffort: "high"), default);
        var events = await DrainTurnAsync(session, new("must fail", "no-fallback"));
        Assert.Single(events.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Empty(events.OfType<UnifiedAgentEvent.TurnCompleted>());
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        Assert.Single(calls, call => call.GetProperty("method").GetString() == "turn/start");
        Assert.DoesNotContain(calls, call => call.GetProperty("method").GetString() == "model/list");
    }

    [Theory]
    [InlineData(null, "high")]
    [InlineData("A", "low")]
    [InlineData("B", null)]
    public async Task OrdinaryTurnSendsLaunchEffortIndependentlyOfModel(string? model, string? effort)
    {
        var log = Path.Combine(_fixture.RootDir, "codex-selection.jsonl");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(_fixture.MockCodexPath, Env: new Dictionary<string, string> { ["MOCK_CODEX_LOG"] = log })
            }
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "codex", model, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: effort), CancellationToken.None);
        await DrainTurnAsync(session, new TurnInput("selected settings", "selection"));
        var call = Assert.Single(File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)),
            call => call.GetProperty("method").GetString() == "turn/start");
        var parameters = call.GetProperty("params");
        Assert.Equal(effort, parameters.TryGetProperty("effort", out var value) ? value.GetString() : null);
        var thread = Assert.Single(File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line)),
            call => call.GetProperty("method").GetString() == "thread/start");
        Assert.Equal(model, thread.GetProperty("params").TryGetProperty("model", out var selection) ? selection.GetString() : null);
    }

    [Fact]
    public async Task WorkerConfigurationUsesLiveModelEffortsAndTheEffectiveDirectoryDefault()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(_fixture.MockCodexPath, Env: new Dictionary<string, string>
                {
                    ["MOCK_CODEX_CONFIG_JSON"] = """{"model":"small","model_reasoning_effort":"medium"}""",
                    ["MOCK_CODEX_MODELS_JSON"] = """
                        {"data":[
                          {"model":"large","displayName":"Large","isDefault":true,"supportedReasoningEfforts":[{"reasoningEffort":"high"}],"defaultReasoningEffort":"high"},
                          {"model":"small","displayName":"Small","isDefault":false,"supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"medium"}],"defaultReasoningEffort":"low"}
                        ]}
                        """
                })
            }
        });
        var spec = new HarnessLaunchSpec("codex", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers(), ConfigureWorker: true);
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        var configuration = Assert.IsType<WorkerProviderConfiguration>(session.WorkerConfiguration);
        Assert.Equal("small", configuration.DefaultModel);
        Assert.Equal("medium", configuration.GetDefaultReasoningEffort(null));
        Assert.Equal(["high"], configuration.GetReasoningEfforts("large"));
        Assert.Equal(["low", "medium"], configuration.GetReasoningEfforts("small"));
    }

    [Theory]
    [InlineData("REJECT_WORKER_CONFIGURATION")]
    [InlineData("REJECT_WORKER_CONFIGURATION_EARLY_EVENT")]
    public async Task WorkerTurnDoesNotAcknowledgeAConfigurationAlreadyRejectedAtStart(string prompt)
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec("codex", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers(), ConfigureWorker: true);
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        var events = await DrainTurnAsync(session, new TurnInput(prompt, "worker-rejection", "large"));
        Assert.DoesNotContain(events, item => item is UnifiedAgentEvent.TurnAccepted);
        Assert.Contains(events, item => item is UnifiedAgentEvent.TurnError { Code: "model_not_found", BeforePrompt: true });
    }

    [Fact]
    public async Task Codex_FullMultiTurnLifecycle_ThreadStartAndResume_McpAndEvents()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec(
            Provider: "codex",
            Model: "gpt-5.4",
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: "Be concise.",
            McpServers: MockCliFixture.CreateSampleMcpServers());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        Assert.True(session.SupportsNativeSteer);
        Assert.Equal("thr_mock_001", session.NativeThreadId);

        var turn1Events = await DrainTurnAsync(session, new TurnInput("hello-turn-1", "corr_1"));
        Assert.Contains(turn1Events, e => e is UnifiedAgentEvent.ThreadBound { NativeThreadId: "thr_mock_001" });
        Assert.Contains(turn1Events, e => e is UnifiedAgentEvent.ThinkingDelta { Delta: "thinking(corr_1)" });
        var started = Assert.Single(turn1Events.OfType<UnifiedAgentEvent.ToolCallStarted>());
        Assert.Equal("perga_report_read", started.Name);
        Assert.True(started.Args.GetProperty("whole").GetBoolean());

        var completed = Assert.Single(turn1Events.OfType<UnifiedAgentEvent.ToolCallCompleted>());
        Assert.Equal(started.CallId, completed.CallId);
        Assert.Equal("report-ok", completed.Output);
        Assert.False(completed.IsError);

        var text = string.Concat(turn1Events.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("thread/start|thr_mock_001|mcp=local-tools,perga|model=gpt-5.4", text, StringComparison.Ordinal);

        var turnDone = Assert.Single(turn1Events.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.NotNull(turnDone.Usage);
        Assert.Equal(120, turnDone.Usage!.InputTokens);
        Assert.Equal(45, turnDone.Usage.OutputTokens);
        Assert.Equal(30, turnDone.Usage.CachedInputTokens);
        Assert.Equal(272000, turnDone.Usage.ContextWindowTokens);

        // Second turn on the same session should not re-emit ThreadBound for unchanged threadId.
        var turn2Events = await DrainTurnAsync(session, new TurnInput("hello-turn-2", "corr_2"));
        Assert.Empty(turn2Events.OfType<UnifiedAgentEvent.ThreadBound>());
        Assert.Single(turn2Events.OfType<UnifiedAgentEvent.TurnCompleted>());

        // Resume existing NativeThreadId in a new session.
        var resumeSpec = spec with { ResumeNativeThreadId = "thr_existing_999" };
        await using var resumed = await runtime.OpenSessionAsync(resumeSpec, CancellationToken.None);
        Assert.Equal("thr_existing_999", resumed.NativeThreadId);
        var resumedEvents = await DrainTurnAsync(resumed, new TurnInput("after-resume", "corr_3"));
        var resumedText = string.Concat(resumedEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("thread/resume|thr_existing_999", resumedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codex_BidirectionalApproval_AcceptAndDecline_AndNumericId()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec(
            "codex",
            "gpt-5.4",
            _fixture.WorkspaceDir,
            null,
            null,
            new Dictionary<string, McpServerConfig>());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        // Turn 1: Approve numeric JSON-RPC request id
        var approvedEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_APPROVAL NUMERIC_ID", "corr_app_1"), CancellationToken.None))
        {
            approvedEvents.Add(evt);
            if (evt is UnifiedAgentEvent.ApprovalRequested req)
            {
                Assert.StartsWith("permission_", req.RequestId, StringComparison.Ordinal);
                Assert.Equal("commandExecution", req.ToolName);
                await session.RespondApprovalAsync(req.RequestId, approved: true, ct: CancellationToken.None);
            }
        }

        var approvedTool = Assert.Single(approvedEvents.OfType<UnifiedAgentEvent.ToolCallCompleted>());
        Assert.False(approvedTool.IsError);
        Assert.Contains("decision=accept", approvedTool.Output, StringComparison.Ordinal);

        // Turn 2: Decline string JSON-RPC request id with reason
        var declinedEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_APPROVAL", "corr_app_2"), CancellationToken.None))
        {
            declinedEvents.Add(evt);
            if (evt is UnifiedAgentEvent.ApprovalRequested req)
            {
                Assert.StartsWith("permission_", req.RequestId, StringComparison.Ordinal);
                await session.RespondApprovalAsync(req.RequestId, approved: false, reason: "unsafe command", ct: CancellationToken.None);
            }
        }

        var declinedTool = Assert.Single(declinedEvents.OfType<UnifiedAgentEvent.ToolCallCompleted>());
        Assert.True(declinedTool.IsError);
        Assert.Contains("decision=decline;reason=unsafe command", declinedTool.Output, StringComparison.Ordinal);

        // Turn 3: CancelTurnAsync during pending approval declines the pending RPC and finishes interrupted
        var cancelApprovalEvents = new List<UnifiedAgentEvent>();
        var cancelSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_APPROVAL_CANCEL", "corr_app_cancel"), CancellationToken.None))
        {
            cancelApprovalEvents.Add(evt);
            if (!cancelSent && evt is UnifiedAgentEvent.ApprovalRequested)
            {
                cancelSent = true;
                await session.CancelTurnAsync(CancellationToken.None);
            }
        }

        Assert.True(cancelSent);
        Assert.Single(cancelApprovalEvents.OfType<UnifiedAgentEvent.TurnInterrupted>());
    }

    [Fact]
    public async Task Codex_NativeSteer_Interrupt_AndTurnError()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec(
            "codex",
            null,
            _fixture.WorkspaceDir,
            null,
            null,
            new Dictionary<string, McpServerConfig>());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        // Steer when idle returns false
        Assert.Equal(SteerDelivery.Declined, await session.SteerAsync("corr_steer", "idle-steer", CancellationToken.None));

        // Steer mid-turn succeeds and injects steering message
        var steerEvents = new List<UnifiedAgentEvent>();
        var steeredSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("WAIT_FOR_STEER", "corr_steer"), CancellationToken.None))
        {
            steerEvents.Add(evt);
            if (!steeredSent && evt is UnifiedAgentEvent.TextDelta { Delta: "initial-plan;" })
            {
                steeredSent = true;
                var delivery = await session.SteerAsync("corr_steer", "focus-on-tests", CancellationToken.None);
                Assert.Equal(SteerDelivery.Accepted, delivery);
            }
        }

        var combinedSteerText = string.Concat(steerEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("steered:focus-on-tests", combinedSteerText, StringComparison.Ordinal);
        Assert.Single(steerEvents.OfType<UnifiedAgentEvent.TurnCompleted>());

        // CancelTurnAsync interrupts active turn cleanly
        var interruptEvents = new List<UnifiedAgentEvent>();
        var interruptSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("WAIT_FOR_INTERRUPT", "corr_int"), CancellationToken.None))
        {
            interruptEvents.Add(evt);
            if (!interruptSent && evt is UnifiedAgentEvent.TextDelta)
            {
                interruptSent = true;
                await session.CancelTurnAsync(CancellationToken.None);
            }
        }

        Assert.Single(interruptEvents.OfType<UnifiedAgentEvent.TurnInterrupted>());

        // TurnError surfaces structured error code, message, and retryability
        var errorEvents = await DrainTurnAsync(session, new TurnInput("TRIGGER_ERROR", "corr_err"));
        var turnError = Assert.Single(errorEvents.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Equal("rate_limit_exceeded", turnError.Code);
        Assert.Contains("quota exceeded", turnError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(turnError.IsRetryable);
    }

    [Fact]
    public async Task Codex_StaleDefaultConfigModel_AutomaticallyQueriesModelListAndRetriesWithSupportedModel()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(
                    _fixture.MockCodexPath,
                    Env: new Dictionary<string, string>
                    {
                        ["MOCK_CODEX_STALE_DEFAULT_MODEL"] = "gpt-6.1-sol",
                        // Even when gpt-6.1-sol appears first in model/list, parser skips the rejected model!
                        ["MOCK_CODEX_MODELS_JSON"] =
                            """{"data":[{"id":"gpt-6.1-sol","model":"gpt-6.1-sol"},{"id":"gpt-6.2-sol","model":"gpt-6.2-sol","isDefault":true}]}"""
                    })
            }
        });

        var spec = new HarnessLaunchSpec(
            Provider: "codex",
            Model: null,
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: new Dictionary<string, McpServerConfig>());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        // Turn 1: initial turn/start with null model hits gpt-6.1-sol model_not_found, queries model/list, and succeeds with gpt-6.2-sol!
        var turn1Events = await DrainTurnAsync(session, new TurnInput("hello-fallback-1", "corr_fb_1"));
        Assert.Empty(turn1Events.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Single(turn1Events.OfType<UnifiedAgentEvent.TurnCompleted>());
        var text1 = string.Concat(turn1Events.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("model=gpt-6.2-sol]:hello-fallback-1", text1, StringComparison.Ordinal);

        // Turn 2: reuses cached fallback model directly without failing first
        var turn2Events = await DrainTurnAsync(session, new TurnInput("hello-fallback-2", "corr_fb_2"));
        Assert.Empty(turn2Events.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Single(turn2Events.OfType<UnifiedAgentEvent.TurnCompleted>());
        var text2 = string.Concat(turn2Events.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("model=gpt-6.2-sol]:hello-fallback-2", text2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Codex_NestedHttp400ModelNotFoundError_UnwrapsCrispTurnErrorWhenUnrecoverable()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(
                    _fixture.MockCodexPath,
                    Env: new Dictionary<string, string>
                    {
                        ["MOCK_CODEX_REJECT_ALL_MODELS"] = "1"
                    })
            }
        });

        var spec = new HarnessLaunchSpec(
            Provider: "codex",
            Model: null,
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: null,
            McpServers: new Dictionary<string, McpServerConfig>());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        var events = await DrainTurnAsync(session, new TurnInput("hello-unrecoverable", "corr_fb_err"));
        var err = Assert.Single(events.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Equal("model_not_found", err.Code);
        Assert.Contains("is not supported when using Codex with a ChatGPT account", err.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"type\":\"error\"", err.Message, StringComparison.Ordinal);
        Assert.False(err.IsRetryable);
    }

    [Fact]
    public async Task EarlyCompletionIsAppliedOnlyToTheTurnReturnedByStart()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec("codex", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers());
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<UnifiedAgentEvent>();
        await foreach (var item in session.StreamTurnAsync(new("COMPLETE_BEFORE_START_REPLY", "early"), deadline.Token))
            events.Add(item);

        var accepted = Assert.Single(events.OfType<UnifiedAgentEvent.TurnAccepted>());
        Assert.Equal("turn_1", accepted.NativeTurnId);
        Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.DoesNotContain(events, item => item is UnifiedAgentEvent.TurnInterrupted);
    }

    [Fact]
    public async Task EarlyModelFailureCannotCloseTheFallbackTurn()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec("codex", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers());
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<UnifiedAgentEvent>();
        await foreach (var item in session.StreamTurnAsync(new("FAIL_BEFORE_START_REPLY", "fallback"), deadline.Token))
            events.Add(item);

        Assert.Contains(events, item => item is UnifiedAgentEvent.TurnAccepted { NativeTurnId: "turn_2" });
        Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.DoesNotContain(events, item => item is UnifiedAgentEvent.TurnError);
    }

    [Fact]
    public async Task NativeCompact_UsesThreadCompactStartAndReturnsUpdatedUsage()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec("codex", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers());
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        Assert.True(session.SupportsNativeCompact);
        var result = await session.CompactAsync(CancellationToken.None);
        Assert.NotNull(result.Usage);
        Assert.Equal(1_200, result.Usage!.InputTokens);
        Assert.Equal(100_000, result.Usage.ContextWindowTokens);
    }

    [Fact]
    public async Task NativeCompact_SettlesPendingCompactOnDisposeAsync()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new ProviderCommandOverride(
                    "python3",
                    PrependArgs: [_fixture.MockCodexPath],
                    Env: new Dictionary<string, string> { ["MOCK_CODEX_HANG_COMPACT"] = "1" })
            }
        });
        var spec = new HarnessLaunchSpec("codex", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers());
        var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        var compactTask = session.CompactAsync(CancellationToken.None);
        await Task.Delay(100);
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await compactTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task NativeCompact_CanceledBeforeCompletionRefusesOverlappingSubsequentCompact()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = new ProviderCommandOverride(
                    "python3",
                    PrependArgs: [_fixture.MockCodexPath],
                    Env: new Dictionary<string, string> { ["MOCK_CODEX_DELAY_COMPACT_MS"] = "600" })
            }
        });
        var spec = new HarnessLaunchSpec("codex", null, _fixture.WorkspaceDir, null, null,
            MockCliFixture.CreateSampleMcpServers());
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.CompactAsync(cts.Token));

        // Second compaction must not be prematurely completed by the first compaction's late turn/completed.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.CompactAsync(CancellationToken.None));
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
}
