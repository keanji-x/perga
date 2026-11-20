using System.Collections.Frozen;
using Perga.AgentKit;

namespace Perga.Kernel;

internal static class RuntimePermissionModes
{
    internal static IReadOnlyDictionary<string, string> ValidateWorkerDefaults(
        IReadOnlyDictionary<string, string> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, mode) in configured)
        {
            var key = $"Perga:WorkerPermissionModes:{provider}";
            var canonicalProvider = AgentRuntime.NormalizeProviderName(provider);
            var profile = ProviderPermissionModes.GetProfile(canonicalProvider);
            var validModes = profile.StaticModes.Select(option => option.Id)
                .Concat(profile.LaunchModes.Select(mode => mode.Option.Id))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (validModes.Length == 0)
                throw new InvalidOperationException($"{key} has no configurable launch modes. Omit this setting to use the provider default.");
            if (!validModes.Contains(mode, StringComparer.Ordinal))
                throw new InvalidOperationException($"{key} has invalid mode '{mode}'. Expected one of: {string.Join(", ", validModes)}.");
            if (!result.TryAdd(canonicalProvider, mode))
                throw new InvalidOperationException($"{key} duplicates a configured mode for '{canonicalProvider}'.");
        }

        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    internal static string? ResolveWorkerDefault(string provider, IReadOnlyDictionary<string, string> configured) =>
        configured.TryGetValue(AgentRuntime.NormalizeProviderName(provider), out var mode) ? mode : Resolve(provider, null);

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
