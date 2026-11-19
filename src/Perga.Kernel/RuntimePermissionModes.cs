using Perga.AgentKit;

namespace Perga.Kernel;

internal static class RuntimePermissionModes
{
    internal static string? Resolve(string provider, string? requested, IReadOnlyList<PermissionModeOption>? openModes = null)
    {
        var acp = provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase);
        if (requested is not null && string.IsNullOrWhiteSpace(requested))
            throw KernelException.InvalidArgument("Permission mode must not be blank.");
        if (acp && requested is null) return null;
        var modes = acp ? openModes ?? [] : ProviderPermissionModes.GetModesForProvider(provider);
        if (requested is null)
            return (modes.FirstOrDefault(mode => mode.IsDefault) ?? modes.FirstOrDefault())?.Id;
        var selected = requested.Trim();
        if ((!acp || modes.Count > 0) && !modes.Any(mode => mode.Id == selected))
            throw KernelException.InvalidArgument($"Unsupported permission mode '{requested}' for provider '{provider}'.");
        return selected;
    }
}
