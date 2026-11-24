using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Actors;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel;

public sealed partial class PergaKernelService
{
    async Task<TrackDto> IFlowTrackSpawner.SpawnAsync(TrackDto parent, AgentSessionDto? parentLead, string taskId,
        int runCount, FlowDoSpecDto action, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(action.Prompt))
            throw KernelException.InvalidArgument("A Track spawn requires a non-blank prompt for the new Track Lead.");

        var runKey = FormattableString.Invariant($"{parent.Id}:{taskId}:r{runCount}");
        var creationKey = CreateKey("flow_track", parent.Id, $"flow_track:{runKey}", action)!;
        var firstInput = new SessionInputRequest($"flow_track_prompt:{runKey}",
            TrackActor.HashMessage(action.Prompt, "default", null, null, []), action.Prompt,
            AuthorId: parentLead?.Id ?? parent.Id);
        var child = await _store.FindCreationAsync(creationKey, ct).ConfigureAwait(false) is { } existingId
            ? await ReadCreatedTrackAsync(existingId, ct).ConfigureAwait(false)
            : await CreateFlowTrackAsync(parent, parentLead, action, creationKey, firstInput, ct).ConfigureAwait(false);

        try
        {
            await Actors.GetOrActivate(child.Track.Id).WarmWorkspaceAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Could not warm spawned Track {0}: {1}", child.Track.Id, ex.Message);
        }
        return child.Track;
    }

    private async Task<TrackAggregateSnapshot> CreateFlowTrackAsync(TrackDto parent, AgentSessionDto? parentLead,
        FlowDoSpecDto action, ResourceCreationKey creationKey, SessionInputRequest firstInput, CancellationToken ct)
    {
        await RequireTrackSpawnDepthAsync(parent.Id, ct).ConfigureAwait(false);
        var area = string.IsNullOrWhiteSpace(action.Area)
            ? await _store.GetAreaAsync(parent.AreaId, ct).ConfigureAwait(false)
                ?? throw KernelException.NotFound($"Area '{parent.AreaId}' not found.")
            : await ResolveAreaByIdOrNameAsync(action.Area, ct).ConfigureAwait(false);
        var targetId = string.IsNullOrWhiteSpace(action.ExecutionTargetId)
            ? area.ExecutionTargetId : ExecutionTargetIds.Normalize(action.ExecutionTargetId);

        var providers = await GetTargetProvidersAsync(targetId, ct: ct).ConfigureAwait(false);
        var defaultProvider = SessionLaunchValidator.NormalizeProvider(_options.DefaultProvider);
        var sourceProvider = parentLead is not null ? SessionLaunchValidator.NormalizeProvider(parentLead.Provider) : null;
        var requestedProvider = SessionLaunchValidator.NormalizeTaskProviderId(action.Provider);
        var provider = requestedProvider is not null
            ? SessionLaunchValidator.NormalizeProvider(requestedProvider)
            : sourceProvider ?? defaultProvider;
        var status = SessionLaunchValidator.FindProvider(providers, provider);
        if (requestedProvider is null && targetId != parent.ExecutionTargetId && providers.Count > 0 &&
            status is not { Available: true })
        {
            var fallback = providers.Where(candidate => candidate.Available)
                .OrderByDescending(candidate => SessionLaunchValidator.NormalizeProvider(candidate.Provider) == defaultProvider)
                .FirstOrDefault();
            provider = fallback is not null ? SessionLaunchValidator.NormalizeProvider(fallback.Provider) : defaultProvider;
        }
        status = SessionLaunchValidator.ValidateProvider(providers, provider,
            explicitlySelected: requestedProvider is not null || targetId != parent.ExecutionTargetId, requireAvailable: true);

        var requestedModel = SessionLaunchValidator.NormalizeRequestedModel(action.Model);
        var inheritedModel = provider == sourceProvider ? parentLead?.Model : null;
        if (status is { Models.Count: > 0 } && inheritedModel is not null &&
            !status.Models.Contains(inheritedModel, StringComparer.Ordinal))
            inheritedModel = null;
        var model = requestedModel ?? inheritedModel ?? SessionLaunchValidator.DefaultModel(status)
            ?? (provider == defaultProvider ? _options.DefaultModel : null);

        var effort = action.ReasoningEffort?.Trim();
        SessionLaunchValidator.ValidateModelAndEffort(provider, status, requestedModel, model, effort);
        if (effort is null && provider == sourceProvider && model == parentLead?.Model)
        {
            var inherited = parentLead?.ReasoningEffort;
            var supported = status?.ModelCatalog?.FirstOrDefault(entry => entry.Model == model)?.SupportedReasoningEfforts;
            if (supported is not { Count: > 0 } || supported.Any(entry => entry.ReasoningEffort == inherited))
                effort = inherited;
        }

        var mode = action.PermissionMode is not null
            ? RuntimePermissionModes.Resolve(provider, action.PermissionMode, status?.PermissionModes)
            : ProviderPermissionModes.ResolveInheritedMode(sourceProvider, parentLead?.PermissionMode, provider);
        if (action.PermissionMode is null && ProviderPermissionModes.GetProfile(provider).CatalogSource == PermissionCatalogSource.Dynamic &&
            status?.PermissionModes is { Count: > 0 } modes &&
            mode is not null && !modes.Any(candidate => candidate.Id == mode))
            mode = modes.FirstOrDefault(candidate => candidate.IsDefault)?.Id;

        var requestedDriver = string.IsNullOrWhiteSpace(action.WorkspaceDriver) ? null : action.WorkspaceDriver.Trim();
        var workspaceOptionId = requestedDriver is null
            ? (await GetTrackWorkspaceOptionsAsync(area.Id, action.Cwd, action.Template, ct, targetId).ConfigureAwait(false)).DefaultId
            : null;
        var request = new CreateTrackRequest(area.Id, Title: action.Title, Cwd: action.Cwd,
            WorkspaceDriver: requestedDriver, TemplateId: action.Template, TemplateInputs: action.TemplateInputs,
            Provider: provider, Model: model, ReasoningEffort: effort, PermissionMode: mode,
            ExecutionTargetId: action.ExecutionTargetId, WorkspaceOptionId: workspaceOptionId);
        return await CreateTrackCoreAsync(request, ct, requestKey: null, flowKey: creationKey, originTrackId: parent.Id,
            firstInput: firstInput).ConfigureAwait(false);
    }

    private async Task RequireTrackSpawnDepthAsync(string parentTrackId, CancellationToken ct)
    {
        var ancestorId = parentTrackId;
        for (var depth = 1; ancestorId is not null; depth++)
        {
            if (depth > _options.MaxTrackSpawnDepth)
                throw KernelException.PreconditionFailed($"Track spawn would exceed MaxTrackSpawnDepth ({_options.MaxTrackSpawnDepth}).");
            ancestorId = (await _store.GetTrackAsync(ancestorId, ct).ConfigureAwait(false))?.OriginTrackId;
        }
    }
}
