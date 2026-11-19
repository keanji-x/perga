using Perga.AgentKit;

namespace Perga.Kernel;

internal static class RuntimePermissionModes
{
    internal static string? Resolve(string provider, string? requested, IReadOnlyList<ProviderStatus> statuses)
    {
        var acp = provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase);
        if (acp && string.IsNullOrWhiteSpace(requested)) return null;
        var modes = statuses.FirstOrDefault(status => string.Equals(status.Provider, provider, StringComparison.OrdinalIgnoreCase))?.PermissionModes;
        if (modes is not { Count: > 0 }) modes = ProviderPermissionModes.GetModesForProvider(provider);
        if (string.IsNullOrWhiteSpace(requested))
            return (modes.FirstOrDefault(mode => mode.IsDefault) ?? modes.FirstOrDefault())?.Id;
        if (acp && modes.Count == 0)
            throw KernelException.PreconditionFailed($"Permission modes for provider '{provider}' are unavailable. Retry discovery.");
        var selected = requested.Trim();
        if (!modes.Any(mode => mode.Id == selected))
            throw KernelException.InvalidArgument($"Unsupported permission mode '{requested}' for provider '{provider}'.");
        return selected;
    }

    internal static async Task<string?> ResolveAsync(IAgentRuntime runtime, string provider, string? requested, CancellationToken ct, string? cwd = null)
    {
        if (provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(requested)) return null;
        IReadOnlyList<ProviderStatus> statuses;
        try
        {
            if (provider.StartsWith("acp:", StringComparison.OrdinalIgnoreCase))
            {
                var modes = await runtime.ProbePermissionModesAsync(provider, cwd, ct).ConfigureAwait(false);
                statuses = [new ProviderStatus(provider, true, null, null, [], PermissionModes: modes)];
            }
            else statuses = await runtime.ProbeProvidersAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { statuses = []; }
        return Resolve(provider, requested, statuses);
    }
}
