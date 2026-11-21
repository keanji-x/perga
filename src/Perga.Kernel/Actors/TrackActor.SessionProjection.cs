using Perga.AgentKit;
using Perga.Contracts;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private async Task<SessionTaskBindingDto?> ReadTaskBindingCoreAsync(AgentSessionDto session, CancellationToken ct)
    {
        if (session.BoundTaskId is not { } taskId) return null;
        var task = RequireTaskCore(taskId);
        var phase = task.Status.ToString().ToLowerInvariant();
        if (session.BoundAttemptId is { } attemptId)
        {
            if (_activeAttempts.TryGetValue(attemptId, out var active))
                phase = active.Paused ? "paused" : active.AttemptDto.Status.ToLowerInvariant();
            else
                phase = (await _store.ListAttemptsForTaskAsync(taskId, ct).ConfigureAwait(false))
                    .Single(a => a.AttemptId == attemptId).Status.ToLowerInvariant();
        }
        string? waitingReason = null;
        if (phase == "pending")
            waitingReason = task.DependsOn.Any(key => RequireTaskCore(key).Status != TaskRunStatus.Done)
                ? "waiting_dependencies" : "waiting_lease";
        return new(task, session.BoundAttemptId, phase, waitingReason);
    }

    private SessionCapabilitiesDto ReadCapabilitiesCore(AgentSessionDto session)
    {
        _runtimes.TryGetOpen(session.Id, out var handle);
        if (handle?.LaunchSpec.Provider != session.Provider) handle = null;
        var configuration = handle?.Agent.WorkerConfiguration;
        return new(configuration?.Models ?? [], configuration?.DefaultModel, configuration?.DefaultReasoningEffort,
            handle?.Agent.AvailablePermissionModes ?? [], handle?.Agent.SupportsNativeSteer == true,
            handle?.Agent.SupportsSessionResume ?? _agentRuntime.CanResumeSession(session.Provider),
            handle?.Agent.SupportsNativeCompact == true);
    }

    private IReadOnlyList<PendingInteractionDto> ReadInteractionsCore(AgentSessionDto session, InteropPrincipal principal)
    {
        var caller = principal is InteropPrincipal.Session identity ? RequireSessionCore(identity.SessionId) : null;
        var canRespond = SessionPolicy.CanAccess(principal, session, caller, write: true, activeTurn: true);
        bool CanRespondTo(string? delegateSessionId) => canRespond &&
            (principal is InteropPrincipal.Human || caller!.IsLead || delegateSessionId == caller.Id);
        return ReadPendingApprovals().Where(a => a.SessionId == session.Id).Select(a => new PendingInteractionDto(
                a.Id, "approval", a.SessionId, a.TurnId, a.ExecutionId, a.Title, a.Options, null, CanRespondTo(a.DelegateSessionId), a.CreatedAtMs, a.ToolName, a.Arguments))
            .Concat(_asks.Values.Where(a => a.Status == AskStatus.Open && a.SessionId == session.Id).Select(a =>
                new PendingInteractionDto(a.Id, "ask", session.Id, a.TurnId, null,
                    a.Questions[0].Question, null, a.Questions, CanRespondTo(a.TargetSessionId), a.CreatedAtMs)))
            .OrderBy(i => i.CreatedAtMs).ToArray();
    }
}
