namespace Perga.AgentKit;

public enum PermissionCatalogSource
{
    Static,
    Dynamic
}

public sealed record LaunchPermissionMode(
    PermissionModeOption Option,
    IReadOnlyList<string> ExtraLaunchArgs);

public sealed record ProviderPermissionProfile(
    PermissionCatalogSource CatalogSource,
    IReadOnlyList<PermissionModeOption> StaticModes,
    IReadOnlyList<LaunchPermissionMode> LaunchModes)
{
    public static ProviderPermissionProfile EmptyStatic { get; } =
        new(PermissionCatalogSource.Static, [], []);

    public static ProviderPermissionProfile PlainDynamic { get; } =
        new(PermissionCatalogSource.Dynamic, [], []);

    public string? DefaultModeId =>
        CatalogSource == PermissionCatalogSource.Static
            ? (StaticModes.FirstOrDefault(m => m.IsDefault) ?? StaticModes.FirstOrDefault())?.Id
            : null;

    public IReadOnlyList<string> GetLaunchArgs(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode) || LaunchModes.Count == 0)
        {
            return [];
        }

        var trimmed = mode.Trim();
        foreach (var launchMode in LaunchModes)
        {
            if (string.Equals(launchMode.Option.Id, trimmed, StringComparison.Ordinal))
            {
                return launchMode.ExtraLaunchArgs;
            }
        }

        return [];
    }
}

/// <summary>
/// Single authoritative declaration of provider permission catalogs and launch-argument modes.
/// Each provider owns its catalog source (static vs runtime-discovered) and any CLI-flag modes.
/// </summary>
public static class ProviderPermissionModes
{
    private static readonly IReadOnlyList<PermissionModeOption> CodexModes =
    [
        new("never", "Never", "Sandbox only; never asks.", "neutral", IsDefault: true),
        new("ask", "Ask me", "Pauses the turn to ask you.", "warning"),
        new("full", "Full access", "No sandbox; never asks. Runs with your account's access.", "danger")
    ];

    private static readonly IReadOnlyList<PermissionModeOption> ClaudeModes =
    [
        new("dontAsk", "Never", "Auto-denies unapproved actions; never asks.", "neutral", IsDefault: true),
        new("default", "Ask me", "Pauses the turn to ask you.", "warning"),
        new("acceptEdits", "Accept edits", "Auto-approves edits; asks for commands.", "warning"),
        new("plan", "Plan", "Plans without executing changes.", "neutral"),
        new("auto", "Auto", "Provider automatically reviews permission requests.", "warning"),
        new("bypassPermissions", "Full access", "No sandbox; never asks. Runs with your account's access.", "danger")
    ];

    private static readonly IReadOnlyList<LaunchPermissionMode> AntigravityLaunchModes =
    [
        new(
            new PermissionModeOption(
                "bypassPermissions",
                "Full access",
                "Auto-approves all tool permission requests without prompting.",
                "danger",
                IsDefault: false),
            ["--dangerously-skip-permissions"])
    ];

    private static readonly ProviderPermissionProfile CodexProfile =
        new(PermissionCatalogSource.Static, CodexModes, []);

    private static readonly ProviderPermissionProfile ClaudeProfile =
        new(PermissionCatalogSource.Static, ClaudeModes, []);

    private static readonly ProviderPermissionProfile AntigravityProfile =
        new(PermissionCatalogSource.Dynamic, [], AntigravityLaunchModes);

    public static ProviderPermissionProfile GetProfile(string provider)
    {
        var normalized = AgentRuntime.NormalizeProviderName(provider);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ProviderPermissionProfile.EmptyStatic;
        }

        if (string.Equals(normalized, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return CodexProfile;
        }

        if (string.Equals(normalized, "claude", StringComparison.OrdinalIgnoreCase))
        {
            return ClaudeProfile;
        }

        if (string.Equals(normalized, "acp:antigravity", StringComparison.OrdinalIgnoreCase))
        {
            return AntigravityProfile;
        }

        if (normalized.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderPermissionProfile.PlainDynamic;
        }

        return ProviderPermissionProfile.EmptyStatic;
    }

    public static IReadOnlyList<PermissionModeOption> GetModesForProvider(string provider) =>
        GetProfile(provider).StaticModes;

    public static string? GetDefaultMode(string provider) =>
        GetProfile(provider).DefaultModeId;

}
