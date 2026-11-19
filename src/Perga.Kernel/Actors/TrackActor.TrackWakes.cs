using Perga.Kernel.Ports;

namespace Perga.Kernel.Actors;

public sealed partial class TrackActor
{
    public async Task TriggerScheduleWakeAsync(string scheduleId, CancellationToken ct = default)
    {
        await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            await _store.TrackWakes.EnqueueAsync(TrackWakeRequests.Schedule(TrackId, scheduleId), ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        await PokeLeadAsync(ct).ConfigureAwait(false);
    }

    public async Task PokeLeadAsync(CancellationToken ct = default, bool allowInline = true)
    {
        var run = await EnqueueAsync(async _ =>
        {
            await EnsureHydratedCoreAsync(ct).ConfigureAwait(false);
            return await _scheduler.PokeLeadAsync(allowInline: allowInline).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        if (run is not null) await run(ct).ConfigureAwait(false);
    }

    public Task<bool> CancelPendingWakeAsync(string wakeId, CancellationToken ct = default) =>
        _store.TrackWakes.CancelPendingWakeAsync(TrackId, wakeId, ct);

    private Task<string> PrepareWakePromptAsync(TrackWake wake, TrackWakePayload payload, CancellationToken ct) => EnqueueAsync(async _ =>
    {
        if (wake.Kind != TrackWakeKinds.Schedule)
            return payload.Prompt ?? throw new InvalidOperationException($"Track wake '{wake.Id}' has no prompt.");
        if (!string.IsNullOrWhiteSpace(payload.Prompt))
            return payload.Prompt;
        var scheduleId = payload.ScheduleId ?? throw new InvalidOperationException($"Schedule wake '{wake.Id}' has no scheduleId.");
        var fallback = $"Scheduled wake '{scheduleId}' triggered.";
        if (_policy is null) return fallback;
        var eval = _policyVm.BuildScheduleWakePrompt(_policy, scheduleId, _track.TemplateInputs, _track.TemplateState);
        await UpdateTemplateStateIfChangedCoreAsync(eval.NextState, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(eval.Output)) return eval.Output;

        // The policy produced no prompt. A static manifest prompt wins when present. Otherwise a
        // non-null blank output is deliberate and suppresses the wake (the caller drops blank prompts),
        // while a null output means the policy said nothing, so use the default prompt.
        var staticPrompt = _policy.Manifest.Schedules.FirstOrDefault(s => s.Id == scheduleId)?.StaticWakePrompt;
        if (staticPrompt is not null) return staticPrompt;
        if (eval.Output is not null) return eval.Output;
        return fallback;
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
