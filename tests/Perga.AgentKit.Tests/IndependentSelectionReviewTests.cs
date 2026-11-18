using System.Text.Json;

namespace Perga.AgentKit.Tests;

public sealed class IndependentSelectionReviewTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("stored-session")]
    public async Task ExplicitAdvertisedRawIdMustSurviveGroupCollision(string? resumeId)
    {
        using var fixture = new MockCliFixture();
        var log = Path.Combine(fixture.RootDir, "raw-id.jsonl");
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["acp:custom"] = new(fixture.MockAcpPath, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_LOG"] = log,
                    ["MOCK_ACP_STRICT_MODELS"] = "1",
                    ["MOCK_ACP_MODELS"] = JsonSerializer.Serialize(new { models = new {
                        currentModelId = "vendor/model-high", availableModels = new[] {
                            new { modelId = "vendor/model-high" }, new { modelId = "vendor/model-high-low" } } } })
                })
            }
        });
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:custom", "vendor/model-high", fixture.WorkspaceDir, resumeId, null,
            new Dictionary<string, McpServerConfig>()), CancellationToken.None);
        Assert.True(await session.TrySetModelAsync("vendor/model-high"));
        var calls = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.GetProperty("method").GetString() == "session/set_model").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal("vendor/model-high", call.GetProperty("params").GetProperty("modelId").GetString()));
    }

    [Theory]
    [InlineData(null, "vendor/model-high", false)]
    [InlineData("stored-session", "vendor/model-high-low", false)]
    [InlineData(null, "vendor/model-high-high", true)]
    [InlineData("stored-session", null, true)]
    public async Task CollidingCatalogExposesSelectableRawVariants(
        string? resumeId, string? currentId, bool reverse)
    {
        using var fixture = new MockCliFixture();
        var log = Path.Combine(fixture.RootDir, "catalog-collision.jsonl");
        var ids = new[] { "vendor/model-high", "vendor/model-high-low", "vendor/model-high-high" };
        if (reverse) Array.Reverse(ids);
        await using var runtime = new AgentRuntime(new AgentRuntimeOptions
        {
            ProviderOverrides = new Dictionary<string, ProviderCommandOverride>
            {
                ["codex"] = new(Path.Combine(fixture.RootDir, "unavailable-codex")),
                ["claude"] = new(Path.Combine(fixture.RootDir, "unavailable-claude")),
                ["acp:antigravity"] = new(Path.Combine(fixture.RootDir, "unavailable-antigravity")),
                ["acp:opencode"] = new(fixture.MockAcpPath, Env: new Dictionary<string, string>
                {
                    ["MOCK_ACP_LOG"] = log,
                    ["MOCK_ACP_STRICT_MODELS"] = "1",
                    ["MOCK_ACP_MODELS"] = JsonSerializer.Serialize(new { models = new {
                        currentModelId = currentId, availableModels = ids.Select(id => new {
                            modelId = id, name = id + " display", description = id + " description" }) } })
                })
            }
        });
        var status = Assert.Single(await runtime.ProbeProvidersAsync(CancellationToken.None),
            p => p.Provider == "acp:opencode");
        var catalog = status.ModelCatalog!;
        Assert.Equal(3, catalog.Count);
        Assert.Equal(3, catalog.Select(m => m.Model).Distinct().Count());
        var normal = Assert.Single(catalog, m => m.Model == "vendor/model");
        Assert.Equal("high", normal.DefaultReasoningEffort);
        Assert.Equal("high", Assert.Single(normal.SupportedReasoningEfforts!).ReasoningEffort);
        var defaultModel = currentId is null ? "vendor/model-high-low"
            : currentId == "vendor/model-high" ? "vendor/model" : currentId;
        Assert.Equal(defaultModel, Assert.Single(catalog, m => m.IsDefault).Model);
        foreach (var rawId in new[] { "vendor/model-high-low", "vendor/model-high-high" })
        {
            var raw = Assert.Single(catalog, m => m.Model == rawId);
            Assert.Equal(rawId + " display", raw.DisplayName);
            Assert.Equal(rawId + " description", raw.Description);
            Assert.Null(raw.DefaultReasoningEffort);
            Assert.Empty(raw.SupportedReasoningEfforts!);
        }

        File.WriteAllText(log, ""); // Exclude discovery's temporary session.
        await using var session = await runtime.OpenSessionAsync(new HarnessLaunchSpec(
            "acp:opencode", normal.Model, fixture.WorkspaceDir, resumeId, null,
            new Dictionary<string, McpServerConfig>(), ReasoningEffort: "high"), CancellationToken.None);
        foreach (var selection in catalog)
        {
            Assert.True(await session.TrySetModelAsync(selection.Model));
            Assert.True(await session.TrySetModelAsync(selection.Model, "high"));
        }
        // The advertised ID retains its identity even with an effort matching the other group.
        Assert.True(await session.TrySetModelAsync("vendor/model-high", "low"));
        var actual = File.ReadAllLines(log).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
            .Where(call => call.GetProperty("method").GetString() == "session/set_model")
            .Select(call => call.GetProperty("params").GetProperty("modelId").GetString()).ToArray();
        var expected = new List<string> { "vendor/model-high" };
        foreach (var selection in catalog)
        {
            var rawId = selection.Model == normal.Model ? "vendor/model-high" : selection.Model;
            expected.Add(rawId);
            expected.Add(rawId);
        }
        expected.Add("vendor/model-high");
        Assert.Equal(expected, actual);
    }
}
