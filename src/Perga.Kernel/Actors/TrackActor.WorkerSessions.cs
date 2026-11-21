using System.Text.Json;
using System.Text;
using Perga.AgentKit;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private (AgentSessionDto Session, ActiveAttemptState Attempt) RequireCurrentWorkerCore(
        string sessionId, string expectedAttemptId)
    {
        var session = RequireSessionCore(sessionId);
        if (session.BoundAttemptId != expectedAttemptId || !_activeAttempts.TryGetValue(expectedAttemptId, out var attempt) ||
            attempt.SessionId != sessionId || RequireTaskCore(attempt.TaskId).LatestAttemptId != expectedAttemptId ||
            !string.Equals(attempt.AttemptDto.Status, "running", StringComparison.OrdinalIgnoreCase) || attempt.Settling || _track.ClosedAtMs.HasValue)
            throw KernelException.Conflict("The Worker attempt is no longer running. Refresh the task before sending a command.");
        RequireDispatchWorkspace(session);
        return (session, attempt);
    }

    private async Task CloseInterruptedWorkerAsync(ActiveTurnState turn, ActiveAttemptState attempt)
    {
        var confirmed = false;
        try
        {
            await _runtimes.Get(turn.SessionId).CloseAsync(_options.SessionStartupTimeout, CancellationToken.None).ConfigureAwait(false);
            confirmed = true;
        }
        catch { /* Durable settlement fences the session and retains the handle for retry. */ }
        await EnqueueAsync(_ =>
        {
            attempt.InterruptionUnconfirmed = !confirmed;
            return Task.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    internal Task<WorkerCompletionOrigin> CaptureWorkerCompletionOriginAsync(McpSessionPrincipal caller, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireBoundAttemptSessionCore(caller.SessionId, "Worker completion");
            if (!_scheduler.TryGetActive(session.Id, out var active) || active.Interrupted ||
                _mcpBridge.ResolveToken(caller.Token)?.SessionId != session.Id)
                throw KernelException.Conflict("The Worker turn no longer accepts completion.");
            return new WorkerCompletionOrigin(caller.Token, active.TurnDto.Id);
        }, ct);

    private void CheckWorkerCompletionCore(ActiveAttemptState attempt, WorkerCompletionOrigin? origin)
    {
        if (attempt.Paused || attempt.Settling || !string.Equals(attempt.AttemptDto.Status, "running", StringComparison.OrdinalIgnoreCase))
            throw KernelException.Conflict("The Worker attempt no longer accepts completion from this turn.");
        if (origin is not null &&
            (attempt.SessionId is not { } sessionId || _mcpBridge.ResolveToken(origin.Token)?.SessionId != sessionId ||
             !_scheduler.TryGetActive(sessionId, out var turn) || turn.Interrupted || turn.TurnDto.Id != origin.TurnId))
            throw KernelException.Conflict("The Worker turn no longer accepts completion.");
    }

    private async Task ScheduleWorkerMessageCoreAsync(SessionMessage next, ActiveAttemptState attempt)
    {
        var sessionId = next.SessionId;
        if (!await Messages.TransitionAsync(next with { Delivery = "dispatching" }, "queued",
                CancellationToken.None).ConfigureAwait(false))
            return;
        var reservation = Guid.NewGuid();
        _scheduler.Reserve(sessionId, reservation);
        _ = TrackBackgroundJob(async () =>
        {
            var shouldContinue = false;
            string? error = null;
            try
            {
                var turn = await _turnRunner.RunAsync(new WorkerMessageWorkItem(this, next, attempt, reservation), attempt.Cts.Token).ConfigureAwait(false);
                shouldContinue = turn?.Status == "completed";
                if (turn?.Status is "failed" or "unknown")
                    error = turn.ErrorText;
            }
            catch (OperationCanceledException) when (attempt.Cts.IsCancellationRequested || _actorShutdownCts.IsCancellationRequested)
            {
                await Messages.TransitionAsync(next with { Delivery = "interrupted" }, "dispatching", CancellationToken.None).ConfigureAwait(false);
            }
            catch (KernelException ex) when (ex.Code == KernelErrorCode.Conflict)
            {
                await Messages.TransitionAsync(next with { Delivery = "interrupted" }, "dispatching", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                await Messages.TransitionAsync(next with { Delivery = "unknown" }, "dispatching", CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (!_actorShutdownCts.IsCancellationRequested)
                    await EnqueueAsync(async _ =>
                    {
                        if (_scheduler.TryGetReservation(sessionId, out var held) && held == reservation)
                            _scheduler.ReleaseReservation(sessionId);
                        attempt.LastError = error;
                        if (shouldContinue)
                            await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
            }
        });
    }

    private void PublishTurnActivity(ActiveTurnState active, string kind, JsonElement payload, long now)
    {
        var sequence = active.Activity.Append(kind, payload, now);
        var wire = payload.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        wire["sequence"] = JsonHelpers.ToElement(sequence);
        _eventHub.PublishTransient(_track.AreaId, TrackId, kind, JsonHelpers.ToElement(wire), now);
    }

    private sealed class TurnActivity
    {
        private const int Capacity = 128;
        private const int PayloadLimit = 4096;
        private const int TextLimit = 65536;
        private readonly Queue<WorkerActivityEvent> _recent = new();
        private readonly StringBuilder _text = new();
        private bool _textTruncated;
        private long _sequence;

        public long Append(string kind, JsonElement payload, long now)
        {
            var raw = payload.GetRawText();
            lock (_recent)
            {
                if (kind == KernelEventKinds.SessionTextDelta && payload.TryGetProperty("delta", out var delta))
                {
                    _text.Append(delta.GetString());
                    if (_text.Length > TextLimit)
                    {
                        _text.Remove(0, _text.Length - TextLimit);
                        _textTruncated = true;
                    }
                }
                if (raw.Length > PayloadLimit)
                    payload = JsonHelpers.ToElement(new { preview = raw[..PayloadLimit], truncated = true });
                if (_recent.Count == Capacity)
                    _recent.Dequeue();
                _recent.Enqueue(new WorkerActivityEvent(++_sequence, kind, payload, now));
                return _sequence;
            }
        }

        public (long Sequence, bool Reset, WorkerActivityEvent[] Events, string Text, bool TextTruncated) Read(long? after, bool sameTurn)
        {
            lock (_recent)
            {
                var reset = after is not null && (!sameTurn || after < _sequence - _recent.Count || after > _sequence);
                return (_sequence, reset, _recent.Where(item => reset || after is null || item.Sequence > after).ToArray(),
                    sameTurn && after is not null && !reset ? string.Empty : _text.ToString(), _textTruncated);
            }
        }
    }
}
