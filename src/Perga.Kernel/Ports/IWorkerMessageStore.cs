namespace Perga.Kernel.Ports;

public sealed record WorkerMessage(
    string Id, string SessionId, string AttemptId, string AuthorId, string RequestKey, string RequestHash,
    string? TargetTurnId, string? ResultTurnId, string Content, string Delivery, long CreatedAtMs);

public interface IWorkerMessageStore
{
    Task<WorkerMessage?> FindAsync(string sessionId, string authorId, string requestKey, CancellationToken ct);
    Task<WorkerMessage?> NextAsync(string sessionId, CancellationToken ct);
    Task InsertAsync(WorkerMessage message, CancellationToken ct);
    Task UpdateAsync(WorkerMessage message, CancellationToken ct);
    Task<IReadOnlyList<WorkerMessage>> RecentAsync(string sessionId, CancellationToken ct);
}

public interface IWorkerSettingsStore
{
    Task<WorkerSettings?> ReadAsync(string sessionId, CancellationToken ct);
    Task WriteAsync(string sessionId, WorkerSettings settings, string authorId, Perga.Contracts.EventEnvelopeDto evt, CancellationToken ct);
}
