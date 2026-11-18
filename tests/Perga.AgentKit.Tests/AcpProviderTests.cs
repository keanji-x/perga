namespace Perga.AgentKit.Tests;

public sealed class AcpProviderTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private AgentRuntime CreateRuntime() =>
        new(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:antigravity"] = new(_fixture.MockAcpPath),
                ["acp:opencode"] = new(_fixture.MockAcpPath)
            }
        });

    [Theory]
    [InlineData("acp:antigravity", true)]
    [InlineData("acp:opencode", true)]
    public async Task AcpBuiltInSurfaces_SessionNewAndLoad_McpAndUpdates(string provider, bool expectsAcpSubcommand)
    {
        await using var runtime = CreateRuntime();
        var spec = new HarnessLaunchSpec(
            Provider: provider,
            Model: "gemini-3.1-pro",
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
        Assert.Contains($"acp[session/new|acp_sess_001|sub={expectsAcpSubcommand}|mcp=local-tools,perga]:hello-acp", text, StringComparison.Ordinal);

        var done = Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>());
        Assert.NotNull(done.Usage);
        Assert.Equal(200, done.Usage!.InputTokens);
        Assert.Equal(75, done.Usage.OutputTokens);
        Assert.Equal(40, done.Usage.CachedInputTokens);
        Assert.Equal(1048576, done.Usage.ContextWindowTokens);

        // Resume via session/load
        var resumeSpec = spec with { ResumeNativeThreadId = "acp_loaded_88" };
        await using var loadedSession = await runtime.OpenSessionAsync(resumeSpec, CancellationToken.None);
        Assert.Equal("acp_loaded_88", loadedSession.NativeThreadId);
        var loadedEvents = await DrainTurnAsync(loadedSession, new TurnInput("resumed-prompt", "acp_corr_2"));
        var loadedText = string.Concat(loadedEvents.OfType<UnifiedAgentEvent.TextDelta>().Select(t => t.Delta));
        Assert.Contains("acp[session/load|acp_loaded_88", loadedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcpGenericExecutableSurface_PermissionRequestCancelAndRpcError()
    {
        // Generic "acp:<cmd>" works out-of-the-box without requiring ProviderOverrides!
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
}
