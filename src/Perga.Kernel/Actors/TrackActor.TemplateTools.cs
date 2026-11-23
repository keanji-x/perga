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
    public Task<JsonElement> InspectCurrentTemplateAsync(string sessionId, CancellationToken ct,
        string? format = null, string? file = null) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        RequireGovernanceSessionCore(sessionId, KernelMcpToolNames.TemplateRead);
        var header = TemplatePresentation.Evaluate(_track, _policy, _policyVm);
        return TemplateInspection.Project(JsonHelpers.ToElement(new
        {
            manifest = _policy?.Manifest, inputs = _track.TemplateInputs, data = _track.Data,
            headerView = TemplatePresentation.Project(_track, _policy, header.View).TemplateHeaderView,
            headerViewError = header.Error
        }), _templateSnapshotSource, "snapshot", format, file);
    }, ct);

    public Task<JsonElement> InspectCurrentTemplateAsync(CancellationToken ct = default) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        return InspectCurrentTemplateCore();
    }, ct);

    private JsonElement InspectCurrentTemplateCore() => JsonHelpers.ToElement(new
    {
        snapshot = _templateSnapshotSource, manifest = _policy?.Manifest,
        inputs = _track.TemplateInputs, data = _track.Data, headerView = PresentTrackCore().TemplateHeaderView
    });

    public Task<TrackDto> ClearTemplateAsync(CancellationToken ct = default) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        if (_track.ClosedAtMs is not null) throw KernelException.Conflict("Cannot clear a template on a closed track.");
        return (await DetachTemplateCoreAsync(false, ct).ConfigureAwait(false)).Track;
    }, ct);

    private Task<TemplateSetResult> DetachTemplateCoreAsync(
        bool dryRun, CancellationToken ct, string? callerSessionId = null, bool resetData = false,
        InteropPrincipal.Session? authorization = null)
    {
        var data = resetData ? JsonHelpers.EmptyObject : _track.Data;
        return ApplyTemplateBindingCoreAsync(_track with
        {
            TemplateId = null,
            TemplateInputs = JsonHelpers.EmptyObject,
            Data = data,
            DataRev = JsonElement.DeepEquals(data, _track.Data) ? _track.DataRev : checked(_track.DataRev + 1),
            UpdatedAtMs = NowMs()
        }, null, dryRun, ct, callerSessionId, resetData ? "reset" : "preserve", [], null, authorization);
    }

    private async Task<TemplateSetResult> ApplyTemplateBindingCoreAsync(
        TrackDto next, CompiledPolicy? policy, bool dryRun, CancellationToken ct, string? callerSessionId,
        string dataAction, IReadOnlyList<string> templateWarnings, TemplateReportSeed? reportSeed,
        InteropPrincipal.Session? authorization)
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
        var header = TemplatePresentation.Evaluate(next, policy, _policyVm);
        var presented = TemplatePresentation.Project(next, policy, header.View);
        var warnings = new List<string>(templateWarnings);
        if (header.Error is { } error)
            warnings.Add($"headerView: {error}");
        var currentDiagnostics = ReportWidgetValidator.Diagnose(_reports.Document, _policy, _policyVm, _track.TemplateInputs).ToHashSet();
        foreach (var diagnostic in ReportWidgetValidator.Diagnose(_reports.Document, policy, _policyVm, next.TemplateInputs))
            if (!currentDiagnostics.Contains(diagnostic))
                warnings.Add($"Report widget '{diagnostic.BlockId}': {diagnostic.Message}");

        var diff = TrackFlowReconciler.ComputePlanDiff(_flowSpec, spec, new HashSet<string>());
        var plan = new TemplateSetPlanDto(new(diff.Added, diff.Updated, diff.Canceled), dataAction,
            reportSeed is null ? "keep" : "seed",
            next.EnabledPlugins.Except(_track.EnabledPlugins, StringComparer.Ordinal).ToArray());
        var result = new TemplateSetResult(presented, warnings, dryRun, plan);
        if (dryRun)
        {
            RequireMcpAuthorizationCore(authorization);
            return result;
        }

        var evt = new EventEnvelopeDto(null, next.AreaId, TrackId, KernelEventKinds.TrackUpdated,
            JsonHelpers.ToElement(presented), next.UpdatedAtMs);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        var snapshot = policy?.JsSource;
        if (flow is not null)
            flow = flow with { SettlingTasks = _taskCoordinator.FlowSettlementsCore(flow.Tasks) };
        var settled = await _store.UpdateTrackTemplateAndEmitAsync(next, snapshot, evt, ct,
            writeData: next.DataRev != _track.DataRev, flow: flow, reportSeed: reportSeed,
            beforeWrite: CaptureMcpWriteFenceCore(authorization)).ConfigureAwait(false);
        var pluginsChanged = !_track.EnabledPlugins.SequenceEqual(next.EnabledPlugins, StringComparer.Ordinal);
        _track = next;
        if (reportSeed is not null)
            _reports.Hydrate(reportSeed.Document);
        _policy = policy;
        _policyUnavailableReason = null;
        _templateSnapshotSource = snapshot;
        _policyGeneration = Guid.NewGuid().ToString("N");
        _flowSpec = spec;
        if (flowChanged)
            MarkSemanticFlowRevisionCore(spec.Rev);
        if (flow is not null)
            foreach (var task in flow.Tasks) _taskCoordinator.ReplaceTaskInMemoryCore(task);
        _ = BeginFlowCancellationCore(canceledRuns, stoppingNodes);
        foreach (var (sessionId, settlement) in settled)
            _taskCoordinator.ApplyTaskSessionSettlementCore(sessionId, settlement);

        var lifecycle = _actorShutdownCts.Token;
        try
        {
            if (flowChanged)
            {
                try { await ProjectCommittedFlowFileCoreAsync(lifecycle).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning("Could not project committed Flow file for Track {0}: {1}", TrackId, ex);
                }
            }
            if (pluginsChanged)
            {
                try { _mcpBridge.UpdateTrackPlugins(TrackId, next.EnabledPlugins); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning("Could not update MCP plugins for Track {0}: {1}", TrackId, ex);
                }
            }
            try
            {
                await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, lifecycle).ConfigureAwait(false);
                if (reportSeed is not null)
                    await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, reportSeed.Event, lifecycle, allowFallback: false)
                        .ConfigureAwait(false);
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
        if (flowChanged)
            await TryPokeLeadForPendingWakesCoreAsync().ConfigureAwait(false);
        return result with { Track = TemplatePresentation.Project(_track, _policy, header.View) };
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
            var detached = task is not null && TemplateFlow.WasDetached(task);
            if ((previous is not null && previous.Source != TrackScheduleSources.Template) ||
                (task is not null && !FlowConstants.IsFlowTask(task)))
                throw KernelException.Conflict($"Template Flow node '{node.Id}' already belongs to an independent task. Choose a different ID.");

            if (previous is null && task is not null)
            {
                var source = TemplateFlow.GetSource(task);
                if (source is null && !detached)
                    throw KernelException.Conflict($"Template Flow node '{node.Id}' is claimed by a task with unknown Flow provenance. Choose a different ID.");
                if (source is not null && source != TrackScheduleSources.Template)
                    throw KernelException.Conflict($"Template Flow node '{node.Id}' already belongs to an independent task. Choose a different ID.");
            }

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
                _sessions, next.LeadSessionId, policy, _policyVm, next.TemplateInputs, _scheduleCronEvaluator, _timeProvider, replaced,
                await GetFlowWorkerCatalogCoreAsync(spec, ct).ConfigureAwait(false)));

        if (replaced.Count > 0)
        {
            // A fresh task restarts its run count, so its first wave can reuse a retained consumer's key.
            foreach (var node in spec.Nodes)
            {
                if (node.Canceled || seeds.ContainsKey(node.Id) ||
                    node.When?.After?.Any(dep => replaced.Contains(dep.Node)) != true ||
                    !tasksByKey.TryGetValue(node.Id, out var task) || task.TriggerRunKey is null)
                    continue;
                taskUpdates.Add(task with
                {
                    TriggerRunKey = null,
                    Ready = task.Status != TaskRunStatus.Pending && task.Ready,
                    UpdatedAtMs = next.UpdatedAtMs
                });
            }
        }

        var clocks = policy is null ? [] :
            await RefreshTemplateClockTextCoreAsync(spec, next, policy, ct, replaced).ConfigureAwait(false);
        var retained = taskUpdates.ToDictionary(task => task.Id, StringComparer.Ordinal);
        foreach (var task in RefreshRoutedFlowAccessCore(spec, next, policy, tasksByKey, stopping, retained))
            retained[task.Id] = task;
        if (spec.Rev == _flowSpec.Rev && clocks.Length == 0 && retained.Count == 0)
            return (spec, null, stopping);

        var seeded = policy is null ? null :
            TemplateFlow.BuildSeedUpdate(next, _flowSpec.Rev, seeds.Values.ToArray(), policy, _policyVm, _scheduleCronEvaluator, _timeProvider, _sessions);
        var seededClocks = seeded?.Clocks ?? [];
        var activeClockIds = seededClocks.Select(clock => clock.Id).ToHashSet(StringComparer.Ordinal);
        return (spec, new TrackFlowUpdate(_flowSpec.Rev, [.. retained.Values, .. seeded?.Tasks ?? []], [.. clocks, .. seededClocks])
        {
            CanceledClockIds = stopping.Select(FlowConstants.MakeScheduleId).Where(id => !activeClockIds.Contains(id)).ToArray(),
            ResetClockIds = replaced.Select(FlowConstants.MakeScheduleId).ToHashSet(StringComparer.Ordinal)
        }, stopping);
    }

    private IEnumerable<TaskDto> RefreshRoutedFlowAccessCore(FlowSpecDto spec, TrackDto next, CompiledPolicy? policy,
        IReadOnlyDictionary<string, TaskDto> tasksByKey, IReadOnlySet<string>? stopping = null,
        IReadOnlyDictionary<string, TaskDto>? updates = null)
    {
        foreach (var node in spec.Nodes)
        {
            if (stopping?.Contains(node.Id) == true || !tasksByKey.TryGetValue(node.Id, out var task))
                continue;
            task = updates?.GetValueOrDefault(task.Id) ?? task;
            if (task is not { Status: TaskRunStatus.Pending, ArchivedAtMs: null } || _taskCoordinator.HasActiveRun(task.Id))
                continue;

            var access = FlowTaskRouting.ResolveAccess(node, policy, _policyVm, next.TemplateInputs, _sessions);
            if (access is { } effective && effective != task.Access)
                yield return task with { Access = effective, UpdatedAtMs = next.UpdatedAtMs };
        }
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
        ReadUpdatedTemplateTrackAsync(patch, ct);

    private async Task<TrackDto> ReadUpdatedTemplateTrackAsync(JsonElement patch, CancellationToken ct) =>
        (await UpdateTemplateInputsCoreAsync(null, patch, null, ct).ConfigureAwait(false)).Track;

    public Task<TemplateInputsUpdateResult> UpdateTemplateInputsAsync(string sessionId, JsonElement patch, string generation, CancellationToken ct, InteropPrincipal.Session? authorization = null) =>
        UpdateTemplateInputsCoreAsync(sessionId, patch, generation, ct, authorization);

    private Task<TemplateInputsUpdateResult> UpdateTemplateInputsCoreAsync(string? sessionId, JsonElement patch, string? generation, CancellationToken ct, InteropPrincipal.Session? authorization = null) => EnqueueAsync(async _ =>
    {
        await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
        RequireMcpAuthorizationCore(authorization);
        if (sessionId is not null) RequireTemplateLeadCore(sessionId, KernelMcpToolNames.TemplateUpdateInputs);
        if (generation is not null && _policyGeneration != generation) throw KernelException.PolicyDenied("Active policy changed after authorization.");
        if (_track.ClosedAtMs is not null) throw KernelException.Conflict("Cannot update a closed track.");
        if (_policy is null) throw KernelException.Conflict("The track has no active template.");
        if (patch.ValueKind != JsonValueKind.Object) throw KernelException.InvalidArgument("inputs must be an object.");
        var schema = _policy.Manifest.InputsSchema;
        if (schema.EnumerateObject().Any())
        {
            foreach (var entry in patch.EnumerateObject())
                if (!schema.TryGetProperty(entry.Name, out var _))
                    throw KernelException.InvalidArgument($"Unknown template input '{entry.Name}'. Allowed inputs: " +
                        string.Join(", ", schema.EnumerateObject().Select(property => property.Name)) + ".");
        }
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
            _sessions, next.LeadSessionId, _policy, _policyVm, next.TemplateInputs, _scheduleCronEvaluator, _timeProvider,
            workerCatalog: await GetFlowWorkerCatalogCoreAsync(_flowSpec, ct).ConfigureAwait(false)));
        var clocks = await RefreshTemplateClockTextCoreAsync(_flowSpec, next, _policy, ct).ConfigureAwait(false);
        var updates = RefreshRoutedFlowAccessCore(_flowSpec, next, _policy,
            TrackFlowReconciler.BuildTasksByKey(_taskCoordinator.Snapshot())).ToArray();
        var flow = clocks.Length == 0 && updates.Length == 0 ? null : new TrackFlowUpdate(_flowSpec.Rev, updates, clocks);
        var header = TemplatePresentation.Evaluate(next, _policy, _policyVm);
        var presented = TemplatePresentation.Project(next, _policy, header.View);
        var warnings = new List<string>();
        if (sessionId is not null)
        {
            warnings.AddRange(await TemplateWarningsCoreAsync(_policy, next.TemplateInputs, ct).ConfigureAwait(false));
            if (header.Error is { } error)
                warnings.Add($"headerView: {error}");
        }
        var evt = new EventEnvelopeDto(null, next.AreaId, TrackId, KernelEventKinds.TrackUpdated,
            JsonHelpers.ToElement(presented), next.UpdatedAtMs);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.UpdateTrackTemplateAndEmitAsync(next, _policy.JsSource, evt, ct, flow: flow,
            beforeWrite: CaptureMcpWriteFenceCore(authorization)).ConfigureAwait(false);
        _track = next;
        foreach (var task in updates) _taskCoordinator.ReplaceTaskInMemoryCore(task);
        _policyGeneration = Guid.NewGuid().ToString("N");
        await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
        return new TemplateInputsUpdateResult(presented, warnings);
    }, ct);

    private void RequireTemplateLeadCore(string sessionId, string toolName)
    {
        var session = RequireGovernanceSessionCore(sessionId, toolName);
        if (session.Id != _track.LeadSessionId || session.ArchivedAtMs is not null)
            throw KernelException.Forbidden("Only the current Track Lead can change the template.");
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

        RequireCompatibleTrackData(errors, prefix, suffix);
    }

    private static void RequireCompatibleTrackData(IReadOnlyList<DataValidationError> errors, string prefix, string suffix)
    {
        if (errors.Count > 0)
            throw KernelException.Conflict(prefix + ": " +
                string.Join("; ", errors.Select(e => $"{(string.IsNullOrEmpty(e.Path) ? "$" : e.Path)}: {e.Message}")) + suffix);
    }

    private static bool ValidateTemplateSetArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            throw KernelException.InvalidArgument("Template arguments must be an object.");
        foreach (var flag in new[] { "dryRun", "clear", "resetData", "seedReport" })
            if (arguments.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw KernelException.InvalidArgument(flag + " must be a boolean.");
        return arguments.TryGetProperty("dryRun", out var dryRun) && dryRun.ValueKind == JsonValueKind.True;
    }

    private async Task<IReadOnlyList<string>> TemplateWarningsCoreAsync(CompiledPolicy policy, JsonElement inputs, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (policy.Manifest.DefaultPlugins.Count > 0 && _mcpBridge.ConfiguredPluginIds is { } configured)
        {
            foreach (var id in policy.Manifest.DefaultPlugins)
                if (!id.Equals("perga", StringComparison.OrdinalIgnoreCase) &&
                    !id.Equals("vcs", StringComparison.OrdinalIgnoreCase) &&
                    !configured.Contains(id, StringComparer.Ordinal))
                    warnings.Add($"Template plugin '{id}' is not configured on this host.");
        }
        if (policy.Manifest.WorkspaceDriver is not null || policy.Manifest.WorkspaceLowerCacheDirs.Count > 0 ||
            policy.Manifest.WorkspaceCreateCmd is not null || policy.Manifest.WorkspaceCloseCmd is not null)
            warnings.Add("Template workspace settings are ignored for an existing track.");
        var targets = _policyVm.ResolveRoutingTargets(policy, inputs)
            .Where(route => route.Provider is not null || route.Model is not null).ToArray();
        if (targets.Length > 0)
        {
            var providers = await EffectiveAgentRuntime.ProbeProvidersAsync(ct).ConfigureAwait(false);
            foreach (var route in targets)
            {
                var provider = route.Provider ?? GetDefaultProviderCore();
                var normalizedProvider = SessionLaunchValidator.NormalizeProvider(provider);
                var model = route.Model;
                if (!providers.Any(p => p.Available && SessionLaunchValidator.NormalizeProvider(p.Provider) == normalizedProvider &&
                    (model is null || p.Models.Contains(model, StringComparer.Ordinal))))
                {
                    var alternative = route.Provider is null && model is not null
                        ? providers.FirstOrDefault(p => p.Available && p.Models.Contains(model, StringComparer.Ordinal))
                        : null;
                    warnings.Add(alternative is null
                        ? $"Template provider/model is unavailable: {provider}/{model}."
                        : $"Template model '{model}' is unavailable on default provider '{provider}'. Specify provider '{alternative.Provider}' in the routing rule.");
                }
            }
        }
        return warnings.Distinct(StringComparer.Ordinal).ToArray();
    }
    public async Task<TrackDto> SetTemplateBySessionAsync(string callerSessionId, string? templateId,
        string? source, string expectedPolicyGeneration, JsonElement? inputs, CancellationToken ct = default, bool resetData = false) =>
        (await SetTemplateWithOptionsAsync(callerSessionId, templateId, source, expectedPolicyGeneration, inputs, false, false, ct, resetData).ConfigureAwait(false)).Track;

    private TemplateReportSeed? PrepareTemplateReportSeedCore(
        TrackDto next, CompiledPolicy policy, bool? seedReport, string? callerSessionId)
    {
        var previous = _reports.Document;
        var isBlank = TemplateReport.IsBlank(previous);
        if (seedReport == true && !isBlank)
            throw KernelException.Conflict("Cannot seed a non-empty report.");
        if (seedReport == false || !isBlank || string.IsNullOrWhiteSpace(policy.Manifest.InitialReportMarkdown))
            return null;

        var document = TemplateReport.Create(_reportEngine, _policyVm, policy, next.TemplateInputs,
            rev: checked(previous.DocRev + 1), summary: previous.Summary);
        var changes = document.Blocks.Select((block, index) =>
            new BlockChangeRecord(block.Id, block.Kind, null, block.Rev, index, false)).ToArray();
        var evt = new EventEnvelopeDto(null, next.AreaId, TrackId, KernelEventKinds.ReportUpdated,
            JsonHelpers.ToElement(new { document.DocRev, document.Summary, changes }), next.UpdatedAtMs);
        return new(document, _reportEngine.CompressZstd(document.Blocks), callerSessionId, evt);
    }

    public async Task<TemplateSetResult> SetTemplateWithOptionsAsync(string callerSessionId, string? templateId,
        string? source, string expectedPolicyGeneration, JsonElement? inputs, bool dryRun, bool clear,
        CancellationToken ct = default, bool resetData = false, string? path = null, bool? seedReport = null,
        InteropPrincipal.Session? authorization = null)
    {
        string? workspaceSource = null;
        if (!clear && path is not null && templateId is null && source is null && !string.IsNullOrWhiteSpace(path))
        {
            var (cwd, targetId) = await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                RequireMcpAuthorizationCore(authorization);
                RequireTemplateLeadCore(callerSessionId, KernelMcpToolNames.TrackSet);
                if (_policyGeneration != expectedPolicyGeneration)
                    throw KernelException.PolicyDenied("Active policy changed after template application was authorized.");
                if (_track.ClosedAtMs is not null)
                    throw KernelException.Conflict("Cannot set a template on a closed track.");
                return (_track.Cwd, _track.ExecutionTargetId);
            }, ct).ConfigureAwait(false);

            Interlocked.Increment(ref _inFlightExternalOps);
            try
            {
                workspaceSource = await Task.Run(() => WorkspaceTemplateSource.Read(cwd, targetId, path), ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightExternalOps);
            }
        }

        return await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireMcpAuthorizationCore(authorization);
            RequireTemplateLeadCore(callerSessionId, KernelMcpToolNames.TrackSet);
            if (_policyGeneration != expectedPolicyGeneration)
                throw KernelException.PolicyDenied("Active policy changed after template application was authorized.");
            if (_track.ClosedAtMs is not null)
                throw KernelException.Conflict("Cannot set a template on a closed track.");
            return await ApplyTemplateSelectionCoreAsync(
                callerSessionId, templateId, source, workspaceSource, path, inputs,
                dryRun, clear, resetData, seedReport, authorization, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<TemplateSetResult> ApplyTemplateSelectionCoreAsync(
        string? callerSessionId,
        string? templateId,
        string? source,
        string? workspaceSource,
        string? path,
        JsonElement? inputs,
        bool dryRun,
        bool clear,
        bool resetData,
        bool? seedReport,
        InteropPrincipal.Session? authorization,
        CancellationToken ct)
    {
        var selectors = new[] { templateId, source, path }.OfType<string>().ToArray();
        if (clear && (selectors.Length > 0 || inputs is not null || seedReport is not null))
            throw KernelException.InvalidArgument("clear cannot be combined with a template, inputs, or seedReport.");
        if (clear)
            return await DetachTemplateCoreAsync(dryRun, ct, callerSessionId, resetData, authorization).ConfigureAwait(false);

        if (selectors.Length != 1 || string.IsNullOrWhiteSpace(selectors[0]))
            throw KernelException.InvalidArgument(path is null && callerSessionId is null
                ? "Specify exactly one non-empty 'templateId' or 'source'."
                : "Specify exactly one non-empty 'templateId', 'source', or 'path'.");
        if (inputs is { ValueKind: not JsonValueKind.Object })
            throw KernelException.InvalidArgument("'inputs' must be an object.");

        CompiledPolicy policy;
        JsonElement nextInputs;
        try
        {
            if (templateId is not null)
                policy = _templateCatalog.GetRequiredPolicy(templateId);
            else
                policy = _templateCatalog.GetOrCompileSnapshot(workspaceSource ?? source!);
            var preserveInputs = inputs is null && !resetData && _track.TemplateId is not null &&
                string.Equals(_track.TemplateId, policy.TemplateId, StringComparison.Ordinal);
            nextInputs = preserveInputs
                ? JsonHelpers.EnsureClonedObject(_track.TemplateInputs)
                : JsonHelpers.EnsureClonedObject(inputs);
            TemplateFlow.ValidateInputs(_policyVm, policy, nextInputs);
        }
        catch (KernelException) { throw; }
        catch (Exception ex)
        {
            throw KernelException.InvalidArgument($"Template compilation or input validation failed: {ex.Message}");
        }

        var data = _track.Data.ValueKind == JsonValueKind.Object ? _track.Data : JsonHelpers.EmptyObject;
        var dataAction = resetData ? "reset" : HasBusinessDataFields(data) ? "preserve" : "init";
        if (dataAction != "preserve")
        {
            try { data = _policyVm.InitData(policy, nextInputs); }
            catch (InvalidOperationException ex) { throw KernelException.InvalidArgument(ex.Message); }
        }
        else
        {
            DataPatchValidationResult upgraded;
            try
            {
                var knownTaskKeys = _taskCoordinator.Snapshot().Select(task => task.TaskKey).ToArray();
                upgraded = _policyVm.ValidateAndPatchData(policy, nextInputs, data, JsonHelpers.EmptyObject,
                    knownTaskKeys, _timeProvider.GetUtcNow().ToString("O"), "template:upgrade");
            }
            catch (InvalidOperationException ex)
            {
                throw KernelException.InvalidArgument($"Template data schema evaluation failed: {ex.Message}");
            }
            RequireCompatibleTrackData(upgraded.Errors,
                "Existing track data is incompatible with the new template",
                ". Existing data can be preserved only when it satisfies the new template schema. Pass resetData: true to initialize fresh data.");
            data = upgraded.NextData;
        }
        var dataRev = JsonElement.DeepEquals(data, _track.Data) ? _track.DataRev : checked(_track.DataRev + 1);
        var warnings = await TemplateWarningsCoreAsync(policy, nextInputs, ct).ConfigureAwait(false);
        RequireMcpAuthorizationCore(authorization);
        var now = NowMs();
        var nextTrack = _track with
        {
            TemplateId = policy.TemplateId,
            TemplateInputs = nextInputs,
            Data = data,
            DataRev = dataRev,
            EnabledPlugins = _track.EnabledPlugins.Concat(policy.Manifest.DefaultPlugins).Distinct(StringComparer.Ordinal).ToArray(),
            UpdatedAtMs = now
        };
        var reportSeed = PrepareTemplateReportSeedCore(nextTrack, policy, seedReport, callerSessionId);
        return await ApplyTemplateBindingCoreAsync(nextTrack, policy, dryRun, ct, callerSessionId,
            dataAction, warnings, reportSeed, authorization).ConfigureAwait(false);
    }
}
