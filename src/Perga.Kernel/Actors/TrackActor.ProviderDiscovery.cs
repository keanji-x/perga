using Perga.AgentKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    internal Task<(string DefaultProvider, IReadOnlyDictionary<string, IReadOnlyList<PermissionModeOption>> LiveModes)>
        ReadProviderDiscoveryContextAsync(CancellationToken ct) =>
        EnqueueAsync<(string, IReadOnlyDictionary<string, IReadOnlyList<PermissionModeOption>>)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var liveModes = new Dictionary<string, IReadOnlyList<PermissionModeOption>>(StringComparer.Ordinal);
            foreach (var (provider, modes) in ObservedProviderPermissionModesCore())
                liveModes.TryAdd(provider, modes.ToArray());
            return (SessionLaunchValidator.NormalizeProvider(GetDefaultProviderCore()), liveModes);
        }, ct);

    private IEnumerable<(string Provider, IReadOnlyList<PermissionModeOption> Modes)> ObservedProviderPermissionModesCore()
    {
        foreach (var session in _sessions.Values)
        {
            if (FindOpenSessionForSettingsCore(session.Id, session.Provider)?.AvailablePermissionModes is { Count: > 0 } modes)
                yield return (SessionLaunchValidator.NormalizeProvider(session.Provider), modes);
        }
    }

    private IReadOnlyList<PermissionModeOption>? ResolvePermissionCatalogCore(string provider, ProviderStatus? status,
        string? preferredSessionId = null, bool useTrackCatalog = true)
    {
        if (preferredSessionId is not null &&
            FindOpenSessionForSettingsCore(preferredSessionId, provider)?.AvailablePermissionModes is { Count: > 0 } preferred)
            return preferred;
        if (useTrackCatalog)
        {
            foreach (var (observedProvider, modes) in ObservedProviderPermissionModesCore())
                if (observedProvider == provider) return modes;
        }
        return status?.PermissionModes;
    }
}
