using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Perga.AgentKit;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    private Task RequestTurnStop(ActiveTurnState active, bool observeTerminal = false)
    {
        var control = active.Control;
        lock (control.Sync)
        {
            if (control.StopDispatch is { IsCompleted: false } inFlight)
                return inFlight;
            if (control.Phase == ExecutionPhase.Ended)
                return HasStopEvidence(active)
                    ? Task.CompletedTask
                    : Task.FromException(KernelException.PreconditionFailed("The provider ended without confirming the turn outcome."));
            if (control.StopDispatch is { } existing)
            {
                if ((existing.IsFaulted || existing.IsCanceled) && !active.UnconfirmedCancel)
                    return control.StopDispatch = StartTurnStopDispatch(active, observeTerminal);
                return existing;
            }
            if (control.Phase == ExecutionPhase.Stopping)
                return Task.FromException(KernelException.Conflict("The turn is already being closed."));

            var started = control.Phase == ExecutionPhase.Running;
            active.StoppedBeforeDispatch = !started;
            control.StopRequested = true;
            control.Stopping.TrySetResult();
            control.Phase = ExecutionPhase.Stopping;
            ExpireApprovalsCore(control);
            return control.StopDispatch = started
                ? StartTurnStopDispatch(active, observeTerminal)
                : Task.CompletedTask;
        }
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_actorShutdownCts.Token);
        deadline.CancelAfter(ProviderStopGrace);
        Task? dispatch = null;
        try
        {
            dispatch = active.AgentSession.CancelTurnAsync(deadline.Token);
            active.TrackUnfinishedCancel(dispatch);
            await dispatch.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (observeTerminal || active.AgentSession.RequiresTerminalStop)
            {
                if (!await active.ObservationEnded.Task.WaitAsync(deadline.Token).ConfigureAwait(false))
                    throw KernelException.PreconditionFailed("The provider ended without confirming the turn outcome.");
            }
            else
                active.CancelStream();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException)
        {
            if (dispatch is { IsCompleted: false })
            {
                _ = dispatch.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
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
                        _ = SealTurn(active, item is not UnifiedAgentEvent.TurnError { IsConclusive: false });
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
                _ = SealTurn(active);
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
        lock (active.Control.Sync)
        {
            active.Control.Phase = ExecutionPhase.Ended;
            ExpireApprovalsCore(active.Control);
            active.ObservationEnded.TrySetResult(conclusive);
            return active.Control.StopDispatch ?? Task.CompletedTask;
        }
    }

    private void AbortTurn(ActiveTurnState active)
    {
        lock (active.Control.Sync)
        {
            active.Control.StopRequested = true;
            active.Control.Stopping.TrySetResult();
            active.Control.Phase = ExecutionPhase.Stopping;
            ExpireApprovalsCore(active.Control);
        }
        active.CancelStream();
    }
}
