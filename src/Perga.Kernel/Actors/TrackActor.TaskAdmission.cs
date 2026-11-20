using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Tasks;
using Perga.PolicyVm;
using Perga.ReportDoc;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private readonly Dictionary<string, (int Generation, string PolicyGeneration, string DispatchFields)> _taskDispatchApprovals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingTaskDispatchApprovals = new(StringComparer.Ordinal);

    private async Task<(List<GuardDecision.AskHuman> Asks, HashSet<string> ApprovedKeys)> AdmitTaskDeclarationsCoreAsync(
        ReportCommitResult preview, CancellationToken ct)
    {
        var asks = new List<GuardDecision.AskHuman>();
        var approvedKeys = new HashSet<string>(StringComparer.Ordinal);
        var changedIds = preview.Changes.Where(change => change.Kind == ReportBlockKind.Task && change.CurrentRev is not null && (change.PreviousRev is null || change.CurrentRev != change.PreviousRev))
            .Select(change => change.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var block in preview.NextDocument.Blocks.Where(block => changedIds.Contains(block.Id)))
        {
            // Use exactly the same JSON/fence parser as report synchronization.
            var parsed = TaskBlockSynchronizer.ParseTaskBlock(TrackId, block, NowMs());
            if (!parsed.Ready) continue;
            var route = RouteTaskCore(parsed);
            if (route.Denial is { } denial) throw KernelException.PolicyDenied(denial);
            var eval = EvaluateTaskDispatchCore(parsed, route);
            await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);
            if (eval.Output is GuardDecision.Deny denied) throw KernelException.PolicyDenied(denied.Reason);
            if (eval.Output is GuardDecision.AskHuman ask) asks.Add(ask);
            await ValidateTaskProviderModelCoreAsync(route.Provider, route.Model, ct).ConfigureAwait(false);
            approvedKeys.Add(parsed.TaskKey);
        }
        return (asks, approvedKeys);
    }

    private PolicyTaskRoute RouteTaskCore(TaskDto task)
    {
        RequirePolicyAvailableCore();
        var model = task.Context is { ValueKind: JsonValueKind.Object } context &&
            context.TryGetProperty("initialModel", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return _policy is null ? new(task.Provider, model) : _policyVm.RouteTaskDispatch(_policy,
            task.Role, FormatAccessModeForPolicy(task.Access), task.Provider, model, _track.TemplateInputs);
    }

    private PolicyEvalResult<GuardDecision> EvaluateTaskDispatchCore(TaskDto task, PolicyTaskRoute route) =>
        _policy is null ? new(_track.TemplateState, new GuardDecision.Allow()) :
        _policyVm.Before(_policy, "task.dispatch", TrackId, JsonHelpers.ToElement(new {
            taskKey = task.TaskKey, role = task.Role, access = FormatAccessModeForPolicy(task.Access),
            provider = route.Provider, model = route.Model, gate = task.Gate, acceptance = task.Acceptance
        }), _track.TemplateInputs, _track.TemplateState);

    private async Task<bool> EnsureTaskDispatchApprovedCoreAsync(TaskDto task, PolicyTaskRoute route, CancellationToken ct)
    {
        if (route.Denial is { } denial) throw KernelException.PolicyDenied(denial);
        if (_policy is null || _taskDispatchApprovals.TryGetValue(task.TaskKey, out var approval) &&
            approval == (task.Generation, _policyGeneration, TaskDispatchFields(task))) return true;
        if (_pendingTaskDispatchApprovals.Contains(task.Id)) return false;
        var evaluation = EvaluateTaskDispatchCore(task, route);
        await UpdateTemplateStateIfChangedCoreAsync(evaluation.NextState, ct).ConfigureAwait(false);
        if (evaluation.Output is GuardDecision.Deny denied)
            throw KernelException.PolicyDenied(denied.Reason);
        if (_policy.Manifest.RequireGateForReadWrite && task.Access == AccessMode.ReadWrite && (task.Gate is null || task.Gate.Steps.Count == 0))
            throw KernelException.PolicyDenied("Template policy requires a gate for this task.");
        if (evaluation.Output is GuardDecision.AskHuman ask)
        {
            var suspension = ApplyGuardDecisionCore(ask, _track.LeadSessionId, null, _actorShutdownCts.Token)!;
            _pendingTaskDispatchApprovals.Add(task.Id);
            var generation = _policyGeneration;
            _ = TrackBackgroundJob(() => ResumeTaskDispatchApprovalAsync(task, generation, suspension));
            return false;
        }
        _taskDispatchApprovals[task.TaskKey] = (task.Generation, _policyGeneration, TaskDispatchFields(task));
        return true;
    }

    private async Task ValidateTaskProviderModelCoreAsync(string? provider, string? model, CancellationToken ct)
    {
        if (provider is null && model is null) return;
        IReadOnlyList<Perga.AgentKit.ProviderStatus> providers;
        try
        {
            providers = await _agentRuntime.ProbeProvidersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new KernelException(KernelErrorCode.Conflict, "Task provider probe is temporarily unavailable.", innerException: ex);
        }
        var selected = providers.FirstOrDefault(p => p.Provider == (provider ?? GetDefaultProviderCore()));
        if (selected is { Available: false })
            throw KernelException.Conflict("The requested task provider is temporarily unavailable.");
        if (selected is null || model is not null && !selected.Models.Contains(model, StringComparer.Ordinal))
            throw KernelException.InvalidArgument("The requested task provider and model combination is unavailable.");
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
                var current = _tasks.FirstOrDefault(t => t.Id == task.Id);
                if (current is null || current.Status != TaskRunStatus.Pending) return;
                if (current.Generation == task.Generation && _policyGeneration == generation && TaskDispatchFields(current) == TaskDispatchFields(task))
                {
                    if (choice.IsApproval)
                        _taskDispatchApprovals[task.TaskKey] = (task.Generation, generation, TaskDispatchFields(task));
                    else
                    {
                        var failed = current with { Status = TaskRunStatus.Failed, StatusDetail = "Task dispatch denied by human.", UpdatedAtMs = NowMs() };
                        await PersistAndEmitTaskUpdatedCoreAsync(failed, null, null, failed.UpdatedAtMs, _actorShutdownCts.Token).ConfigureAwait(false);
                        ReplaceTaskInMemoryCore(failed);
                    }
                }
                await ScheduleEligibleTasksCoreAsync(_actorShutdownCts.Token).ConfigureAwait(false);
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
                        await ScheduleEligibleTasksCoreAsync(_actorShutdownCts.Token).ConfigureAwait(false);
                    }, _actorShutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_actorShutdownCts.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (_actorShutdownCts.IsCancellationRequested) { }
            }
        }
    }

}
