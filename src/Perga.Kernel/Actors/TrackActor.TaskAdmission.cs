using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private readonly Dictionary<string, (int Generation, string PolicyGeneration, string DispatchFields)> _taskDispatchApprovals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingTaskDispatchApprovals = new(StringComparer.Ordinal);
    private bool _taskProviderProbePending;

    private (PolicyTaskRoute Route, bool UsesPreparedDefault) RouteTaskCore(TaskDto task)
    {
        RequirePolicyAvailableCore();
        var model = task.Context is { ValueKind: JsonValueKind.Object } context &&
            context.TryGetProperty("initialModel", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var reuseFlowSession = FlowConstants.IsFlowSessionReuse(task);
        var session = !FlowConstants.IsFlowTask(task) || reuseFlowSession ? ReadTaskDispatchSessionCore(task) : null;
        var provider = task.Provider;
        if (reuseFlowSession)
        {
            if (session is null || session.ArchivedAtMs is not null || session.IsLead)
                throw KernelException.PolicyDenied("The flow task's reuse session is unavailable.");
            if ((provider is not null && provider != session.Provider) || (model is not null && model != session.Model))
                throw KernelException.PolicyDenied("Requested provider/model differ from the reuse session.");
            if (ReadTaskReasoningEffort(task) is { } effort && effort != session.ReasoningEffort)
                throw KernelException.PolicyDenied("Requested reasoningEffort differs from the reuse session.");
            if (ReadTaskPermissionMode(task) is { } permissionMode && permissionMode != session.PermissionMode)
                throw KernelException.PolicyDenied("Requested permissionMode differs from the reuse session.");
            provider = session.Provider;
            model = session.Model;
        }

        var route = _policy is null ? new PolicyTaskRoute(provider, model) : _policyVm.RouteTaskDispatch(_policy,
            task.Role, ReadTaskRequestedAccessCore(task), provider, model, _track.TemplateInputs);
        if (route.Denial is null)
            route = route with { Model = SessionLaunchValidator.NormalizeRequestedModel(route.Model) };
        if (FlowConstants.IsFlowSessionReuse(task) && route.Denial is null && route.Model != model)
            throw KernelException.PolicyDenied($"Template routing requires model '{route.Model}' on the reuse session.");
        if (session is null) return (route, false);
        // Reused sessions can carry a Track default that the catalog does not list.
        var usesPreparedDefault = route.Model is null && task.RunCount == 0 &&
            session is { ConfigVersion: 0 };
        return (route with { Provider = session.Provider, Model = session.Model }, usesPreparedDefault);
    }

    private string? ReadTaskRequestedAccessCore(TaskDto task)
    {
        if (!FlowConstants.IsFlowTask(task) || task.Kind == "command")
            return FormatAccessModeForPolicy(task.Access);
        var node = _flowSpec.Nodes.FirstOrDefault(node => node.Id == task.TaskKey);
        return node?.Do.Kind == "run" ? FlowTaskRouting.RequestedAccess(node) : FormatAccessModeForPolicy(task.Access);
    }

    private GuardDecision EvaluateTaskDispatchCore(TaskDto task, PolicyTaskRoute route)
    {
        if (_policy is null)
            return new GuardDecision.Allow();
        var effectiveProvider = route.Provider ?? GetDefaultProviderCore();
        var effectiveAccess = route.Access ?? FormatAccessModeForPolicy(task.Access);
        var effectivePermissionMode = ResolveEffectiveTaskPermissionModeCore(task, effectiveProvider, effectiveAccess);
        return _policyVm.Before(_policy, "task.dispatch", TrackId, JsonHelpers.ToElement(new
        {
            taskKey = task.TaskKey,
            role = task.Role,
            access = effectiveAccess,
            provider = route.Provider,
            model = route.Model,
            permissionMode = effectivePermissionMode,
            gate = task.Gate,
            acceptance = task.Acceptance
        }), _track.TemplateInputs, _track.Data);
    }

    private bool EnsureTaskDispatchApprovedCore(TaskDto task, PolicyTaskRoute route)
    {
        if (route.Denial is { } denial) throw KernelException.PolicyDenied(denial);
        if (_policy is null || _taskDispatchApprovals.TryGetValue(task.TaskKey, out var approval) &&
            approval == (task.Generation, _policyGeneration, TaskDispatchFields(task))) return true;
        if (_pendingTaskDispatchApprovals.Contains(task.Id)) return false;
        var evaluation = EvaluateTaskDispatchCore(task, route);
        if (evaluation is GuardDecision.Deny denied)
            throw KernelException.PolicyDenied(denied.Reason);
        if (_policy.Manifest.RequireGateForReadWrite && !(task.Kind == "flow_watch" && FlowConstants.IsFlowTask(task)) &&
            (route.Access ?? FormatAccessModeForPolicy(task.Access)) == "read_write" &&
            (task.Gate is null || task.Gate.Steps.Count == 0))
            throw KernelException.PolicyDenied("Template policy requires a gate for this task.");
        if (evaluation is GuardDecision.AskHuman ask)
        {
            var suspension = ApplyGuardDecisionCore(ask, _track.LeadSessionId, new ExecutionControl(), _actorShutdownCts.Token)!;
            _pendingTaskDispatchApprovals.Add(task.Id);
            var generation = _policyGeneration;
            _ = TrackBackgroundJob(() => ResumeTaskDispatchApprovalAsync(task, generation, suspension));
            return false;
        }
        _taskDispatchApprovals[task.TaskKey] = (task.Generation, _policyGeneration, TaskDispatchFields(task));
        return true;
    }

    private static string? ReadTaskReasoningEffort(TaskDto task) =>
        FlowConstants.IsFlowTask(task) && task.Context is { ValueKind: JsonValueKind.Object } context &&
        context.TryGetProperty("initialReasoningEffort", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? ReadTaskPermissionMode(TaskDto task) =>
        FlowConstants.IsFlowTask(task) && task.Context is { ValueKind: JsonValueKind.Object } context &&
        context.TryGetProperty("initialPermissionMode", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private string? ResolveEffectiveTaskPermissionModeCore(
        TaskDto task,
        string workerProvider,
        string? accessOverride = null,
        bool validateExplicit = false)
    {
        if (FlowConstants.IsFlowSessionReuse(task) && ReadTaskDispatchSessionCore(task) is { } reused)
            return reused.PermissionMode;

        var normalizedWorker = SessionLaunchValidator.NormalizeProvider(workerProvider);
        if (ReadTaskPermissionMode(task) is { } explicitMode)
        {
            return validateExplicit
                ? RuntimePermissionModes.Resolve(normalizedWorker, explicitMode, ResolvePermissionCatalogCore(normalizedWorker, null, null))
                : explicitMode.Trim();
        }

        var lead = _track.LeadSessionId is { } leadId ? _sessions.GetValueOrDefault(leadId) : null;
        var isReadOnly = accessOverride is not null
            ? string.Equals(accessOverride, "read_only", StringComparison.OrdinalIgnoreCase)
            : task.Access == AccessMode.ReadOnly;
        var scope = isReadOnly ? PermissionInheritanceScope.ReadOnlyTask : PermissionInheritanceScope.ReadWriteTask;
        return ProviderPermissionModes.ResolveInheritedMode(lead?.Provider, lead?.PermissionMode, normalizedWorker, scope);
    }

    private void ValidateTaskProviderModelCore(PolicyTaskRoute route, IReadOnlyList<ProviderStatus> providers,
        string? effort, bool validateModel, string? permissionMode = null)
    {
        var provider = SessionLaunchValidator.NormalizeProvider(route.Provider ?? GetDefaultProviderCore());
        try
        {
            var status = SessionLaunchValidator.ValidateProvider(providers, provider, explicitlySelected: true, requireAvailable: true);
            SessionLaunchValidator.ValidateModelAndEffort(provider, status, validateModel ? route.Model : null,
                route.Model ?? ResolveTaskWorkerModelCore(provider), effort, requireKnownEffort: effort is not null);
            if (permissionMode is not null)
                RuntimePermissionModes.Resolve(provider, permissionMode, ResolvePermissionCatalogCore(provider, status, null));
        }
        catch (KernelException ex) when (ex.Code == KernelErrorCode.PreconditionFailed)
        {
            throw KernelException.Conflict(ex.Message, ex.NextStep);
        }
    }

    private async Task<TrackFlowValidator.WorkerCatalog?> GetFlowWorkerCatalogCoreAsync(FlowSpecDto spec, CancellationToken ct)
    {
        if (!spec.Nodes.Any(node => !node.Canceled && (node.Do.ReasoningEffort is not null || node.Do.PermissionMode is not null)))
            return null;
        var provider = GetDefaultProviderCore();
        var providers = await ProbeSessionProvidersAsync(ct).ConfigureAwait(false);
        return new(providers, provider, ResolveTaskWorkerModelCore(provider),
            (targetProvider, status, sessionId) => ResolvePermissionCatalogCore(targetProvider, status, sessionId));
    }

    private void StartTaskProviderProbeCore()
    {
        if (_taskProviderProbePending) return;
        _taskProviderProbePending = true;
        var ct = _actorShutdownCts.Token;
        _ = TrackBackgroundJob(async () =>
        {
            IReadOnlyList<ProviderStatus>? providers = null;
            try { providers = await ProbeSessionProvidersAsync(ct).ConfigureAwait(false); }
            catch (KernelException ex) when (ex.Code == KernelErrorCode.PreconditionFailed) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }

            await EnqueueAsync(async _ =>
            {
                _taskProviderProbePending = false;
                if (providers is not null)
                    await _taskCoordinator.ScheduleEligibleTasksCoreAsync(ct, providers).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        });
    }

    private async Task<IReadOnlyList<ProviderStatus>> ProbeSessionProvidersAsync(CancellationToken ct,
        KernelErrorCode failureCode = KernelErrorCode.PreconditionFailed)
    {
        try
        {
            return await EffectiveAgentRuntime.ProbeProvidersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new KernelException(failureCode, "The provider probe is temporarily unavailable.",
                "Retry discovery with provider_list(refresh=true).", ex);
        }
    }

    private static string TaskDispatchFields(TaskDto task) => JsonSerializer.Serialize(new {
        task.Role, task.Access, task.Provider, task.Context, task.Gate, task.Acceptance
    });

    private async Task ResumeTaskDispatchApprovalAsync(TaskDto task, string generation, PolicyApproval suspension)
    {
        var pendingRemoved = false;
        try
        {
            var choice = await suspension.Decision.WaitAsync(_actorShutdownCts.Token).ConfigureAwait(false);
            await EnqueueAsync(async _ => {
                _pendingTaskDispatchApprovals.Remove(task.Id);
                pendingRemoved = true;
                var current = _taskCoordinator.Snapshot().FirstOrDefault(t => t.Id == task.Id);
                if (current is null || current.Status != TaskRunStatus.Pending) return;
                if (current.Generation == task.Generation && _policyGeneration == generation && TaskDispatchFields(current) == TaskDispatchFields(task))
                {
                    if (choice.IsApproval)
                        _taskDispatchApprovals[task.TaskKey] = (task.Generation, generation, TaskDispatchFields(task));
                    else
                    {
                        var now = NowMs();
                        var failed = current with
                        {
                            Status = TaskRunStatus.Failed,
                            RunCount = FlowConstants.IsFlowTask(current) ? checked(current.RunCount + 1) : current.RunCount,
                            Outcome = FlowOutcome.Attention,
                            StatusDetail = "Task dispatch denied by human.",
                            FinishedAtMs = now,
                            UpdatedAtMs = now
                        };
                        await _taskCoordinator.PersistAndEmitTaskSettlementCoreAsync(failed, failed.UpdatedAtMs, _actorShutdownCts.Token).ConfigureAwait(false);
                        await OnTaskSettledFlowHookCoreAsync(failed, _actorShutdownCts.Token).ConfigureAwait(false);
                    }
                }
                await _taskCoordinator.ScheduleEligibleTasksCoreAsync(_actorShutdownCts.Token).ConfigureAwait(false);
            }, _actorShutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            ExpireApproval(suspension.Pending);
            // A live approval can expire without a decision when the Track closes.
            // Release its scheduling fence on the mailbox so reopening can ask again.
            // A successful continuation already released it before scheduling, which
            // may have installed a new approval for a changed task contract.
            if (!pendingRemoved && !_actorShutdownCts.IsCancellationRequested)
            {
                try
                {
                    await EnqueueAsync(async _ => {
                        _pendingTaskDispatchApprovals.Remove(task.Id);
                        await _taskCoordinator.ScheduleEligibleTasksCoreAsync(_actorShutdownCts.Token).ConfigureAwait(false);
                    }, _actorShutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_actorShutdownCts.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (_actorShutdownCts.IsCancellationRequested) { }
            }
        }
    }

}
