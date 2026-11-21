using Perga.Kernel.Internal;
using Perga.PolicyVm;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private sealed record TurnPolicyAdmission(string Generation, string? Context, Exception? Failure);

    private async Task<TurnPolicyAdmission> PreparePolicyTurnAdmissionAsync(string sessionId, CancellationToken ct)
    {
        var preparation = await EnqueueAsync<(TurnPolicyAdmission Admission, PolicyApproval? Ask)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            var generation = _policyGeneration;
            if (_policyUnavailableReason is { } reason) return (new(generation, null, KernelException.PolicyDenied(reason)), null);
            if (_policy is null) return (new(generation, null, null), null);
            try
            {
                var evaluation = _policyVm.PrepareTurn(_policy, new(TrackId, session.Id, session.IsLead,
                    TryGetActiveTaskForSessionCore(session.Id)?.Task.Id, CurrentContextUsageRatioCore(session) ?? 0, _track.TemplateInputs), _track.TemplateState);
                await UpdateTemplateStateIfChangedCoreAsync(evaluation.NextState, ct).ConfigureAwait(false);
                var suspension = ApplyGuardDecisionCore(evaluation.Output.Decision, sessionId, null, ct);
                return (new(generation, evaluation.Output.Context, null), suspension);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return (new(generation, null, ex), null);
            }
        }, ct).ConfigureAwait(false);
        if (preparation.Ask is not { } ask) return preparation.Admission;
        try
        {
            await RequirePolicyApprovalAsync(ask, ct).ConfigureAwait(false);
            return preparation.Admission;
        }
        catch (KernelException ex)
        {
            return preparation.Admission with { Failure = ex };
        }
    }
}
