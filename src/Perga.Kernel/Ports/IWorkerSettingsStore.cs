namespace Perga.Kernel.Ports;

public interface IWorkerSettingsStore
{
    Task<WorkerSettings?> ReadAsync(string sessionId, CancellationToken ct);
    Task WriteAsync(string sessionId, WorkerSettings settings, string authorId, Perga.Contracts.EventEnvelopeDto evt, CancellationToken ct);
}
