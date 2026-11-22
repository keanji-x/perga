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

    private Task<TrackDto> DetachTemplateCoreAsync(bool dryRun, CancellationToken ct, string? callerSessionId = null) =>
        ApplyTemplateBindingCoreAsync(_track with
        {
            TemplateId = null,
            TemplateInputs = JsonHelpers.EmptyObject,
            TemplateState = JsonHelpers.EmptyObject,
            UpdatedAtMs = NowMs()
        }, null, dryRun, ct, callerSessionId);

    private async Task<TrackDto> ApplyTemplateBindingCoreAsync(
        TrackDto next, CompiledPolicy? policy, bool dryRun, CancellationToken ct, string? callerSessionId)
    {
        var (spec, flow, stoppingNodes) = await PrepareTemplateFlowCoreAsync(next, policy, ct).ConfigureAwait(false);
        var activeToStop = _taskCoordinator.Snapshot().Where(task => stoppingNodes.Contains(task.TaskKey) && _taskCoordinator.HasActiveRun(task.Id)).ToArray();
        var callerWake = callerSessionId is null ? null :
            activeToStop.FirstOrDefault(task => task.Kind == "flow_wake" && task.SessionId == callerSessionId);
        if (callerWake is not null)
            throw KernelException.Conflict($"Cannot change the template while its wake '{callerWake.TaskKey}' is running on the calling session.");
        var canceledRuns = activeToStop.Select(task => _taskCoordinator.GetActiveRun(task.Id)).ToArray();
        _taskCoordinator.RequireTasksOutsideVerification(canceledRuns);
        var flowChanged = spec.Rev != _flowSpec.Rev;
        if (flowChanged)
            next = next with { FlowRev = spec.Rev, FlowSpecJson = TrackFlowSerializer.SerializeCanonical(spec) };
        var presented = TemplatePresentation.Project(next, policy, _policyVm);
        if (dryRun) return presented;

        var evt = new EventEnvelopeDto(null, next.AreaId, TrackId, KernelEventKinds.TrackUpdated,
            JsonHelpers.ToElement(presented), next.UpdatedAtMs);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var snapshot = policy is null ? null : TemplateHost.Freeze(policy);
        await _store.UpdateTrackTemplateAndEmitAsync(next, snapshot, evt, ct,
            writeData: next.DataRev != _track.DataRev, flow: flow).ConfigureAwait(false);
        var pluginsChanged = !_track.EnabledPlugins.SequenceEqual(next.EnabledPlugins, StringComparer.Ordinal);
        _track = next;
        _policy = policy;
        _policyUnavailableReason = null;
        _templateSnapshotSource = snapshot;
        _presentedTemplateSnapshotSource = TemplateHost.InspectSource(snapshot);
        _policyGeneration = Guid.NewGuid().ToString("N");
        _flowSpec = spec;
        if (flow is not null)
            foreach (var task in flow.Tasks) _taskCoordinator.ReplaceTaskInMemoryCore(task);
        _ = BeginFlowCancellationCore(canceledRuns, stoppingNodes);

        var lifecycle = _actorShutdownCts.Token;
        try
        {
            if (flowChanged)
                await ProjectCommittedFlowFileCoreAsync(lifecycle).ConfigureAwait(false);
            if (pluginsChanged)
                _mcpBridge.UpdateTrackPlugins(TrackId, next.EnabledPlugins);
            try
            {
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, lifecycle).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning("Could not publish template binding event for Track {0}: {1}", TrackId, ex);
            }
        }
        finally
        {
            try { await EvaluateFlowAfterTriggersCoreAsync(lifecycle).ConfigureAwait(false); }
            finally { await _taskCoordinator.ScheduleEligibleTasksCoreAsync(lifecycle).ConfigureAwait(false); }
        }
        return PresentTrackCore();
    }

    private async Task<(FlowSpecDto Spec, TrackFlowUpdate? Update, HashSet<string> StoppingNodes)> PrepareTemplateFlowCoreAsync(
        TrackDto next, CompiledPolicy? policy, CancellationToken ct)
    {
        var blueprint = policy is null ? [] : TemplateFlow.ReadSeedNodes(_policyVm, policy, next.TemplateInputs);
        var incomingIds = blueprint.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var previousById = _flowSpec.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var tasksByKey = TrackFlowReconciler.BuildTasksByKey(_taskCoordinator.Snapshot());
        var sameTemplate = _policy?.TemplateId == policy?.TemplateId;
        var seeds = new Dictionary<string, FlowNodeSpecDto>(StringComparer.Ordinal);
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        var stopping = new HashSet<string>(StringComparer.Ordinal);
        var taskUpdates = new List<TaskDto>();

        foreach (var node in blueprint)
        {
            var previous = previousById.GetValueOrDefault(node.Id);
            var task = tasksByKey.GetValueOrDefault(node.Id);
            if ((previous is not null && previous.Source != TrackScheduleSources.Template) ||
                (task is not null && (!FlowConstants.IsFlowTask(task) ||
                    (previous is null && TemplateFlow.HasIndependentSource(task)))))
                throw KernelException.Conflict($"Template Flow node '{node.Id}' already belongs to an independent task. Choose a different ID.");

            var detached = task is not null && TemplateFlow.WasDetached(task);
            if (previous is not null)
            {
                if (!detached && (previous.Canceled || (sameTemplate && TrackFlowReconciler.NodeSpecEquals(previous, node))))
                    continue;
                stopping.Add(node.Id);
            }
            else if (task is not null && !detached)
            {
                // Settled history and explicit cancellations keep their claim on the ID.
                continue;
            }

            if (previous is not null || task is not null)
                replaced.Add(node.Id);
            if (task is not null)
                taskUpdates.Add(TrackFlowReconciler.ArchiveForTemplateReplacement(task, next.UpdatedAtMs));
            seeds.Add(node.Id, node);
        }

        var outgoing = _flowSpec.Nodes.Where(node =>
            node.Source == TrackScheduleSources.Template && !node.Canceled && !incomingIds.Contains(node.Id))
            .Select(node => node with { Canceled = true }).ToDictionary(node => node.Id, StringComparer.Ordinal);
        stopping.UnionWith(outgoing.Keys);
        foreach (var node in outgoing.Values)
        {
            var canceled = TrackFlowReconciler.BuildOrUpdateTaskForNode(TrackId, node, tasksByKey.GetValueOrDefault(node.Id), next.UpdatedAtMs);
            taskUpdates.Add(TemplateFlow.MarkDetached(canceled));
        }

        var spec = outgoing.Count == 0 && seeds.Count == 0 ? _flowSpec :
            _flowSpec with
            {
                Rev = checked(_flowSpec.Rev + 1),
                Nodes = [.. _flowSpec.Nodes.Select(node => outgoing.GetValueOrDefault(node.Id, seeds.GetValueOrDefault(node.Id, node))),
                    .. seeds.Values.Where(node => !previousById.ContainsKey(node.Id))]
            };
        if (policy is not null)
            TemplateFlow.RequireValid(TrackFlowValidator.Validate(spec, _flowSpec, _taskCoordinator.Snapshot(), CollectActiveFlowTaskIdsCore(),
                _sessions, next.LeadSessionId, policy, _policyVm, next.TemplateInputs, _scheduleCronEvaluator, _timeProvider, replaced));

        var clocks = policy is null ? [] :
            await RefreshTemplateClockTextCoreAsync(spec, next, policy, ct, replaced).ConfigureAwait(false);
        if (spec.Rev == _flowSpec.Rev && clocks.Length == 0)
            return (spec, null, stopping);

        var seeded = policy is null ? null :
            TemplateFlow.BuildSeedUpdate(next, _flowSpec.Rev, seeds.Values.ToArray(), policy, _policyVm, _scheduleCronEvaluator, _timeProvider);
        var seededClocks = seeded?.Clocks ?? [];
        var activeClockIds = seededClocks.Select(clock => clock.Id).ToHashSet(StringComparer.Ordinal);
        return (spec, new TrackFlowUpdate(_flowSpec.Rev, [.. taskUpdates, .. seeded?.Tasks ?? []], [.. clocks, .. seededClocks])
        {
            CanceledClockIds = stopping.Select(FlowConstants.MakeScheduleId).Where(id => !activeClockIds.Contains(id)).ToArray(),
            ResetClockIds = replaced.Select(FlowConstants.MakeScheduleId).ToHashSet(StringComparer.Ordinal)
        }, stopping);
    }

    private async Task<TrackScheduleDto[]> RefreshTemplateClockTextCoreAsync(
        FlowSpecDto spec, TrackDto next, CompiledPolicy policy, CancellationToken ct, IReadOnlySet<string>? replaced = null)
    {
        var templateNodes = spec.Nodes.Where(node => node.Source == TrackScheduleSources.Template && !node.Canceled &&
                replaced?.Contains(node.Id) != true)
            .ToDictionary(node => node.Id, StringComparer.Ordinal);
        if (templateNodes.Count == 0) return [];

        var updates = new List<TrackScheduleDto>();
        foreach (var clock in await ListTrackSchedulesSafeCoreAsync(ct).ConfigureAwait(false))
        {
            if (clock.State == TrackScheduleStates.Canceled || clock.NodeKey is null ||
                !templateNodes.TryGetValue(clock.NodeKey, out var node))
                continue;
            var (title, prompt) = TrackFlowReconciler.ScheduleText(node);
            var updated = TemplateFlow.ResolveClockText(clock with { Title = title, Prompt = prompt }, next, policy, _policyVm);
            if (updated.Title != clock.Title || updated.Prompt != clock.Prompt)
                updates.Add(updated);
        }
        return updates.ToArray();
    }

    public Task<TrackDto> UpdateTemplateInputsAsync(JsonElement patch, CancellationToken ct = default) =>
        UpdateTemplateInputsCoreAsync(null, patch, null, ct);

    public Task<TrackDto> UpdateTemplateInputsAsync(string sessionId, JsonElement patch, string generation, CancellationToken ct) =>
        UpdateTemplateInputsCoreAsync(sessionId, patch, generation, ct);

    private Task<TrackDto> UpdateTemplateInputsCoreAsync(string? sessionId, JsonElement patch, string? generation, CancellationToken ct) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        if (sessionId is not null) RequireTemplateLeadCore(sessionId, KernelMcpToolNames.TemplateUpdateInputs);
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
        var clocks = await RefreshTemplateClockTextCoreAsync(_flowSpec, next, _policy, ct).ConfigureAwait(false);
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

    private void RequireTemplateLeadCore(string sessionId, string toolName)
    {
        var session = RequireGovernanceSessionCore(sessionId, toolName);
        if (session.Id != _track.LeadSessionId || session.ArchivedAtMs is not null)
            throw KernelException.Forbidden("Only the current Track Lead can change the template.");
    }

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
            RequireTemplateLeadCore(callerSessionId, KernelMcpToolNames.TrackSet);
            if (_policyGeneration != expectedPolicyGeneration)
                throw KernelException.PolicyDenied("Active policy changed after template application was authorized.");
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot set a template on a closed track.");
            if (clear && (templateId is not null || source is not null || inputs is not null || resetData))
                throw KernelException.InvalidArgument("clear cannot be combined with a template, inputs, or resetData.");
            if (clear)
            {
                return new TemplateSetResult(await DetachTemplateCoreAsync(dryRun, ct, callerSessionId).ConfigureAwait(false), [], dryRun);
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
            var binding = await ApplyTemplateBindingCoreAsync(nextTrack, policy, dryRun, ct, callerSessionId).ConfigureAwait(false);
            return new TemplateSetResult(binding, warnings, dryRun);
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
