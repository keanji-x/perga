using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    public async Task PokePendingWakesAsync(CancellationToken ct = default, bool allowInline = true)
    {
        var runs = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            var pending = new List<Func<CancellationToken, Task>>();
            foreach (var sessionId in _sessions.Keys)
                if (await _scheduler.PokeAsync(sessionId, allowInline: allowInline).ConfigureAwait(false) is { } run)
                    pending.Add(run);
            return pending;
        }, ct).ConfigureAwait(false);
        foreach (var run in runs) await run(ct).ConfigureAwait(false);
    }

    public Task<bool> CancelPendingWakeAsync(string wakeId, CancellationToken ct = default) =>
        _store.TrackWakes.CancelPendingWakeAsync(TrackId, wakeId, ct);

    private Task<string> PrepareWakePromptAsync(TrackWake wake, TrackWakePayload payload, CancellationToken ct) => EnqueueAsync(async _ =>
    {
        if (wake.Kind != TrackWakeKinds.Schedule)
            return payload.Prompt ?? throw new InvalidOperationException($"Track wake '{wake.Id}' has no prompt.");
        var scheduleId = payload.ScheduleId ?? throw new InvalidOperationException($"Schedule wake '{wake.Id}' has no scheduleId.");
        var fallback = payload.Prompt ?? throw new InvalidOperationException($"Schedule wake '{wake.Id}' has no prompt.");
        if (_policy is null) return fallback;
        Perga.PolicyVm.PolicyEvalResult<string?> eval;
        try
        {
            eval = _policyVm.After(_policy, "schedule.fired", TrackId, System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                scheduleId, sourceKind = payload.SourceKind, prompt = fallback
            }), _track.TemplateInputs, _track.TemplateState);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Trace.TraceWarning("Policy after hook schedule.fired failed for Track {0}: {1}", TrackId, ex);
            return fallback;
        }
        await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct, emitEvent: true).ConfigureAwait(false);
        return eval.Output ?? fallback;
    }, ct);

    private async Task RunReservedWakeAsync(string sessionId, IReadOnlyList<TrackWake> batch, Guid reservation, CancellationToken ct)
    {
        var work = new TrackWakeBatchWorkItem(this, sessionId, batch, reservation);
        try
        {
            await _turnRunner.RunAsync(work, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await work.FailBeforeSettlementAsync().ConfigureAwait(false);
            await work.NotifyUpgradeOutcomeAsync(null, ex.Message).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!_actorShutdownCts.IsCancellationRequested)
                await EnqueueAsync(async _ =>
                {
                    if (_scheduler.TryGetReservation(sessionId, out var current) && current == reservation)
                    {
                        _scheduler.ReleaseReservation(sessionId);
                        await PokeSessionCoreAsync(sessionId).ConfigureAwait(false);
                    }
                }, CancellationToken.None).ConfigureAwait(false);
        }
    }

}
