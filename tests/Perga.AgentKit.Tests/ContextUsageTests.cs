namespace Perga.AgentKit.Tests;

public sealed class ContextUsageTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private AgentRuntime CreateRuntime() => new(new AgentRuntimeOptions
    {
        ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
        {
            ["codex"] = new(_fixture.MockCodexPath),
            ["claude"] = new(_fixture.MockClaudePath),
            ["acp:antigravity"] = new(_fixture.MockAcpPath)
        }
    });

    private Task<IAgentSession> OpenAsync(AgentRuntime runtime, string provider) =>
        runtime.OpenSessionAsync(new HarnessLaunchSpec(provider, null, _fixture.WorkspaceDir,
            null, null, new Dictionary<string, McpServerConfig>()), CancellationToken.None);

    private static async Task<List<UnifiedAgentEvent>> TurnAsync(IAgentSession session, string prompt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = new List<UnifiedAgentEvent>();
        await foreach (var evt in session.StreamTurnAsync(new TurnInput(prompt, prompt), timeout.Token))
            events.Add(evt);
        return events;
    }

    [Fact]
    public void TokenUsageSupportsLegacyConstructionAndExplicitSnapshots()
    {
        var legacy = new TokenUsage(100, 20, 40, 200000);
        var (input, output, cached, window) = legacy;
        Assert.Equal((100L, 20L, 40L, (long?)200000), (input, output, cached, window));
        Assert.Null(legacy.UsedTokens);
        Assert.Equal(120, legacy.EffectiveUsedTokens);
        Assert.Equal(500, (legacy with { UsedTokens = 500 }).EffectiveUsedTokens);
        Assert.Equal(0, (legacy with { UsedTokens = 0 }).EffectiveUsedTokens);
    }

    [Fact]
    public async Task CodexNeverUsesCumulativeTotalWithoutLast()
    {
        await using var runtime = CreateRuntime();
        await using var session = await OpenAsync(runtime, "codex");
        var events = await TurnAsync(session, "CONTEXT_CODEX_TOTAL_ONLY");
        Assert.Null(Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>()).Usage);
        Assert.Empty(events.OfType<UnifiedAgentEvent.ContextUsageUpdated>());
    }

    [Fact]
    public async Task CodexPreservesWindowAcrossStreamingAndCompletionUsage()
    {
        await using var runtime = CreateRuntime();
        await using var session = await OpenAsync(runtime, "codex");
        var events = await TurnAsync(session, "CONTEXT_CODEX_MERGE");
        var usage = Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>()).Usage;
        Assert.NotNull(usage);
        Assert.Equal(140, usage.InputTokens);
        Assert.Equal(272000, usage.ContextWindowTokens);
        Assert.Equal(195, usage.EffectiveUsedTokens);
        var updates = events.OfType<UnifiedAgentEvent.ContextUsageUpdated>().ToArray();
        Assert.Equal(2, updates.Length);
        Assert.Equal(120, updates[0].Usage.InputTokens);
        Assert.Equal(165, updates[0].Usage.UsedTokens);
        Assert.Equal(130, updates[1].Usage.InputTokens);
        Assert.All(updates, update => Assert.Equal(272000, update.Usage.ContextWindowTokens));
        Assert.IsType<UnifiedAgentEvent.TurnCompleted>(events[^1]);
    }

    [Fact]
    public async Task ClaudeUsesLastAssistantSnapshotAndCachesWindowBetweenTurns()
    {
        await using var runtime = CreateRuntime();
        await using var session = await OpenAsync(runtime, "claude");
        foreach (var prompt in new[] { "CONTEXT_CLAUDE", "CONTEXT_CLAUDE_CACHED_WINDOW" })
        {
            var events = await TurnAsync(session, prompt);
            var usage = Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>()).Usage;
            Assert.NotNull(usage);
            Assert.Equal(200, usage.InputTokens);
            Assert.Equal(200000, usage.ContextWindowTokens);
            Assert.Equal(20, usage.OutputTokens);
            Assert.Equal(400, usage.CachedInputTokens);
            Assert.Equal(920, usage.UsedTokens);
            var updates = events.OfType<UnifiedAgentEvent.ContextUsageUpdated>().ToArray();
            Assert.Equal(prompt == "CONTEXT_CLAUDE" ? 1 : 3, updates.Length);
            Assert.Equal(usage, updates[^1].Usage);
            if (updates.Length == 3)
            {
                Assert.Equal(820, updates[0].Usage.EffectiveUsedTokens);
                Assert.Equal(920, updates[1].Usage.EffectiveUsedTokens);
            }
            Assert.IsType<UnifiedAgentEvent.TurnCompleted>(events[^1]);
        }
        // A new turn without an assistant frame must not reuse the previous snapshot.
        var fallback = await TurnAsync(session, "CONTEXT_CLAUDE_FALLBACK");
        var fallbackUsage = Assert.Single(fallback.OfType<UnifiedAgentEvent.TurnCompleted>()).Usage;
        Assert.NotNull(fallbackUsage);
        Assert.Equal(15000, fallbackUsage.EffectiveUsedTokens);
        Assert.Equal(200000, fallbackUsage.ContextWindowTokens);
        Assert.Equal(fallbackUsage, Assert.Single(fallback.OfType<UnifiedAgentEvent.ContextUsageUpdated>()).Usage);
    }

    [Theory]
    [InlineData("CONTEXT_ACP_USED")]
    [InlineData("CONTEXT_ACP_STANDARD")]
    [InlineData("CONTEXT_ACP_MERGE")]
    public async Task AcpPreservesUsageUpdateWindowAtCompletion(string prompt)
    {
        await using var runtime = CreateRuntime();
        await using var session = await OpenAsync(runtime, "acp:antigravity");
        var events = await TurnAsync(session, prompt);
        var usage = Assert.Single(events.OfType<UnifiedAgentEvent.TurnCompleted>()).Usage;
        Assert.NotNull(usage);
        Assert.Equal(1048576, usage.ContextWindowTokens);
        var updates = events.OfType<UnifiedAgentEvent.ContextUsageUpdated>().ToArray();
        Assert.Equal(prompt == "CONTEXT_ACP_STANDARD" ? 2 : 1, updates.Length);
        Assert.Equal(1234, updates[0].Usage.UsedTokens);
        Assert.Equal(1234, updates[0].Usage.EffectiveUsedTokens);
        Assert.All(updates, update => Assert.Equal(1048576, update.Usage.ContextWindowTokens));
        if (updates.Length == 2)
        {
            Assert.Equal(275, updates[1].Usage.EffectiveUsedTokens);
            Assert.Equal(40, updates[1].Usage.CachedInputTokens);
        }
        Assert.Equal(prompt == "CONTEXT_ACP_USED" ? 1234 : 290, usage.EffectiveUsedTokens);
        Assert.IsType<UnifiedAgentEvent.TurnCompleted>(events[^1]);
    }
}
