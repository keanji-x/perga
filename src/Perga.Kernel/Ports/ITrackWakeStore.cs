using System.Text.Json;

namespace Perga.Kernel.Ports;

public sealed record TrackWakeRequest(string TrackId, string Kind, string? CoalesceKey, JsonElement Payload, string? TargetSessionId = null);
public sealed record TrackWake(long Ordinal, string Id, string TrackId, string? TargetSessionId, string Kind,
    string? CoalesceKey, string PayloadJson, string State, string? Reason, string? TurnId, long Attempts,
    long CreatedAtMs, long UpdatedAtMs)
{
    public bool IsCritical => TrackWakeKinds.IsCritical(Kind);
}

public interface ITrackWakeStore
{
    Task EnqueueAsync(TrackWakeRequest request, CancellationToken ct);
    Task<IReadOnlyList<TrackWake>> ListAsync(string trackId, CancellationToken ct);
    Task<IReadOnlyList<string>> ListPendingTracksAsync(CancellationToken ct);
    Task<IReadOnlyList<TrackWake>> ClaimBatchAsync(string trackId, string targetSessionId, int limit, CancellationToken ct,
        IReadOnlyCollection<string>? excludeWakeIds = null);
    Task MarkDoneAsync(IReadOnlyList<TrackWake> batch, string reason, string turnId, CancellationToken ct);
    Task MarkDroppedAsync(IReadOnlyList<TrackWake> batch, string reason, string? turnId, CancellationToken ct);
    Task LinkTurnAsync(IReadOnlyList<TrackWake> batch, string turnId, CancellationToken ct);
    Task<bool> CancelPendingWakeAsync(string trackId, string wakeId, CancellationToken ct);
    Task<IReadOnlyList<string>> RecoverOnBootAsync(CancellationToken ct);
}
