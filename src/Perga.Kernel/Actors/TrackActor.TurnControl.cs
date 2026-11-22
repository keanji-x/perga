using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Perga.Contracts;
using Perga.Kernel.Events;
using Perga.Kernel.Flow;
using Perga.Kernel.Internal;
using Perga.Kernel.Mcp;
using Perga.Kernel.Ports;
using Perga.Kernel.Tasks;
using Perga.Kernel.Templates;
using Perga.PolicyVm;
using Perga.ReportDoc;
using Perga.TermClient;
using Perga.VcsKit;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Perga.AgentKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private static void ObserveTaskFault(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task EnterStoppingAsync(ActiveTurnState active) => EnqueueAsync(_ => PersistStoppingCoreAsync(active), CancellationToken.None);

    private async Task PersistStoppingCoreAsync(ActiveTurnState active)
    {
        if (!_scheduler.TryGetActive(active.SessionId, out var current) || !ReferenceEquals(current, active)) return;
        var session = RequireSessionCore(active.SessionId);
        if (session.Status != "running") return;
        await PersistAndEmitSessionUpdatedCoreAsync(session with { Status = "stopping", UpdatedAtMs = NowMs() }, NowMs(), CancellationToken.None).ConfigureAwait(false);
    }

    private Task RequestTurnStop(ActiveTurnState active, bool observeTerminal = false, TrackTasks.ActiveTaskRunState? pauseTask = null)
    {
        var control = active.Control;
        List<PendingApproval>? expired = null;
        Task stopTask;
        lock (control.Sync)
        {
            if (pauseTask is not null && control.Phase != ExecutionPhase.Ended)
            {
                _taskCoordinator.SetPaused(pauseTask, true);
                active.CloseRuntimeOnStop = true;
                RevokeSessionRuntime(active.SessionId);
            }

            if (control.StopDispatch is { IsCompleted: false } inFlight)
                return inFlight;
            if (control.Phase == ExecutionPhase.Ended)
                return control.HasStopEvidenceLocked
                    ? Task.CompletedTask
                    : Task.FromException(KernelException.PreconditionFailed("The provider ended without confirming the turn outcome."));
            if (control.StopDispatch is { } existing)
            {
                if ((existing.IsFaulted || existing.IsCanceled) && !control.HasUnfinishedCancelLocked)
                    return control.StopDispatch = StartTurnStopDispatch(active, observeTerminal);
                return existing;
            }
            if (control.Phase == ExecutionPhase.Stopping)
                return Task.FromException(KernelException.Conflict("The turn is already being closed."));

            var started = control.Phase == ExecutionPhase.Running;
            control.StoppedBeforeDispatch = !started;
            control.StopRequested = true;
            control.Stopping.TrySetResult();
            control.Phase = ExecutionPhase.Stopping;
            // Queue the transition before cancellation, but never make provider stop wait for SQLite.
            ObserveTaskFault(EnterStoppingAsync(active));
            expired = CollectExpiredApprovalsLocked(control, includeStopControl: true);
            stopTask = control.StopDispatch = started
                ? StartTurnStopDispatch(active, observeTerminal)
                : Task.CompletedTask;
        }

        FinishExpiredApprovals(expired);
        return stopTask;
    }

    private Task StartTurnStopDispatch(ActiveTurnState active, bool observeTerminal)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = TrackBackgroundJob(async () =>
        {
            try
            {
                await DispatchTurnStopAsync(active, observeTerminal).ConfigureAwait(false);
                completion.TrySetResult();
            }
            catch (Exception error)
            {
                // The Stop and Resume callers own this failure. Waiting for
                // background work must not fault for the same handled failure.
                completion.TrySetException(error);
            }
        });
        return completion.Task;
    }

    private async Task DispatchTurnStopAsync(ActiveTurnState active, bool observeTerminal)
    {
        var cancelGrace = _options.ProviderCancelDispatchGrace;
        using var cancelDeadline = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        cancelDeadline.CancelAfter(cancelGrace);
        Task? dispatch = null;
        try
        {
            dispatch = active.AgentSession.CancelTurnAsync(cancelDeadline.Token);
            lock (active.Control.Sync)
                active.Control.CancelCall = dispatch;
            await dispatch.WaitAsync(cancelDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException)
        {
            active.Control.StopTimedOut = true;
            if (dispatch is not null)
                ObserveTaskFault(dispatch);
            if (!observeTerminal || _actorShutdownCts.IsCancellationRequested)
                active.CancelStream();
            if (!_actorShutdownCts.IsCancellationRequested)
                throw KernelException.PreconditionFailed("The provider did not answer the stop request.");
            return;
        }

        if (!observeTerminal && !active.AgentSession.RequiresTerminalStop)
        {
            active.CancelStream();
            return;
        }

        // Separate cancel-dispatch timeout from terminal-event observation timeout:
        // Resume probes briefly before falling back to scoped close; normal Stop gives
        // real providers enough grace to unwind tools/streams cleanly without false unknowns.
        using var terminalDeadline = observeTerminal
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        terminalDeadline?.CancelAfter(_options.ProviderTerminalStopGrace);
        var terminalToken = terminalDeadline?.Token ?? cancelDeadline.Token;
        try
        {
            if (!await active.Control.ObservationEnded.Task.WaitAsync(terminalToken).ConfigureAwait(false))
                throw KernelException.PreconditionFailed("The provider ended without confirming the turn outcome.");
        }
        catch (OperationCanceledException)
        {
            active.Control.StopTimedOut = true;
            if (!observeTerminal || _actorShutdownCts.IsCancellationRequested)
                active.CancelStream();
            if (!_actorShutdownCts.IsCancellationRequested)
                throw KernelException.PreconditionFailed("The provider did not confirm that this turn stopped.");
        }
    }

    private async IAsyncEnumerable<UnifiedAgentEvent> ObserveTurnAsync(
        ActiveTurnState active, TurnInput input, [EnumeratorCancellation] CancellationToken ct)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inbox = Channel.CreateUnbounded<(UnifiedAgentEvent Item, TaskCompletionSource Processed)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        async Task PumpAsync()
        {
            Exception? failure = null;
            try
            {
                await using var events = active.AgentSession.StreamTurnAsync(input, observation.Token)
                    .GetAsyncEnumerator(observation.Token);
                ValueTask<bool> read;
                lock (active.Control.Sync)
                {
                    if (active.Control.Phase != ExecutionPhase.Starting)
                        return;
                    active.Control.Phase = ExecutionPhase.Running;
                    // Enter the adapter before allowing Stop to target this binding.
                    read = events.MoveNextAsync();
                }

                while (await read.ConfigureAwait(false))
                {
                    var item = events.Current;
                    if (item is UnifiedAgentEvent.TurnCompleted or UnifiedAgentEvent.TurnInterrupted or UnifiedAgentEvent.TurnError)
                        ObserveTaskFault(SealTurn(active, item is not UnifiedAgentEvent.TurnError { IsConclusive: false }));
                    var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    inbox.Writer.TryWrite((item, processed));
                    // Ordinary observation keeps the runner's backpressure. Once
                    // stopping, terminal evidence can bypass blocked persistence.
                    await Task.WhenAny(processed.Task, active.Control.Stopping.Task)
                        .WaitAsync(observation.Token).ConfigureAwait(false);
                    read = events.MoveNextAsync();
                }
            }
            catch (Exception error)
            {
                failure = error;
            }
            finally
            {
                ObserveTaskFault(SealTurn(active));
                inbox.Writer.TryComplete(failure);
            }
        }

        var pump = PumpAsync();
        try
        {
            // Drain already accepted events even when Stop ends observation.
            await foreach (var (item, processed) in inbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try { yield return item; }
                finally { processed.TrySetResult(); }
            }
        }
        finally
        {
            observation.Cancel();
            await pump.ConfigureAwait(false);
        }
    }

    private Task SealTurn(ActiveTurnState active, bool conclusive = false)
    {
        List<PendingApproval>? expired;
        Task stopTask;
        lock (active.Control.Sync)
        {
            active.Control.Phase = ExecutionPhase.Ended;
            expired = CollectExpiredApprovalsLocked(active.Control);
            active.Control.ObservationEnded.TrySetResult(conclusive);
            stopTask = active.Control.StopDispatch ?? Task.CompletedTask;
        }

        FinishExpiredApprovals(expired);
        return stopTask;
    }

    private void AbortTurn(ActiveTurnState active)
    {
        List<PendingApproval>? expired;
        lock (active.Control.Sync)
        {
            active.Control.StopRequested = true;
            active.Control.Stopping.TrySetResult();
            active.Control.Phase = ExecutionPhase.Stopping;
            expired = CollectExpiredApprovalsLocked(active.Control, includeStopControl: true);
        }

        FinishExpiredApprovals(expired);
        active.CancelStream();
    }
    private static SessionItemDto CreateWakeUserItem(SessionTurnDto turn, string prompt, string? inputId, string? deliveryError = null, string? presentation = null) =>
        new(Id: 0, SessionId: turn.SessionId, TurnId: turn.Id, Kind: "user",
            Payload: inputId is null
                ? JsonHelpers.ToElement(new { v = 1, content = prompt, triggerKind = turn.TriggerKind, presentation })
                : JsonHelpers.ToElement(new { v = 1, content = prompt, triggerKind = turn.TriggerKind, inputId, deliveryError }),
            CreatedAtMs: turn.StartedAtMs!.Value);

    private async Task CompleteWakeTurnCoreAsync(SessionTurnDto turn, IReadOnlyList<SessionItemDto> items,
        AgentSessionDto session)
    {
        var ct = CancellationToken.None;
        var turnEvent = TurnCompletedEvent(turn, session.UpdatedAtMs);
        await _eventHub.EnsureInitializedFromStoreAsync(_store, ct).ConfigureAwait(false);
        await _store.RecordTurnAndItemsAsync(turn, items, session, ct).ConfigureAwait(false);
        _sessions[session.Id] = session;
        try
        {
            await _store.AppendEventAsync(turnEvent, ct).ConfigureAwait(false);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, turnEvent, ct).ConfigureAwait(false);
            var sessionEvent = SessionUpdatedEvent(session);
            await _store.AppendEventAsync(sessionEvent, ct).ConfigureAwait(false);
            await _eventHub.PublishSyncedAfterStoreWriteAsync(_store, sessionEvent, ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseSettledTurnCoreAsync(session, turn.Id).ConfigureAwait(false);
        }
    }

    private sealed class ActiveTurnState(
        string sessionId,
        SessionConfiguration executionConfiguration,
        LiveContextUsage contextBinding,
        LiveRuntimeActivity runtimeActivity,
        SessionTurnDto turnDto,
        IAgentSession agentSession,
        CancellationTokenSource turnCts,
        bool hasStoredTurn,
        long openGeneration)
    {
        public bool CanSteerQueuedMessages => hasStoredTurn && AgentSession.SupportsNativeSteer;
        public string SessionId { get; } = sessionId;
        public SessionTurnDto TurnDto { get; } = turnDto;
        public IAgentSession AgentSession { get; } = agentSession;
        public CancellationTokenSource TurnCts { get; } = turnCts;
        public ExecutionControl Control { get; } = new(starting: true);
        public TurnActivity Activity { get; } = new();
        public bool CloseRuntimeOnStop { get; set; }
        public long OpenGeneration { get; } = openGeneration;
        public bool CloseRuntimeOnSettle { get; set; }
        public SessionConfiguration Configuration { get; } = executionConfiguration;
        public LiveContextUsage ContextBinding { get; } = contextBinding;
        public LiveRuntimeActivity RuntimeActivity { get; } = runtimeActivity;
        // Frozen inputs can execute a configuration different from the committed selection.
        public bool MatchesSessionConfiguration(AgentSessionDto session) =>
            Configuration == SessionConfiguration.FromNullable(session.Model, session.ReasoningEffort, session.PermissionMode);
        public bool Interrupted
        {
            get { lock (Control.Sync) return Control.StopRequested; }
        }
        public void CancelStream()
        {
            try { TurnCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        public bool ProviderAccepted { get; set; }
        public TaskCompletionSource<bool> Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }


    private sealed record TurnPolicyAdmission(string Generation, string? Context, Exception? Failure);

    private async Task<TurnPolicyAdmission> PreparePolicyTurnAdmissionAsync(string sessionId, CancellationToken ct)
    {
        var preparation = await EnqueueAsync<(TurnPolicyAdmission Admission, PolicyApproval? Ask)>(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var session = RequireSessionCore(sessionId);
            var generation = _policyGeneration;
            if (_policyUnavailableReason is { } reason) return (new(generation, null, KernelException.PolicyDenied(reason)), null);
            if (_policy is null) return (new(generation, null, null), null);
            try
            {
                var evaluation = _policyVm.PrepareTurn(_policy, new(TrackId, session.Id, session.IsLead,
                    _taskCoordinator.TryGetActiveTaskForSessionCore(session.Id)?.Task.Id, CurrentContextUsageRatioCore(session) ?? 0, _track.TemplateInputs), _track.Data);
                var suspension = ApplyGuardDecisionCore(evaluation.Decision, sessionId, new ExecutionControl(), ct);
                return (new(generation, evaluation.Context, null), suspension);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return (new(generation, null, ex), null);
            }
        }, ct).ConfigureAwait(false);
        if (preparation.Ask is not { } ask) return preparation.Admission;
        try
        {
            await RequirePolicyApprovalAsync(ask, ct).ConfigureAwait(false);
            return preparation.Admission;
        }
        catch (KernelException ex)
        {
            return preparation.Admission with { Failure = ex };
        }
    }
}
