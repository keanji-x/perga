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
    private void RequireWorkerOperatorCore(string? callerSessionId)
    {
        if (callerSessionId is not null && !string.Equals(callerSessionId, _track.LeadSessionId, StringComparison.Ordinal))
            throw KernelException.Forbidden("Only the current Track Lead can operate a Worker through MCP.");
    }

    private async Task<WorkerActivityDto?> ReadWorkerActivityCoreAsync(
        AgentSessionDto session, ActiveTurnState? active, string? activityTurnId, long? afterSequence, CancellationToken ct)
    {
        if (session.BoundAttemptId is not { } attemptId || session.BoundTaskId is not { } taskId)
            return null;

        var task = RequireTaskCore(taskId);
        _activeAttempts.TryGetValue(attemptId, out var attempt);
        var phase = attempt is null ? (await _store.ListAttemptsForTaskAsync(taskId, ct).ConfigureAwait(false))
            .FirstOrDefault(row => row.AttemptId == attemptId)?.Status.ToLowerInvariant() ?? "unavailable"
            : active?.Interrupted == true ? "interrupting"
            : attempt.InterruptionUnconfirmed ? "interrupt_unknown"
            : attempt.Paused ? "paused"
            : attempt.AttemptDto.Status.ToLowerInvariant();

        var asks = active is null ? [] : active.OwnedAskIds
            .Select(id => _asks.GetValueOrDefault(id)).OfType<AskDto>().Where(ask => ask.Status == AskStatus.Open).ToArray();
        var (sequence, reset, events, text, truncated) = active is null
            ? (0L, activityTurnId is not null, Array.Empty<WorkerActivityEvent>(), string.Empty, false)
            : active.Activity.Read(afterSequence, activityTurnId == active.TurnDto.Id);

        var messages = await _store.WorkerMessages.RecentAsync(session.Id, ct).ConfigureAwait(false);
        var settings = await ReadWorkerSettingsCoreAsync(session, ct).ConfigureAwait(false);
        var support = WorkerConfigurationSupport.ForProvider(session.Provider);
        return new WorkerActivityDto(task, attemptId, phase, active?.AgentSession.SupportsNativeSteer == true,
            asks, sequence, reset, events, messages.Select(message => new WorkerMessageActivity(message.Id, message.AuthorId,
                message.Content, message.ResultTurnId ?? message.TargetTurnId, message.Delivery, message.CreatedAtMs)).ToArray(), text, truncated,
            settings, active?.Settings, support.ModelOverride, support.ReasoningEfforts, attempt?.LastError);
    }

    private async Task<WorkerSettings> ReadWorkerSettingsCoreAsync(AgentSessionDto session, CancellationToken ct) =>
        (_store.WorkerSettings is { } store ? await store.ReadAsync(session.Id, ct).ConfigureAwait(false) : null)
        ?? new WorkerSettings(0, session.Model, null, "ask");

    public Task<WorkerSettings> UpdateWorkerSettingsAsync(
        string sessionId, UpdateWorkerSettingsRequest request, string? callerSessionId, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireWorkerOperatorCore(callerSessionId);
            var (session, _) = RequireCurrentWorkerCore(sessionId, request.AttemptId);
            var current = await ReadWorkerSettingsCoreAsync(session, ct).ConfigureAwait(false);
            if (request.Version != current.Version)
                throw KernelException.Conflict("Worker settings changed. Refresh before updating them.");

            var model = request.Model?.Trim();
            var support = WorkerConfigurationSupport.ForProvider(session.Provider);
            if (model != current.Model && (!support.ModelOverride || string.IsNullOrWhiteSpace(model)))
                throw KernelException.InvalidArgument("Choose an explicit model supported by this Worker provider.");
            if (model != current.Model)
            {
                var providers = await _agentRuntime.ProbeProvidersAsync(ct).ConfigureAwait(false);
                if (!providers.Any(provider => provider.Available && provider.Provider == session.Provider &&
                    provider.Models.Contains(model!, StringComparer.Ordinal)))
                    throw KernelException.InvalidArgument("The model is not listed by this Worker provider.");
            }
            if (request.ReasoningEffort != current.ReasoningEffort &&
                (request.ReasoningEffort is null || !support.ReasoningEfforts.Contains(request.ReasoningEffort, StringComparer.Ordinal)))
                throw KernelException.InvalidArgument("Choose an explicit reasoning effort supported by this Worker provider.");
            if (request.ApprovalMode is not ("ask" or "deny"))
                throw KernelException.InvalidArgument("Worker approval mode must be ask or deny.");
            if (current.ApprovalMode == "deny" && request.ApprovalMode == "ask")
                throw KernelException.Forbidden("Start a new attempt to restore approval requests. Denied requests cannot be re-enabled for this attempt.");

            var next = new WorkerSettings(checked(current.Version + 1), model, request.ReasoningEffort, request.ApprovalMode);
            var evt = SessionUpdatedEvent(session);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            await _store.WorkerSettings!.WriteAsync(sessionId, next, callerSessionId ?? "owner:installation", evt, ct).ConfigureAwait(false);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
            return next;
        }, ct);

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

    public async Task<WorkerMessageReceipt> SendWorkerMessageAsync(
        string sessionId, WorkerMessageRequest request, string? callerSessionId, CancellationToken ct)
    {
        ValidateRequestKey(request.RequestKey);
        if (string.IsNullOrWhiteSpace(request.Content) || string.IsNullOrWhiteSpace(request.AttemptId))
            throw KernelException.InvalidArgument("A Worker message requires attemptId and non-empty content.");
        var hash = RequestIdentity.Hash(new { version = 1, request.AttemptId, request.TurnId, request.Content, request.AllowQueue });
        var author = callerSessionId ?? "owner:installation";

        var delivery = await EnqueueAsync(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireWorkerOperatorCore(callerSessionId);
            RequireSessionCore(sessionId);
            var replay = await _store.WorkerMessages.FindAsync(sessionId, author, request.RequestKey, ct).ConfigureAwait(false);
            if (replay is not null)
            {
                RequireMatchingHash(replay.RequestHash, hash);
                return Task.FromResult(ToWorkerMessageReceipt(replay));
            }

            var (_, attempt) = RequireCurrentWorkerCore(sessionId, request.AttemptId);
            _activeTurns.TryGetValue(sessionId, out var active);
            if (request.TurnId is not null && (active?.TurnDto.Id != request.TurnId || active.Interrupted))
                throw KernelException.Conflict("The Worker turn changed. Refresh the session before steering.");
            if (attempt.InterruptionUnconfirmed ||
                (request.TurnId is null && (active is not null || _reservedWakeSessions.ContainsKey(sessionId))))
                throw KernelException.Conflict("A Worker turn is already running or starting. Target a turn to steer it.");

            var native = active?.AgentSession.SupportsNativeSteer == true;
            if (active is not null && !native && !request.AllowQueue)
                throw KernelException.Conflict("This provider requires explicit permission to queue the message for the next turn.");

            var message = new WorkerMessage($"inp_{Guid.NewGuid():N}", sessionId, attempt.AttemptId, author,
                request.RequestKey, hash, request.TurnId, native ? request.TurnId : null, request.Content,
                native ? "dispatching" : "queued", NowMs());
            await _store.WorkerMessages.InsertAsync(message, ct).ConfigureAwait(false);

            if (!native)
            {
                attempt.HasQueuedMessages = true;
                if (active is null)
                {
                    attempt.Paused = false;
                    await ScheduleWorkerMessageCoreAsync(sessionId).ConfigureAwait(false);
                }
                return Task.FromResult(ToWorkerMessageReceipt(message));
            }

            var result = new TaskCompletionSource<WorkerMessageReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            var turnCancellation = active!.TurnCts.Token;
            _ = TrackBackgroundJob(async () =>
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(turnCancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var outcome = "unknown";
                    try
                    {
                        outcome = turnCancellation.IsCancellationRequested ? "interrupted"
                            : (await active.AgentSession.SteerAsync(active.TurnDto.Id, message.Content, timeout.Token).ConfigureAwait(false)) switch
                            {
                                SteerDelivery.Accepted => "delivered",
                                SteerDelivery.Declined => "rejected",
                                _ => "unknown"
                            };
                    }
                    catch (Exception)
                    {
                        // The provider may have consumed the message before losing the response.
                    }

                    var settled = message with { Delivery = outcome };
                    await _store.WorkerMessages.UpdateAsync(settled, CancellationToken.None).ConfigureAwait(false);
                    if (outcome == "delivered")
                    {
                        var item = JsonHelpers.ToElement(new { content = message.Content, author = message.AuthorId, inputId = message.Id });
                        active.Activity.Append("steer", item, NowMs());
                    }
                    result.TrySetResult(ToWorkerMessageReceipt(settled));
                }
                catch (Exception ex)
                {
                    result.TrySetException(ex);
                }
            });
            return result.Task;
        }, ct).ConfigureAwait(false);

        return await delivery.WaitAsync(ct).ConfigureAwait(false);
    }

    public Task InterruptWorkerAsync(string sessionId, string attemptId, string turnId, string? callerSessionId, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireWorkerOperatorCore(callerSessionId);
            var (_, attempt) = RequireCurrentWorkerCore(sessionId, attemptId);
            if (!_activeTurns.TryGetValue(sessionId, out var active) || active.TurnDto.Id != turnId)
                throw KernelException.Conflict("The Worker turn changed. Refresh the session before interrupting.");
            if (active.Interrupted)
                return;
            attempt.Paused = true;
            _mcpBridge.RevokeSession(sessionId);
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await InterruptActiveTurnCoreAsync(active, stopTimeout.Token).ConfigureAwait(false);
        }, ct);

    private static async Task InterruptActiveTurnCoreAsync(ActiveTurnState active, CancellationToken ct)
    {
        active.Interrupted = true;
        try
        {
            await active.AgentSession.CancelTurnAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            active.TurnCts.Cancel();
        }
    }

    private async Task CloseInterruptedWorkerCoreAsync(ActiveTurnState turn, ActiveAttemptState attempt)
    {
        foreach (var askId in turn.OwnedAskIds)
        {
            if (!_asks.TryGetValue(askId, out var ask) || ask.Status != AskStatus.Open)
                continue;
            var withdrawn = ask with { Status = AskStatus.Withdrawn };
            var evt = new EventEnvelopeDto(null, _track.AreaId, TrackId, KernelEventKinds.AskChanged, JsonHelpers.ToElement(withdrawn), NowMs());
            await _store.UpsertAskAsync(withdrawn, evt, CancellationToken.None).ConfigureAwait(false);
            _asks[askId] = withdrawn;
            if (_pendingAskWaiters.Remove(askId, out var waiter))
                waiter.TrySetCanceled();
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, CancellationToken.None).ConfigureAwait(false);
        }
        if (_openAgentSessions.Remove(turn.SessionId, out var agent))
        {
            try
            {
                await agent.DisposeAsync().ConfigureAwait(false);
                attempt.InterruptionUnconfirmed = !agent.ShutdownConfirmed;
            }
            catch (Exception)
            {
                attempt.InterruptionUnconfirmed = true;
            }
        }
    }

    internal Task<WorkerCompletionOrigin> CaptureWorkerCompletionOriginAsync(McpSessionPrincipal caller, CancellationToken ct) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireBoundAttemptSessionCore(caller.SessionId, "Worker completion");
            if (!_activeTurns.TryGetValue(session.Id, out var active) || active.Interrupted ||
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
             !_activeTurns.TryGetValue(sessionId, out var turn) || turn.Interrupted || turn.TurnDto.Id != origin.TurnId))
            throw KernelException.Conflict("The Worker turn no longer accepts completion.");
    }

    private async Task ScheduleWorkerMessageCoreAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) || session.BoundAttemptId is not { } attemptId ||
            !_activeAttempts.TryGetValue(attemptId, out var attempt) || !attempt.HasQueuedMessages || attempt.Paused ||
            !string.Equals(attempt.AttemptDto.Status, "running", StringComparison.OrdinalIgnoreCase) || attempt.Settling || _activeTurns.ContainsKey(sessionId) ||
            _compactingSessions.Contains(sessionId) || _reservedWakeSessions.ContainsKey(sessionId) || HoldsDurableSlot(session))
            return;

        var next = await _store.WorkerMessages.NextAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        if (next is null)
        {
            attempt.HasQueuedMessages = false;
            return;
        }

        await _store.WorkerMessages.UpdateAsync(next with { Delivery = "dispatching" }, CancellationToken.None).ConfigureAwait(false);
        var reservation = Guid.NewGuid();
        _reservedWakeSessions.Add(sessionId, reservation);
        _ = TrackBackgroundJob(async () =>
        {
            var shouldContinue = false;
            string? error = null;
            try
            {
                var turn = await ExecuteSessionTurnPipelineAsync(sessionId, "user", next.Content,
                    attempt.Cts.Token, wakeReservation: reservation, workerInput: next).ConfigureAwait(false);
                shouldContinue = turn.Status == "completed";
                if (turn.Status is "failed" or "unknown")
                    error = turn.ErrorText;
            }
            catch (OperationCanceledException) when (attempt.Cts.IsCancellationRequested || _actorShutdownCts.IsCancellationRequested)
            {
                await _store.WorkerMessages.UpdateAsync(next with { Delivery = "interrupted" }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (KernelException ex) when (ex.Code == KernelErrorCode.Conflict)
            {
                await _store.WorkerMessages.UpdateAsync(next with { Delivery = "interrupted" }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                await _store.WorkerMessages.UpdateAsync(next with { Delivery = "unknown" }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (!_actorShutdownCts.IsCancellationRequested)
                    await EnqueueAsync(async _ =>
                    {
                        if (_reservedWakeSessions.TryGetValue(sessionId, out var held) && held == reservation)
                            _reservedWakeSessions.Remove(sessionId);
                        attempt.LastError = error;
                        if (shouldContinue)
                            await ScheduleWorkerMessageCoreAsync(sessionId).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
            }
        });
    }

    private static WorkerMessageReceipt ToWorkerMessageReceipt(WorkerMessage message) =>
        new(message.ResultTurnId ?? message.TargetTurnId, message.Delivery, message.Id);

    private void PublishTurnActivity(ActiveTurnState active, string kind, JsonElement payload, long now)
    {
        if (active.Settings is null)
        {
            _eventHub.PublishTransient(_track.AreaId, TrackId, kind, payload, now);
            return;
        }
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
