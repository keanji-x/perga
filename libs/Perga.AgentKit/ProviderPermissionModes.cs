namespace Perga.AgentKit;

/// <summary>
/// Provider-declared permission modes and launch-time validation helpers.
/// Each harness owns its supported mode IDs and how they map to provider flags/RPC fields.
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

    private static readonly IReadOnlyList<PermissionModeOption> AcpModes = [];

    public static IReadOnlyList<PermissionModeOption> GetModesForProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return [];
        }

        var normalized = provider.Trim();
        if (string.Equals(normalized, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return CodexModes;
        }

        if (string.Equals(normalized, "claude", StringComparison.OrdinalIgnoreCase))
        {
            return ClaudeModes;
        }

        if (normalized.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
        {
            return AcpModes;
        }

        return [];
    }

    public static string? GetDefaultMode(string provider)
    {
        var modes = GetModesForProvider(provider);
        if (modes.Count == 0)
        {
            return null;
        }

        return (modes.FirstOrDefault(m => m.IsDefault) ?? modes[0]).Id;
    }

    public static bool IsSupported(string provider, string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return false;
        }

        var trimmed = mode.Trim();
        return GetModesForProvider(provider)
            .Any(m => string.Equals(m.Id, trimmed, StringComparison.Ordinal));
    }
}
