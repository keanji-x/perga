namespace Perga.AgentKit;

public enum PermissionCatalogSource
{
    Static,
    Dynamic
}

public enum PermissionInheritanceScope
{
    ReadOnlyTask,
    ReadWriteTask,
    Collaborator
}

public sealed record ProviderPermissionProfile(
    PermissionCatalogSource CatalogSource,
    IReadOnlyList<PermissionModeOption> StaticModes,
    string? FullAccessModeId = null,
    string? ReadOnlyPlanningModeId = null)
{
    public static ProviderPermissionProfile EmptyStatic { get; } = new(PermissionCatalogSource.Static, []);
    public static ProviderPermissionProfile PlainDynamic { get; } = new(PermissionCatalogSource.Dynamic, []);

    public string? DefaultModeId =>
        CatalogSource == PermissionCatalogSource.Static
            ? (StaticModes.FirstOrDefault(m => m.IsDefault) ?? StaticModes.FirstOrDefault())?.Id
            : null;
}

/// <summary>Single authoritative declaration of provider permission catalogs.</summary>
public static class ProviderPermissionModes
{
    private static readonly ProviderPermissionProfile CodexProfile = new(
        PermissionCatalogSource.Static,
        [
            new("never", "Never", "Sandbox only; never asks.", "neutral", IsDefault: true),
            new("full", "Full access", "No sandbox; never asks. Runs with your account's access.", "danger")
        ],
        FullAccessModeId: "full");

    private static readonly ProviderPermissionProfile ClaudeProfile = new(
        PermissionCatalogSource.Static,
        [
            new("dontAsk", "Never", "Auto-denies unapproved actions; never asks.", "neutral", IsDefault: true),
            new("acceptEdits", "Accept edits", "Auto-approves edits; denies unapproved commands.", "warning"),
            new("plan", "Plan", "Plans without executing changes.", "neutral"),
            new("bypassPermissions", "Full access", "No sandbox; never asks. Runs with your account's access.", "danger")
        ],
        FullAccessModeId: "bypassPermissions",
        ReadOnlyPlanningModeId: "plan");

    public static ProviderPermissionProfile GetProfile(string provider)
    {
        var normalized = AgentRuntime.NormalizeProviderName(provider);
        if (string.IsNullOrWhiteSpace(normalized)) return ProviderPermissionProfile.EmptyStatic;
        if (normalized.Equals("codex", StringComparison.OrdinalIgnoreCase)) return CodexProfile;
        if (normalized.Equals("claude", StringComparison.OrdinalIgnoreCase)) return ClaudeProfile;
        if (normalized.StartsWith("acp:", StringComparison.OrdinalIgnoreCase)) return ProviderPermissionProfile.PlainDynamic;
        return ProviderPermissionProfile.EmptyStatic;
    }

    public static IReadOnlyList<PermissionModeOption> GetModesForProvider(string provider) =>
        GetProfile(provider).StaticModes;

    public static string? GetDefaultMode(string provider) =>
        GetProfile(provider).DefaultModeId;

    /// <summary>
    /// Resolves the inherited permission mode when spawning a child/worker session whose
    /// permissionMode was omitted.
    /// - <see cref="PermissionInheritanceScope.ReadOnlyTask"/> never inherits elevated permissions
    ///   and always returns the target provider's default mode.
    /// - For the same provider, inherits <paramref name="sourceMode"/> directly (except read-only
    ///   planning modes such as Claude "plan" are not inherited onto <see cref="PermissionInheritanceScope.ReadWriteTask"/>).
    /// - Across providers, only maps declared Full access equivalents to the target provider's
    ///   Full access mode; all intermediate/unknown modes fail closed to the target default.
    /// </summary>
    public static string? ResolveInheritedMode(
        string? sourceProvider,
        string? sourceMode,
        string targetProvider,
        PermissionInheritanceScope scope = PermissionInheritanceScope.Collaborator)
    {
        var normalizedTarget = AgentRuntime.NormalizeProviderName(targetProvider);
        var defaultMode = GetDefaultMode(normalizedTarget);
        if (scope == PermissionInheritanceScope.ReadOnlyTask || string.IsNullOrWhiteSpace(sourceProvider))
            return defaultMode;

        var normalizedSource = AgentRuntime.NormalizeProviderName(sourceProvider);
        var trimmedSource = string.IsNullOrWhiteSpace(sourceMode) ? null : sourceMode.Trim();

        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            if (trimmedSource is null)
                return defaultMode;
            if (scope == PermissionInheritanceScope.ReadWriteTask &&
                string.Equals(trimmedSource, GetProfile(normalizedSource).ReadOnlyPlanningModeId, StringComparison.Ordinal))
                return defaultMode;
            return trimmedSource;
        }

        if (IsFullAccessMode(normalizedSource, trimmedSource))
            return GetFullAccessMode(normalizedTarget);

        return defaultMode;
    }

    public static bool IsFullAccessMode(string provider, string? mode)
    {
        var normalized = AgentRuntime.NormalizeProviderName(provider);
        var trimmed = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();

        if (IsImplicitFullAccessWorkaround(normalized, trimmed))
            return true;

        var fullAccessModeId = GetProfile(normalized).FullAccessModeId;
        return fullAccessModeId is not null && string.Equals(trimmed, fullAccessModeId, StringComparison.Ordinal);
    }

    public static string? GetFullAccessMode(string provider)
    {
        var profile = GetProfile(provider);
        return profile.FullAccessModeId ?? profile.DefaultModeId;
    }

    /// <summary>
    /// Returns true when a static provider's permission mode grants more execution authority
    /// than its default or read-only planning modes (for example, Codex "full" or Claude
    /// "acceptEdits" / "bypassPermissions"). Dynamic providers with no static catalog return false.
    /// </summary>
    public static bool IsElevatedForReadOnlyReuse(string provider, string? mode)
    {
        var profile = GetProfile(provider);
        if (profile.CatalogSource != PermissionCatalogSource.Static)
            return false;
        var trimmed = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim();
        if (trimmed is null)
            return false;
        return !string.Equals(trimmed, profile.DefaultModeId, StringComparison.Ordinal) &&
               !string.Equals(trimmed, profile.ReadOnlyPlanningModeId, StringComparison.Ordinal);
    }

    /// <summary>
    /// TEMPORARY COMPATIBILITY HACK (non-default policy — isolated here intentionally):
    /// Today, Perga runs <c>acp:antigravity</c> via OAR without a sandbox and without a reported
    /// permission catalog, so Antigravity Lead sessions persist <c>permissionMode == null</c>.
    /// Treating <c>acp:antigravity</c> (<c>null</c>) as Full access lets cross-provider
    /// <c>read_write</c> Flow Workers and <c>session_create</c> helpers (e.g., Codex) inherit
    /// Full access and run local tests/sockets instead of stalling on Codex's
    /// <c>workspace-write</c> sandbox.
    /// Remove this single check once Antigravity exposes real sandboxed/non-full permission modes.
    /// </summary>
    private static bool IsImplicitFullAccessWorkaround(string normalizedProvider, string? trimmedMode) =>
        trimmedMode is null &&
        normalizedProvider.Equals("acp:antigravity", StringComparison.OrdinalIgnoreCase);
}
