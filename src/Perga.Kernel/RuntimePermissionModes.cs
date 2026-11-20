using Perga.AgentKit;

namespace Perga.Kernel;

internal static class RuntimePermissionModes
{
    internal static string? Resolve(string provider, string? requested, IReadOnlyList<PermissionModeOption>? openModes = null)
    {
        if (requested is not null && string.IsNullOrWhiteSpace(requested))
            throw KernelException.InvalidArgument("Permission mode must not be blank.");

        var profile = ProviderPermissionModes.GetProfile(provider);
        if (requested is null)
            return profile.DefaultModeId;

        var selected = requested.Trim();
        var validModes = profile.CatalogSource == PermissionCatalogSource.Dynamic
            ? openModes ?? []
            : profile.StaticModes;

        if ((profile.CatalogSource == PermissionCatalogSource.Static || validModes.Count > 0) &&
            !validModes.Any(mode => string.Equals(mode.Id, selected, StringComparison.Ordinal)))
            throw KernelException.InvalidArgument($"Unsupported permission mode '{requested}' for provider '{provider}'.");

        return selected;
    }
}
