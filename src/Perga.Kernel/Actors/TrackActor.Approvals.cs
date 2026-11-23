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

    private PolicySessionContext BuildPolicySessionContextCore(AgentSessionDto caller) =>
        new(caller.Id, caller.IsLead, caller.ContextUsage?.Ratio ?? 0.0,
            _taskCoordinator.TryGetActiveTaskForSessionCore(caller.Id)?.Task.Id);

    private readonly object _approvalAdmission = new();
    private bool _approvalAdmissionClosed;
    private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);

    internal enum ExecutionPhase { Starting, Running, Stopping, Ended }

    // Approval claims only change in-memory state. Approval delivery, expiry,
    // notifications and audit writes run outside the execution and admission locks.
    internal sealed class ExecutionControl(bool starting = false)
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
        public ExecutionControl? StopControl { get; init; }
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
        string source = "policy", string? toolName = null, JsonElement? arguments = null, ExecutionControl? stopControl = null)
    {
        var dto = new ApprovalDto($"apr_{Guid.NewGuid():N}", executionId, sessionId, turnId,
            title, options, NowMs(), source, toolName, ApprovalArguments(arguments), ApprovalDelegateCore(sessionId));
        var pending = new PendingApproval(dto, control, claimDelivery, expire) { StopControl = stopControl };
        // An already canceled token can run its callback inline during registration.
        pending.Cancellation = ct.UnsafeRegister(_ => ExpireApproval(pending), null);
        bool admitted;
        lock (_approvalAdmission)
        lock (stopControl?.Sync ?? control.Sync)
        lock (control.Sync)
        {
            admitted = !_approvalAdmissionClosed && !ct.IsCancellationRequested && control.Phase == ExecutionPhase.Running &&
                stopControl?.StopRequested != true;
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

    private List<PendingApproval>? CollectExpiredApprovalsLocked(ExecutionControl control, bool includeStopControl = false)
    {
        List<PendingApproval>? expired = null;
        foreach (var approval in _approvals.Values)
        {
            if (!ReferenceEquals(approval.Control, control) &&
                !(includeStopControl && ReferenceEquals(approval.StopControl, control))) continue;
            lock (approval.Control.Sync)
                if (TryDetachApprovalLocked(approval)) (expired ??= []).Add(approval);
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
        lock (pending.StopControl?.Sync ?? pending.Control.Sync)
        lock (pending.Control.Sync)
        {
            if (_approvalAdmissionClosed || pending.StopControl?.StopRequested == true || pending.Control.Phase != ExecutionPhase.Running ||
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
            var send = principal is InteropPrincipal.Session
                ? await EnqueueAsync(_ =>
                {
                    delivery = "aborted";
                    ct.ThrowIfCancellationRequested();
                    RequireInteropCore(principal, RequireSessionCore(pending.Dto.SessionId), write: true);
                    delivery = "expired";
                    return Task.FromResult(ClaimDelivery());
                }, _actorShutdownCts.Token).ConfigureAwait(false)
                : ClaimDelivery();

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

        Func<Task>? ClaimDelivery()
        {
            lock (_approvalAdmission)
            lock (pending.StopControl?.Sync ?? pending.Control.Sync)
            lock (pending.Control.Sync)
            {
                if (_approvalAdmissionClosed || pending.StopControl?.StopRequested == true || pending.Control.Phase != ExecutionPhase.Running ||
                    !TryDetachApprovalLocked(pending))
                    throw KernelException.Conflict("The execution ended before the decision could be delivered.");

                return pending.ClaimDelivery(choice);
            }
        }
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

    private PolicyApproval? ApplyGuardDecisionCore(GuardDecision decision, string? sessionId, ExecutionControl control, CancellationToken ct,
        string? toolName = null, JsonElement? arguments = null, ActiveTurnState? turn = null)
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
            ask.Title, choices, control, choice =>
            {
                waiter.TrySetResult(choice);
                return static () => Task.CompletedTask;
            }, () => waiter.TrySetCanceled(), ct, toolName: toolName, arguments: arguments, stopControl: turn?.Control);
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
        McpSessionPrincipal principal,
        string mcpServer,
        string toolName,
        JsonElement arguments,
        CancellationToken ct = default,
        bool write = true,
        bool isPlugin = false)
    {
        var identity = new InteropPrincipal.Session(principal.TrackId, principal.SessionId, principal.IsLead,
            principal.BoundTaskId, 0, principal.Token, principal.RuntimeBindingId);
        var sessionId = principal.SessionId;
        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            string authorizedPolicyGeneration = null!;
            var askSuspension = await EnqueueAsync<PolicyApproval?>(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                var caller = RequireSessionCore(sessionId);
                RequireMcpToolAccessCore(sessionId, mcpServer, toolName, isPlugin);
                // Capture before evaluation, including across an AskHuman suspension.
                authorizedPolicyGeneration = _policyGeneration;

                var kernelTool = !isPlugin && string.Equals(mcpServer, "perga", StringComparison.OrdinalIgnoreCase);
                var open = write || principal.RuntimeBindingId is not null ? RequireRuntimeBindingCore(identity, authorized: false) : null;

                if (kernelTool && KernelMcpCatalog.IsReadOnlyTool(toolName))
                    return null;

                var trackSet = kernelTool && toolName == KernelMcpToolNames.TrackSet;
                var dryRunOnly = trackSet && ValidateTemplateSetArguments(arguments);
                // The current Lead can detach a template whose policy blocks ordinary writes.
                if (trackSet && arguments.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True)
                    return null;
                if (kernelTool && toolName == KernelMcpToolNames.FlowCommit &&
                    arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("dryRun", out var dryRun))
                    dryRunOnly = dryRun.ValueKind == JsonValueKind.True;
                if (!trackSet)
                    RequirePolicyAvailableCore();
                if (_policy is null)
                    return null;

                var session = BuildPolicySessionContextCore(caller);
                var eval = _policyVm.Before(_policy, "tool." + toolName, TrackId, JsonHelpers.ToElement(new
                {
                    sessionId = caller.Id, isLead = caller.IsLead, boundTaskId = session.BoundTaskId,
                    mcpServer, toolName, arguments = arguments.ValueKind == JsonValueKind.Undefined ? JsonHelpers.EmptyObject : arguments
                }), _track.TemplateInputs, _track.Data, session);
                if (dryRunOnly && eval is GuardDecision.AskHuman)
                    throw KernelException.PolicyDenied("Template preview requires human approval; dryRun cannot create an approval request.");

                if (eval is GuardDecision.Allow) return null;
                if (eval is GuardDecision.Deny deny) throw KernelException.PolicyDenied(deny.Reason);
                open ??= RequireRuntimeBindingCore(identity, authorized: false);
                var approvalTurn = _scheduler.TryGetActive(sessionId, out var active) ? active : null;
                return ApplyGuardDecisionCore(eval, caller.Id, open.RuntimeControl, ct, toolName, arguments, approvalTurn);
            }, ct).ConfigureAwait(false);

            if (askSuspension is not null)
            {
                await RequirePolicyApprovalAsync(askSuspension, ct).ConfigureAwait(false);
                return await EnqueueAsync(_ =>
                {
                    RequireMcpToolAccessCore(sessionId, mcpServer, toolName, isPlugin);
                    RequireRuntimeBindingCore(identity with { AuthorizedPolicyGeneration = authorizedPolicyGeneration });
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

    private void RequireMcpToolAccessCore(string sessionId, string mcpServer, string toolName, bool isPlugin = false)
    {
        if (!isPlugin && string.Equals(mcpServer, "perga", StringComparison.OrdinalIgnoreCase))
        {
            if (KernelMcpCatalog.IsLeadOnly(toolName) && _track.LeadSessionId != sessionId)
            {
                if (toolName is KernelMcpToolNames.SystemUpgradeCheck or KernelMcpToolNames.SystemUpgradeApply or KernelMcpToolNames.SystemUpgradeRollback)
                    throw new KernelException(KernelErrorCode.SystemUpgradeLeadRequired, "System upgrades require the current Track Lead session.");
                throw KernelException.Forbidden("Only the current Track Lead can use this tool.");
            }
            if (KernelMcpCatalog.IsGovernanceOnly(toolName)) RequireGovernanceSessionCore(sessionId, toolName);
        }
        else if ((isPlugin || !string.Equals(mcpServer, "vcs", StringComparison.OrdinalIgnoreCase)) &&
            !_track.EnabledPlugins.Contains(mcpServer, StringComparer.Ordinal))
            throw KernelException.Forbidden($"MCP plugin '{mcpServer}' is not enabled on track '{TrackId}'.");
    }

    public Task EnsureMcpAuthorizationAsync(McpSessionPrincipal caller, string generation, string mcpServer,
        string toolName, CancellationToken ct = default, bool isPlugin = false) =>
        EnqueueAsync(_ =>
        {
            RequireMcpToolAccessCore(caller.SessionId, mcpServer, toolName, isPlugin);
            RequireRuntimeBindingCore(new(caller.TrackId, caller.SessionId, caller.IsLead, caller.BoundTaskId,
                0, caller.Token, caller.RuntimeBindingId) { AuthorizedPolicyGeneration = generation });
            return Task.CompletedTask;
        }, ct);

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
