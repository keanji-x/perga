using Perga.Kernel.Ports;

namespace Perga.Kernel.Tests.Fakes;

internal sealed partial class InMemoryTrackStore : ITrackWakeStore
{
    private readonly List<TrackWake> _trackWakes = [];
    private long _wakeOrdinal;
    public ITrackWakeStore TrackWakes => this;
    private void EnqueueWakeLocked(TrackWakeRequest request, bool once = false)
    {
        if (!_tracks.TryGetValue(request.TrackId, out var track) || track.ClosedAtMs is not null) return;
        if (once && _trackWakes.Any(w => w.TrackId == request.TrackId && w.CoalesceKey == request.CoalesceKey)) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < _trackWakes.Count; i++)
            if (_trackWakes[i] is { State: TrackWakeStates.Pending } w && request.CoalesceKey is not null &&
                w.TrackId == request.TrackId && w.CoalesceKey == request.CoalesceKey)
                _trackWakes[i] = w with { State = TrackWakeStates.Dropped, Reason = TrackWakeReasons.Coalesced, UpdatedAtMs = now };
        _trackWakes.Add(new(++_wakeOrdinal, $"wke_{Guid.NewGuid():N}", request.TrackId, request.TargetSessionId,
            request.Kind, request.CoalesceKey, request.Payload.GetRawText(), TrackWakeStates.Pending, null, null, 0, now, now));
    }
    public Task EnqueueAsync(TrackWakeRequest request, CancellationToken ct)
    { lock (_gate) EnqueueWakeLocked(request); return Task.CompletedTask; }
    public Task<IReadOnlyList<TrackWake>> ListAsync(string trackId, CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<TrackWake>>(_trackWakes.Where(w => w.TrackId == trackId).ToArray()); }
    public Task<IReadOnlyList<string>> ListPendingTracksAsync(CancellationToken ct)
    { lock (_gate) return Task.FromResult<IReadOnlyList<string>>(_trackWakes.Where(w => w.State == TrackWakeStates.Pending && _tracks[w.TrackId].ClosedAtMs is null).Select(w => w.TrackId).Distinct().Order().ToArray()); }
    public Task<IReadOnlyList<TrackWake>> ClaimBatchAsync(string trackId, string targetSessionId, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            var claimed = new List<TrackWake>();
            for (var i = 0; i < _trackWakes.Count && claimed.Count < Math.Clamp(limit, 1, 32); i++)
                if (_trackWakes[i] is { State: TrackWakeStates.Pending } w && w.TrackId == trackId &&
                    _tracks[trackId].ClosedAtMs is null && (w.TargetSessionId is null || w.TargetSessionId == targetSessionId))
                { w = w with { State = TrackWakeStates.Delivering, Attempts = w.Attempts + 1 }; _trackWakes[i] = w; claimed.Add(w); }
            return Task.FromResult<IReadOnlyList<TrackWake>>(claimed);
        }
    }
    private Task TransitionAsync(IReadOnlyList<TrackWake> batch, string state, string? reason, string? turnId)
    {
        lock (_gate)
            for (var i = 0; i < _trackWakes.Count; i++)
                if (_trackWakes[i] is { State: TrackWakeStates.Delivering } w && batch.Any(b => b.Id == w.Id))
                    _trackWakes[i] = w with { State = state, Reason = reason, TurnId = turnId ?? w.TurnId };
        return Task.CompletedTask;
    }
    public Task MarkDoneAsync(IReadOnlyList<TrackWake> batch, string reason, string turnId, CancellationToken ct) => TransitionAsync(batch, TrackWakeStates.Done, reason, turnId);
    public Task MarkDroppedAsync(IReadOnlyList<TrackWake> batch, string reason, string? turnId, CancellationToken ct) => TransitionAsync(batch, TrackWakeStates.Dropped, reason, turnId);
    public Task LinkTurnAsync(IReadOnlyList<TrackWake> batch, string turnId, CancellationToken ct) => TransitionAsync(batch, TrackWakeStates.Delivering, null, turnId);
    public Task<bool> CancelPendingWakeAsync(string trackId, string wakeId, CancellationToken ct)
    {
        lock (_gate)
        {
            var i = _trackWakes.FindIndex(w => w.Id == wakeId && w.TrackId == trackId && w.State == TrackWakeStates.Pending);
            if (i < 0) return Task.FromResult(false);
            _trackWakes[i] = _trackWakes[i] with { State = TrackWakeStates.Dropped, Reason = TrackWakeReasons.Canceled };
            return Task.FromResult(true);
        }
    }
    public Task<IReadOnlyList<string>> RecoverOnBootAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var tracks = _trackWakes.Where(w => w.State == TrackWakeStates.Delivering).Select(w => w.TrackId).Distinct().ToArray();
            for (var i = 0; i < _trackWakes.Count; i++)
                if (_trackWakes[i] is { State: TrackWakeStates.Delivering, CoalesceKey: not null } w && _trackWakes.Any(n =>
                    n.TrackId == w.TrackId && n.CoalesceKey == w.CoalesceKey &&
                    (n.State == TrackWakeStates.Pending || n.State == TrackWakeStates.Delivering && n.Ordinal > w.Ordinal)))
                    _trackWakes[i] = w with { State = TrackWakeStates.Dropped, Reason = TrackWakeReasons.BootSuperseded };
            for (var i = 0; i < _trackWakes.Count; i++)
                if (_trackWakes[i] is { State: TrackWakeStates.Delivering } w)
                    _trackWakes[i] = w with { State = w.IsCritical ? TrackWakeStates.Pending : TrackWakeStates.Dropped,
                        Reason = w.IsCritical ? TrackWakeReasons.BootRequeued : TrackWakeReasons.BootDropped, TurnId = w.IsCritical ? null : w.TurnId };
            return Task.FromResult<IReadOnlyList<string>>(tracks);
        }
    }
}
