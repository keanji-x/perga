using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private AgentSessionDto? RequireInteropCore(InteropPrincipal principal, AgentSessionDto? target = null,
        bool write = false, bool destructive = false)
    {
        if (principal is InteropPrincipal.Human) return null;
        var identity = (InteropPrincipal.Session)principal;
        if (identity.TrackId != TrackId) throw KernelException.Forbidden("The caller belongs to another Track.");
        var caller = RequireSessionCore(identity.SessionId);
        if (write) RequireRuntimeBindingCore(identity);
        if (target is not null)
            SessionPolicy.RequireAccess(principal, target, caller, write, _scheduler.HasActive(target.Id), destructive);
        return caller;
    }

    private OpenSessionHandle RequireRuntimeBindingCore(InteropPrincipal.Session identity, bool authorized = true)
    {
        if (identity.TrackId != TrackId) throw KernelException.Forbidden("The caller belongs to another Track.");
        var session = RequireSessionCore(identity.SessionId);
        if (_disposed || _track.ClosedAtMs is not null || _deletingTrack || _closingTrack ||
            session.ArchivedAtMs is not null || session.Status is "unknown" or "stopping" ||
            identity.RuntimeBindingId is not { } bindingId ||
            !_runtimes.TryGetOpen(session.Id, out var open) || open.RuntimeBindingId != bindingId)
            throw KernelException.Conflict("The originating session runtime is no longer authorized.");
        RequireRuntimeWriteFence(identity, open, authorized);
        return open;
    }

    // Also used at the recipient's mail commit point. Read only lifecycle state
    // that is safe to observe without entering the sender's mailbox.
    private void RequireRuntimeWriteFence(InteropPrincipal.Session identity, OpenSessionHandle open, bool authorized = true,
        ActiveTurnState? originatingTurn = null)
    {
        var runtime = _runtimes.Get(identity.SessionId);
        if (_disposed || _track.ClosedAtMs is not null || _closingTrack || _deletingTrack ||
            runtime.IsDisposing || runtime.IsShutdownUnconfirmed || originatingTurn?.Interrupted == true ||
            _scheduler.TryGetActive(identity.SessionId, out var active) && active.Interrupted ||
            _mcpBridge.ResolveToken(identity.Token) is not { } credential ||
            credential.TrackId != TrackId || credential.SessionId != identity.SessionId ||
            credential.RuntimeBindingId != open.RuntimeBindingId)
            throw KernelException.Conflict("The originating session runtime is no longer authorized.");
        lock (open.RuntimeControl.Sync)
            if (open.RuntimeControl.Phase != ExecutionPhase.Running)
                throw KernelException.Conflict("The originating session runtime is no longer authorized.");
        if (authorized && identity.AuthorizedPolicyGeneration != _policyGeneration)
            throw KernelException.PolicyDenied("Active policy changed after authorization.");
        runtime.TouchUsed();
    }

    private bool HasRuntimeCredential(string sessionId, OpenSessionHandle open) =>
        _mcpBridge.GetTokenForSession(sessionId) is { } token &&
        _mcpBridge.ResolveToken(token) is { } credential && credential.RuntimeBindingId == open.RuntimeBindingId;

    private void RequireMcpAuthorizationCore(InteropPrincipal.Session? authorization)
    {
        if (authorization is not null) RequireRuntimeBindingCore(authorization);
    }

    // The store runs this after its writer wait, before the transaction changes any state.
    private Action? CaptureMcpWriteFenceCore(InteropPrincipal.Session? authorization)
    {
        if (authorization is null) return null;
        var open = RequireRuntimeBindingCore(authorization);
        return () => RequireRuntimeWriteFence(authorization, open);
    }

    private void RequireSessionMutationCore(AgentSessionDto session, InteropPrincipal principal, int? expectedEpoch = null,
        bool destructive = false)
    {
        RequireInteropCore(principal, session, write: true, destructive: destructive);
        if (expectedEpoch is { } epoch && session.Epoch != epoch)
            throw KernelException.Conflict("The session epoch changed.");
        RequireSessionWritableCore(session);
    }

    private void RequireSessionWritableCore(AgentSessionDto session)
    {
        if (_track.ClosedAtMs is not null || session.ArchivedAtMs is not null)
            throw KernelException.PreconditionFailed("Reopen the conversation before modifying it.");
        if (_taskCoordinator.TryGetActiveOwningTaskCore(session.Id) is { } task)
        {
            if (!_taskCoordinator.TryGetActiveRun(task.Id, out var active) || active.Settling || task.Status != TaskRunStatus.Running)
                throw KernelException.Conflict("The task is no longer running. Refresh the task before sending a command.");
            _taskCoordinator.RequireDispatchWorkspace(session);
        }
    }

    private string? ReadSessionReadonlyReasonCore(AgentSessionDto session)
    {
        if (session.ArchivedAtMs is not null) return "archived";
        if (_track.ClosedAtMs is not null) return "track_closed";
        var task = _taskCoordinator.TryGetActiveOwningTaskCore(session.Id);
        if (task?.Status == TaskRunStatus.Pending) return "waiting_task";
        if (task?.Status == TaskRunStatus.Verifying) return "task_verifying";
        return null;
    }

    public Task<IReadOnlyList<SessionSummaryDto>> ListSessionsAsync(SessionListFilter filter, InteropPrincipal caller, CancellationToken ct) =>
        EnqueueAsync<IReadOnlyList<SessionSummaryDto>>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireInteropCore(caller);
            var summaries = new List<SessionSummaryDto>();
            var pendingApprovals = ReadPendingApprovals();
            foreach (var session in OrderedSessionsCore().Where(s => (filter.IncludeArchived || s.ArchivedAtMs is null) &&
                         (filter.ParentSessionId is null || s.ParentSessionId == filter.ParentSessionId)))
            {
                var last = (await _store.ListSessionTurnsAsync(session.Id, ct).ConfigureAwait(false)).MaxBy(t => t.TurnIndex);
                var owningTask = _taskCoordinator.TryGetActiveOwningTaskCore(session.Id);
                var activeTask = owningTask is not null && _taskCoordinator.TryGetActiveRun(owningTask.Id, out var found) ? found : null;
                var state = session.Status == "queued" ? "running" : session.Status;
                var readonlyReason = ReadSessionReadonlyReasonCore(session);
                var pendingInteractionCount = pendingApprovals.Count(a => a.SessionId == session.Id) +
                    _interactions.OpenAsks().Count(a => a.Status == AskStatus.Open && a.SessionId == session.Id);
                summaries.Add(new(session, state, readonlyReason, activeTask?.Paused == true, last, pendingInteractionCount, owningTask?.Id));
            }
            return summaries;
        }, ct);

    public async Task<AgentSessionDto> UpdateSessionAsync(string sessionId, UpdateSessionRequest request,
        InteropPrincipal caller, CancellationToken ct)
    {
        var configChanged = request.Provider is not null || request.Model.IsSet || request.ReasoningEffort.IsSet || request.PermissionMode.IsSet;
        // Human permission-only edits can use the provider's static catalog or the target's live modes.
        var needsProbe = request.Provider is not null || request.Model.IsSet || request.ReasoningEffort.IsSet ||
            request.PermissionMode.IsSet && caller is InteropPrincipal.Session;
        if (!needsProbe)
            return await ApplySessionUpdateAsync(sessionId, request, caller, configChanged, [], ct).ConfigureAwait(false);

        Interlocked.Increment(ref _inFlightExternalOps);
        try
        {
            await EnqueueAsync(async _ =>
            {
                await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
                RequireSessionUpdateCore(sessionId, request, caller, configChanged);
            }, ct).ConfigureAwait(false);
            var providers = await ProbeSessionProvidersAsync(ct).ConfigureAwait(false);
            return await ApplySessionUpdateAsync(sessionId, request, caller, configChanged, providers, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightExternalOps);
        }
    }

    private AgentSessionDto RequireSessionUpdateCore(string sessionId, UpdateSessionRequest request,
        InteropPrincipal caller, bool configChanged)
    {
        var session = RequireSessionCore(sessionId);
        RequireInteropCore(caller, session, write: true, destructive: request.Archived == true || request.Provider is not null);
        if (_track.ClosedAtMs is not null) throw KernelException.PreconditionFailed("The Track is closed.");
        if (_taskCoordinator.TryGetActiveOwningTaskCore(sessionId) is { Status: not TaskRunStatus.Pending } task)
        {
            if (!_taskCoordinator.TryGetActiveRun(task.Id, out var active) || active.Settling || task.Status != TaskRunStatus.Running)
                throw KernelException.Conflict("The task is no longer running. Refresh the task before updating it.");
            _taskCoordinator.RequireDispatchWorkspace(session);
        }
        if (configChanged && request.ExpectedConfigVersion is { } version && version != session.ConfigVersion)
            throw KernelException.Conflict("Session configuration changed. Refresh before updating it.");
        if (configChanged && caller is InteropPrincipal.Session && request.ExpectedConfigVersion is null)
            throw KernelException.InvalidArgument("expectedConfigVersion is required for configuration updates.");
        if (request.Provider is { } provider && SessionLaunchValidator.NormalizeProvider(provider) !=
            SessionLaunchValidator.NormalizeProvider(session.Provider) && _scheduler.IsOccupied(session))
            throw KernelException.PreconditionFailed("Provider changes require an idle session.");
        return session;
    }

    private async Task<AgentSessionDto> ApplySessionUpdateAsync(string sessionId, UpdateSessionRequest request,
        InteropPrincipal caller, bool configChanged, IReadOnlyList<ProviderStatus> providers, CancellationToken ct)
    {
        var operation = await EnqueueAsync(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionUpdateCore(sessionId, request, caller, configChanged);
            var provider = request.Provider is { } requestedProvider
                ? SessionLaunchValidator.NormalizeProvider(requestedProvider)
                : session.Provider;
            var providerChanged = SessionLaunchValidator.NormalizeProvider(provider) != SessionLaunchValidator.NormalizeProvider(session.Provider);
            var requestedModel = string.IsNullOrWhiteSpace(request.Model.Value) ? null : request.Model.Value.Trim();
            var requestedEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort.Value) ? null : request.ReasoningEffort.Value.Trim();
            var model = request.Model.IsSet || providerChanged ? requestedModel : session.Model;
            var effort = request.ReasoningEffort.IsSet || providerChanged ? requestedEffort : session.ReasoningEffort;
            var permissionMode = session.PermissionMode;
            if (configChanged)
            {
                var status = SessionLaunchValidator.ValidateProvider(providers, SessionLaunchValidator.NormalizeProvider(provider),
                    explicitlySelected: request.Provider is not null, requireAvailable: caller is InteropPrincipal.Session);
                SessionLaunchValidator.ValidateModelAndEffort(provider, status, requestedModel, model, requestedEffort);
                if (request.PermissionMode.IsSet || providerChanged)
                {
                    var nativeMode = request.PermissionMode.Value;
                    permissionMode = RuntimePermissionModes.Resolve(provider, nativeMode, nativeMode is null ? null :
                        ResolvePermissionCatalogCore(SessionLaunchValidator.NormalizeProvider(provider), status, sessionId,
                            useTrackCatalog: caller is InteropPrincipal.Session));
                }
            }
            var modelChanged = providerChanged || model != session.Model || effort != session.ReasoningEffort;
            var archivedAtMs = session.ArchivedAtMs;
            if (request.Archived == true) archivedAtMs ??= NowMs();
            else if (request.Archived == false) archivedAtMs = null;
            var updated = session with
            {
                Title = request.Title?.Trim() ?? session.Title,
                ArchivedAtMs = archivedAtMs,
                Provider = provider,
                Model = model,
                ReasoningEffort = effort,
                PermissionMode = permissionMode,
                ConfigVersion = configChanged ? checked(session.ConfigVersion + 1) : session.ConfigVersion,
                NativeThreadId = providerChanged ? null : session.NativeThreadId,
                ContextUsage = modelChanged ? null : session.ContextUsage,
                UpdatedAtMs = NowMs()
            };
            ct.ThrowIfCancellationRequested();
            await PersistAndEmitSessionUpdatedCoreAsync(updated, updated.UpdatedAtMs,
                request.Archived == true ? _actorShutdownCts.Token : ct).ConfigureAwait(false);
            if (modelChanged) InvalidateContextBindingCore(sessionId);
            if (providerChanged || request.Archived == true)
            {
                RevokeSessionRuntime(sessionId);
                if (_scheduler.TryGetActive(sessionId, out var active))
                    active.CloseRuntimeOnSettle = true;
                else if (_scheduler.IsIdleForRuntimeClose(sessionId) && _runtimes.TryGetOpen(sessionId, out var open))
                    return actor.TrackSessionUpdateCloseAsync(updated, open.Generation);
            }
            return Task.FromResult(updated);
        }, ct).ConfigureAwait(false);
        return await operation.WaitAsync(ct).ConfigureAwait(false);
    }

    private Task<AgentSessionDto> TrackSessionUpdateCloseAsync(AgentSessionDto updated, long generation)
    {
        var completion = new TaskCompletionSource<AgentSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = TrackBackgroundJob(async () =>
        {
            try
            {
                await CloseSessionRuntimeAsync(updated.Id, generation, propagateFailure: true).ConfigureAwait(false);
                completion.TrySetResult(updated);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });
        return completion.Task;
    }

    private void ScheduleSessionRuntimeCloseCore(string sessionId, bool revokeToken = false)
    {
        if (revokeToken) RevokeSessionRuntime(sessionId);
        if (_scheduler.TryGetActive(sessionId, out var active))
            active.CloseRuntimeOnSettle = true;
        else if (_runtimes.TryGetOpen(sessionId, out var open))
            _ = TrackBackgroundJob(() => CloseSessionRuntimeAsync(sessionId, open.Generation));
    }

    private async Task<bool> CloseSessionRuntimeAsync(string sessionId, long expectedGeneration,
        bool propagateFailure = false, long? idleCutoffMs = null, string? allowDispatchInputId = null)
    {
        var runtime = _runtimes.Get(sessionId);
        try
        {
            return await runtime.CloseAsync(_options.SessionStartupTimeout, _actorShutdownCts.Token,
                shouldClose: () => _scheduler.IsIdleForRuntimeClose(sessionId, allowDispatchInputId) &&
                    (idleCutoffMs is null || !runtime.IsShutdownUnconfirmed &&
                        runtime.Snapshot is { } handle && RuntimeIsIdleCore(handle, idleCutoffMs.Value)),
                expectedGeneration: expectedGeneration).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!_actorShutdownCts.IsCancellationRequested && runtime.IsShutdownUnconfirmed)
                await EnqueueAsync(_ => MarkShutdownUnconfirmedIfNeededAsync(sessionId),
                    touchActivity: false).ConfigureAwait(false);
            if (propagateFailure) throw;
            return false;
        }
    }

    internal async Task<int> EvictIdleSessionRuntimesAsync(CancellationToken ct = default)
    {
        if (_disposed || _runtimes.SnapshotOpen().Count == 0)
            return 0;

        long? idleCutoffMs = _options.SessionIdleTimeout > TimeSpan.Zero
            ? NowMs() - (long)_options.SessionIdleTimeout.TotalMilliseconds
            : null;
        List<(string SessionId, long Generation, long? IdleCutoffMs)> candidates;
        try
        {
            candidates = await EnqueueAsync(_ =>
            {
                var idle = new List<(string, long, long?)>();
                if (!_hydrated) return Task.FromResult(idle);
                foreach (var (sessionId, handle) in _runtimes.SnapshotOpen())
                {
                    if (_runtimes.Get(sessionId).HasActiveWork ||
                        !_sessions.TryGetValue(sessionId, out var session) ||
                        !_scheduler.IsIdleForRuntimeClose(sessionId)) continue;
                    var retired = session.ArchivedAtMs is not null || _track.ClosedAtMs is not null ||
                        !HasRuntimeCredential(sessionId, handle);
                    if (retired)
                    {
                        idle.Add((sessionId, handle.Generation, null));
                        continue;
                    }
                    if (idleCutoffMs is null || !RuntimeIsIdleCore(handle, idleCutoffMs.Value) || _scheduler.IsOccupied(session)) continue;
                    if (handle.Agent.SupportsSessionResume && !string.IsNullOrWhiteSpace(session.NativeThreadId))
                        idle.Add((sessionId, handle.Generation, idleCutoffMs));
                }
                return Task.FromResult(idle);
            }, ct, touchActivity: false).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return 0;
        }

        if (candidates.Count == 0) return 0;
        var results = await Task.WhenAll(candidates.Select(candidate =>
            CloseSessionRuntimeAsync(candidate.SessionId, candidate.Generation, idleCutoffMs: candidate.IdleCutoffMs)))
            .ConfigureAwait(false);
        return results.Count(closed => closed);
    }

    private bool RuntimeIsIdleCore(OpenSessionHandle handle, long cutoffMs) =>
        handle.LastUsedAtMs <= cutoffMs &&
        !handle.RuntimeActivity.Tasks.Values.Any(task => task.Status is "running" or "pending") &&
        !_approvals.Values.Any(approval => ReferenceEquals(approval.Control, handle.RuntimeControl)) &&
        !_interactions.HasRuntimeWaiter(handle.RuntimeControl);

    private async Task NotifyParentCoreAsync(AgentSessionDto child, string policy, string key, string prompt)
    {
        if (child.NotifyParent != policy || child.ParentSessionId is not { } parentId ||
            !_sessions.TryGetValue(parentId, out var parent) || parent.ArchivedAtMs is not null) return;
        await _store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(TrackId, TrackWakeKinds.TaskSettled,
            key, prompt) with { TargetSessionId = parentId }, CancellationToken.None).ConfigureAwait(false);
        await PokeSessionCoreAsync(parentId).ConfigureAwait(false);
    }

    private static bool IsWorkingSessionStatus(string status) =>
        status is "queued" or "running" or "stopping" or "compacting";

    private bool IsSessionBlockingTrack(AgentSessionDto session) =>
        _taskCoordinator.TryGetActiveOwningTaskCore(session.Id) is not { } task ||
        task.Status is TaskRunStatus.Running or TaskRunStatus.Verifying;

    internal Task<TrackOverlayDto> MergeOverlayAsync(StoredTrackOverlay stored, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var open = _track.ClosedAtMs is null;
            var sessions = _sessions.Values.Where(s => s.ArchivedAtMs is null).ToArray();
            var openAsks = open ? _interactions.OpenAsks() : [];
            var blockingSessions = open ? sessions.Where(s =>
                string.Equals(s.Status, "unknown", StringComparison.OrdinalIgnoreCase) && IsSessionBlockingTrack(s)).ToArray() : [];
            var workspaceFailed = open && _workspacePreparation?.Phase is
                WorkspacePreparationPhase.Failed or WorkspacePreparationPhase.Unknown;
            var ask = openAsks.OrderByDescending(a => a.CreatedAtMs)
                .ThenByDescending(a => a.Id, StringComparer.Ordinal).FirstOrDefault();
            var blockedError = blockingSessions
                .OrderByDescending(s => s.UpdatedAtMs).ThenByDescending(s => s.Id, StringComparer.Ordinal)
                .Select(s => !string.IsNullOrWhiteSpace(s.UnknownLastError) ? s.UnknownLastError
                    : stored.Overlay.HasBlockedSession && stored.BlockedErrorSource is { } source &&
                      source.SessionId == s.Id && source.TurnId == s.UnknownTurnId
                        ? stored.Previews.BlockedSessionError : null)
                .FirstOrDefault(error => !string.IsNullOrWhiteSpace(error));
            var (previewText, previewKind) = TrackOverlayPreview.Select(stored.Previews with
            {
                Ask = ask?.Questions.FirstOrDefault()?.Question,
                BlockedSessionError = blockedError,
                WorkspaceError = workspaceFailed ? _workspacePreparation?.FailureDetail : null
            }, open);
            return stored.Overlay with
            {
                IsWorking = open && (
                    IsSessionOrTaskRunning ||
                    _workspacePreparation?.Phase is WorkspacePreparationPhase.Pending or
                        WorkspacePreparationPhase.Provisioning or WorkspacePreparationPhase.Mounting or
                        WorkspacePreparationPhase.Initializing or WorkspacePreparationPhase.Provisioned ||
                    sessions.Any(s => IsWorkingSessionStatus(s.Status)) ||
                    _taskCoordinator.Snapshot().Any(t => t.Status is TaskRunStatus.Running or TaskRunStatus.Verifying)),
                OpenAskCount = openAsks.Length,
                PendingApprovalCount = open ? PendingApprovalCount : 0,
                HasBlockedSession = blockingSessions.Length > 0,
                WorkspaceFailed = workspaceFailed,
                UpdatedAtMs = Math.Max(stored.Overlay.UpdatedAtMs, LastActivityAtMs),
                NotifiableSessions = sessions
                    .Where(s => !_taskCoordinator.IsTaskOwnedSessionCore(s.Id) && s.LastNotifiableTurnAtMs is not null)
                    .OrderBy(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new NotifiableSessionWatermarkDto(s.Id, s.LastNotifiableTurnAtMs!.Value))
                    .ToArray(),
                PreviewText = previewText,
                PreviewKind = previewKind
            };
        }, ct, touchActivity: false);

    private SessionTaskBindingDto? ReadTaskBindingCore(AgentSessionDto session)
    {
        if (_taskCoordinator.TryGetActiveOwningTaskCore(session.Id) is not { } task) return null;
        var phase = task.Status.ToString().ToLowerInvariant();
        if (_taskCoordinator.TryGetActiveRun(task.Id, out var active) && active.SessionId == session.Id)
        {
            if (session.Status == "stopping") phase = "interrupting";
            else if (session.UnknownReason == "shutdown_unconfirmed") phase = "interrupt_unknown";
            else if (active.Paused) phase = "paused";
        }
        string? waitingReason = null;
        if (phase == "pending")
            waitingReason = task.DependsOn.Any(key => _taskCoordinator.RequireTaskCore(key).Status != TaskRunStatus.Done)
                ? "waiting_dependencies" : "waiting_slot";
        return new(task, phase, waitingReason);
    }

    private SessionCapabilitiesDto ReadCapabilitiesCore(AgentSessionDto session)
    {
        _runtimes.TryGetOpen(session.Id, out var handle);
        if (!string.Equals(handle?.LaunchSpec.Provider, session.Provider, StringComparison.OrdinalIgnoreCase)) handle = null;
        var configuration = handle?.Agent.WorkerConfiguration;
        var supportsResume = handle?.Agent.SupportsSessionResume ?? EffectiveAgentRuntime.CanResumeSession(session.Provider);
        // Compact can reopen an existing session before choosing native compaction or an epoch handover.
        var canResumeForCompact = supportsResume && !session.ReconnectInvalidated &&
            !string.IsNullOrWhiteSpace(session.NativeThreadId);
        return new(configuration?.Models ?? [], configuration?.DefaultModel, configuration?.DefaultReasoningEffort,
            handle?.Agent.AvailablePermissionModes ?? [], handle?.Agent.SupportsNativeSteer == true,
            supportsResume, handle?.Agent.SupportsNativeCompact == true || canResumeForCompact);
    }

    private IReadOnlyList<PendingInteractionDto> ReadInteractionsCore(AgentSessionDto session, InteropPrincipal principal)
    {
        var caller = principal is InteropPrincipal.Session identity ? RequireSessionCore(identity.SessionId) : null;
        var canRespond = SessionPolicy.CanAccess(principal, session, caller, write: true, activeTurn: true);
        bool CanRespondTo(string? delegateSessionId) => canRespond &&
            (principal is InteropPrincipal.Human || caller!.IsLead || delegateSessionId == caller.Id);
        return ReadPendingApprovals().Where(a => a.SessionId == session.Id).Select(a => new PendingInteractionDto(
                a.Id, "approval", a.SessionId, a.TurnId, a.ExecutionId, a.Title, a.Options, null, CanRespondTo(a.DelegateSessionId), a.CreatedAtMs, a.ToolName, a.Arguments))
            .Concat(_interactions.OpenAsks().Where(a => a.Status == AskStatus.Open && a.SessionId == session.Id).Select(a =>
                new PendingInteractionDto(a.Id, "ask", session.Id, a.TurnId, null,
                    a.Questions[0].Question, null, a.Questions, CanRespondTo(a.TargetSessionId), a.CreatedAtMs)))
            .OrderBy(i => i.CreatedAtMs).ToArray();
    }
}
