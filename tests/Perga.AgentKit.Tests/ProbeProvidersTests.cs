namespace Perga.AgentKit.Tests;

public sealed class ProbeProvidersTests : IDisposable
{
    private readonly MockCliFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ProbeProvidersAsync_ReportsAvailableMissingFailingAndTimeoutCleanly()
    {
        var options = new AgentRuntimeOptions
        {
            ProbeTimeout = TimeSpan.FromMilliseconds(350),
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(_fixture.MockCodexPath),
                ["claude"] = new(_fixture.MockClaudePath, Models: ["claude-sonnet-4-6-custom"]),
                ["acp:antigravity"] = new("/nonexistent/perga/antigravity-missing"),
                ["acp:opencode"] = new(_fixture.MockFailingVersionPath),
                ["acp:slow-hang"] = new(_fixture.MockSlowVersionPath)
            }
        };

        await using var runtime = new AgentRuntime(options);
        var statuses = await runtime.ProbeProvidersAsync(CancellationToken.None);

        var codex = Assert.Single(statuses, s => s.Provider == "codex");
        Assert.True(codex.Available);
        Assert.Equal("codex-cli 0.99.0-mock", codex.Version);
        Assert.Null(codex.Reason);
        Assert.NotEmpty(codex.Models);

        var claude = Assert.Single(statuses, s => s.Provider == "claude");
        Assert.True(claude.Available);
        Assert.Equal("2.1.0 (Claude Code Mock)", claude.Version);
        Assert.Equal(["claude-sonnet-4-6-custom"], claude.Models);

        var agy = Assert.Single(statuses, s => s.Provider == "acp:antigravity");
        Assert.False(agy.Available);
        Assert.Null(agy.Version);
        Assert.Contains("not found", agy.Reason, StringComparison.OrdinalIgnoreCase);

        var opencode = Assert.Single(statuses, s => s.Provider == "acp:opencode");
        Assert.False(opencode.Available);
        Assert.Contains("fatal configuration error", opencode.Reason, StringComparison.OrdinalIgnoreCase);

        var slow = Assert.Single(statuses, s => s.Provider == "acp:slow-hang");
        Assert.False(slow.Available);
        Assert.Contains("Timed out", slow.Reason, StringComparison.OrdinalIgnoreCase);
    }
}
