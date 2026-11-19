using Perga.AgentKit;
using Perga.Contracts;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static AgentSessionDto ApplyContextUsage(
        AgentSessionDto session, TokenUsage usage, long atMs, bool invalidated) => session with
    {
        ContextUsedTokens = usage.EffectiveUsedTokens,
        ContextWindowTokens = usage.ContextWindowTokens,
        ContextUsageAtMs = atMs,
        ContextUsageRatio = usage.ContextWindowTokens is > 0
            ? Math.Clamp((double)usage.EffectiveUsedTokens / usage.ContextWindowTokens.Value, 0.0, 1.0)
            : 0.0,
        ContextUsageInvalidated = invalidated,
        UpdatedAtMs = atMs
    };

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

        if (pending is not null &&
            TryBuildIdleContextUsageUpdateCore(sessionId, agentSession, expectedEpoch, pending, out var updated))
        {
            await PersistAndEmitSessionUpdatedCoreAsync(updated, updated.UpdatedAtMs, ct).ConfigureAwait(false);
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
        out AgentSessionDto updated)
    {
        updated = null!;
        if (_track.ClosedAtMs is not null ||
            !_sessions.TryGetValue(sessionId, out var current) ||
            current.ArchivedAtMs is not null ||
            current.BoundAttemptId is not null ||
            current.Epoch != expectedEpoch ||
            !string.Equals(current.Status, "idle", StringComparison.Ordinal) ||
            _activeTurns.ContainsKey(sessionId) ||
            _compactingSessions.Contains(sessionId) ||
            _sessionResumeHolds.ContainsKey(sessionId) ||
            !_openAgentSessions.TryGetValue(sessionId, out var openSession) ||
            !ReferenceEquals(openSession, expectedSession))
        {
            return false;
        }

        if (!string.Equals(current.Model, idleUsage.Model, StringComparison.Ordinal) ||
            !_boundAgentModels.TryGetValue(sessionId, out var bound) ||
            !string.Equals(bound.Model, current.Model, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(current.NativeThreadId) &&
            !string.IsNullOrWhiteSpace(idleUsage.NativeThreadId) &&
            !string.Equals(current.NativeThreadId, idleUsage.NativeThreadId, StringComparison.Ordinal))
        {
            return false;
        }

        var usage = idleUsage.Usage;
        if (usage.EffectiveUsedTokens < 0 || usage.ContextWindowTokens is not > 0)
        {
            return false;
        }

        updated = ApplyContextUsage(current, usage, NowMs(), invalidated: false);
        return true;
    }
}
