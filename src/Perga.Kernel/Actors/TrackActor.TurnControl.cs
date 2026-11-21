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

    private Task RequestTurnStop(ActiveTurnState active, bool observeTerminal = false, ActiveTaskRunState? pauseTask = null)
    {
        var control = active.Control;
        List<PendingApproval>? expired = null;
        Task stopTask;
        lock (control.Sync)
        {
            if (pauseTask is not null && control.Phase != ExecutionPhase.Ended)
            {
                pauseTask.Paused = true;
                active.CloseRuntimeOnStop = true;
                _mcpBridge.RevokeSession(active.SessionId);
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
            expired = CollectExpiredApprovalsLocked(control);
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
            expired = CollectExpiredApprovalsLocked(active.Control);
        }

        FinishExpiredApprovals(expired);
        active.CancelStream();
    }
}
