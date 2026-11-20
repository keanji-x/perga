using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class ClaudeProviderTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private AgentRuntime CreateRuntime() =>
        new(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["claude"] = new(_fixture.MockClaudePath)
            }
        });

    [Theory]
    [InlineData(null, "low")]
    [InlineData(null, "medium")]
    [InlineData(null, "high")]
    [InlineData("resumed-claude", "xhigh")]
    [InlineData("resumed-claude", "max")]
    public async Task ExplicitEffortWithDefaultModelIsPassedSeparatelyOnOpenAndResume(string? resumeId, string effort)
    {
        var log = Path.Combine(_fixture.RootDir, "claude-args.json");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["claude"] = new(_fixture.MockClaudePath, Env: new Dictionary<string, string> { ["MOCK_CLAUDE_ARGS_LOG"] = log })
            }
        });
        var spec = new HarnessLaunchSpec("claude", null, _fixture.WorkspaceDir, resumeId, null, new Dictionary<string, McpServerConfig>(), ReasoningEffort: effort);
        await using var session = await runtime.OpenSessionAsync(spec, default);
        await DrainTurnAsync(session, new("capture args", "effort"));
        var args = JsonSerializer.Deserialize<string[]>(File.ReadAllText(log))!;
        Assert.DoesNotContain("--model", args);
        Assert.Equal(effort, args[Array.IndexOf(args, "--effort") + 1]);
        if (resumeId is not null) Assert.Equal(resumeId, args[Array.IndexOf(args, "--resume") + 1]);
        Assert.Equal(SessionConfiguration.FromLaunch(spec), session.ConfigurationState?.Selection);
        Assert.Equal(ConfigurationEvidence.LaunchAccepted, session.ConfigurationState?.Evidence);
        Assert.IsType<SessionConfigurationOutcome.RequiresReopen>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "other")));
    }

    [Fact]
    public async Task StartupRejectsExplicitEffortBeforeReturningConfirmedSession()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec("claude", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "ultra");
        var error = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(spec, default));
        Assert.IsType<SessionConfigurationOutcome.Rejected>(error.Outcome);
    }

    [Fact]
    public async Task MissingEffortSupportIsExplicitAndDoesNotLaunchExplicitEffort()
    {
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["claude"] = new(_fixture.MockClaudePath, Env: new Dictionary<string, string> { ["MOCK_CLAUDE_NO_EFFORT"] = "1" })
            }
        });
        var spec = new HarnessLaunchSpec("claude", null, _fixture.WorkspaceDir, null, null, new Dictionary<string, McpServerConfig>());
        await using var session = await runtime.OpenSessionAsync(spec, default);
        Assert.Equal(ConfigurationSupport.Unsupported, session.ConfigurationCapabilities.Effort.Open);
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(await session.ConfigureAsync(SessionConfiguration.FromNullable(null, "high")));
        var error = await Assert.ThrowsAsync<SessionConfigurationException>(() => runtime.OpenSessionAsync(spec with { ReasoningEffort = "high" }, default));
        Assert.IsType<SessionConfigurationOutcome.Unsupported>(error.Outcome);
    }

    [Fact]
    public async Task Claude_MultiTurnStreamJson_McpTempFileCleanup_Resume_AndNoDuplicateDeltas()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec(
            Provider: "claude",
            Model: "claude-sonnet-4-6",
            Cwd: _fixture.WorkspaceDir,
            ResumeNativeThreadId: null,
            SystemPrompt: "Follow system rules",
            McpServers: MockCliFixture.CreateSampleMcpServers());

        string mcpTempFilePath;
        await using (var session = await runtime.OpenSessionAsync(spec, CancellationToken.None))
        {
            Assert.False(session.SupportsNativeSteer);
            Assert.Equal(SteerDelivery.Declined, await session.SteerAsync("turn", "ignored", CancellationToken.None));

            var claudeSession = Assert.IsType<ClaudeAgentSession>(session);
            mcpTempFilePath = claudeSession.McpConfigFilePath;
            Assert.True(File.Exists(mcpTempFilePath));

            using var mcpDoc = JsonDocument.Parse(await File.ReadAllTextAsync(mcpTempFilePath));
            var serversElem = mcpDoc.RootElement.GetProperty("mcpServers");
            Assert.Equal("http", serversElem.GetProperty("perga").GetProperty("type").GetString());
            Assert.Equal("stdio", serversElem.GetProperty("local-tools").GetProperty("type").GetString());

            var turn1 = await DrainTurnAsync(session, new TurnInput("first-claude-turn", "c_1"));
            Assert.Contains(turn1, e => e is UnifiedAgentEvent.ThreadBound { NativeThreadId: "claude_sess_mock_42" });
            Assert.Equal("claude_sess_mock_42", session.NativeThreadId);

            // Verify stream_event + assistant summary did NOT duplicate text or thinking deltas!
            var thinkingChunks = turn1.OfType<UnifiedAgentEvent.ThinkingDelta>().Select(t => t.Delta).ToArray();
            Assert.Equal(["claude-think-1"], thinkingChunks);

            var textChunks = turn1.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta).ToArray();
            var singleText = Assert.Single(textChunks);
            Assert.Contains("claude[claude_sess_mock_42|model=claude-sonnet-4-6|sys=Follow system rules|mcp=local-tools,perga]:first-claude-turn", singleText, StringComparison.Ordinal);

            var toolStart = Assert.Single(turn1.OfType<UnifiedAgentEvent.ToolCallStarted>());
            Assert.Equal("perga_task_ls", toolStart.Name);

            var toolDone = Assert.Single(turn1.OfType<UnifiedAgentEvent.ToolCallCompleted>());
            Assert.Equal(toolStart.CallId, toolDone.CallId);
            Assert.Equal("tasks-listed-ok", toolDone.Output);
            Assert.False(toolDone.IsError);

            var completed = Assert.Single(turn1.OfType<UnifiedAgentEvent.TurnCompleted>());
            Assert.NotNull(completed.Usage);
            Assert.Equal(150, completed.Usage!.InputTokens);
            Assert.Equal(60, completed.Usage.OutputTokens);
            Assert.Equal(25, completed.Usage.CachedInputTokens);
            Assert.Equal(200000, completed.Usage.ContextWindowTokens);

            // Second turn reuses stdin/stdout stream seamlessly
            var turn2 = await DrainTurnAsync(session, new TurnInput("second-claude-turn", "c_2"));
            Assert.Empty(turn2.OfType<UnifiedAgentEvent.ThreadBound>());
            Assert.Contains(turn2.OfType<UnifiedAgentEvent.TextDelta>(), t => t.Delta.Contains("second-claude-turn", StringComparison.Ordinal));
        }

        // Verify <tempJson> was deleted on DisposeAsync!
        Assert.False(File.Exists(mcpTempFilePath));

        // Verify --resume passes NativeThreadId cleanly
        var resumeSpec = spec with { ResumeNativeThreadId = "claude_resumed_77" };
        await using var resumed = await runtime.OpenSessionAsync(resumeSpec, CancellationToken.None);
        Assert.Equal("claude_resumed_77", resumed.NativeThreadId);
        var resumedEvents = await DrainTurnAsync(resumed, new TurnInput("check-resume", "c_3"));
        Assert.Contains(resumedEvents, e => e is UnifiedAgentEvent.ThreadBound { NativeThreadId: "claude_resumed_77" });
    }

    [Theory]
    [InlineData("MODEL")]
    [InlineData("ASSISTANT")]
    [InlineData("RESULT")]
    [InlineData("RESULT_WITH_ASSISTANT")]
    public async Task Claude_ReportsExplicitContextWindowOnlyInTheTurnThatSuppliedIt(string source)
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec("claude", null, _fixture.WorkspaceDir, null, null,
            new Dictionary<string, McpServerConfig>());
        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        foreach (var (prompt, expectedWindow) in new[]
                 {
                     ("CONTEXT_CLAUDE", (long?)200000),
                     ($"WINDOW_UPDATE_{source}", 1000000L),
                     ("CONTEXT_CLAUDE_CACHED_WINDOW", (long?)null)
                 })
        {
            var events = await DrainTurnAsync(session, new TurnInput(prompt, prompt));
            var completed = Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
            Assert.NotNull(completed.Usage);
            Assert.Equal(expectedWindow, completed.Usage.ContextWindowTokens);
            var updates = events.OfType<UnifiedAgentEvent.ContextUsageUpdated>().ToArray();
            Assert.NotEmpty(updates);
            Assert.Equal(completed.Usage, updates[^1].Usage);
            Assert.Equal(source == "RESULT" && prompt.StartsWith("WINDOW_UPDATE_", StringComparison.Ordinal)
                ? 9000L : 200L, completed.Usage.InputTokens);
        }
    }

    [Fact]
    public async Task Claude_ControlApproval_SigintCancel_AndTurnError()
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec(
            "claude",
            null,
            _fixture.WorkspaceDir,
            null,
            null,
            new Dictionary<string, McpServerConfig>());

        await using var session = await runtime.OpenSessionAsync(spec, CancellationToken.None);

        // Control request approval: allow
        var allowEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_APPROVAL", "c_allow"), CancellationToken.None))
        {
            allowEvents.Add(evt);
            if (evt is UnifiedAgentEvent.ApprovalRequested req)
            {
                Assert.StartsWith("permission_", req.RequestId, StringComparison.Ordinal);
                Assert.Equal("Bash", req.ToolName);
                await session.RespondApprovalAsync(req.RequestId, approved: true, ct: CancellationToken.None);
            }
        }

        var allowText = string.Concat(allowEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("claude-approval:allow|updatedInput=True", allowText, StringComparison.Ordinal);

        // Control request approval: deny with reason
        var denyEvents = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_APPROVAL", "c_deny"), CancellationToken.None))
        {
            denyEvents.Add(evt);
            if (evt is UnifiedAgentEvent.ApprovalRequested req)
            {
                await session.RespondApprovalAsync(req.RequestId, approved: false, reason: "blocked by policy", ct: CancellationToken.None);
            }
        }

        var denyText = string.Concat(denyEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("claude-approval:deny|updatedInput=False|msg=blocked by policy", denyText, StringComparison.Ordinal);

        // CancelTurnAsync during pending approval denies the pending control_request so the turn settles cleanly
        var cancelApprovalEvents = new List<UnifiedAgentEvent>();
        var cancelApprovalSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("REQUIRE_APPROVAL_CANCEL", "c_cancel_approval"), CancellationToken.None))
        {
            cancelApprovalEvents.Add(evt);
            if (!cancelApprovalSent && evt is UnifiedAgentEvent.ApprovalRequested)
            {
                cancelApprovalSent = true;
                await session.CancelTurnAsync(CancellationToken.None);
            }
        }

        Assert.True(cancelApprovalSent);
        Assert.Single(cancelApprovalEvents.OfType<UnifiedAgentEvent.TurnInterrupted>());

        // CancelTurnAsync sends SIGINT and finishes turn cleanly while keeping session usable!
        var sigintEvents = new List<UnifiedAgentEvent>();
        var sigintSent = false;
        await foreach (var evt in session.StreamTurnAsync(new TurnInput("WAIT_FOR_SIGINT", "c_sigint"), CancellationToken.None))
        {
            sigintEvents.Add(evt);
            if (!sigintSent && evt is UnifiedAgentEvent.TextDelta)
            {
                sigintSent = true;
                await session.CancelTurnAsync(CancellationToken.None);
            }
        }

        var sigintErr = Assert.Single(sigintEvents.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Contains("Interrupted", sigintErr.Message, StringComparison.OrdinalIgnoreCase);

        // TurnError on error_max_turns
        var errEvents = await DrainTurnAsync(session, new TurnInput("TRIGGER_ERROR", "c_err"));
        var err = Assert.Single(errEvents.OfType<UnifiedAgentEvent.TurnError>());
        Assert.Equal("error_max_turns", err.Code);
        Assert.Contains("Maximum turns", err.Message, StringComparison.Ordinal);

        Assert.True(session.SupportsNativeCompact);
        var compact = await session.CompactAsync(CancellationToken.None);
        Assert.Equal("Compacted.", compact.Summary);
        Assert.NotNull(compact.Usage);
        Assert.Equal(500, compact.Usage!.InputTokens);
        Assert.Equal(200_000, compact.Usage.ContextWindowTokens);
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
