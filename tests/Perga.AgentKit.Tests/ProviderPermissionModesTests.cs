using Perga.AgentKit;

namespace Perga.AgentKit.Tests;

public sealed class ProviderPermissionModesTests
{
    [Theory]
    [InlineData("codex", "full", "codex", true, true, "full")]
    [InlineData("codex", "never", "codex", true, true, "never")]
    [InlineData("claude", "bypassPermissions", "claude", true, true, "bypassPermissions")]
    [InlineData("claude", "acceptEdits", "claude", true, true, "acceptEdits")]
    // Claude "plan" is read-only planning: not inherited onto read_write tasks, preserved for collaborators.
    [InlineData("claude", "plan", "claude", true, true, "dontAsk")]
    [InlineData("claude", "plan", "claude", true, false, "plan")]
    // Cross-provider explicit Full access mapping.
    [InlineData("codex", "full", "claude", true, true, "bypassPermissions")]
    [InlineData("claude", "bypassPermissions", "codex", true, true, "full")]
    [InlineData("codex", "full", "acp:antigravity", true, true, null)]
    [InlineData("acp:opencode", "bypassPermissions", "codex", true, true, "full")]
    [InlineData("acp:antigravity", "bypassPermissions", "codex", true, true, "full")]
    // Cross-provider non-Full modes fail closed to the target provider default.
    [InlineData("codex", "never", "claude", true, true, "dontAsk")]
    [InlineData("claude", "acceptEdits", "codex", true, true, "never")]
    [InlineData("claude", "plan", "codex", true, true, "never")]
    [InlineData("acp:opencode", null, "codex", true, true, "never")]
    [InlineData("acp:custom", null, "codex", true, true, "never")]
    // Isolated temporary workaround: acp:antigravity with null mode maps to Full for read_write inheritance only.
    [InlineData("acp:antigravity", null, "codex", true, true, "full")]
    [InlineData("acp:antigravity", null, "claude", true, true, "bypassPermissions")]
    [InlineData("acp:antigravity", "plan", "codex", true, true, "never")]
    // Read-only tasks (allowFullInheritance: false) never inherit elevated permissions.
    [InlineData("codex", "full", "codex", false, false, "never")]
    [InlineData("claude", "bypassPermissions", "claude", false, false, "dontAsk")]
    [InlineData("claude", "bypassPermissions", "codex", false, false, "never")]
    [InlineData("acp:antigravity", null, "codex", false, false, "never")]
    [InlineData("acp:antigravity", null, "claude", false, false, "dontAsk")]
    public void ResolveInheritedMode_HandlesSameProviderCrossProviderReadOnlyAndAgyWorkaround(
        string sourceProvider,
        string? sourceMode,
        string targetProvider,
        bool allowFullInheritance,
        bool forReadWriteTask,
        string? expectedMode)
    {
        var resolved = ProviderPermissionModes.ResolveInheritedMode(
            sourceProvider,
            sourceMode,
            targetProvider,
            allowFullInheritance: allowFullInheritance,
            forReadWriteTask: forReadWriteTask);

        Assert.Equal(expectedMode, resolved);
    }

    [Fact]
    public void ResolveInheritedMode_NullSourceProviderFallsBackToTargetDefault()
    {
        Assert.Equal("never", ProviderPermissionModes.ResolveInheritedMode(null, null, "codex"));
        Assert.Equal("dontAsk", ProviderPermissionModes.ResolveInheritedMode(null, null, "claude"));
        Assert.Null(ProviderPermissionModes.ResolveInheritedMode(null, null, "acp:antigravity"));
    }
}
