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
        return new(task, session.BoundAttemptId, phase, phase == "pending"
            ? task.DependsOn.Any(key => RequireTaskCore(key).Status != TaskRunStatus.Done) ? "waiting_dependencies" : "waiting_lease" : null);
    }

    private SessionCapabilitiesDto ReadCapabilitiesCore(AgentSessionDto session)
    {
        _openSessionHandles.TryGetValue(session.Id, out var handle);
        var configuration = handle?.Agent.WorkerConfiguration;
        return new(configuration?.Models ?? [], configuration?.DefaultModel, configuration?.DefaultReasoningEffort,
            handle?.Agent.AvailablePermissionModes ?? [], handle?.Agent.SupportsNativeSteer == true,
            handle?.Agent.SupportsSessionResume ?? _agentRuntime.CanResumeSession(session.Provider),
            handle?.Agent.SupportsNativeCompact == true);
    }

    private IReadOnlyList<PendingInteractionDto> ReadInteractionsCore(AgentSessionDto session, InteropPrincipal principal)
    {
        var caller = principal is InteropPrincipal.Session identity ? RequireSessionCore(identity.SessionId) : null;
        var canRespond = principal is InteropPrincipal.Human || caller is not null && caller.Id != session.Id &&
            (caller.IsLead || session.ParentSessionId == caller.Id);
        return ReadPendingApprovals().Where(a => a.SessionId == session.Id).Select(a => new PendingInteractionDto(
                a.Id, "approval", a.SessionId, a.TurnId, a.ExecutionId, a.Title, a.Options, null, canRespond, a.CreatedAtMs, a.ToolName, a.Arguments))
            .Concat(_asks.Values.Where(a => a.Status == AskStatus.Open && a.SessionId == session.Id).Select(a =>
                new PendingInteractionDto(a.Id, "ask", session.Id, a.TurnId, null,
                    a.Questions[0].Question, null, a.Questions, canRespond &&
                    (principal is InteropPrincipal.Human || caller!.IsLead || a.TargetSessionId == caller.Id), a.CreatedAtMs)))
            .OrderBy(i => i.CreatedAtMs).ToArray();
    }
}
