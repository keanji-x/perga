using Perga.AgentKit;

namespace Perga.AgentKit.Tests;

public sealed class ProviderPermissionModesTests
{
    [Theory]
    [InlineData("codex", "full", "codex", PermissionInheritanceScope.ReadWriteTask, "full")]
    [InlineData("codex", "never", "codex", PermissionInheritanceScope.ReadWriteTask, "never")]
    [InlineData("claude", "bypassPermissions", "claude", PermissionInheritanceScope.ReadWriteTask, "bypassPermissions")]
    [InlineData("claude", "acceptEdits", "claude", PermissionInheritanceScope.ReadWriteTask, "acceptEdits")]
    // Claude "plan" is read-only planning: not inherited onto read_write tasks, preserved for collaborators.
    [InlineData("claude", "plan", "claude", PermissionInheritanceScope.ReadWriteTask, "dontAsk")]
    [InlineData("claude", "plan", "claude", PermissionInheritanceScope.Collaborator, "plan")]
    // Cross-provider explicit Full access mapping from declared provider profiles.
    [InlineData("codex", "full", "claude", PermissionInheritanceScope.ReadWriteTask, "bypassPermissions")]
    [InlineData("claude", "bypassPermissions", "codex", PermissionInheritanceScope.ReadWriteTask, "full")]
    [InlineData("codex", "full", "acp:antigravity", PermissionInheritanceScope.ReadWriteTask, null)]
    // Cross-provider non-Full and undeclared dynamic ACP modes fail closed to the target provider default (no name guessing).
    [InlineData("codex", "never", "claude", PermissionInheritanceScope.ReadWriteTask, "dontAsk")]
    [InlineData("claude", "acceptEdits", "codex", PermissionInheritanceScope.ReadWriteTask, "never")]
    [InlineData("claude", "plan", "codex", PermissionInheritanceScope.ReadWriteTask, "never")]
    [InlineData("acp:opencode", null, "codex", PermissionInheritanceScope.ReadWriteTask, "never")]
    [InlineData("acp:opencode", "bypassPermissions", "codex", PermissionInheritanceScope.ReadWriteTask, "never")]
    [InlineData("acp:custom", null, "codex", PermissionInheritanceScope.ReadWriteTask, "never")]
    [InlineData("acp:custom", "full", "codex", PermissionInheritanceScope.ReadWriteTask, "never")]
    [InlineData("acp:antigravity", "bypassPermissions", "codex", PermissionInheritanceScope.ReadWriteTask, "never")]
    // Isolated temporary workaround: acp:antigravity with null mode maps to Full for read_write/collaborator inheritance only.
    [InlineData("acp:antigravity", null, "codex", PermissionInheritanceScope.ReadWriteTask, "full")]
    [InlineData("acp:antigravity", null, "codex", PermissionInheritanceScope.Collaborator, "full")]
    [InlineData("acp:antigravity", null, "claude", PermissionInheritanceScope.ReadWriteTask, "bypassPermissions")]
    [InlineData("acp:antigravity", "plan", "codex", PermissionInheritanceScope.ReadWriteTask, "never")]
    // Read-only tasks never inherit elevated permissions.
    [InlineData("codex", "full", "codex", PermissionInheritanceScope.ReadOnlyTask, "never")]
    [InlineData("claude", "bypassPermissions", "claude", PermissionInheritanceScope.ReadOnlyTask, "dontAsk")]
    [InlineData("claude", "bypassPermissions", "codex", PermissionInheritanceScope.ReadOnlyTask, "never")]
    [InlineData("acp:antigravity", null, "codex", PermissionInheritanceScope.ReadOnlyTask, "never")]
    [InlineData("acp:antigravity", null, "claude", PermissionInheritanceScope.ReadOnlyTask, "dontAsk")]
    public void ResolveInheritedMode_HandlesSameProviderCrossProviderReadOnlyAndAgyWorkaround(
        string sourceProvider,
        string? sourceMode,
        string targetProvider,
        PermissionInheritanceScope scope,
        string? expectedMode)
    {
        var resolved = ProviderPermissionModes.ResolveInheritedMode(
            sourceProvider,
            sourceMode,
            targetProvider,
            scope);

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
