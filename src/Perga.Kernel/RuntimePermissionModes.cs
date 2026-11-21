using Perga.AgentKit;

namespace Perga.Kernel;

internal static class RuntimePermissionModes
{
    internal static string? Resolve(string provider, string? requested, IReadOnlyList<PermissionModeOption>? openModes = null)
    {
        var profile = ProviderPermissionModes.GetProfile(provider);
        if (requested is null)
            return profile.DefaultModeId;

        var selected = requested.Trim();
        var validModes = profile.CatalogSource == PermissionCatalogSource.Dynamic
            ? openModes ?? []
            : profile.StaticModes;

        if (selected.Length == 0 ||
            (profile.CatalogSource == PermissionCatalogSource.Static || validModes.Count > 0) &&
            !validModes.Any(mode => string.Equals(mode.Id, selected, StringComparison.Ordinal)))
        {
            var defaultMode = profile.DefaultModeId ?? validModes.FirstOrDefault(mode => mode.IsDefault)?.Id;
            var choices = validModes.Count == 0 ? "(none reported)" : string.Join(", ", validModes.Select(
                mode => $"{mode.Id} ({mode.Label}{(mode.Id == defaultMode ? ", default" : "")})"));
            throw KernelException.InvalidArgument(
                $"Unsupported permission mode '{requested}' for provider '{provider}'. Supported modes: {choices}.",
                $"Call provider_list(provider='{provider}') to inspect valid modes.");
        }

        return selected;
    }
}
