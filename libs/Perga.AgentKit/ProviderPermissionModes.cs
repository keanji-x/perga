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
}
