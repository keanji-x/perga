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

        var approvals = active is null ? [] : ReadPendingApprovals(active.TurnDto.Id);
        var (sequence, reset, events, text, truncated) = active is null
            ? (0L, activityTurnId is not null, Array.Empty<WorkerActivityEvent>(), string.Empty, false)
            : active.Activity.Read(afterSequence, activityTurnId == active.TurnDto.Id);

        var messages = await Messages.RecentWorkerAsync(session.Id, ct).ConfigureAwait(false);
        var settings = await ReadWorkerSettingsCoreAsync(session, ct).ConfigureAwait(false);
        var configuration = GetWorkerProviderConfigurationCore(session.Id, attempt);
        return new WorkerActivityDto(task, attemptId, phase, active?.AgentSession.SupportsNativeSteer == true,
            approvals, sequence, reset, events, messages.Select(message => new WorkerMessageActivity(message.Id, message.AuthorId,
                message.Content, message.DeliveredTurnId ?? message.TurnId ?? message.TargetTurnId, message.Delivery, message.CreatedAtMs)).ToArray(), text, truncated,
            settings, active?.ProviderAccepted == true ? active.Settings : null, configuration?.Models.Count > 0,
            configuration?.GetReasoningEfforts(settings.Model) ?? [], attempt?.LastError, configuration,
            _options.WorkerApprovalAuthorizationConfigured);
    }

    private async Task<WorkerSettings> ReadWorkerSettingsCoreAsync(AgentSessionDto session, CancellationToken ct) =>
        (_store.WorkerSettings is { } store ? await store.ReadAsync(session.Id, ct).ConfigureAwait(false) : null)
        ?? new WorkerSettings(0, session.Model, null, "ask");

    public Task<WorkerSettings> UpdateWorkerSettingsAsync(
        string sessionId, UpdateWorkerSettingsRequest request, string? callerSessionId, CancellationToken ct,
        bool verifiedApprovalOwner = false) =>
        EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireWorkerOperatorCore(callerSessionId);
            var (session, attempt) = RequireCurrentWorkerCore(sessionId, request.AttemptId);
            var current = await ReadWorkerSettingsCoreAsync(session, ct).ConfigureAwait(false);
            if (request.Version != current.Version)
                throw KernelException.Conflict("Worker settings changed. Refresh before updating them.");

            var model = request.Model?.Trim();
            var configuration = GetWorkerProviderConfigurationCore(session.Id, attempt);
            if (model != current.Model && (model is null ? configuration?.DefaultModel is null
                : configuration?.Models.Any(option => option.Id == model) != true))
                throw KernelException.InvalidArgument("Choose a model reported by this Worker provider or an available provider default.");
            if (request.ReasoningEffort is { } effort && (effort != current.ReasoningEffort || model != current.Model) &&
                configuration?.GetReasoningEfforts(model).Contains(effort, StringComparer.Ordinal) != true)
                throw KernelException.InvalidArgument("This Worker provider did not report that reasoning effort for the selected model.");
            if (request.ReasoningEffort is null && current.ReasoningEffort is not null && configuration?.GetDefaultReasoningEffort(model) is null)
                throw KernelException.InvalidArgument("This Worker provider did not report a reasoning default for the selected model.");
            if (request.ApprovalMode is not ("ask" or "deny"))
                throw KernelException.InvalidArgument("Worker approval mode must be ask or deny.");
            var restoringApproval = current.ApprovalMode == "deny" && request.ApprovalMode == "ask";
            if (restoringApproval && (callerSessionId is not null || !verifiedApprovalOwner || !_options.WorkerApprovalAuthorizationConfigured))
                throw KernelException.Forbidden("Restoring Worker approval requests requires verified authorization from the installation owner.");

            var next = new WorkerSettings(checked(current.Version + 1), model, request.ReasoningEffort, request.ApprovalMode);
            var configurationChanged = (current.Model, current.ReasoningEffort) != (next.Model, next.ReasoningEffort);
            var updatedSession = configurationChanged ? session with { ContextUsage = null, UpdatedAtMs = NowMs() } : null;
            var evt = SessionUpdatedEvent(updatedSession ?? session);
            await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
            var author = restoringApproval ? "owner:verified" : callerSessionId ?? InstallationAuthor;
            await _store.WorkerSettings!.WriteAsync(sessionId, next, author, updatedSession, evt, ct).ConfigureAwait(false);
            if (updatedSession is not null)
            {
                _sessions[sessionId] = updatedSession;
                InvalidateContextBindingCore(sessionId);
            }
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, evt, ct).ConfigureAwait(false);
            return next;
        }, ct);

    private WorkerProviderConfiguration? GetWorkerProviderConfigurationCore(string sessionId, ActiveAttemptState? attempt)
    {
        if (!_openAgentSessions.TryGetValue(sessionId, out var agent)) return attempt?.ProviderConfiguration;
        if (agent.WorkerConfiguration is not { } configuration || attempt is null) return agent.WorkerConfiguration;

        // Some adapters report current values on resume without reporting the original defaults.
        var previous = attempt.ProviderConfiguration;
        var model = configuration.DefaultModel;
        if (model is null && previous?.DefaultModel is { } originalModel && configuration.Models.Any(option => option.Id == originalModel))
            model = originalModel;
        var effort = configuration.DefaultReasoningEffort;
        if (effort is null && previous?.DefaultReasoningEffort is { } originalEffort && previous.DefaultModel == model &&
            configuration.GetReasoningEfforts(model).Contains(originalEffort, StringComparer.Ordinal))
            effort = originalEffort;
        attempt.ProviderConfiguration = configuration with { DefaultModel = model, DefaultReasoningEffort = effort };
        return attempt.ProviderConfiguration;
    }

    private WorkerSettings ResolveWorkerTurnSettingsCore(AgentSessionDto session, WorkerSettings desired)
    {
        if (session.BoundAttemptId is not { } attemptId || !_activeAttempts.TryGetValue(attemptId, out var attempt) ||
            (GetWorkerProviderConfigurationCore(session.Id, attempt) ?? attempt.ProviderConfiguration) is not { } configuration)
            return desired;
        var model = desired.Model ?? configuration.DefaultModel;
        return desired with { Model = model, ReasoningEffort = desired.ReasoningEffort ?? configuration.GetDefaultReasoningEffort(model) };
    }

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
        if (request.Mode is { } mode && (!Enum.IsDefined(mode) || request.AllowQueue))
            throw KernelException.InvalidArgument("Choose an explicit Worker delivery mode or allowQueue.");
        if (request.Mode == WorkerMessageMode.Steer && string.IsNullOrWhiteSpace(request.TurnId))
            throw KernelException.InvalidArgument("Steering requires the current turnId.");
        if (request.Mode == WorkerMessageMode.Queue && request.TurnId is not null)
            throw KernelException.InvalidArgument("Queued Worker messages target an attempt. Omit turnId.");

        // Existing receipts hash the original request shape. Explicit delivery choices use a new request version.
        var hash = request.Mode is null
            ? RequestIdentity.Hash(new { version = 1, request.AttemptId, request.TurnId, request.Content, request.AllowQueue })
            : RequestIdentity.Hash(new { version = 2, request.AttemptId, request.TurnId, request.Content, request.Mode });
        var author = callerSessionId ?? InstallationAuthor;

        var delivery = await EnqueueAsync(async actor =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireWorkerOperatorCore(callerSessionId);
            RequireSessionCore(sessionId);
            var replay = await Messages.FindAsync(sessionId, worker: true, author, request.RequestKey, ct).ConfigureAwait(false);
            if (replay is not null)
            {
                RequireMatchingHash(replay.RequestHash, hash);
                if (replay.Delivery == "dispatching" && _scheduler.TryGetSteering(sessionId, out var inFlight) &&
                    inFlight.InputId == replay.Id && inFlight.Attempt is Task<WorkerMessageReceipt> sent)
                    return sent;
                return Task.FromResult(ToWorkerMessageReceipt(replay));
            }

            _scheduler.TryGetActive(sessionId, out var active);
            if (request.TurnId is not null && (active?.TurnDto.Id != request.TurnId || active.Interrupted))
                throw new KernelException(request.Mode == WorkerMessageMode.Steer ? KernelErrorCode.WorkerTurnChanged : KernelErrorCode.Conflict,
                    "The Worker turn changed. Refresh the session before steering.");
            var (_, attempt) = RequireCurrentWorkerCore(sessionId, request.AttemptId);
            if (attempt.InterruptionUnconfirmed)
                throw KernelException.Conflict("The previous Worker interruption could not be confirmed.");
            var explicitQueue = request.Mode == WorkerMessageMode.Queue;
            if (!explicitQueue && request.TurnId is null && (active is not null || _scheduler.HasReservation(sessionId)))
                throw KernelException.Conflict("A Worker turn is already running or starting. Target a turn to steer it.");

            var native = !explicitQueue && active?.AgentSession.SupportsNativeSteer == true;
            if (active is not null && !native && !explicitQueue && !request.AllowQueue)
                throw KernelException.Conflict("This provider requires explicit permission to queue the message for the next turn.");

            var message = new SessionMessage($"inp_{Guid.NewGuid():N}", sessionId, attempt.AttemptId, author,
                request.RequestKey, hash, request.TurnId, null, request.Content,
                native ? "dispatching" : "queued", NowMs(), "worker");
            await Messages.InsertAsync(message, ct).ConfigureAwait(false);

            if (!native)
            {
                if (active is null && !explicitQueue)
                    attempt.Paused = false;
                NotifyQueueChangedCore(sessionId);
                await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                return Task.FromResult(ToWorkerMessageReceipt(message));
            }

            _scheduler.TryGetSteering(sessionId, out var previous);
            var send = RunWorkerSteerAsync(message, active!, previous.Attempt, _timeProvider.GetTimestamp());
            _scheduler.SetSteering(sessionId, (message.Id, send));
            _ = TrackBackgroundJob(() => send);
            return send;
        }, ct).ConfigureAwait(false);

        return await delivery.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<WorkerMessageReceipt> RunWorkerSteerAsync(SessionMessage message, ActiveTurnState active, Task? previous, long startedAt)
    {
        var ct = _actorShutdownCts.Token;
        if (previous is not null)
            await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        var outcome = "unknown";
        try
        {
            var delivery = await AttemptSteeringAsync(message, active.TurnDto.Id, startedAt, ct).ConfigureAwait(false);
            outcome = delivery switch
            {
                SteerDelivery.Accepted => "delivered",
                SteerDelivery.Declined => "rejected",
                null => active.Interrupted ? "interrupted" : "rejected",
                _ => "unknown",
            };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The provider may have consumed the message before losing the response.
        }
        return await EnqueueAsync(async _ =>
        {
            try
            {
                var saved = await SettleSteerCoreAsync(message, outcome, ct).ConfigureAwait(false);
                if (saved.Delivery == "delivered")
                    active.Activity.Append("steer",
                        JsonHelpers.ToElement(new { content = message.Content, author = message.AuthorId, inputId = message.Id }), NowMs());
                return ToWorkerMessageReceipt(saved);
            }
            finally
            {
                if (_scheduler.TryGetSteering(message.SessionId, out var latest) && latest.InputId == message.Id)
                    _scheduler.EndSteering(message.SessionId);
                await PokeSessionCoreAsync(message.SessionId).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task InterruptWorkerAsync(string sessionId, string attemptId, string turnId, string? callerSessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stop = await EnqueueAsync<Task>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            RequireWorkerOperatorCore(callerSessionId);
            var (_, attempt) = RequireCurrentWorkerCore(sessionId, attemptId);
            if (!_scheduler.TryGetActive(sessionId, out var active) || active.TurnDto.Id != turnId)
                throw KernelException.Conflict("The Worker turn changed. Refresh the session before interrupting.");
            ct.ThrowIfCancellationRequested();
            return StopWorkerTurn(active, attempt);
        }, ct).ConfigureAwait(false);
        await stop.WaitAsync(ct).ConfigureAwait(false);
    }

    private Task StopWorkerTurn(ActiveTurnState active, ActiveAttemptState attempt)
    {
        lock (active.Control.Sync)
        {
            if (active.Control.Phase == ExecutionPhase.Ended)
                return RequestTurnStop(active);
            attempt.Paused = true;
            _mcpBridge.RevokeSession(active.SessionId);
            return RequestTurnStop(active);
        }
    }

    private async Task CloseInterruptedWorkerCoreAsync(ActiveTurnState turn, ActiveAttemptState attempt)
    {
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

    private static WorkerMessageReceipt ToWorkerMessageReceipt(SessionMessage message) =>
        new(message.DeliveredTurnId ?? message.TurnId ?? message.TargetTurnId, message.Delivery, message.Id);

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
