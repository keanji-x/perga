namespace Perga.AgentKit;

public enum PermissionCatalogSource
{
    Static,
    Dynamic
}

public sealed record ProviderPermissionProfile(
    PermissionCatalogSource CatalogSource,
    IReadOnlyList<PermissionModeOption> StaticModes)
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
        ]);

    private static readonly ProviderPermissionProfile ClaudeProfile = new(
        PermissionCatalogSource.Static,
        [
            new("dontAsk", "Never", "Auto-denies unapproved actions; never asks.", "neutral", IsDefault: true),
            new("acceptEdits", "Accept edits", "Auto-approves edits; denies unapproved commands.", "warning"),
            new("plan", "Plan", "Plans without executing changes.", "neutral"),
            new("bypassPermissions", "Full access", "No sandbox; never asks. Runs with your account's access.", "danger")
        ]);

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
    /// - When <paramref name="allowFullInheritance"/> is false (e.g., read_only Flow tasks),
    ///   never inherits elevated permissions and always returns the target provider's default mode.
    /// - For the same provider, inherits <paramref name="sourceMode"/> directly (except Claude's
    ///   read-only "plan" mode is not inherited when <paramref name="forReadWriteTask"/> is true).
    /// - Across providers, only maps explicit Full access equivalents to the target provider's
    ///   Full access mode; all intermediate/unknown modes fail closed to the target default.
    /// </summary>
    public static string? ResolveInheritedMode(
        string? sourceProvider,
        string? sourceMode,
        string targetProvider,
        bool allowFullInheritance = true,
        bool forReadWriteTask = false)
    {
        var normalizedTarget = AgentRuntime.NormalizeProviderName(targetProvider);
        var defaultMode = GetDefaultMode(normalizedTarget);
        if (!allowFullInheritance || string.IsNullOrWhiteSpace(sourceProvider))
            return defaultMode;

        var normalizedSource = AgentRuntime.NormalizeProviderName(sourceProvider);
        var trimmedSource = string.IsNullOrWhiteSpace(sourceMode) ? null : sourceMode.Trim();

        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            if (trimmedSource is null)
                return defaultMode;
            if (forReadWriteTask && IsReadOnlyPlanningMode(normalizedSource, trimmedSource))
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

        if (trimmed is null)
            return false;

        if (normalized.Equals("codex", StringComparison.OrdinalIgnoreCase))
            return string.Equals(trimmed, "full", StringComparison.Ordinal);

        if (normalized.Equals("claude", StringComparison.OrdinalIgnoreCase))
            return string.Equals(trimmed, "bypassPermissions", StringComparison.Ordinal);

        if (normalized.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            return string.Equals(trimmed, "bypassPermissions", StringComparison.Ordinal) ||
                   string.Equals(trimmed, "full", StringComparison.Ordinal);

        return false;
    }

    public static string? GetFullAccessMode(string provider)
    {
        var normalized = AgentRuntime.NormalizeProviderName(provider);
        if (normalized.Equals("codex", StringComparison.OrdinalIgnoreCase))
            return "full";
        if (normalized.Equals("claude", StringComparison.OrdinalIgnoreCase))
            return "bypassPermissions";
        // Dynamic ACP providers (e.g., acp:antigravity, acp:opencode) run non-interactively
        // with null permissionMode under OAR; returning null avoids injecting an unadvertised ID.
        if (normalized.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            return null;
        return GetDefaultMode(normalized);
    }

    private static bool IsReadOnlyPlanningMode(string normalizedProvider, string mode) =>
        normalizedProvider.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(mode, "plan", StringComparison.Ordinal);

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
