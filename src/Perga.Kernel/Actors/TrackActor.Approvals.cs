using System.Collections.Concurrent;
using System.Text.Json;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;
using Perga.PolicyVm;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private readonly object _approvalAdmission = new();
    private bool _approvalAdmissionClosed;
    private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);

    private enum ExecutionPhase { Starting, Running, Stopping, Ended }

    // This lock only protects in-memory state transitions. Provider I/O, event
    // publishing, expiration callbacks, and audit writes always run outside it.
    private sealed class ExecutionControl(bool starting = false)
    {
        public object Sync { get; } = new();
        public ExecutionPhase Phase { get; set; } = starting ? ExecutionPhase.Starting : ExecutionPhase.Running;
        public bool StopRequested { get; set; }
        public bool StoppedBeforeDispatch { get; set; }
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ObservationEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? StopDispatch { get; set; }
        public Task? CancelCall { get; set; }

        public bool HasUnfinishedCancelLocked => CancelCall is { IsCompleted: false };

        public bool HasUnfinishedCancel
        {
            get { lock (Sync) return HasUnfinishedCancelLocked; }
        }

        public bool HasStopEvidenceLocked =>
            StoppedBeforeDispatch ||
            (StopDispatch is not { IsCompleted: false } &&
             !HasUnfinishedCancelLocked &&
             ObservationEnded.Task is { IsCompletedSuccessfully: true, Result: true });

        public bool HasStopEvidence
        {
            get { lock (Sync) return HasStopEvidenceLocked; }
        }
    }

    private sealed class PendingApproval(
        ApprovalDto dto, ExecutionControl control, Func<JsonElement, Task> deliver, Action expire)
    {
        public ApprovalDto Dto { get; } = dto;
        public ExecutionControl Control { get; } = control;
        public Func<JsonElement, Task> Deliver { get; } = deliver;
        public Action Expire { get; } = expire;
        public bool RecordingDecision { get; set; }
        public CancellationTokenRegistration Cancellation { get; set; }
    }

    internal int PendingApprovalCount => _approvals.Count;

    private ApprovalDto[] ReadPendingApprovals(string? turnId = null) =>
        _approvals.Values.Select(item => item.Dto)
            .Where(item => turnId is null || item.TurnId == turnId)
            .OrderBy(item => item.CreatedAtMs).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();

    private PendingApproval? RegisterApproval(
        string executionId, string sessionId, string? turnId, string title, IReadOnlyList<string> options,
        ExecutionControl control, Func<JsonElement, Task> deliver, Action expire, CancellationToken ct = default)
    {
        var dto = new ApprovalDto($"apr_{Guid.NewGuid():N}", executionId, sessionId, turnId,
            [new AskQuestionDto(title, options.Count > 0 ? options : ["Approve", "Deny"])], NowMs());
        var pending = new PendingApproval(dto, control, deliver, expire);
        lock (_approvalAdmission)
        lock (control.Sync)
        {
            if (_approvalAdmissionClosed || ct.IsCancellationRequested || control.Phase != ExecutionPhase.Running)
                return null;
            _approvals[dto.Id] = pending;
            pending.Cancellation = ct.UnsafeRegister(_ => ExpireApproval(pending), null);
        }
        NotifyApprovalsChanged(sessionId);
        return pending;
    }

    private bool TryDetachApprovalLocked(PendingApproval approval)
    {
        if (!_approvals.TryRemove(approval.Dto.Id, out _))
            return false;
        approval.Cancellation.Unregister();
        return true;
    }

    private List<PendingApproval>? CollectExpiredApprovalsLocked(ExecutionControl control)
    {
        List<PendingApproval>? expired = null;
        foreach (var approval in _approvals.Values)
        {
            if (ReferenceEquals(approval.Control, control) && TryDetachApprovalLocked(approval))
                (expired ??= []).Add(approval);
        }
        return expired;
    }

    private void FinishExpiredApprovals(List<PendingApproval>? expired)
    {
        if (expired is null or { Count: 0 })
            return;

        HashSet<string>? notifiedSessions = null;
        foreach (var approval in expired)
        {
            approval.Expire();
            if (expired.Count == 1 || (notifiedSessions ??= new(StringComparer.Ordinal)).Add(approval.Dto.SessionId))
                NotifyApprovalsChanged(approval.Dto.SessionId);
        }
    }

    private void ExpireApproval(PendingApproval approval)
    {
        bool detached;
        lock (approval.Control.Sync)
            detached = TryDetachApprovalLocked(approval);

        if (!detached)
            return;
        approval.Expire();
        NotifyApprovalsChanged(approval.Dto.SessionId);
    }

    private void SetApprovalAdmissionClosed(bool closed, bool stopPendingTurns = false)
    {
        HashSet<string>? turns = stopPendingTurns ? new(StringComparer.Ordinal) : null;
        List<PendingApproval>? expired = null;
        lock (_approvalAdmission)
        {
            _approvalAdmissionClosed = closed;
            if (closed)
            {
                foreach (var approval in _approvals.Values)
                {
                    if (approval.Dto.TurnId is { } turnId)
                        turns?.Add(turnId);
                    lock (approval.Control.Sync)
                    {
                        if (TryDetachApprovalLocked(approval))
                            (expired ??= []).Add(approval);
                    }
                }
            }
        }

        FinishExpiredApprovals(expired);

        if (turns is { Count: > 0 })
        {
            foreach (var active in _scheduler.ActiveTurns)
            {
                if (turns.Contains(active.TurnDto.Id))
                    ObserveTaskFault(RequestTurnStop(active));
            }
        }
    }

    private void NotifyApprovalsChanged(string sessionId) =>
        _eventHub.PublishTransient(_track.AreaId, TrackId, KernelEventKinds.ApprovalsChanged,
            JsonHelpers.ToElement(new { sessionId }), NowMs());

    internal async Task<ApprovalDto> DecideApprovalAsync(
        string executionId, string approvalId, JsonElement answer, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_approvals.TryGetValue(approvalId, out var pending) || pending.Dto.ExecutionId != executionId)
            throw KernelException.Conflict("This approval is no longer pending.");

        lock (_approvalAdmission)
        lock (pending.Control.Sync)
        {
            if (_approvalAdmissionClosed || pending.Control.Phase != ExecutionPhase.Running ||
                !_approvals.ContainsKey(approvalId) || pending.RecordingDecision)
                throw KernelException.Conflict("This approval is no longer available for a decision.");
            pending.RecordingDecision = true;
        }

        var decision = answer.Clone();
        try
        {
            await RecordApprovalAsync(KernelEventKinds.ApprovalDecided, pending.Dto,
                JsonHelpers.ToElement(new { answer = decision })).ConfigureAwait(false);
        }
        catch
        {
            lock (pending.Control.Sync)
                pending.RecordingDecision = false;
            throw;
        }

        string delivery = "expired";
        try
        {
            lock (_approvalAdmission)
            lock (pending.Control.Sync)
            {
                if (_approvalAdmissionClosed || pending.Control.Phase != ExecutionPhase.Running ||
                    !TryDetachApprovalLocked(pending))
                    throw KernelException.Conflict("The execution ended before the decision could be delivered.");

                delivery = "unconfirmed";
            }

            // Notification and provider delivery always run outside admission and control locks.
            NotifyApprovalsChanged(pending.Dto.SessionId);
            await pending.Deliver(decision).ConfigureAwait(false);
            delivery = "sent";
        }
        finally
        {
            _ = TrackBackgroundJob(() => RecordApprovalDeliveryAsync(pending.Dto, delivery));
        }
        return pending.Dto;
    }

    private async Task RecordApprovalDeliveryAsync(ApprovalDto approval, string delivery)
    {
        var detail = JsonHelpers.ToElement(new { delivery });
        try
        {
            await RecordApprovalAsync(KernelEventKinds.ApprovalDelivery, approval, detail).ConfigureAwait(false);
        }
        catch (Exception error) when (!_actorShutdownCts.IsCancellationRequested)
        {
            System.Diagnostics.Trace.TraceWarning("Approval {0} delivery audit could not be saved: {1}",
                approval.Id, error.Message);
        }
    }

    private Task RecordApprovalAsync(string kind, ApprovalDto approval, JsonElement detail) =>
        _store.AppendEventAsync(new(null, _track.AreaId, TrackId, kind,
            JsonHelpers.ToElement(new { approval, detail }), NowMs()), _actorShutdownCts.Token);

    private void AdmitProviderApproval(ActiveTurnState turn, UnifiedAgentEvent.ApprovalRequested approval)
    {
        var options = approval.Options?.Select(option => option.Label).ToArray();
        if (turn.Settings?.ApprovalMode != "deny" &&
            RegisterApproval(turn.TurnDto.Id, turn.SessionId, turn.TurnDto.Id, $"Approve tool '{approval.ToolName}'?",
                options ?? [], turn.Control, async answer =>
                {
                    var selected = approval.Options is { Count: > 0 } choices &&
                        AskAnswerEvaluator.TryGetSelectedOptionIndex(answer, options!, out var index)
                        ? choices[index]
                        : null;
                    var approved = selected?.IsApproval ??
                        AskAnswerEvaluator.IsApproved(answer, options is { Length: > 0 } ? options : ["Approve", "Deny"]);
                    try
                    {
                        await turn.AgentSession.RespondApprovalAsync(approval.RequestId, approved, selected?.OptionId,
                            approved ? null : "Denied by user.", turn.TurnCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        ObserveTaskFault(RequestTurnStop(turn));
                        throw;
                    }
                }, static () => { }) is not null)
            return;

        _ = TrackBackgroundJob(async () =>
        {
            try
            {
                await turn.AgentSession.RespondApprovalAsync(approval.RequestId, false,
                    "The execution does not accept this request.", turn.TurnCts.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException)
            {
                // The adapter may already have settled or closed this approval request.
            }
        });
    }

    private sealed record PolicyApproval(PendingApproval Pending, Task<JsonElement> Decision);

    private void RequireApprovalOrigin(string? sessionId, ActiveTurnState? turn, McpExecutionOrigin? origin)
    {
        if (origin is { } observed && observed.TurnId != turn?.TurnDto.Id ||
            turn is not null && (turn.Interrupted || ReadActiveTurnId(sessionId!) != turn.TurnDto.Id))
            throw KernelException.Conflict("The approval's originating execution has ended.");
    }

    private PolicyApproval? ApplyGuardDecisionCore(GuardDecision decision, string? sessionId, ActiveTurnState? turn, CancellationToken ct)
    {
        if (decision is GuardDecision.Allow)
            return null;
        if (decision is GuardDecision.Deny deny)
            throw KernelException.PolicyDenied(deny.Reason);
        var ask = (GuardDecision.AskHuman)decision;
        ct.ThrowIfCancellationRequested();
        if (_track.ClosedAtMs is not null || _deletingTrack || sessionId is null)
            throw KernelException.Forbidden("An approval requires an open Track and a session.");

        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = RegisterApproval($"op_{Guid.NewGuid():N}", sessionId, turn?.TurnDto.Id,
            ask.Title, ask.Options, turn?.Control ?? new ExecutionControl(), answer =>
            {
                waiter.TrySetResult(answer);
                return Task.CompletedTask;
            }, () => waiter.TrySetCanceled(), ct);
        return new PolicyApproval(pending ?? throw KernelException.Conflict("The turn is stopping."), waiter.Task);
    }

    private async Task RequirePolicyApprovalAsync(PolicyApproval approval, CancellationToken ct)
    {
        try
        {
            var answer = await approval.Decision.WaitAsync(ct).ConfigureAwait(false);
            if (!AskAnswerEvaluator.IsApproved(answer, approval.Pending.Dto.Questions[0].Options))
                throw KernelException.PolicyDenied($"Operation denied by human on approval '{approval.Pending.Dto.Id}'.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw KernelException.Conflict("The approved operation is no longer running.");
        }
        finally
        {
            ExpireApproval(approval.Pending);
        }
    }
}
