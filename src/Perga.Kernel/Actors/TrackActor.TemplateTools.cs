using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Perga.AgentKit;
using Perga.Kernel.Tasks;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;
using System.Text.Json;
using System.Text.Json.Nodes;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Events;
using Perga.Kernel.Flow;
using Perga.Kernel.Ports;
using Perga.Kernel.Mcp;
using Perga.PolicyVm;
using Perga.Kernel.Templates;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    public Task<JsonElement> InspectCurrentTemplateAsync(string sessionId, CancellationToken ct) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        RequireGovernanceSessionCore(sessionId, KernelMcpToolNames.TemplateRead);
        return InspectCurrentTemplateCore(TemplateHost.LegacyReapplySource(_policy));
    }, ct);

    public Task<JsonElement> InspectCurrentTemplateAsync(CancellationToken ct = default) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        return InspectCurrentTemplateCore();
    }, ct);

    private JsonElement InspectCurrentTemplateCore(string? legacyReapply = null) => JsonHelpers.ToElement(new
    {
        snapshot = legacyReapply ?? _presentedTemplateSnapshotSource, manifest = _policy?.Manifest,
        editableSource = legacyReapply is null ? null : _presentedTemplateSnapshotSource,
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
        _presentedTemplateSnapshotSource = null;
        _policyGeneration = Guid.NewGuid().ToString("N");
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
        await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
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
        TemplateFlow.ValidateInputs(_policyVm, _policy, next.TemplateInputs);
        ValidateTrackDataCompatibilityCore(_policy, next.TemplateInputs, _track.Data,
            "Existing track data is incompatible with the updated template inputs",
            ". Migrate data before changing inputs.");
        TemplateFlow.ReadSeedNodes(_policyVm, _policy, next.TemplateInputs);
        TemplateFlow.RequireValid(TrackFlowValidator.Validate(_flowSpec, _flowSpec, _taskCoordinator.Snapshot(), CollectActiveFlowTaskIdsCore(),
            _sessions, next.LeadSessionId, _policy, _policyVm, next.TemplateInputs, _scheduleCronEvaluator, _timeProvider));
        var templateNodes = _flowSpec.Nodes.Where(node => node.Source == TrackScheduleSources.Template)
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        var clocks = (await ListTrackSchedulesSafeCoreAsync(ct).ConfigureAwait(false))
            .Where(clock => clock.NodeKey is not null && templateNodes.ContainsKey(clock.NodeKey) &&
                clock.State == TrackScheduleStates.Active)
            .Select(clock =>
            {
                var (title, prompt) = TrackFlowReconciler.ScheduleText(templateNodes[clock.NodeKey!]);
                return TemplateFlow.ResolveClockText(clock with { Title = title, Prompt = prompt }, next, _policy, _policyVm);
            }).ToArray();
        var flow = clocks.Length == 0 ? null : new TrackFlowUpdate(_flowSpec.Rev, [], clocks);
        var evt = new EventEnvelopeDto(null, next.AreaId, TrackId, KernelEventKinds.TrackUpdated,
            JsonHelpers.ToElement(Templates.TemplatePresentation.Project(next, _policy, _policyVm)), next.UpdatedAtMs);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpdateTrackTemplateAndEmitAsync(next, _templateSnapshotSource ?? TemplateHost.Freeze(_policy), evt, ct, flow: flow).ConfigureAwait(false);
        _track = next;
        _policyGeneration = Guid.NewGuid().ToString("N");
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
        return PresentTrackCore();
    }, ct);

    private static string GetPolicyToolName(string mcpServer, string toolName, bool useLegacyToolNames)
    {
        if (!useLegacyToolNames || mcpServer is not ("perga" or "vcs")) return toolName;
        if (mcpServer == "perga")
        {
            toolName = toolName switch
            {
                KernelMcpToolNames.TaskList => "task_ls",
                KernelMcpToolNames.MailList => "mail_ls",
                KernelMcpToolNames.MailRead => "mail_cat",
                KernelMcpToolNames.TemplateList => "template_ls",
                KernelMcpToolNames.TemplateRead => "template_cat",
                _ => toolName
            };
        }
        return mcpServer + "_" + toolName;
    }

    public async Task AfterMcpToolCallAsync(string mcpServer, string toolName, JsonElement data, CancellationToken ct)
    {
        try
        {
            await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var policyToolName = GetPolicyToolName(mcpServer, toolName, _policy?.UsesLegacyAfterToolNames == true);
                await AfterPolicyEventCoreAsync("tool." + policyToolName, data, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Could not deliver policy after event for {0}.{1} on Track {2}: {3}",
                mcpServer, toolName, TrackId, ex);
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

    private static bool HasBusinessDataFields(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.EnumerateObject().Any(p => p.Name != "_meta");

    private void ValidateTrackDataCompatibilityCore(
        CompiledPolicy policy, JsonElement inputs, JsonElement data, string prefix, string suffix)
    {
        IReadOnlyList<DataValidationError> errors;
        try
        {
            errors = _policyVm.ValidateData(policy, inputs, data, _taskCoordinator.Snapshot().Select(t => t.TaskKey).ToArray());
        }
        catch (InvalidOperationException ex)
        {
            throw KernelException.InvalidArgument($"Template data schema evaluation failed: {ex.Message}");
        }

        if (errors.Count > 0)
            throw KernelException.Conflict(prefix + ": " +
                string.Join("; ", errors.Select(e => $"{(string.IsNullOrEmpty(e.Path) ? "$" : e.Path)}: {e.Message}")) + suffix);
    }

    private static bool ValidateTemplateSetArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw KernelException.InvalidArgument("Template arguments must be an object.");
        foreach (var flag in new[] { "dryRun", "clear", "resetData" })
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
            var providers = await EffectiveAgentRuntime.ProbeProvidersAsync(ct).ConfigureAwait(false);
            foreach (var route in _policyVm.ResolveRoutingTargets(policy, inputs))
            {
                var provider = route.Provider ?? GetDefaultProviderCore();
                var normalizedProvider = SessionLaunchValidator.NormalizeProvider(provider);
                var model = route.Model;
                if (!providers.Any(p => p.Available && SessionLaunchValidator.NormalizeProvider(p.Provider) == normalizedProvider &&
                    (model is null || p.Models.Contains(model, StringComparer.Ordinal))))
                    warnings.Add($"Template provider/model is unavailable: {provider}/{model}.");
            }
        }
        return warnings.Distinct(StringComparer.Ordinal).ToArray();
    }
    public async Task<TrackDto> SetTemplateBySessionAsync(string callerSessionId, string? templateId,
        string? source, string expectedPolicyGeneration, JsonElement? inputs, CancellationToken ct = default, bool resetData = false) =>
        (await SetTemplateWithOptionsAsync(callerSessionId, templateId, source, expectedPolicyGeneration, inputs, false, false, ct, resetData).ConfigureAwait(false)).Track;

    public Task<TemplateSetResult> SetTemplateWithOptionsAsync(string callerSessionId, string? templateId,
        string? source, string expectedPolicyGeneration, JsonElement? inputs, bool dryRun, bool clear, CancellationToken ct = default, bool resetData = false)
    {
        return EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireGovernanceSessionCore(callerSessionId, KernelMcpToolNames.TrackSet);
            if (_policyGeneration != expectedPolicyGeneration)
                throw KernelException.PolicyDenied("Active policy changed after template application was authorized.");
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot set a template on a closed track.");
            if (clear && (templateId is not null || source is not null || inputs is not null || resetData))
                throw KernelException.InvalidArgument("clear cannot be combined with a template, inputs, or resetData.");
            if (clear)
            {
                return new TemplateSetResult(await DetachTemplateCoreAsync(dryRun, ct).ConfigureAwait(false), [], dryRun);
            }

            if ((templateId is null) == (source is null) ||
                templateId is not null && string.IsNullOrWhiteSpace(templateId) ||
                source is not null && string.IsNullOrWhiteSpace(source))
                throw KernelException.InvalidArgument("Specify exactly one non-empty 'templateId' or 'source'.");
            if (inputs is { ValueKind: not JsonValueKind.Object })
                throw KernelException.InvalidArgument("'inputs' must be an object.");

            var nextInputs = JsonHelpers.EnsureClonedObject(inputs);
            CompiledPolicy policy;
            JsonElement state;
            try
            {
                if (templateId is not null)
                    policy = _templateCatalog.GetRequiredPolicy(templateId);
                else if (_templateSnapshotSource is { } existing && source == _presentedTemplateSnapshotSource)
                    policy = _templateCatalog.GetOrCompileSnapshot(existing);
                else
                    policy = _templateCatalog.CompileSource(source!);
                TemplateFlow.ValidateInputs(_policyVm, policy, nextInputs);
                var init = _policyVm.InitState(policy, nextInputs);
                state = (init.Output.ValueKind != JsonValueKind.Undefined ? init.Output : init.NextState).Clone();
            }
            catch (KernelException) { throw; }
            catch (Exception ex)
            {
                throw KernelException.InvalidArgument($"Template compilation or initialization failed: {ex.Message}");
            }

            var data = _track.Data.ValueKind == JsonValueKind.Object ? _track.Data : JsonHelpers.EmptyObject;
            if (resetData || !HasBusinessDataFields(data))
            {
                try { data = _policyVm.InitData(policy, nextInputs); }
                catch (InvalidOperationException ex) { throw KernelException.InvalidArgument(ex.Message); }
            }
            else
            {
                ValidateTrackDataCompatibilityCore(policy, nextInputs, data,
                    "Existing track data is incompatible with the new template",
                    ". Migrate data or explicitly pass resetData: true.");
            }
            var dataRev = JsonElement.DeepEquals(data, _track.Data) ? _track.DataRev : checked(_track.DataRev + 1);
            var warnings = await TemplateWarningsCoreAsync(policy, nextInputs, ct).ConfigureAwait(false);
            var now = NowMs();
            var nextTrack = _track with
            {
                TemplateId = policy.TemplateId,
                TemplateInputs = nextInputs,
                TemplateState = state,
                Data = data,
                DataRev = dataRev,
                EnabledPlugins = _track.EnabledPlugins.Concat(policy.Manifest.DefaultPlugins).Distinct(StringComparer.Ordinal).ToArray(),
                UpdatedAtMs = now
            };
            var (nextFlow, flowUpdate) = PreviewTemplateFlowCore(nextTrack, policy);
            if (flowUpdate is not null)
                nextTrack = nextTrack with { FlowRev = nextFlow.Rev, FlowSpecJson = TrackFlowSerializer.SerializeCanonical(nextFlow) };
            if (dryRun) return new TemplateSetResult(TemplatePresentation.Project(nextTrack, policy, _policyVm), warnings, true);
            var evt = new EventEnvelopeDto(null, nextTrack.AreaId, TrackId, KernelEventKinds.TrackUpdated,
                JsonHelpers.ToElement(TemplatePresentation.Project(nextTrack, policy, _policyVm)), now);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            var snapshotSource = TemplateHost.Freeze(policy);
            await _store.UpdateTrackTemplateAndEmitAsync(nextTrack, snapshotSource, evt, ct, writeData: dataRev != _track.DataRev, flow: flowUpdate).ConfigureAwait(false);
            _policy = policy;
            _policyUnavailableReason = null;
            _policyGeneration = Guid.NewGuid().ToString("N");
            _templateSnapshotSource = snapshotSource;
            _presentedTemplateSnapshotSource = TemplateHost.InspectSource(snapshotSource);
            var pluginsChanged = !_track.EnabledPlugins.SequenceEqual(nextTrack.EnabledPlugins, StringComparer.Ordinal);
            _track = nextTrack;
            if (flowUpdate is not null)
            {
                _flowSpec = nextFlow;
                foreach (var task in flowUpdate.Tasks) _taskCoordinator.ReplaceTaskInMemoryCore(task);
                await ProjectCommittedFlowFileCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (pluginsChanged)
                _mcpBridge.UpdateTrackPlugins(TrackId, nextTrack.EnabledPlugins);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
            if (flowUpdate is not null)
            {
                await EvaluateFlowAfterTriggersCoreAsync(CancellationToken.None).ConfigureAwait(false);
                await _taskCoordinator.ScheduleEligibleTasksCoreAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return new TemplateSetResult(PresentTrackCore(), warnings, false);
        }, ct);
    }

    private async Task UpdateTemplateStateIfChangedCoreAsync(JsonElement nextState, CancellationToken ct, bool emitEvent = false)
    {
        if (nextState.ValueKind == JsonValueKind.Undefined || JsonElement.DeepEquals(_track.TemplateState, nextState))
        {
            return;
        }

        var nextTrack = _track with { TemplateState = nextState.Clone(), UpdatedAtMs = NowMs() };
        var evt = emitEvent ? new EventEnvelopeDto(null, nextTrack.AreaId, TrackId, KernelEventKinds.TrackUpdated,
            JsonHelpers.ToElement(TemplatePresentation.Project(nextTrack, _policy, _policyVm)), nextTrack.UpdatedAtMs) : null;
        if (evt is not null) await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpdateTrackAndEmitAsync(nextTrack, evt, ct).ConfigureAwait(false);
        _track = nextTrack;
        if (evt is not null) await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
    }

}
