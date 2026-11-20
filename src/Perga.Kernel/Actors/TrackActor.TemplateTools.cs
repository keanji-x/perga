using System.Text.Json;
using System.Text.Json.Nodes;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Events;
using Perga.Kernel.Mcp;
using Perga.PolicyVm;
using Perga.Kernel.Templates;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    public Task<JsonElement> InspectCurrentTemplateAsync(string sessionId, CancellationToken ct) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        RequireGovernanceSessionCore(sessionId, KernelMcpToolNames.TemplateCat);
        return InspectCurrentTemplateCore();
    }, ct);

    public Task<JsonElement> InspectCurrentTemplateAsync(CancellationToken ct = default) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        return InspectCurrentTemplateCore();
    }, ct);

    private JsonElement InspectCurrentTemplateCore() => JsonHelpers.ToElement(new
    {
        snapshot = _templateSnapshotSource, manifest = _policy?.Manifest,
        inputs = _track.TemplateInputs, state = _track.TemplateState, headerView = PresentTrackCore().TemplateHeaderView
    });

    public Task<TrackDto> ClearTemplateAsync(CancellationToken ct = default) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        if (_track.ClosedAtMs is not null) throw KernelException.Conflict("Cannot clear a template on a closed track.");
        return await DetachTemplateCoreAsync(false, ct).ConfigureAwait(false);
    }, ct);

    private async Task<TrackDto> DetachTemplateCoreAsync(bool dryRun, CancellationToken ct)
    {
        var detached = _track with { TemplateId = null, TemplateInputs = JsonHelpers.EmptyObject,
            TemplateState = JsonHelpers.EmptyObject, UpdatedAtMs = NowMs() };
        if (dryRun) return detached;
        var evt = new EventEnvelopeDto(null, detached.AreaId, TrackId, KernelEventKinds.TrackUpdated,
            JsonHelpers.ToElement(detached), detached.UpdatedAtMs);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpdateTrackTemplateAndEmitAsync(detached, null, evt, ct).ConfigureAwait(false);
        _track = detached;
        _policy = null;
        _policyUnavailableReason = null;
        _templateSnapshotSource = null;
        _policyGeneration = Guid.NewGuid().ToString("N");
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
        await ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
        return PresentTrackCore();
    }

    public Task<TrackDto> UpdateTemplateInputsAsync(JsonElement patch, CancellationToken ct = default) =>
        UpdateTemplateInputsCoreAsync(null, patch, null, ct);

    public Task<TrackDto> UpdateTemplateInputsAsync(string sessionId, JsonElement patch, string generation, CancellationToken ct) =>
        UpdateTemplateInputsCoreAsync(sessionId, patch, generation, ct);

    private Task<TrackDto> UpdateTemplateInputsCoreAsync(string? sessionId, JsonElement patch, string? generation, CancellationToken ct) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        if (sessionId is not null) RequireGovernanceSessionCore(sessionId, KernelMcpToolNames.TemplateUpdateInputs);
        if (generation is not null && _policyGeneration != generation) throw KernelException.PolicyDenied("Active policy changed after authorization.");
        if (_track.ClosedAtMs is not null) throw KernelException.Conflict("Cannot update a closed track.");
        if (_policy is null) throw KernelException.Conflict("The track has no active template.");
        if (patch.ValueKind != JsonValueKind.Object) throw KernelException.InvalidArgument("inputs must be an object.");
        var merged = JsonNode.Parse(_track.TemplateInputs.GetRawText())!.AsObject();
        foreach (var entry in patch.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Null) merged.Remove(entry.Name);
            else merged[entry.Name] = JsonNode.Parse(entry.Value.GetRawText());
        }
        var next = _track with { TemplateInputs = JsonHelpers.ParseClone(merged.ToJsonString()), UpdatedAtMs = NowMs() };
        TemplateSchedules.ValidateInputs(_policyVm, _policy, next.TemplateInputs);
        var schedules = TemplateSchedules.Materialize(_policy, next.TemplateInputs, TrackId, next.UpdatedAtMs, _policyVm, _scheduleCronEvaluator);
        var evt = new EventEnvelopeDto(null, next.AreaId, TrackId, KernelEventKinds.TrackUpdated,
            JsonHelpers.ToElement(Templates.TemplatePresentation.Project(next, _policy, _policyVm)), next.UpdatedAtMs);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpdateTrackTemplateAndEmitAsync(next, _templateSnapshotSource ?? _policy.JsSource, evt, ct, schedules, refreshPromptsOnly: true).ConfigureAwait(false);
        _track = next;
        _policyGeneration = Guid.NewGuid().ToString("N");
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
        return PresentTrackCore();
    }, ct);

    public async Task AfterPolicyEventAsync(string eventName, JsonElement data, CancellationToken ct)
    {
        try
        {
            await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                await AfterPolicyEventCoreAsync(eventName, data, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Could not deliver policy after event {0} for Track {1}: {2}", eventName, TrackId, ex);
        }
    }

    private PolicyEvalResult<string?> EvaluateAfterPolicyEventCore(string eventName, JsonElement data)
    {
        if (_policy is not null)
        {
            try
            {
                return _policyVm.After(_policy, eventName, TrackId, data, _track.TemplateInputs, _track.TemplateState);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Policy after hook {0} failed for Track {1}: {2}", eventName, TrackId, ex);
            }
        }
        return new(_track.TemplateState, null);
    }

    private async Task<string?> AfterPolicyEventCoreAsync(string eventName, JsonElement data, CancellationToken ct)
    {
        if (_policy is null) return null;
        try
        {
            var eval = _policyVm.After(_policy, eventName, TrackId, data, _track.TemplateInputs, _track.TemplateState);
            await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);
            return eval.Output;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Policy after hook {0} failed for Track {1}: {2}", eventName, TrackId, ex);
            return null;
        }
    }

    private static bool ValidateTemplateSetArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw KernelException.InvalidArgument("Template arguments must be an object.");
        foreach (var flag in new[] { "dryRun", "clear" })
            if (arguments.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw KernelException.InvalidArgument(flag + " must be a boolean.");
        return arguments.TryGetProperty("dryRun", out var dryRun) && dryRun.ValueKind == JsonValueKind.True;
    }

    private async Task<IReadOnlyList<string>> TemplateWarningsCoreAsync(CompiledPolicy policy, JsonElement inputs, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (policy.Manifest.WorkspaceDriver is not null || policy.Manifest.WorkspaceLowerCacheDirs.Count > 0 ||
            policy.Manifest.WorkspaceCreateCmd is not null || policy.Manifest.WorkspaceCloseCmd is not null)
            warnings.Add("Template workspace settings are ignored for an existing track.");
        if (policy.Manifest.Routing.Count > 0)
        {
            var providers = await _agentRuntime.ProbeProvidersAsync(ct).ConfigureAwait(false);
            foreach (var route in _policyVm.ResolveRoutingTargets(policy, inputs))
            {
                var provider = route.Provider ?? GetDefaultProviderCore();
                var model = route.Model;
                if (!providers.Any(p => p.Available && p.Provider == provider && (model is null || p.Models.Contains(model, StringComparer.Ordinal))))
                    warnings.Add($"Template provider/model is unavailable: {provider}/{model}.");
            }
        }
        return warnings.Distinct(StringComparer.Ordinal).ToArray();
    }
}
