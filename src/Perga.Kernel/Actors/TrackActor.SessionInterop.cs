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
        if (write)
        {
            if (identity.AuthorizedPolicyGeneration is { } generation && generation != _policyGeneration)
                throw KernelException.PolicyDenied("Active policy changed after authorization.");
            if (_mcpBridge.ResolveToken(identity.Token)?.SessionId != caller.Id ||
                identity.ExecutionOrigin?.TurnId is not { } turnId ||
                !_scheduler.TryGetActive(caller.Id, out var turn) || turn.TurnDto.Id != turnId || turn.Interrupted)
                throw KernelException.Conflict("The originating session turn is no longer running.");
        }
        if (target is not null)
            SessionPolicy.RequireAccess(principal, target, caller, write, _scheduler.HasActive(target.Id), destructive);
        return caller;
    }

    private void RequireSessionMutationCore(AgentSessionDto session, InteropPrincipal principal, string? expectedAttemptId = null,
        bool destructive = false)
    {
        RequireInteropCore(principal, session, write: true, destructive: destructive);
        if (expectedAttemptId is not null && session.BoundAttemptId != expectedAttemptId)
            throw KernelException.Conflict("The session attempt changed.");
        RequireSessionWritableCore(session);
    }

    private void RequireSessionWritableCore(AgentSessionDto session)
    {
        if (_track.ClosedAtMs is not null || session.ArchivedAtMs is not null)
            throw KernelException.PreconditionFailed("Reopen the conversation before modifying it.");
        if (session.BoundAttemptId is { } attemptId) RequireCurrentWorkerCore(session.Id, attemptId);
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
                _activeAttempts.TryGetValue(session.BoundAttemptId ?? "", out var attempt);
                var state = session.Status == "queued" ? "running" : session.Status;
                string? readonlyReason = null;
                if (session.ArchivedAtMs is not null) readonlyReason = "archived";
                else if (_track.ClosedAtMs is not null) readonlyReason = "track_closed";
                else if (session.BoundAttemptId is not null && attempt is null) readonlyReason = "worker_finished";
                var pendingInteractionCount = pendingApprovals.Count(a => a.SessionId == session.Id) +
                    _asks.Values.Count(a => a.Status == AskStatus.Open && a.SessionId == session.Id);
                summaries.Add(new(session, state, readonlyReason, attempt?.Paused == true, last, pendingInteractionCount));
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
        if (session.BoundAttemptId is { } attemptId) RequireCurrentWorkerCore(sessionId, attemptId);
        if (request.ExpectedAttemptId is not null && session.BoundAttemptId != request.ExpectedAttemptId)
            throw KernelException.Conflict("The session attempt changed.");
        if (configChanged && request.ExpectedConfigVersion is { } version && version != session.ConfigVersion)
            throw KernelException.Conflict("Session configuration changed. Refresh before updating it.");
        if (configChanged && caller is InteropPrincipal.Session && request.ExpectedConfigVersion is null)
            throw KernelException.InvalidArgument("expectedConfigVersion is required for configuration updates.");
        if (request.Provider is { } provider && SessionLaunchValidator.NormalizeProvider(provider) !=
            SessionLaunchValidator.NormalizeProvider(session.Provider) && _scheduler.IsOccupied(session))
            throw KernelException.Conflict("Provider changes require an idle session.");
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
            await PersistAndEmitSessionUpdatedCoreAsync(updated, updated.UpdatedAtMs, ct).ConfigureAwait(false);
            if (modelChanged) InvalidateContextBindingCore(sessionId);
            if (providerChanged) _mcpBridge.RevokeSession(sessionId);
            if (!providerChanged) return Task.FromResult(updated);
            return actor.TrackProviderChangeCloseAsync(updated);
        }, ct).ConfigureAwait(false);
        return await operation.WaitAsync(ct).ConfigureAwait(false);
    }

    private Task<AgentSessionDto> TrackProviderChangeCloseAsync(AgentSessionDto updated)
    {
        var completion = new TaskCompletionSource<AgentSessionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = TrackBackgroundJob(async () =>
        {
            try
            {
                await _runtimes.Get(updated.Id).CloseAsync(_options.SessionStartupTimeout,
                    _actorShutdownCts.Token, shouldClose: () => !_scheduler.IsBusy(updated.Id)).ConfigureAwait(false);
                completion.TrySetResult(updated);
            }
            catch (Exception error)
            {
                if (!_actorShutdownCts.IsCancellationRequested && _runtimes.IsShutdownUnconfirmed(updated.Id))
                    await EnqueueAsync(_ => MarkShutdownUnconfirmedIfNeededAsync(updated.Id), CancellationToken.None).ConfigureAwait(false);
                completion.TrySetException(error);
            }
        });
        return completion.Task;
    }

    private async Task NotifyParentCoreAsync(AgentSessionDto child, string policy, string key, string prompt)
    {
        if (child.NotifyParent != policy || child.ParentSessionId is not { } parentId ||
            !_sessions.TryGetValue(parentId, out var parent) || parent.ArchivedAtMs is not null) return;
        await _store.TrackWakes.EnqueueAsync(TrackWakeRequests.Prompt(TrackId, TrackWakeKinds.TaskSettled,
            key, prompt) with { TargetSessionId = parentId }, CancellationToken.None).ConfigureAwait(false);
        await PokeSessionCoreAsync(parentId).ConfigureAwait(false);
    }
}
