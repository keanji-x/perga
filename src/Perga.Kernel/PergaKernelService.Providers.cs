using Perga.AgentKit;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    public async Task<IReadOnlyList<ProviderStatus>> GetTargetProvidersAsync(
        string targetId,
        bool refresh = false,
        CancellationToken ct = default)
    {
        var observation = await GetCachedOrProbeProvidersAsync(ExecutionTargetIds.Normalize(targetId), refresh, ct)
            .ConfigureAwait(false);
        return observation.Providers;
    }

    public async Task<TrackProvidersDto> ListProvidersForTrackAsync(string trackId, string? provider = null,
        bool refresh = false, bool includeUnavailable = false, CancellationToken ct = default)
    {
        var track = await _store.GetTrackAsync(trackId, ct).ConfigureAwait(false)
            ?? throw KernelException.NotFound($"Track '{trackId}' not found.");
        var (defaultProvider, liveModes) = await Actors.GetOrActivate(trackId)
            .ReadProviderDiscoveryContextAsync(ct).ConfigureAwait(false);
        var observation = await GetCachedOrProbeProvidersAsync(track.ExecutionTargetId, refresh, ct).ConfigureAwait(false);
        IEnumerable<ProviderStatus> providers = observation.Providers;
        if (provider is not null)
        {
            var selected = SessionLaunchValidator.FindProvider(observation.Providers, SessionLaunchValidator.NormalizeProvider(provider))
                ?? throw SessionLaunchValidator.UnknownProvider(provider, observation.Providers);
            providers = [selected];
        }
        else if (!includeUnavailable)
        {
            providers = providers.Where(status => status.Available);
        }

        var entries = providers.Select(status =>
        {
            var id = SessionLaunchValidator.NormalizeProvider(status.Provider);
            var permissionSource = ProviderPermissionModes.GetProfile(id).CatalogSource;
            var defaultModel = SessionLaunchValidator.DefaultModel(status);
            var models = status.ModelCatalog is { Count: > 0 } catalog
                ? catalog.Select(model => new TrackProviderModelDto(model.Model, model.DisplayName, model.IsDefault,
                    model.DefaultReasoningEffort, model.SupportedReasoningEfforts.Select(effort => effort.ReasoningEffort).ToArray())).ToArray()
                : status.Models.Select(model => new TrackProviderModelDto(model, model, model == defaultModel, null, [])).ToArray();
            var permissionModes = permissionSource == PermissionCatalogSource.Static
                ? ProviderPermissionModes.GetModesForProvider(id)
                : liveModes.GetValueOrDefault(id) ?? status.PermissionModes ?? [];
            return new TrackProviderEntryDto(id, status.Available, status.Version, status.Reason, defaultModel,
                permissionSource == PermissionCatalogSource.Static ? "static" : "dynamic", models, permissionModes);
        }).ToArray();

        return new(defaultProvider, observation.ObservedAtMs, entries);
    }
}
