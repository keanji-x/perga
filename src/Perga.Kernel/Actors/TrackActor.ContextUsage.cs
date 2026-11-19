using System.Runtime.CompilerServices;
using Perga.AgentKit;
using Perga.Contracts;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private readonly ConditionalWeakTable<IAgentSession, LiveContextUsage> _liveContextUsage = new();

    private sealed class LiveContextUsage
    {
        public long? Revision { get; set; }
        public long? WindowTokens { get; set; }
        public SessionContextUsage? Latest { get; set; }
        public IdleContextUsage? Trailing { get; set; }
        public IdleContextUsage? TrailingWithWindow { get; set; }
    }

    private bool IsCurrentContextBindingCore(
        AgentSessionDto session, IAgentSession provider, LiveContextUsage state) =>
        _openAgentSessions.TryGetValue(session.Id, out var open) && ReferenceEquals(provider, open) &&
        _liveContextUsage.TryGetValue(provider, out var current) && ReferenceEquals(state, current);

    private AgentSessionDto ApplyContextUsageCore(
        AgentSessionDto session, IAgentSession provider, LiveContextUsage state, long? revision, TokenUsage usage, long atMs)
    {
        if (!IsCurrentContextBindingCore(session, provider, state) ||
            revision is null || provider.ContextUsageRevision != revision ||
            usage.EffectiveUsedTokens < 0 || usage.ContextWindowTokens is <= 0)
        {
            return session;
        }

        state.WindowTokens = usage.ContextWindowTokens ?? (provider.CanReuseContextWindow && state.Revision == revision ? state.WindowTokens : null);
        state.Revision = revision;
        state.Latest = new SessionContextUsage(usage.EffectiveUsedTokens, state.WindowTokens, atMs);
        return session with { ContextUsage = state.Latest, UpdatedAtMs = atMs };
    }

    private AgentSessionDto ApplyTrailingContextUsageCore(
        AgentSessionDto session, IAgentSession provider, LiveContextUsage state, long? completedPromptId, long atMs)
    {
        var trailing = state.Trailing;
        var window = state.TrailingWithWindow;
        state.Trailing = null;
        state.TrailingWithWindow = null;
        if (completedPromptId is null || trailing is null || trailing.CompletedPromptId != completedPromptId)
            return session;
        if (window is not null && window.CompletedPromptId == completedPromptId && !ReferenceEquals(window, trailing))
            session = ApplyContextUsageCore(session, provider, state, window.ContextUsageRevision, window.Usage, atMs);
        return ApplyContextUsageCore(session, provider, state, trailing.ContextUsageRevision, trailing.Usage, atMs);
    }

    private void InvalidateContextBindingCore(string sessionId)
    {
        if (_openAgentSessions.TryGetValue(sessionId, out var provider))
            _liveContextUsage.Remove(provider);
    }

    private double? CurrentContextUsageRatioCore(AgentSessionDto session) =>
        _openAgentSessions.TryGetValue(session.Id, out var provider) &&
        _liveContextUsage.TryGetValue(provider, out var state) &&
        state.Revision == provider.ContextUsageRevision && ReferenceEquals(session.ContextUsage, state.Latest)
            ? state.Latest?.Ratio
            : null;

    private async Task RegisterOpenAgentSessionCoreAsync(
        AgentSessionDto session,
        IAgentSession agentSession,
        string? permissionMode,
        CancellationToken ct)
    {
        _openAgentSessions[session.Id] = agentSession;
        _boundAgentModels[session.Id] = (session.Model, session.ReasoningEffort);
        _openAgentSessionPermissionModes[session.Id] = permissionMode;

        if (session.BoundAttemptId is not null)
        {
            return;
        }

        var sessionId = session.Id;
        var expectedEpoch = session.Epoch;
        var pending = agentSession.AttachIdleContextUsageHandler(idleUsage =>
            EnqueueIdleContextUsage(sessionId, agentSession, expectedEpoch, idleUsage));

        AgentSessionDto? updated = null;
        foreach (var observation in pending)
        {
            if (TryBuildIdleContextUsageUpdateCore(sessionId, agentSession, expectedEpoch, observation, out var next, bindPending: true))
                updated = next;
        }

        if (updated is not null)
        {
            try
            {
                await PersistAndEmitSessionUpdatedCoreAsync(updated, updated.UpdatedAtMs, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort pending idle usage persistence; never fail or dispose an already-bound session.
            }
        }
    }

    private void EnqueueIdleContextUsage(
        string sessionId,
        IAgentSession expectedSession,
        int expectedEpoch,
        IdleContextUsage idleUsage)
    {
        if (_disposed || _actorShutdownCts.IsCancellationRequested)
        {
            return;
        }

        TouchActivity();
        Interlocked.Increment(ref _queuedMailboxOps);
        var written = _mailbox.Writer.TryWrite(async actor =>
        {
            try
            {
                if (actor._disposed || actor._actorShutdownCts.IsCancellationRequested || actor._track.ClosedAtMs is not null)
                {
                    return;
                }

                await actor.EnsureHydratedCoreAsync(CancellationToken.None).ConfigureAwait(false);
                if (actor.TryBuildIdleContextUsageUpdateCore(
                        sessionId, expectedSession, expectedEpoch, idleUsage, out var updated))
                {
                    await actor.PersistAndEmitSessionUpdatedCoreAsync(
                        updated, updated.UpdatedAtMs, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort idle usage persistence; ignore shutdown/eviction races.
            }
            finally
            {
                actor.TouchActivity();
                Interlocked.Decrement(ref actor._queuedMailboxOps);
            }
        });

        if (!written)
        {
            Interlocked.Decrement(ref _queuedMailboxOps);
        }
    }

    private bool TryBuildIdleContextUsageUpdateCore(
        string sessionId,
        IAgentSession expectedSession,
        int expectedEpoch,
        IdleContextUsage idleUsage,
        out AgentSessionDto updated,
        bool bindPending = false)
    {
        updated = null!;
        if (_track.ClosedAtMs is not null ||
            !_sessions.TryGetValue(sessionId, out var current) ||
            current.ArchivedAtMs is not null ||
            current.BoundAttemptId is not null ||
            current.Epoch != expectedEpoch ||
            _scheduler.HasResumeHold(sessionId) ||
            !_openAgentSessions.TryGetValue(sessionId, out var openSession) ||
            !ReferenceEquals(openSession, expectedSession))
        {
            return false;
        }

        if (!_boundAgentModels.TryGetValue(sessionId, out var bound) ||
            bound != (current.Model, current.ReasoningEffort))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(current.NativeThreadId) &&
            !string.IsNullOrWhiteSpace(idleUsage.NativeThreadId) &&
            !string.Equals(current.NativeThreadId, idleUsage.NativeThreadId, StringComparison.Ordinal))
        {
            return false;
        }

        if (_scheduler.HasActive(sessionId) || _scheduler.IsCompacting(sessionId))
        {
            // The notification can arrive on the RPC reader before the actor settles the terminal event.
            if (idleUsage.CompletedPromptId is not null && idleUsage.ContextUsageRevision == expectedSession.ContextUsageRevision &&
                _liveContextUsage.TryGetValue(expectedSession, out var state))
            {
                state.Trailing = idleUsage;
                if (idleUsage.Usage.ContextWindowTokens is > 0)
                    state.TrailingWithWindow = idleUsage;
            }
            return false;
        }
        if (current.Status != "idle" && !(bindPending && current.Status == "running"))
            return false;

        updated = ApplyContextUsageCore(current, expectedSession, _liveContextUsage.GetOrCreateValue(expectedSession),
            idleUsage.ContextUsageRevision, idleUsage.Usage, NowMs());
        return !ReferenceEquals(updated, current);
    }
}
