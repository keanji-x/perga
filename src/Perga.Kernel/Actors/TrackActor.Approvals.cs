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
    private static readonly IReadOnlyList<ApprovalChoiceDto> DefaultBinaryChoices =
        [new("approve", "Approve", true), new("deny", "Deny", false)];

    private readonly object _approvalAdmission = new();
    private bool _approvalAdmissionClosed;
    private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);

    private enum ExecutionPhase { Starting, Running, Stopping, Ended }

    // Approval claims only change in-memory state. Approval delivery, expiry,
    // notifications and audit writes run outside the execution and admission locks.
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
        ApprovalDto dto, ExecutionControl control, Func<ApprovalChoiceDto, Func<Task>?> claimDelivery, Action expire)
    {
        public ApprovalDto Dto { get; } = dto;
        public ExecutionControl Control { get; } = control;
        public Func<ApprovalChoiceDto, Func<Task>?> ClaimDelivery { get; } = claimDelivery;
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
        string executionId, string sessionId, string? turnId, string title, IReadOnlyList<ApprovalChoiceDto> options,
        ExecutionControl control, Func<ApprovalChoiceDto, Func<Task>?> claimDelivery, Action expire, CancellationToken ct = default)
    {
        var dto = new ApprovalDto($"apr_{Guid.NewGuid():N}", executionId, sessionId, turnId,
            title, options.Count > 0 ? options : DefaultBinaryChoices, NowMs());
        var pending = new PendingApproval(dto, control, claimDelivery, expire);
        // An already canceled token can run its callback inline during registration.
        pending.Cancellation = ct.UnsafeRegister(_ => ExpireApproval(pending), null);
        bool admitted;
        lock (_approvalAdmission)
        lock (control.Sync)
        {
            admitted = !_approvalAdmissionClosed && !ct.IsCancellationRequested && control.Phase == ExecutionPhase.Running;
            if (admitted)
                _approvals[dto.Id] = pending;
        }

        if (!admitted)
        {
            pending.Cancellation.Unregister();
            return null;
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

        foreach (var approval in expired)
            approval.Expire();
        foreach (var sessionId in expired.Select(approval => approval.Dto.SessionId).Distinct(StringComparer.Ordinal))
            NotifyApprovalsChanged(sessionId);
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
        string executionId, string approvalId, string optionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(optionId))
            throw KernelException.InvalidArgument("optionId is required.");
        if (!_approvals.TryGetValue(approvalId, out var pending) || pending.Dto.ExecutionId != executionId)
            throw KernelException.Conflict("This approval is no longer pending.");

        var normalizedOptionId = optionId.Trim();
        var choice = pending.Dto.Options.FirstOrDefault(
            option => string.Equals(option.Id, normalizedOptionId, StringComparison.Ordinal))
            ?? throw KernelException.InvalidArgument($"Unknown approval option '{optionId}'.");

        lock (_approvalAdmission)
        lock (pending.Control.Sync)
        {
            if (_approvalAdmissionClosed || pending.Control.Phase != ExecutionPhase.Running ||
                !_approvals.ContainsKey(approvalId) || pending.RecordingDecision)
                throw KernelException.Conflict("This approval is no longer available for a decision.");
            pending.RecordingDecision = true;
        }

        try
        {
            await RecordApprovalAsync(KernelEventKinds.ApprovalDecided, pending.Dto,
                JsonHelpers.ToElement(new { optionId = choice.Id, label = choice.Label, isApproval = choice.IsApproval }))
                .ConfigureAwait(false);
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
            Func<Task>? send;
            lock (_approvalAdmission)
            lock (pending.Control.Sync)
            {
                if (_approvalAdmissionClosed || pending.Control.Phase != ExecutionPhase.Running ||
                    !TryDetachApprovalLocked(pending))
                    throw KernelException.Conflict("The execution ended before the decision could be delivered.");

                // Claim the provider request before Stop can cancel it. Writing the reply
                // can block and must run outside both locks.
                send = pending.ClaimDelivery(choice);
            }

            NotifyApprovalsChanged(pending.Dto.SessionId);
            if (send is null)
                throw KernelException.Conflict("The approval request ended before the decision could be delivered.");

            delivery = "unconfirmed";
            await send().ConfigureAwait(false);
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
        var hasProviderOptions = approval.Options is { Count: > 0 };
        var choices = hasProviderOptions
            ? approval.Options!.Select(option => new ApprovalChoiceDto(option.OptionId, option.Label, option.IsApproval)).ToArray()
            : DefaultBinaryChoices;
        if (turn.Settings?.ApprovalMode != "deny" &&
            RegisterApproval(turn.TurnDto.Id, turn.SessionId, turn.TurnDto.Id, $"Approve tool '{approval.ToolName}'?",
                choices, turn.Control, choice =>
                {
                    var send = turn.AgentSession.TryClaimApprovalResponse(
                        approval.RequestId,
                        choice.IsApproval,
                        hasProviderOptions ? choice.Id : null,
                        choice.IsApproval ? null : "Denied by user.",
                        turn.TurnCts.Token);
                    if (send is null)
                        return null;

                    return async () =>
                    {
                        try
                        {
                            await send().ConfigureAwait(false);
                        }
                        catch
                        {
                            ObserveTaskFault(RequestTurnStop(turn));
                            throw;
                        }
                    };
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

    private sealed record PolicyApproval(PendingApproval Pending, Task<ApprovalChoiceDto> Decision);

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

        var waiter = new TaskCompletionSource<ApprovalChoiceDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        ApprovalChoiceDto[] choices = [new("approve", ask.ApproveLabel, true), new("deny", ask.DenyLabel, false)];
        var pending = RegisterApproval($"op_{Guid.NewGuid():N}", sessionId, turn?.TurnDto.Id,
            ask.Title, choices, turn?.Control ?? new ExecutionControl(), choice =>
            {
                waiter.TrySetResult(choice);
                return static () => Task.CompletedTask;
            }, () => waiter.TrySetCanceled(), ct);
        return new PolicyApproval(pending ?? throw KernelException.Conflict("The turn is stopping."), waiter.Task);
    }

    private async Task RequirePolicyApprovalAsync(PolicyApproval approval, CancellationToken ct)
    {
        try
        {
            var choice = await approval.Decision.WaitAsync(ct).ConfigureAwait(false);
            if (!choice.IsApproval)
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
