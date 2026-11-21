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
        var operation = await EnqueueAsync(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            RequireInteropCore(caller, session, write: true, destructive: request.Archived == true || request.Provider is not null);
            if (_track.ClosedAtMs is not null) throw KernelException.PreconditionFailed("The Track is closed.");
            if (session.BoundAttemptId is { } attemptId) RequireCurrentWorkerCore(sessionId, attemptId);
            if (request.ExpectedAttemptId is not null && session.BoundAttemptId != request.ExpectedAttemptId)
                throw KernelException.Conflict("The session attempt changed.");
            var configChanged = request.Provider is not null || request.Model.IsSet || request.ReasoningEffort.IsSet || request.PermissionMode.IsSet;
            if (configChanged && request.ExpectedConfigVersion is { } version && version != session.ConfigVersion)
                throw KernelException.Conflict("Session configuration changed. Refresh before updating it.");
            if (configChanged && caller is InteropPrincipal.Session && request.ExpectedConfigVersion is null)
                throw KernelException.InvalidArgument("expectedConfigVersion is required for configuration updates.");
            request = request with
            {
                Model = request.Model.IsSet ? new(string.IsNullOrWhiteSpace(request.Model.Value) ? null : request.Model.Value.Trim()) : default,
                ReasoningEffort = request.ReasoningEffort.IsSet ? new(string.IsNullOrWhiteSpace(request.ReasoningEffort.Value) ? null : request.ReasoningEffort.Value.Trim()) : default
            };
            var provider = request.Provider ?? session.Provider;
            var providerChanged = provider != session.Provider;
            if (providerChanged && _scheduler.IsOccupied(session))
                throw KernelException.Conflict("Provider changes require an idle session.");
            var modelChanged = providerChanged || request.Model.IsSet && request.Model.Value != session.Model ||
                request.ReasoningEffort.IsSet && request.ReasoningEffort.Value != session.ReasoningEffort;
            var archivedAtMs = session.ArchivedAtMs;
            if (request.Archived == true) archivedAtMs ??= NowMs();
            else if (request.Archived == false) archivedAtMs = null;
            var updated = session with
            {
                Title = request.Title?.Trim() ?? session.Title,
                ArchivedAtMs = archivedAtMs,
                Provider = provider,
                Model = request.Model.IsSet ? request.Model.Value : session.Model,
                ReasoningEffort = request.ReasoningEffort.IsSet ? request.ReasoningEffort.Value : session.ReasoningEffort,
                PermissionMode = request.PermissionMode.IsSet || providerChanged
                    ? ResolveSessionPermissionModeCore(provider, request.PermissionMode.IsSet ? request.PermissionMode.Value : null, sessionId)
                    : session.PermissionMode,
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
                    _actorShutdownCts.Token, idleOnly: true).ConfigureAwait(false);
                completion.TrySetResult(updated);
            }
            catch (Exception error)
            {
                if (!_actorShutdownCts.IsCancellationRequested && _runtimes.IsShutdownUnconfirmed(updated.Id))
                    await EnqueueAsync(async _ =>
                    {
                        var current = RequireSessionCore(updated.Id);
                        var unknown = current with { Status = "unknown", UnknownReason = "shutdown_unconfirmed", UpdatedAtMs = NowMs() };
                        await PersistAndEmitSessionUpdatedCoreAsync(unknown, unknown.UpdatedAtMs, CancellationToken.None).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
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
