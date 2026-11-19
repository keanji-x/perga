using Perga.AgentKit;

namespace Perga.Kernel;

internal static class RuntimePermissionModes
{
    internal static string? Resolve(string provider, string? requested, IReadOnlyList<ProviderStatus> statuses)
    {
        var modes = statuses.FirstOrDefault(status => string.Equals(status.Provider, provider, StringComparison.OrdinalIgnoreCase))?.PermissionModes;
        if (modes is not { Count: > 0 }) modes = ProviderPermissionModes.GetModesForProvider(provider);
        if (string.IsNullOrWhiteSpace(requested))
            return (modes.FirstOrDefault(mode => mode.IsDefault) ?? modes.FirstOrDefault())?.Id;
        var selected = requested.Trim();
        if (!modes.Any(mode => mode.Id == selected))
            throw KernelException.InvalidArgument($"Unsupported permission mode '{requested}' for provider '{provider}'.");
        return selected;
    }

    internal static async Task<string?> ResolveAsync(IAgentRuntime runtime, string provider, string? requested, CancellationToken ct)
    {
        IReadOnlyList<ProviderStatus> statuses;
        try { statuses = await runtime.ProbeProvidersAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { statuses = []; }
        return Resolve(provider, requested, statuses);
    }
}
