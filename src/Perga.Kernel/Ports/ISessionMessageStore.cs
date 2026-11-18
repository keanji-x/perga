namespace Perga.Kernel.Ports;

/// <summary>One receipt across queueing, dispatch and transcript handoff.</summary>
public sealed record SessionMessage(
    string Id, string SessionId, string? AttemptId, string AuthorId, string RequestKey, string RequestHash,
    string? TargetTurnId, string? TurnId, string Content, string Delivery, long CreatedAtMs, string Kind,
    string? DeliveredTurnId = null);

public interface ISessionMessageStore
{
    Task<SessionMessage?> FindAsync(string sessionId, bool worker, string authorId, string requestKey, CancellationToken ct);
    Task<SessionMessage?> GetAsync(string sessionId, bool worker, string messageId, CancellationToken ct);
    Task<SessionMessage?> NextAsync(string sessionId, string? attemptId, CancellationToken ct);
    Task InsertAsync(SessionMessage message, CancellationToken ct);
    Task<bool> TransitionAsync(SessionMessage message, string expected, CancellationToken ct);
    Task<string?> FindQueuedSteerAsync(SessionMessage message, string turnId, CancellationToken ct);
    Task<bool> ClaimQueuedSteerAsync(SessionMessage message, string turnId, CancellationToken ct);
    Task<string> SettleQueuedSteerAsync(SessionMessage message, string turnId, string outcome,
        Perga.Contracts.SessionItemDto? item, CancellationToken ct);
    Task<bool> DeliverSteerAsync(SessionMessage message, Perga.Contracts.SessionItemDto item, CancellationToken ct);
    Task<IReadOnlyList<SessionMessage>> ListQueueAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<string>> ListHandoffsAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<SessionMessage>> RecentWorkerAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<(string TrackId, string SessionId)>> ListRecoverableQueuesAsync(CancellationToken ct);
}
