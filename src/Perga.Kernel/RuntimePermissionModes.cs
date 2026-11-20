using System.Collections.Frozen;
using Perga.AgentKit;

namespace Perga.Kernel;

internal sealed class RuntimePermissionModes
{
    internal static RuntimePermissionModes Unconfigured { get; } = new(FrozenDictionary<string, string>.Empty);

    internal IReadOnlyDictionary<string, string> Defaults { get; }

    internal RuntimePermissionModes(IReadOnlyDictionary<string, string> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, mode) in configured)
        {
            var key = $"Perga:PermissionModeDefaults:{provider}";
            var canonical = AgentRuntime.NormalizeProviderName(provider);
            if (string.IsNullOrWhiteSpace(mode) || mode != mode.Trim() || mode.Any(char.IsControl))
                throw new InvalidOperationException($"{key} must be an exact, non-empty permission mode ID.");
            var profile = ProviderPermissionModes.GetProfile(canonical);
            if (profile.CatalogSource == PermissionCatalogSource.Static)
            {
                if (profile.StaticModes.Count == 0)
                    throw new InvalidOperationException($"{key} does not name a supported provider.");
                if (!profile.StaticModes.Any(option => option.Id == mode))
                    throw new InvalidOperationException($"{key} has invalid mode '{mode}'. Expected one of: {string.Join(", ", profile.StaticModes.Select(option => option.Id))}.");
            }
            if (!defaults.TryAdd(canonical, mode))
                throw new InvalidOperationException($"{key} duplicates the default for '{canonical}'.");
        }

        Defaults = defaults.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    internal string? Resolve(string provider, string? requested, IReadOnlyList<PermissionModeOption>? openModes = null)
    {
        if (requested is not null && string.IsNullOrWhiteSpace(requested))
            throw KernelException.InvalidArgument("Permission mode must not be blank.");

        var profile = ProviderPermissionModes.GetProfile(provider);
        if (requested is null)
            return Defaults.TryGetValue(AgentRuntime.NormalizeProviderName(provider), out var configured)
                ? configured : profile.DefaultModeId;

        var selected = requested.Trim();
        var validModes = profile.CatalogSource == PermissionCatalogSource.Dynamic
            ? openModes ?? []
            : profile.StaticModes;

        if ((profile.CatalogSource == PermissionCatalogSource.Static || validModes.Count > 0) &&
            !validModes.Any(mode => string.Equals(mode.Id, selected, StringComparison.Ordinal)))
            throw KernelException.InvalidArgument($"Unsupported permission mode '{requested}' for provider '{provider}'.");

        return selected;
    }

    internal ProviderStatus ApplyDefault(ProviderStatus provider)
    {
        if (!Defaults.TryGetValue(AgentRuntime.NormalizeProviderName(provider.Provider), out var mode))
            return provider;

        var profile = ProviderPermissionModes.GetProfile(provider.Provider);
        var options = provider.PermissionModes is { Count: > 0 } declared ? declared : profile.StaticModes;
        if (!options.Any(option => option.Id == mode))
        {
            var launch = profile.LaunchModes.FirstOrDefault(entry => entry.Option.Id == mode);
            if (launch is null)
                return provider;
            options = [.. options, launch.Option];
        }

        return provider with
        {
            PermissionModes = options.Select(option => option with { IsDefault = option.Id == mode }).ToArray()
        };
    }
}
