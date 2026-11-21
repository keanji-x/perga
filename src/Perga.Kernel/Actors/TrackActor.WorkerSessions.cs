using System.Text;
using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Internal;
using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    internal Task<WorkerCompletionOrigin> CaptureWorkerCompletionOriginAsync(McpSessionPrincipal caller, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(caller.BoundTaskId) || caller.TaskRunCount is not { } runCount)
                throw KernelException.Forbidden("Completion tool requires an active bound task session.");
            var session = RequireSessionCore(caller.SessionId);
            if (!_activeTasks.TryGetValue(caller.BoundTaskId, out var activeTask) ||
                activeTask.SessionId != session.Id || activeTask.RunCount != runCount)
                throw KernelException.Conflict("The task execution no longer accepts completion.");
            if (!_scheduler.TryGetActive(session.Id, out var active) || active.Interrupted ||
                _mcpBridge.ResolveToken(caller.Token)?.SessionId != session.Id)
                throw KernelException.Conflict("The worker turn no longer accepts completion.");
            return new WorkerCompletionOrigin(caller.Token, active.TurnDto.Id, caller.BoundTaskId, runCount);
        }, ct);

    private void CheckWorkerCompletionCore(ActiveTaskRunState activeTask, WorkerCompletionOrigin? origin)
    {
        var task = RequireTaskCore(activeTask.TaskId);
        if (activeTask.Paused || activeTask.Settling || task.Status != TaskRunStatus.Running)
            throw KernelException.Conflict("The task no longer accepts completion from this turn.");
        if (origin is not null &&
            (origin.TaskId != activeTask.TaskId || origin.RunCount != activeTask.RunCount ||
             activeTask.SessionId is not { } sessionId || _mcpBridge.ResolveToken(origin.Token)?.SessionId != sessionId ||
             !_scheduler.TryGetActive(sessionId, out var turn) || turn.Interrupted || turn.TurnDto.Id != origin.TurnId))
            throw KernelException.Conflict("The worker turn no longer accepts completion.");
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
