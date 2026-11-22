using System.Text;
using System.Threading.Channels;
using Perga.Kernel.Flow;
using Perga.Kernel.Mcp;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;
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
        public volatile bool StopTimedOut;
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
        public bool NotificationScheduled { get; set; }
        public CancellationTokenRegistration Cancellation { get; set; }
    }

    internal int PendingApprovalCount => _approvals.Count;

    private ApprovalDto[] ReadPendingApprovals(string? turnId = null) =>
        _approvals.Values.Select(item => item.Dto)
            .Where(item => turnId is null || item.TurnId == turnId)
            .OrderBy(item => item.CreatedAtMs).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();

    private PendingApproval? RegisterApproval(
        string executionId, string sessionId, string? turnId, string title, IReadOnlyList<ApprovalChoiceDto> options,
        ExecutionControl control, Func<ApprovalChoiceDto, Func<Task>?> claimDelivery, Action expire, CancellationToken ct = default,
        string source = "policy", string? toolName = null, JsonElement? arguments = null)
    {
        var dto = new ApprovalDto($"apr_{Guid.NewGuid():N}", executionId, sessionId, turnId,
            title, options, NowMs(), source, toolName, ApprovalArguments(arguments), ApprovalDelegateCore(sessionId));
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
        if (dto.DelegateSessionId is { } target)
            _ = TrackBackgroundJob(() => EnqueueAsync(_ => PokeSessionCoreAsync(target), CancellationToken.None));
        return pending;
    }

    private string? ApprovalDelegateCore(string sessionId)
    {
        var session = RequireSessionCore(sessionId);
        if (session.IsLead) return null;
        return session.ParentSessionId is { } parentId && _sessions.TryGetValue(parentId, out var parent) && parent.ArchivedAtMs is null
            ? parentId : _track.LeadSessionId;
    }

    private static JsonElement? ApprovalArguments(JsonElement? arguments)
    {
        if (arguments is not { } value) return null;
        var text = value.GetRawText();
        // Keep structured JSON when it fits; oversized payloads become a bounded JSON string.
        if (text.Length <= 4096) return value.Clone();
        var length = 4094;
        while (true)
        {
            var truncated = JsonHelpers.ToElement(text[..length]);
            var excess = truncated.GetRawText().Length - 4096;
            if (excess <= 0) return truncated;
            length -= Math.Max(1, excess / 6);
        }
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

    private async Task<ApprovalDto> DecideApprovalAsync(
        string executionId, string approvalId, string optionId, CancellationToken ct, InteropPrincipal? principal = null, string? sessionId = null)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(optionId))
            throw KernelException.InvalidArgument("optionId is required.");
        if (!_approvals.TryGetValue(approvalId, out var pending) || pending.Dto.ExecutionId != executionId)
            throw KernelException.Conflict("This approval is no longer pending.");

        if (principal is not null)
            await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                if (pending.Dto.SessionId != sessionId) throw KernelException.NotFound("Approval does not belong to this session.");
                RequireInteropCore(principal, RequireSessionCore(sessionId!), write: true);
            }, ct).ConfigureAwait(false);
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
            if (principal is InteropPrincipal.Session)
            {
                delivery = "aborted";
                await EnqueueAsync(_ =>
                {
                    RequireInteropCore(principal, write: true);
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
                delivery = "expired";
            }
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
            if (delivery == "aborted")
                lock (pending.Control.Sync) pending.RecordingDecision = false;
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
        if (RegisterApproval(turn.TurnDto.Id, turn.SessionId, turn.TurnDto.Id, $"Approve tool '{approval.ToolName}'?",
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
                }, static () => { }, source: "provider", toolName: approval.ToolName, arguments: approval.Args) is not null)
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

    private PolicyApproval? ApplyGuardDecisionCore(GuardDecision decision, string? sessionId, ActiveTurnState? turn, CancellationToken ct,
        string? toolName = null, JsonElement? arguments = null)
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
            }, () => waiter.TrySetCanceled(), ct, toolName: toolName, arguments: arguments);
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
    public async Task<string> GuardMcpToolCallAsync(
        string sessionId,
        string mcpServer,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default,
        McpExecutionOrigin? origin = null)
    {
        var approvalTurn = _scheduler.TryGetActive(sessionId, out var callingTurn) ? callingTurn : null;
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            string authorizedPolicyGeneration = null!;
            var askSuspension = await EnqueueAsync<PolicyApproval?>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var caller = RequireSessionCore(sessionId);
                if (string.Equals(mcpServer, "perga", StringComparison.OrdinalIgnoreCase) &&
                    KernelMcpCatalog.IsGovernanceOnly(toolName))
                    RequireGovernanceSessionCore(sessionId, toolName);

                if (!string.Equals(mcpServer, "perga", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(mcpServer, "vcs", StringComparison.OrdinalIgnoreCase) &&
                    !_track.EnabledPlugins.Contains(mcpServer, StringComparer.Ordinal))
                {
                    throw KernelException.Forbidden(
                        $"MCP plugin '{mcpServer}' is not enabled on track '{TrackId}'.");
                }

                // Capture before evaluation, including across an AskHuman suspension.
                authorizedPolicyGeneration = _policyGeneration;
                if (mcpServer == "perga" && KernelMcpCatalog.IsLeadOnly(toolName))
                {
                    if (caller.Id != _track.LeadSessionId)
                        throw KernelException.Forbidden("Only the current Track Lead can use this tool.");
                }

                var providerDiscovery = mcpServer == "perga" && toolName == KernelMcpToolNames.ProviderList;
                var flowRead = mcpServer == "perga" && toolName == KernelMcpToolNames.FlowRead;
                var previewOnly = providerDiscovery || flowRead;
                if (mcpServer == "perga" && toolName == KernelMcpToolNames.TrackSet)
                    previewOnly = ValidateTemplateSetArguments(arguments);
                if (mcpServer == "perga" && toolName == KernelMcpToolNames.FlowCommit &&
                    arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("dryRun", out var dryRun))
                    previewOnly = dryRun.ValueKind == JsonValueKind.True;
                if (!(mcpServer == "perga" && toolName == KernelMcpToolNames.TrackSet))
                    RequirePolicyAvailableCore();
                if (_policy is null)
                {
                    return null;
                }

                var eval = _policyVm.Before(_policy, "tool." + toolName, TrackId, JsonHelpers.ToElement(new {
                    sessionId = caller.Id, isLead = caller.IsLead, boundTaskId = _taskCoordinator.TryGetActiveTaskForSessionCore(caller.Id)?.Task.Id,
                    mcpServer, toolName, arguments = arguments.ValueKind == JsonValueKind.Undefined ? JsonHelpers.EmptyObject : arguments
                }), _track.TemplateInputs, _track.Data);
                if (providerDiscovery && eval is GuardDecision.AskHuman)
                    throw KernelException.PolicyDenied("Provider discovery is read-only and cannot request approval.");
                if (flowRead && eval is GuardDecision.AskHuman)
                    throw KernelException.PolicyDenied("Reading flow is read-only and cannot request approval.");
                if (eval is GuardDecision.AskHuman)
                    RequireApprovalOrigin(sessionId, approvalTurn, origin);
                if (previewOnly && eval is GuardDecision.AskHuman)
                    throw KernelException.PolicyDenied("Template preview requires human approval; dryRun cannot create an approval request.");

                return ApplyGuardDecisionCore(eval, caller.Id, approvalTurn, ct, toolName, arguments);
            }, ct).ConfigureAwait(false);

            if (askSuspension is not null)
            {
                await RequirePolicyApprovalAsync(askSuspension, ct).ConfigureAwait(false);
                return await EnqueueAsync(_ =>
                {
                    if (_policyGeneration != authorizedPolicyGeneration)
                        throw KernelException.PolicyDenied("Active policy or template data changed during approval.");
                    return Task.FromResult(authorizedPolicyGeneration);
                }, ct).ConfigureAwait(false);
            }

            return authorizedPolicyGeneration;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private void RequirePolicyAvailableCore()
    {
        if (_policyUnavailableReason is { } reason) throw KernelException.PolicyDenied(reason);
    }


    // The pending approval itself owns its notification. Detaching it on decision,
    // expiry or shutdown also removes all undispatched notification work.
    private bool TryScheduleApprovalNotificationCore(string sessionId)
    {
        var pending = _approvals.Values.Where(a => a.Dto.DelegateSessionId == sessionId && !a.NotificationScheduled).ToArray();
        if (pending.Length == 0) return false;
        foreach (var approval in pending) approval.NotificationScheduled = true;
        var reservation = Guid.NewGuid();
        _scheduler.Reserve(sessionId, reservation);
        _ = TrackBackgroundJob(async () =>
        {
            try { await _turnRunner.RunAsync(new ApprovalNotificationWorkItem(this, sessionId, pending, reservation), _actorShutdownCts.Token).ConfigureAwait(false); }
            finally
            {
                if (!_actorShutdownCts.IsCancellationRequested)
                    await EnqueueAsync(async _ =>
                    {
                        if (_scheduler.TryGetReservation(sessionId, out var held) && held == reservation)
                            _scheduler.ReleaseReservation(sessionId);
                        await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
            }
        });
        return true;
    }

    private sealed class ApprovalNotificationWorkItem(TrackActor actor, string sessionId,
        PendingApproval[] pending, Guid reservation) : ReservedTurnWorkItem(actor, sessionId, reservation)
    {
        public override string TriggerKind => "system";
        public override Task<string?> PreparePromptAsync(CancellationToken ct) => Actor.EnqueueAsync(_ =>
            Task.FromResult(FinalizePromptCore(string.Empty)), ct);
        public override string? FinalizePromptCore(string prompt)
        {
            var live = pending.Where(a => Actor._approvals.ContainsKey(a.Dto.Id)).Select(a => a.Dto).ToArray();
            return live.Length == 0 ? null :
                "Pending approvals delegated to you. Use session_respond to decide, or leave them for the human:\n" +
                JsonHelpers.ToElement(live).GetRawText();
        }
        public override SessionTurnDto? CreateUnstartedTurn() => null;
        public override Task OnProviderDeliveredAsync(SessionTurnDto turn, UnifiedAgentEvent evt, string? nativeThreadId) => Task.CompletedTask;
        public override async Task OnTurnSettledAsync(TurnSettlement settlement) => await settlement.SettleAsync().ConfigureAwait(false);
    }
}
