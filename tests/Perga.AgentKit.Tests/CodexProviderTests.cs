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
                Assert.Equal("701", req.RequestId);
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
                Assert.Equal("codex_req_1", req.RequestId);
                await session.RespondApprovalAsync(req.RequestId, approved: false, reason: "unsafe command", ct: CancellationToken.None);
            }
        }

        var declinedTool = Assert.Single(declinedEvents.OfType<UnifiedAgentEvent.ToolCallCompleted>());
        Assert.True(declinedTool.IsError);
        Assert.Contains("decision=decline;reason=unsafe command", declinedTool.Output, StringComparison.Ordinal);
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
        Assert.False(await session.TrySteerAsync("idle-steer"));

        // Steer mid-turn succeeds and injects steering message
        var steerEvents = new List<UnifiedAgentEvent>();
        var steeredSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("WAIT_FOR_STEER", "corr_steer"), CancellationToken.None))
        {
            steerEvents.Add(evt);
            if (!steeredSent && evt is UnifiedAgentEvent.TextDelta { Delta: "initial-plan;" })
            {
                steeredSent = true;
                var ok = await session.TrySteerAsync("focus-on-tests", CancellationToken.None);
                Assert.True(ok);
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
